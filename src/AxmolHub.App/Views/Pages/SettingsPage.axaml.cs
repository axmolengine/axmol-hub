using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private readonly Action<string> _openFolder;

    /// <summary>After a language change the shell must recompute its own **imperative** copy (title, bottom-left version line).</summary>
    private readonly Action _languageChanged;

    /// <summary>Asks the shell to switch the data root. Returns whether it actually switched.</summary>
    private readonly Func<string, bool> _switchDataRoot;

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

    /// <summary>For the XAML loader and design-time preview (missing it raises AVLN3001).</summary>
    public SettingsPage()
    {
        _workspace = null!;
        _preferencesStore = null!;
        _preferences = new HubPreferences();
        _openFolder = _ => { };
        _languageChanged = () => { };
        _switchDataRoot = _ => false;

        InitializeComponent();
    }

    public SettingsPage(
        HubWorkspace workspace,
        PreferencesStore preferencesStore,
        HubPreferences preferences,
        Action<string> openFolder,
        Action languageChanged,
        Func<string, bool> switchDataRoot)
    {
        _workspace = workspace;
        _preferencesStore = preferencesStore;
        _preferences = preferences;
        _openFolder = openFolder;
        _languageChanged = languageChanged;
        _switchDataRoot = switchDataRoot;

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

        // Installed exactly once, on purpose — see SetDownloadTooltip for why re-assigning ToolTip.Tip
        // per progress tick is what used to make the tooltip impossible to keep on screen.
        ToolTip.SetTip(UpdateProgressHost, _speedTip);

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
    /// Switches theme: write settings → apply the variant. Same order as the language path (persist
    /// first), for the same reason: a failed write must leave the UI untouched rather than showing a
    /// theme the settings file doesn't have — it would silently revert on the next launch.
    ///
    /// Unlike the language path there is **nothing to notify the shell about**: the theme has no
    /// imperative copy, every token is a DynamicResource and re-resolves on its own, including in
    /// windows built long before the switch.
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

        try
        {
            _preferences.Theme = theme;
            _preferencesStore.Save(_preferences);
            ThemeService.Apply(theme);
        }
        catch (Exception ex)
        {
            // On failure, revert the UI, otherwise it would show a theme that disagrees with the settings file.
            _ready = false;
            _preferences.Theme = previous;
            ThemeService.Apply(previous);
            SelectTheme(previous);
            _ready = true;
            _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
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
            UpdateStatusLine.Text = string.Format(HubStrings.Get("UpdateDownloading"), service.DownloadPercent);
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
            UpdateStatusLine.Text = HubStrings.Get("UpdateReadyToRestart");
            // The bar stays on screen once the download is done, so its tooltip needs copy that is still
            // true — otherwise hovering it would keep repeating a speed that no longer means anything.
            SetDownloadTooltip(HubStrings.Get("UpdateReadyToRestart"));
            return;
        }

        // A failed download is reported right here instead of in a dialog: the whole update flow is
        // deliberately modal-free, and this card is where the user is already looking. The retry is the
        // "Download & restart" button (and any new check, which clears the error).
        if (service.DownloadError is { } error)
        {
            SetStatusLineError(true);
            UpdateStatusLine.Text = HubStrings.Get("UpdateDownloadFailed") + error;
            DownloadUpdateButton.IsVisible = true;
            return;
        }

        SetStatusLineError(false);

        var last = service.Last;
        UpdateStatusLine.Text = last?.Result switch
        {
            UpdateService.CheckResult.UpdateAvailable => string.Format(HubStrings.Get("UpdateReady"), last.Update!.TargetFullRelease.Version),
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
}
