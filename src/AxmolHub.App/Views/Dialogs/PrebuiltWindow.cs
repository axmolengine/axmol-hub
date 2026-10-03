using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 「预编译库设置」对话框（每项目）。纯代码构建，跟随 <see cref="AndroidReleaseWindow"/> 的路子。
///
/// 它要回答用户的三个问题：
/// <list type="number">
/// <item>现在这个项目到底链不链预编译库；</item>
/// <item>引擎那一份在**当前目标 + 配置**下能不能用、在哪个目录；</item>
/// <item>不能用的话该怎么办（去引擎页构建）。</item>
/// </list>
/// 第 2 条尤其重要：引擎在目录不可用时会**静默退回源码构建**（不报错），
/// 所以「我勾了但到底生效没有」必须在这里说清楚，而不是等构建完才发现慢得离谱。
/// </summary>
public sealed class PrebuiltWindow : Window
{
    private readonly CheckBox _enabled;
    private readonly Action<string>? _navigate;

    public bool Enabled => _enabled.IsChecked == true;

    public PrebuiltWindow(ProjectEntry project, EngineEntry engine, BuildTarget target,
        PrebuiltAvailability availability, bool enabled, Action<string>? navigate)
    {
        _navigate = navigate;

        Title = HubStrings.Get("PrebuiltSettings");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("PrebuiltSettings"), FontSize = 18 });
        panel.Children.Add(new TextBlock
        {
            Text = project.Name + "\n" + engine + " · " + target.Name + " · " + project.Configuration,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _enabled = new CheckBox
        {
            Content = HubStrings.Get("UsePrebuilt"),
            IsChecked = enabled,
            Margin = new Thickness(0, 16, 0, 0),
            // 目标平台不支持时不给勾：勾了也只会让构建失败。
            IsEnabled = availability.Status != PrebuiltStatus.PlatformUnsupported,
        };
        panel.Children.Add(_enabled);

        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("PrebuiltHint"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        // 解析结论：就绪时给「平台 架构 配置 → 目录」，否则给精确原因。
        var status = availability.Usable
            ? string.Format(HubStrings.Get("PrebuiltReadyFormat"), availability.Label, availability.RelativeDirectory)
            : HubStrings.Get(availability.TextKey);
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("PrebuiltStatus") + "：" + status,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = HubStrings.Get("Ok"), IsDefault = true, Classes = { "primary" } };
        ok.Click += (_, _) => Close(HubDialogResult.Ok);
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };

        // 不可用时把「怎么办」和入口一起给出来，省得用户自己找。
        if (!availability.Usable)
        {
            panel.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("PrebuiltUnavailableAction"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });

            var open = new Button { Content = HubStrings.Get("OpenEnginesPage"), Classes = { "quiet" }, Margin = new Thickness(0, 12, 0, 0) };
            open.Click += (_, _) =>
            {
                _navigate?.Invoke("Installs");
                Close(HubDialogResult.Cancel);
            };
            panel.Children.Add(open);
        }

        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };
    }
}
