using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The two-step authentication dialog: pick a method, then do it.
/// </summary>
/// <para><b>It is a modal for one reason that is not about focus handling.</b> The provider list on the settings
/// page is rebuilt from the credential and provider collections on every change, so a <c>TextBox</c> living
/// inside a group would be thrown away mid-edit the moment anything repainted — the user would be typing a
/// key into a field that no longer exists. A separate window is not rebuilt, so what they typed survives.</para>
///
/// <para><b>Step one lists what the manifest declares, not a fixed pair of buttons.</b> <c>authMethods</c> is
/// data (see <see cref="AiProviderEntry.AuthMethods"/>): a key-only provider has one route and skips straight
/// to the key box, and adding a third method to a provider later needs no change here.</para>
///
/// <para><b>Which step it opens on is decided here, from the route list.</b> The caller may say which route
/// the provider is already bound to, and that only marks the row step one starts on; it never picks the step.
/// Deriving the step from that hint is what once opened a key box for a provider whose bound route was the
/// browser, leaving the method list one Back click away.</para>
///
/// <para><b>Step two is either the browser flow or a key box.</b> Key entry optionally validates first — see
/// <see cref="KeyCheckOutcome"/> for why an unreachable probe is not treated as a rejection.</para>
/// </summary>
public sealed partial class AuthDialog : Window
{
    private readonly ModelProvider _provider;
    private readonly Func<string, Task<KeyCheckOutcome>>? _checkKey;
    /// <summary>The routes step one lists, in the order it lists them. Snapshotted once so the opening step and
    /// the list itself cannot disagree about how many there are.</summary>
    private readonly IReadOnlyList<string> _routes;

    private readonly StackPanel _steps = new() { Spacing = 12 };
    private readonly StackPanel _navButtons = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Spacing = 8,
        Margin = new Thickness(0, 16, 0, 0),
    };

    /// <summary>The route currently marked in the list. A mark on a row, not a record of which step is showing —
    /// <see cref="_keyStep"/> answers that, and keeping them apart is the fix: one field used to answer both,
    /// so any marked route also meant "go to the key box".</summary>
    private string? _selection;
    /// <summary>Whether the key surface is the one on screen.</summary>
    private bool _keyStep;
    private TextBox? _keyBox;
    private TextBlock? _status;
    private Button? _next;
    private Button? _confirm;

    private AuthDialog(ModelProvider provider, string? preferredMethod, Func<string, Task<KeyCheckOutcome>>? checkKey)
    {
        _provider = provider;
        _checkKey = checkKey;
        _routes = Routes();

        // One route and it is a key: there is no decision to make, so do not dress it up as one. Every other
        // shape — two routes, or a single browser route — starts on the list, because the list is the choice
        // and a browser route is reported from here rather than performed before the window is up.
        _keyStep = _routes.Count == 1 && _routes[0] == ProviderAuthMethods.ApiKey;

        // A bound method arrives as a mark, never as a step: it says which row Enter would accept, and it is
        // dropped when the provider no longer offers that route.
        _selection = _keyStep
            ? ProviderAuthMethods.ApiKey
            : preferredMethod is not null && _routes.Contains(preferredMethod) ? preferredMethod : null;

        Title = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("AuthDialogTitle"),
            provider.Name);
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(_steps);
        root.Children.Add(_navButtons);
        Content = chrome.Wrap(root);

        Render();
    }

    /// <summary>
    /// Shows the dialog.
    /// </summary>
    /// <param name="preferredMethod">
    /// The route this provider is already bound to, if any. It marks that row on step one so Enter carries the
    /// user back to what they did last time; it never skips the step, because a provider that offers two routes
    /// is a provider whose route the user may want to change. Only a single key route opens on the key box, and
    /// that decision belongs to the dialog's own route list.
    /// </param>
    public static async Task<AuthDialogResult?> ShowAsync(
        Window? owner,
        ModelProvider provider,
        string? preferredMethod = null,
        Func<string, Task<KeyCheckOutcome>>? checkKey = null)
    {
        var dialog = new AuthDialog(provider, preferredMethod, checkKey);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Result : null;
    }

    /// <summary>What the dialog produced, or <c>null</c> when the user closed it without choosing.</summary>
    public AuthDialogResult? Result { get; private set; }

    private void Render()
    {
        _steps.Children.Clear();
        _navButtons.Children.Clear();
        _keyBox = null;
        _status = null;
        _next = null;
        _confirm = null;

        if (_keyStep) RenderKeyStep();
        else RenderMethodStep();

        Opened -= OnOpened;
        Opened += OnOpened;
    }

    /// <summary>
    /// Step one: the methods this provider declares, a selection, and a Next button.
    ///
    /// <para><b>Selection then Next, rather than "click an option and it happens".</b> Browser sign-in opens a
    /// real browser and a key paste is a commitment of typing; both are awkward to back out of once started.
    /// A visible confirm step is also the only way the two routes can be read side by side before either is
    /// taken — the previous build advanced on click, which made the list a menu rather than a choice.</para>
    ///
    /// <para>This is also what an already-bound provider opens on, with its old route marked: the exception is a
    /// provider with a single key route, which the constructor sends straight to the key box instead of showing
    /// one option and a Next button over it.</para>
    /// </summary>
    private void RenderMethodStep()
    {
        _steps.Children.Add(Heading(HubStrings.Get("AuthDialogHint")));

        var options = new StackPanel { Spacing = 6 };
        var rows = new List<Button>();
        foreach (var method in _routes)
        {
            var row = MethodOption(method, LabelFor(method), () => Select(method, rows));
            rows.Add(row);
            options.Children.Add(row);
        }

        _steps.Children.Add(options);

        _next = new Button { Content = HubStrings.Get("Next"), IsDefault = true, IsEnabled = false, Classes = { "compact" }, Margin = new Thickness(0) };
        _next.Click += (_, _) => Choose(_selection!);
        _navButtons.Children.Add(_next);
        _navButtons.Children.Add(DialogButtons.Cancel(this));

        // The bound route is marked, not taken: the step is the same for everyone and the mark only says where
        // Enter goes. A fresh provider has nothing marked and a disabled Next until a row is clicked.
        if (_selection is not null) Select(_selection, rows);
    }

    /// <summary>
    /// The routes step one offers, in the order it offers them: the browser first, then the key.
    ///
    /// <para>A provider that declares nothing usable still needs a way in; the key box is the universal fallback
    /// and is what <see cref="ModelProvider.EffectiveAuthMethods"/> already decided on.</para>
    /// </summary>
    private List<string> Routes()
    {
        var routes = new List<string>(2);
        if (_provider.SupportsOAuth) routes.Add(ProviderAuthMethods.OAuth);
        if (_provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey)) routes.Add(ProviderAuthMethods.ApiKey);
        if (routes.Count == 0) routes.Add(ProviderAuthMethods.ApiKey);
        return routes;
    }

    private string LabelFor(string method)
        => method == ProviderAuthMethods.OAuth
            ? string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("AuthMethodLoginOption"),
                _provider.Name)
            : HubStrings.Get("AuthMethodKeyOption");

    /// <summary>Marks one row as chosen. The marker is the row's own styling, not a separate radio control,
    /// so the list reads as a list.</summary>
    private void Select(string method, List<Button> rows)
    {
        _selection = method;
        foreach (var row in rows) row.Classes.Set("quiet", row.Tag as string != method);
        if (_next is not null) _next.IsEnabled = true;
    }

    private void RenderKeyStep()
    {
        _steps.Children.Add(Heading(HubStrings.Get("AuthKeyStepTitle")));
        _steps.Children.Add(Body(_provider.KeyValidation is { IsKnown: true }
            ? string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("AuthKeyStepHint"),
                _provider.Name)
            : HubStrings.Get("AuthKeyStepHintNoCheck")));

        _keyBox = new TextBox { Classes = { "password" }, PlaceholderText = HubStrings.Get("ApiKey") };
        _steps.Children.Add(_keyBox);

        _status = new TextBlock
        {
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _steps.Children.Add(_status);

        _confirm = new Button
        {
            Content = HubStrings.Get("Save"),
            IsDefault = true,
            Classes = { "compact", "primary" },
            Margin = new Thickness(0),
        };
        _confirm.Click += async (_, _) => await ConfirmKeyAsync();

        // Built outside the initializer: Click is an event, and an object initializer can only assign
        // properties. Constructing then subscribing is the same shape PromptWindow uses.
        var back = new Button
        {
            Content = HubStrings.Get("Back"),
            IsCancel = true,
            Classes = { "compact" },
            Margin = new Thickness(0),
        };
        back.Click += (_, _) =>
        {
            // Back keeps the mark, so returning to the list lands on the row the user came from with Next live.
            _keyStep = false;
            Render();
        };

        _navButtons.Children.Add(back);
        _navButtons.Children.Add(_confirm);
    }

    private void Choose(string method)
    {
        _selection = method;
        if (method == ProviderAuthMethods.OAuth)
        {
            // The browser route is reported, not performed: the flow (browser, callback, failure messages)
            // belongs to ChatWorkspace, and running it here would give one flow two owners.
            Result = new AuthDialogResult(AuthRoute.Login);
            Close(HubDialogResult.Ok);
            return;
        }

        _keyStep = true;
        Render();
    }

    private async Task ConfirmKeyAsync()
    {
        var key = (_keyBox?.Text ?? "").Trim();
        if (key.Length == 0) return;

        if (_confirm is not null) _confirm.IsEnabled = false;

        var outcome = _checkKey is null ? KeyCheckOutcome.Unsupported : await _checkKey(key);

        if (_confirm is not null) _confirm.IsEnabled = true;

        switch (outcome)
        {
            case KeyCheckOutcome.Rejected:
                // The one case that blocks: the endpoint understood the request and refused the credential.
                SetStatus(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("AuthKeyRejected"),
                    _provider.Name), error: true);
                _keyBox?.Focus();
                return;

            case KeyCheckOutcome.Unreachable:
                // Not a verdict. The key is stored and the caveat is shown, so a network problem never
                // becomes an accusation against a key that may well be fine.
                Result = new AuthDialogResult(AuthRoute.Key, key, Validated: false);
                Close(HubDialogResult.Ok);
                return;

            case KeyCheckOutcome.Accepted:
                SetStatus(HubStrings.Get("AuthKeyAccepted"), error: false);
                Result = new AuthDialogResult(AuthRoute.Key, key, Validated: true);
                Close(HubDialogResult.Ok);
                return;

            default:
                Result = new AuthDialogResult(AuthRoute.Key, key, Validated: false);
                Close(HubDialogResult.Ok);
                return;
        }
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
        if (_keyBox is not null)
        {
            _keyBox.Focus();
        }
        else
        {
            // Next is the key that carries a choice forward, and it is live whenever a row is already marked —
            // which is the case whenever the provider was bound to a route before this dialog opened.
            _next?.Focus();
        }
    }

    private static Button MethodOption(string method, string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Classes = { "quiet" },
            // Carries which method this row stands for, so Select can compare against the whole list without
            // a closure per row.
            Tag = method,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = Avalonia.Media.FontWeight.SemiBold,
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        Classes = { "muted" },
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };
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

/// <summary>Buttons shared by the Hub dialogs, so "the cancel key" is one implementation.</summary>
internal static class DialogButtons
{
    public static Button Cancel(Window owner)
    {
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true, Classes = { "compact" }, Margin = new Thickness(0) };
        cancel.Click += (_, _) => owner.Close(HubDialogResult.Cancel);
        return cancel;
    }
}
