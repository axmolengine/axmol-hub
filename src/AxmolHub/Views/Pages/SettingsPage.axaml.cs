using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
// GetLogicalDescendants (not GetVisualDescendants) on purpose: the model section is read for a group that may
// not be attached to a visual root yet, and the visual walk returns an empty collection in that case — which
// turns "the check found nothing" into "the check passed".
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The settings page (WPF's <c>SettingsPage</c>).
///
/// Beyond "porting the settings over", it also carries a verification duty: **does localization
/// actually take effect**. Switching language requires reloading the resource dictionary in place
/// and updating **already-existing controls**' text — this can't be judged by reading code (whether
/// Avalonia's <c>DynamicResource</c> re-resolves when the resource dictionary changes can only be
/// verified empirically), so <c>--verify-shell</c> really switches the language once and reads back
/// the controls' text.
///
/// Switching the data root is handled by the shell (<see cref="MainWindow.SwitchDataRoot"/>): it
/// only rebuilds the workspace and pages, **not the window** — so this doesn't depend on the shell
/// shape. The page here only "asks the user for a directory".
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private readonly PreferencesStore _preferencesStore;
    private readonly HubPreferences _preferences;
    private readonly ChatWorkspace _chat;
    private readonly Action<string> _openFolder;

    /// <summary>After a language change the shell must recompute its own **imperative** copy (title, bottom-left version line).</summary>
    private readonly Action _languageChanged;

    /// <summary>Asks the shell to switch the data root. Returns whether it actually switched.</summary>
    private readonly Func<string, bool> _switchDataRoot;

    /// <summary>Asks the shell to switch the theme (persist → apply → revert on failure). Returns null on
    /// success, the error message on failure. The shell owns the sequence: the bottom menu's appearance
    /// items drive the same path, so the picker must not grow a second copy of it.</summary>
    private readonly Func<string, string?> _applyTheme;

    /// <summary>Suppresses <c>SelectionChanged</c> during initialization: the WPF version likewise had a <c>preferencesReady</c> gate.</summary>
    private bool _ready;

    /// <summary>Keeps workspace notifications from rebuilding the editor picker during its own selection event.</summary>
    private bool _savingEditorSelection;

    /// <summary>True while a manual check is running: it owns the status line, so
    /// <see cref="RenderUpdateState"/> must not clobber it. The download needs no such flag — its phase
    /// lives in <see cref="UpdateService"/> and <see cref="RenderUpdateState"/> reads it from there.</summary>
    private bool _checking;

    /// <summary>Whether this page is currently subscribed to <see cref="UpdateService.Changed"/>
    /// (hooked while on screen only).</summary>
    private bool _updateHooked;

    // ── Provider draft state (v3) ──
    // A provider's edit view is a draft that lives *inside* its group, and an "add model" / "add custom
    // endpoint" draft is always the first child of its list. While any draft is open, ChatWorkspace.Changed
    // must not rebuild the groups — that would tear apart the very TextBox being typed in (the historical
    // inline-edit bug). The rebuild is deferred and applied when the draft closes.

    /// <summary>The id of the provider whose *provider* edit draft is open (the pencil beside its name),
    /// or <c>null</c> when none.</summary>
    private string? _editingProviderId;

    /// <summary>The id of the provider whose *model* draft (add row or context-window edit) is open, or
    /// <c>null</c> when none. Deliberately a second id rather than a reuse of
    /// <see cref="_editingProviderId"/>: a model draft is placed inside that provider's model list, and
    /// the group's header must stay the collapsed one — sharing the id replaced the header with the
    /// "Edit provider" form, so pressing a model's pencil showed two forms at once, both with a default
    /// Save button, both closed by one Cancel.</summary>
    private string? _modelDraftProviderId;

    /// <summary>Whether an "add custom endpoint" draft is open at the top of the provider list.</summary>
    private bool _addingProvider;
    private Task? _addProviderCommitTask;

    /// <summary>Whether an "add model" draft row is open inside a provider.</summary>
    private bool _addingModel;

    /// <summary>The name of the model being edited (context-window draft), or <c>null</c> when none.</summary>
    private string? _editingModelName;

    /// <summary>True while any draft is open, which suspends the group rebuild (see the note above).</summary>
    private bool DraftOpen => _editingProviderId is not null || _addingProvider || _modelDraftProviderId is not null;

    /// <summary>Set when a rebuild was skipped while a draft was open; applied when the draft closes.</summary>
    private bool _pendingReload;

    /// <summary>For the XAML loader and design-time preview (missing it raises AVLN3001).</summary>
    public SettingsPage()
    {
        _workspace = null!;
        _preferencesStore = null!;
        _preferences = new HubPreferences();
        _chat = null!;
        _openFolder = _ => { };
        _languageChanged = () => { };
        _switchDataRoot = _ => false;
        _applyTheme = _ => null;

        InitializeComponent();
    }

    public SettingsPage(
        HubWorkspace workspace,
        PreferencesStore preferencesStore,
        HubPreferences preferences,
        ChatWorkspace chat,
        Action<string> openFolder,
        Action languageChanged,
        Func<string, bool> switchDataRoot,
        Func<string, string?> applyTheme)
    {
        _workspace = workspace;
        _preferencesStore = preferencesStore;
        _preferences = preferences;
        _chat = chat;
        _openFolder = openFolder;
        _languageChanged = languageChanged;
        _switchDataRoot = switchDataRoot;
        _applyTheme = applyTheme;

        InitializeComponent();

        _workspace.Changed += Reload;

        LanguagePicker.SelectionChanged += (_, _) => OnLanguageChanged();
        ThemePicker.SelectionChanged += (_, _) => OnThemeChanged();
        // The custom URL is committed on focus loss rather than on every keystroke: each commit
        // writes the settings file, and "typed half a URL" is not a state worth persisting.
        DownloadSourcePicker.SelectionChanged += (_, _) => OnDownloadSourceChanged();
        ToolApprovalPicker.SelectionChanged += (_, _) => OnToolApprovalChanged();
        // The revoke-all button is the one escape from a grant the user cannot find the card for any more — the
        // session that made it may be long archived, while the trust it wrote is still answering for every session.
        RevokeAllTrustedToolsButton.Click += (_, _) => RevokeAllTrusted();
        RevokeAllTrustedCommandsButton.Click += (_, _) => RevokeAllTrustedCommands();
        RevokeAllTrustedComputerAppsButton.Click += (_, _) => RevokeAllTrustedComputerApps();
        UpdateChannelPicker.SelectionChanged += (_, _) => OnUpdateChannelChanged();
        CustomDownloadSourceBox.LostFocus += (_, _) => OnCustomDownloadSourceChanged();
        ChooseDataDirectoryButton.Click += async (_, _) => await ChooseDataDirectoryAsync();
        ChooseProjectDirectoryButton.Click += async (_, _) => await ChooseProjectDirectoryAsync();
        OpenDataFolderButton.Click += (_, _) => _openFolder(_workspace.Store.Root);
        BrowseEditorButton.Click += async (_, _) => await BrowseEditorAsync();
        DetectEditorButton.Click += (_, _) =>
        {
            _workspace.RefreshDetectedEditors();
            RenderEditors();
        };
        EditorPicker.SelectionChanged += (_, _) => OnEditorChanged();
        // One button, two jobs, decided by the phase the card is rendering (see RenderUpdateState): a check
        // while nothing is downloaded, and the restart once the bytes have landed — the same restart, through
        // the same armed gate, as the sidebar's update pill.
        CheckForUpdatesButton.Click += async (_, _) => await OnUpdateActionClickedAsync();
        AutoDownloadCheck.IsCheckedChanged += (_, _) => OnAutoDownloadChanged();
        SpawnCheck.IsCheckedChanged += (_, _) => OnSpawnChanged();
        AskQuestionsCheck.IsCheckedChanged += (_, _) => OnAskQuestionsChanged();
        WebFetchCheck.IsCheckedChanged += (_, _) => OnWebFetchChanged();
        WebSearchCheck.IsCheckedChanged += (_, _) => OnWebSearchChanged();
        ShowThinkingCheck.IsCheckedChanged += (_, _) => OnShowThinkingChanged();
        CommitSummaryPrefixesBox.LostFocus += (_, _) => OnCommitSummaryPrefixesChanged();
        WireProviders();
        InitializeCategories();

        // The update card is a *view* of the shared UpdateService, so a check started elsewhere (the shell's
        // startup check) shows up here live. The download's **progress** is still not this page's to report —
        // that is the strip's queue slot, which measures it — but its **phase** is, because this card is where
        // the user pressed 「检查更新」: it says where the bytes are going, and it turns its own button into the
        // restart once they have landed. Hooked while the page is on screen only: a cached page that has been
        // navigated away shouldn't keep rendering.
        AttachedToVisualTree += (_, _) => HookUpdateService();
        DetachedFromVisualTree += (_, _) => UnhookUpdateService();

        // DataRootNote's copy goes through XAML's {DynamicResource}; **don't** assign it imperatively here:
        // an imperative value won't change on language switch, and the self-check reads this control's text to judge whether localization works.
        Reload();
    }

    // ───────────────────────── The category rail ─────────────────────────

    /// <summary>The row the card opens on when nobody asked for a specific one.</summary>
    internal const string DefaultCategory = "appearance";

    /// <summary>Every category the rail can land on, in rail order. The shell holds the deep-link string, so
    /// this is the one list both sides agree on — a key that is not here is not a category.</summary>
    internal static readonly string[] CategoryKeys =
        ["appearance", "providers", "permission", "engine", "storage", "updates", "editors"];

    internal static bool IsCategory(string? key)
        => key is { Length: > 0 } && Array.IndexOf(CategoryKeys, key) >= 0;

    /// <summary>The rail's rows and the pane each one shows, paired in code rather than by naming convention:
    /// a row that opens nothing, or a pane nobody can reach, is exactly the mistake a convention hides.</summary>
    private (string Key, RadioButton Row, Control Pane)[] _categories = [];

    private string _selectedCategory = DefaultCategory;

    private void InitializeCategories()
    {
        _categories =
        [
            ("appearance", CatAppearance, PaneAppearance),
            ("providers", CatProviders, PaneProviders),
            ("permission", CatPermission, PanePermission),
            ("engine", CatEngine, PaneEngine),
            ("storage", CatStorage, PaneStorage),
            ("updates", CatUpdates, PaneUpdates),
            ("editors", CatEditors, PaneEditors),
        ];

        foreach (var (key, row, _) in _categories)
        {
            row.Click += (_, _) => SelectCategory(key);
        }

        SelectCategory(_selectedCategory);
    }

    /// <summary>Shows one group and hides the other six. The rail's checked state is set here rather than left
    /// to the toggle: a raised <c>Click</c> does not flip a ToggleButton (measured, and the reason the shell's
    /// own checks drive rows through this method), and a row that reads checked while its pane is hidden is a
    /// worse answer than one that reads unchecked.</summary>
    internal void SelectCategory(string key)
    {
        if (!IsCategory(key))
        {
            key = DefaultCategory;
        }

        _selectedCategory = key;
        if (_categories.Length == 0)
        {
            // Constructed before the rail was paired — the constructor's own Reload lands here.
            return;
        }

        foreach (var (candidate, row, pane) in _categories)
        {
            var shown = candidate == key;
            row.IsChecked = shown;
            pane.IsVisible = shown;
        }

        // Every group shares the one detail scroll, so an offset left over from the bottom of a long provider
        // list would open the next group somewhere in its middle.
        DetailScroll.Offset = default;
    }

    /// <summary>Whether any picker in the card has its dropdown up. The shell's Escape handler asks, because a
    /// dropdown is its own top-level: the key belongs to the list the reader is looking at, not to the card
    /// behind it.</summary>
    internal bool HasOpenDropDown
        => this.GetLogicalDescendants().OfType<ComboBox>().Any(box => box.IsDropDownOpen);

    /// <summary>The currently selected language tag. Unknown/none fall back to the default language (same rule as <see cref="HubTexts.Normalize"/>).</summary>
    internal string SelectedLanguage
        => LanguagePicker.SelectedItem is ComboBoxItem { Tag: string tag } && HubTexts.IsSupported(tag)
            ? tag
            : HubTexts.DefaultLanguage;

    /// <summary>The currently selected theme. Unknown/none fall back to following the system (same rule as <see cref="HubTheme.Normalize"/>).</summary>
    internal string SelectedTheme
        => ThemePicker.SelectedItem is ComboBoxItem { Tag: string tag } && HubTheme.IsSupported(tag)
            ? HubTheme.Normalize(tag)
            : HubTheme.DefaultTheme;

    internal void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        _ready = false;
        try
        {
            // A language switch discards any open draft rather than transplanting half-typed input across a
            // locale boundary. Clearing the flags first lets ReloadProviders below rebuild from scratch.
            CloseDraft();

            LocalizeThemeItems();
            LocalizeDownloadSourceItems();
            LocalizeToolApprovalItems();
            // The ledger is rebuilt rather than re-bound, for the same reason the provider groups are: every row
            // carries the key its revoke button has to write back, and the empty line is a property of all three
            // lists together.
            RebuildTrustedGrants();
            LocalizeUpdateChannelItems();
            SelectLanguage(HubStrings.Language);
            SelectTheme(_preferences.Theme);
            SelectDownloadSource(_preferences.DownloadSource);
            SelectToolApproval(_preferences.ToolApprovalMode);
            SelectUpdateChannel(HubReleaseInfo.IsPrereleaseBuild ? UpdateChannels.Preview : _preferences.UpdateChannel);
            UpdateChannelPicker.IsEnabled = !HubReleaseInfo.IsPrereleaseBuild;
            CustomDownloadSourceBox.Text = _preferences.CustomDownloadSource ?? "";
            DataLocation.Text = _workspace.Store.Root;
            DefaultProjectLocation.Text = _preferences.ProjectDirectory ?? HubStrings.Get("NotSelected");

            var state = _workspace.State;
            var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
            DefaultEngine.Text = engine?.ToString() ?? HubStrings.Get("NoDefault");
            if (!_savingEditorSelection)
            {
                RenderEditors();
            }
            // The current-version line is read from the assembly; it is not localizable copy, so it
            // is safe to assign imperatively here and won't need a language-switch refresh.
            CurrentVersionLine.Text = HubStrings.Get("CurrentVersion") + "  v" +
                (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            // The auto-download switch mirrors the preference. Assigning it here doesn't re-save:
            // _ready is false for the whole reload (the same gate the dropdowns rely on). It is
            // disabled on a dev/portable copy, where there is nothing to auto-update.
            AutoDownloadCheck.IsChecked = _preferences.AutoDownloadUpdates;
            AutoDownloadCheck.IsEnabled = UpdateService.Instance.IsInstalled;
            SpawnCheck.IsChecked = _preferences.AllowSpawnedSessions;
            AskQuestionsCheck.IsChecked = _preferences.AllowAssistantQuestions;
            WebFetchCheck.IsChecked = _preferences.AllowOutboundWebFetch;
            WebSearchCheck.IsChecked = _preferences.AllowHostedWebSearch || _preferences.AllowDeepSeekWebApi;
            ShowThinkingCheck.IsChecked = _preferences.ShowThinking;
            CommitSummaryPrefixesBox.Text = string.Join(", ", CommitSummary.NormalizePrefixes(_preferences.CommitSummaryPrefixes));
            // Re-render the update card from the last known check: this re-localizes the status line
            // on a language switch (it is written imperatively) and reflects a check the shell already
            // ran at startup (the update dot and this card share UpdateService.Last).
            RenderUpdateState();
            ReloadProviders();
        }
        finally
        {
            _ready = true;
        }
    }

    /// <summary>
    /// Selects an item by the tag the dropdown item itself declares. If not found, selects the first item — never leaves an empty "nothing selected" state.
    /// Verification code calls it directly — same idea as navigation using <c>NavSettings.IsChecked = true</c>:
    /// go through the real event path rather than bypassing the event to mutate internal state.
    /// </summary>
    internal void SelectLanguage(string language)
    {
        var index = 0;
        foreach (var item in LanguagePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == HubTexts.Normalize(language))
            {
                LanguagePicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        LanguagePicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Writes the three download-source labels from the text table. Imperative for the same reason as
    /// <see cref="LocalizeThemeItems"/>: <c>DynamicResource</c> on a ComboBoxItem inside <c>Items</c>
    /// resolves empty, and <see cref="Reload"/> runs on every language switch.
    /// </summary>
    private void LocalizeDownloadSourceItems()
    {
        foreach (var item in DownloadSourcePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } source && DownloadSources.IsKnown(tag))
            {
                source.Content = HubStrings.Get(DownloadSources.TextKey(tag));
            }
        }
    }

    /// <summary>The download-source tags declared in XAML, in order. Same contract as <see cref="DeclaredThemes"/>.</summary>
    internal string[] DeclaredDownloadSources
        => DownloadSourcePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>The selected download source; unknown tags fall back to the official host (same rule as <see cref="DownloadSources.Normalize"/>).</summary>
    internal string SelectedDownloadSource
        => DownloadSourcePicker.SelectedItem is ComboBoxItem { Tag: string tag } && DownloadSources.IsKnown(tag)
            ? DownloadSources.Normalize(tag)
            : DownloadSources.GitHubId;

    /// <summary>Selects a source by the tag the item declares; not found → the first (GitHub).</summary>
    internal void SelectDownloadSource(string source)
    {
        var index = 0;
        foreach (var item in DownloadSourcePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == DownloadSources.Normalize(source))
            {
                DownloadSourcePicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        DownloadSourcePicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Commits a download-source change. Invalid custom input is refused **before** the preference is
    /// written, and the UI is reverted: saving "custom" with a URL we can't use would make the next
    /// install fail with an error the user has no way to connect back to this page.
    /// </summary>
    private void OnDownloadSourceChanged()
    {
        if (!_ready) return;

        var source = SelectedDownloadSource;
        var custom = CustomText;
        // The value must really have changed: Reload re-selects on every refresh, and SelectionChanged
        // fires for that too (the WPF version's `preferencesReady` gate exists for the same reason).
        if (source == _preferences.DownloadSource && custom == (_preferences.CustomDownloadSource ?? "")) return;

        if (TrySaveDownloadSource(source, custom)) return;

        _ready = false;
        SelectDownloadSource(_preferences.DownloadSource);
        _ready = true;
    }

    private void OnCustomDownloadSourceChanged()
    {
        if (!_ready) return;

        var custom = CustomText;
        if (custom == (_preferences.CustomDownloadSource ?? "")) return;
        // While another source is selected the text is just a draft; committing it would save a
        // preference that isn't in effect yet.
        if (SelectedDownloadSource != DownloadSources.CustomId) return;

        if (TrySaveDownloadSource(SelectedDownloadSource, custom)) return;

        _ready = false;
        CustomDownloadSourceBox.Text = _preferences.CustomDownloadSource ?? "";
        _ready = true;
    }

    private string CustomText => (CustomDownloadSourceBox.Text ?? "").Trim();

    /// <summary>
    /// Writes the three permission-mode labels, and each mode's own explanation as a tooltip on that item.
    /// Imperative for the same measured reason as <see cref="LocalizeThemeItems"/>: a ComboBoxItem inside
    /// <c>Items</c> is not attached to the logical tree until the dropdown opens, so <c>DynamicResource</c>
    /// resolves empty. Only <c>Content</c> and the item's tooltip are touched, so this cannot re-enter
    /// <see cref="OnToolApprovalChanged"/>.
    ///
    /// <para>The hints come from <see cref="ChatPanel.ToolApprovalModeHintKey"/> rather than from copy of its own:
    /// the composer's compact menu describes the same three tiers, and two spellings of "what auto-approve lets
    /// through" is how the page and the menu start disagreeing about the gate.</para>
    /// </summary>
    private void LocalizeToolApprovalItems()
    {
        foreach (var item in ToolApprovalPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } mode && IsToolApprovalMode(tag))
            {
                mode.Content = HubStrings.Get(ChatPanel.ToolApprovalModeKey(tag));
                ToolTip.SetTip(mode, HubStrings.Get(ChatPanel.ToolApprovalModeHintKey(tag)));
            }
        }
    }

    private static bool IsToolApprovalMode(string tag)
        => tag is ToolApprovalModes.Ask or ToolApprovalModes.Auto or ToolApprovalModes.Full;

    internal void SelectToolApproval(string mode)
    {
        var normalized = ToolApprovalModes.Normalize(mode);
        var index = 0;
        foreach (var item in ToolApprovalPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == normalized)
            {
                ToolApprovalPicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        ToolApprovalPicker.SelectedIndex = 0;
    }

    private void LocalizeUpdateChannelItems()
    {
        foreach (var item in UpdateChannelPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } channel && UpdateChannels.IsSupported(tag))
            {
                channel.Content = HubStrings.Get(tag == UpdateChannels.Preview
                    ? "UpdateChannelPreview"
                    : "UpdateChannelStable");
            }
        }
    }

    internal string SelectedUpdateChannel
        => UpdateChannelPicker.SelectedItem is ComboBoxItem { Tag: string tag } && UpdateChannels.IsSupported(tag)
            ? UpdateChannels.Normalize(tag)
            : UpdateChannels.DefaultChannel;

    internal void SelectUpdateChannel(string channel)
    {
        var normalized = UpdateChannels.Normalize(channel);
        var index = 0;
        foreach (var item in UpdateChannelPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == normalized)
            {
                UpdateChannelPicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        UpdateChannelPicker.SelectedIndex = 0;
    }

    private void OnUpdateChannelChanged()
    {
        if (!_ready || HubReleaseInfo.IsPrereleaseBuild) return;

        var channel = SelectedUpdateChannel;
        if (channel == _preferences.UpdateChannel) return;

        _preferences.UpdateChannel = channel;
        _preferencesStore.Save(_preferences);
        UpdateService.Instance.UpdateChannel = channel;
    }

    private void OnToolApprovalChanged()
    {
        if (!_ready) return;

        // One owner for the write. The assistant already reads this setting, offers the same change on the
        // composer's own menu row, and records the move in the activity log; a second path here would be a second
        // meaning for "the default moved", and the one that skips the audit line is the one that leaves a mystery
        // in the log the next time a call asks — or stops asking — for a reason nobody can read back.
        _chat.SetDefaultApprovalMode(SelectedToolApprovalMode);
    }

    /// <summary>
    /// The app-wide trust ledger: three lists, one heading and one line that speaks for all of them. Built in code
    /// because each row's revoke button has to carry the key it belongs to — the same reason
    /// <see cref="RebuildProviderGroups"/> is not a bound template.
    ///
    /// <para>One renderer for the three lists rather than one per list: "nothing is granted yet" is a property of the
    /// whole ledger, so a list that drew its own empty line could only speak for itself while the other two happened
    /// to be empty as well. It is also what lets a group leave the page entirely — a heading and a revoke-all button
    /// over no rows is a control that does nothing, and three of those read as three settings that failed to
    /// render.</para>
    ///
    /// <para>No per-row tier annotation: which card a grant came from is a fact about a session's sandbox, and this
    /// page has no session in front of it. What a grant does and does not cover is said once, on the ledger's
    /// heading, where it is true for every row.</para>
    /// </summary>
    private void RebuildTrustedGrants()
    {
        TrustedToolList.Children.Clear();
        TrustedCommandList.Children.Clear();
        TrustedComputerAppList.Children.Clear();
        if (_chat is null) return;

        foreach (var tool in _chat.TrustedTools)
        {
            TrustedToolList.Children.Add(TrustRow(tool, TrustGrantRowTag.Tool, () => RevokeTrusted(tool)));
        }

        foreach (var verb in _chat.TrustedCommands)
        {
            TrustedCommandList.Children.Add(TrustRow(verb, TrustGrantRowTag.Command, () => RevokeTrustedCommand(verb)));
        }

        foreach (var app in _chat.TrustedComputerApps)
        {
            TrustedComputerAppList.Children.Add(TrustRow(app, TrustGrantRowTag.ComputerApp, () => RevokeTrustedComputerApp(app)));
        }

        ShowTrustGroup(TrustedToolGroup, TrustedToolList);
        ShowTrustGroup(TrustedCommandGroup, TrustedCommandList);
        ShowTrustGroup(TrustedComputerAppGroup, TrustedComputerAppList);
        TrustGrantsEmptyLine.IsVisible = TrustedToolList.Children.Count == 0
            && TrustedCommandList.Children.Count == 0
            && TrustedComputerAppList.Children.Count == 0;
    }

    /// <summary>One grant, as a row: the key the settings file stores, and the button that takes exactly it back.</summary>
    private static Grid TrustRow(string key, object tag, Action revoke)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Tag = tag };
        row.Children.Add(new TextBlock
        {
            Text = key,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        var button = new Button
        {
            Classes = { "quiet" },
            Content = HubStrings.Get("RevokeTrustedTool"),
            Tag = key,
        };
        button.Click += (_, _) => revoke();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    /// <summary>An empty list takes its heading and its revoke-all button off the page with it.</summary>
    private static void ShowTrustGroup(Control group, StackPanel list) => group.IsVisible = list.Children.Count > 0;

    private void RevokeTrusted(string tool)
    {
        if (_chat.RevokeTrustedTool(tool)) RebuildTrustedGrants();
    }

    private void RevokeAllTrusted()
    {
        if (_chat.RevokeAllTrustedTools()) RebuildTrustedGrants();
    }

    private void RevokeTrustedCommand(string key)
    {
        if (_chat.RevokeTrustedCommand(key)) RebuildTrustedGrants();
    }

    private void RevokeAllTrustedCommands()
    {
        if (_chat.RevokeAllTrustedCommands()) RebuildTrustedGrants();
    }

    private void RevokeTrustedComputerApp(string app)
    {
        if (_chat.RevokeTrustedComputerApp(app)) RebuildTrustedGrants();
    }

    private void RevokeAllTrustedComputerApps()
    {
        if (_chat.RevokeAllTrustedComputerApps()) RebuildTrustedGrants();
    }

    /// <summary>The <c>Tag</c> a ledger row carries, one per list. What the self-checks count a row by lives here,
    /// so a row and the count of it cannot disagree about the string — it used to be spelled three times over, two
    /// of them as literals in the check surface.</summary>
    private static class TrustGrantRowTag
    {
        internal const string Tool = "trusted-tool-row";
        internal const string Command = "trusted-command-row";
        internal const string ComputerApp = "trusted-computer-app-row";
    }

    private bool TrySaveDownloadSource(string source, string custom)
    {
        if (DownloadSources.Validate(source, custom) is { } problem)
        {
            _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"), HubStrings.Get(problem));
            return false;
        }

        _preferences.DownloadSource = source;
        _preferences.CustomDownloadSource = custom.Length == 0 ? null : custom;
        _preferencesStore.Save(_preferences);
        return true;
    }

    /// <summary>Copy key for a theme value. Kept next to the values so adding a theme can't leave its label behind.</summary>
    internal static string ThemeTextKey(string theme) => HubTheme.Normalize(theme) switch
    {
        HubTheme.Light => "ThemeLight",
        HubTheme.Dark => "ThemeDark",
        _ => "ThemeSystem",
    };

    /// <summary>
    /// The languages the interface speaks and the name each one is called by its own speakers. The picker in
    /// XAML declares the same pairs and an assertion compares the two — the bottom menu cannot read the
    /// picker's items (the page may not exist yet), and a second copy of the list is exactly where they would
    /// drift. The display names are **not** localized: they are here for people who cannot read the current UI.
    /// </summary>
    internal static readonly (string Tag, string Display)[] LanguageChoices =
        [(HubTexts.ChineseLanguage, "简体中文"), (HubTexts.EnglishLanguage, "English")];

    /// <summary>
    /// Writes the three labels from the text table. It is imperative **on purpose**: a ComboBoxItem
    /// inside <c>Items</c> is not attached to the logical tree until the dropdown opens, and
    /// <c>DynamicResource</c> resolves through the resource host it is attached to — measured here,
    /// all three labels silently came back empty. Reload runs on every language switch
    /// (see <c>MainWindow.ApplyLanguage</c>), which is what keeps them in the current language.
    /// Only the <c>Content</c> is touched, never the selection, so this can't re-enter
    /// <see cref="OnThemeChanged"/>.
    /// </summary>
    private void LocalizeThemeItems()
    {
        foreach (var item in ThemePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } theme && HubTheme.IsSupported(tag))
            {
                theme.Content = HubStrings.Get(ThemeTextKey(tag));
            }
        }
    }

    /// <summary>
    /// Selects a theme item by the tag the item itself declares; not found → the first item (following
    /// the system), never an empty "nothing selected" state. Same contract as <see cref="SelectLanguage"/>.
    /// </summary>
    internal void SelectTheme(string theme)
    {
        var index = 0;
        foreach (var item in ThemePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == HubTheme.Normalize(theme))
            {
                ThemePicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        ThemePicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Switches language: write settings → reload the resource dictionary → existing controls follow with new text.
    ///
    /// The order can't be reversed: reload the dictionary before writing settings, otherwise a failed write leaves "UI switched language but settings not saved",
    /// reverting on the next launch and looking like a lost setting.
    ///
    /// Notify the shell after reloading: the shell and pages have **imperatively** written copy that DynamicResource doesn't cover.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (!_ready)
        {
            return;
        }

        var previous = HubStrings.Language;
        var language = SelectedLanguage;
        if (language == previous)
        {
            return;
        }

        try
        {
            _preferences.Language = language;
            _preferencesStore.Save(_preferences);

            // Reload in place: DynamicResource re-resolves when the resource dictionary changes, so
            // already-built controls (and other windows) also swap text — no window rebuild needed.
            HubStrings.Apply(language, Application.Current!);
            _languageChanged();
        }
        catch (Exception ex)
        {
            // On failure, revert the UI, otherwise it would show a language that disagrees with the settings file.
            _ready = false;
            _preferences.Language = previous;
            HubStrings.Apply(previous, Application.Current!);
            SelectLanguage(previous);
            _ready = true;
            _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Switches theme through the shell (<see cref="MainWindow.UseTheme"/>), which owns the
    /// persist → apply → revert sequence — the bottom menu's appearance items share it.
    ///
    /// The page's own job is only the picker: on failure put the selection back, otherwise the
    /// dropdown would show a theme that isn't in effect.
    /// </summary>
    private void OnThemeChanged()
    {
        if (!_ready)
        {
            return;
        }

        var previous = ThemeService.Current;
        var theme = SelectedTheme;
        if (theme == previous)
        {
            return;
        }

        var error = _applyTheme(theme);
        if (error is null)
        {
            return;
        }

        _ready = false;
        SelectTheme(previous);
        _ready = true;
        _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"), error);
    }

    private async Task ChooseDataDirectoryAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var picked = await Pickers.PickFolderAsync(top, HubStrings.Get("Select data directory"));
        if (picked.Outcome != PickOutcome.Picked)
        {
            if (picked.Outcome == PickOutcome.NotLocal)
            {
                await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
            }

            return;
        }

        try
        {
            // After a successful switch **this page instance is discarded** (the shell clears the page cache and rebuilds),
            // so here we must not touch any of our own controls: that would read an already-disposed workspace.
            // The success path ends here; only the failure path needs a dialog — and this page is still alive then.
            // The whole workspace is rebuilt on switch (page cache cleared), so confirm first: it is the one
            // "Browse" on this page that really re-homes Hub rather than just storing a path.
            var confirm = string.Format(HubStrings.Get("SwitchDataRootPrompt"), picked.Path);
            if (await HubDialog.ShowAsync(top as Window, HubStrings.Get("DataDirectory"), confirm, HubDialogButtons.OkCancel) != HubDialogResult.Ok)
            {
                return;
            }

            _switchDataRoot(picked.Path!);
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }

    private async Task ChooseProjectDirectoryAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var picked = await Pickers.PickFolderAsync(top, HubStrings.Get("Select default project directory"));
        if (picked.Outcome != PickOutcome.Picked)
        {
            if (picked.Outcome == PickOutcome.NotLocal)
            {
                await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
            }

            return;
        }

        try
        {
            // Unlike the data root, the project parent directory **only stores a path, no switch**: it carries no state, so switching needs no window rebuild.
            // Persistence and validation both happen in the workspace; on failure it rolls the preference back to the previous value.
            _workspace.SetProjectDirectory(picked.Path!);
            Reload();
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Draws the one editor row: what was detected here, plus the current choice when detection cannot see it (a
    /// hand-picked application, or one installed where the catalog does not look). Showing that row is what makes
    /// the page say what will actually be launched instead of an empty dropdown over a working setting.
    ///
    /// Imperative for the same reason <see cref="LocalizeThemeItems"/> is: a <see cref="ComboBoxItem"/> in
    /// <c>Items</c> is not attached to the logical tree until the dropdown opens, so a <c>DynamicResource</c>
    /// there resolves to nothing. The rows are product names — not localized copy — except the placeholder
    /// and the "nothing found" line, which every <see cref="Reload"/> repaints.
    /// </summary>
    private void RenderEditors()
    {
        EditorPicker.Items.Clear();
        EditorPicker.PlaceholderText = HubStrings.Get("NotSelected");

        // One row per path. The saved choice wins over the one detection found — an older install, or a place the
        // catalog does not look, is exactly what "Browse…" was for — and a saved editor that is gone is not shown at
        // all, because the row is a promise that the press will launch something.
        var saved = CodeEditors.Bind(_workspace.State.CodeEditor);
        var rows = _workspace.DetectedEditors
            .Where(candidate => saved is null || !string.Equals(candidate.Path, saved.Path, StringComparison.Ordinal))
            .ToList();
        if (saved is not null)
        {
            rows.Insert(0, saved);
        }

        // This picker edits the global default, so its selection must not follow a project-specific override.
        // With no usable global choice, the first detected editor is the fallback used for new projects too.
        var selected = saved ?? rows.FirstOrDefault();
        foreach (var binding in rows)
        {
            var item = new ComboBoxItem { Content = binding.Preset.DisplayName, Tag = new CodeEditor(binding.Preset.Id, binding.Path) };
            EditorPicker.Items.Add(item);
            if (selected is not null && string.Equals(binding.Path, selected.Path, StringComparison.Ordinal))
            {
                EditorPicker.SelectedItem = item;
            }
        }

        EditorLocation.Text = selected?.Path ?? HubStrings.Get("NoEditorFound");
    }

    private async void OnEditorChanged()
    {
        if (!_ready || EditorPicker.SelectedItem is not ComboBoxItem { Tag: CodeEditor editor })
        {
            return;
        }

        await ChooseEditorAsync(editor);
    }

    /// <summary>Makes an editor the default, and says so when the path could not be launched.</summary>
    private async Task ChooseEditorAsync(CodeEditor editor)
    {
        _savingEditorSelection = true;
        bool selected;
        try
        {
            // SelectEditorAsync notifies the workspace synchronously. Reload still updates the page, but leaves this
            // picker intact until its SelectionChanged handler has finished.
            selected = await _workspace.SelectEditorAsync(editor.Id, editor.Path, global: true);
        }
        finally
        {
            _savingEditorSelection = false;
        }

        if (selected)
        {
            EditorLocation.Text = CodeEditors.Bind(editor)?.Path ?? editor.Path;
            return;
        }

        // A rejected choice needs the old value restored. Suppress the selection event caused by rebuilding rows.
        var wasReady = _ready;
        _ready = false;
        try
        {
            RenderEditors();
        }
        finally
        {
            _ready = wasReady;
        }

        await HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"),
            HubStrings.Get("NoEditorFound") + "\n" + editor.Path);
    }

    /// <summary>
    /// Points at an application the catalog does not know. What an editor *is* differs per host — a bundle on
    /// macOS, an executable elsewhere — and <see cref="Pickers.PickEditorAsync"/> is the one place that answer
    /// lives; whether the result can be launched is <see cref="CodeEditors.Bind"/>'s question.
    /// </summary>
    private async Task BrowseEditorAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        try
        {
            var picked = await Pickers.PickEditorAsync(top, HubStrings.Get("SelectEditor"));
            _workspace.Log.Write($"Editor picker: {picked.Outcome} · {picked.Path ?? "(no path)"}");
            if (picked.Outcome != PickOutcome.Picked)
            {
                if (picked.Outcome == PickOutcome.NotLocal)
                {
                    await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
                }

                return;
            }

            await ChooseEditorAsync(new CodeEditor(CodeEditors.CustomId, picked.Path!));
        }
        catch (Exception ex)
        {
            // An exception in an async event handler disappears into the dispatcher, which reads exactly like "the
            // button does nothing". A picker that fails has to say so, in the log and on screen.
            _workspace.Log.Write("Editor picker failed: " + ex);
            await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Persists the "auto-download" choice and, when it is switched on with an update already known,
    /// starts the background download right away — otherwise the choice would not take effect until the
    /// next check, which reads as "the checkbox does nothing".
    /// </summary>
    /// <summary>Spawning is turned off by default and the tool says so when a model asks, so this switch is the
    /// only entrance it has — which is also why it lives beside the permission mode rather than hidden in a
    /// provider row: it is a decision about what the assistant may do on this machine, not about which model to use.</summary>
    private void OnSpawnChanged()
    {
        if (!_ready) return;
        var value = SpawnCheck.IsChecked == true;
        if (value == _preferences.AllowSpawnedSessions) return;
        _preferences.AllowSpawnedSessions = value;
        _preferencesStore.Save(_preferences);
    }

    private void OnAskQuestionsChanged()
    {
        if (!_ready) return;
        var value = AskQuestionsCheck.IsChecked == true;
        if (value == _preferences.AllowAssistantQuestions) return;
        _preferences.AllowAssistantQuestions = value;
        _preferencesStore.Save(_preferences);
    }

    /// <summary>Outbound fetching ships <b>on</b>, unlike spawning, so this checkbox is an exit rather than an
    /// entrance: its job is to let a person say "nothing this machine reads goes out without me deciding". The
    /// assistant's own refusal names this setting when it is off, which is the only reason the switch and the
    /// sentence can not drift apart.</summary>
    private void OnWebFetchChanged()
    {
        if (!_ready) return;
        var value = WebFetchCheck.IsChecked == true;
        if (value == _preferences.AllowOutboundWebFetch) return;
        _preferences.AllowOutboundWebFetch = value;
        _preferencesStore.Save(_preferences);
    }

    /// <summary>One global switch enables the configured provider's search route. Keep both legacy preference
    /// fields in sync so existing installations migrate without changing their current choice.</summary>
    private void OnWebSearchChanged()
    {
        if (!_ready) return;
        var value = WebSearchCheck.IsChecked == true;
        if (value == (_preferences.AllowHostedWebSearch || _preferences.AllowDeepSeekWebApi)
            && _preferences.AllowHostedWebSearch == value && _preferences.AllowDeepSeekWebApi == value) return;
        _preferences.AllowHostedWebSearch = value;
        _preferences.AllowDeepSeekWebApi = value;
        _preferencesStore.Save(_preferences);
    }

    /// <summary>The one switch on this pane that changes pixels already on screen, so it cannot stop at the file.
    /// The assistant page is cached and its renderer only lays down turns past the last one it painted — a flip
    /// that only wrote the setting would sit there looking broken until the person started a new conversation.
    /// The notice is what makes the turns already in the transcript redraw.</summary>
    private void OnShowThinkingChanged()
    {
        if (!_ready) return;
        var value = ShowThinkingCheck.IsChecked == true;
        if (value == _preferences.ShowThinking) return;
        _preferences.ShowThinking = value;
        _preferencesStore.Save(_preferences);
        _chat.NotifyAppSettingsChanged();
    }

    private void OnCommitSummaryPrefixesChanged()
    {
        if (!_ready) return;
        var parsed = CommitSummary.NormalizePrefixes((CommitSummaryPrefixesBox.Text ?? "")
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _preferences.CommitSummaryPrefixes = [.. parsed];
        CommitSummaryPrefixesBox.Text = string.Join(", ", parsed);
        _preferencesStore.Save(_preferences);
        _chat.NotifyAppSettingsChanged();
    }

    private void OnAutoDownloadChanged()
    {
        if (!_ready)
        {
            return;
        }

        var value = AutoDownloadCheck.IsChecked == true;
        if (value == _preferences.AutoDownloadUpdates)
        {
            return;
        }

        _preferences.AutoDownloadUpdates = value;
        _preferencesStore.Save(_preferences);
        UpdateService.Instance.AutoDownload = value;
        if (value)
        {
            UpdateService.Instance.StartPendingDownload();
        }
    }

    /// <summary>Live-renders the card whenever the shared update state changes (check result or download
    /// progress). The event can be raised from the download thread, so marshal before touching controls.</summary>
    private void OnUpdateChanged() => Dispatcher.UIThread.Post(RenderUpdateState);

    private void HookUpdateService()
    {
        if (_updateHooked)
        {
            return;
        }

        _updateHooked = true;
        UpdateService.Instance.Changed += OnUpdateChanged;
        RenderUpdateState();
    }

    private void UnhookUpdateService()
    {
        if (!_updateHooked)
        {
            return;
        }

        _updateHooked = false;
        UpdateService.Instance.Changed -= OnUpdateChanged;
    }

    /// <summary>
    /// The update card's one press, in whichever phase the card is showing. The check is the entry point — the
    /// button is clicked *because* the user wants a visible answer — until the bytes have landed, and then the
    /// same button owes the restart. That restart is the rail's own press (<see cref="UpdateService.PressUpdateRestart"/>,
    /// the armed second tap included) rather than a second gate of this page's: replacing the process is the one
    /// thing the app cannot undo from the inside, so it keeps one rule and one arm wherever it is offered from.
    /// </summary>
    private async Task OnUpdateActionClickedAsync()
    {
        var service = UpdateService.Instance;
        if (IsRestartPending(service.Last, service.Download))
        {
            service.PressUpdateRestart();
            return;
        }

        await CheckForUpdatesAsync();
    }

    /// <summary>
    /// The settings-page "Check for updates" entry point. Unlike the silent startup check, this one
    /// surfaces every outcome: the button is clicked *because* the user wants a visible answer.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        // The status line doubles as a "busy" indicator; the button goes dark for the duration so a second
        // click can't stack a second network round-trip on top of the first.
        _checking = true;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusLine.Text = HubStrings.Get("CheckingUpdate");
        try
        {
            await UpdateService.Instance.CheckAsync();
        }
        finally
        {
            // The renderer owns the button's enabled state from here (see RenderUpdateState): the busy flag is
            // one of the two reasons it can be dark, so clearing it is what lets the renderer decide again.
            _checking = false;
            RenderUpdateState();
        }
    }

    /// <summary>Whether the card's button owes the restart rather than a check: an update is pending *and* its
    /// bytes have landed. The phase, not the switch — a cancelled or never-started transfer leaves
    /// <see cref="UpdateService.DownloadState.None"/>, which still wants the check button.</summary>
    private static bool IsRestartPending(UpdateService.CheckOutcome? last, UpdateService.DownloadState download)
        => last is { Result: UpdateService.CheckResult.UpdateAvailable }
           && download == UpdateService.DownloadState.Ready;

    /// <summary>The word on the card's button, as a pure function so the branches can be asserted without a real
    /// download behind them. The three keys are borrowed from the rail on purpose: one action, one sentence —
    /// "重启完成更新" means the same thing wherever it is offered, and a second translation of it would let the
    /// two buttons name the same press differently.</summary>
    internal static string UpdateCardLabelKey(
        UpdateService.CheckOutcome? last, UpdateService.DownloadState download, bool armed)
        => IsRestartPending(last, download)
            ? (armed ? "TaskRestartArmed" : "TaskRestartAction")
            : "CheckForUpdates";

    /// <summary>The card's phase line: where the transfer is, or that nobody has started it. Null means the line
    /// is not shown at all — an up-to-date or unchecked card has nothing to add to the result line above, and
    /// the ready phase is the button's own sentence rather than a second one.</summary>
    internal static string? UpdateCardHintKey(
        UpdateService.CheckOutcome? last, UpdateService.DownloadState download)
        => last is { Result: UpdateService.CheckResult.UpdateAvailable }
            ? download switch
            {
                UpdateService.DownloadState.Downloading => "UpdateDownloadingHint",
                UpdateService.DownloadState.Ready => null,
                _ => "UpdatePendingDownloadHint",
            }
            : null;

    /// <summary>
    /// Renders the card from <see cref="UpdateService"/>: the check result, the phase line under it, and what the
    /// card's one button is for right now. Called when the page is shown, on <see cref="UpdateService.Changed"/>,
    /// and after a manual check, so a check started elsewhere (the shell's startup check) shows up here too and
    /// the words re-localize on a language switch.
    ///
    /// <para>What is deliberately <b>not</b> on this card: the download's progress, its percentage and its cancel
    /// — the strip's queue slot measures the transfer and its popover cancels it, because this page can be
    /// navigated away from while the bytes are moving, and an update whose end nobody can see is an update nobody
    /// can restart. What <b>is</b> here is which phase that transfer is in and what the user owes it, because this
    /// card is where they asked: the result line names the version and keeps naming it through every phase, the
    /// phase line says where the bytes are, and the button becomes the restart the moment there is one to press.</para>
    /// </summary>
    private void RenderUpdateState()
    {
        // A manual check owns the status line while it runs; don't overwrite "checking…".
        if (_checking)
        {
            return;
        }

        var service = UpdateService.Instance;

        var last = service.Last;
        UpdateStatusLine.Text = last?.Result switch
        {
            UpdateService.CheckResult.UpdateAvailable => string.Format(HubStrings.Get("UpdateReady"), service.PendingVersion),
            UpdateService.CheckResult.UpToDate => HubStrings.Get("UpdateUpToDate"),
            UpdateService.CheckResult.NotInstalled => HubStrings.Get("UpdateNotInstalled"),
            UpdateService.CheckResult.Failed => HubStrings.Get("UpdateFailed"),
            _ => HubStrings.Get("UpdateCheckHint"),
        };

        var hintKey = UpdateCardHintKey(last, service.Download);
        UpdateHintLine.Text = hintKey is null ? "" : HubStrings.Get(hintKey);
        UpdateHintLine.IsVisible = hintKey is not null;

        // The button never leaves the card. It used to: a check that found something took it away and left the
        // user with a sentence about a version and nothing to press, while the fetch that sentence pointed at
        // lives on another surface. Now one button follows the phase — the check while nothing is downloaded, the
        // restart once the bytes have landed — and it goes dark only while it has nothing to offer: a check is
        // already running, or the transfer is and the queue row is what reports it.
        var restartPending = IsRestartPending(last, service.Download);
        CheckForUpdatesButton.Content = HubStrings.Get(UpdateCardLabelKey(last, service.Download, service.RestartArmed));
        CheckForUpdatesButton.Classes.Set("primary", restartPending);
        CheckForUpdatesButton.IsVisible = true;
        CheckForUpdatesButton.IsEnabled = !_checking
            && service.Download != UpdateService.DownloadState.Downloading;
    }

    /// <summary>
    /// The provider card's own status line. Separate from the update card's on purpose: they report on
    /// unrelated subsystems, and a page can only register one control per <c>x:Name</c>.
    ///
    /// <para>Text, ink and visibility are set together rather than by separate calls: the line is hidden while
    /// empty, so a caller that assigned the text after asking it to re-style would leave it invisible — a
    /// failure that looks exactly like "the operation did nothing".</para>
    /// </summary>
    private void SetProviderStatus(bool error, string text)
    {
        ProviderStatusLine.Text = text;
        ProviderStatusLine.Classes.Set("muted", !error);
        ProviderStatusLine.Classes.Set("danger", error);
        ProviderStatusLine.IsVisible = text.Length > 0;
    }

    // ───────────────────────── Model providers ─────────────────────────
    //
    // This block used to live in the assistant drawer and moved here when the assistant became a full page:
    // the conversation page keeps the model picker, and endpoint/model management — adding, enabling,
    // removing, keying — happens in Settings ▸ Models. GitHub Copilot draws the same separation.
    //
    // Adding a custom provider remains the only path to a local model, so the logic is unchanged; only its
    // host moved.

    private void WireProviders()
    {
        AddProviderButton.Click += async (_, _) => await AddProviderAsync();
    }

    /// <summary>
    /// Rebuilds the provider group list. Called from <see cref="Reload"/>, which is the only place that needs
    /// to: the rows carry no persistent state of their own — they are a rendering of <c>ChatWorkspace</c> — so
    /// a language switch and a data change are the same operation.
    /// </summary>
    private void ReloadProviders()
    {
        if (_chat is null) return;

        // A draft is live input; rebuilding under it destroys the control being typed in. Defer the rebuild
        // and apply it when the draft closes. Reload (a language switch) is the one caller that must not be
        // deferred: it calls CloseDraft() first, so by the time it reaches here no draft is open.
        if (DraftOpen)
        {
            _pendingReload = true;
            return;
        }

        AddProviderButton.Content = HubStrings.Get("AddProvider");
        RebuildProviderGroups();
    }

    /// <summary>
    /// Renders one group per configured provider. Large model lists start collapsed; smaller lists remain
    /// expanded so the short, useful ones still take one glance.
    ///
    /// <para>Built in code rather than bound: each group owns handlers that close over the provider they
    /// belong to (which credential to rotate, which model to mark), and a bound template would need a
    /// view-model per row for four controls. The cost of code-building is that a language switch has to
    /// rebuild rather than re-bind, which is why <see cref="Reload"/> calls straight in here.</para>
    /// </summary>
    private void RebuildProviderGroups()
    {
        ProviderGroupList.Children.Clear();
        if (_chat is null) return;

        // The add-custom-endpoint draft is always the first child, above every provider — the form is placed
        // where the eye already is, and it never scrolls out from under the user who just opened it.
        if (_addingProvider)
        {
            ProviderGroupList.Children.Add(BuildAddProviderDraft());
        }

        if (_chat.Providers.Count == 0 && !_addingProvider)
        {
            var empty = new StackPanel { Spacing = 6 };
            empty.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("NoProvider"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
            empty.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("NoProviderGuide"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
            ProviderGroupList.Children.Add(empty);
            return;
        }

        foreach (var provider in _chat.Providers)
        {
            ProviderGroupList.Children.Add(BuildProviderGroup(provider));
        }

        // One line for the whole card rather than one per provider: which backend holds the keys is a fact about
        // this machine, not about OrcaRouter. Windows says nothing, because DPAPI is the promise every install
        // already got; the file tier and an unreadable blob both speak up, because there "stored in the OS
        // credential store" would be an overstatement the user could act on.
        if (BuildSecretBackendLine(_chat.SecretBackend) is { } backend)
        {
            ProviderGroupList.Children.Add(backend);
            if (_chat.SecretBackend.Kind == SecretStoreKind.Keychain && _chat.SecretBackend.DegradedReason is not null)
            {
                var retry = new Button { Content = HubStrings.Get("TaskRetry") };
                retry.Click += (_, _) =>
                {
                    try { _chat.RetrySecretReads(); RebuildProviderGroups(); }
                    catch (SecretStoreException exception) { SetProviderStatus(true, exception.Message); }
                };
                ProviderGroupList.Children.Add(retry);
            }
        }
    }

    /// <summary>
    /// The line that names where keys actually go, or <c>null</c> when the tier needs no explaining.
    /// Static and descriptor-driven on purpose: the same function renders the real card and the verification
    /// hook, so an assertion about the file tier's wording is an assertion about what the page would show.
    /// </summary>
    private static Control? BuildSecretBackendLine(SecretStoreDescriptor backend)
    {
        if (backend.DegradedReason is { Length: > 0 } degraded)
        {
            return new TextBlock
            {
                Text = HubStrings.Get("AuthSecretBackendDegraded") + degraded,
                Classes = { "danger" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Tag = SectionTags.SecretBackend,
            };
        }

        // Only the file tier explains itself. DPAPI is the promise every Windows install already got, a None
        // descriptor already surfaces through the auth dialog's own refusal, and repeating either here would be
        // a second label for information the screen carries once already.
        if (backend.Kind is not (SecretStoreKind.EncryptedFile or SecretStoreKind.Keychain)) return null;

        return new TextBlock
        {
            Text = HubStrings.Get(backend.Kind == SecretStoreKind.Keychain ? "AuthSecretBackendKeychain" : "AuthSecretBackendFile"),
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Tag = SectionTags.SecretBackend,
        };
    }

    /// <summary>
    /// One provider group: a collapsed one-line header by default, or the in-place edit draft when the pencil is
    /// pressed. The model section is shown in <b>both</b> states — collapsing hides the edit form, not the
    /// model list, because the list is what a person glances at to answer "what can I call here?".
    ///
    /// <para>There is deliberately no "browse but not edit" expanded state: the only reason to open a provider
    /// is to change it, so the pencil opens the draft directly and a stale read-only layout never has to be kept
    /// in sync with the draft.</para>
    /// </summary>
    private Control BuildProviderGroup(ModelProvider provider)
    {
        var editing = _editingProviderId == provider.Id;
        var content = new StackPanel { Spacing = 12 };

        content.Children.Add(editing ? BuildEditDraft(provider) : BuildCollapsedHeader(provider));

        // Identity row is separated from the model list by a hairline, so the card reads as two bands.
        content.Children.Add(new Border
        {
            Height = 1,
            Background = BrushOrNull("Hub.BorderSubtle"),
            Tag = SectionTags.ProviderHeaderDivider,
            Margin = new Thickness(0, 2),
        });

        // The model section is present in both states; only the edit form is gated on the draft.
        content.Children.Add(BuildModelSection(provider));

        if (!provider.CanAuthenticate)
        {
            content.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("ProviderNoCredential"),
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Tag = SectionTags.NoCredential,
            });
        }

        if (provider.Affiliate)
        {
            content.Children.Add(BuildAffiliateSection(provider));
        }

        // Outlined card: the list of providers has to read as a list. Tag stays the provider id so the
        // self-check can find the group by id.
        return new Border
        {
            Classes = { "provider-card" },
            Tag = provider.Id,
            Child = content,
        };
    }

    /// <summary>
    /// The collapsed one-line header: name (with source/current/disabled pills), the auth status mark, the
    /// state-dependent auth action, and — revealed on hover — the pencil and trash icons.
    /// </summary>
    private Control BuildCollapsedHeader(ModelProvider provider)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock
        {
            Text = provider.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        title.Children.Add(Pill(
            HubStrings.Get(provider.IsCustom ? "ProviderCustomTag" : "ProviderBuiltInTag"),
            brush: null));

        if (!provider.Enabled)
        {
            title.Children.Add(Pill(HubStrings.Get("ProviderDisabledTag"),
                BrushOrNull("Hub.DangerBorder"), BrushOrNull("Hub.DangerText")));
        }
        else if (_chat?.ActiveProvider?.Id == provider.Id)
        {
            title.Children.Add(Pill(HubStrings.Get("ProviderInUse"), BrushOrNull("Hub.AccentSoft"),
                BrushOrNull("Hub.TextOnAccent")));
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Status mark is always painted: green + words when callable, grey + words otherwise. Colour never
        // carries the state alone.
        actions.Children.Add(BuildLinkedStatusMark(provider));

        // The single auth control. Its label is the state: 鉴权 when not linked, 断开鉴权 when linked.
        // Method inputs never live on this page — the button opens the bound method's dialog.
        if (provider.CanAuthenticate)
        {
            var linked = IsLinked(provider);
            var auth = new Button
            {
                Content = HubStrings.Get(linked ? "DisconnectProvider" : "Authenticate"),
                Classes = { "quiet" },
                FontSize = 11,
                Padding = new Thickness(8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.AuthButton,
            };
            auth.Click += async (_, _) =>
            {
                if (linked) await DisconnectProviderAsync(provider);
                else await AuthenticateProviderAsync(provider);
            };
            actions.Children.Add(auth);
            if (provider.IsOpenAiOAuth && provider.Credential?.Secret is not null && !linked)
            {
                var clear = new Button { Content = HubStrings.Get("DisconnectProvider"), Classes = { "quiet" },
                    FontSize = 11, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
                clear.Click += async (_, _) => await DisconnectProviderAsync(provider);
                actions.Children.Add(clear);
            }
            if (provider.IsOpenAiPlan && provider.Credential is { } planCredential)
            {
                var resume = new Button { Content = HubStrings.Get("OpenAiResumePlan"), Classes = { "quiet" },
                    FontSize = 11, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
                resume.Click += (_, _) => { planCredential.PlanUsagePaused = false; resume.IsVisible = false; };
                resume.IsVisible = planCredential.PlanUsagePaused;
                actions.Children.Add(resume);
                actions.AttachedToVisualTree += (_, _) => resume.IsVisible = planCredential.PlanUsagePaused;
            }
            if (provider.IsOpenAiPlan && linked && !OpenAiTokens.Decode(provider.Credential!.Secret!).HasPlanUsage)
            {
                var enable = new Button { Content = HubStrings.Get("OpenAiEnablePlan"), Classes = { "quiet" },
                    FontSize = 11, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
                enable.Click += async (_, _) => await SignInAsync(provider, enable);
                actions.Children.Add(enable);
            }
        }

        var reveal = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "reveal" },
        };

        // Custom providers own their identity fields; a built-in is pinned by the manifest and has no
        // configuration left to edit on this page.
        if (provider.IsCustom)
        {
            var edit = new Button
            {
                Content = BuildEditIcon(),
                Classes = { "quiet" },
                Padding = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.EditProvider,
            };
            ToolTip.SetTip(edit, HubStrings.Get("EditProvider"));
            edit.Click += (_, _) => OpenEditDraft(provider);
            reveal.Children.Add(edit);
        }

        if (ChatWorkspace.CanRemoveProvider(provider))
        {
            // Always visible: removing a provider is a primary management action, not a hover secret.
            // The custom-provider pencil stays on the reveal cluster.
            var remove = new Button
            {
                Content = BuildRemoveIcon(),
                Classes = { "quiet" },
                Padding = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.RemoveProvider,
            };
            ToolTip.SetTip(remove, HubStrings.Get("RemoveProviderIcon"));
            remove.Click += async (_, _) => await RemoveProviderAsync(provider);
            actions.Children.Add(remove);
        }

        actions.Children.Add(reveal);

        // The wire this provider's model is sent on, under the name: the one place a claim about the endpoint
        // becomes visible before a 400 has to explain it. The value comes from ChatClientFactory, not from a copy
        // of the rule here — a settings row that promises a wire the request will not use is worse than none.
        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(title);
        heading.Children.Add(new TextBlock
        {
            Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ProviderWireFormat"),
                AgentWirePath(provider)),
            Classes = { "muted" },
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Tag = SectionTags.Wire,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "provider-row" } };
        Grid.SetColumn(heading, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(heading);
        grid.Children.Add(actions);
        return grid;
    }

    /// <summary>
    /// The path this provider's selected model will be posted to, resolved by the same function the client factory
    /// uses at send time (<c>ChatClientFactory.ProtocolFor</c>) and then turned into the path it is called on.
    /// </summary>
    private static string AgentWirePath(ModelProvider provider)
        => AxmolHub.Agent.ChatClientFactory.PathOf(
            AxmolHub.Agent.ChatClientFactory.ProtocolFor(provider, provider.Model));

    /// <summary>The brush key for a provider's status dot: green when callable, tertiary otherwise.</summary>
    private static string ProviderStatusBrushKey(ModelProvider provider)
        => provider.IsCallReady ? "Hub.Success" : "Hub.TextTertiary";

    /// <summary>
    /// The edit draft: configuration only. Name and base URL are read-only for a built-in provider (the
    /// manifest pins them). Authentication never appears here — no key box, no sign-in button — so the form
    /// cannot contradict the status mark on the header.
    /// </summary>
    private Control BuildEditDraft(ModelProvider provider)
    {
        var isBuiltIn = provider is { IsCustom: false };

        var stack = new StackPanel { Spacing = 8, Tag = SectionTags.ProviderDraft };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("EditProviderTitle"),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(Pill(
            HubStrings.Get(provider.IsCustom ? "ProviderCustomTag" : "ProviderBuiltInTag"),
            brush: null));
        titleRow.Children.Add(BuildLinkedStatusMark(provider));
        stack.Children.Add(titleRow);

        if (isBuiltIn)
        {
            stack.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("ProviderBuiltInLockedHint"),
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1*"),
            RowDefinitions = new RowDefinitions("Auto"),
            ColumnSpacing = 12,
        };

        var name = new TextBox { Text = provider.Name, IsReadOnly = isBuiltIn };
        if (isBuiltIn) name.Classes.Add("muted");
        fields.Children.Add(BuildField(HubStrings.Get("ProviderName"), name));
        Grid.SetColumn(fields.Children[^1], 0);
        Grid.SetRow(fields.Children[^1], 0);

        var baseUrl = new TextBox { Text = provider.BaseUrl, IsReadOnly = isBuiltIn };
        if (isBuiltIn) baseUrl.Classes.Add("muted");
        fields.Children.Add(BuildField(HubStrings.Get("ProviderBaseUrl"), baseUrl));
        Grid.SetColumn(fields.Children[^1], 1);
        Grid.SetRow(fields.Children[^1], 0);

        stack.Children.Add(fields);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), Classes = { "quiet" } };
        cancel.Click += (_, _) => CloseDraft();
        var save = new Button { Content = HubStrings.Get("Save"), Classes = { "primary" }, IsDefault = true };
        save.Click += (_, _) => CommitEditDraft(provider, name, baseUrl);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        return stack;
    }

    /// <summary>Wraps one full-width labelled field in a provider draft.</summary>
    private static Control BuildField(string label, Control box, string? hint = null)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Classes = { "muted" },
            FontSize = 11,
        });
        stack.Children.Add(box);
        if (hint is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return stack;
    }

    /// <summary>The linked-state mark in words + glyph, the three states the dot compresses into colour.</summary>
    private Control BuildLinkedStatusMark(ModelProvider provider)
    {
        var linked = IsLinked(provider);
        var keyless = !provider.RequiresCredential;
        var ready = keyless || linked;

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Children.Add(ready ? BuildCheckIcon() : BuildUnlinkedIcon());

        var label = new TextBlock
        {
            Text = HubStrings.Get(
                keyless ? "ProviderKeylessReady"
                    : provider.NeedsOpenAiRelogin ? "OpenAiLegacyRelogin"
                    : linked ? "ProviderConnected"
                    : "ProviderNotConnected"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                ready ? "Hub.Success" : "Hub.TextTertiary"));
        status.Children.Add(label);
        return status;
    }

    /// <summary>The custom endpoint form inserted in place at the top of the provider list. Each setting gets
    /// its own full-width row, following GitHub Desktop's custom endpoint fields.</summary>
    private Control BuildAddProviderDraft()
    {
        var stack = new StackPanel { Spacing = 12, Tag = SectionTags.AddProviderDraft };
        stack.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("AddCustomProvider"),
            FontWeight = FontWeight.SemiBold,
        });

        var name = new TextBox { Tag = SectionTags.AddDraftName, PlaceholderText = HubStrings.Get("ProviderDisplayNameHint") };
        var baseUrl = new TextBox { Tag = SectionTags.AddDraftBaseUrl, PlaceholderText = "https://api.example.com/v1" };
        var wire = new ComboBox
        {
            Tag = SectionTags.AddDraftWire,
            SelectedIndex = 0,
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = HubStrings.Get("ProviderWireChat"), Tag = ModelProtocols.Chat },
                new ComboBoxItem { Content = HubStrings.Get("ProviderWireResponses"), Tag = ModelProtocols.Responses },
            },
        };
        var apiKey = new TextBox
        {
            Tag = SectionTags.AddDraftKey,
            PasswordChar = '•',
            PlaceholderText = "sk-…",
            Classes = { "password" },
        };
        var customHeaders = new TextBox
        {
            Tag = SectionTags.AddDraftHeaders,
            PlaceholderText = "{}",
        };

        stack.Children.Add(BuildField(HubStrings.Get("ProviderName"), name, HubStrings.Get("ProviderDisplayNameHint")));
        stack.Children.Add(BuildField(HubStrings.Get("ProviderBaseUrl"), baseUrl, HubStrings.Get("ProviderBaseUrlHint")));
        stack.Children.Add(BuildField(HubStrings.Get("ProviderWireApi"), wire, HubStrings.Get("ProviderWireApiHint")));
        stack.Children.Add(BuildField(HubStrings.Get("ProviderApiKeyOptional"), apiKey, HubStrings.Get("ProviderApiKeyHint")));
        stack.Children.Add(BuildField(HubStrings.Get("ProviderCustomHeaders"), customHeaders, HubStrings.Get("ProviderCustomHeadersHint")));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), Classes = { "quiet" } };
        cancel.Click += (_, _) => CloseDraft();
        var save = new Button
        {
            Content = HubStrings.Get("AddProviderAction"),
            Classes = { "primary" },
            IsDefault = true,
            Tag = SectionTags.AddDraftSave,
        };
        save.Click += async (_, _) =>
        {
            _addProviderCommitTask = CommitAddProviderDraft(name, baseUrl, wire, apiKey, customHeaders);
            await _addProviderCommitTask;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        void RefreshDraft()
        {
            var problem = ChatWorkspace.ValidateDraft(name.Text ?? "", baseUrl.Text ?? "", customHeaders.Text);
            save.IsEnabled = problem is null;
            var entered = !string.IsNullOrWhiteSpace(name.Text) || !string.IsNullOrWhiteSpace(baseUrl.Text)
                          || !string.IsNullOrWhiteSpace(customHeaders.Text);
            if (problem is not null && entered)
                SetProviderStatus(true, HubStrings.Get(problem));
            else
                SetProviderStatus(false, "");
        }

        name.TextChanged += (_, _) => RefreshDraft();
        baseUrl.TextChanged += (_, _) => RefreshDraft();
        customHeaders.TextChanged += (_, _) => RefreshDraft();
        RefreshDraft();
        return stack;
    }

    private async Task CommitAddProviderDraft(
        TextBox name,
        TextBox baseUrl,
        ComboBox wire,
        TextBox apiKey,
        TextBox customHeaders)
    {
        if (_chat is null) return;
        if (!ChatProviderCoordinator.TryParseCustomHeaders(customHeaders.Text, out var parsedHeaders))
        {
            SetProviderStatus(true, HubStrings.Get("ProviderCustomHeadersInvalid"));
            return;
        }

        var protocol = (wire.SelectedItem as ComboBoxItem)?.Tag as string ?? ModelProtocols.Chat;
        var secret = string.IsNullOrWhiteSpace(apiKey.Text) ? null : apiKey.Text;
        var created = _chat.AddProvider(
            name.Text ?? "", baseUrl.Text ?? "", "", secret, out var problem, protocol, parsedHeaders);
        if (created is null)
        {
            if (problem is not null) SetProviderStatus(true, HubStrings.Get(problem));
            return;
        }

        SetProviderStatus(false, HubStrings.Get("ProviderAdded"));
        CloseDraft();
        // A supplied key is stored in the OS credential store by AddProvider and can immediately fetch models.
        // With a blank optional key, finish keyless and leave the existing row authentication action available.
        if (secret is not null)
            await RefreshModelsAsync(created);
    }

    /// <summary>
    /// Whether a provider counts as linked: it has a saved credential <b>whose secret is really there</b>.
    ///
    /// <para>Source-agnostic by design. The account model made a pasted key and a browser sign-in the same
    /// shape, so asking "how did this credential arrive?" here would undo that — and would show "not
    /// connected" for a provider that plainly works.</para>
    ///
    /// <para>The secret rather than the record is what the mark claims. A credential is a row in
    /// <c>credentials.json</c>; the key itself lives in the OS store and is read back at load, and when that read
    /// comes up empty the provider cannot be called. Showing 已鉴权 for that state made the settings page and the
    /// conversation disagree — the row said connected, the send answered "authenticate first".</para>
    /// </summary>
    private bool IsLinked(ModelProvider provider)
        => !provider.NeedsOpenAiRelogin && _chat?.CredentialFor(provider.Id)?.HasSecret == true;

    /// <summary>Opens the edit draft for a provider, replacing its collapsed header. Any model draft goes
    /// away with it: one draft at a time is what makes the page-wide flags readable, and Cancel/Save close
    /// both regardless of which form was pressed.</summary>
    private void OpenEditDraft(ModelProvider provider)
    {
        _editingProviderId = provider.Id;
        _addingProvider = false;
        _modelDraftProviderId = null;
        _addingModel = false;
        _editingModelName = null;
        RebuildProviderGroups();
    }

    /// <summary>
    /// Closes every open draft and applies any deferred rebuild. Called on cancel, save, Escape, and — from
    /// <see cref="Reload"/> — before a language-switch rebuild that must discard the draft.
    ///
    /// <para>A close is only observable through a rebuild: <see cref="BuildProviderGroup"/> swaps the draft
    /// for the collapsed header purely from the flags, so returning without one would leave the form on
    /// screen after cancel or save. The deferred reload rides the same pass.</para>
    /// </summary>
    private void CloseDraft()
    {
        var wasOpen = DraftOpen;
        _editingProviderId = null;
        _addingProvider = false;
        _modelDraftProviderId = null;
        _addingModel = false;
        _editingModelName = null;
        if (wasOpen || _pendingReload)
        {
            _pendingReload = false;
            AddProviderButton.Content = HubStrings.Get("AddProvider");
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Applies the edit draft. Built-in name/base URL are pinned by the manifest and already read-only. The
    /// blank key argument is a deliberate no-op: authentication is owned by the auth dialog, and this form
    /// must never touch the stored credential.
    /// </summary>
    private void CommitEditDraft(ModelProvider provider, TextBox name, TextBox baseUrl)
    {
        if (_chat is null) return;

        // Keep the existing default-model name; this page no longer edits it.
        if (_chat.UpdateProvider(provider.Id, name.Text ?? "", baseUrl.Text ?? "", provider.Model, "", out var problem))
        {
            SetProviderStatus(false, HubStrings.Get("ProviderEditSaved"));
        }
        else
        {
            // Only the gate's own reason is shown: a null problem means the row itself is gone, which is not
            // something to put in a status line about a field.
            if (problem is not null) SetProviderStatus(true, HubStrings.Get(problem));
        }
        CloseDraft();
    }

    private void SetProviderEnabled(string providerId, bool enabled)
    {
        if (_chat?.SetProviderEnabled(providerId, enabled) == true)
        {
            RebuildProviderGroups();
        }
    }


    /// <summary>
    /// The configured model sub-list. Always rendered: list membership is the activation set, and a provider
    /// that is not yet authenticated must still be able to hold local model names. Catalog fetch stays gated
    /// on a credential; manual add does not.
    ///
    /// <para>The header states only the model count. Which model a conversation runs is not this page's job.</para>
    /// </summary>
    private Control BuildModelSection(ModelProvider provider)
    {
        // Tagged so the self-check can find this section by role instead of by position: an index-based walk
        // silently reads the wrong node the moment the layout gains a row, and then reports a failure that
        // has nothing to do with what broke.
        var section = new StackPanel { Spacing = 6, Tag = SectionTags.Models };

        // A long list starts collapsed, but a draft opened in it forces it back open: a form folded into a
        // hidden container is a form nobody can fill or cancel, and while it is "open" the deferred rebuild
        // never runs either.
        var expanded = provider.Models.Count <= 10 || _modelDraftProviderId == provider.Id;
        var listContent = new StackPanel { Spacing = 6, IsVisible = expanded, Tag = SectionTags.ModelList };
        var toggleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var disclosure = new Avalonia.Controls.Shapes.Path
        {
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M6 9L12 15L18 9"),
            Stroke = BrushOrNull("Hub.TextSecondary"),
            StrokeThickness = 1.8,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(expanded ? 180 : 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        disclosure.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        toggleContent.Children.Add(disclosure);

        var toggleText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        toggleText.Children.Add(new TextBlock
        {
            Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ModelsCount"),
                provider.Models.Count),
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
        });
        toggleContent.Children.Add(toggleText);

        // The dedicated collapse-header ControlTheme, not Fluent's default: Fluent paints a :checked
        // ToggleButton with a full accent slab from inside its own template, which no style setter or local
        // Background value could remove (observed on the screenshot). Replacing the theme removes the slab
        // at the source and keeps the header a quiet text row with a hover surface. TryFindResource, not the
        // Resources indexer: the indexer does not walk merged dictionaries, so the key lives in the
        // HubControls include and the indexer returns null — a silent fall-back to the accent slab.
        object? collapseHeaderThemeRaw = null;
        Application.Current?.TryFindResource("HubCollapseHeader", null, out collapseHeaderThemeRaw);
        var collapseHeaderTheme = collapseHeaderThemeRaw as ControlTheme;
        var toggle = new ToggleButton
        {
            Content = toggleContent,
            Theme = collapseHeaderTheme as ControlTheme,
            IsChecked = expanded,
            Padding = new Thickness(4, 2),
            Margin = new Thickness(-4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = SectionTags.ToggleModelList,
        };
        ToolTip.SetTip(toggle, HubStrings.Get("ToggleModelList"));
        toggle.IsCheckedChanged += (_, _) =>
        {
            expanded = toggle.IsChecked == true;
            listContent.IsVisible = expanded;
            disclosure.RenderTransform = new RotateTransform(expanded ? 180 : 0);
        };

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(toggle);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var catalog = new Button
        {
            Content = BuildSearchIcon(),
            Classes = { "quiet" },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = SectionTags.ModelCatalog,
        };
        ToolTip.SetTip(catalog, HubStrings.Get("OpenModelCatalog"));
        catalog.Click += async (_, _) => await OpenModelCatalogAsync(provider);
        actions.Children.Add(catalog);

        var add = new Button
        {
            Content = BuildAddIcon(),
            Classes = { "quiet" },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = SectionTags.AddModelButton,
        };
        ToolTip.SetTip(add, HubStrings.Get("AddModel"));
        add.Click += async (_, _) => await AddModelAsync(provider);
        actions.Children.Add(add);

        // Rightmost: the one action that empties the list. Disabled rather than hidden when there is
        // nothing to remove, so the toolbar keeps a stable shape across empty and populated states.
        var removeAll = new Button
        {
            Content = BuildClearListIcon(),
            Classes = { "quiet" },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = provider.Models.Count > 0,
            Tag = SectionTags.RemoveAllModels,
        };
        ToolTip.SetTip(removeAll, HubStrings.Get("ModelsRemoveAll"));
        removeAll.Click += async (_, _) => await RemoveAllModelsAsync(provider);
        actions.Children.Add(removeAll);

        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        section.Children.Add(heading);

        // The model draft belongs only to its provider — without the id gate every group would render a copy.
        // Add drafts lead the list; an edit draft replaces its model row in place. _modelDraftProviderId answers
        // "whose model draft is this"; it is not _editingProviderId, which owns the provider edit form.
        var modelDraftOpen = _modelDraftProviderId == provider.Id
            && (_addingModel || _editingModelName is not null);
        if (provider.Models.Count == 0 && !modelDraftOpen)
        {
            // Empty state is one quiet line. The toolbar already carries ＋ 手动添加 and 🔍 浏览目录 —
            // a second 「添加模型」 or an auth CTA here would be the same actions twice. Unauthenticated
            // providers say what to do; the header 鉴权 button is the only entry, and a successful auth
            // fetches the catalog by itself.
            var empty = new StackPanel { Spacing = 8, Tag = SectionTags.ModelsEmpty };
            empty.Children.Add(new TextBlock
            {
                Text = IsLinked(provider) || !provider.RequiresCredential
                    ? HubStrings.Get("NoEnabledModels")
                    : HubStrings.Get("ModelsAuthHint"),
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            listContent.Children.Add(empty);
            section.Children.Add(listContent);
            return section;
        }

        var editingModel = modelDraftOpen && _editingModelName is not null;
        var editDraftInserted = false;
        if (modelDraftOpen && !editingModel)
        {
            listContent.Children.Add(BuildModelDraftRow(provider));
        }

        foreach (var model in provider.Models)
        {
            if (editingModel && string.Equals(model.Name, _editingModelName, StringComparison.Ordinal))
            {
                listContent.Children.Add(BuildModelDraftRow(provider));
                editDraftInserted = true;
                continue;
            }

            listContent.Children.Add(BuildModelRow(provider, model));
        }

        // Keep a stale edit draft reachable if the model disappeared during a refresh; normally edits replace
        // exactly the matching model row above.
        if (editingModel && !editDraftInserted)
        {
            listContent.Children.Insert(0, BuildModelDraftRow(provider));
        }

        section.Children.Add(listContent);
        return section;
    }

    /// <summary>The add/edit-model draft row. Adds appear at the start of the list; edits replace the selected
    /// model row in place. In edit mode the name is pre-filled and read-only, and a second field carries the
    /// context-window token count.</summary>
    private Control BuildModelDraftRow(ModelProvider provider)
    {
        var stack = new StackPanel { Spacing = 4, Tag = SectionTags.ModelDraft };

        var editing = _editingModelName is not null;
        var name = editing ? _editingModelName! : "";

        var input = new TextBox
        {
            Text = name,
            IsReadOnly = editing,
            PlaceholderText = HubStrings.Get("ModelNamePlaceholder"),
        };
        if (editing) input.Classes.Add("muted");
        // The input joins the tree only through the inline row below — adding it to the stack here as well
        // gave it a second parent and threw "already has a visual parent" mid-rebuild, which is what froze
        // the settings card on the first pencil press.

        // The token field is built here but appended after the row, so the stack reads name-row first and
        // the context-window field second (the docstring's "second field").
        Control? tokensField = null;
        TextBox? tokens = null;
        if (editing)
        {
            var (current, _, _) = _chat!.ContextWindowFor(provider.Id, name);
            tokens = new TextBox
            {
                Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            tokensField = BuildField(HubStrings.Get("ModelContextEditHint"), tokens);
        }

        // The three controls share one row edge, so they need one height: the buttons stretch to the row the
        // input sets rather than carrying a height of their own. An explicit button height (34 was tried) plus
        // the input's own padding left 3px of input above and below the buttons — centred, and still visibly
        // misaligned, because the eye compares the two boxes' edges, not their middle.
        // A Grid rather than a horizontal StackPanel: the panel's cross-axis sizing would have to be trusted to
        // hand the buttons the row's full height, while a Grid cell gives it unconditionally.
        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var cancel = new Button
        {
            Content = HubStrings.Get("Cancel"),
            Classes = { "quiet" },
            MinWidth = 72,
            Padding = new Thickness(12, 0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        cancel.Click += (_, _) => CloseDraft();
        var confirm = new Button
        {
            Content = HubStrings.Get(editing ? "Save" : "Add"),
            Classes = { "primary" },
            IsDefault = true,
            MinWidth = 72,
            Padding = new Thickness(12, 0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        confirm.Click += (_, _) =>
        {
            if (editing) CommitModelEditDraft(provider, name, tokens!);
            else CommitModelDraft(provider, input);
        };
        // Both buttons land in column 0 unless told otherwise: a Grid stacks overlapping children there, so the
        // later-added "保存" painted straight over "取消" and the row looked as if the cancel had been deleted.
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        Grid.SetColumn(cancel, 0);
        Grid.SetColumn(confirm, 1);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        // The TextBox theme carries a 6px top margin — the gap a field wants under the label above it. This
        // row has no label above it, so the margin only pushed the input's box 3px below the buttons' centre.
        input.Margin = new Thickness(0);
        buttons.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(input, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(input);
        row.Children.Add(buttons);

        stack.Children.Add(row);
        if (tokensField is not null) stack.Children.Add(tokensField);
        return stack;
    }

    private void CommitModelDraft(ModelProvider provider, TextBox input)
    {
        if (_chat is null) return;

        var name = input.Text ?? "";
        if (name.Trim().Length == 0)
        {
            SetProviderStatus(true, HubStrings.Get("ModelNameRequired"));
            return;
        }

        if (_chat.AddModel(provider.Id, name) is null)
        {
            SetProviderStatus(true, HubStrings.Get("ModelDuplicated"));
            return;
        }

        SetProviderStatus(false, HubStrings.Get("ModelAdded"));
        CloseDraft();
    }

    /// <summary>Opens the context-window draft for a model (the pencil on a model row). It writes the
    /// <b>model</b>-draft id only — the group's header stays collapsed.</summary>
    private void OpenModelEditDraft(ModelProvider provider, ProviderModel model)
    {
        _modelDraftProviderId = provider.Id;
        _editingModelName = model.Name;
        _addingModel = false;
        RebuildProviderGroups();
    }

    private void CommitModelEditDraft(ModelProvider provider, string name, TextBox tokens)
    {
        if (_chat is null) return;

        int? parsed = null;
        if (int.TryParse((tokens.Text ?? "").Trim(), out var value) && value > 0)
        {
            parsed = value;
        }

        _chat.SetModelContextTokens(provider.Id, name, parsed);
        SetProviderStatus(false, HubStrings.Get("ProviderEditSaved"));
        CloseDraft();
    }

    /// <summary>
    /// Whether the provider's model catalog may be fetched.
    ///
    /// <para>True for a provider that needs no credential or one that holds a credential. False for a provider
    /// that needs one and does not have it — the request would come back 401.</para>
    ///
    /// <para>Lives here rather than in <see cref="ChatWorkspace"/> because it is a <b>presentation</b>
    /// decision: the fetch itself is legal without a key (the endpoint decides), and a future caller that
    /// wants to try anyway should not have to fight a UI-layer guard.</para>
    /// </summary>
    private bool CanListModels(ModelProvider provider)
        => !provider.RequiresCredential || IsLinked(provider);

    /// <summary>
    /// The window this model is treated as having, stated once as a number. Where that number came from lives in
    /// the tooltip, not in a suffix: the same source tag already rides the chat's usage line, so repeating it
    /// here would say one thing twice on a page that is only explaining a capability.
    ///
    /// <para>There is deliberately <b>no</b> box on this row. The one it replaces parsed on <c>TextChanged</c>
    /// and wrote through <see cref="ChatWorkspace.SetModelContextTokens"/>, whose <c>Changed</c> event rebuilds
    /// every provider group — so the first keystroke tore down the very text box being typed in. A number that
    /// is wrong is corrected in <c>ai/providers.json</c>, which is what the tooltip now names; the model
    /// property and its precedence in <see cref="ContextBudget"/> are untouched, so that file remains a real
    /// path rather than a documented-looking one.</para>
    /// </summary>
    private Control BuildModelContextRow(ModelProvider provider, ProviderModel model)
    {
        var (tokens, _, source) = _chat!.ContextWindowFor(provider.Id, model.Name);
        var line = new TextBlock
        {
            Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center,
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ModelContextFormat"), tokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)),
            Tag = SectionTags.ModelContext,
        };
        ToolTip.SetTip(line, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ModelContextSourceHint"), HubStrings.Get(ContextBudget.SourceLabelKey(source))));
        return line;
    }

    private Control BuildModelRow(ModelProvider provider, ProviderModel model)
    {
        // List membership is the activation set: every row here is selectable in a conversation. No enable
        // toggle and no "in use" marker — those answered a question this page does not ask.
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = model.Name,
            VerticalAlignment = VerticalAlignment.Center,
        });
        text.Children.Add(BuildModelContextRow(provider, model));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "reveal" },
        };

        // The pencil opens a context-window draft for this model. The one field is MaxContextTokens — the
        // zero-cost override the model already persists — so "edit model" is real without growing the model's
        // schema. Display name / wire override / output cap stay out until a later pass.
        var edit = new Button
        {
            Content = BuildEditIcon(),
            Classes = { "quiet" },
            Padding = new Thickness(6, 2),
        };
        ToolTip.SetTip(edit, HubStrings.Get("ModelEditTitle"));
        edit.Click += (_, _) => OpenModelEditDraft(provider, model);
        actions.Children.Add(edit);

        var remove = new Button
        {
            Content = BuildRemoveIcon(),
            Classes = { "quiet" },
            Padding = new Thickness(6, 2),
        };
        ToolTip.SetTip(remove, HubStrings.Get("ModelRemove"));
        remove.Click += async (_, _) => await RemoveModelAsync(provider, model);
        actions.Children.Add(remove);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(text);
        grid.Children.Add(actions);

        // Outlined card so the model list reads as a list; the fill is Hub.Surface (bound, not a captured
        // null brush). Hover only lifts the fill.
        var row = new Border
        {
            Padding = new Thickness(12, 8),
            Tag = SectionTags.ModelRow,
            Classes = { "model-row", "provider-row" },
            Child = grid,
        };
        return row;
    }

    private Control BuildAffiliateSection(ModelProvider provider)
    {
        var stack = new StackPanel { Spacing = 4 };
        var disclosure = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("AffiliateDisclosure"),
            provider.Name);
        _affiliateTextForCheck = disclosure;
        stack.Children.Add(new TextBlock
        {
            Text = disclosure,
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var referral = new Button
        {
            Content = HubStrings.Get("ViewReferral"),
            Classes = { "quiet" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3),
            FontSize = 11,
        };
        referral.Click += (_, _) => OpenReferral(provider);
        stack.Children.Add(referral);

        var box = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Tag = SectionTags.Affiliate,
            Child = stack,
        };
        // Same reason as the model rows: a static BrushOrNull can capture null before attach.
        box.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.Surface"));
        return box;
    }

    /// <summary>A small rounded label ("内置", "使用中"), used instead of colour alone to carry meaning.</summary>
    private static Control Pill(string text, IBrush? brush, IBrush? foreground = null)
        => new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = foreground,
            },
        };

    /// <summary>
    /// The remove affordance, drawn rather than typed: a trash outline on a 12×12 canvas. Generated instead of
    /// pulling in an icon font, so the button has no asset dependency and scales cleanly with the theme
    /// foreground.
    /// </summary>
    /// <summary>
    /// The remove affordance, drawn rather than typed: a trash outline on a 12×12 canvas. Generated instead of
    /// pulling in an icon font, so the button has no asset dependency.
    ///
    /// <para>The stroke is taken from the same theme brush the muted text uses: Avalonia's <c>Shape.Stroke</c>
    /// does <b>not</b> inherit <c>Foreground</c> (unlike a <c>Path</c> inside a control template), so leaving
    /// it unset renders nothing at all — a silent blank button, which is exactly the kind of failure this
    /// project's self-checks exist to catch.</para>
    /// </summary>
    private Control BuildRemoveIcon()
    {
        // A trash outline drawn as a single Path geometry. Generated instead of pulling in an icon font or an
        // asset, so the button has no external dependency.
        //
        // The stroke is bound with DynamicResource rather than read once through BrushOrNull: these rows are
        // built inside RebuildProviderGroups, which Reload calls while the page may not yet be attached to a
        // resource host — a one-off lookup returns null there and the icon renders as an invisible shape
        // inside a visible button. A binding re-resolves once the control is attached, and again on a theme
        // switch, which is what the rest of the page relies on.
        var path = new Avalonia.Controls.Shapes.Path
        {
            // Lid, handle, body, two slots.
            Data = Avalonia.Media.Geometry.Parse(
                "M1.5,2.5 H10.5 M4.5,1.2 H7.5 M2.5,2.5 H9.5 V11 H2.5 Z M5,4.8 V8.8 M7,4.8 V8.8"),
            StrokeThickness = 1.2,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));

        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    /// <summary>
    /// The edit affordance: a pencil outline. Same construction as the other icons — generated geometry, stroke
    /// bound through DynamicResource so it re-resolves on theme/language switch rather than going blank.
    /// </summary>
    private Control BuildEditIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(
                "M10.5,2.2 L11.8,3.5 L4.6,10.7 L2.9,11.1 L3.3,9.4 Z M9.4,3.3 L10.7,4.6"),
            StrokeThickness = 1.2,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    private Control BuildSearchIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(
                "M8,4.75 C8,6.55 6.55,8 4.75,8 C2.95,8 1.5,6.55 1.5,4.75 C1.5,2.95 2.95,1.5 4.75,1.5 C6.55,1.5 8,2.95 8,4.75 M7.25,7.25 L11,11"),
            StrokeThickness = 1.5,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 14, Height = 14, Child = path };
    }

    private Control BuildAddIcon()
    {
        var icon = HubActionIcon.Create("Hub.Icon.Plus");
        icon.Width = 14;
        icon.Height = 14;
        icon.StrokeThickness = 1.7;
        return icon;
    }

    /// <summary>
    /// The remove-all icon: a list (three short strokes) crossed by a small ×. A plain trash would read as
    /// "remove this row" — the same glyph the per-row button already uses — so the list-plus-cross shape
    /// carries the "the whole list" meaning. Same construction rules as the other icons: generated geometry,
    /// stroke bound through DynamicResource.
    /// </summary>
    private Control BuildClearListIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(
                "M1.5,2.5 H6.5 M1.5,6 H6.5 M1.5,9.5 H6.5 M8,4 L11.5,7.5 M11.5,4 L8,7.5"),
            StrokeThickness = 1.4,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 14, Height = 14, Child = path };
    }

    private static Border BuildProviderHeaderDivider()
    {
        var divider = new Border
        {
            Height = 1,
            Tag = SectionTags.ProviderHeaderDivider,
        };
        divider.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.BorderSubtle"));
        return divider;
    }

    /// <summary>
    /// The linked state mark: a tick in the success green. Same construction rules as the remove icon — a
    /// <c>Path</c> whose stroke is bound, because an unset <c>Shape.Stroke</c> draws nothing and the failure is
    /// silent.
    /// </summary>
    private Control BuildCheckIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M1.5,6.5 L4.6,9.6 L11,2.6"),
            StrokeThickness = 1.8,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.Success"));
        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    /// <summary>
    /// The not-linked mark: a broken link (a gap between two short strokes) in the tertiary ink.
    ///
    /// <para>Deliberately a different <i>shape</i>, not just a different colour: a grey tick would read as
    /// "disabled but connected", which is the opposite of what the state says.</para>
    /// </summary>
    private Control BuildUnlinkedIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M1.5,6 H4.6 M7.4,6 H10.5 M6.1,4.4 L5.9,7.6"),
            StrokeThickness = 1.6,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextTertiary"));
        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    /// <summary>
    /// Looks a theme brush up by key, returning <c>null</c> when it is absent.
    ///
    /// <c>FindResource</c> returns <c>Avalonia.UnsetValueType</c> for a missing key, and a direct cast to
    /// <see cref="IBrush"/> then throws <c>InvalidCastException</c> at runtime — with no compiler warning,
    /// because the cast is legal for the declared type. A missing key would otherwise take down the whole
    /// settings page the first time a row was drawn.
    /// </summary>
    private IBrush? BrushOrNull(string key)
        => this.TryFindResource(key, out var value) ? value as IBrush : null;

    private void SetModelEnabled(string providerId, string modelName, bool enabled)
    {
        if (_chat?.SetModelEnabled(providerId, modelName, enabled) == true)
        {
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Fetches the provider's model list and repaints.
    ///
    /// <para>A fetch that fails reports the reason and <b>leaves the existing list alone</b>, so the section
    /// does not collapse to empty on a network blip. A fetch that succeeds with zero models says so
    /// explicitly rather than letting the empty state speak for itself.</para>
    /// </summary>
    private async Task<ModelFetchResult> RefreshModelsAsync(ModelProvider provider)
    {
        if (_chat is null) return ModelFetchResult.Unreachable("The chat workspace is unavailable.");

        SetProviderStatus(false, HubStrings.Get("ModelsRefreshing"));

        try
        {
            var result = await _chat.RefreshModelsAsync(provider.Id);
            if (!result.Reachable)
            {
                SetProviderStatus(true, HubStrings.Get("ModelsFetchFailed") + " " + result.Problem);
                return result;
            }

            SetProviderStatus(false, result.Models.Count == 0
                ? HubStrings.Get("ModelsFetchedEmpty")
                : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("ModelsFetched"),
                    result.Models.Count));
            return result;
        }
        finally
        {
            RebuildProviderGroups();
        }
    }

    private async Task OpenModelCatalogAsync(ModelProvider provider)
    {
        if (_chat is null) return;
        await CreateModelCatalogWindow(provider).ShowAsync(Owner());
        RebuildProviderGroups();
    }

    private ModelCatalogWindow CreateModelCatalogWindow(ModelProvider provider)
        => ModelCatalogWindow.Create(
            provider,
            () => _chat.CachedModels(provider.Id),
            name => provider.Models.Any(model =>
                string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase) && model.Enabled),
            name => _chat.EnableCatalogModel(provider.Id, name),
            () => RefreshModelsAsync(provider),
            CanListModels(provider));

    private async Task AddModelAsync(ModelProvider provider)
    {
        if (_chat is null) return;

        // The draft row replaces the old PromptWindow modal: it stays where the list is, and it is always the
        // first row so the input never scrolls out from under the person who just clicked "add".
        _modelDraftProviderId = provider.Id;
        _addingModel = true;
        RebuildProviderGroups();
        await Task.CompletedTask;
    }

    private async Task RemoveModelAsync(ModelProvider provider, ProviderModel model)
    {
        if (_chat is null) return;

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("ModelRemove"),
                model.Name + "\n\n" + HubStrings.Get("ModelRemoveConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        if (_chat.RemoveModel(provider.Id, model.Name))
        {
            SetProviderStatus(false, HubStrings.Get("ModelRemoved"));
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Empties one provider's configured-model list. Same confirm-first shape as the single-row removal:
    /// the click is one icon away from "add", and a misclick that costs the whole list is not recoverable
    /// by one more click.
    /// </summary>
    private async Task RemoveAllModelsAsync(ModelProvider provider)
    {
        if (_chat is null || provider.Models.Count == 0) return;

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("ModelsRemoveAll"),
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("ModelsRemoveAllConfirm"),
                    provider.Models.Count),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        var removed = _chat.RemoveAllModels(provider.Id);
        if (removed > 0)
        {
            SetProviderStatus(false, string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ModelsRemovedAll"),
                removed));
            RebuildProviderGroups();
        }
    }

    private async Task SignInAsync(ModelProvider provider, Button? button)
    {
        if (_chat is null || !provider.SupportsOAuth || OpenAiLoginLayer.IsVisible) return;

        SetProviderStatus(false, HubStrings.Get("AuthOAuthPending"));
        if (button is not null) button.IsEnabled = false;

        // A device-code sign-in needs a surface for the code and a way to be cancelled; the loopback flow needs
        // neither, because its redirect comes back to this machine by itself. The window is what the person
        // cancels *by* — closing it stops the polling, so there is no Cancel button repeating that.
        CancellationTokenSource? deviceCancellation = null;
        DeviceAuthWindow? device = null;
        IProgress<AxmolHub.Agent.DeviceCodeChallenge>? deviceProgress = null;
        if (provider.OAuth?.EffectiveFlow == ProviderOAuthFlows.DeviceCode)
        {
            deviceCancellation = new CancellationTokenSource();
            // Progress<T> posts to the synchronization context it was created on — the UI thread here. The flow
            // reports the challenge from a worker, and a window must not be built or shown from one.
            deviceProgress = new Progress<AxmolHub.Agent.DeviceCodeChallenge>(challenge =>
            {
                device = new DeviceAuthWindow(challenge);
                DeviceWindowForCheck = device;
                // Closing the window IS the cancel, which is why the subscription lives here and not inside the
                // window: the flow is already awaiting by the time a window exists, so only the caller holds a
                // token it can hand to it. Without this line the polling would keep running against a window the
                // person already threw away, and the sign-in would land after they said no.
                device.Closed += (_, _) =>
                {
                    // A close that arrives after the run has already finished lands on a source the finally block
                    // disposed. That is not a failure to report — the sign-in being over is exactly what the close
                    // wanted — so the no-op is written out instead of being left to throw inside an event handler,
                    // where there is nobody to see it.
                    try { deviceCancellation?.Cancel(); }
                    catch (ObjectDisposedException) { }
                };
                // Same tolerance AuthDialog shows: a design-time host has no top-level window to own this one,
                // and the window still opens standalone rather than swallowing the sign-in. A check host keeps it
                // off screen for the reason every detached window in this app exists: the assertions read the
                // object graph, and a second live top-level window would be measuring the harness instead.
                if (!_deviceWindowStaysOffScreen) device.Show(Owner()!);
            });
        }

        using var openAiCancellation = new CancellationTokenSource();
        void Detached(object? sender, VisualTreeAttachmentEventArgs args) => openAiCancellation.Cancel();
        if (provider.OAuth?.EffectiveFlow == ProviderOAuthFlows.OpenAi) DetachedFromVisualTree += Detached;
        try
        {
            var result = await _chat.SignInWithOAuthAsync(provider.Id, url =>
            {
                // The manual URL has nowhere to live in a group (it would have to be rebuilt into the row that
                // is about to be rebuilt anyway), so it goes to the status line, which is already the channel
                // for every other outcome of this flow.
                SetProviderStatus(false, HubStrings.Get("AuthOAuthNoBrowser") + "\n" + url);
            }, deviceCancellation?.Token ?? openAiCancellation.Token, deviceProgress, attempt =>
            {
                OpenAiLoginHost.Content = new OpenAiLoginCard(attempt, () => openAiCancellation.Cancel());
                OpenAiLoginLayer.IsVisible = true;
            });

            OpenAiLoginLayer.IsVisible = false;
            OpenAiLoginHost.Content = null;

            if (result is null)
            {
                SetProviderStatus(true, HubStrings.Get("AuthOAuthPlatformUnsupported"));
                return;
            }

            if (result.Cancelled)
            {
                SetProviderStatus(false, HubStrings.Get("AuthOAuthCancelled"));
                return;
            }

            if (result.ScopeRejected is { } granted)
            {
                SetProviderStatus(true, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("AuthOAuthScopeRejected"),
                    granted));
                return;
            }

            if (result.Error is { Length: > 0 } error)
            {
                SetProviderStatus(true, HubStrings.Get("AuthOAuthFailed") + error);
                return;
            }

            if (provider.IsOpenAiPlan && result.Credential?.Secret is { } planSecret)
            {
                if (!OpenAiTokens.Decode(planSecret).HasPlanUsage)
                {
                    SetProviderStatus(true, HubStrings.Get("OpenAiPlanNotGranted"));
                    RebuildProviderGroups();
                    return;
                }
                if (!provider.OpenAiPlanWelcomeAcknowledged && Owner() is { } welcomeOwner)
                {
                    await HubDialog.ShowAsync(welcomeOwner, HubStrings.Get("OpenAiPlanWelcomeTitle"),
                        HubStrings.Get("OpenAiPlanWelcome"), HubDialogButtons.Ok);
                    provider.OpenAiPlanWelcomeAcknowledged = true;
                    _chat.SaveProviders();
                }
            }
            SetProviderStatus(false, HubStrings.Get("AuthOAuthSucceeded"));

            _chat?.SetPreferredAuthMethod(provider.Id, ProviderAuthMethods.OAuth);

            // The credential is stored the moment the flow lands, so the linked mark repaints now — the
            // fetch below can take seconds and must not hold the state badge hostage.
            RebuildProviderGroups();

            // Same reasoning as the pasted-key path: a sign-in that leaves an empty model list is only half
            // finished, and the list is reachable now precisely because the browser just minted the key.
            await RefreshModelsAsync(provider);
        }
        finally
        {
            DetachedFromVisualTree -= Detached;
            OpenAiLoginLayer.IsVisible = false;
            OpenAiLoginHost.Content = null;
            if (button is not null) button.IsEnabled = true;
            // The outcome belongs to the settings status line — the same channel every other sign-in result
            // uses — so the code window has nothing left to say and goes away. IsVisible rather than a
            // try/catch: when the person closed it themselves (which is how they cancel), closing it again is
            // an error for a state that is already the one we wanted. An off-screen check window was never
            // opened, so there is nothing to close — and Window.Close on a window with no platform side behind
            // it is not a thing to discover at assertion time.
            if (!_deviceWindowStaysOffScreen && device is { IsVisible: true }) device.Close();
            deviceCancellation?.Dispose();
        }
    }

    /// <summary>
    /// The "Add provider" button. It opens the **preset picker** (a searchable list of known providers), not the
    /// empty form — the form is reachable only through the picker's "Custom endpoint…" row. That ordering is the
    /// whole point: a user who has never configured a provider gets a list they can read through, and the user
    /// with a private gateway still gets their form, one extra click in.
    /// </summary>
    private async Task AddProviderAsync()
    {
        var picker = await ProviderPickerWindow.ShowAsync(Owner(), _chat.AvailablePresets());
        if (picker is null) return;

        if (picker.WantsCustom)
        {
            await AddCustomProviderAsync();
            return;
        }

        if (picker.SelectedId is not { } presetId || _chat.AddPreset(presetId) is null)
        {
            SetProviderStatus(true, HubStrings.Get("ProviderAlreadyAdded"));
            return;
        }

        SetProviderStatus(false, "");
        RebuildProviderGroups();

        // Adding a provider and connecting it are one job. Dual-method presets choose the method inside the
        // auth dialog; a keyless preset has nothing to do. On success the catalog is fetched and the
        // manifest defaults are enabled — otherwise the card lands empty and the user has to remember a
        // second step.
        var provider = _chat.Providers.FirstOrDefault(candidate => candidate.Id == presetId);
        if (provider is { CanAuthenticate: true })
        {
            await AuthenticateProviderAsync(provider);
        }
    }

    /// <summary>The free-form path, shared by the picker's custom row. Instead of a modal dialog it inserts an
    /// "add custom endpoint" draft at the top of the provider list; the draft is always first, so the person
    /// never has to scroll to find the form they just opened.</summary>
    private Task AddCustomProviderAsync()
    {
        OpenAddDraft();
        return Task.CompletedTask;
    }

    /// <summary>Puts the page into "adding a custom endpoint": no edit draft, no model draft, one add draft.</summary>
    private void OpenAddDraft()
    {
        _addingProvider = true;
        _editingProviderId = null;
        _modelDraftProviderId = null;
        _addingModel = false;
        RebuildProviderGroups();
    }

    private async Task RemoveProviderAsync(ModelProvider provider)
    {
        // The header only offers this for a provider that may be removed, so this is a guard rather than the
        // rule; the rule itself lives in ChatWorkspace so the two cannot drift.
        if (!ChatWorkspace.CanRemoveProvider(provider)) return;
        if (_chat.HasPlanCredentials(provider.Id))
        {
            SetProviderStatus(true, HubStrings.Get("OpenAiPlanSignOutFirst"));
            return;
        }

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("RemoveProvider"),
                provider.Name + "\n\n" + HubStrings.Get("RemoveProviderConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        if (_chat.RemoveProvider(provider.Id))
        {
            SetProviderStatus(false, HubStrings.Get("ProviderRemoved"));
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Cuts a provider's link: its accounts and their secrets go, the provider stays in the list. Confirmed
    /// first — it destroys secrets that cannot be recovered, and the provider looking unchanged afterwards
    /// would otherwise make the action easy to trigger by accident and hard to notice.
    /// </summary>
    private async Task DisconnectProviderAsync(ModelProvider provider)
    {
        if (_chat is null) return;
        if (!provider.IsOpenAiOAuth && _chat.HasPlanCredentials(provider.Id))
        {
            SetProviderStatus(true, HubStrings.Get("OpenAiPlanSignOutFirst"));
            return;
        }

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("DisconnectProvider"),
                provider.Name + "\n\n" + HubStrings.Get(provider.IsOpenAiPlan ? "OpenAiPlanDisconnectConfirm" : "DisconnectProviderConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        if (provider.IsOpenAiPlan || (provider.IsOpenAiOAuth && provider.Credential?.HasSecret != true))
        {
            try
            {
                var (removed, revoked) = await _chat.DisconnectOpenAiPlanAsync();
                SetProviderStatus(removed && !revoked, HubStrings.Get(!removed ? "ProviderNothingToDisconnect"
                    : revoked ? "ProviderDisconnected" : "OpenAiPlanRevokeUnconfirmed"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                SetProviderStatus(true, ex.Message);
            }
        }
        else
            SetProviderStatus(false, _chat.DisconnectProvider(provider.Id)
                ? HubStrings.Get("ProviderDisconnected")
                : HubStrings.Get("ProviderNothingToDisconnect"));
        RebuildProviderGroups();
    }

    /// <summary>
    /// Opens the authentication dialog and applies whatever it produces.
    ///
    /// <para>A provider that offers both routes always opens on the choice, with the route it is already bound
    /// to marked. That is the dialog's own decision (see <see cref="AuthDialog"/>): handing it the bound method
    /// as a step once made every already-configured provider open a key box, even the ones signed in through
    /// the browser, and put the choice one Back click away.</para>
    ///
    /// <para>The two routes end differently on purpose. A pasted key is stored here and now. Browser sign-in
    /// closes this dialog immediately and hands off to <see cref="SignInAsync"/>, which owns the browser, the
    /// callback and the failure messages — the dialog's part was only to ask which route, and pretending
    /// otherwise would put two owners on one flow.</para>
    /// </summary>
    private async Task AuthenticateProviderAsync(ModelProvider provider)
    {
        if (_chat is null) return;
        if (Owner() is not { } owner) return;

        if (!_chat.CanStoreSecrets)
        {
            SetProviderStatus(true, HubStrings.Get("AuthSecretsUnsupported"));
            return;
        }

        var outcome = await AuthDialog.ShowAsync(
            owner,
            provider,
            preferredMethod: BoundAuthMethod(provider),
            checkKey: key => _chat.CheckApiKeyAsync(provider.Id, key));

        // Null is the user closing the dialog without choosing. Anything else is one of the two routes, and
        // the dialog says which — so there is no guessing from "was a key returned".
        if (outcome is null)
        {
            SetProviderStatus(false, HubStrings.Get("AuthDialogCancelled"));
            return;
        }

        if (outcome.Route == AuthRoute.Login)
        {
            // Hand off to the flow that already owns the browser and the callback. It reports on the status
            // line and rebuilds the groups itself when it lands.
            await SignInAsync(provider, null);
            return;
        }

        ProviderCredential? stored;
        try { stored = _chat.AddCredential(provider.Id, "", outcome.ApiKey, CredentialSources.ApiKey); }
        catch (SecretStoreException exception)
        {
            SetProviderStatus(true, exception.Message);
            return;
        }
        if (stored is null)
        {
            SetProviderStatus(true, HubStrings.Get("AuthSecretsUnsupported"));
            return;
        }

        _chat.SetPreferredAuthMethod(provider.Id, ProviderAuthMethods.ApiKey);

        // The key is already stored, so "linked" is true now: repaint the mark before the fetch rather than
        // after it — the list can take seconds to arrive and the state must not wait on it.
        RebuildProviderGroups();

        // The model list needs the key, and the user has just handed one over — so it is fetched here rather
        // than left as the next thing to remember. It is the same call the refresh button makes; doing it
        // automatically is what turns "authenticated" into "ready to use" without a second visit.
        await RefreshModelsAsync(provider);
    }

    /// <summary>
    /// The route this provider is already bound to, or null when nothing has been chosen yet.
    ///
    /// <para>It is passed to the dialog as a mark only: a provider offering two routes still opens on the choice,
    /// so the remembered route is a default rather than a decision taken for the user. Which step to show is
    /// derived from the provider's own route list, and a provider with a single key route lands on the key box
    /// without this method having to say so.</para>
    /// </summary>
    private static string? BoundAuthMethod(ModelProvider provider)
    {
        if (provider.Credential?.Source == CredentialSources.OAuth) return ProviderAuthMethods.OAuth;
        if (provider.Credential?.Source == CredentialSources.ApiKey) return ProviderAuthMethods.ApiKey;
        return provider.PreferredAuthMethod is { Length: > 0 } preferred ? preferred : null;
    }

    /// <summary>The top-level window, used as the dialog owner. <c>null</c> in a design-time host, in which
    /// case the dialog still opens (it degrades to a standalone window).</summary>
    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;

    /// <summary>
    /// Opens the affiliate/referral page. This is the same entrance the sign-in flow uses
    /// (<see cref="UrlLauncher"/>), because the disclosure link and the OAuth link are one job — telling the user
    /// where their key came from — and two launchers means one of them silently stops working on a platform the
    /// other added. A <c>directory</c> is deliberately not routed here: shell-opening a folder has to skip
    /// <c>$BROWSER</c>, and <c>MainWindow.OpenFolder</c> keeps its own path for that reason.
    /// </summary>
    private void OpenReferral(ModelProvider provider)
    {
        if (provider.ReferralUrl is not { Length: > 0 } url) return;
        if (UrlLauncher.TryOpen(url)) return;

        // No browser: report rather than throw, and hand over the link itself, which is what the sign-in
        // fallback does too. On a headless box the URL is still actionable — copy it and open it elsewhere.
        SetProviderStatus(true, HubStrings.Get("AuthNoBrowserLink") + url);
    }

    /// <summary>Number of built-in presets the "add provider" dialog would offer for this workspace — the
    /// catalog minus whatever is already configured.</summary>
    internal int AvailablePresetCount => _chat.AvailablePresets().Count;

    /// <summary>Whether the provider status line is on screen. It is hidden while empty, so a failed operation
    /// that wrote nothing would otherwise leave the page looking untouched.</summary>
    internal bool StatusLineVisible => ProviderStatusLine.IsVisible;

    private bool _deviceWindowStaysOffScreen;

    /// <summary>
    /// Role markers attached to the nodes of a provider group with <c>Control.Tag</c>.
    ///
    /// <para>A group is a deep composite of StackPanels, Grids and Borders whose shape depends on the
    /// provider (a key-only provider has no sign-in row, a keyless one has no auth section at all). A self-check
    /// that walks it by position therefore reads the wrong node as soon as the layout moves — and reports a
    /// failure unrelated to whatever broke. Tagging each part with what it <i>is</i> keeps the walk honest and
    /// lets the assertions name a part instead of describing where it sits.</para>
    /// </summary>
    private static class SectionTags
    {
        internal const string Group = "provider-group";
        internal const string Summary = "provider-summary";
        internal const string Models = "provider-models";
        internal const string ModelList = "provider-model-list";
        internal const string ToggleModelList = "provider-toggle-model-list";

        /// <summary>
        /// Stands in for the model section when it is not rendered at all (an unauthenticated provider that
        /// needs a key). A separate tag from <see cref="Models"/> on purpose: "there is no section" and "the
        /// section is present but empty" are different states, and one check cannot stand for both.
        /// </summary>
        internal const string ModelsHidden = "provider-models-hidden";

        /// <summary>The empty-state line inside the model section: reachable, but the endpoint listed nothing.</summary>
        internal const string ModelsEmpty = "provider-models-empty";

        internal const string ModelRow = "provider-model-row";
        internal const string ModelEnabled = "provider-model-enabled";
        internal const string ModelContext = "provider-model-context";
        internal const string ModelCatalog = "provider-model-catalog";
        internal const string AddModelButton = "provider-add-model";
        internal const string RemoveAllModels = "provider-remove-all-models";
        internal const string RemoveProvider = "provider-remove";
        internal const string ProviderHeaderDivider = "provider-header-divider";

        /// <summary>The control that re-fetches the list from the endpoint.</summary>
        internal const string RefreshModels = "provider-refresh-models";
        // The single authenticate/disconnect control. It is tagged because the self-check has to tell "the
        // button is there" from "the button says the right thing for this state", and those are different
        // failures with the same shape in the tree.
        internal const string AuthButton = "provider-auth-button";
        /// <summary>Historical tag for the removed change-method control; kept so a regression that reintroduces it fails.</summary>
        internal const string ChangeAuthMethod = "provider-change-auth-method";
        internal const string NoCredential = "provider-no-credential";

        /// <summary>
        /// The one line under the provider cards that names where keys are actually stored. Tagged because it is
        /// the only place the honesty about the file tier is visible, and an assertion has to be able to tell
        /// "the line is missing" from "the line is there and empty".
        /// </summary>
        internal const string SecretBackend = "provider-secret-backend";
        internal const string Affiliate = "provider-affiliate";

        // ── Draft interaction (v3) ──
        // The edit form lives inside the group rather than in a modal window, and every draft is always the
        // first child of the list it belongs to. Tags let the self-check find each draft state without walking
        // the tree positionally, which a collapsible layout invalidates on every reshuffle.

        /// <summary>The in-group edit draft form (opened by the header's pencil icon).</summary>
        internal const string EditProvider = "provider-edit";

        /// <summary>The header enable/disable toggle for the whole provider.</summary>
        internal const string ProviderEnabled = "provider-enabled";

        /// <summary>The collapsed header's status dot (colour compresses the three link states).</summary>
        internal const string StatusDot = "provider-status-dot";

        /// <summary>The header's wire line: which path this provider's model is actually sent to.</summary>
        internal const string Wire = "provider-wire";

        /// <summary>The edit draft's form container, distinct from the collapsed header.</summary>
        internal const string ProviderDraft = "provider-draft";

        /// <summary>The draft row inserted at the top of a model list by "add model".</summary>
        internal const string ModelDraft = "provider-model-draft";

        /// <summary>The draft form inserted at the top of the provider list by "add custom endpoint".</summary>
        internal const string AddProviderDraft = "provider-add-draft";

        // The add draft's five controls and its save button. Each control is tagged so the self-check can measure
        // that every setting occupies its own full-width row, and can read the selected wire and validation state.
        internal const string AddDraftName = "provider-add-name";
        internal const string AddDraftBaseUrl = "provider-add-base-url";
        internal const string AddDraftWire = "provider-add-wire";
        internal const string AddDraftKey = "provider-add-key";
        internal const string AddDraftHeaders = "provider-add-headers";
        internal const string AddDraftSave = "provider-add-save";
    }
private string SelectedToolApprovalMode=> ToolApprovalPicker.SelectedItem is ComboBoxItem { Tag: string tag } && IsToolApprovalMode(tag) ? tag : ToolApprovalModes.Ask;
}
