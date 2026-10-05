using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
// GetLogicalDescendants (not GetVisualDescendants) on purpose: the model section is read for a group that may
// not be attached to a visual root yet, and the visual walk returns an empty collection in that case — which
// turns "the check found nothing" into "the check passed".
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub.App;

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

    /// <summary>True while a manual check is running: it owns the status line, so
    /// <see cref="RenderUpdateState"/> must not clobber it. The download needs no such flag — its phase
    /// lives in <see cref="UpdateService"/> and <see cref="RenderUpdateState"/> reads it from there.</summary>
    private bool _checking;

    /// <summary>Whether this page is currently subscribed to <see cref="UpdateService.Changed"/>
    /// (hooked while on screen only).</summary>
    private bool _updateHooked;

    /// <summary>
    /// The progress area's tooltip content. It stays the **same instance** for the page's whole life and
    /// is installed as the tip exactly once (see <see cref="SetDownloadTooltip"/>): that is what lets the
    /// tooltip remain open while the pointer rests in the progress area.
    /// </summary>
    private readonly TextBlock _speedTip = new();

    /// <summary>
    /// The two update action buttons' tooltip content. Stable instances installed once and rewritten in
    /// place, for the same reason as <see cref="_speedTip"/>: re-assigning <c>ToolTip.Tip</c> closes an
    /// open tooltip. They name the version the button will install — the status line says "ready", the
    /// button is where the user decides, and that is where the number has to be.
    /// </summary>
    private readonly TextBlock _downloadTip = new();
    private readonly TextBlock _restartTip = new();

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
        CustomDownloadSourceBox.LostFocus += (_, _) => OnCustomDownloadSourceChanged();
        ChooseDataDirectoryButton.Click += async (_, _) => await ChooseDataDirectoryAsync();
        ChooseProjectDirectoryButton.Click += async (_, _) => await ChooseProjectDirectoryAsync();
        OpenDataFolderButton.Click += (_, _) => _openFolder(_workspace.Store.Root);
        SelectVisualStudioButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: true);
        SelectCodeButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: false);
        CheckForUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();
        DownloadUpdateButton.Click += async (_, _) => await DownloadUpdateAsync();
        CancelUpdateButton.Click += (_, _) => UpdateService.Instance.CancelDownload();
        RestartUpdateButton.Click += (_, _) => UpdateService.Instance.ApplyAndRestart();
        AutoDownloadCheck.IsCheckedChanged += (_, _) => OnAutoDownloadChanged();
        WireProviders();

        // Installed exactly once, on purpose — see SetDownloadTooltip for why re-assigning ToolTip.Tip
        // per progress tick is what used to make the tooltip impossible to keep on screen.
        ToolTip.SetTip(UpdateProgressHost, _speedTip);
        ToolTip.SetTip(DownloadUpdateButton, _downloadTip);
        ToolTip.SetTip(RestartUpdateButton, _restartTip);

        // The update card is a *view* of the shared UpdateService, so a check or a background download
        // started elsewhere (the shell's startup check) shows up here live. Hooked while the page is on
        // screen only: a cached page that has been navigated away shouldn't keep rendering.
        AttachedToVisualTree += (_, _) => HookUpdateService();
        DetachedFromVisualTree += (_, _) => UnhookUpdateService();

        // DataRootNote's copy goes through XAML's {DynamicResource}; **don't** assign it imperatively here:
        // an imperative value won't change on language switch, and the self-check reads this control's text to judge whether localization works.
        Reload();
    }

    /// <summary>
    /// The language tags declared in XAML. The language mapping lives on XAML's <c>Tag</c>; here we just read it out —
    /// assertions rely on it to confirm "the languages the dropdown items declare" and "the supported languages the code knows" haven't diverged.
    /// </summary>
    internal string[] DeclaredLanguages
        => LanguagePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>The currently selected language tag. Unknown/none fall back to the default language (same rule as <see cref="HubTexts.Normalize"/>).</summary>
    internal string SelectedLanguage
        => LanguagePicker.SelectedItem is ComboBoxItem { Tag: string tag } && HubTexts.IsSupported(tag)
            ? tag
            : HubTexts.DefaultLanguage;

    /// <summary>The theme tags declared in XAML, in the order they appear. Assertions compare it against <see cref="HubTheme.All"/>.</summary>
    internal string[] DeclaredThemes
        => ThemePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>The currently selected theme. Unknown/none fall back to following the system (same rule as <see cref="HubTheme.Normalize"/>).</summary>
    internal string SelectedTheme
        => ThemePicker.SelectedItem is ComboBoxItem { Tag: string tag } && HubTheme.IsSupported(tag)
            ? HubTheme.Normalize(tag)
            : HubTheme.DefaultTheme;

    /// <summary>
    /// The editor-executable validation rule (the WPF version wrote it once per branch).
    /// Extracted into a pure static function so it **can be asserted** — actually popping a file picker can't be automated,
    /// and "rejecting the wrong file" is exactly the branch most easily missed and hardest to spot in a few manual clicks.
    /// </summary>
    internal static bool MatchesEditorExecutable(bool visualStudio, string path)
        => System.IO.Path.GetFileName(path).Equals(visualStudio ? "devenv.exe" : "Code.exe", StringComparison.OrdinalIgnoreCase);

    internal void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        _ready = false;
        try
        {
            LocalizeThemeItems();
            LocalizeDownloadSourceItems();
            SelectLanguage(HubStrings.Language);
            SelectTheme(_preferences.Theme);
            SelectDownloadSource(_preferences.DownloadSource);
            CustomDownloadSourceBox.Text = _preferences.CustomDownloadSource ?? "";
            DataLocation.Text = _workspace.Store.Root;
            DefaultProjectLocation.Text = _preferences.ProjectDirectory ?? HubStrings.Get("NotSelected");

            var state = _workspace.State;
            var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
            DefaultEngine.Text = engine?.ToString() ?? HubStrings.Get("NoDefault");
            VisualStudioLocation.Text = Display(state.VisualStudioExecutable);
            CodeLocation.Text = Display(state.CodeExecutable);
            // The current-version line is read from the assembly; it is not localizable copy, so it
            // is safe to assign imperatively here and won't need a language-switch refresh.
            CurrentVersionLine.Text = HubStrings.Get("CurrentVersion") + "  v" +
                (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            // Visual Studio (devenv.exe) is Windows-only; hide its row on other hosts so the user isn't
            // offered an editor that can never be installed there.
            VisualStudioRow.IsVisible = OperatingSystem.IsWindows();
            // The auto-download switch mirrors the preference. Assigning it here doesn't re-save:
            // _ready is false for the whole reload (the same gate the dropdowns rely on). It is
            // disabled on a dev/portable copy, where there is nothing to auto-update.
            AutoDownloadCheck.IsChecked = _preferences.AutoDownloadUpdates;
            AutoDownloadCheck.IsEnabled = UpdateService.Instance.IsInstalled;
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

    private async Task SelectEditorAsync(bool visualStudio)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        // Both the title and the rejection copy name the concrete executable: when picked wrong, the user should see at a glance which one to choose, not just be told "wrong pick".
        var title = HubStrings.Get(visualStudio ? "Select devenv.exe" : "Select Code.exe");
        var picked = await Pickers.PickFileAsync(top, title,
            [new FilePickerFileType(visualStudio ? "devenv.exe" : "Code.exe") { Patterns = ["*.exe"] }]);

        if (picked.Outcome != PickOutcome.Picked)
        {
            if (picked.Outcome == PickOutcome.NotLocal)
            {
                await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
            }

            return;
        }

        await _workspace.SelectEditorAsync(visualStudio, picked.Path);
        Reload();
    }

    private string Display(string? path) => path is { Length: > 0 } ? path : HubStrings.Get("NotSelected");

    /// <summary>
    /// Persists the "auto-download" choice and, when it is switched on with an update already known,
    /// starts the background download right away — otherwise the choice would not take effect until the
    /// next check, which reads as "the checkbox does nothing".
    /// </summary>
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
            UpdateService.Instance.StartPendingAutoDownload();
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
    /// The settings-page "Check for updates" entry point. Unlike the silent startup check, this one
    /// surfaces every outcome: the button is clicked *because* the user wants a visible answer.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        // The status line doubles as a "busy" indicator; the button is disabled while a check runs so
        // a second click can't stack a second network round-trip on top of the first.
        _checking = true;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusLine.Text = HubStrings.Get("CheckingUpdate");
        try
        {
            await UpdateService.Instance.CheckAsync();
        }
        finally
        {
            _checking = false;
            CheckForUpdatesButton.IsEnabled = true;
            RenderUpdateState();
        }
    }

    /// <summary>
    /// The tooltip of an update action button: the copy named by <paramref name="key"/>, filled in with
    /// the version it will install. Empty while no version is known — a tip that says "download and
    /// restart" without saying what is worse than no tip at all.
    /// </summary>
    internal static string ComposeActionTip(string key, string? version)
        => version is { Length: > 0 } ? string.Format(HubStrings.Get(key), version) : "";

    /// <summary>
    /// Prefixes an update status line with the version being installed, so the number survives past the
    /// "found" moment: <see cref="RenderUpdateState"/> swaps the whole line for download progress, then
    /// for the restart hint, and neither of those carries a version of its own — before this, the version
    /// disappeared the instant the download started (and with auto-download on, that is immediately).
    ///
    /// Pure and separate from the rendering so the rule can be asserted: a dropped version still reads
    /// like a perfectly good status line, which is the "invisible failure" shape.
    /// </summary>
    internal static string ComposeUpdateStatus(string status, string? version)
        => version is { Length: > 0 }
            ? string.Format(HubStrings.Get("UpdateVersionPrefix"), version) + status
            : status;

    /// <summary>
    /// Renders the card from <see cref="UpdateService"/>: the download phase first (progress bar, cancel
    /// / restart), then the check result (status text, which button is offered). Called when the page is
    /// shown, on <see cref="UpdateService.Changed"/>, and after a manual check — so a check or a
    /// background download started elsewhere shows up here too, and the status line re-localizes on a
    /// language switch.
    /// </summary>
    private void RenderUpdateState()
    {
        // A manual check owns the status line while it runs; don't overwrite "checking…".
        if (_checking)
        {
            return;
        }

        var service = UpdateService.Instance;
        var pendingVersion = service.PendingVersion;

        // Both action buttons name the version all the time — not only in the phase that shows them —
        // because a tip read off a stale render would still be on screen after the state moved on.
        _downloadTip.Text = ComposeActionTip("UpdateDownloadActionTip", pendingVersion);
        _restartTip.Text = ComposeActionTip("UpdateRestartActionTip", pendingVersion);

        // Only the host is toggled: it carries the padding that makes the progress area hoverable, so
        // hiding the bar alone would leave an invisible strip still able to show a tooltip.
        UpdateProgressHost.IsVisible = false;
        CheckForUpdatesButton.IsVisible = false;
        DownloadUpdateButton.IsVisible = false;
        RestartUpdateButton.IsVisible = false;
        CancelUpdateButton.IsVisible = false;

        if (service.Download == UpdateService.DownloadState.Downloading)
        {
            SetStatusLineError(false);
            UpdateProgress.Value = service.DownloadPercent;
            UpdateProgressHost.IsVisible = true;
            CancelUpdateButton.IsVisible = true;
            UpdateStatusLine.Text = ComposeUpdateStatus(
                string.Format(HubStrings.Get("UpdateDownloading"), service.DownloadPercent), pendingVersion);
            SetDownloadTooltip(string.Format(
                HubStrings.Get("UpdateDownloadSpeed"), UpdateService.FormatSpeed(service.DownloadBytesPerSecond)));
            return;
        }

        if (service.Download == UpdateService.DownloadState.Ready)
        {
            SetStatusLineError(false);
            UpdateProgress.Value = 100;
            UpdateProgressHost.IsVisible = true;
            RestartUpdateButton.IsVisible = true;
            var readyLine = ComposeUpdateStatus(HubStrings.Get("UpdateReadyToRestart"), pendingVersion);
            UpdateStatusLine.Text = readyLine;
            // The bar stays on screen once the download is done, so its tooltip needs copy that is still
            // true — otherwise hovering it would keep repeating a speed that no longer means anything.
            // It carries the version too: this is the line the user reads while deciding to restart.
            SetDownloadTooltip(readyLine);
            return;
        }

        // A failed download is reported right here instead of in a dialog: the whole update flow is
        // deliberately modal-free, and this card is where the user is already looking. The retry is the
        // "Download & restart" button (and any new check, which clears the error).
        if (service.DownloadError is { } error)
        {
            SetStatusLineError(true);
            UpdateStatusLine.Text = ComposeUpdateStatus(HubStrings.Get("UpdateDownloadFailed") + error, pendingVersion);
            DownloadUpdateButton.IsVisible = true;
            return;
        }

        SetStatusLineError(false);

        var last = service.Last;
        UpdateStatusLine.Text = last?.Result switch
        {
            UpdateService.CheckResult.UpdateAvailable => string.Format(HubStrings.Get("UpdateReady"), pendingVersion),
            UpdateService.CheckResult.UpToDate => HubStrings.Get("UpdateUpToDate"),
            UpdateService.CheckResult.NotInstalled => HubStrings.Get("UpdateNotInstalled"),
            UpdateService.CheckResult.Failed => HubStrings.Get("UpdateFailed"),
            _ => HubStrings.Get("UpdateCheckHint"),
        };

        if (last?.Result is UpdateService.CheckResult.UpdateAvailable)
        {
            DownloadUpdateButton.IsVisible = true;
        }
        else
        {
            CheckForUpdatesButton.IsVisible = true;
        }
    }

    /// <summary>
    /// Switches the status line between the muted look and the danger ink by swapping style classes —
    /// not an inline <c>Foreground</c> — so the token re-resolves on a theme switch. Never both at once:
    /// they set the same property and whichever style is declared later would silently win.
    /// </summary>
    private void SetStatusLineError(bool error)
    {
        UpdateStatusLine.Classes.Set("muted", !error);
        UpdateStatusLine.Classes.Set("danger", error);
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

    /// <summary>
    /// Sets the text of the progress area's tooltip.
    ///
    /// It writes into the <see cref="_speedTip"/> TextBlock that was attached as the tip **once** in the
    /// constructor, rather than re-assigning <c>ToolTip.Tip</c> on every progress tick. That is not
    /// tidiness — it is the whole reason the tooltip can stay on screen:
    ///
    /// Avalonia's ToolTipService closes an open tooltip when the Tip value changes to **null**, and a new
    /// value assigned while the tooltip is closed does **not** re-open it (its TipChanged handler only
    /// acts while <c>IsOpen</c> is true). Resetting the tip to null on every render therefore killed the
    /// tooltip after the first tick, and the pointer had to leave the control and come back before it
    /// would show again — which is exactly the "it won't stay put" behaviour. Passing a stable tip object
    /// keeps <c>TipProperty</c> unchanged, so nothing ever closes it: the tooltip stays up for as long as
    /// the pointer rests in the progress area, and the text still updates live.
    /// </summary>
    private void SetDownloadTooltip(string text) => _speedTip.Text = text;

    /// <summary>
    /// The "Download &amp; restart" button. Reaching the line after the await means the download failed or
    /// was cancelled (success replaces this process); either way the card renders the outcome by itself,
    /// through <see cref="UpdateService.Changed"/> — the update flow puts up **no dialog at all**.
    /// </summary>
    private async Task DownloadUpdateAsync() => await UpdateService.Instance.DownloadAndApplyAsync();

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

        if (_chat.Providers.Count == 0)
        {
            ProviderGroupList.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("NoProvider"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var provider in _chat.Providers)
        {
            ProviderGroupList.Children.Add(BuildProviderGroup(provider));
        }
    }

    /// <summary>
    /// One provider group: header (name, source tag, remove affordance), then the model list, then the sign-in
    /// entrances, then the accounts.
    ///
    /// <para><b>The sign-in entrance follows the manifest, not a control.</b> A provider that supports pasted
    /// keys gets a key field, one that supports browser sign-in gets a button, one that supports both gets both
    /// with an "or" between them, and a keyless local endpoint gets a plain note. There is no method dropdown:
    /// which routes exist is a fact about the provider, and asking the user to pick one asks them to understand
    /// our transport before they can sign in.</para>
    /// </summary>
    private Control BuildProviderGroup(ModelProvider provider)
    {
        var content = new StackPanel { Spacing = 10 };

        content.Children.Add(BuildProviderHeader(provider));
        content.Children.Add(BuildProviderSummary(provider));
        if (CanListModels(provider))
            content.Children.Add(BuildProviderHeaderDivider());
        content.Children.Add(BuildModelSection(provider));

        // A keyless provider says so once, in the summary, rather than growing a section of its own.
        if (!NeedsCredential(provider))
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

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            // The provider id, not the display name: a lookup by name breaks the moment a preset is renamed
            // ("Ollama (local)") and would report a missing group as a layout failure.
            Tag = provider.Id,
            Child = content,
        };
        card.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.ProviderSurface"));
        card.Bind(Border.BorderBrushProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.BorderSubtle"));
        return card;
    }

    /// <summary>
    /// The one line of prose under the name: how many models are configured, not how many are in the full catalog.
    ///
    /// <para>It exists because the header is now only a name and a state, and a bare list of names gives the
    /// eye nothing to compare rows by. It is deliberately counts and not detail — the details are one click
    /// away, and printing a base URL under every row is what made the list feel heavy in the first place.</para>
    ///
    /// <para><b>No account count.</b> A provider has exactly one credential, so "1 account" was a number with
    /// nothing to vary; the state mark beside the name already says whether it is connected.</para>
    /// </summary>
    private Control BuildProviderSummary(ModelProvider provider)
    {
        var models = provider.Models.Count;
        var text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get(models == 1 ? "SummaryModelCountOne" : "SummaryModelCount"),
            models);

        return new TextBlock
        {
            Text = text,
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Tag = SectionTags.Summary,
        };
    }

    /// <summary>
    /// The group header: who it is, where it came from, and — on the right — whether it is linked and the two
    /// actions that change that.
    ///
    /// <para><b>The check mark is the linked state, and it is drawn at the header's right edge</b> because that
    /// is where the eye goes to answer "is this one set up?". The rule is deliberately source-agnostic: a
    /// pasted key and a browser sign-in produce the same kind of credential, so both light it up. Splitting
    /// "authenticated" from "has a key" would re-introduce the distinction the account model exists to
    /// remove.</para>
    ///
    /// <para><b>Disconnect and remove are two buttons, not one.</b> They answer different intentions:
    /// disconnect is "revoke access on this machine" (available everywhere, including the pinned default),
    /// remove is "stop showing me this endpoint" (unavailable only for the default, which the manifest
    /// re-seeds). Folding them together would force someone who only wants to unlink a key to reconfigure the
    /// endpoint afterwards.</para>
    /// </summary>
    private Control BuildProviderHeader(ModelProvider provider)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock
        {
            Text = provider.Name,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // The source tag answers "why can I not edit this base URL?" before the user tries.
        title.Children.Add(Pill(
            HubStrings.Get(provider.IsCustom ? "ProviderCustomTag" : "ProviderBuiltInTag"),
            brush: null));

        if (_chat?.ActiveProvider?.Id == provider.Id)
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

        var linked = IsLinked(provider);

        // The state mark sits left of the actions so it reads as a status column rather than as part of the
        // button row. A drawn tick, not a coloured dot: colour alone is not a label, and "connected" is the
        // kind of thing that must survive a colour-blind reading.
        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Children.Add(linked ? BuildCheckIcon() : BuildUnlinkedIcon());

        // The label is bound through DynamicResource rather than resolved on the spot, for the same reason
        // the icons are: the group list is rebuilt from Reload(), and at that moment this page may not be
        // attached to the resource host yet — a one-shot lookup then yields null and the text renders with no
        // brush at all, which reads on screen as "the status is missing" rather than as a theming bug. A
        // deferred binding resolves once attached, and re-resolves when the language or theme changes.
        var statusLabel = new TextBlock
        {
            Text = HubStrings.Get(linked ? "ProviderConnected" : "ProviderNotConnected"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusLabel.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                linked ? "Hub.Success" : "Hub.TextTertiary"));
        status.Children.Add(statusLabel);
        actions.Children.Add(status);

        // A provider that needs no credential gets no button. Ollama is the case that proves the rule: it is
        // never "unauthenticated", so offering "authenticate" would be offering an action with no outcome.
        var needsCredential = NeedsCredential(provider);

        // One button, two meanings. "Authenticate" opens the dialog; "Disconnect" clears the credential. It
        // is a single control rather than two because the two are mutually exclusive by construction — there
        // is never a state where both would be the right offer — and a row that showed both at once would
        // ask the user to choose between starting over and undoing, which is not a question this row asks.
        if (needsCredential)
        {
            var auth = new Button
            {
                Classes = { "quiet" },
                Padding = new Thickness(8, 3),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.AuthButton,
            };

            if (linked)
            {
                // The wording is the state's own verb: the row already says "authenticated", so the label is
                // what tells the user what the button will do.
                auth.Content = HubStrings.Get("DisconnectProvider");
                auth.Click += async (_, _) => await DisconnectProviderAsync(provider);
            }
            else
            {
                auth.Content = HubStrings.Get("Authenticate");
                auth.Click += async (_, _) => await AuthenticateProviderAsync(provider);
            }

            actions.Children.Add(auth);
        }

        // Remove is absent — not present-and-refusing — for the one provider that cannot be removed: an icon
        // whose only outcome is an error message is worse than no icon. The hint is on the group's tooltip
        // instead, so the rule is discoverable without being a dead control.
        if (ChatWorkspace.CanRemoveProvider(provider))
        {
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
        else if (needsCredential)
        {
            // Only worth saying when there is a disconnect button next to it to explain; on a keyless
            // provider the hint would be explaining a rule that has nothing to do with it.
            ToolTip.SetTip(status, HubStrings.Get("ProviderPinnedHint"));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(title);
        grid.Children.Add(actions);

        return grid;
    }

    /// <summary>
    /// Whether a provider counts as linked: it has at least one saved credential.
    ///
    /// <para>Source-agnostic by design. The account model made a pasted key and a browser sign-in the same
    /// shape, so asking "how did this credential arrive?" here would undo that — and would show "not
    /// connected" for a provider that plainly works.</para>
    /// </summary>
    private bool IsLinked(ModelProvider provider)
        => _chat?.CredentialFor(provider.Id) is not null;


    /// <summary>
    /// The configured model sub-list: catalog entries are added here only when selected or when a manifest
    /// default matches. Each row has enable/remove actions; the full catalog is in the model-section toolbar.
    ///
    /// <para>Each row keeps the model name, optional description, current-use marker and availability toggle.
    /// Model selection stays in the conversation page's picker. The description is a lookup
    /// (<see cref="ModelCatalog"/>) and is simply absent for a model Hub
    /// does not know, so a brand-new name still works.</para>
    ///
    /// <para><b>The configured-model list is not rendered before authentication.</b> The full cached catalog
    /// remains reachable through the model toolbar, but fetching it requires the user's key. A keyless provider
    /// is exempt: Ollama needs no key, so it can fetch and show its configured entries immediately.</para>
    /// </summary>
    private Control BuildModelSection(ModelProvider provider)
    {
        if (!CanListModels(provider)) return new Border { Tag = SectionTags.ModelsHidden };

        // Tagged so the self-check can find this section by role instead of by position: an index-based walk
        // silently reads the wrong node the moment the layout gains a row, and then reports a failure that
        // has nothing to do with what broke.
        var section = new StackPanel { Spacing = 6, Tag = SectionTags.Models };

        var expanded = provider.Models.Count <= 10;
        var listContent = new StackPanel { Spacing = 8, IsVisible = expanded, Tag = SectionTags.ModelList };
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
        });
        if (provider.ActiveModel is { } activeModel)
        {
            toggleText.Children.Add(new TextBlock
            {
                Text = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("CurrentModelSummary"),
                    activeModel.Name),
                Classes = { "muted" },
                FontSize = 11,
            });
        }
        toggleContent.Children.Add(toggleText);

        var toggle = new ToggleButton
        {
            Content = toggleContent,
            Classes = { "quiet" },
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

        if (provider.Models.Count == 0)
        {
            // The catalog is separate; this empty state means the user has not enabled or manually added a model.
            listContent.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("NoEnabledModels"),
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Tag = SectionTags.ModelsEmpty,
            });
            section.Children.Add(listContent);
            return section;
        }

        foreach (var model in provider.Models)
        {
            listContent.Children.Add(BuildModelRow(provider, model));
        }

        section.Children.Add(listContent);
        return section;
    }

    /// <summary>
    /// Whether the provider's model catalog may be fetched.
    ///
    /// <para>True for a provider that does not require a key or one that holds a credential. False for a
    /// provider that requires a key and does not have one — the request would come back 401.</para>
    ///
    /// <para>Lives here rather than in <see cref="ChatWorkspace"/> because it is a <b>presentation</b>
    /// decision: the fetch itself is legal without a key (the endpoint decides), and a future caller that
    /// wants to try anyway should not have to fight a UI-layer guard.</para>
    /// </summary>
    private bool CanListModels(ModelProvider provider)
        => !provider.ApiKeyRequired || IsLinked(provider);

    /// <summary>Whether this provider has any way to authenticate at all — the same test the header uses to
    /// decide whether to offer the auth button, so the two cannot disagree about what "unready" means.</summary>
    private static bool NeedsCredential(ModelProvider provider)
        => provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey)
           || provider.SupportsOAuth;

    private Control BuildModelRow(ModelProvider provider, ProviderModel model)
    {
        var inUse = ReferenceEquals(model, provider.ActiveModel);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock
        {
            Text = model.Name,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (inUse)
        {
            nameRow.Children.Add(Pill(HubStrings.Get("AccountActive"), BrushOrNull("Hub.AccentSoft"),
                BrushOrNull("Hub.TextOnAccent")));
        }
        text.Children.Add(nameRow);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var enabled = new ToggleSwitch
        {
            IsChecked = model.Enabled,
            // The Fluent theme labels the knob "On"/"Off" — English text in a bilingual UI that the row's
            // knob position already says. Clear both contents rather than translating them.
            OnContent = null,
            OffContent = null,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = SectionTags.ModelEnabled,
        };
        ToolTip.SetTip(enabled, HubStrings.Get("ModelEnabledHint"));
        enabled.IsCheckedChanged += (_, _) => SetModelEnabled(provider.Id, model.Name, enabled.IsChecked == true);
        actions.Children.Add(enabled);

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

        var row = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Tag = SectionTags.ModelRow,
            Child = grid,
        };
        // Bind, don't capture: BrushOrNull runs while the page may still be unattached, and a static null
        // here rendered the rows transparent — they silently took the provider card's colour instead of
        // the settings card's (the exact confusion that made the card itself get recoloured once).
        row.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.Surface"));
        row.Bind(Border.BorderBrushProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.BorderSubtle"));
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
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M6,1.5 V10.5 M1.5,6 H10.5"),
            StrokeThickness = 1.7,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 14, Height = 14, Child = path };
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
        if (_chat is null || Owner() is not { } owner) return;

        var name = await PromptWindow.ShowAsync(
            owner,
            HubStrings.Get("AddModelTitle"),
            "",
            HubStrings.Get("ModelNamePlaceholder"));
        if (name is null) return;

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
        RebuildProviderGroups();
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

    /// <summary>
    /// The browser sign-in for one provider group. Everything that can go wrong here is reported through the
    /// status line rather than a dialog: the flow is async and the user is looking at another window, so a
    /// modal box would be waiting in the wrong place.
    ///
    /// <para>The button is passed in rather than looked up: with several groups on screen there is no single
    /// "the sign-in button", and disabling the wrong one would leave the clicked button live while a second
    /// flow started underneath.</para>
    /// </summary>
    /// <param name="button">
    /// The button that started the flow, disabled while the browser is open. <c>null</c> when the flow was
    /// started from the auth dialog instead of from a row: there is no row button to disable, and the dialog
    /// has already closed by the time the flow runs.
    /// </param>
    private async Task SignInAsync(ModelProvider provider, Button? button)
    {
        if (_chat is null || !provider.SupportsOAuth) return;

        SetProviderStatus(false, HubStrings.Get("AuthOAuthPending"));
        if (button is not null) button.IsEnabled = false;

        try
        {
            var result = await _chat.SignInWithOAuthAsync(provider.Id, url =>
            {
                // The manual URL has nowhere to live in a group (it would have to be rebuilt into the row that
                // is about to be rebuilt anyway), so it goes to the status line, which is already the channel
                // for every other outcome of this flow.
                SetProviderStatus(false, HubStrings.Get("AuthOAuthNoBrowser") + "\n" + url);
            });

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

            SetProviderStatus(false, HubStrings.Get("AuthOAuthSucceeded"));

            // The credential is stored the moment the flow lands, so the linked mark repaints now — the
            // fetch below can take seconds and must not hold the state badge hostage.
            RebuildProviderGroups();

            // Same reasoning as the pasted-key path: a sign-in that leaves an empty model list is only half
            // finished, and the list is reachable now precisely because the browser just minted the key.
            await RefreshModelsAsync(provider);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
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
    }

    /// <summary>The free-form path, shared by the picker's custom row. Validation lives in the dialog and is
    /// re-run by <see cref="ChatWorkspace.AddProvider"/>, so a half-filled provider cannot reach the file.</summary>
    private async Task AddCustomProviderAsync()
    {
        var dialog = await ProviderEditWindow.ShowAsync(Owner(), existing: null);
        if (dialog is null) return;

        if (_chat.AddProvider(dialog.ProviderName, dialog.BaseUrl, dialog.Model, dialog.ApiKey) is null)
        {
            // The dialog validates too; reaching here means a platform without a secret store refused the key.
            SetProviderStatus(true, HubStrings.Get(_chat.CanStoreSecrets ? "ProviderBaseUrlInvalid" : "ApiKeyPlatformUnsupported"));
            return;
        }

        SetProviderStatus(false, "");
        RebuildProviderGroups();
    }

    private async Task RemoveProviderAsync(ModelProvider provider)
    {
        // The header only offers this for a provider that may be removed, so this is a guard rather than the
        // rule; the rule itself lives in ChatWorkspace so the two cannot drift.
        if (!ChatWorkspace.CanRemoveProvider(provider)) return;

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

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("DisconnectProvider"),
                provider.Name + "\n\n" + HubStrings.Get("DisconnectProviderConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        SetProviderStatus(false, _chat.DisconnectProvider(provider.Id)
            ? HubStrings.Get("ProviderDisconnected")
            : HubStrings.Get("ProviderNothingToDisconnect"));
        RebuildProviderGroups();
    }

    /// <summary>
    /// Opens the two-step authentication dialog and applies whatever it produces.
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

        var stored = _chat.AddCredential(provider.Id, "", outcome.ApiKey, CredentialSources.ApiKey);
        if (stored is null)
        {
            SetProviderStatus(true, HubStrings.Get("AuthSecretsUnsupported"));
            return;
        }

        // The key is already stored, so "linked" is true now: repaint the mark before the fetch rather than
        // after it — the list can take seconds to arrive and the state must not wait on it.
        RebuildProviderGroups();

        // The model list needs the key, and the user has just handed one over — so it is fetched here rather
        // than left as the next thing to remember. It is the same call the refresh button makes; doing it
        // automatically is what turns "authenticated" into "ready to use" without a second visit.
        await RefreshModelsAsync(provider);
    }

    /// <summary>The top-level window, used as the dialog owner. <c>null</c> in a design-time host, in which
    /// case the dialog still opens (it degrades to a standalone window).</summary>
    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;

    private void OpenReferral(ModelProvider provider)
    {
        if (provider.ReferralUrl is { Length: > 0 } url)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatusLineError(true);
                UpdateStatusLine.Text = ex.Message;
            }
        }
    }

    // ── Verification hooks for the update card (used by --verify-shell) ──

    /// <summary>The update card's status line **as rendered**. Read back rather than recomputed: what the
    /// user sees is this control's text, and every phase of the update is a different render of it.</summary>
    internal string UpdateStatusText => UpdateStatusLine.Text ?? "";

    /// <summary>The "Download &amp; restart" button's tooltip — it has to name the version it will install.</summary>
    internal string DownloadActionTip => _downloadTip.Text ?? "";

    /// <summary>The "Restart now" button's tooltip, shown only once an update is downloaded and waiting.</summary>
    internal string RestartActionTip => _restartTip.Text ?? "";

    /// <summary>Whether the "Restart now" button is the action being offered (i.e. the download finished).</summary>
    internal bool RestartButtonVisible => RestartUpdateButton.IsVisible;

    // ── Verification hooks for the provider card (used by --verify-shell) ──

    /// <summary>Number of configured providers.</summary>
    internal int ProviderCount => _chat.Providers.Count;

    /// <summary>Number of built-in presets the "add provider" dialog would offer for this workspace — the
    /// catalog minus whatever is already configured.</summary>
    internal int AvailablePresetCount => _chat.AvailablePresets().Count;

    /// <summary>Runs the real "Add provider" button handler, so the self-check drives the same path a click
    /// does (the picker dialog is modal and cannot be clicked from the harness).</summary>
    internal Task ClickAddProviderForCheckAsync() => AddProviderAsync();

    /// <summary>Whether the "add provider" affordance is rendered.</summary>
    internal bool ProviderButtonsVisible => AddProviderButton.IsVisible;

    /// <summary>The provider card's status line text — provider operations and the sign-in flow report every
    /// outcome through it.</summary>
    internal string StatusLineText => ProviderStatusLine.Text ?? "";

    /// <summary>Whether the provider status line is on screen. It is hidden while empty, so a failed operation
    /// that wrote nothing would otherwise leave the page looking untouched.</summary>
    internal bool StatusLineVisible => ProviderStatusLine.IsVisible;

    // ── Verification hooks for the provider group list ──
    //
    // The groups are built in code, so the assertions read the rendered visual tree rather than the model:
    // what matters is that a group for the right provider exists and that its parts are actually on screen.
    // A missing group would otherwise pass a "provider exists in the model" check while the page showed
    // nothing at all.

    /// <summary>One rendered provider group, with the parts the self-check inspects.</summary>
    internal sealed record ProviderGroupView(
        string Name,
        bool HasRemoveButton,
        bool ShowsNoCredentialNote,
        bool HasAffiliateBox,
        bool IsLinked,
        string LinkedStatusText,
        bool StatusLabelIsPainted,
        bool StatusMarkIsPainted,
        /// <summary>The label on the single auth control; it is the state, so it is asserted, not just counted.</summary>
        string AuthButtonText,
        /// <summary>No credential input is rendered anywhere in the group. The whole point of the revision.</summary>
        bool HasCredentialField,
        string SummaryText,
        string[] ModelNames,
        bool[] ModelEnabled,
        string ActiveModelName,
        bool HasModelListToggle,
        bool ModelListExpanded,
        bool HasUseModelButton,
        bool HasModelCatalogButton,
        bool HasAddModelButton,
        bool ModelCatalogPrecedesAdd,
        bool HasRemoveAllModelsButton,
        /// <summary>Remove-all sits rightmost, after add: presence and order are different failures.</summary>
        bool RemoveAllFollowsAdd,
        bool HasProviderOutline,
        bool HasProviderSurface,
        bool HasModelRowsOutline,
        /// <summary>
        /// Every model row paints the settings-card surface, not the provider card's. A statically captured
        /// brush (<c>BrushOrNull</c> before attach) left the rows transparent once, and they silently took
        /// the card's darker colour — the rows must be bound, and this is what pins that.
        /// </summary>
        bool HasModelRowSurface,
        bool HasProviderHeaderDivider,
        /// <summary>
        /// Whether the model section is rendered at all. False for a provider that needs a credential and has
        /// none: the list is fetched with the user's key, so there is nothing to show and asking would be a
        /// request the user never authorized.
        /// </summary>
        bool ShowsModelSection,
        /// <summary>The section is present but the endpoint listed nothing (or has not been asked yet).</summary>
        bool ShowsModelEmptyState,
        /// <summary>True only if the legacy provider-list refresh control is present; it should stay false.</summary>
        bool HasRefreshButton,
        /// <summary>Legacy rule count; outlined model cards intentionally keep this at zero.</summary>
        int ModelDividerCount);

    /// <summary>The provider groups currently rendered, in display order.</summary>
    internal IReadOnlyList<ProviderGroupView> ProviderGroups
        => GroupCards.Select(ReadGroup).ToArray();

    /// <summary>Number of provider groups rendered — the count the page actually shows, not the model's.</summary>
    internal int ProviderGroupCount => GroupCards.Count();

    internal bool HasOnlyProviderCards
        => _chat is not null
           && ProviderGroupList.Children.Count == _chat.Providers.Count
           && GroupCards.All(card => _chat.Providers.Any(provider => provider.Id == card.Tag as string));

    /// <summary>The provider cards currently rendered.</summary>
    private IEnumerable<Border> GroupCards
        => ProviderGroupList.Children.OfType<Border>();

    /// <summary>
    /// Reads one group card.
    ///
    /// <para>An instance method so the card can be checked against the provider model while reading the rendered
    /// visual tree.</para>
    /// </summary>
    private ProviderGroupView ReadGroup(Border group)
    {
        var content = (StackPanel)group.Child!;

        // The header is a two-column Grid: name pills on the left, the status mark and available actions on the
        // right. Both columns are StackPanels, so the two are told apart by column rather than by shape — a
        // positional read would swap them the moment the title gained a pill.
        var header = (Grid)content.Children[0];
        var title = (StackPanel)header.Children[0];
        var name = title.Children.OfType<TextBlock>().First().Text ?? "";

        // The actions column packs [status stack] + [auth, catalog and remove actions as applicable]. The mark's
        // label is the source of truth for "linked": matching on the drawn tick would make the assertion pass
        // on any provider that happens to render a check-shaped Path, and the label is what the user reads.
        var actions = (StackPanel)header.Children[1];
        var statusStack = actions.Children.OfType<StackPanel>().FirstOrDefault();
        var statusLabel = statusStack?.Children.OfType<TextBlock>().FirstOrDefault();
        var statusText = statusLabel?.Text ?? "";
        var isLinked = statusText == HubStrings.Get("ProviderConnected");
        // Tags distinguish icon actions even though their content is a generated control rather than text.
        var hasRemove = actions.Children.OfType<Button>()
            .Any(button => button.Tag as string == SectionTags.RemoveProvider);
        var hasHeaderCatalogButton = actions.Children.OfType<Button>()
            .Any(button => button.Tag as string == SectionTags.ModelCatalog);

        // "The text is in the tree" is not "the user can see it". A brush that resolved to null while the page
        // was detached leaves a label with the right string and nothing to draw it in, and every check that
        // reads .Text still passes — which is exactly how the first version of this mark shipped a blank
        // status. So the assertion looks at the resolved paint, not at the string.
        var statusLabelIsPainted = statusLabel?.Foreground is not null;
        var statusMarkIsPainted = statusStack?.GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Shape>()
            .Any(shape => shape.Stroke is not null) ?? false;

        var sections = content.Children.OfType<StackPanel>()
            .Where(section => section.Tag is string)
            .ToArray();

        var modelSection = sections.FirstOrDefault(section => (string)section.Tag! == SectionTags.Models);
        var modelActions = modelSection?.Children.OfType<Grid>().FirstOrDefault()?.Children
            .OfType<StackPanel>().FirstOrDefault(stack => stack.Children.OfType<Button>().Any());
        var modelActionButtons = modelActions?.Children.OfType<Button>().ToArray() ?? [];
        var modelCatalogIndex = Array.FindIndex(modelActionButtons,
            button => button.Tag as string == SectionTags.ModelCatalog);
        var addModelIndex = Array.FindIndex(modelActionButtons,
            button => button.Tag as string == SectionTags.AddModelButton);
        var hasModelCatalogButton = modelCatalogIndex >= 0;
        var hasAddModelButton = addModelIndex >= 0;
        var modelCatalogPrecedesAdd = modelCatalogIndex >= 0 && addModelIndex > modelCatalogIndex;
        var removeAllIndex = Array.FindIndex(modelActionButtons,
            button => button.Tag as string == SectionTags.RemoveAllModels);
        var hasRemoveAllModelsButton = removeAllIndex >= 0;
        var removeAllFollowsAdd = addModelIndex >= 0 && removeAllIndex > addModelIndex;
        var hasProviderOutline = group.BorderThickness.Left > 0
                                 && group.BorderBrush is not null;
        var hasProviderSurface = ThemeProbe.IsToken(group.Background, "Hub.ProviderSurface");
        var hasProviderHeaderDivider = content.Children.OfType<Border>()
            .Any(border => border.Tag as string == SectionTags.ProviderHeaderDivider);

        // "Not rendered" is a *Border* placeholder rather than a missing section, so that "this group has no
        // model list" and "this group failed to build its model list" stay distinguishable — both would read as
        // an absent StackPanel otherwise.
        var modelsHidden = content.Children.OfType<Border>()
            .Any(border => border.Tag as string == SectionTags.ModelsHidden);

        var showsModelSection = modelSection is not null;

        // The empty-state line and model controls are looked up by tag rather than by position.
        var showsModelEmptyState = modelSection?.GetLogicalDescendants().OfType<TextBlock>()
            .Any(text => text.Tag as string == SectionTags.ModelsEmpty) ?? false;
        var hasRefresh = modelSection?.GetLogicalDescendants()
            .OfType<Button>()
            .Any(button => button.Tag as string == SectionTags.RefreshModels) ?? false;
        var modelList = modelSection?.GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(list => list.Tag as string == SectionTags.ModelList);
        var hasModelListToggle = modelSection?.GetLogicalDescendants().OfType<ToggleButton>()
            .Any(button => button.Tag as string == SectionTags.ToggleModelList) ?? false;
        var hasUseModelButton = modelSection?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Content?.ToString() == HubStrings.Get("ModelUse")) ?? false;

        // Each model row is [name (+ optional "in use" pill), optional description] in a text stack. The rows
        // live in a nested untagged StackPanel under the section's heading Grid.
        var modelRows = modelSection?.Children.OfType<Border>()
            .Where(row => row.Tag as string == SectionTags.ModelRow)
            .Concat(modelSection.Children.OfType<StackPanel>()
                .SelectMany(list => list.Children.OfType<Border>())
                .Where(row => row.Tag as string == SectionTags.ModelRow))
            .ToArray() ?? [];
        var hasModelRowsOutline = modelRows.Length > 0
                                  && modelRows.All(row => row.BorderThickness.Left > 0
                                                         && row.BorderBrush is not null);
        var hasModelRowSurface = modelRows.Length > 0
                                 && modelRows.All(row => ThemeProbe.IsToken(row.Background, "Hub.Surface"));

        var modelNames = new List<string>();
        var modelEnabled = new List<bool>();
        var activeModel = "";
        foreach (var row in modelRows)
        {
            var text = (StackPanel)((Grid)row.Child!).Children[0];
            var nameRow = (StackPanel)text.Children[0];
            var modelName = nameRow.Children.OfType<TextBlock>().First().Text ?? "";
            modelNames.Add(modelName);
            modelEnabled.Add(row.GetVisualDescendants().OfType<ToggleSwitch>()
                .FirstOrDefault(toggle => toggle.Tag as string == SectionTags.ModelEnabled)?.IsChecked == true);

            // The "in use" pill is the Border the row adds after the name.
            if (nameRow.Children.OfType<Border>().Any()) activeModel = modelName;
        }

        // Model rows are individually outlined cards, so there should be no extra dividers between them.
        const int modelDividers = 0;

        // The single auth control, read by its tag. Its *label* is the state: the same button reads
        // "authenticate" or "disconnect", so a check that only counted buttons would pass for either.
        var authButton = actions.Children.OfType<Button>()
            .FirstOrDefault(button => button.Tag as string == SectionTags.AuthButton);
        var authButtonText = authButton?.Content as string ?? "";

        // The whole point of this revision: no credential input exists anywhere in the list. A password-class
        // TextBox anywhere under the group is the regression this check exists to catch, and it is checked
        // over descendants because a future layout could easily nest it one level deeper.
        var hasCredentialField = content.GetVisualDescendants().OfType<TextBox>()
            .Any(box => box.Classes.Contains("password"));

        var summary = content.Children.OfType<TextBlock>()
            .FirstOrDefault(text => text.Tag as string == SectionTags.Summary)?.Text ?? "";
        var showsNoCredential = content.Children.OfType<TextBlock>()
            .Any(text => text.Tag as string == SectionTags.NoCredential);
        var hasAffiliate = content.Children.OfType<Border>()
            .Any(border => border.Tag as string == SectionTags.Affiliate);

        return new ProviderGroupView(
            name,
            hasRemove,
            showsNoCredential,
            hasAffiliate,
            isLinked,
            statusText,
            statusLabelIsPainted,
            statusMarkIsPainted,
            authButtonText,
            hasCredentialField,
            summary,
            modelNames.ToArray(),
            modelEnabled.ToArray(),
            activeModel,
            hasModelListToggle,
            modelList?.IsVisible == true,
            hasUseModelButton,
            hasModelCatalogButton && !hasHeaderCatalogButton,
            hasAddModelButton,
            modelCatalogPrecedesAdd,
            hasRemoveAllModelsButton,
            removeAllFollowsAdd,
            hasProviderOutline,
            hasProviderSurface,
            hasModelRowsOutline,
            hasModelRowSurface,
            hasProviderHeaderDivider,
            showsModelSection && !modelsHidden,
            showsModelEmptyState,
            hasRefresh,
            modelDividers);
    }

    /// <summary>Forces a repaint of the group list, so a self-check sees the tree a click would produce.</summary>
    internal void RefreshProviderGroupsForCheck()
    {
        RebuildProviderGroups();
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal bool SetModelListExpandedForCheck(string providerId, bool expanded)
    {
        var toggle = GroupCards
            .FirstOrDefault(group => group.Tag as string == providerId)?
            .GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(button => button.Tag as string == SectionTags.ToggleModelList);
        if (toggle is null) return false;

        toggle.IsChecked = expanded;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return true;
    }

    internal bool SetModelEnabledForCheck(string providerId, string modelName, bool enabled)
    {
        var modelRow = GroupCards
            .Where(group => group.Tag as string == providerId)
            .SelectMany(group => group.GetVisualDescendants().OfType<Border>())
            .FirstOrDefault(row => row.Tag as string == SectionTags.ModelRow
                                   && row.GetVisualDescendants().OfType<TextBlock>()
                                       .Any(text => text.Text == modelName));
        var rowToggle = modelRow?.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
        if (rowToggle is null) return false;

        rowToggle.IsChecked = enabled;
        return true;
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
        var border = ProviderGroupList.Children.OfType<Border>()
            .FirstOrDefault(candidate => candidate.Tag as string == providerId);
        return border is null ? null : ReadGroup(border);
    }

    /// <summary>Whether the affiliate disclosure is rendered at all (inside the OrcaRouter group).</summary>
    internal bool AffiliateDisclosureVisible => ProviderGroups.Any(group => group.HasAffiliateBox);

    internal string AffiliateDisclosureText => _affiliateTextForCheck;

    /// <summary>The affiliate text of the last rendered group that showed one — kept so the assertion can read
    /// the disclosure wording, which lives inside a group rather than in a page-level block.</summary>
    private string _affiliateTextForCheck = "";

    /// <summary>Points the sign-in flow at an injected transport so the self-check never opens a socket.</summary>
    internal void UseOAuthHandlerForCheck(System.Net.Http.HttpMessageHandler handler)
    {
        _chat.OAuthHttp = new System.Net.Http.HttpClient(handler);
        // The real flow waits minutes for a human and launches a browser; the harness must do neither, or the
        // shell check's own timeout fires first and reports the run as a hang rather than as a result.
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
        if (provider is null) return Task.CompletedTask;

        return SignInAsync(provider, null);
    }

    /// <summary>
    /// Points the model-list fetch at an injected transport, so the self-check can drive the whole path —
    /// request, parse, adopt, cache, repaint — without a live provider.
    ///
    /// <para>A separate seam from <see cref="UseOAuthHandlerForCheck"/> on purpose: the two answer different
    /// questions, and a handler wired for one would happily answer the other. Sharing a client here would make
    /// a check pass on the wrong response.</para>
    /// </summary>
    internal void UseModelListHandlerForCheck(System.Net.Http.HttpMessageHandler handler)
    {
        if (_chat is not null) _chat.ModelListHttp = new System.Net.Http.HttpClient(handler);
    }

    /// <summary>
    /// Runs the model catalog's refresh handler through the same path as its dialog button.
    /// </summary>
    /// <param name="providerId">Provider to refresh; a missing group is a no-op rather than an exception.</param>
    internal async Task RefreshModelsForCheckAsync(string providerId)
    {
        var provider = _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return;
        await CreateModelCatalogWindow(provider).RefreshForCheckAsync();
    }

    internal ModelCatalogWindow? ModelCatalogForCheck(string providerId)
        => _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId) is { } provider
            ? CreateModelCatalogWindow(provider)
            : null;

    /// <summary>
    /// Scrolls to the second provider group, so a screenshot shows that the groups actually stack rather than
    /// only that one of them renders. A feature that puts every provider in the same single card would look
    /// identical to this one in a picture of the first group alone.
    /// </summary>
    internal void ScrollToSecondGroupForCheck()
    {
        Avalonia.Visual? node = ProviderGroupList;
        while (node is not null and not ScrollViewer) node = node.GetVisualParent();
        if (node is not ScrollViewer viewer || viewer.Content is not Control content) return;

        // Skipped through GroupCards rather than the raw children: the list now holds rules between groups, so a
        // positional Skip(1) would land on the divider above the second group and scroll to the boundary
        // between them — the one place the picture cannot show a second card.
        var second = GroupCards.Skip(1).FirstOrDefault();
        if (second is null) return;

        var offset = second.TranslatePoint(new Point(0, 0), content) ?? new Point(0, 0);
        viewer.Offset = new Vector(0, Math.Max(0, offset.Y - 40));
    }

    /// <summary>
    /// Scrolls the provider card into view so a screenshot shows it. The page is one long
    /// <c>ScrollViewer</c>, and the group list sits well below the fold — without this a captured page image
    /// simply does not contain the thing being reviewed.
    /// </summary>
    internal void ScrollToProviderCardForCheck()
    {
        // Walking up to the ScrollViewer (rather than naming it) keeps the page free of a screenshot-only
        // reference; the page has exactly one, and its child is the card stack.
        Avalonia.Visual? node = ProviderGroupList;
        while (node is not null and not ScrollViewer) node = node.GetVisualParent();

        if (node is ScrollViewer viewer && viewer.Content is Control content)
        {
            // Bring the group list's own top into view: the card heading above it is what makes the picture
            // legible, so the offset is pulled up by the height of that heading and hint.
            var offset = ProviderGroupList.TranslatePoint(new Point(0, 0), content) ?? new Point(0, 0);
            viewer.Offset = new Vector(0, Math.Max(0, offset.Y - 150));
        }
    }

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
        internal const string NoCredential = "provider-no-credential";
        internal const string Affiliate = "provider-affiliate";
    }
}
