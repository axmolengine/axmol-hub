using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 设置页（WPF 版的 <c>SettingsPage</c>）。
///
/// 除了"把设置搬过来"，它还承担一个验证职责：**本地化到底是不是真的生效**。
/// 语言切换要求就地重灌资源字典，并让**已经存在的控件**换文字 ——
/// 这件事读代码判断不了（Avalonia 的 <c>DynamicResource</c> 会不会跟着资源字典变更
/// 重新解析，只能实测），所以 <c>--verify-shell</c> 会真切一次语言并读回控件的文字。
///
/// 切换数据根由外壳接管（<see cref="MainWindow.SwitchDataRoot"/>）：它只重建工作区与页面，
/// **不重建窗口** —— 因此这件事不依赖外壳形态。页面在这里只负责"问用户要一个目录"。
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private readonly PreferencesStore _preferencesStore;
    private readonly HubPreferences _preferences;
    private readonly Action<string> _openFolder;

    /// <summary>换语言后外壳需要重算自己那些**命令式**的文案（标题、左下角版本行）。</summary>
    private readonly Action _languageChanged;

    /// <summary>请求外壳切换数据根。返回是否真的换了。</summary>
    private readonly Func<string, bool> _switchDataRoot;

    /// <summary>抑制初始化期间的 <c>SelectionChanged</c>：WPF 版同样有一个 <c>preferencesReady</c> 闸门。</summary>
    private bool _ready;

    /// <summary>供 XAML 加载器与设计预览使用（缺它会报 AVLN3001）。</summary>
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
        ChooseDataDirectoryButton.Click += async (_, _) => await ChooseDataDirectoryAsync();
        ChooseProjectDirectoryButton.Click += async (_, _) => await ChooseProjectDirectoryAsync();
        OpenDataFolderButton.Click += (_, _) => _openFolder(_workspace.Store.Root);
        SelectVisualStudioButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: true);
        SelectCodeButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: false);

        // DataRootNote 的文案走 XAML 的 {DynamicResource}，**不要**在这里命令式赋值：
        // 命令式赋的值在换语言时不会自己变，而自检正是靠读这个控件的文字来判断本地化生不生效的。
        Reload();
    }

    /// <summary>
    /// XAML 里声明的语言标记。语言映射住在 XAML 的 <c>Tag</c> 上，这里只是把它读出来 ——
    /// 断言靠它确认"下拉项声明的语言"和"代码认识的受支持语言"没有分叉。
    /// </summary>
    internal string[] DeclaredLanguages
        => LanguagePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>当前选中的语言标记。未知/未选一律回落中文（与 <see cref="HubTexts.Normalize"/> 同规矩）。</summary>
    internal string SelectedLanguage
        => LanguagePicker.SelectedItem is ComboBoxItem { Tag: string tag } && HubTexts.IsSupported(tag)
            ? tag
            : HubTexts.DefaultLanguage;

    /// <summary>
    /// 编辑器可执行文件的校验规则（WPF 版在每个分支里各写一遍）。
    /// 抽成纯静态函数是为了**能被断言** —— 真去弹文件选择器没法进自动化，
    /// 而"选错文件要拒绝"恰是最容易漏、也最难在人工点几次里发现的分支。
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
            SelectLanguage(HubStrings.Language);
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
    /// 按下拉项自己声明的标记选中一项。找不到就选第一项，不留一个"什么都没选"的空状态。
    /// 验收代码直接调它 —— 与导航用 <c>NavSettings.IsChecked = true</c> 同理：
    /// 走真实事件路径，而不是绕开事件去改内部状态。
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
    /// 切换语言：写设置 → 重灌资源字典 → 已存在的控件跟着换文字。
    ///
    /// 顺序不能反：先灌字典再写设置，写失败时会留下"界面已换语言但设置没存"的状态，
    /// 下次启动又变回去，看起来像设置丢失。
    ///
    /// 重灌之后再通知外壳：外壳与页面里有**命令式**写入的文案，DynamicResource 管不到它们。
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

            // 就地重灌：DynamicResource 会跟着资源字典变更重新解析，因此
            // 已经建好的控件（以及别的窗口）也会换文字，不需要重建窗口。
            HubStrings.Apply(language, Application.Current!);
            _languageChanged();
        }
        catch (Exception ex)
        {
            // 失败要把界面扳回去，否则会显示一个与设置文件不符的语言。
            _ready = false;
            _preferences.Language = previous;
            HubStrings.Apply(previous, Application.Current!);
            SelectLanguage(previous);
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
            // 切换成功后**本页实例就废弃了**（外壳清空页面缓存并重建），
            // 所以这里不能再去碰自己的任何控件：那会读到一个已经 Dispose 的工作区。
            // 成功路径到此为止，失败路径才需要弹窗 —— 那时本页还活着。
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
            // 与数据根不同，项目父目录**只存路径不切换**：它不承载状态，切换不需要重建窗口。
            // 落盘与校验都在工作区里，失败时它会把偏好回滚到上一个值。
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

        // 标题与拒绝文案都点名具体可执行文件：选错了要一眼看出该选哪个，而不是只被告知"选错了"。
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
