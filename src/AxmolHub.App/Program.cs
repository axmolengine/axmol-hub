using Avalonia;
using Velopack;

namespace AxmolHub.App;

internal static class Program
{
    // Avalonia 的入门代码必须在 AppMain 之前完成，入口本身是 STA。
    // 与 WPF 版的差别：官方模板天生就是 [STAThread] Main，不必像 WPF 那样把 App.xaml 从
    // ApplicationDefinition 改成 Page 再手工设 StartupObject。
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 安装/更新/卸载时会用 --veloapp-* 拉起本进程做钩子回调，
        // 必须在任何 UI 初始化之前处理并退出，否则安装过程中会弹出主窗口。
        // 放在最前面（连编码设置都要排在它后面）：这条路径不能有任何副作用。
        VelopackApp.Build().Run();

        // 验收模式的报告走 stdout，内容含中文。不设成 UTF-8 的话在 Windows 上是乱码
        // （P5 自检第一次跑就踩到了：断言结果整段不可读，等于没有证据）。
        // WinExe 在没有控制台时（双击启动）设置编码会抛 IOException，所以这里必须兜住 ——
        // 拿不到可读日志是遗憾，因此起不来就是故障了。
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (IOException)
        {
        }

        // 参数解析必须在 AppBuilder 之前：窗口选型由它决定。
        App.Options = HubHostOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 视觉设计器也会调用这个方法，不要删。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
