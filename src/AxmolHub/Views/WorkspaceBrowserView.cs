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
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480 };
    private NativeWebView? _web;
    private bool _loading;
    private bool _disposed;
    private bool _active;
    private bool _initializing;
    private TaskCompletionSource _initialized = new();
    internal event Action? Released;
    internal Task Ready => _initializing ? _initialized.Task : Task.CompletedTask;
    private Uri? _requested;

    internal WorkspaceBrowserView()
    {
        RowDefinitions = new RowDefinitions("Auto,*");
        ClipToBounds = true;
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(6), ColumnSpacing = 4 };
        _back = Action("WorkspaceBrowserBack", BrowserIcon("Hub.Icon.Back"), () => _web?.GoBack());
        _forward = Action("WorkspaceBrowserForward", BrowserIcon("Hub.Icon.Forward"), () => _web?.GoForward());
        _reload = Action("WorkspaceBrowserReload", _reloadIcon, ReloadOrStop);
        var external = Action("WorkspaceBrowserExternal", BrowserIcon("Hub.Icon.OpenFile"), OpenExternal);
        _address.Bind(TextBox.PlaceholderTextProperty, new DynamicResourceExtension("WorkspaceBrowserAddressHint"));
        _address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { NavigateAddress(); e.Handled = true; } };
        foreach (var (control, column) in new (Control, int)[] { (_back, 0), (_forward, 1), (_address, 2), (_reload, 3), (external, 4) })
        { Grid.SetColumn(control, column); bar.Children.Add(control); }
        Children.Add(bar);
        Grid.SetRow(_surface, 1);
        Children.Add(_surface);
        _surface.Children.Add(_message);
        ShowMessage("WorkspaceBrowserEmpty");
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
        if (!EnsureWebView()) return;
        _message.IsVisible = false;
        _web!.IsVisible = _active;
        _web.Navigate(uri);
    }

    private static bool IsWebAddress(Uri? uri) => uri is { Scheme: "http" or "https", Host.Length: > 0 };

    private bool EnsureWebView()
    {
        if (_disposed) return false;
        if (_web is not null) return true;
        var type = OperatingSystem.IsWindows() ? WebViewAdapterType.WebView2
            : OperatingSystem.IsMacOS() ? WebViewAdapterType.WkWebView : WebViewAdapterType.WebKitGtk;
        try
        {
            var info = WebViewAdapterInfo.GetAdapterInfo(type);
            if (!info.IsInstalled || !info.IsSupported) { ShowMessage(MissingRuntimeKey()); return false; }
            var web = _web = new NativeWebView { Name = "WorkspaceBrowserWebView", AdapterPreference = [type], IsVisible = _active };
            web.EnvironmentRequested += (_, args) =>
            {
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
                if (args.Request?.AbsoluteUri == "about:blank") return;
                if (!IsWebAddress(args.Request)) { args.Cancel = true; return; }
                _requested = args.Request;
                _address.Text = args.Request!.AbsoluteUri;
                _loading = true;
                SyncNavigation();
            };
            web.NavigationCompleted += (_, args) =>
            {
                _loading = false;
                if (IsWebAddress(args.Request)) _address.Text = args.Request!.AbsoluteUri;
                if (!args.IsSuccess) ShowMessage("WorkspaceBrowserLoadFailed");
                else { _message.IsVisible = false; web.IsVisible = _active; }
                SyncNavigation();
            };
            web.NewWindowRequested += (_, args) =>
            {
                // A popup stays in this tab; it cannot silently start another app or invoke a custom protocol.
                args.Handled = true;
                if (IsWebAddress(args.Request)) Dispatcher.UIThread.Post(() => { if (!_disposed) web.Navigate(args.Request!); });
            };
            _initialized = new TaskCompletionSource();
            _initializing = true;
            WebViewInitialization.Watch(web, _ =>
            {
                _initializing = false;
                _initialized.TrySetResult();
                _surface.Children.Remove(web);
                _web = null;
                ShowMessage(MissingRuntimeKey());
                if (_disposed) Release();
            });
            web.AdapterCreated += (_, _) =>
            {
                WebViewInitialization.Finished(web);
                _initializing = false;
                _initialized.TrySetResult();
                SyncNavigation();
                if (_disposed) Dispatcher.UIThread.Post(Release);
            };
            _surface.Children.Insert(0, web);
            return true;
        }
        catch (Exception)
        {
            if (_web is not null) { WebViewInitialization.Finished(_web); _surface.Children.Remove(_web); }
            _initializing = false;
            _initialized.TrySetResult();
            _web = null;
            ShowMessage(MissingRuntimeKey());
            return false;
        }
    }

    private static string MissingRuntimeKey() => OperatingSystem.IsWindows() ? "WorkspaceBrowserMissingWindows"
        : OperatingSystem.IsMacOS() ? "WorkspaceBrowserMissingMac" : "WorkspaceBrowserMissingLinux";

    private void ReloadOrStop()
    {
        if (_loading)
        {
            _web?.Stop();
            _loading = false;
            SyncNavigation();
        }
        else if (_web is not null)
        {
            _message.IsVisible = false;
            _web.IsVisible = _active;
            _web.Refresh();
        }
        else NavigateAddress();
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsVisible = false;
        // An initializing WebView2 controller must keep its HWND until its callback completes.
        if (!_initializing) Release();
    }

    private void Release()
    {
        _web?.Stop();
        if (_web is not null) WebViewInitialization.Finished(_web);
        _surface.Children.Clear();
        _web = null;
        Released?.Invoke();
    }
}
