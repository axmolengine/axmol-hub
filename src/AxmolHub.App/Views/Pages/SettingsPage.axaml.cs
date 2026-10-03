using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
        ChooseDataDirectoryButton.Click += async (_, _) => await ChooseDataDirectoryAsync();
        ChooseProjectDirectoryButton.Click += async (_, _) => await ChooseProjectDirectoryAsync();
        OpenDataFolderButton.Click += (_, _) => _openFolder(_workspace.Store.Root);
        SelectVisualStudioButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: true);
        SelectCodeButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: false);

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
            SelectLanguage(HubStrings.Language);
            SelectTheme(_preferences.Theme);
            DataLocation.Text = _workspace.Store.Root;
            DefaultProjectLocation.Text = _preferences.ProjectDirectory ?? HubStrings.Get("NotSelected");

            var state = _workspace.State;
            var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
            DefaultEngine.Text = engine?.ToString() ?? HubStrings.Get("NoDefault");
            VisualStudioLocation.Text = Display(state.VisualStudioExecutable);
            CodeLocation.Text = Display(state.CodeExecutable);
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
}
