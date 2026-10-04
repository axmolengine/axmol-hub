using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// The two-step authentication dialog: pick a method, then do it.
/// </summary>
/// <para><b>It is a modal for one reason that is not about focus handling.</b> The provider list on the settings
/// page is rebuilt from the credential and provider collections on every change, so a <c>TextBox</c> living
/// inside a group would be thrown away mid-edit the moment anything repainted — the user would be typing a
/// key into a field that no longer exists. A separate window is not rebuilt, so what they typed survives.</para>
///
/// <para><b>Step one lists what the manifest declares, not a fixed pair of buttons.</b> <c>authMethods</c> is
/// data (see <see cref="AiProviderEntry.AuthMethods"/>): a key-only provider shows one option and skips
/// straight to the key box, and adding a third method to a provider later needs no change here.</para>
///
/// <para><b>Step two is either the browser flow or a key box.</b> Key entry optionally validates first — see
/// <see cref="KeyCheckOutcome"/> for why an unreachable probe is not treated as a rejection.</para>
/// </summary>
public sealed class AuthDialog : Window
{
    private readonly ModelProvider _provider;
    private readonly Func<string, Task<KeyCheckOutcome>>? _checkKey;

    private readonly StackPanel _steps = new() { Spacing = 12 };
    private readonly StackPanel _navButtons = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Spacing = 8,
        Margin = new Thickness(0, 16, 0, 0),
    };

    private string? _chosenMethod;
    private TextBox? _keyBox;
    private TextBlock? _status;
    private Button? _next;
    private Button? _confirm;

    /// <summary>
    /// Builds a detached instance for a screenshot, optionally already advanced to a step.
    ///
    /// <para>The step is a parameter because a picture of step one alone would misrepresent the dialog: the
    /// method list and the key box are the two halves of one flow, and only the second half has an input in
    /// it. <c>"key"</c> is what the key step is called in <see cref="Choose"/>.</para>
    /// </summary>
    internal static AuthDialog ForCheck(ModelProvider provider, string? step = null)
    {
        var dialog = new AuthDialog(provider, null, null);
        if (step is not null) dialog.RenderKeyStep();
        return dialog;
    }

    /// <summary>Picks a method row without taking it, mirroring a click on the row itself.</summary>
    internal void SelectForCheck(string method)
    {
        var rows = LogicalControls
            .OfType<Button>()
            .Where(button => button.Tag as string == method)
            .ToList();
        foreach (var row in rows) Select(method, rows);
    }

    private AuthDialog(ModelProvider provider, string? initialMethod, Func<string, Task<KeyCheckOutcome>>? checkKey)
    {
        _provider = provider;
        _checkKey = checkKey;
        _chosenMethod = initialMethod;

        Title = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("AuthDialogTitle"),
            provider.Name);
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(_steps);
        root.Children.Add(_navButtons);
        Content = new Border { Child = root, Padding = new Thickness(24) };

        Render();
    }

    /// <summary>
    /// Shows the dialog. <paramref name="initialMethod"/> skips step one when the provider offers exactly one
    /// route — a key-only provider should not make the user click "next" on a dialog with nothing to choose.
    /// </summary>
    public static async Task<AuthDialogResult?> ShowAsync(
        Window? owner,
        ModelProvider provider,
        string? initialMethod = null,
        Func<string, Task<KeyCheckOutcome>>? checkKey = null)
    {
        var dialog = new AuthDialog(provider, initialMethod, checkKey);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Result : null;
    }

    /// <summary>What the dialog produced, or <c>null</c> when the user closed it without choosing.</summary>
    public AuthDialogResult? Result { get; private set; }

    // ── Self-check surface ──
    //
    // A modal cannot be driven by --verify-shell, so these read the built state instead. Two rules make the
    // reads trustworthy, and both were learned by breaking them:
    //
    // 1. Read the LOGICAL tree, not the visual one. A window that was never shown has no visual tree —
    //    GetVisualDescendants() on it returns nothing, so "no blank text" passed on an empty collection and
    //    the option list read as "[]". An assertion that cannot fail is worse than no assertion, because it
    //    looks like evidence. StepCountForCheck exists to close that door: it fails loudly when the tree
    //    the other reads walk turns out to be empty.
    // 2. Read the TEXT, never the control's existence. Body(string) once took a parameter and never
    //    assigned it to Text — the block laid out, measured a line's height, painted nothing, and every
    //    check that counted controls or read Tag still passed. Counting is not reading.

    /// <summary>How many text-bearing controls the current step actually built. The non-empty witness.</summary>
    internal int StepControlCountForCheck => LogicalControls.Count();

    /// <summary>Every non-empty string currently painted in the dialog's content, in tree order.</summary>
    internal string[] VisibleTextForCheck
        => LogicalControls
            .Select(control => Read(control))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .ToArray();

    /// <summary>
    /// How many <see cref="TextBlock"/>s in the tree are blank <i>and showing</i>.
    ///
    /// <para>Visibility is part of the predicate on purpose. A status line that is built empty and filled
    /// in only when there is something to report is legitimate; what is not legitimate is a block the user
    /// can see with nothing in it. Counting the hidden ones would make the check cry wolf over the design
    /// rather than the defect.</para>
    /// </summary>
    internal int BlankTextCountForCheck
        => LogicalControls
            .OfType<TextBlock>()
            .Count(block => string.IsNullOrWhiteSpace(block.Text) && block.IsVisible);

    /// <summary>Names of the blank, visible text blocks. Empty when there are none; diagnostic detail.</summary>
    internal string[] BlankTextClassesForCheck
        => LogicalControls
            .OfType<TextBlock>()
            .Where(block => string.IsNullOrWhiteSpace(block.Text) && block.IsVisible)
            .Select(block => string.Join("+", block.Classes))
            .ToArray();

    /// <summary>The label on the step-one confirm button, or <c>null</c> when this step has none.</summary>
    internal string? NextTextForCheck => (_next?.Content as string) ?? _next?.Content?.ToString();

    /// <summary>Whether the step-one confirm button would take the selection.</summary>
    internal bool NextEnabledForCheck => _next?.IsEnabled ?? false;

    /// <summary>The method rows on step one, in order. Empty on the key step.</summary>
    internal string[] MethodOptionsForCheck
        => LogicalControls
            .OfType<Button>()
            .Select(button => button.Tag as string)
            .Where(tag => tag is not null)
            .Cast<string>()
            .ToArray();

    /// <summary>The password boxes on the current step. Used to prove the key box exists at all.</summary>
    internal bool HasPasswordBoxForCheck
        => LogicalControls.OfType<TextBox>().Any(box => box.Classes.Contains("password"));

    private IEnumerable<Control> LogicalControls
        => this.GetLogicalDescendants().OfType<Control>();

    private static string? Read(Control control) => control switch
    {
        TextBlock block => block.Text,
        Button { Content: string text } => text,
        TextBox box => box.Text ?? box.PlaceholderText,
        _ => null,
    };

    private void Render()
    {
        _steps.Children.Clear();
        _navButtons.Children.Clear();
        _keyBox = null;
        _status = null;
        _next = null;
        _confirm = null;

        if (_chosenMethod is null) RenderMethodStep();
        else RenderKeyStep();

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
    /// <para>The single-route case is the exception: a key-only provider has nothing to choose, so the dialog
    /// opens straight on the key box instead of showing one option and a Next button over it.</para>
    /// </summary>
    private void RenderMethodStep()
    {
        var methods = new List<string>();
        if (_provider.SupportsOAuth) methods.Add(ProviderAuthMethods.OAuth);
        if (_provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey)) methods.Add(ProviderAuthMethods.ApiKey);

        // One route and it is a key: there is no decision to make, so do not dress it up as one.
        if (methods.Count == 1 && methods[0] == ProviderAuthMethods.ApiKey)
        {
            Choose(ProviderAuthMethods.ApiKey);
            return;
        }

        if (methods.Count == 0)
        {
            // A provider that declares nothing usable still needs a way in; key is the universal fallback and
            // is what EffectiveAuthMethods already decided on.
            Choose(ProviderAuthMethods.ApiKey);
            return;
        }

        _steps.Children.Add(Heading(HubStrings.Get("AuthDialogHint")));

        var options = new StackPanel { Spacing = 6 };
        var rows = new List<Button>();
        foreach (var method in methods)
        {
            var row = MethodOption(method, LabelFor(method), () => Select(method, rows));
            rows.Add(row);
            options.Children.Add(row);
        }

        _steps.Children.Add(options);

        _next = new Button { Content = HubStrings.Get("Next"), IsDefault = true, IsEnabled = false };
        _next.Click += (_, _) => Choose(_chosenMethod!);
        _navButtons.Children.Add(_next);
        _navButtons.Children.Add(DialogButtons.Cancel(this));
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
        _chosenMethod = method;
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
            Classes = { "primary" },
        };
        _confirm.Click += async (_, _) => await ConfirmKeyAsync();

        // Built outside the initializer: Click is an event, and an object initializer can only assign
        // properties. Constructing then subscribing is the same shape PromptWindow uses.
        var back = new Button
        {
            Content = HubStrings.Get("Back"),
            IsCancel = true,
        };
        back.Click += (_, _) => { _chosenMethod = null; Render(); };

        _navButtons.Children.Add(back);
        _navButtons.Children.Add(_confirm);
    }

    private void Choose(string method)
    {
        _chosenMethod = method;
        if (method == ProviderAuthMethods.OAuth)
        {
            // The browser route is reported, not performed: the flow (browser, callback, failure messages)
            // belongs to ChatWorkspace, and running it here would give one flow two owners.
            Result = new AuthDialogResult(AuthRoute.Login);
            Close(HubDialogResult.Ok);
            return;
        }

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
            // Put the caret on the only option when there is only one, so Enter alone carries the user on.
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
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => owner.Close(HubDialogResult.Cancel);
        return cancel;
    }
}
