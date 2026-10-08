using Avalonia;
using Avalonia.X11;
using Velopack;

namespace AxmolHub;

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
            velopack.OnAfterInstallFastCallback(_ =>
            {
                DeepLinkProtocolRegistration.RegisterWindowsAfterInstall();
                WindowsMuiCache.ClearForCurrentInstall();
            });
            velopack.OnBeforeUninstallFastCallback(_ => DeepLinkProtocolRegistration.UnregisterWindowsOnUninstall());
        }
        velopack.Run();
        SystemAttentionService.SetWindowsAppUserModelId();

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

        // Same rule as the Velopack hook above: decide and leave before any UI exists. --check-secrets opens no
        // window on purpose, so a CI runner with no display server can still assert that its platform stores a
        // provider key — the one claim neither the Windows-only Checks project nor --verify-shell can reach.
        if (HubHostOptions.IsSecretsSelfTest(args))
        {
            Environment.ExitCode = SecretStoreSelfCheck.Run(HubHostOptions.SecretsScratchArgument(args));
            return;
        }

        // Same headless contract as --check-secrets, for the other thing only a real Linux session can prove:
        // that the desktop entry and the icon theme entries the shell reads to draw our logo are actually
        // written, and name the same identifier the window advertises.
        if (HubHostOptions.IsLinuxIntegrationSelfTest(args))
        {
            Environment.ExitCode = LinuxIntegrationSelfCheck.Run(HubHostOptions.LinuxIntegrationScratchArgument(args));
            return;
        }

        // Argument parsing must happen before AppBuilder: it decides which window to show.
        App.Options = HubHostOptions.Parse(args);
        if (!App.Options.IsAutomation)
        {
            App.Activations = DeepLinkActivationBroker.Start(HubHostOptions.DeepLinkArgument(args));
            if (App.Activations.IsSecondaryInstance)
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
            // X11 only (ignored on Windows/macOS): Avalonia defaults the window class to the entry
            // assembly name, and the desktop-entry matching that serves the taskbar icon needs the
            // kebab identity instead. See LinuxDesktopIdentity.
            .With(new X11PlatformOptions { WmClass = LinuxDesktopIdentity.Id })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
