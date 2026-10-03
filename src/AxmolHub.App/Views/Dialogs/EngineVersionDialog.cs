using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 「选择引擎版本」对话框。纯代码构建，与 <see cref="BuildTargetDialog"/> 同一个路子
/// （这两个对话框都是一次性的小窗口，为它们单开一个 .axaml 不划算）。
///
/// 它存在的理由：以前引擎页那个按钮直接装清单里最新的 LTS，想装旧版本没有入口。
/// 而"每个项目锁定自己的引擎版本"是 Hub 的前提 —— 项目 A 锁 2.11.3、项目 B 锁 2.11.5
/// 是正常状态，所以**能装哪个版本**必须是用户的选择，而不是实现的副作用。
///
/// 列表来自 <see cref="EngineReleases"/>：启动时拉到的远端索引，或拉不到时的内置清单。
/// 不在代码里枚举版本 —— 版本从哪来是数据问题，不是代码问题。
/// 返回 <c>null</c> = 用户取消，此时一个字节都不该下载。
/// </summary>
public sealed class EngineVersionDialog : Window
{
    private readonly ListBox _versions = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) };
    private readonly Button _install;

    public EngineRelease? Selected => _versions.SelectedItem as EngineRelease;

    private EngineVersionDialog(IReadOnlyList<EngineRelease> releases, string? preferredVersion, bool fromRemoteIndex)
    {
        Title = HubStrings.Get("ChooseEngineVersion");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("ChooseEngineVersion"), FontSize = 18 });
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("EngineVersionHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        // 如实告诉用户这份列表的出处。回落不是故障，但"列表从哪来"必须可见 ——
        // 否则"有 6 个版本"和"只有内置那几个版本"看起来一模一样。
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(fromRemoteIndex ? "EngineIndexRemote" : "EngineIndexBuiltIn"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(_versions);
        panel.Children.Add(_detail);
        panel.Children.Add(_warning);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        _install = new Button { Content = HubStrings.Get("InstallSelected"), IsDefault = true, Classes = { "primary" } };
        _install.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_install);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };

        _versions.ItemsSource = releases;
        // 预选：调用方指定的版本，否则清单里最新的那个。找不到就退回第一项 ——
        // 一个都不预选会让"继续"按钮毫无来由地失效。
        var preferred = preferredVersion is null ? null : releases.FirstOrDefault(release => release.Version == preferredVersion);
        _versions.SelectedItem = preferred ?? releases.FirstOrDefault();
        _versions.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (Selected is not { } release)
        {
            _detail.Text = HubStrings.Get("EngineVersionNone");
            _warning.Text = "";
            _warning.IsVisible = false;
            _install.IsEnabled = false;
            return;
        }

        _install.IsEnabled = true;
        _detail.Text = string.Join("\n", new[]
        {
            HubStrings.Get("Channel") + "：" + release.Channel,
            HubStrings.Get("DownloadSize") + "：" + Size(release.Package.DownloadBytes),
            HubStrings.Get(release.Installed ? "Installed" : "Missing"),
        });

        // 没验过模块清单的版本仍然允许安装 —— 引擎本身是自洽的，模块清单只影响
        // "添加模块"那一步。但必须在这里说出来，否则用户装完才发现加不了模块。
        _warning.Text = HubStrings.Get("EngineModulesUnverified");
        _warning.IsVisible = !release.ModulesVerified;
    }

    public static string Size(long? value) => value is null ? "—" : value >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{value / (1024d * 1024 * 1024):0.00} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{value / (1024d * 1024):0.00} MB");

    public static async Task<EngineRelease?> PickAsync(Window? owner, EngineReleases catalog, string? preferredVersion)
    {
        var releases = catalog.All();
        if (releases.Count == 0) return null;
        var dialog = new EngineVersionDialog(releases, preferredVersion, catalog.UsingRemoteIndex);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        return result == HubDialogResult.Ok ? dialog.Selected : null;
    }
}
