using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxmolHub.Core;

namespace AxmolHub.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataIndex = Array.IndexOf(e.Args, "--data-root");
            var preferencesIndex = Array.IndexOf(e.Args, "--preferences");
            var preferencesPath = preferencesIndex >= 0 && preferencesIndex + 1 < e.Args.Length ? e.Args[preferencesIndex + 1]
                : System.IO.Path.Combine(AppContext.BaseDirectory, "hub-settings.json");
            var preferencesStore = new PreferencesStore(preferencesPath);
            var preferences = preferencesStore.Load();
            Texts.Apply(preferences.Language);
            var root = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1]
                : preferences.DataRoot ?? System.IO.Path.Combine(AppContext.BaseDirectory, "data");
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
