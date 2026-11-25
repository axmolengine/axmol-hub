using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// A single authentication surface. Providers that support both routes show browser sign-in and API key entry
/// together; a provider with one route shows only that route.
/// </summary>
/// <para>The dialog is modal because the settings provider list is rebuilt when credentials change. Keeping the
/// key field in its own window means a repaint cannot destroy text the user is entering.</para>
public sealed partial class AuthDialog : Window
{
    private readonly ModelProvider _provider;
    private readonly Func<string, Task<KeyCheckOutcome>>? _checkKey;
    private readonly IReadOnlyList<string> _routes;
    private readonly StackPanel _content = new() { Spacing = 14 };
    private TextBox? _keyBox;
    private TextBlock? _status;
    private Button? _login;
    private Button? _connect;
    private Button? _apiKeyToggle;
    private StackPanel? _keySection;
    private bool _checkingKey;
    private bool _isClosed;

    private AuthDialog(ModelProvider provider, Func<string, Task<KeyCheckOutcome>>? checkKey)
    {
        _provider = provider;
        _checkKey = checkKey;
        _routes = Routes();

        Title = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("AuthDialogTitle"),
            provider.Name);
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        BuildSurface();
        Content = chrome.Wrap(_content);
        Opened += OnOpened;
        Closed += (_, _) => _isClosed = true;
    }

    /// <summary>Shows the dialog.</summary>
    public static async Task<AuthDialogResult?> ShowAsync(
        Window? owner,
        ModelProvider provider,
        Func<string, Task<KeyCheckOutcome>>? checkKey = null)
    {
        var dialog = new AuthDialog(provider, checkKey);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Result : null;
    }

    /// <summary>What the dialog produced, or <c>null</c> when the user closed it without choosing.</summary>
    public AuthDialogResult? Result { get; private set; }

    private List<string> Routes()
    {
        var routes = new List<string>(2);
        if (_provider.SupportsOAuth) routes.Add(ProviderAuthMethods.OAuth);
        if (_provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey)) routes.Add(ProviderAuthMethods.ApiKey);
        if (routes.Count == 0) routes.Add(ProviderAuthMethods.ApiKey);
        return routes;
    }

    private void BuildSurface()
    {
        var hasLogin = _routes.Contains(ProviderAuthMethods.OAuth);
        var hasKey = _routes.Contains(ProviderAuthMethods.ApiKey);
        if (hasLogin)
        {
            _login = new Button
            {
                Content = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("AuthMethodLoginOption"),
                    _provider.Name),
                Tag = ProviderAuthMethods.OAuth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                MinHeight = 36,
                IsDefault = true,
                Classes = { "compact", "primary" },
                Margin = new Thickness(0),
            };
            _login.Click += (_, _) => ChooseLogin();
            _content.Children.Add(_login);

            var loginHint = Body(HubStrings.Get("AuthMethodLoginHint"));
            loginHint.HorizontalAlignment = HorizontalAlignment.Center;
            _content.Children.Add(loginHint);
        }

        if (hasLogin && hasKey)
        {
            _content.Children.Add(Rule());
            _apiKeyToggle = new Button
            {
                Content = HubStrings.Get("AuthUseApiKeyExpand"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Classes = { "compact", "quiet" },
                Margin = new Thickness(0),
            };
            _apiKeyToggle.Click += (_, _) => ToggleApiKeySection();
            _content.Children.Add(_apiKeyToggle);
        }

        if (hasKey)
        {
            _keySection = new StackPanel
            {
                Spacing = 10,
                ClipToBounds = true,
                IsVisible = !hasLogin,
            };

            _keySection.Children.Add(Body(_provider.KeyValidation is { IsKnown: true }
                ? string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("AuthKeyStepHint"),
                    _provider.Name)
                : HubStrings.Get("AuthKeyStepHintNoCheck")));

            _keyBox = new TextBox
            {
                Classes = { "password" },
                PasswordChar = '\u25CF',
                PlaceholderText = HubStrings.Get("ApiKey"),
                Height = 32,
                MinHeight = 32,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _keyBox.TextChanged += (_, _) => UpdateConnectEnabled();

            _connect = new Button
            {
                Content = HubStrings.Get("AuthDo"),
                Tag = ProviderAuthMethods.ApiKey,
                IsDefault = !hasLogin,
                IsEnabled = false,
                Height = 32,
                MinHeight = 32,
                Classes = { "compact", hasLogin ? "quiet" : "primary" },
                Margin = new Thickness(0),
            };
            _connect.Click += async (_, _) => await ConnectKeyAsync();

            var keyRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8,
            };
            keyRow.Children.Add(_keyBox);
            Grid.SetColumn(_connect, 1);
            keyRow.Children.Add(_connect);
            _keySection.Children.Add(keyRow);

            if (_provider.ApiKeyUrl is { } url)
            {
                var getKey = new Button
                {
                    Content = HubStrings.Get("AuthGetApiKey"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Classes = { "compact", "quiet" },
                };
                ToolTip.SetTip(getKey, url);
                getKey.Click += (_, _) =>
                {
                    if (!UrlLauncher.TryOpen(url))
                        SetStatus(HubStrings.Get("AuthNoBrowserLink") + url, error: true);
                };
                _keySection.Children.Add(getKey);
            }

            _status = new TextBlock
            {
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false,
            };
            _keySection.Children.Add(_status);
            _content.Children.Add(_keySection);
        }
    }

    private void ToggleApiKeySection()
    {
        if (_keySection is null) return;
        var expanded = !_keySection.IsVisible;
        _keySection.IsVisible = expanded;
        if (_apiKeyToggle is not null)
            _apiKeyToggle.Content = HubStrings.Get(expanded ? "AuthUseApiKeyCollapse" : "AuthUseApiKeyExpand");
        if (_login is not null) _login.IsDefault = !expanded;
        if (_connect is not null) _connect.IsDefault = expanded;
        if (expanded) _keyBox?.Focus();
        else _login?.Focus();
    }

    private static Border Rule()
    {
        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        rule.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Hub.BorderSubtle"));
        return rule;
    }

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        Classes = { "muted" },
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    private void ChooseLogin()
    {
        Result = new AuthDialogResult(AuthRoute.Login);
        Close(HubDialogResult.Ok);
    }

    private void UpdateConnectEnabled()
    {
        if (_connect is not null)
            _connect.IsEnabled = !_checkingKey && !string.IsNullOrWhiteSpace(_keyBox?.Text);
    }

    private async Task ConnectKeyAsync()
    {
        var key = (_keyBox?.Text ?? "").Trim();
        if (key.Length == 0 || _connect is null) return;

        _checkingKey = true;
        _connect.IsEnabled = false;
        if (_keyBox is not null) _keyBox.IsEnabled = false;
        SetStatus(HubStrings.Get("AuthKeyChecking"), error: false);

        var outcome = _checkKey is null ? KeyCheckOutcome.Unsupported : await _checkKey(key);
        if (_isClosed) return;
        _checkingKey = false;
        if (_keyBox is not null) _keyBox.IsEnabled = true;
        UpdateConnectEnabled();
        if (outcome == KeyCheckOutcome.Rejected)
        {
            SetStatus(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("AuthKeyRejected"),
                _provider.Name), error: true);
            _keyBox?.Focus();
            return;
        }

        Result = new AuthDialogResult(AuthRoute.Key, key, Validated: outcome == KeyCheckOutcome.Accepted);
        Close(HubDialogResult.Ok);
    }

    private void SetStatus(string text, bool error)
    {
        if (_status is null) return;
        _status.Text = text;
        _status.IsVisible = true;
        _status.Classes.Set("muted", !error);
        _status.Classes.Set("error", error);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_login is not null) _login.Focus();
        else _keyBox?.Focus();
    }
}

/// <summary>Which route the user took out of the dialog.</summary>
public enum AuthRoute
{
    /// <summary>A pasted key, carried in <see cref="AuthDialogResult.ApiKey"/>.</summary>
    Key,

    /// <summary>Browser sign-in. The dialog closes at once; the flow lives in ChatWorkspace.</summary>
    Login,
}

/// <summary>What the dialog produced.</summary>
/// <param name="Route">Which of the two paths the user chose.</param>
/// <param name="ApiKey">The pasted key, trimmed. Empty for <see cref="AuthRoute.Login"/>.</param>
/// <param name="Validated">
/// <c>true</c> only when the provider answered and accepted the key. A key stored without a verdict is still a
/// usable key — <c>false</c> records that nobody vouched for it, which is information the UI may show later.
/// </param>
public sealed record AuthDialogResult(AuthRoute Route, string ApiKey = "", bool Validated = false);
