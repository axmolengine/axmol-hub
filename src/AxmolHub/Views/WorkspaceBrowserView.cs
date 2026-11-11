using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>A lazy, native-engine browser. No page messages are wired to Hub tools or credentials.</summary>
internal sealed class WorkspaceBrowserView : Grid, IDisposable
{
    private readonly TextBox _address = new() { Name = "WorkspaceBrowserAddress", MinWidth = 40,
        Height = 24, MinHeight = 24, Margin = default, Padding = new Thickness(8, 0), FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _reload;
    private readonly Control _reloadIcon = BrowserIcon("Hub.Icon.Refresh");
    private readonly Control _stopIcon = BrowserIcon("Hub.Icon.Close");
    private readonly Grid _surface = new() { ClipToBounds = true };
    private readonly SelectableTextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480 };
    private readonly Action<string> _log;
    private readonly string _logId = Guid.NewGuid().ToString("N")[..8];
    private NativeWebView? _web;
    private MacWebViewPointerLock? _pointerLock;
    private bool _loading;
    private bool _disposed;
    private bool _active;
    private bool _initializing;
    private TaskCompletionSource _initialized = new();
    internal event Action? Released;
    internal Task Ready => _initializing ? _initialized.Task : Task.CompletedTask;
    private Uri? _requested;
    private string? _pageTitle;
    private int _navigationVersion;
    private IImage? _favicon;
    private string? _faviconCandidates;
    private CancellationTokenSource? _faviconCancellation;
    internal string TabId { get; } = "browser:" + Guid.NewGuid().ToString("N");
    internal event Action<WorkspaceBrowserView>? StateChanged;
    internal bool Loading => !_disposed && (_loading || _initializing);
    internal string? PageTitle => _pageTitle;
    internal string? TabTitle => _pageTitle ?? _requested?.Host;
    internal IImage? Favicon => _favicon;

    internal WorkspaceBrowserView(Action<string> log)
    {
        _log = log;
        RowDefinitions = new RowDefinitions("Auto,*");
        ClipToBounds = true;
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(6), ColumnSpacing = 4 };
        _back = Action("WorkspaceBrowserBack", BrowserIcon("Hub.Icon.Back"), () => _web?.GoBack());
        _forward = Action("WorkspaceBrowserForward", BrowserIcon("Hub.Icon.Forward"), () => _web?.GoForward());
        _reload = Action("WorkspaceBrowserReload", _reloadIcon, ReloadOrStop);
        var external = Action("WorkspaceBrowserExternal", BrowserIcon("Hub.Icon.OpenFile"), OpenExternal);
        _address.Bind(TextBox.PlaceholderTextProperty, new DynamicResourceExtension("WorkspaceBrowserAddressHint"));
        _address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { NavigateAddress(); e.Handled = true; } };
        foreach (var (control, column) in new (Control, int)[] { (_back, 0), (_forward, 1), (_reload, 2), (_address, 3), (external, 4) })
        { Grid.SetColumn(control, column); bar.Children.Add(control); }
        Children.Add(bar);
        Grid.SetRow(_surface, 1);
        Children.Add(_surface);
        _surface.Children.Add(_message);
        ShowMessage("WorkspaceBrowserEmpty");
        // Detect missing Linux libraries before asking for a URL, without creating a native view.
        if (OperatingSystem.IsLinux()) RuntimeAvailable(WebViewAdapterType.WebKitGtk);
        SyncNavigation();
    }

    private static Control BrowserIcon(string key)
    {
        var icon = HubActionIcon.Create(key);
        icon.Width = icon.Height = 11;
        return icon;
    }

    private static Button Action(string name, object content, System.Action click)
    {
        var button = new Button { Name = name, Content = content, Width = 24, Height = 24, Padding = default,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "viewer-action" } };
        button.Bind(ToolTip.TipProperty, new DynamicResourceExtension(name));
        button.Click += (_, _) => click();
        return button;
    }

    internal void SetActive(bool active, bool obscured)
    {
        _active = active && !obscured && !_disposed;
        IsVisible = active && !_disposed;
        if (_web is not null) _web.IsVisible = _active && !_message.IsVisible;
    }

    internal IDisposable? BeginReparenting() => _web?.BeginReparenting(false);
    internal NativeWebView? WebViewForCheck => _web;
    internal TextBox AddressForCheck => _address;
    internal bool LoadingForCheck => _loading || _initializing;
    internal bool NativeVisibleForCheck => _web?.IsEffectivelyVisible == true;
    internal bool DisposedForCheck => _disposed;
    internal void NavigateForCheck(Uri uri) { _address.Text = uri.AbsoluteUri; NavigateAddress(); }

    private void NavigateAddress()
    {
        var text = _address.Text?.Trim() ?? "";
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsWebAddress(uri))
        { ShowMessage("WorkspaceBrowserInvalidAddress"); return; }
        _requested = uri;
        _address.Text = uri.AbsoluteUri;
        _loading = true;
        ResetMetadata();
        if (!EnsureWebView()) { _loading = false; SyncNavigation(); return; }
        _message.IsVisible = false;
        _web!.IsVisible = _active;
        SyncNavigation();
        Log("navigation requested");
        _web.Navigate(uri);
        Log("navigation request returned");
    }

    private static bool IsWebAddress(Uri? uri) => uri is { Scheme: "http" or "https", Host.Length: > 0 };

    private bool RuntimeAvailable(WebViewAdapterType type)
    {
        try
        {
            Log($"runtime detection started: adapter={type}");
            var info = WebViewAdapterInfo.GetAdapterInfo(type);
            Log($"runtime detection returned: installed={info.IsInstalled}, supported={info.IsSupported}");
            if (info.IsInstalled && info.IsSupported) return true;
        }
        catch (Exception error)
        {
            Log("runtime detection failed: " + error);
        }
        ShowMessage(MissingRuntimeKey());
        return false;
    }

    private bool EnsureWebView()
    {
        if (_disposed) return false;
        if (_web is not null) return true;
        var type = OperatingSystem.IsWindows() ? WebViewAdapterType.WebView2
            : OperatingSystem.IsMacOS() ? WebViewAdapterType.WkWebView : WebViewAdapterType.WebKitGtk;
        if (!RuntimeAvailable(type)) return false;
        try
        {
            // Set the native page canvas, not the surrounding Hub surface or the document's CSS.
            var web = _web = new NativeWebView { Name = "WorkspaceBrowserWebView", AdapterPreference = [type],
                Background = Brushes.White, IsVisible = _active };
            web.EnvironmentRequested += (_, args) =>
            {
                Log($"environment requested: {args.GetType().Name}");
                args.EnableDevTools = false;
                // Never borrow a user's browser profile. All browsing state is transient to this tab.
                if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
                {
                    windows.UserDataFolder = Path.Combine(Path.GetTempPath(), "AxmolHub-WebView", Environment.ProcessId.ToString());
                    windows.ProfileName = "tab-" + Guid.NewGuid().ToString("N");
                    windows.IsInPrivateModeEnabled = true;
                    windows.AllowSingleSignOnUsingOSPrimaryAccount = false;
                }
                else if (args is AppleWKWebViewEnvironmentRequestedEventArgs apple) apple.NonPersistentDataStore = true;
                else if (args is GtkWebViewEnvironmentRequestedEventArgs gtk)
                {
                    gtk.EphemeralDataManager = true;
                    gtk.ForceX11GdkBackend = true;
                }
            };
            web.NavigationStarted += (_, args) =>
            {
                if (_disposed) { args.Cancel = true; return; }
                if (args.Request?.AbsoluteUri == "about:blank") return;
                if (!IsWebAddress(args.Request)) { args.Cancel = true; return; }
                _requested = args.Request;
                _address.Text = args.Request!.AbsoluteUri;
                _loading = true;
                ResetMetadata();
                // GTK's policy callback waits for this handler. Reading native history here waits for
                // GTK in turn, so refresh the toolbar only after the synchronous decision returns.
                Dispatcher.UIThread.Post(() => { if (!_disposed) SyncNavigation(); });
            };
            web.NavigationCompleted += (_, args) =>
            {
                if (_disposed) return;
                Log("navigation completed: success=" + args.IsSuccess);
                _loading = false;
                if (IsWebAddress(args.Request)) _address.Text = args.Request!.AbsoluteUri;
                if (!args.IsSuccess) ShowMessage("WorkspaceBrowserLoadFailed");
                else { _message.IsVisible = false; web.IsVisible = _active; }
                SyncNavigation();
                if (args.IsSuccess) _ = RefreshMetadataAsync();
            };
            web.NewWindowRequested += (_, args) =>
            {
                // A popup stays in this tab; it cannot silently start another app or invoke a custom protocol.
                args.Handled = true;
                if (IsWebAddress(args.Request)) Dispatcher.UIThread.Post(() => { if (!_disposed) web.Navigate(args.Request!); });
            };
            _initialized = new TaskCompletionSource();
            _initializing = true;
            SyncNavigation();
            WebViewInitialization.Watch(web, error =>
            {
                Log("asynchronous initialization failed: " + error);
                _initializing = false;
                _loading = false;
                _initialized.TrySetResult();
                _surface.Children.Remove(web);
                _web = null;
                ShowMessage(MissingRuntimeKey());
                SyncNavigation();
                if (_disposed) Release();
            });
            web.AdapterCreated += (_, args) =>
            {
                Log("adapter created");
                MacWebViewCanvas.EnableBackground(args.TryGetPlatformHandle());
                _pointerLock = MacWebViewPointerLock.Install(args.TryGetPlatformHandle(),
                    () => _active && !_disposed && !_message.IsVisible, Log);
                WebViewInitialization.Finished(web);
                _initializing = false;
                _initialized.TrySetResult();
                SyncNavigation();
                if (_disposed) Dispatcher.UIThread.Post(Release);
            };
            web.AdapterDestroyed += (_, _) =>
            {
                _pointerLock?.Dispose();
                _pointerLock = null;
            };
            Log("native host attachment started");
            _surface.Children.Insert(0, web);
            Log("native host attachment returned");
            return true;
        }
        catch (Exception error)
        {
            Log("initialization failed: " + error);
            if (_web is not null) { WebViewInitialization.Finished(_web); _surface.Children.Remove(_web); }
            _initializing = false;
            _loading = false;
            _initialized.TrySetResult();
            _web = null;
            ShowMessage(MissingRuntimeKey());
            return false;
        }
    }

    // Log stages, never addresses: URLs can carry credentials or private query strings.
    private void Log(string message) => _log($"Workspace browser {_logId}: {message}");

    private static string MissingRuntimeKey() => OperatingSystem.IsWindows() ? "WorkspaceBrowserMissingWindows"
        : OperatingSystem.IsMacOS() ? "WorkspaceBrowserMissingMac" : "WorkspaceBrowserMissingLinux";

    private void ReloadOrStop()
    {
        if (_loading)
        {
            _web?.Stop();
            _loading = false;
            _navigationVersion++;
            CancelFavicon();
            SyncNavigation();
        }
        else if (_web is not null)
        {
            _message.IsVisible = false;
            _web.IsVisible = _active;
            _loading = true;
            ResetMetadata();
            SyncNavigation();
            if (!_web.Refresh()) { _loading = false; SyncNavigation(); }
        }
        else NavigateAddress();
    }

    private sealed record PageMetadata(string? Title, string[]? Icons);
    internal async Task RefreshMetadataAsync()
    {
        if (_disposed || Loading || _message.IsVisible || _web is not { } web) return;
        var version = _navigationVersion;
        PageMetadata? metadata;
        try
        {
            var result = await web.InvokeScript("""
                JSON.stringify({ Title: document.title,
                    Icons: Array.from(document.querySelectorAll('link[rel~="icon" i]')).slice(0, 4).map(link => link.href) })
                """);
            // WebView2 JSON-encodes script results; WebKit returns strings directly.
            if (OperatingSystem.IsWindows() && result is not null) result = JsonSerializer.Deserialize<string>(result);
            metadata = result is not null ? JsonSerializer.Deserialize<PageMetadata>(result) : null;
        }
        catch (Exception)
        {
            // Page metadata is optional. Keep the fallbacks without logging page content or addresses.
            return;
        }
        if (_disposed || version != _navigationVersion || !ReferenceEquals(web, _web)) return;
        var title = TextEol.ToLf(metadata?.Title ?? "").Trim();
        _pageTitle = title.Length > 0 ? title : null;
        StateChanged?.Invoke(this);
        if (_requested is not { } address) return;
        var icons = (metadata?.Icons ?? []).Where(icon => !string.IsNullOrWhiteSpace(icon) && icon.Length <= 4096).Take(4)
            .Append(new Uri(address, "/favicon.ico").AbsoluteUri).Distinct().ToArray();
        var key = string.Join("\n", icons);
        if (_faviconCandidates == key) return;
        _faviconCandidates = key;
        CancelFavicon();
        var cancellation = _faviconCancellation = new CancellationTokenSource();
        var image = await BrowserFavicon.LoadAsync(icons, cancellation.Token);
        if (_disposed || version != _navigationVersion || !ReferenceEquals(cancellation, _faviconCancellation))
        {
            (image as IDisposable)?.Dispose();
            return;
        }
        SetFavicon(image);
    }

    private void ResetMetadata()
    {
        _navigationVersion++;
        _pageTitle = null;
        _faviconCandidates = null;
        CancelFavicon();
        SetFavicon(null);
    }

    private void CancelFavicon()
    {
        _faviconCancellation?.Cancel();
        _faviconCancellation?.Dispose();
        _faviconCancellation = null;
    }

    private void SetFavicon(IImage? image)
    {
        var previous = _favicon;
        _favicon = image;
        StateChanged?.Invoke(this);
        (previous as IDisposable)?.Dispose();
    }

    private void OpenExternal()
    {
        if (IsWebAddress(_requested) && !UrlLauncher.TryOpen(_requested!.AbsoluteUri)) ShowMessage("WorkspaceBrowserExternalFailed");
    }

    private void ShowMessage(string key)
    {
        _message.Bind(TextBlock.TextProperty, new DynamicResourceExtension(key));
        _message.IsVisible = true;
        if (_web is not null) _web.IsVisible = false;
    }

    private void SyncNavigation()
    {
        _back.IsEnabled = _web?.CanGoBack == true;
        _forward.IsEnabled = _web?.CanGoForward == true;
        _reload.Content = _loading ? _stopIcon : _reloadIcon;
        _reload.Bind(ToolTip.TipProperty, new DynamicResourceExtension(_loading ? "WorkspaceBrowserStop" : "WorkspaceBrowserReload"));
        StateChanged?.Invoke(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _navigationVersion++;
        StateChanged = null;
        CancelFavicon();
        SetFavicon(null);
        IsVisible = false;
        // An initializing WebView2 controller must keep its HWND until its callback completes.
        if (!_initializing) Release();
    }

    private void Release()
    {
        _pointerLock?.Dispose();
        _pointerLock = null;
        _web?.Stop();
        if (_web is not null) WebViewInitialization.Finished(_web);
        _surface.Children.Clear();
        _web = null;
        Released?.Invoke();
    }
}
