using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 「选择构建平台」对话框。WPF 版是 <c>MainWindow.PickBuildTarget</c> 里现场堆出来的窗口，
/// 这里独立成文件，因为它同时被「生成 / 运行」和验收程序用到。
///
/// 返回 <c>null</c> 表示用户取消 —— 与 WPF 版一致，**取消不允许改动项目元数据**：
/// 选平台本身会写 <c>.axmol-hub.json</c>，所以落笔推迟到真正开始构建时
/// （见 <see cref="HubWorkspace.BuildAsync"/> 里的 <c>BuildTargets.Select</c>）。
/// </summary>
public sealed class BuildTargetDialog : Window
{
    private readonly ComboBox _picker = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly ComboBox _configuration = new() { Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly ProjectEntry _project;

    private BuildTargetDialog(ProjectEntry project)
    {
        _project = project;
        Title = HubStrings.Get("ChooseBuildPlatform");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("ChooseBuildPlatform"), FontSize = 18 });
        panel.Children.Add(_picker);
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("BuildConfiguration") });
        panel.Children.Add(_configuration);
        panel.Children.Add(_hint);
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("BuildPlatformHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        var build = new Button { Content = HubStrings.Get("Continue"), IsDefault = true, Classes = { "primary" } };
        build.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(build);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };

        _picker.ItemsSource = BuildTargets.All;
        _picker.SelectedItem = BuildTargets.Get(project.Platform);
        _picker.SelectionChanged += (_, _) => RefreshConfigurations();
        RefreshConfigurations();
    }

    private void RefreshConfigurations()
    {
        var android = (_picker.SelectedItem as BuildTarget)?.Family == "android";
        var previous = _configuration.SelectedItem as string ?? _project.Configuration;
        _configuration.ItemsSource = BuildConfigurations.All;
        _configuration.SelectedItem = BuildConfigurations.All.Contains(previous) ? previous : "Debug";
        _hint.Text = HubStrings.Get(android ? "AndroidReleasePending" : "ConfigurationHint");
    }

    public static async Task<(BuildTarget Target, string Configuration)?> PickAsync(Window? owner, ProjectEntry project)
    {
        var dialog = new BuildTargetDialog(project);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        return result == HubDialogResult.Ok && dialog._picker.SelectedItem is BuildTarget target
            && dialog._configuration.SelectedItem is string configuration
            ? (target, configuration)
            : null;
    }
}
