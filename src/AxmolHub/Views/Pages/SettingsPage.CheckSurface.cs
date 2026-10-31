using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
// GetLogicalDescendants (not GetVisualDescendants) on purpose: the model section is read for a group that may
// not be attached to a visual root yet, and the visual walk returns an empty collection in that case — which
// turns "the check found nothing" into "the check passed".
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Avalonia.Layout;
using Avalonia.Media;

namespace AxmolHub;
public partial class SettingsPage : UserControl
{
    /// <summary>Opens the first model's edit draft without a click — the seam --verify-shell drives to
    /// assert that the model-row pencil's draft builds (it once tore the tree on the first press).</summary>
    internal void OpenFirstModelEditForCheck()
    {
        var provider = _chat.Providers.FirstOrDefault(candidate => candidate.Models.Count > 0) ?? throw new InvalidOperationException("no provider with a configured model");
        OpenModelEditDraft(provider, provider.Models[0]);
    }

    /// <summary>How many model-draft rows are in the tree. More than one means the page-wide draft flags
    /// leaked into a group that is not the one being edited.</summary>
    internal int ModelDraftCountForCheck => this.GetLogicalDescendants().OfType<Control>().Count(control => SectionTags.ModelDraft.Equals(control.Tag));
    /// <summary>How many provider-draft forms are in the tree. Any count other than zero while only a
    /// <b>model</b> draft is open means the two drafts share a flag again and one press opened both.</summary>
    internal int ProviderDraftCountForCheck => this.GetLogicalDescendants().OfType<Control>().Count(control => SectionTags.ProviderDraft.Equals(control.Tag));

    /// <summary>Closes whatever draft is open through the same path cancel and save use.</summary>
    internal void CloseDraftForCheck() => CloseDraft();
    /// <summary>The permission modes declared in XAML, in order. Same contract as <see cref = "DeclaredThemes"/>.</summary>
    internal string[] DeclaredToolApprovalModesForCheck => ToolApprovalPicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();
    /// <summary>The selected mode, with an unrecognized value falling back to ask — the same rule the gate uses,
    /// so the picker cannot offer a setting the runtime would ignore in a different direction.</summary>
    internal string SelectedToolApprovalModeForCheck => SelectedToolApprovalMode;
    /// <summary>The three labels really were written from the text table — a <c>ComboBoxItem</c> in
    /// <c>Items</c> is the one place a bound resource is known to resolve empty.</summary>
    internal bool ToolApprovalLabelsAreLocalizedForCheck => ToolApprovalPicker.Items.OfType<ComboBoxItem>().All(item => item.Content is string text && text.Length > 0);
    internal bool UpdateChannelLabelsAreLocalizedForCheck => UpdateChannelPicker.Items.OfType<ComboBoxItem>().All(item => item.Content is string text && text.Length > 0);
    internal bool UpdateChannelPickerEnabledForCheck => UpdateChannelPicker.IsEnabled;
    internal ComputerApplication[] TrustedComputerAppsForCheck => (_chat?.TrustedComputerApps ?? []).ToArray();
    internal int TrustedComputerAppRowsForCheck => TrustedComputerAppList.Children.OfType<Control>().Count(child => Equals(child.Tag, TrustGrantRowTag.ComputerApp));
    /// <summary>Whether that list is on the page at all — an empty one takes its heading and its revoke-all button
    /// with it, which is the whole point of the ledger's single empty line.</summary>
    internal bool TrustedComputerAppGroupShownForCheck => TrustedComputerAppGroup.IsVisible;

    internal bool TrustComputerAppForCheck(ComputerApplication app) => ApplyTrustGrantForCheck(() => _chat?.TrustComputerApp(app) == true);
    internal bool RevokeTrustedComputerAppForCheck(ComputerApplication app) => ApplyTrustGrantForCheck(() => _chat?.RevokeTrustedComputerApp(app) == true);
    internal bool RevokeAllTrustedComputerAppsForCheck() => ApplyTrustGrantForCheck(() => _chat?.RevokeAllTrustedComputerApps() == true);

    /// <summary>The trust list as stored, for the assertion that a grant written from a card lands here.</summary>
    internal string[] TrustedToolsForCheck => (_chat?.TrustedTools ?? []).ToArray();
    /// <summary>How many rows the page drew. Rows are the whole list now: an empty ledger draws no group at all and
    /// says so in the one line all three lists share.</summary>
    internal int TrustedToolRowsShownForCheck => TrustedToolList.Children.OfType<Control>().Count(child => TrustGrantRowTag.Tool.Equals(child.Tag));
    internal bool TrustedToolGroupShownForCheck => TrustedToolGroup.IsVisible;

    /// <summary>The verb list as stored, and the rows it drew — the assertion that a grant written from a card's
    /// verb button lands here and can be taken back from here.</summary>
    internal string[] TrustedCommandsForCheck => (_chat?.TrustedCommands ?? []).ToArray();
    internal int TrustedCommandRowsShownForCheck => TrustedCommandList.Children.OfType<Control>().Count(child => TrustGrantRowTag.Command.Equals(child.Tag));
    internal bool TrustedCommandGroupShownForCheck => TrustedCommandGroup.IsVisible;

    /// <summary>Whether the ledger's own line stands in for it — shown only while all three lists are empty.</summary>
    internal bool TrustGrantsEmptyShownForCheck => TrustGrantsEmptyLine.IsVisible;

    internal bool TrustToolForCheck(string tool) => ApplyTrustGrantForCheck(() => _chat?.TrustTool(tool) == true);
    internal bool RevokeTrustedToolForCheck(string tool) => ApplyTrustGrantForCheck(() => _chat?.RevokeTrustedTool(tool) == true);
    internal bool RevokeAllTrustedToolsForCheck() => ApplyTrustGrantForCheck(() => _chat?.RevokeAllTrustedTools() == true);
    internal bool TrustCommandForCheck(string key) => ApplyTrustGrantForCheck(() => _chat?.TrustCommand(key) == true);
    internal bool RevokeTrustedCommandForCheck(string key) => ApplyTrustGrantForCheck(() => _chat?.RevokeTrustedCommand(key) == true);
    internal bool RevokeAllTrustedCommandsForCheck() => ApplyTrustGrantForCheck(() => _chat?.RevokeAllTrustedCommands() == true);

    /// <summary>Writes one grant or revocation through the same workspace call the card's button makes, then
    /// re-renders the ledger. One helper for nine seams: the page is checked against the write path rather than
    /// against a copy of it, and a per-list rebuild is exactly the copy that drifts.</summary>
    private bool ApplyTrustGrantForCheck(Func<bool> apply)
    {
        var changed = apply();
        if (changed) RebuildTrustedGrants();
        return changed;
    }

    /// <summary>Reads the switch the way the assistant does, for a self-check that has to prove the preference is
    /// the thing the tool consults rather than a checkbox that remembers nothing.</summary>
    internal bool AllowSpawnedSessionsForCheck => _preferences.AllowSpawnedSessions;
    /// <summary>The same argument as the spawn hook: a self-check that turns fetching off has to read the
    /// preference the tool consults, not the checkbox that forgot it.</summary>
    internal bool AllowOutboundWebFetchForCheck => _preferences.AllowOutboundWebFetch;
    internal bool AllowWebSearchForCheck => _preferences.AllowHostedWebSearch || _preferences.AllowDeepSeekWebApi;
    /// <summary>Read off the preference the transcript consults, not off the checkbox that may have forgotten
    /// it — the same seam the two switches above hand to the self-checks.</summary>
    internal bool ShowThinkingForCheck => _preferences.ShowThinking;

    /// <summary>Flips the switch the way a person does rather than by writing the preference. A self-check needs
    /// the property-changed path exercised end to end, because the wiring between this checkbox and the file is
    /// exactly the link that can be missing while both ends still look correct on their own.</summary>
    internal void ToggleShowThinkingForCheck() => ShowThinkingCheck.IsChecked = ShowThinkingCheck.IsChecked != true;
    /// <summary>
    /// The disclosure line as it would render for a given tier. Not a second copy of the wording: it calls the
    /// same builder the card calls, which is what lets the assertion cover the two tiers this host is not running.
    /// </summary>
    internal string? SecretBackendTextForCheck(SecretStoreKind kind, string? degradedReason) => (BuildSecretBackendLine(new SecretStoreDescriptor(kind, "", degradedReason)) as TextBlock)?.Text;
    /// <summary>
    /// The browser sign-in for one provider group. Everything that can go wrong here is reported through the
    /// status line rather than a dialog: the flow is async and the user is looking at another window, so a
    /// modal box would be waiting in the wrong place.
    ///
    /// <para>OpenAI shows a settings-layer card with manual callback entry; discovery providers use their
    /// loopback listener, and device providers retain the existing code window.</para>
    ///
    /// <para>The button is passed in rather than looked up: with several groups on screen there is no single
    /// "the sign-in button", and disabling the wrong one would leave the clicked button live while a second
    /// flow started underneath.</para>
    /// </summary>
    /// <param name = "button">
    /// The button that started the flow, disabled while the browser is open. <c>null</c> when the flow was
    /// started from the auth dialog instead of from a row: there is no row button to disable, and the dialog
    /// has already closed by the time the flow runs.
    /// </param>
    internal bool OpenAiLayerForCheck => OpenAiLoginLayer.ClipToBounds && OpenAiLoginLayer.Background is not null && OpenAiLoginLayer.Parent is Grid grid && grid.Children.IndexOf(OpenAiLoginLayer) == grid.Children.Count - 1;

    /// <summary>Presses the card's button the way the user does, so a check walks the same routing — including
    /// the branch that hands the press to the restart's own armed gate.</summary>
    internal void ClickUpdateCardButtonForCheck() => CheckForUpdatesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>Runs the real "Add provider" button handler, so the self-check drives the same path a click
    /// does (the picker dialog is modal and cannot be clicked from the harness).</summary>
    internal Task ClickAddProviderForCheckAsync() => AddProviderAsync();

    /// <summary>Opens the add-custom-endpoint draft directly, skipping the picker for the same reason the method
    /// above runs the button handler: what has to be inspected is the draft, and a modal cannot be clicked.</summary>
    internal void OpenAddProviderDraftForCheck()
    {
        OpenAddDraft();
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Where every add-draft field landed and whether Add is enabled, read after layout.</summary>
    internal (bool Present, Rect Name, Rect BaseUrl, Rect Wire, Rect Key, Rect Headers, bool SaveEnabled)
        AddProviderDraftLayoutForCheck()
    {
        var draft = AddDraftChild();
        Rect Where(string tag)
            => AddDraftChild<Control>(draft, tag) is { } control && draft is not null
               && control.TranslatePoint(default, draft) is { } origin
                ? new Rect(origin, control.Bounds.Size)
                : default;
        return (draft is not null,
            Where(SectionTags.AddDraftName),
            Where(SectionTags.AddDraftBaseUrl),
            Where(SectionTags.AddDraftWire),
            Where(SectionTags.AddDraftKey),
            Where(SectionTags.AddDraftHeaders),
            AddDraftChild<Button>(draft, SectionTags.AddDraftSave)?.IsEnabled == true);
    }

    internal (int Count, string? SelectedProtocol, bool KeyIsMasked) AddProviderDraftWireOptionsForCheck()
    {
        var draft = AddDraftChild();
        var wire = AddDraftChild<ComboBox>(draft, SectionTags.AddDraftWire);
        return (wire?.Items.Count ?? 0,
            (wire?.SelectedItem as ComboBoxItem)?.Tag as string,
            AddDraftChild<TextBox>(draft, SectionTags.AddDraftKey)?.PasswordChar != default(char));
    }

    /// <summary>Fills the draft through its real input controls so validation events run as they do for a user.</summary>
    internal bool FillAddProviderDraftForCheck(
        string name,
        string baseUrl,
        string protocol = "chat",
        string apiKey = "",
        string headersJson = "")
    {
        var draft = AddDraftChild();
        if (draft is null) return false;
        if (AddDraftChild<TextBox>(draft, SectionTags.AddDraftName) is { } nameBox) nameBox.Text = name;
        if (AddDraftChild<TextBox>(draft, SectionTags.AddDraftBaseUrl) is { } urlBox) urlBox.Text = baseUrl;
        if (AddDraftChild<ComboBox>(draft, SectionTags.AddDraftWire) is { } wireBox)
            wireBox.SelectedItem = wireBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, protocol, StringComparison.Ordinal));
        if (AddDraftChild<TextBox>(draft, SectionTags.AddDraftKey) is { } keyBox) keyBox.Text = apiKey;
        if (AddDraftChild<TextBox>(draft, SectionTags.AddDraftHeaders) is { } headersBox) headersBox.Text = headersJson;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return true;
    }

    /// <summary>Presses Add and awaits its real commit path, including the model-list refresh for a supplied key.</summary>
    internal async Task<bool> ClickAddProviderDraftSaveForCheck()
    {
        var save = AddDraftChild<Button>(AddDraftChild(), SectionTags.AddDraftSave);
        if (save is null || !save.IsEnabled) return false;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_addProviderCommitTask is { } commit) await commit;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return true;
    }

    private Control? AddDraftChild()
        => ProviderGroupList.Children.OfType<Control>().FirstOrDefault(child => child.Tag as string == SectionTags.AddProviderDraft);

    private static T? AddDraftChild<T>(Control? draft, string tag) where T : Control
        => draft?.GetVisualDescendants().OfType<T>().FirstOrDefault(child => child.Tag as string == tag);
    /// <summary>The three counts the card-boundary assertion reads: outlined cards, the providers behind them,
    /// and the children that are the page's own notices rather than a card. Read together so a failure can name
    /// which of the three moved instead of only reporting that the list was not what it should be.</summary>
    internal (int Cards, int Providers, int Notices) ProviderCardLayoutForCheck => (GroupCards.Count(), _chat?.Providers.Count ?? 0, ProviderGroupList.Children.Count - GroupCards.Count());

    /// <summary>Forces a repaint of the group list, so a self-check sees the tree a click would produce.</summary>
    internal void RefreshProviderGroupsForCheck()
    {
        RebuildProviderGroups();
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal bool ProviderWebToolControlsHiddenForCheck(string providerId) => GroupCards.FirstOrDefault(card => card.Tag as string == providerId)?.Child is StackPanel content && !content.Children.OfType<CheckBox>().Any(toggle => toggle.Tag as string == "provider-hosted-search") && !content.Children.OfType<ComboBox>().Any(combo => combo.Tag as string == "provider-web-tools-backend");
    internal bool SetModelListExpandedForCheck(string providerId, bool expanded)
    {
        var toggle = GroupCards.FirstOrDefault(group => group.Tag as string == providerId)?.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.Tag as string == SectionTags.ToggleModelList);
        if (toggle is null)
            return false;
        toggle.IsChecked = expanded;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return true;
    }

    internal bool SetModelEnabledForCheck(string providerId, string modelName, bool enabled)
    {
        var modelRow = GroupCards.Where(group => group.Tag as string == providerId).SelectMany(group => group.GetVisualDescendants().OfType<Border>()).FirstOrDefault(row => row.Tag as string == SectionTags.ModelRow && row.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == modelName));
        var rowToggle = modelRow?.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
        if (rowToggle is null)
            return false;
        rowToggle.IsChecked = enabled;
        return true;
    }

    /// <summary>
    /// The first model row that rendered a context line: its text, the tooltip behind it, and whether any editor
    /// came with it. <c>HasEditor</c> is asserted <b>false</b> on purpose — this row used to carry a text box that
    /// rebuilt the page on every keystroke, and an assertion that only checked the box was present would have
    /// passed the day it was deleted and told nobody why it should never come back.
    ///
    /// <para>A generic "first row" rather than a named one because which models are configured is the user's data,
    /// and an assertion that depends on it would fail for reasons that have nothing to do with this feature.</para>
    /// </summary>
    internal (string Text, string Tip, bool HasEditor) FirstModelContextRowForCheck()
    {
        var text = GroupCards.SelectMany(group => group.GetVisualDescendants().OfType<TextBlock>()).FirstOrDefault(candidate => candidate.Tag as string == SectionTags.ModelContext);
        if (text is null)
            return ("", "", false);
        // Scoped to the row, not the page: a text box elsewhere on Settings is legitimate, one on this row is the
        // defect this line exists to keep out.
        var row = text.GetVisualAncestors().OfType<Border>().FirstOrDefault(candidate => candidate.Tag as string == SectionTags.ModelRow);
        return (text.Text ?? "", ToolTip.GetTip(text) as string ?? "", row is not null && row.GetVisualDescendants().OfType<TextBox>().Any());
    }

    /// <summary>The affiliate text of the last rendered group that showed one — kept so the assertion can read
    /// the disclosure wording, which lives inside a group rather than in a page-level block.</summary>
    private string _affiliateTextForCheck = "";
    /// <summary>Points the sign-in flow at an injected transport so the self-check never opens a socket.</summary>
    internal void UseOAuthHandlerForCheck(System.Net.Http.HttpMessageHandler handler)
    {
        _chat.OAuthHttp = new System.Net.Http.HttpClient(handler);
        // The real flow waits 45 seconds for a human and launches a browser; the harness must do neither, or the
        // shell check's own timeout fires first and reports the run as a hang rather than as a result. Reporting
        // the browser as unavailable is what drives the sign-in down its no-browser ending.
        _chat.OAuthTimeout = TimeSpan.FromSeconds(1);
        _chat.BrowserOpener = _ => false;
    }

    /// <summary>
    /// Runs the real sign-in flow against the first provider that offers it.
    ///
    /// <para>It no longer goes through a row button, because there is no longer a sign-in button on the page:
    /// the entry point is the auth <i>dialog</i>, and a modal cannot be driven from a self-check. What is still
    /// verified end to end is the flow itself — discovery, the exchange, and the status line it writes — which
    /// is the part that can actually break. The button's wiring is covered separately by asserting the rendered
    /// control exists and carries the right label for the state.</para>
    /// </summary>
    internal Task RunOAuthFlowForCheckAsync()
    {
        var provider = _chat?.Providers.FirstOrDefault(candidate => candidate.SupportsOAuth);
        if (provider is null)
            return Task.CompletedTask;
        return SignInAsync(provider, null);
    }

    /// <summary>The device-code window the last sign-in built, whether or not it went on screen.</summary>
    internal DeviceAuthWindow? DeviceWindowForCheck { get; private set; }

    /// <summary>
    /// Runs the device-code sign-in for one named provider and hands back the task instead of awaiting it.
    ///
    /// <para>Returning rather than awaiting is the point: closing the code window only means something while the
    /// flow is still waiting for the approval, which is exactly the moment a check has to act in.</para>
    ///
    /// <para>The window is built but never opened — a shell check measures the settings page's own layout, and a
    /// second live top-level window would be measuring the harness instead of the product. One-way like the
    /// injected transport next to it: this page instance belongs to the check run, and a flag that quietly reset
    /// itself mid-suite would make the window assertions depend on where they sit in the file.</para>
    /// </summary>
    internal Task RunDeviceCodeFlowForCheck(string providerId)
    {
        var provider = _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null)
            return Task.CompletedTask;
        DeviceWindowForCheck = null;
        _deviceWindowStaysOffScreen = true;
        // Shorter than the five minutes a person walking to their phone needs, longer than the interval the
        // scripted poll answers with: a run that is cancelled or approved ends on its own, and only a broken
        // cancel reaches this ceiling. It is not zero, because "the wait ran out" has to stay a thing that can
        // happen *after* the check has finished acting.
        _chat!.DeviceCodeWait = TimeSpan.FromSeconds(10);
        return SignInAsync(provider, null);
    }

    /// <summary>
    /// Closes the device-code window the way the person does, and says whether there was one to close.
    ///
    /// <para>Routed through the window's own <see cref = "Window.Closed"/> event rather than the cancellation
    /// source, because the subscription between the two is the thing under test: a check that cancelled the
    /// token itself would pass on a page that had forgotten to wire the close at all.</para>
    /// </summary>
    internal bool CloseDeviceWindowForCheck()
    {
        if (DeviceWindowForCheck is null)
            return false;
        DeviceWindowForCheck.CloseForCheck();
        return true;
    }

    /// <summary>
    /// Points the model-list fetch at an injected transport, so the self-check can drive the whole path —
    /// request, parse, adopt, cache, repaint — without a live provider.
    ///
    /// <para>A separate seam from <see cref = "UseOAuthHandlerForCheck"/> on purpose: the two answer different
    /// questions, and a handler wired for one would happily answer the other. Sharing a client here would make
    /// a check pass on the wrong response.</para>
    /// </summary>
    internal void UseModelListHandlerForCheck(System.Net.Http.HttpMessageHandler handler)
    {
        if (_chat is not null)
            _chat.ModelListHttp = new System.Net.Http.HttpClient(handler);
    }

    /// <summary>
    /// Runs the model catalog's refresh handler through the same path as its dialog button.
    /// </summary>
    /// <param name = "providerId">Provider to refresh; a missing group is a no-op rather than an exception.</param>
    internal async Task RefreshModelsForCheckAsync(string providerId)
    {
        var provider = _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null)
            return;
        await CreateModelCatalogWindow(provider).RefreshForCheckAsync();
    }

    internal ModelCatalogWindow? ModelCatalogForCheck(string providerId) => _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId)is { } provider ? CreateModelCatalogWindow(provider) : null;
    /// <summary>
    /// Scrolls to the second provider group, so a screenshot shows that the groups actually stack rather than
    /// only that one of them renders. A feature that puts every provider in the same single card would look
    /// identical to this one in a picture of the first group alone.
    /// </summary>
    internal void ScrollToSecondGroupForCheck()
    {
        Avalonia.Visual? node = ProviderGroupList;
        while (node is not null and not ScrollViewer)
            node = node.GetVisualParent();
        if (node is not ScrollViewer viewer || viewer.Content is not Control content)
            return;
        // Skipped through GroupCards rather than the raw children: the list now holds rules between groups, so a
        // positional Skip(1) would land on the divider above the second group and scroll to the boundary
        // between them — the one place the picture cannot show a second card.
        var second = GroupCards.Skip(1).FirstOrDefault();
        if (second is null)
            return;
        var offset = second.TranslatePoint(new Point(0, 0), content) ?? new Point(0, 0);
        viewer.Offset = new Vector(0, Math.Max(0, offset.Y - 40));
    }

    /// <summary>Drives the rail the way a click on it does, for the checks and the shot modes that need one
    /// specific group on screen. This replaced <c>ScrollToProviderCardForCheck</c>, which no longer means
    /// anything: the provider list is not below a fold to crawl past any more, it is a category the rail
    /// selects, and selecting it already lands at its top.</summary>
    internal void SelectCategoryForCheck(string key) => SelectCategory(key);
    internal string SelectedCategory => _selectedCategory;
    /// <summary>
    /// The language tags declared in XAML. The language mapping lives on XAML's <c>Tag</c>; here we just read it out —
    /// assertions rely on it to confirm "the languages the dropdown items declare" and "the supported languages the code knows" haven't diverged.
    /// </summary>
    internal string[] DeclaredLanguages => LanguagePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();
    /// <summary>The theme tags declared in XAML, in the order they appear. Assertions compare it against <see cref = "HubTheme.All"/>.</summary>
    internal string[] DeclaredThemes => ThemePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();
    internal string[] DeclaredUpdateChannels => UpdateChannelPicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();
    /// <summary>Whether the card currently carries the backend disclosure line. Read for the assertion.</summary>
    internal bool SecretBackendLineShown => ProviderGroupList.Children.OfType<Control>().Any(child => SectionTags.SecretBackend.Equals(child.Tag));
    // ── Verification hooks for the update card (used by --verify-shell) ──
    /// <summary>The update card's status line **as rendered**. Read back rather than recomputed: what the
    /// user sees is this control's text, and the check result is only one of the strings it can hold.</summary>
    internal string UpdateStatusText => UpdateStatusLine.Text ?? "";
    /// <summary>The card's phase line as rendered, and whether it is on screen at all — the two are read
    /// separately because "the line says nothing" and "the line is not there" are different states of the card
    /// (an up-to-date card holds an empty, hidden line rather than an empty visible one).</summary>
    internal string UpdateHintText => UpdateHintLine.Text ?? "";
    internal bool UpdateHintShown => UpdateHintLine.IsVisible;
    /// <summary>The word on the card's one button, as rendered: "检查更新" until the download lands, then the
    /// restart's own word. Read off the control because the point of this round is that the button stays on the
    /// card in every phase.</summary>
    internal string UpdateCardButtonLabel => CheckForUpdatesButton.Content as string ?? "";
    internal bool UpdateCardButtonVisible => CheckForUpdatesButton.IsVisible;
    internal bool UpdateCardButtonEnabled => CheckForUpdatesButton.IsEnabled;
    /// <summary>Progress bars this page can still draw. The update card is meant to hold none: its download
    /// moved to the strip's queue, and a bar left here would be a second surface reporting one transfer —
    /// the shape of the bug this round was about.</summary>
    internal int ProgressBarCount => this.GetVisualDescendants().OfType<ProgressBar>().Count();
    // ── Verification hooks for the provider card (used by --verify-shell) ──
    /// <summary>Number of configured providers.</summary>
    internal int ProviderCount => _chat.Providers.Count;
    /// <summary>Whether the "add provider" affordance is rendered.</summary>
    internal bool ProviderButtonsVisible => AddProviderButton.IsVisible;
    /// <summary>The provider card's status line text — provider operations and the sign-in flow report every
    /// outcome through it.</summary>
    internal string StatusLineText => ProviderStatusLine.Text ?? "";

    // ── Verification hooks for the provider group list ──
    //
    // The groups are built in code, so the assertions read the rendered visual tree rather than the model:
    // what matters is that a group for the right provider exists and that its parts are actually on screen.
    // A missing group would otherwise pass a "provider exists in the model" check while the page showed
    // nothing at all.
    /// <summary>One rendered provider group, with the parts the self-check inspects.</summary>
    internal sealed record ProviderGroupView(string Name,
    /// <summary>The rendered wire line: which path this provider's model is sent to ("请求线路 /v1/responses").</summary>
    string WireText, bool HasRemoveButton, bool ShowsNoCredentialNote, bool HasAffiliateBox, bool IsLinked, string LinkedStatusText, bool StatusLabelIsPainted, bool StatusMarkIsPainted,
    /// <summary>The label on the single auth control; it is the state, so it is asserted, not just counted.</summary>
    string AuthButtonText,
    /// <summary>No credential input is rendered anywhere in the group. The whole point of the revision.</summary>
    bool HasCredentialField, string SummaryText, string[] ModelNames, bool[] ModelEnabled, string ActiveModelName, bool HasModelListToggle, bool ModelListExpanded, bool HasUseModelButton, bool HasModelCatalogButton, bool HasAddModelButton, bool ModelCatalogPrecedesAdd, bool HasRemoveAllModelsButton,
    /// <summary>Remove-all sits rightmost, after add: presence and order are different failures.</summary>
    bool RemoveAllFollowsAdd, bool HasProviderOutline, bool HasProviderSurface, bool HasModelRowsOutline,
    /// <summary>
    /// Every model row paints the settings-card surface, not the provider card's. A statically captured
    /// brush (<c>BrushOrNull</c> before attach) left the rows transparent once, and they silently took
    /// the card's darker colour — the rows must be bound, and this is what pins that.
    /// </summary>
    bool HasModelRowSurface, bool HasProviderHeaderDivider,
    /// <summary>
    /// Whether the model section is rendered at all. False for a provider that needs a credential and has
    /// none: the list is fetched with the user's key, so there is nothing to show and asking would be a
    /// request the user never authorized.
    /// </summary>
    bool ShowsModelSection,
    /// <summary>The section is present but the endpoint listed nothing (or has not been asked yet).</summary>
    bool ShowsModelEmptyState,
    /// <summary>Buttons living inside the empty-state stack (should stay zero — toolbar/header own the actions).</summary>
    int EmptyStateButtonCount,
    /// <summary>A change-auth-method control is present (should stay false — disconnect then authenticate).</summary>
    bool HasChangeAuthMethodButton,
    /// <summary>True only if the legacy provider-list refresh control is present; it should stay false.</summary>
    bool HasRefreshButton,
    /// <summary>Legacy rule count; outlined model cards intentionally keep this at zero.</summary>
    int ModelDividerCount);
    /// <summary>The provider groups currently rendered, in display order.</summary>
    internal IReadOnlyList<ProviderGroupView> ProviderGroups => GroupCards.Select(ReadGroup).ToArray();
    /// <summary>Number of provider groups rendered — the count the page actually shows, not the model's.</summary>
    internal int ProviderGroupCount => GroupCards.Count();

    /// <summary>
    /// Whether the list is one outlined card per provider with nothing inserted between them.
    ///
    /// <para>It is deliberately <b>not</b> "the list holds nothing but cards". Two other children belong in this
    /// list on their own terms and each appears only in the state that produces it — the add-endpoint draft while
    /// that form is open, and the secret-backend disclosure line, which exists exactly where the backend is not
    /// the platform's own store (see <see cref = "BuildSecretBackendLine"/>) — so a predicate that counted them as
    /// separators would be asserting against the host rather than against the layout. Each is still counted, not
    /// waved through: a third non-card row, or one of these two in a state that should not have it, fails.</para>
    /// </summary>
    internal bool HasOnlyProviderCards
    {
        get
        {
            if (_chat is null)
                return false;
            var layout = ProviderCardLayoutForCheck;
            return layout.Cards == _chat.Providers.Count && GroupCards.All(card => _chat.Providers.Any(provider => provider.Id == card.Tag as string)) && layout.Notices == (_addingProvider ? 1 : 0) + (SecretBackendLineShown ? 1 : 0) + (_chat.Providers.Count == 0 && !_addingProvider ? 1 : 0);
        }
    }

    /// <summary>The provider cards currently rendered.</summary>
    private IEnumerable<Border> GroupCards => ProviderGroupList.Children.OfType<Border>();

    /// <summary>
    /// Reads one group card.
    ///
    /// <para>An instance method so the card can be checked against the provider model while reading the rendered
    /// visual tree.</para>
    /// </summary>
    private ProviderGroupView ReadGroup(Border group)
    {
        var content = (StackPanel)group.Child!;
        // The header is a two-column Grid: the left column stacks the name pills over the wire line, the right
        // column carries the status mark and the available actions. Both columns are StackPanels, so the two are
        // told apart by column rather than by shape — a positional read would swap them the moment the title
        // gained a pill.
        var header = (Grid)content.Children[0];
        var heading = (StackPanel)header.Children[0];
        var title = heading.Children.OfType<StackPanel>().First();
        var name = title.Children.OfType<TextBlock>().First().Text ?? "";
        // The wire line is tagged rather than positional: it is the only thing in this row that is not the name,
        // and a check that reads "the first TextBlock" would silently start reading the path instead.
        var wireText = heading.Children.OfType<TextBlock>().FirstOrDefault(text => text.Tag as string == SectionTags.Wire)?.Text ?? "";
        // The actions column packs [status stack] + [auth, catalog and remove actions as applicable]. The mark's
        // label is the source of truth for "linked": matching on the drawn tick would make the assertion pass
        // on any provider that happens to render a check-shaped Path, and the label is what the user reads.
        var actions = (StackPanel)header.Children[1];
        var statusStack = actions.Children.OfType<StackPanel>().FirstOrDefault();
        var statusLabel = statusStack?.Children.OfType<TextBlock>().FirstOrDefault();
        var statusText = statusLabel?.Text ?? "";
        var isLinked = statusText == HubStrings.Get("ProviderConnected");
        // Tags distinguish icon actions even though their content is a generated control rather than text.
        var hasRemove = actions.Children.OfType<Button>().Concat(actions.GetLogicalDescendants().OfType<Button>()).Any(button => button.Tag as string == SectionTags.RemoveProvider);
        var hasHeaderCatalogButton = actions.Children.OfType<Button>().Concat(actions.GetLogicalDescendants().OfType<Button>()).Any(button => button.Tag as string == SectionTags.ModelCatalog);
        // "The text is in the tree" is not "the user can see it". A brush that resolved to null while the page
        // was detached leaves a label with the right string and nothing to draw it in, and every check that
        // reads .Text still passes — which is exactly how the first version of this mark shipped a blank
        // status. So the assertion looks at the resolved paint, not at the string.
        var statusLabelIsPainted = statusLabel?.Foreground is not null;
        var statusMarkIsPainted = statusStack?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Shape>().Any(shape => shape.Stroke is not null) ?? false;
        var sections = content.Children.OfType<StackPanel>().Where(section => section.Tag is string).ToArray();
        var modelSection = sections.FirstOrDefault(section => (string)section.Tag! == SectionTags.Models);
        var modelActions = modelSection?.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<StackPanel>().FirstOrDefault(stack => stack.Children.OfType<Button>().Any());
        var modelActionButtons = modelActions?.Children.OfType<Button>().ToArray() ?? [];
        var modelCatalogIndex = Array.FindIndex(modelActionButtons, button => button.Tag as string == SectionTags.ModelCatalog);
        var addModelIndex = Array.FindIndex(modelActionButtons, button => button.Tag as string == SectionTags.AddModelButton);
        var hasModelCatalogButton = modelCatalogIndex >= 0;
        var hasAddModelButton = addModelIndex >= 0;
        var modelCatalogPrecedesAdd = modelCatalogIndex >= 0 && addModelIndex > modelCatalogIndex;
        var removeAllIndex = Array.FindIndex(modelActionButtons, button => button.Tag as string == SectionTags.RemoveAllModels);
        var hasRemoveAllModelsButton = removeAllIndex >= 0;
        var removeAllFollowsAdd = addModelIndex >= 0 && removeAllIndex > addModelIndex;
        var hasProviderOutline = group.BorderThickness.Left > 0 && group.BorderBrush is not null;
        var hasProviderSurface = ThemeProbe.IsToken(group.Background, "Hub.ProviderSurface");
        var hasProviderHeaderDivider = content.Children.OfType<Border>().Any(border => border.Tag as string == SectionTags.ProviderHeaderDivider);
        // "Not rendered" is a *Border* placeholder rather than a missing section, so that "this group has no
        // model list" and "this group failed to build its model list" stay distinguishable — both would read as
        // an absent StackPanel otherwise.
        var modelsHidden = content.Children.OfType<Border>().Any(border => border.Tag as string == SectionTags.ModelsHidden);
        var showsModelSection = modelSection is not null;
        // The empty-state line and model controls are looked up by tag rather than by position.
        var showsModelEmptyState = modelSection?.GetLogicalDescendants().Any(node => (node as Control)?.Tag as string == SectionTags.ModelsEmpty) ?? false;
        var emptyStateButtonCount = modelSection?.GetLogicalDescendants().OfType<StackPanel>().FirstOrDefault(stack => stack.Tag as string == SectionTags.ModelsEmpty)?.GetLogicalDescendants().OfType<Button>().Count() ?? 0;
        var hasChangeAuthMethodButton = content.GetVisualDescendants().OfType<Button>().Any(button => button.Tag as string == SectionTags.ChangeAuthMethod);
        var hasRefresh = modelSection?.GetLogicalDescendants().OfType<Button>().Any(button => button.Tag as string == SectionTags.RefreshModels) ?? false;
        var modelList = modelSection?.GetLogicalDescendants().OfType<StackPanel>().FirstOrDefault(list => list.Tag as string == SectionTags.ModelList);
        var hasModelListToggle = modelSection?.GetLogicalDescendants().OfType<ToggleButton>().Any(button => button.Tag as string == SectionTags.ToggleModelList) ?? false;
        var hasUseModelButton = modelSection?.GetLogicalDescendants().OfType<Button>().Any(button => button.Content?.ToString() == HubStrings.Get("ModelUse")) ?? false;
        // Each model row is [name (+ optional "in use" pill), optional description] in a text stack. The rows
        // live in a nested untagged StackPanel under the section's heading Grid.
        var modelRows = modelSection?.Children.OfType<Border>().Where(row => row.Tag as string == SectionTags.ModelRow).Concat(modelSection.Children.OfType<StackPanel>().SelectMany(list => list.Children.OfType<Border>()).Where(row => row.Tag as string == SectionTags.ModelRow)).ToArray() ?? [];
        var hasModelRowsOutline = modelRows.Length > 0 && modelRows.All(row => row.BorderThickness.Left > 0 && row.BorderBrush is not null);
        var hasModelRowSurface = modelRows.Length > 0 && modelRows.All(row => ThemeProbe.IsToken(row.Background, "Hub.Surface"));
        var modelNames = new List<string>();
        var modelEnabled = new List<bool>();
        var activeModel = "";
        foreach (var row in modelRows)
        {
            var text = (StackPanel)((Grid)row.Child!).Children[0];
            // Name is the first TextBlock; there is no nameRow and no "in use" pill any more — every row in
            // the list is an active, selectable model.
            var modelName = text.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
            modelNames.Add(modelName);
            modelEnabled.Add(true);
        }

        // Model rows are individually outlined cards, so there should be no extra dividers between them.
        const int modelDividers = 0;
        // The single auth control, read by its tag. Its *label* is the state: the same button reads
        // "authenticate" or "disconnect", so a check that only counted buttons would pass for either.
        // It lives in the header actions stack (status + auth + reveal cluster).
        var authButton = actions.Children.OfType<Button>().Concat(actions.GetLogicalDescendants().OfType<Button>()).FirstOrDefault(button => button.Tag as string == SectionTags.AuthButton);
        var authButtonText = authButton?.Content as string ?? "";
        // The whole point of this revision: no credential input exists anywhere in the list. A password-class
        // TextBox, or any TextBox with PasswordChar set, anywhere under the group is the regression this
        // check exists to catch — checked over descendants because a future layout could nest it deeper.
        var hasCredentialField = content.GetVisualDescendants().OfType<TextBox>().Any(box => box.Classes.Contains("password") || box.PasswordChar != default(char));
        var summary = content.Children.OfType<TextBlock>().FirstOrDefault(text => text.Tag as string == SectionTags.Summary)?.Text ?? "";
        var showsNoCredential = content.Children.OfType<TextBlock>().Any(text => text.Tag as string == SectionTags.NoCredential);
        var hasAffiliate = content.Children.OfType<Border>().Any(border => border.Tag as string == SectionTags.Affiliate);
        return new ProviderGroupView(name, wireText, hasRemove, showsNoCredential, hasAffiliate, isLinked, statusText, statusLabelIsPainted, statusMarkIsPainted, authButtonText, hasCredentialField, summary, modelNames.ToArray(), modelEnabled.ToArray(), activeModel, hasModelListToggle, modelList?.IsVisible == true, hasUseModelButton, hasModelCatalogButton && !hasHeaderCatalogButton, hasAddModelButton, modelCatalogPrecedesAdd, hasRemoveAllModelsButton, removeAllFollowsAdd, hasProviderOutline, hasProviderSurface, hasModelRowsOutline, hasModelRowSurface, hasProviderHeaderDivider, showsModelSection && !modelsHidden, showsModelEmptyState, emptyStateButtonCount, hasChangeAuthMethodButton, hasRefresh, modelDividers);
    }

    /// <summary>
    /// The group rendered for one provider id, or <c>null</c> when no group was rendered for it.
    ///
    /// Keyed on the id rather than the display name: a preset's name is copy ("Ollama (local)") and can change
    /// with the manifest or the language, so a name lookup would report a missing group as soon as the copy
    /// moved — a false failure that hides the real one.
    /// </summary>
    internal ProviderGroupView? ProviderGroupForId(string providerId)
    {
        var border = ProviderGroupList.Children.OfType<Border>().FirstOrDefault(candidate => candidate.Tag as string == providerId);
        return border is null ? null : ReadGroup(border);
    }

    /// <summary>Whether the affiliate disclosure is rendered at all (inside the OrcaRouter group).</summary>
    internal bool AffiliateDisclosureVisible => ProviderGroups.Any(group => group.HasAffiliateBox);
    internal string AffiliateDisclosureText => _affiliateTextForCheck;
}
