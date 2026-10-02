using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

// 模块选择只表达用户意图；安装状态必须来自私有目录和安装凭据。
public sealed class ModuleWindow : Window
{
    private readonly EngineModules modules;
    private readonly string root;
    private readonly ComboBox enginePicker = new();
    private readonly StackPanel rows = new();
    private readonly TextBlock totals = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button install = new();
    private readonly Dictionary<string, CheckBox> choices = [];
    private readonly Dictionary<string, List<CheckBox>> dependencies = [];
    private readonly Dictionary<string, CheckBox> commonChoices = [];
    private static readonly string[] CommonIds = ["cmake", "ninja", "git", "axslcc", "nuget"];
    private static string PackageName(string id) => id switch { "cmake" => "CMake", "ninja" => "Ninja", "git" => "Git", "android-ndk" => "Android NDK", "jdk" => "OpenJDK", "gradle" => "Gradle", "android-platform" => "Android SDK Platform", "android-build-tools" => "Android SDK Build Tools", "android-platform-tools" => "Android Platform Tools / ADB", "python-full" => "Python", "node" => "Node.js", "emscripten" => "Emscripten", "nuget" => "NuGet", _ => id };
    public EngineEntry SelectedEngine => (EngineEntry)enginePicker.SelectedItem;
    public string[] SelectedIds => choices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
    private static bool Chinese => Texts.Language == "zh-CN";
    public static string ModuleName(string id) => id switch
    {
        "windows" => Chinese ? "Windows 构建支持 (x64)" : "Windows Build Support (x64)",
        "android" => Chinese ? "Android 构建支持" : "Android Build Support",
        "web" => Chinese ? "WebAssembly 构建支持" : "WebAssembly Build Support",
        "ios" => Chinese ? "iOS 构建支持" : "iOS Build Support", "tvos" => Chinese ? "tvOS 构建支持" : "tvOS Build Support",
        "macos" => Chinese ? "macOS 构建支持" : "macOS Build Support", "linux" => Chinese ? "Linux 构建支持" : "Linux Build Support",
        "uwp" => "UWP / Xbox", _ => id
    };
    public static string Size(long? value) => value is null ? "—" : value >= 1024L * 1024 * 1024
        ? $"{value / (1024d * 1024 * 1024):0.00} GB" : $"{value / (1024d * 1024):0.00} MB";
    private static TextBlock Label(string text, double size = 13, string color = "#E4E4E4") => new()
        { Text = text, FontSize = size, Foreground = (Brush)new BrushConverter().ConvertFromString(color)!, TextWrapping = TextWrapping.Wrap };
    public ModuleWindow(EngineModules modules, string root, IEnumerable<EngineEntry> engines, EngineEntry engine)
    {
        this.modules = modules; this.root = root;
        Title = Texts.Get("AddModules"); Width = 1100; Height = 810; MinWidth = 900; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        var layout = new Grid { Background = Background }; Content = layout;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel { Margin = new Thickness(28, 24, 28, 18) };
        top.Children.Add(Label(Texts.Get("AddModules"), 24));
        var engineRow = new DockPanel { Margin = new Thickness(0, 16, 0, 12) };
        var engineLabel = Label(Texts.Get("EngineVersion")); engineLabel.Margin = new Thickness(0, 0, 20, 0);
        engineRow.Children.Add(engineLabel);
        enginePicker.Width = 330; enginePicker.HorizontalAlignment = HorizontalAlignment.Left;
        enginePicker.ItemsSource = engines.ToArray(); engineRow.Children.Add(enginePicker);
        top.Children.Add(engineRow); top.Children.Add(Label(Texts.Get("ModulesHint"), 12, "#AAAAAA"));
        layout.Children.Add(top);
        var body = new StackPanel();
        body.Children.Add(Columns(Label(Texts.Get("Platform"), 13), Label(Texts.Get("Status"), 12), Label(Texts.Get("DownloadSize"), 12), Label(Texts.Get("DiskSize"), 12), "#292929"));
        body.Children.Add(rows);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20, 0, 20, 0) };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var footer = new Grid { Margin = new Thickness(28, 18, 28, 22) };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var notes = new StackPanel(); notes.Children.Add(totals);
        var hint = Label(Texts.Get("DependencyHint"), 11, "#999999"); hint.Margin = new Thickness(0, 7, 18, 0); notes.Children.Add(hint); footer.Children.Add(notes);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var back = new Button { Content = Texts.Get("Back"), IsCancel = true }; back.Click += (_, _) => Close(); buttons.Children.Add(back);
        install.Content = Texts.Get("InstallSelected"); install.Style = (Style)FindResource("Primary");
        install.Click += (_, _) => { DialogResult = true; }; buttons.Children.Add(install);
        Grid.SetColumn(buttons, 1); footer.Children.Add(buttons); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        enginePicker.SelectionChanged += (_, _) => Populate();
        enginePicker.SelectedItem = engine;
    }
    private static Border Columns(UIElement title, UIElement status, UIElement download, UIElement disk, string background)
    {
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(215), new GridLength(125), new GridLength(125) }) grid.ColumnDefinitions.Add(new() { Width = width });
        var items = new[] { title, status, download, disk };
        for (var i = 0; i < items.Length; i++) { Grid.SetColumn(items[i], i); grid.Children.Add(items[i]); }
        return new Border { Background = (Brush)new BrushConverter().ConvertFromString(background)!, Padding = new Thickness(18, 14, 18, 14), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58)), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }
    private void Populate()
    {
        rows.Children.Clear(); choices.Clear(); dependencies.Clear(); commonChoices.Clear();
        try
        {
            var engine = SelectedEngine;
            var selected = modules.Load(engine).ModuleIds;
            var packages = modules.Packages();
            var commonRows = new StackPanel();
            foreach (var id in CommonIds)
            {
                var package = packages[id]; var present = modules.IsPackageInstalled(package);
                var indicator = new CheckBox { Content = $"{PackageName(id)}  {package.Version}", IsEnabled = false, Foreground = Brushes.LightGray, Margin = new Thickness(24, 0, 0, 0) };
                commonChoices.Add(id, indicator);
                commonRows.Children.Add(Columns(indicator, Label(Texts.Get(present ? "ModuleInstalled" : "ModuleMissing"), 11, "#999999"), Label(Size(package.DownloadBytes), 12), Label(Size(package.InstalledBytes), 12), "#222222"));
            }
            rows.Children.Add(new Expander { Header = Chinese ? "公共开发工具 · 随平台自动选择" : "Shared development tools · selected by platforms", Content = commonRows, Margin = new Thickness(18, 10, 0, 10), Foreground = Brushes.LightGray });
            foreach (var definition in modules.ForEngine(engine))
            {
                var deferred = !definition.Hosts.Contains(BuildTargets.Host) || definition.Packages.Length + definition.Installers.Length == 0;
                var installed = !deferred && definition.Packages.All(id => modules.IsPackageInstalled(packages[id])) && definition.Installers.All(modules.IsInstallerPresent);
                var choice = new CheckBox { Content = ModuleName(definition.Id), Foreground = Brushes.White, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center, IsChecked = selected.Contains(definition.Id) || (!modules.HasSelection(engine) && installed) };
                choices.Add(definition.Id, choice); dependencies.Add(definition.Id, []);
                var modulePackages = definition.Packages.Select(id => packages[id]).ToArray();
                var download = deferred ? "—" : installed ? Size(0) : Size(modulePackages.Any(p => p.DownloadBytes == null) ? null : modulePackages.Where(p => !modules.IsPackageInstalled(p)).Sum(p => p.DownloadBytes));
                var disk = deferred ? "—" : installed ? Size(0) : Size(modulePackages.Any(p => p.InstalledBytes == null) ? null : modulePackages.Where(p => !modules.IsPackageInstalled(p)).Sum(p => p.InstalledBytes));
                if (!installed && definition.Installers.Length > 0) { download += " +"; disk += " +"; }
                var status = definition.Id == "uwp" ? Texts.Get("UwpPending") : deferred ? Texts.Get("ModuleExternal") : Texts.Get(installed ? "ModuleInstalled" : "ModuleMissing");
                rows.Children.Add(Columns(choice, Label(status, 11, installed ? "#70D7AF" : "#B0B0B0"), Label(download), Label(disk), "#252525"));
                var childRows = new StackPanel();
                foreach (var package in modulePackages.Where(package => !CommonIds.Contains(package.Id)))
                {
                    var present = modules.IsPackageInstalled(package);
                    var version = package.Id == "android-platform" ? (Chinese ? "API 36 · 修订 2" : "API 36 · Revision 2") : package.Version;
                    var indicator = new CheckBox { Content = $"{PackageName(package.Id)}  {version}", IsChecked = choice.IsChecked, IsEnabled = false, Foreground = Brushes.LightGray, Margin = new Thickness(24, 0, 0, 0) };
                    dependencies[definition.Id].Add(indicator);
                    childRows.Children.Add(Columns(indicator, Label(Texts.Get(present ? "ModuleInstalled" : "ModuleMissing"), 11, "#999999"), Label(Size(package.DownloadBytes), 12), Label(Size(package.InstalledBytes), 12), "#222222"));
                }
                foreach (var id in definition.Installers)
                    childRows.Children.Add(Columns(Label("     " + (id == "msvc" ? "MSVC Build Tools 17.14" : "Windows SDK 10.0.26100")), Label(Texts.Get(modules.IsInstallerPresent(id) ? "ModuleInstalled" : "ModuleMissing"), 11), Label(Texts.Get("ModuleUnknownSize"), 11), Label("—"), "#222222"));
                var detail = definition.Id switch
                {
                    "android" => Texts.Get("AndroidNativeNote"), "uwp" => Texts.Get("UwpPending"),
                    "ios" or "tvos" or "macos" => "macOS · Hub tools/Xcode.app · " + Texts.Get("ModuleExternal"),
                    "linux" => "Linux · Hub tools/gcc · " + Texts.Get("ModuleExternal"), _ => ""
                };
                if (detail.Length > 0) childRows.Children.Add(new Border { Padding = new Thickness(42, 9, 18, 12), Child = Label(detail, 11, "#AAAAAA") });
                var expander = new Expander { Header = Chinese ? "依赖与构建条件" : "Dependencies and build requirements", Content = childRows, IsExpanded = definition.Id == "android", Margin = new Thickness(20, 4, 0, 8), Foreground = Brushes.LightGray };
                rows.Children.Add(expander);
                choice.Checked += (_, _) => Changed(definition.Id); choice.Unchecked += (_, _) => Changed(definition.Id);
            }
            install.IsEnabled = true; UpdateTotals();
        }
        catch (InvalidOperationException) { rows.Children.Add(Label(Texts.Get("ModuleUnsupported"))); install.IsEnabled = false; totals.Text = ""; }
    }
    private void Changed(string id)
    {
        foreach (var dependency in dependencies[id]) dependency.IsChecked = choices[id].IsChecked;
        UpdateTotals();
    }
    private void UpdateTotals()
    {
        var plan = modules.Plan(SelectedEngine, SelectedIds);
        var required = modules.ForEngine(SelectedEngine).Where(module => SelectedIds.Contains(module.Id)).SelectMany(module => module.Packages).ToHashSet();
        foreach (var pair in commonChoices) pair.Value.IsChecked = required.Contains(pair.Key);
        var available = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        totals.Text = Texts.Get("ModuleTotals") + Size(plan.DownloadBytes) + " / " + Size(plan.InstalledBytes)
            + (plan.HasUnknownSize ? " + " + Texts.Get("ModuleUnknownSize") : "") + "\n" + Texts.Get("AvailableSpace") + Size(available);
    }
    public void CaptureEvidence(string path)
    {
        UpdateLayout();
        var body = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)body.ActualWidth, (int)body.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    public void CheckSelectionBehavior()
    {
        var choice = choices["android"]; var previous = choice.IsChecked;
        choice.IsChecked = false;
        if (SelectedIds.Contains("android") || dependencies["android"].Any(child => child.IsChecked == true)) throw new InvalidOperationException("Deselecting platform failed.");
        choice.IsChecked = true;
        if (!SelectedIds.Contains("android") || dependencies["android"].Any(child => child.IsChecked != true)) throw new InvalidOperationException("Platform dependencies were not selected.");
        choice.IsChecked = previous;
    }
}
