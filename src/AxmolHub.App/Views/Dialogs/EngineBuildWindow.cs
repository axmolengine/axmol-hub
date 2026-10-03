using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 「构建引擎」确认窗口。纯代码构建，与 <see cref="EngineVersionDialog"/> 同一个路子。
///
/// 为什么必须先确认：这一步编译**整棵引擎**（数分钟到数十分钟、产物数 GB），
/// 而且引擎在缺工具链时会顺手触发自己的 setup —— 那会改用户级 PATH / AX_ROOT 并可能弹 UAC。
/// 这些代价不写出来，用户只会看到一个按钮点了没反应。
///
/// 同时让用户选配置：预编译库**按配置分目录**（<c>lib/&lt;Config&gt;</c>），
/// 项目构建时的配置必须与引擎构建的配置一致才能链接上。默认 Release（与引擎 CI 的 <c>-O3</c> 一致）。
///
/// 返回所选配置；<c>null</c> = 用户取消（此时一个字节都不该编译）。
/// </summary>
public sealed class EngineBuildWindow : Window
{
    private readonly ComboBox _configuration = new();
    private readonly string _release;
    private readonly string _debug;

    private string? Result => _configuration.SelectedItem as string;

    private EngineBuildWindow(EngineEntry engine, BuildTarget target)
    {
        _release = "Release";
        _debug = "Debug";

        Title = HubStrings.Get("BuildEngineConfirm");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("BuildEngineConfirm"), FontSize = 18 });

        var engineLabel = new TextBlock
        {
            Text = engine + "\n" + target.Name,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(engineLabel);

        // 代价必须显式说出来 —— 这是本次点击真正会发生的事。
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("EngineBuildCost"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var configurationRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var configurationLabel = new TextBlock { Text = HubStrings.Get("EngineBuildConfig"), VerticalAlignment = VerticalAlignment.Center };
        configurationLabel.Margin = new Thickness(0, 0, 12, 0);
        configurationRow.Children.Add(configurationLabel);
        _configuration.ItemsSource = new[] { _release, _debug };
        _configuration.SelectedItem = _release;
        _configuration.Width = 200;
        configurationRow.Children.Add(_configuration);
        panel.Children.Add(configurationRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        var start = new Button { Content = HubStrings.Get("Build"), IsDefault = true, Classes = { "primary" } };
        start.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };
    }

    /// <summary>返回所选配置；用户取消返回 <c>null</c>。</summary>
    public static async Task<string?> PickAsync(Window? owner, EngineEntry engine, BuildTarget target)
    {
        var dialog = new EngineBuildWindow(engine, target);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Result : null;
    }
}
