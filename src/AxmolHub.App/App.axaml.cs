using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AxmolHub.Core;

namespace AxmolHub.App;

public partial class App : Application
{
    /// <summary>Command line and host directory. Filled in by <see cref="Program.Main"/> before AppBuilder.</summary>
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
            // The main window can't be built on this path, so HubDialog must support a null owner
            // (the WPF version's MessageBox here is standalone too). See Views/HubDialog.axaml.
            // The title comes from the text table: hard-coding Chinese here would render blank on a
            // Linux machine without CJK fonts, and "startup failed" is exactly when the title matters
            // most. At this point HubStrings.Language is still the cold-start language, so the
            // English copy is available.
            _ = HubDialog.ShowAsync(null, HubStrings.Get("StartupFailed"), ex.ToString());
            desktop.Shutdown(1);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Settings must be read before any window is constructed, because HubStrings.Apply must
        // first load the copy into the resource dictionary: DynamicResource resolves by key on
        // demand, so building the window first and loading after would leave a batch of labels
        // resolved to null — with no error, just blank. The WPF version uses the same order.
        var preferencesStore = new PreferencesStore(Options.PreferencesPath);
        var preferences = preferencesStore.Load();

        // Verification mode must be closed: assertions and reports contain concrete copy, so if it
        // followed the user's language setting, the same binary would pass on one machine and fail
        // on another. That's why verification mode forces Chinese from the start (the shell
        // self-check switches the language for real and back internally, and handles its own cleanup).
        //
        // ChineseLanguage must be written explicitly here — don't rely on the `new HubPreferences()`
        // default: that default is the "cold-start language", which was changed to English on
        // 2026-10-03 (see HubTexts.DefaultLanguage). Previously the two happened to both be Chinese,
        // so this constraint looked like "use the default" when it was actually two separate things.
        if (Options.VerifyShellReport is not null || Options.VerifyOpsReport is not null)
        {
            preferences = new HubPreferences { Language = HubTexts.ChineseLanguage };
        }

        HubStrings.Apply(preferences.Language, this);

        // P4's three self-check/verification modes each use a dedicated window; the product mode
        // opens the main window.
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

        // The three-tier data-root priority (command line > settings file > default) matches the
        // WPF version; see HubHostOptions.ResolveDataRoot.
        // The settings store and settings object are handed to the shell together: changing the
        // language in the settings page must be persisted back to the same store and object,
        // otherwise the preferences cached in the shell would diverge from disk.
        var window = new MainWindow(Options.ResolveDataRoot(preferences), preferencesStore, preferences);
        desktop.MainWindow = window;

        // The two screenshot switches are mutually exclusive: --smoke exits right after one shot,
        // while --smoke-pages switches pages eight times in a row.
        // When both are passed, --smoke-pages wins (broader coverage) without an error.
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
