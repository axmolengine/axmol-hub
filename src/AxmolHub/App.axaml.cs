using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

public partial class App : Application
{
    /// <summary>Command line and host directory. Filled in by <see cref="Program.Main"/> before AppBuilder.</summary>
    internal static HubHostOptions Options { get; set; } = HubHostOptions.Parse([]);
    internal static DeepLinkActivationBroker? Activations { get; set; }
    private WindowsTrayIntegration? _trayIntegration;

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
            HubAppIcon.ApplyMacDockIcon();
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

    /// <summary>
    /// The About surface's copy. Fixed English: the product's name, its one-sentence
    /// description and project/publisher links read the same in both UI languages, so they have no second column —
    /// which is why they live here rather than in <see cref="HubTexts"/>.
    /// </summary>
    private const string AboutTitle = "About Axmol Hub";

    private const string AboutTagline =
        "Axmol Hub is the desktop workspace for Axmol game development: engines, projects, toolchains and an AI "
        + "assistant in one place.";

    private const string AboutRepository = "https://github.com/axmolengine/axmol-hub";

    /// <summary>The application menu's About entry, clicked (see the <c>NativeMenu</c> in App.axaml).</summary>
    private void OnAboutMenuClick(object? sender, EventArgs e)
        => _ = ShowAboutAsync((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);

    /// <summary>
    /// One place for this surface: both menu entries and <c>--verify-shell</c> come through here, so the
    /// check reads the same dialog a click opens rather than a re-enactment of it. A null owner is the platform's
    /// own arrangement (HubDialog degrades to a non-modal Show), which is also what lets the check drive it without
    /// blocking the suite.
    ///
    /// <para>The body is the name with the assembly's version — one product version, in Directory.Build.props —
    /// and then the one sentence that says what Hub is. The logo is the same 256 px icon the window wears, and it
    /// is read once and kept: a box the user can open all day must not reload it on every open, and a missing icon
    /// file is an appearance problem rather than a reason for the dialog to fail.</para>
    /// </summary>
    internal static Task<HubDialogResult> ShowAboutAsync(Window? owner)
        => HubDialog.ShowAsync(
            owner,
            AboutTitle,
            "Axmol Hub v" + HubReleaseInfo.DisplayVersion + "\n\n" + AboutTagline,
            buttons: HubDialogButtons.None,
            picture: AboutPicture(),
            links:
            [
                new(AboutRepository, AboutRepository, "Source code:"),
                new("axmol.dev", "https://axmol.dev", "Super project:"),
            ],
            footerLink: new("Simdsoft Limited.", "https://simdsoft.com", "Copyright © 2026"),
            centerBody: true);

    private static Bitmap? _aboutPicture;

    private static Bitmap? AboutPicture()
    {
        if (_aboutPicture is not null) return _aboutPicture;
        try
        {
            var path = HubAppIcon.FilePath(HubAppIcon.WindowIconSize);
            if (File.Exists(path)) _aboutPicture = new Bitmap(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not load the Hub logo for the About box: {ex}");
        }

        return _aboutPicture;
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Settings must be read before any window is constructed, because HubStrings.Apply must
        // first load the copy into the resource dictionary: DynamicResource resolves by key on
        // demand, so building the window first and loading after would leave a batch of labels
        // resolved to null — with no error, just blank. The WPF version uses the same order.
        var preferencesStore = new PreferencesStore(Options.PreferencesPath);
        var preferences = preferencesStore.Load();

        // Opt-in diagnostic recording of the bytes that actually leave on each model request. Set before any
        // client is built so the first request of the run is already covered. A relative name lands under the
        // data root, because a recording whose location depends on where the app happened to be started from is
        // a recording that cannot be found afterwards.
        if (Options.WireLogDirectory is { Length: > 0 } wireLog)
            Agent.WireLog.Directory = System.IO.Path.IsPathRooted(wireLog)
                ? wireLog
                : System.IO.Path.Combine(Options.ResolveDataRoot(preferences), wireLog);

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
        UpdateService.Instance.UpdateChannel = preferences.UpdateChannel;

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

        // --shot-auth-dialog <png>: the authentication dialog. It is a separate switch for the same reason as
        // the picker shot: a modal is not part of the settings page, so --smoke-pages cannot photograph it.
        if (Options.ShotAuthDialogPath is { } authDialogShot)
        {
            // OrcaRouter offers both routes, so the shot shows the combined sign-in and key-entry surface.
            var provider = AiProviderManifest.CreateBuiltIn("orcarouter");
            if (provider is null)
            {
                Console.Error.WriteLine("orcarouter is missing from the manifest.");
                desktop.Shutdown(1);
                return;
            }

            // Keep the key-named shot option as an alias for existing drive scripts; both paths now show the
            // same combined surface.
            var authDialog = AuthDialog.ForCheck(provider);
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
                    // Settings is the overlay card now, so the shot opens the card on the category it means to
                    // photograph instead of navigating to a page that no longer exists.
                    authWindow.OpenSettingsForCheck("providers");
                    var settings = authWindow.SettingsPageForCheck
                        ?? throw new InvalidOperationException("The settings card did not build.");
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
                    // rather than inferred from one card. The first shot needs no scroll at all: opening the
                    // providers category already lands the detail pane on its top.
                    if (authShot.Contains("second", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.ScrollToSecondGroupForCheck();
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
        UpdateService.Instance.WriteLog = window.WriteLog;
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => window.StopTaskbarProgress();

        if (OperatingSystem.IsWindows() && !Options.IsAutomation)
        {
            _trayIntegration = WindowsTrayIntegration.Create(this, window);
            if (_trayIntegration is { } trayIntegration)
            {
                window.EnableTrayCloseBehavior();
                desktop.Exit += (_, _) =>
                {
                    trayIntegration.Dispose();
                    _trayIntegration = null;
                };
            }
        }

        // The macOS counterpart of the tray block above: the close button hides the window (see
        // MainWindow.ShouldHideOnCloseForCheck), and the Dock icon is the way back — AppKit's reopen event, which
        // arrives whether Hub was in the background or is still frontmost with nothing on screen (the state a close
        // leaves behind), plus the did-become-active notification for the Cmd-Tab half. Neither is what Avalonia
        // would provide: ActivationKind.Reopen never comes, because this version's
        // ClassicDesktopStyleApplicationLifetime does not implement IActivatableLifetime. See MacDockActivation; the
        // shell self-check raises both signals for real.
        //
        // The menu entry is the fallback for the one case AppKit leaves us nothing to answer for — no application
        // delegate at all — and is deliberately not installed otherwise: with the reopen hook in place a second
        // route to the same window would be chrome nobody needs.
        if (OperatingSystem.IsMacOS() && !Options.IsAutomation)
        {
            var dockClickAnswers = MacDockActivation.Install(
                window.RestoreFromActivation,
                uri => Activations?.ActivateUri(uri));
            desktop.Exit += (_, _) => MacDockActivation.Uninstall();
            if (!dockClickAnswers) MacApplicationMenu.Install(this, window.ActivateFromRequest);
        }

        if (!Options.IsAutomation)
        {
            try
            {
                DeepLinkProtocolRegistration.Register(window.WriteLog);
            }
            catch (Exception ex)
            {
                window.WriteLog("Deeplink protocol registration failed: " + ex);
            }

            window.Opened += (_, _) => Activations?.SetHandler(uri =>
                Dispatcher.UIThread.Post(() => _ = window.HandleInstallLinkAsync(uri)),
                () => Dispatcher.UIThread.Post(window.ActivateFromRequest));
        }

        if (Options.TestSystemAttention)
            window.Opened += (_, _) => window.RunSystemAttentionDiagnostic();

        // Silent startup update check. Fire-and-forget: it must never block the window from coming
        // up, and its only visible effect is the sidebar's update button (fed by UpdateService.Changed)
        // — **no modal prompt**: an available update is a passive badge, and the actual fetch / restart
        // is a deliberate press on that button.
        // Automation / gallery / verification modes never phone home — a self-check binary checking
        // for updates would be a side effect nobody asked for and would fail on offline CI.
        if (!Options.IsAutomation && !Options.IsDrive)
        {
            window.Opened += async (_, _) => await UpdateService.Instance.CheckAsync();
        }

        // The three screenshot switches are mutually exclusive: --smoke exits right after one shot,
        // while --smoke-pages switches pages eight times in a row.
        // When both are passed, --smoke-pages wins (broader coverage) without an error.
        if (Options.DriveScriptPath is { } driveScript)
        {
            DriveRunner.Attach(window, desktop, driveScript);
        }
        else if (Options.SmokePagesDirectory is { } pagesDirectory)
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
            // containing "sub" opens the 外观 submenu and photographs that flyout instead, because a
            // submenu is yet another popup and never appears inside its parent's frame.
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
                    if (menuShot.Contains("sub", StringComparison.OrdinalIgnoreCase)
                        && menu?.Items.OfType<MenuItem>()
                            .FirstOrDefault(item => item.Tag as string == MainWindow.MenuRowTag.Appearance)
                            is { } appearance)
                    {
                        // The submenu is a popup of its own, so the anchor has to move into it: capturing
                        // the parent's PopupRoot after this would photograph the two rows behind it.
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
        else if (Options.ShotComposerMenuPath is { } composerShot)
        {
            // --shot-composer-menu <png>: the composer's two popups (the + attachments menu and the tool
            // permission menu) are their own top-levels, so --smoke-pages can never photograph them. A file
            // name containing "perm" opens the permission menu instead of the + menu — only one flyout can be
            // on screen at a time, and the two answer different questions.
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var panel = window.OpenAssistant();
                    window.Chat.StartOrOpenEmptyConversation();
                    Dispatcher.UIThread.RunJobs();
                    var anchor = composerShot.Contains("perm", StringComparison.OrdinalIgnoreCase)
                        ? panel.OpenPermissionMenuForCheck()
                        : panel.OpenComposerMenuForCheck();
                    Dispatcher.UIThread.RunJobs();

                    var popupRoot = anchor?.GetSelfAndVisualAncestors().OfType<TopLevel>().FirstOrDefault();
                    if (popupRoot is null)
                    {
                        Console.Error.WriteLine("composer menu did not open.");
                        desktop.Shutdown(1);
                        return;
                    }

                    var stats = SmokeCapture.Capture(popupRoot, composerShot);
                    Console.WriteLine(stats.IsBlank() ? $"BLANK {composerShot}" : $"OK    {composerShot}");
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
