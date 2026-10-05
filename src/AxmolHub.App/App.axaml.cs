using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // Widened from "shell/ops self-check only" to every automation mode: the theme now comes
        // from the same settings file, and it changes what the assertions actually measure. The
        // gallery asserts "switching the variant really changes the background", which only holds
        // when the run starts from a known variant — on a machine whose system theme is light,
        // "follow system" would start the run in light and the same binary would fail there.
        // Pinning dark here is the same closure as pinning Chinese (and makes --smoke screenshots
        // comparable across machines).
        if (Options.IsAutomation)
        {
            preferences = new HubPreferences { Language = HubTexts.ChineseLanguage, Theme = HubTheme.Dark };
        }

        HubStrings.Apply(preferences.Language, this);

        // Theme before the first window, for the same reason as the copy: the variant decides which
        // side of every ThemeDictionaries token DynamicResource resolves to.
        ThemeService.Apply(preferences.Theme, this);

        // Seed the update service with the user's background-download preference before the startup
        // check runs, so a check that finds an update can start fetching it right away.
        UpdateService.Instance.AutoDownload = preferences.AutoDownloadUpdates;

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

        // --shot-provider-picker <png>: shows the modal "add provider" picker on its own and captures it.
        // Nothing else can photograph it — it is a dialog, so --smoke-pages (which walks the shell's pages)
        // never sees it — and it is the one new screen S6 introduced, i.e. exactly the thing a person needs to
        // look at before believing the layout is right. Presets are read from the real manifest, with a couple
        // of already-configured entries filtered out so the list has the shape a user would actually see.
        if (Options.ShotProviderPickerPath is { } pickerShot)
        {
            var presets = AiProviderManifest.Load()
                .Where(entry => entry.Id != "openai")
                .Select(entry => AiProviderManifest.CreateBuiltIn(entry.Id))
                .OfType<ModelProvider>()
                .ToArray();

            var dialog = ProviderPickerWindow.ForCheck(presets);
            desktop.MainWindow = dialog;
            dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    dialog.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var stats = SmokeCapture.Capture(dialog, pickerShot);
                    Console.WriteLine(stats.IsBlank() ? $"BLANK {pickerShot}" : $"OK    {pickerShot}");
                    desktop.Shutdown(stats.IsBlank() ? 1 : 0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    desktop.Shutdown(1);
                }
            });
            return;
        }

        // --shot-auth-dialog <png>: the authentication dialog, on the method step or the key step depending on
        // whether the file name says "key". It is a separate switch for the same reason as the picker shot: a
        // modal is not part of the settings page, so --smoke-pages cannot photograph it at all.
        if (Options.ShotAuthDialogPath is { } authDialogShot)
        {
            // OrcaRouter is the one provider with both routes, so step one is the only place the two options
            // appear together; a key-only provider would render a list of one and misrepresent the dialog.
            var provider = AiProviderManifest.CreateBuiltIn("orcarouter");
            if (provider is null)
            {
                Console.Error.WriteLine("orcarouter is missing from the manifest.");
                desktop.Shutdown(1);
                return;
            }

            var step = authDialogShot.Contains("key", StringComparison.OrdinalIgnoreCase)
                ? ProviderAuthMethods.ApiKey
                : null;
            var authDialog = AuthDialog.ForCheck(provider, step);
            desktop.MainWindow = authDialog;
            authDialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    authDialog.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var stats = SmokeCapture.Capture(authDialog, authDialogShot);
                    Console.WriteLine(stats.IsBlank() ? $"BLANK {authDialogShot}" : $"OK    {authDialogShot}");
                    desktop.Shutdown(stats.IsBlank() ? 1 : 0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    desktop.Shutdown(1);
                }
            });
            return;
        }

        // --shot-settings-auth <png>: the settings page scrolled to the provider card, with an account added.
        // Same reason as the picker shot — the auth block sits below the fold, so --smoke-pages captures a
        // page image that does not contain it. The account is added through the real workspace so what is
        // photographed is the state a configured install would be in, not a hand-built mock.
        if (Options.ShotSettingsAuthPath is { } authShot)
        {
            // A scratch data root, not the user's: this mode writes real credentials to demonstrate the account
            // list, and doing that against the live root would leave demo accounts behind on every run.
            var authRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "axmolhub-shot-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(authRoot);

            var authWindow = new MainWindow(authRoot, preferencesStore, preferences);
            desktop.MainWindow = authWindow;
            authWindow.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var settings = (SettingsPage)authWindow.NavigateTo("Settings");
                    settings.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();

                    // Two accounts so the list has the shape it gets in real use (a named one plus a signed-in
                    // one), which is what makes the active badge and the row of actions worth looking at.
                    authWindow.Chat.AddCredential("orcarouter", "工作账号", "sk-demo-account", CredentialSources.ApiKey);
                    authWindow.Chat.AddOAuthCredential("orcarouter", "acct-demo", "api", "sk-yoex-demo");

                    // A second endpoint and several models, so the picture shows the grouped list the way it
                    // looks once more than one provider is configured — a single group would not reveal whether
                    // the groups actually stack. Three models rather than one for the same reason about the
                    // rules *between* rows: with a single row there is nothing to divide, so a picture taken
                    // then cannot show whether the separators are there, doubled, or drawn in the wrong place.
                    authWindow.Chat.AddPreset("deepseek");
                    authWindow.Chat.AddModel("orcarouter", "orcarouter/auto");
                    authWindow.Chat.AddModel("orcarouter", "gpt-5.1-codex-mini");
                    authWindow.Chat.AddModel("orcarouter", "o4-mini");
                    settings.RefreshProviderGroupsForCheck();
                    // A second shot scrolls to the next group, so the "all groups expanded" claim is visible
                    // rather than inferred from one card.
                    if (authShot.Contains("second", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.ScrollToSecondGroupForCheck();
                    }
                    else
                    {
                        settings.ScrollToProviderCardForCheck();
                    }

                    authWindow.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var stats = SmokeCapture.Capture(authWindow, authShot);
                    Console.WriteLine(stats.IsBlank() ? $"BLANK {authShot}" : $"OK    {authShot}");
                    desktop.Shutdown(stats.IsBlank() ? 1 : 0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    desktop.Shutdown(1);
                }
            });
            return;
        }

        // The three-tier data-root priority (command line > settings file > default) matches the
        // WPF version; see HubHostOptions.ResolveDataRoot.
        // The settings store and settings object are handed to the shell together: changing the
        // language in the settings page must be persisted back to the same store and object,
        // otherwise the preferences cached in the shell would diverge from disk.
        var window = new MainWindow(Options.ResolveDataRoot(preferences), preferencesStore, preferences);
        desktop.MainWindow = window;

        // Silent startup update check. Fire-and-forget: it must never block the window from coming
        // up, and its only visible effect is the dot the shell raises on the Settings nav item (fed
        // by UpdateService.Changed) — **no modal prompt**: an available update is a passive badge, and
        // the actual check / download / apply is a deliberate action taken in the settings page.
        // Automation / gallery / verification modes never phone home — a self-check binary checking
        // for updates would be a side effect nobody asked for and would fail on offline CI.
        if (!Options.IsAutomation)
        {
            window.Opened += async (_, _) => await UpdateService.Instance.CheckAsync();
        }

        // The three screenshot switches are mutually exclusive: --smoke exits right after one shot,
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
        else if (Options.ShotBottomMenuPath is { } menuShot)
        {
            // --shot-bottom-menu <png>: the sidebar's bottom entry is a popup menu, and a popup is
            // its own top-level — --smoke-pages (which photographs the shell window) can never see
            // it, so it gets the same dedicated switch as the picker/auth dialogs above. A file name
            // containing "sub" opens the Appearance submenu and photographs that flyout instead
            // (same trick the auth dialog shot uses with "key"), because a submenu is yet another
            // popup and never appears inside its parent's frame.
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    window.NavigateTo("Toolchains");
                    Dispatcher.UIThread.RunJobs();
                    window.ClickBottomMenuForCheck();
                    Dispatcher.UIThread.RunJobs();

                    // The flyout presents in its own PopupRoot, so the capture must target that
                    // top-level, not the shell window: walk up from a menu item to it.
                    var menu = window.BottomMenuForCheck;
                    var anchor = menu?.Items.OfType<MenuItem>().FirstOrDefault();
                    if (menuShot.Contains("sub", StringComparison.OrdinalIgnoreCase))
                    {
                        var appearance = menu!.Items.OfType<MenuItem>().ElementAt(1);
                        appearance.IsSubMenuOpen = true;
                        Dispatcher.UIThread.RunJobs();
                        anchor = appearance.Items.OfType<MenuItem>().FirstOrDefault();
                    }

                    var popupRoot = anchor?.GetSelfAndVisualAncestors().OfType<TopLevel>().FirstOrDefault();
                    if (popupRoot is null)
                    {
                        Console.Error.WriteLine("bottom menu did not open.");
                        desktop.Shutdown(1);
                        return;
                    }

                    var stats = SmokeCapture.Capture(popupRoot, menuShot);
                    Console.WriteLine(stats.IsBlank() ? $"BLANK {menuShot}" : $"OK    {menuShot}");
                    desktop.Shutdown(stats.IsBlank() ? 1 : 0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    desktop.Shutdown(1);
                }
            });
        }
    }
}
