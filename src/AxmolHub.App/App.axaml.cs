using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AxmolHub.Core;

namespace AxmolHub.App;

public partial class App : Application
{
    /// <summary>命令行与宿主目录。由 <see cref="Program.Main"/> 在 AppBuilder 之前填好。</summary>
    internal static HubHostOptions Options { get; set; } = HubHostOptions.Parse([]);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        try
        {
            Start(desktop);
        }
        catch (Exception ex)
        {
            // 这条路径上主窗口还建不起来，所以 HubDialog 必须支持 owner 为 null
            // （WPF 版这里用的 MessageBox 同样是自立的）。见 Views/HubDialog.axaml。
            _ = HubDialog.ShowAsync(null, "Axmol Hub 启动失败", ex.ToString());
            desktop.Shutdown(1);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 读设置要在**任何窗口构造之前**，因为 HubStrings.Apply 必须先把文案灌进资源字典：
        // DynamicResource 是按 key 现查的，窗口先建好再灌会留下一批解析为 null 的标签，
        // 而且不会报错，只显示空白。WPF 版是同样的顺序。
        var preferencesStore = new PreferencesStore(Options.PreferencesPath);
        var preferences = preferencesStore.Load();

        // 验收模式必须是**封闭**的：断言与报告里都是具体文案，若跟着用户的语言设置走，
        // 同一个二进制在这台机器上通过、在那台机器上失败。所以验收模式强制中文起步
        // （外壳自检内部会真切一次语言再切回来，那部分由它自己负责收尾）。
        if (Options.VerifyShellReport is not null || Options.VerifyOpsReport is not null)
        {
            preferences = new HubPreferences();
        }

        HubStrings.Apply(preferences.Language, this);

        // P4 的三种自检/验收模式各用一个专用窗口；产品模式才开主窗口。
        if (Options.VerifyThemeReport is { } themeReport)
        {
            var gallery = new ControlGalleryWindow();
            desktop.MainWindow = gallery;
            gallery.RunThemeVerification(desktop, themeReport);
            return;
        }

        if (Options.VerifyFoundationReport is { } foundationReport)
        {
            var check = new FoundationCheckWindow();
            desktop.MainWindow = check;
            check.Run(desktop, foundationReport);
            return;
        }

        if (Options.VerifyShellReport is { } shellReport)
        {
            var check = new ShellCheckWindow();
            desktop.MainWindow = check;
            check.Run(desktop, shellReport);
            return;
        }

        if (Options.VerifyOpsReport is { } opsReport)
        {
            var check = new OpsCheckWindow();
            desktop.MainWindow = check;
            check.Run(desktop, opsReport, Options.VerifyOpsEngines);
            return;
        }

        if (Options.Gallery)
        {
            desktop.MainWindow = new ControlGalleryWindow();
            return;
        }

        // 数据根的三级优先（命令行 > 设置文件 > 默认）与 WPF 版一致，见 HubHostOptions.ResolveDataRoot。
        // 设置存储与设置对象一起交给外壳：设置页改语言要落盘，必须写回**同一个**存储与对象，
        // 否则外壳里缓存的 preferences 会与磁盘分叉。
        var window = new MainWindow(Options.ResolveDataRoot(preferences), preferencesStore, preferences);
        desktop.MainWindow = window;

        // 两个截图开关互斥：--smoke 截完就退出，--smoke-pages 要连着切八次页面。
        // 同时传时以 --smoke-pages 为准（它覆盖面更大），不报错。
        if (Options.SmokePagesDirectory is { } pagesDirectory)
        {
            PageShots.Attach(window, desktop, pagesDirectory);
        }
        else if (Options.SmokeImagePath is { } smokeImage)
        {
            SmokeRunner.Attach(window, desktop, smokeImage);
        }
    }
}
