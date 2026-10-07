using Avalonia;
using Velopack;

namespace AxmolHub.App;

internal static class Program
{
    // Avalonia's startup code must complete before AppMain; the entry point itself is STA.
    // Difference from the WPF version: the official template is natively a [STAThread] Main,
    // so we don't need to change App.xaml from ApplicationDefinition to Page and set
    // StartupObject by hand the way WPF does.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack launches this process with --veloapp-* flags for install/update/uninstall
        // hook callbacks; we must handle and exit before any UI initialization, otherwise the
        // main window pops up during installation.
        // Put it first (even before the encoding setup): this path must have no side effects.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopack.OnAfterInstallFastCallback(_ => DeepLinkProtocolRegistration.RegisterWindowsAfterInstall());
            velopack.OnBeforeUninstallFastCallback(_ => DeepLinkProtocolRegistration.UnregisterWindowsOnUninstall());
        }
        velopack.Run();

        // Verification-mode reports go to stdout and contain Chinese. Without UTF-8 they are
        // garbled on Windows (hit on the very first P5 self-check run: the assertion results
        // were entirely unreadable, i.e. no evidence).
        // A WinExe launched by double-clicking has no console, so setting the encoding throws
        // IOException here and must be swallowed — losing readable logs is a pity, but failing to
        // start is a real fault.
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (IOException)
        {
        }

        // Argument parsing must happen before AppBuilder: it decides which window to show.
        App.Options = HubHostOptions.Parse(args);
        if (!App.Options.IsAutomation)
        {
            App.Activations = DeepLinkActivationBroker.Start(HubHostOptions.DeepLinkArgument(args));
            if (App.Activations.ForwardedToExistingInstance)
            {
                App.Activations.Dispose();
                return;
            }
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            App.Activations?.Dispose();
        }
    }

    // The visual designer also calls this method; do not delete.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
