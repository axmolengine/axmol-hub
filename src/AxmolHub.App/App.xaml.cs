using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxmolHub.Core;
using Velopack;

namespace AxmolHub.App;

public partial class App : Application
{
    /// <summary>
    /// Velopack 在更新时整体替换安装目录下的 current\，卸载时删除整个安装目录，
    /// 所以设置与数据根不能放在 AppContext.BaseDirectory 旁边。这里用每用户目录承载两者。
    /// 引擎与工具链是 GB 级的，放 LocalApplicationData 而不是漫游 AppData。
    /// </summary>
    private static string UserDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "AxmolHub");

    internal static string DefaultPreferencesPath => System.IO.Path.Combine(UserDirectory, "hub-settings.json");
    internal static string DefaultDataRoot => System.IO.Path.Combine(UserDirectory, "data");

    // Velopack 安装/更新/卸载时用 --veloapp-* 拉起本进程做钩子回调，
    // 必须在 WPF 初始化之前处理并退出，否则安装过程中会弹出主窗口。
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataIndex = Array.IndexOf(e.Args, "--data-root");
            var preferencesIndex = Array.IndexOf(e.Args, "--preferences");
            var preferencesPath = preferencesIndex >= 0 && preferencesIndex + 1 < e.Args.Length ? e.Args[preferencesIndex + 1]
                : DefaultPreferencesPath;
            var preferencesStore = new PreferencesStore(preferencesPath);
            var preferences = preferencesStore.Load();
            Texts.Apply(preferences.Language);
            var root = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1]
                : preferences.DataRoot ?? DefaultDataRoot;
            var window = new MainWindow(root, preferences, preferencesStore);
            MainWindow = window;
            window.Show();
            var smokeIndex = Array.IndexOf(e.Args, "--smoke");
            var allIndex = Array.IndexOf(e.Args, "--smoke-all");
            var runIndex = Array.IndexOf(e.Args, "--smoke-run");
            var buildIndex = Array.IndexOf(e.Args, "--smoke-build");
            if ((smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length) || (allIndex >= 0 && allIndex + 1 < e.Args.Length) || (runIndex >= 0 && runIndex + 1 < e.Args.Length) || (buildIndex >= 0 && buildIndex + 1 < e.Args.Length))
            {
                window.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        if (buildIndex >= 0)
                        {
                            await window.BuildEvidenceAsync(System.IO.Path.GetFullPath(e.Args[buildIndex + 1]));
                            Shutdown(0); return;
                        }
                        if (runIndex >= 0)
                        {
                            await window.RunProjectAsync();
                            window.EnsureOperationSucceeded();
                            smokeIndex = runIndex;
                        }
                        else await window.VerifyAsync();
                        if (allIndex >= 0)
                        {
                            await window.RenderUiEvidenceAsync(System.IO.Path.GetFullPath(e.Args[allIndex + 1]));
                            Shutdown(0);
                            return;
                        }
                        window.UpdateLayout();
                        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        image.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using var output = System.IO.File.Create(e.Args[smokeIndex + 1]);
                        encoder.Save(output);
                        Shutdown(0);
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
                };
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Axmol Hub startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
