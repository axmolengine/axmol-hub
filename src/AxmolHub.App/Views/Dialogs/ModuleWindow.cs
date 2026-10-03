using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 平台准备窗口（WPF 版叫「添加模块」）。
///
/// 勾选表达的是**用户想让哪个平台可用**；真正把它们准备好的是引擎的
/// <c>setup.ps1 -p &lt;platform&gt;</c>，确认后由 <see cref="HubWorkspace.ChooseModulesAsync"/> 逐个执行。
///
/// 相比 WPF 版刻意去掉的东西：公共工具展开区、逐包/逐安装器明细、下载与磁盘体积。
/// 那些数据来自 Hub 自持的工具链包清单，而安装已经是引擎的事 —— 留一份副本只会漂移。
/// </summary>
public sealed class ModuleWindow : Window
{
    private readonly EngineModules _modules;
    private readonly ComboBox _enginePicker = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _totals = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _install = new();
    private readonly Dictionary<string, CheckBox> _choices = [];

    public EngineEntry SelectedEngine => (EngineEntry)_enginePicker.SelectedItem!;

    public string[] SelectedIds => _choices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();

    private static bool Chinese => HubStrings.Language == HubTexts.DefaultLanguage;

    public static string ModuleName(string id) => id switch
    {
        "windows" => Chinese ? "Windows 构建支持 (x64)" : "Windows Build Support (x64)",
        "android" => Chinese ? "Android 构建支持" : "Android Build Support",
        "web" => Chinese ? "WebAssembly 构建支持" : "WebAssembly Build Support",
        "ios" => Chinese ? "iOS 构建支持" : "iOS Build Support",
        "tvos" => Chinese ? "tvOS 构建支持" : "tvOS Build Support",
        "macos" => Chinese ? "macOS 构建支持" : "macOS Build Support",
        "linux" => Chinese ? "Linux 构建支持" : "Linux Build Support",
        "uwp" => "UWP / Xbox",
        _ => id,
    };

    private static TextBlock Label(string text, double size = 13, string color = "#E4E4E4") => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush.Parse(color),
        TextWrapping = TextWrapping.Wrap,
    };

    public ModuleWindow(EngineModules modules, IEnumerable<EngineEntry> engines, EngineEntry engine)
    {
        _modules = modules;

        Title = HubStrings.Get("AddModules");
        Width = 900;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        Foreground = Brushes.White;
        FontSize = 13;

        var layout = new Grid { Background = Background };
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Content = layout;

        var top = new StackPanel { Margin = new Thickness(28, 24, 28, 18) };
        top.Children.Add(Label(HubStrings.Get("AddModules"), 24));
        var engineRow = new DockPanel { Margin = new Thickness(0, 16, 0, 12) };
        var engineLabel = Label(HubStrings.Get("EngineVersion"));
        engineLabel.Margin = new Thickness(0, 0, 20, 0);
        engineRow.Children.Add(engineLabel);
        _enginePicker.Width = 330;
        _enginePicker.HorizontalAlignment = HorizontalAlignment.Left;
        _enginePicker.ItemsSource = engines.ToArray();
        engineRow.Children.Add(_enginePicker);
        top.Children.Add(engineRow);
        top.Children.Add(Label(HubStrings.Get("EngineSetupHint"), 12, "#AAAAAA"));
        layout.Children.Add(top);

        var body = new StackPanel();
        body.Children.Add(Columns(Label(HubStrings.Get("Platform"), 13), Label(HubStrings.Get("Status"), 12), "#292929"));
        body.Children.Add(_rows);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20, 0, 20, 0) };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(28, 18, 28, 22) };
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var notes = new StackPanel();
        notes.Children.Add(_totals);
        var hint = Label(HubStrings.Get("ModulesHint"), 11, "#999999");
        hint.Margin = new Thickness(0, 7, 18, 0);
        notes.Children.Add(hint);
        footer.Children.Add(notes);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var back = new Button { Content = HubStrings.Get("Back"), IsCancel = true };
        back.Click += (_, _) => Close(HubDialogResult.Cancel);
        buttons.Children.Add(back);
        _install.Content = HubStrings.Get("InstallSelected");
        _install.Classes.Add("primary");
        _install.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(_install);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        _enginePicker.SelectionChanged += (_, _) => Populate();
        _enginePicker.SelectedItem = engine;
    }

    private static Border Columns(Control title, Control status, string background)
    {
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(300) })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(width));
        }

        var items = new[] { title, status };
        for (var i = 0; i < items.Length; i++)
        {
            Grid.SetColumn(items[i], i);
            grid.Children.Add(items[i]);
        }

        return new Border
        {
            Background = Brush.Parse(background),
            Padding = new Thickness(18, 14, 18, 14),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private void Populate()
    {
        _rows.Children.Clear();
        _choices.Clear();

        try
        {
            var engine = SelectedEngine;
            var selected = _modules.Load(engine).ModuleIds;

            foreach (var definition in _modules.ForEngine(engine))
            {
                var hostReady = definition.Hosts.Contains(BuildTargets.Host);
                var platform = AxmolCommandMap.PlatformForModule(definition.Id);
                var choice = new CheckBox
                {
                    Content = ModuleName(definition.Id),
                    Foreground = hostReady ? Brushes.White : Brushes.Gray,
                    FontSize = 15,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    // 宿主不支持或引擎没有对应平台名时不提供勾选：勾了也准备不了。
                    IsEnabled = hostReady && platform is not null,
                    IsChecked = selected.Contains(definition.Id),
                };
                _choices.Add(definition.Id, choice);

                var status = definition.Id == "uwp" ? HubStrings.Get("UwpPending")
                    : hostReady && platform is not null ? "setup.ps1 -p " + platform
                    : HubStrings.Get("ModuleExternal");
                _rows.Children.Add(Columns(choice, Label(status, 11, hostReady ? "#B0B0B0" : "#888888"), "#252525"));

                choice.IsCheckedChanged += (_, _) => UpdateTotals();
            }

            _install.IsEnabled = true;
            UpdateTotals();
        }
        catch (InvalidOperationException)
        {
            _rows.Children.Add(Label(HubStrings.Get("ModuleUnsupported")));
            _install.IsEnabled = false;
            _totals.Text = "";
        }
    }

    private void UpdateTotals()
    {
        var chosen = SelectedIds;
        _totals.Text = chosen.Length == 0
            ? HubStrings.Get("ModulesHint")
            : string.Join(" · ", chosen.Select(id => ModuleName(id)));
    }
}
