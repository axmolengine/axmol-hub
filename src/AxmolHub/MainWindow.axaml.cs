using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The shell. Its shape matches the WPF version pixel for pixel (see the comment at the top of
/// MainWindow.axaml), and it has exactly three responsibilities: wiring navigation, placing pages
/// into <c>PageHost</c>, and translating <see cref="HubWorkspace"/> events into the status bar /
/// log panel / cancel button.
///
/// **Not a single line of business logic lives here**: engines, modules, build, run, and devices
/// are all in Core, orchestrated by <see cref="HubWorkspace"/> (a wholesale port of the non-visual
/// half of WPF <c>MainWindow.xaml.cs</c>).
/// </summary>
public partial class MainWindow : Window
{
    private HubWorkspace _workspace;
    private readonly PreferencesStore _preferencesStore;
    private readonly HubPreferences _preferences;
    private readonly SystemAttentionService _attention = new();
    private readonly Dictionary<string, Control> _pages = [];
    private bool? _attentionDiagnosticBadge;
    private string? _attentionDiagnosticConversationId;
    private bool _windowIsOpen;

    /// <summary>Forced window-visibility answer for the self-check, or <c>null</c> to read the platform. The
    /// suite drives a real <see cref="MainWindow"/> that is never the foreground window and can never truly be
    /// minimized, so without this the desktop half of the visibility composition could only ever be asserted in
    /// one of its two states.</summary>
    private bool? _windowVisibleOverride;

    /// <summary>
    /// The AI assistant page's state. **One instance for the window's whole life**, created before the page
    /// is: it persists providers/conversations under the data root, so unlike <see cref="HubWorkspace"/>
    /// it must survive a data-root switch's decision point — see <see cref="SwitchDataRoot"/>.
    /// </summary>
    private ChatWorkspace _chat;

    /// <summary>The assistant page, built lazily on first navigation (a user who never opens it pays nothing).</summary>
    private ChatPanel? _chatPanel;

    /// <summary>The conversation-list section hosted under the "AI 助手" nav item. Built eagerly because the
    /// sidebar is part of the shell; rebuilt together with <see cref="_chat"/> on a data-root switch.</summary>
    private ChatSidebar? _chatSidebar;

    /// <summary>Sidebar drag state: pointer x and width when the grip grab started.</summary>
    private double _grabStartX;
    private double _grabStartWidth;
    private bool _grabbing;

    /// <summary>Sidebar width bounds (also enforced in the XAML-facing logic).</summary>
    private const double SidebarMin = 240;
    private const double SidebarMax = 420;
    private const double SidebarCollapseThreshold = 200;

    /// <summary>
    /// The update dot's tooltip content. Installed once and rewritten **in place**: re-assigning
    /// <c>ToolTip.Tip</c> closes an open tooltip, and a new value assigned while it is closed does not
    /// re-open it (the settings page's progress tip hits the same rule). Keeping one TextBlock for the
    /// window's whole life is what lets the tip stay up while the pointer rests on the dot.
    /// </summary>
    private readonly TextBlock _updateDotTip = new();

    private string _currentKey = "";

    /// <summary>
    /// For the XAML loader and design-time preview (without it Avalonia reports AVLN3001 "not
    /// available via the runtime loader"). Using the default data root is safe: **construction
    /// only reads, never writes** — nothing is persisted until the user clicks a button.
    /// </summary>
    public MainWindow() : this(HubHostOptions.DefaultDataRoot)
    {
    }

    /// <summary>
    /// Data root and settings file both use their default locations. **Verification programs must
    /// not use this overload**: switching language writes the settings file, and the default
    /// settings file is the user's own.
    /// </summary>
    public MainWindow(string dataRoot)
        : this(dataRoot, new PreferencesStore(HubHostOptions.DefaultPreferencesPath), new HubPreferences())
    {
    }

    public MainWindow(string dataRoot, PreferencesStore preferencesStore, HubPreferences preferences)
    {
        // Set in code, not in MainWindow.axaml: Avalonia 12's WindowIcon has no type converter, so
        // `Icon="avares://…"` would fail at XAML load time.
        Icon = HubAppIcon.CreateWindowIcon();
        _preferencesStore = preferencesStore;
        _preferences = preferences;
        _workspace = new HubWorkspace(dataRoot, preferences, preferencesStore);
        _workspace.Owner = this;
        _chat = new ChatWorkspace(dataRoot);
        _attention.Initialize(id => NotificationActivated(id));
        _attention.NotificationActivated += NotificationActivated;
        _attention.Diagnostic += message => Dispatcher.UIThread.Post(
            () => WriteLog("[System attention] " + message));
        WireChatSeams();
        WireChatRuns();

        InitializeComponent();
        InitializeSidebar();
        InitializeInspector();
        InitializePictureViewer();
        InitializeChrome();
        WireWorkspace();
        WireChrome();

        NavProjects.IsCheckedChanged += (_, _) => OnNavigated(NavProjects, "Projects");
        NavInstalls.IsCheckedChanged += (_, _) => OnNavigated(NavInstalls, "Installs");
        NavToolchains.IsCheckedChanged += (_, _) => OnNavigated(NavToolchains, "Toolchains");
        NavAssistant.IsCheckedChanged += (_, _) =>
        {
            var assistantSelected = NavAssistant.IsChecked == true;
            AssistantSearchButton.IsVisible = assistantSelected;
            AssistantNewButton.IsVisible = assistantSelected;
            OnNavigated(NavAssistant, "Assistant");
        };
        AssistantSearchButton.Click += (_, _) => _chatSidebar!.ToggleSearchFromNavigation();
        AssistantNewButton.Click += (_, _) => _chatSidebar!.StartNewConversationFromNavigation();
        ToolTip.SetTip(AssistantSearchButton, HubStrings.Get("SearchConversationsTip"));
        ToolTip.SetTip(AssistantNewButton, HubStrings.Get("NewConversationTip"));

        // Settings is no longer a nav item: it is the gear at the bottom of the rail. It still navigates like
        // one, so the page keeps its cached instance and ScrollViewer position.
        SettingsButton.Click += (_, _) => NavigateTo("Settings");

        // The brand/host text block beside the gear opens the popup menu (Settings + Appearance). The
        // menu is rebuilt on every open, so its labels and theme checkmarks are always current without
        // any sync machinery.
        BottomMenuButton.Click += (_, _) => ToggleBottomMenu();

        // Default to the "Projects" page, matching the WPF version (WPF uses NavProjects IsChecked="True").
        NavigateTo("Projects");

        // Font conclusions must be handled only after the window is on screen: a modal's owner
        // must be shown first.
        Opened += (_, _) => ReportFonts();
        Opened += (_, _) =>
        {
            _windowIsOpen = true;
            SyncApprovalBadge();
            // Microsoft's contract is that no ITaskbarList3 call may precede the shell's TaskbarButtonCreated
            // message, which explorer posts asynchronously after the HWND appears. Honouring that literally needs
            // a Win32 window-proc hook Avalonia only offers through platform-specific APIs, so this takes the
            // cheap route: one deferred retry. It is safe because SetApprovalBadge records the applied state only
            // on success, so repeating a state that already took is a no-op and a failed one is free to try again.
            // Without it, an approval restored at startup whose first attempt loses the race stays undotted until
            // some later transcript change raises Changed — which for an idle session may never come.
            DispatcherTimer.RunOnce(SyncApprovalBadge, TimeSpan.FromSeconds(1));
        };
    }

    internal void ActivateFromRequest()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal async Task HandleInstallLinkAsync(string value)
    {
        if (SystemAttentionService.TryGetConversationId(value, out var conversationId))
        {
            OpenConversationFromAttention(conversationId);
            return;
        }

        EngineInstallLink request;
        try
        {
            request = EngineInstallLink.Parse(value);
        }
        catch (FormatException ex)
        {
            _workspace.Log.Write("Rejected deeplink: " + ex.Message);
            await HubDialog.ShowAsync(this, HubStrings.Get("DeepLinkTitle"), HubStrings.Get("DeepLinkInvalid"));
            return;
        }

        if (_workspace.CanCancel)
        {
            _workspace.Log.Write($"Rejected deeplink for Axmol {request.Version}: another operation is running.");
            await HubDialog.ShowAsync(this, HubStrings.Get("DeepLinkTitle"), HubStrings.Get("DeepLinkBusy"));
            return;
        }

        WindowState = WindowState.Normal;
        Activate();
        NavigateTo("Installs");
        await _workspace.InstallEngineAsync(request.Version, request.Source);
    }

    internal void WriteLog(string message) => _workspace.Log.Write(message);

    /// <summary>
    /// Startup font self-check. Two things are done separately because they have different
    /// audiences:
    ///
    /// ① **Log**: write a line whenever a missing Chinese font is detected, regardless of the UI
    ///    language — users on the English UI aren't affected, but "why is Chinese rendering as
    ///    boxes" must be findable in the log.
    /// ② **Dialog**: only pop up when the UI language is Chinese (criterion in
    ///    <see cref="CjkFontNotice.ShouldWarn"/>) — at that moment the user sees nothing but boxes,
    ///    and not prompting means leaving them to guess at a broken UI.
    ///
    /// The probe runs in this frame rather than the constructor: <c>FontManager.Current</c> needs
    /// the platform font implementation to be in place.
    /// </summary>
    private void ReportFonts()
    {
        var availability = CjkFontProbe.Availability;
        if (availability == CjkFontAvailability.Available)
        {
            return;
        }

        // Log a line for Unknown too: here we can tell "no font" apart from "probe failed", and the
        // log should make the same distinction.
        _workspace.Log.Write(availability == CjkFontAvailability.Missing
            ? CjkFontNotice.LogLine
            : "CJK font probe returned no result; Chinese rendering is unverified.");

        if (availability == CjkFontAvailability.Missing)
        {
            CjkFontNotifier.NotifyIfNeeded(this);
        }
    }

    /// <summary>The assembled page keys. Verification programs rely on it to confirm every navigation
    /// destination has a real page. <c>Assistant</c> is included: it is a full page now, not a drawer.</summary>
    internal static string[] PageKeys => ["Projects", "Installs", "Toolchains", "Assistant", "Settings"];

    internal HubWorkspace Workspace => _workspace;

    /// <summary>
    /// Switches pages and returns the result to the caller for runtime assertions.
    /// Pages are **constructed on demand and cached**: rebuilding on every navigation would lose
    /// selection and scroll position.
    /// </summary>
    internal Control NavigateTo(string name)
    {
        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "Projects" => new ProjectsPage(_workspace),
                "Installs" => new InstallsPage(_workspace),
                "Toolchains" => new ToolchainsPage(_workspace),
                "Assistant" => _chatPanel ??= CreateChatPanel(),
                "Settings" => new SettingsPage(_workspace, _preferencesStore, _preferences, _chat, OpenFolder, ApplyLanguage, SwitchDataRoot, UseTheme),
                _ => throw new ArgumentException("Unknown page: " + name, nameof(name)),
            };
            _pages[name] = page;
        }

        _currentKey = name;
        PageHost.Content = page;
        SyncNavigation(name);
        SyncApprovalBadge();

        // The conversation list is a child of the assistant nav item: hidden on every other page.
        ChatSidebarHost.IsVisible = name == "Assistant";
        // The inspector belongs to the assistant too, and a picture left open over a page you navigated away
        // from is a picture you can no longer reach the transcript of — so navigating closes it.
        SyncInspectorForPage();
        if (PictureViewerHost.IsVisible) ClosePictureViewer();
        UpdatePageTitle();
        return page;
    }

    /// <summary>Builds the assistant page once and keeps the shell's reference to it, so a later
    /// data-root switch can drop and rebuild it (<see cref="ReplaceWorkspace"/>).</summary>
    private ChatPanel CreateChatPanel()
    {
        var panel = new ChatPanel(_chat);
        // Sending a first message auto-titles the conversation; the workspace does not raise Changed for
        // streamed messages, so the panel tells the shell to refresh its sidebar and top-bar title.
        panel.ConversationStateChanged += () =>
        {
            _chatSidebar?.Reload();
            UpdatePageTitle();
        };
        // The panel owns the transcript; the window owns the surfaces that reach past it. A picture opens in the
        // window-level viewer (it fills the window, not the chat column), and the plan/diff/repository review
        // opens in the inspector column beside the page.
        panel.ShowPictureViewer = (set, index) => OpenPictureViewer(set, index);
        panel.OpenInspector = (tab, turnIndex) => OpenInspector(tab, turnIndex);
        panel.CloseInspector = CloseInspector;
        panel.ToggleInspectorExpanded = ToggleInspectorExpanded;
        panel.RefreshInspector = RefreshInspector;
        return panel;
    }

    /// <summary>
    /// What the assistant reads from the rest of the app, wired in one place because the workspace is rebuilt
    /// whenever the data root changes and a seam missed on the rebuild is a silent downgrade: no engine roots
    /// means engine installs stop being read-only, no log means command output is truncated with nowhere left.
    /// </summary>
    private void WireChatSeams()
    {
        _chat.HubSnapshotProvider = CreateReadOnlyChatSnapshot;
        _chat.PreferencesProvider = () => _preferences;
        // The tool trust list is the one app-wide setting the assistant itself writes, so it writes through the
        // same store the settings page uses rather than opening a second one on the same file.
        _chat.PreferencesPersist = _preferencesStore.Save;
        _chat.AuditWrite = _workspace.Log.Write;
        _chat.LogProvider = () => _workspace.Log;
        _chat.EngineRootsProvider = () => [.. _workspace.State.Engines.Select(engine => engine.Path)];
    }

    private ChatWorkspace.HubReadOnlySnapshot CreateReadOnlyChatSnapshot()
        => new(
            _workspace.State.Projects
                .Select(project => new ChatWorkspace.HubProjectSummary(
                    project.Name, project.Path, project.Version, project.Platform, project.Configuration, project.BuildStatus))
                .ToArray(),
            _workspace.State.Engines
                .Select(engine => new ChatWorkspace.HubEngineSummary(engine.Version, engine.Channel))
                .ToArray(),
            _workspace.Components
                .Select(component => new ChatWorkspace.HubToolchainSummary(
                    component.Name, component.Status.ToString(), component.Executable is not null))
                .ToArray());

    /// <summary>
    /// Keeps the left navigation highlight in sync with the current page. This is exactly what the
    /// WPF version does in <c>SelectPage</c>.
    /// Skipping it yields "showing page A but highlighting page B" — invisible at compile time and
    /// at binding time, only catchable by screenshot review.
    ///
    /// Settings is not a nav item, so it has no highlight to sync: landing on it simply clears the four
    /// radio buttons. That is why the loop below can omit it while <see cref="PageKeys"/> still lists it.
    /// </summary>
    private void SyncNavigation(string name)
    {
        foreach (var (key, button) in new[]
                 {
                     ("Projects", NavProjects), ("Installs", NavInstalls),
                     ("Toolchains", NavToolchains), ("Assistant", NavAssistant),
                 })
        {
            var expected = key == name;
            if (button.IsChecked != expected)
            {
                button.IsChecked = expected;
            }
        }
    }

    internal Control? CurrentPage => PageHost.Content as Control;

    internal void SetStatus(string text) => Status.Text = text;

    /// <summary>
    /// Puts how many sessions are answering right now on the strip. Called for every <see cref="ChatWorkspace"/>
    /// the shell builds, including the one a data-root switch replaces — an unresubscribed strip would quietly
    /// stop reporting replies, which is exactly the kind of failure nobody sees.
    /// </summary>
    private void WireChatRuns()
    {
        _chat.RunsChanged += _ => UpdateChatRunsStatus();
        _chat.Changed += SyncApprovalBadge;
        _chat.AttentionRequired += OnChatAttentionRequired;
        _chat.RunCompleted += OnChatRunCompleted;
        // The strip's repository slot is a render of what has already been read, so it follows the reads other
        // triggers started: one handler per landing, one per conversation switch, and a completion that takes the
        // stale number away. None of the three asks git anything — which is the whole reason a slot that lives
        // outside the chat page can be honest about a repository without paying for it.
        // A completion still lets it come back: the panel reads again on a finished run when the repository tab
        // is the one being looked at, and that landing repaints this.
        _chat.RepositoryChanged += UpdateRepoIndicator;
        _chat.Changed += UpdateRepoIndicator;
        _chat.RunCompleted += (_, _) => InvalidateRepoIndicator();
        SyncApprovalBadge();
    }

    private void OnChatAttentionRequired(string conversationId, ChatAttentionKind kind)
    {
        if (App.Options.IsAutomation) return;
        if (!SystemAttentionService.ShouldNotifyApproval(
                conversationId, _chat.ViewedConversationId, AttentionNow))
        {
            _attention.Report($"No system notification for approval in conversation {conversationId}: "
                              + "the user is already viewing it in the active Assistant window.");
            return;
        }
        var title = _chat.SessionTitleFor(conversationId);
        if (string.IsNullOrWhiteSpace(title)) title = HubStrings.Get("Conversation");
        var titleKey = kind == ChatAttentionKind.PlanApproval
            ? "ChatPlanNotificationTitle"
            : "ChatToolApprovalNotificationTitle";
        var bodyKey = kind == ChatAttentionKind.PlanApproval
            ? "ChatPlanNotificationBody"
            : "ChatToolApprovalNotificationBody";
        _attention.Show(HubStrings.Get(titleKey),
            string.Format(System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get(bodyKey), title),
            conversationId);
        SyncApprovalBadge();
    }

    private void OnChatRunCompleted(string conversationId, RunOutcome outcome)
    {
        if (App.Options.IsAutomation) return;
        if (!SystemAttentionService.ShouldNotifyRun(
                conversationId, _chat.ViewedConversationId, AttentionNow,
                _chat.HasPendingPlanApproval(conversationId), outcome.Result))
            return;

        var title = _chat.SessionTitleFor(conversationId);
        if (string.IsNullOrWhiteSpace(title)) title = HubStrings.Get("Conversation");
        var notificationTitle = outcome.Result switch
        {
            RunResult.Failed => "ChatRunFailedNotificationTitle",
            RunResult.TimedOut => "ChatRunTimedOutNotificationTitle",
            _ => "ChatRunCompletedNotificationTitle",
        };
        _attention.Show(HubStrings.Get(notificationTitle),
            string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatRunNotificationBody"), title),
            conversationId);
    }

    private void SyncApprovalBadge()
    {
        // This reads window state and asks the shell for a taskbar overlay, both of which belong to the UI
        // thread, and it is reached from ChatWorkspace.Changed — one of 54 raise sites, only some of which are
        // provably on the UI thread. So hop rather than assume. The hop is not about COM apartments: the overlay
        // interface is created and marshalled correctly from an MTA thread as well.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncApprovalBadge);
            return;
        }
        if (_windowIsOpen && !App.Options.IsAutomation)
            _attention.SetApprovalBadge(this, _attentionDiagnosticBadge == true
                ? 1
                : _chat.PendingApprovalConversationCount());
    }

    private void NotificationActivated(string conversationId)
        => Dispatcher.UIThread.Post(() => OpenConversationFromAttention(conversationId));

    private void OpenConversationFromAttention(string conversationId)
    {
        if (string.Equals(conversationId, _attentionDiagnosticConversationId, StringComparison.Ordinal))
        {
            NavigateTo("Assistant");
            WindowState = WindowState.Normal;
            Activate();
            WriteLog("The test notification was activated; the app returned to the Assistant page.");
            return;
        }

        if (_chat.OpenConversation(conversationId) is null) return;
        NavigateTo("Assistant");
        WindowState = WindowState.Normal;
        Activate();
    }

    internal void RunSystemAttentionDiagnostic()
    {
        if (!App.Options.TestSystemAttention || App.Options.IsAutomation) return;

        _attentionDiagnosticConversationId = Guid.NewGuid().ToString("N");
        _attentionDiagnosticBadge = true;
        var badgeApplied = _attention.SetApprovalBadge(this, 1);
        _attention.Report(badgeApplied
            ? "The diagnostic numeric taskbar badge was requested successfully."
            : "The diagnostic numeric taskbar badge request failed or is unsupported; see the preceding diagnostic.");
        _attention.Show(
            HubStrings.Get("AttentionDiagnosticTitle"),
            HubStrings.Get("AttentionDiagnosticBody"),
            _attentionDiagnosticConversationId);
        _attention.Report("A test notification was requested. OS notification settings or focus mode may still suppress its display.");

        DispatcherTimer.RunOnce(() =>
        {
            _attentionDiagnosticBadge = null;
            _attentionDiagnosticConversationId = null;
            SyncApprovalBadge();
            WriteLog("System attention diagnostic ended; the badge returned to the real approval state.");
        }, TimeSpan.FromSeconds(30));
    }

    private void UpdateChatRunsStatus()
    {
        var running = _chat.RunningCount;
        ChatRuns.IsVisible = running > 0;
        ChatRuns.Text = running > 0 ? string.Format(HubStrings.Get("RunningSessionsFormat"), running) : "";
    }

    /// <summary>
    /// Renders the strip's repository slot from the last read this app already holds. It never asks for one: the
    /// only states worth 28 pixels of the strip are states some other trigger — entering the repository tab,
    /// pressing its ⟳, a run that finished while the tab was up — already verified by running git. That is what
    /// makes this element safe to subscribe to <see cref="ChatWorkspace.Changed"/>: it reads one slot and formats
    /// one string, and the ban on hanging work off that event is a ban on <em>reads</em>, not on repainting.
    /// </summary>
    private void UpdateRepoIndicator()
    {
        // Reached from the same event as <see cref="SyncApprovalBadge"/>, and that one's comment says why the hop
        // is not paranoia: of ChatWorkspace's raise sites, only some are provably on the UI thread. The null test
        // is the other half — <c>WireChatRuns</c> subscribes before <c>InitializeComponent</c> has built the
        // strip, so a raise that early would be a null dereference of a control that has nothing to say yet.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateRepoIndicator);
            return;
        }
        if (RepoIndicatorText is null) return;
        var (visible, text) = RepoIndicatorStateFor(
            _chat.RepositoryFor(_chat.ActiveConversation?.WorkspaceRoot)?.State);
        RepoIndicator.IsVisible = visible;
        RepoIndicatorText.Text = visible ? text : "";
        ToolTip.SetTip(RepoIndicator, HubStrings.Get("StatusRepoOpenHint"));
    }

    /// <summary>The whole decision, as a function of the snapshot — which is also the shape a check asserts on,
    /// because a strip that lit on every session from first launch would look identical to one that lit on the
    /// dirty one until something was actually read. Silence is the answer for a clean tree, for a directory that
    /// is not a repository, for a session with no directory, and for a directory nobody has read yet: none of
    /// those is something the person needs told twice a second. Only a tree with something uncommitted, and a
    /// read that honestly failed, earn the row.</summary>
    internal static (bool Visible, string Text) RepoIndicatorStateFor(GitRepositoryState? state)
    {
        if (state is null) return (false, "");
        if (!state.IsReadable)
        {
            // NoWorkspace and NotARepository are not failures — there is no repository here to be dirty in — and
            // a strip that reported them would be lit for every ordinary chat. The three that are honest failures
            // own one short sentence here; their five distinct reasons stay down in the tab, which is where a
            // person who asks gets to read them.
            return state.Outcome is GitReadOutcome.GitMissing or GitReadOutcome.UnsafeRepository
                or GitReadOutcome.Failed
                ? (true, HubStrings.Get("StatusRepoFailed"))
                : (false, "");
        }
        if (state.Entries.Count == 0) return (false, "");
        // The same dash the tab's own head line uses for a detached branch, so the two surfaces never disagree
        // about what a branch that is not named is called.
        var branch = string.IsNullOrEmpty(state.Branch) ? "—" : state.Branch;
        // A cut list says how many were listed, not how many are dirty, and the strip is not allowed to let the
        // two read as the same number.
        var key = state.Truncated ? "StatusRepoDirtyTruncatedFormat" : "StatusRepoDirtyFormat";
        return (true, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(key), state.Entries.Count, branch));
    }

    /// <summary>Takes the slot's words away without waiting for anything to answer. A run can commit, check out
    /// or build without Hub's file tool, and from the moment one ends the cached read is no longer a fact — and a
    /// persistent surface that keeps repeating a number the run just invalidated is worse than one that says
    /// nothing. The next read that lands brings it back.</summary>
    private void InvalidateRepoIndicator()
    {
        // Same construction-window as <see cref="UpdateRepoIndicator"/>: a run cannot complete before the strip
        // exists, but the subscription is older than the strip is, and this is the one handler that would find
        // out the hard way.
        if (RepoIndicatorText is null) return;
        RepoIndicator.IsVisible = false;
        RepoIndicatorText.Text = "";
    }

    internal bool RepoIndicatorVisibleForCheck => RepoIndicator.IsVisible;
    internal string RepoIndicatorTextForCheck => RepoIndicatorText.Text ?? "";

    /// <summary>Presses the strip's own button, so a check drives the wiring rather than the shell method the
    /// button happens to end up calling.</summary>
    internal void ClickRepoIndicatorForCheck()
        => RepoIndicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Switches the data root. **Deliberately does not rebuild the window** — the WPF version news
    /// up a <c>MainWindow</c> and closes the old one because its state, pages, and log are all bound
    /// to the root. Here we only rebuild <see cref="HubWorkspace"/> and drop the page cache: pages
    /// hold workspace references, and after a root switch the old selections (selected project,
    /// device list, expanded new-project panel) should be invalid anyway.
    ///
    /// The payoff: switching the data root no longer depends on the shell shape, so we don't have
    /// to wait for the main window shape to be finalized.
    /// </summary>
    /// <returns>Whether it actually switched. Does nothing when the target equals the current
    /// root.</returns>
    internal bool SwitchDataRoot(string directory)
    {
        // Switching is forbidden while an operation is running: the operation holds the old root's
        // downloader, log, and toolchain directories, and switching mid-run would make it write into
        // a directory that no longer belongs to the current session.
        if (_workspace.IsBusy)
        {
            throw new InvalidOperationException(HubStrings.Get("WaitForOperation"));
        }

        var path = PreferencesStore.VerifyDirectory(directory);
        if (path.Equals(_workspace.Store.Root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Persist first, then switch. Once switched there's no going back to the old instance, so a
        // failed write must surface the error in place, on the old state — otherwise the UI would
        // point at the new root while the settings file still says the old root, silently reverting
        // on the next launch.
        var previous = _preferences.DataRoot;
        _preferences.DataRoot = path;
        try
        {
            _preferencesStore.Save(_preferences);
        }
        catch
        {
            _preferences.DataRoot = previous;
            throw;
        }

        ReplaceWorkspace(new HubWorkspace(path, _preferences, _preferencesStore));
        SetStatus(HubStrings.Get("DataChanged"));
        return true;
    }

    private void ReplaceWorkspace(HubWorkspace next)
    {
        _workspace.Dispose();
        _workspace = next;
        _workspace.Owner = this;

        // Events are wired on the new instance, so re-subscribing here won't append the log twice.
        WireWorkspace();

        // Old pages hold the old workspace and must be dropped wholesale; the next NavigateTo will
        // reconstruct them.
        _pages.Clear();

        // The assistant's providers and conversations also live under the data root, so it must be rebuilt
        // for the new one and the page (if it was ever built) dropped with it — otherwise the assistant would
        // keep listing the previous root's conversations and saving new ones into a directory that is no
        // longer current. Rebuilding rather than re-pointing mirrors how the pages are handled.
        _chat.Dispose();
        _chat = new ChatWorkspace(next.Store.Root);
        WireChatSeams();
        WireChatRuns();
        // A new workspace has read nothing yet, so the strip's repository slot has nothing true to say. Left
        // standing, it would report the previous root's dirty count against a directory nobody is looking at.
        InvalidateRepoIndicator();
        // The pane may be living in the window-wide overlay, and it belongs to the panel about to be dropped:
        // left there, a dead workspace's plan would keep covering a page that can no longer produce one.
        RestoreInspector();
        _chatPanel = null;
        // …and the same for the pane itself. `RestoreInspector` only acts on the overlay, so without this the
        // dropped panel's pane stays hosted in the column: the next `NavigateTo` repaints the column from the new
        // root, finds a pane already there, and shows the previous data root's plan under a conversation that has
        // no idea it exists. Which tab and which plan turn go back to the defaults for the same reason — they were
        // answers about a transcript that is gone.
        SetInspectorPane(null);
        _inspectorTab = "plan";
        _inspectorTurn = -1;

        // The conversation sidebar holds the old workspace; rebuild it for the new one.
        _chatSidebar = BuildChatSidebar();
        ChatSidebarHost.Content = _chatSidebar;

        // The log panel holds the previous root's content; leaving it would point people at a
        // directory no one is looking at anymore.
        ActivityLog.Text = "";
        ShowLog(false);

        InitializeChrome();
        NavigateTo(_currentKey);
    }

    /// <summary>The language in settings. <see cref="PageShots"/> relies on it to switch back after
    /// changing language.</summary>
    internal string PreferredLanguage => _preferences.Language;

    /// <summary>
    /// Switches language and repaints the UI immediately. This is the same path as picking a
    /// language in the settings page dropdown; only the caller changes from a person to
    /// <c>--smoke-pages</c>.
    ///
    /// **Writes the settings file**: language is a user preference, and not persisting it would
    /// diverge from the current selection in the settings page. Point <c>--preferences</c> at a
    /// temporary file to avoid touching the user's own.
    /// </summary>
    internal void UseLanguage(string language)
    {
        _preferences.Language = language;
        _preferencesStore.Save(_preferences);
        HubStrings.Apply(language, Application.Current!);
        ApplyLanguage();
    }

    /// <summary>
    /// Switches theme: write settings → apply the variant. The **single owner** of that sequence —
    /// the settings page's picker and the bottom menu's appearance items are two views of it. Same
    /// order as <see cref="UseLanguage"/> (persist first), for the same reason: a failed write must
    /// leave the UI untouched rather than showing a theme the settings file doesn't have — it would
    /// silently revert on the next launch.
    ///
    /// Unlike the language path there is **nothing to repaint afterwards**: every token is a
    /// DynamicResource and re-resolves on its own, including in windows built long before the switch.
    /// </summary>
    /// <returns>Null on success; the error message on failure (the caller decides how to show it).</returns>
    internal string? UseTheme(string theme)
    {
        theme = HubTheme.Normalize(theme);
        var previous = ThemeService.Current;
        if (theme == previous)
        {
            return null;
        }

        try
        {
            _preferences.Theme = theme;
            _preferencesStore.Save(_preferences);
            ThemeService.Apply(theme);
            return null;
        }
        catch (Exception ex)
        {
            // On failure, revert, otherwise the UI would show a theme that disagrees with the settings file.
            _preferences.Theme = previous;
            ThemeService.Apply(previous);
            return ex.Message;
        }
    }

    /// <summary>
    /// Shows/hides the bottom entry's popup. Built fresh on every open: labels follow the UI language
    /// and exactly one checkmark tracks <see cref="ThemeService.Current"/> with no sync machinery.
    /// </summary>
    private void ToggleBottomMenu()
    {
        if (BottomMenuForCheck is { IsOpen: true } open)
        {
            open.Hide();
            return;
        }

        FlyoutBase.SetAttachedFlyout(BottomMenuButton, BuildBottomMenu());
        FlyoutBase.ShowAttachedFlyout(BottomMenuButton);
    }

    /// <summary>
    /// The bottom entry's popup: Settings (navigates like the gear click used to) and an Appearance
    /// submenu holding the three theme values as radio items. <see cref="SettingsPage.ThemeTextKey"/>
    /// stays the theme→copy-key mapping so a new theme can't leave its label behind. (Avalonia 12 has
    /// no MenuFlyoutItem family: the flyout hosts <see cref="MenuItem"/>s directly, and a MenuItem with
    /// children is the submenu.)
    /// </summary>
    internal MenuFlyout BuildBottomMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };

        var settings = new MenuItem { Header = HubStrings.Get("Settings") };
        settings.Click += (_, _) => NavigateTo("Settings");
        menu.Items.Add(settings);

        var appearance = new MenuItem { Header = HubStrings.Get("Appearance") };
        foreach (var theme in HubTheme.All)
        {
            var item = new MenuItem
            {
                Header = HubStrings.Get(SettingsPage.ThemeTextKey(theme)),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = theme == ThemeService.Current,
                Tag = theme,
            };
            item.Click += (_, _) =>
            {
                var error = UseTheme(theme);
                if (error is not null)
                {
                    SetStatus(error);
                }

                // Re-sync every checkmark: a radio click checks itself but the group's behaviour is
                // left to us, so without this the user could end up with no theme checked (clicking
                // the already-checked one) while the theme itself didn't move.
                foreach (var other in appearance.Items.OfType<MenuItem>())
                {
                    other.IsChecked = other.Tag as string == ThemeService.Current;
                }
            };
            appearance.Items.Add(item);
        }

        menu.Items.Add(appearance);
        return menu;
    }

    /// <summary>Recomputes all **imperatively** written copy after a language change (DynamicResource
    /// doesn't cover them).</summary>
    internal void ApplyLanguage()
    {
        // InitializeChrome also rewrites the chrome tooltips (gear + bottom text block), so a language
        // switch reaches them.
        InitializeChrome();
        _chatSidebar?.Reload();
        UpdatePageTitle();
        UpdateChatRunsStatus();
        // The strip's repository slot is composed copy, not a DynamicResource, so a language switch has to be
        // told or the row keeps the previous tongue while everything around it changes.
        UpdateRepoIndicator();

        foreach (var page in _pages.Values)
        {
            switch (page)
            {
                case SettingsPage settings:
                    settings.Reload();
                    break;
                // The assistant's copy is written in code (it composes values), so it only follows a language
                // switch if it is told to — the same reason the settings page reloads here.
                case ChatPanel chat:
                    chat.Reload();
                    break;
            }
        }

        // Copy computed in code inside pages (headers, empty-state hints, button labels) must all be
        // recomputed.
        _workspace.Refresh();

        // Switching language is the second moment to warn about fonts — and the more accurate one:
        // the user has actively chosen Chinese, and only now will they actually see boxes. Do it
        // last — recompute the UI copy first, so when the user dismisses the dialog they see the
        // fully switched UI rather than a half-new, half-old one.
        CjkFontNotifier.NotifyIfNeeded(this);
    }

    private void OnNavigated(RadioButton button, string name)
    {
        if (button.IsChecked == true)
        {
            NavigateTo(name);
        }
    }

    private void WireWorkspace()
    {
        _workspace.Logged += line =>
        {
            ActivityLog.Text += line + Environment.NewLine;
            if (ActivityLog.Text is { Length: > 120000 } text)
            {
                ActivityLog.Text = text[^80000..];
            }

            // Moving the caret to the end is how we "scroll to bottom". Avalonia's TextBox has no
            // ScrollToEnd.
            ActivityLog.CaretIndex = ActivityLog.Text?.Length ?? 0;
        };

        _workspace.StatusChanged += text => Status.Text = text;

        // During an operation pages are disabled and cancel is enabled — matching WPF's
        // Pages.IsEnabled / CancelButton.IsEnabled.
        _workspace.BusyChanged += busy =>
        {
            PageHost.IsEnabled = !busy;
            CancelButton.IsEnabled = busy && _workspace.CanCancel;
        };

        _workspace.Failed += () => ShowLog(true);

        // Dialogs (e.g. "prebuilt library settings") can't reference the main window directly, so
        // page navigation is forwarded here through the workspace.
        _workspace.NavigateRequested += key =>
        {
            if (PageKeys.Contains(key))
            {
                NavigateTo(key);
            }
        };
    }

    /// <summary>
    /// The assistant page, for the shell self-check. Navigates to it first if needed (which also proves the
    /// lazy construction path works).
    /// </summary>
    internal ChatPanel OpenAssistant()
    {
        NavigateTo("Assistant");
        return _chatPanel!;
    }

    /// <summary>The conversation sidebar section, for the shell self-check.</summary>
    internal ChatSidebar ChatSidebarSection => _chatSidebar!;

    internal Button AssistantSearchButtonForCheck => AssistantSearchButton;
    internal Button AssistantNewButtonForCheck => AssistantNewButton;
    internal bool AssistantActionsRightmostForCheck
        => NavAssistant.Parent is Grid grid
           && AssistantSearchButton.Parent == grid
           && AssistantNewButton.Parent == grid
           && Grid.GetColumnSpan(NavAssistant) == grid.ColumnDefinitions.Count
           && Grid.GetColumn(AssistantSearchButton) == grid.ColumnDefinitions.Count - 2
           && Grid.GetColumn(AssistantNewButton) == grid.ColumnDefinitions.Count - 1;
    internal void ClickAssistantSearchForCheck()
        => AssistantSearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void ClickAssistantNewForCheck()
        => AssistantNewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The top-bar title text as shown, for the shell self-check.</summary>
    internal string PageTitleText => PageTitle.Text ?? "";

    // ── Sidebar collapse, for the shell self-check ──
    internal bool SidebarExpandedForCheck => IsSidebarExpanded;
    internal double SidebarWidthForCheck => Sidebar.Width;
    internal bool SidebarClipsForCheck => Sidebar.ClipToBounds;
    internal bool ResizeGripVisibleForCheck => ResizeGrip.IsVisible;
    internal void ToggleSidebarForCheck()
        => SidebarToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The title block (sidebar top), for the shell self-check. The block is deliberately text
    /// only: <c>BrandHeaderHasNoIconForCheck</c> is false (i.e. an icon came back) when the header's
    /// visual subtree contains any image-bearing shape, which is the regression this guards.</summary>
    internal bool BrandHeaderHasNoIconForCheck => !BrandHeader.GetVisualDescendants().Any(
        descendant => descendant is Avalonia.Controls.Shapes.Shape or Avalonia.Controls.Image);

    internal int BrandHeaderLinesForCheck
        => new[] { BrandTitle.Text, BrandVersion.Text }.Count(text => text is { Length: > 0 });

    internal string BrandTitleTextForCheck => BrandHeader.GetVisualDescendants()
        .OfType<TextBlock>()
        .Select(text => text.Text ?? "")
        .FirstOrDefault(text => text == "Axmol Hub") ?? "";

    internal string BrandVersionTextForCheck => BrandVersion.Text ?? "";
    internal bool PreviewBadgeVisibleForCheck => PreviewBadge.IsVisible;
    internal string PreviewBadgeTextForCheck
        => PreviewBadge.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
    internal bool PreviewBadgeUsesThemeTokensForCheck
        => ThemeProbe.IsToken(PreviewBadge.Background, "Hub.PreviewBadgeBackground")
           && ThemeProbe.IsToken(PreviewBadge.BorderBrush, "Hub.PreviewBadgeBorder")
           && PreviewBadge.GetVisualDescendants().OfType<TextBlock>()
               .Any(text => ThemeProbe.IsToken(text.Foreground, "Hub.PreviewBadgeForeground"));

    internal void SetPreviewBadgeVisibleForCheck(bool visible) => PreviewBadge.IsVisible = visible;

    /// <summary>
    /// The assistant's state, for the shell self-check to assert against — and to install a scripted chat
    /// client so the page can be verified with no network and no API key.
    /// </summary>
    internal ChatWorkspace Chat => _chat;

    /// <summary>The strip's background-reply counter, read as the user sees it.</summary>
    internal bool ChatRunsVisibleForCheck => ChatRuns.IsVisible;
    internal string ChatRunsTextForCheck => ChatRuns.Text ?? "";

    /// <summary>The strip row and the log button's own plate, measured while the repository slot is lit. Adding a
    /// sixth column to a 28-pixel row is only safe if the row did not grow and the square glyph did not become a
    /// rectangle, and neither of those is visible in an assertion that only reads strings.</summary>
    internal double StatusStripHeightForCheck => StatusStrip.Bounds.Height;
    internal double RepoIndicatorWidthForCheck => RepoIndicator.Bounds.Width;
    internal (double Width, double Height) LogTogglePlateForCheck => (LogToggle.Bounds.Width, LogToggle.Bounds.Height);
    internal double StatusWidthForCheck => Status.Bounds.Width;

    /// <summary>Whether the assistant page is the one on screen.</summary>
    internal bool AssistantVisible => _currentKey == "Assistant";

    /// <summary>Whether the window is in front of the user: focused, and not minimized. Read live rather than
    /// cached, because a cached copy could become stale across a data-root switch or a recreated window.</summary>
    private bool WindowVisibleNow
        => _windowVisibleOverride ?? (IsActive && WindowState != WindowState.Minimized);

    /// <summary>The visibility answer used to suppress redundant system notifications while the user is already
    /// looking at the session that needs approval.</summary>
    internal AttentionVisibility AttentionNow => new(WindowVisibleNow, AssistantVisible);

    /// <summary>Forces the desktop half of <see cref="AttentionNow"/> for the self-check; <c>null</c> hands the
    /// answer back to the platform. The suite must reset it — an override left behind would stop the notification
    /// gate from reading the desktop.</summary>
    internal void SetWindowVisibleForCheck(bool? visible) => _windowVisibleOverride = visible;

    /// <summary>The settings gear's tooltip, for the shell self-check: the button is icon-only, so the tip is
    /// the only place its name is spelled out.</summary>
    internal string SettingsLabel => ToolTip.GetTip(SettingsButton) as string ?? "";

    /// <summary>Raises the gear's click path as a real click would — the self-check walks the same code the
    /// user's click does instead of calling <see cref="NavigateTo"/> directly.</summary>
    internal void ClickSettingsGearForCheck() => SettingsButton.RaiseEvent(
        new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    /// <summary>The bottom text block's tooltip, for the shell self-check: the block's visible text names
    /// the machine, so the tip is where "this opens settings and appearance" is spelled out.</summary>
    internal string BottomMenuLabel => ToolTip.GetTip(BottomMenuButton) as string ?? "";

    /// <summary>Opens the bottom text block's popup the way the user's click does — the self-check walks
    /// the same code instead of calling <see cref="BuildBottomMenu"/> directly.</summary>
    internal void ClickBottomMenuForCheck() => BottomMenuButton.RaiseEvent(
        new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    /// <summary>The popup the bottom text block last attached, for the shell self-check.</summary>
    internal MenuFlyout? BottomMenuForCheck => FlyoutBase.GetAttachedFlyout(BottomMenuButton) as MenuFlyout;

    private void WireChrome()
    {
        // The update dot reflects UpdateService.Last; both the startup check and the settings-page
        // check feed it. A check can complete on a background continuation, so marshal before touching
        // the control. Subscribed once — WireChrome only runs from the constructor.
        UpdateService.Instance.Changed += () => Dispatcher.UIThread.Post(SyncUpdateBadge);
        SyncUpdateBadge();

        CancelButton.Click += (_, _) => _workspace.Cancel();

        CopyErrorButton.Click += async (_, _) =>
        {
            var text = _workspace.LastError.Length == 0 ? ActivityLog.Text ?? "" : _workspace.LastError;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        };

        OpenLogsButton.Click += (_, _) => _workspace.Open(_workspace.Log.Folder);

        // Closing is forbidden while an operation is running (handled in WPF's Closing), otherwise
        // child processes would be left behind.
        Closing += (_, e) =>
        {
            if (!_workspace.IsBusy)
            {
                return;
            }

            e.Cancel = true;
            _workspace.Cancel();
            SetStatus(HubStrings.Get("Cancelling"));
        };

        Closed += (_, _) => _workspace.Dispose();
        Closed += (_, _) => _chat.Dispose();
        Closed += (_, _) => _attention.Dispose();
    }

    private void SyncUpdateBadge()
    {
        ShowUpdateBadge(UpdateService.Instance.Last?.Result is UpdateService.CheckResult.UpdateAvailable);
        // The dot is the only place an update is announced outside the settings page, so it has to say
        // *which* version — otherwise "there is an update" is all the user gets without going there.
        _updateDotTip.Text = FormatUpdateBadgeTip(UpdateService.Instance.PendingVersion);
    }

    /// <summary>
    /// The update dot's tooltip: the version when one is known, the plain "update available" line when
    /// it isn't. Pure so the branch can be asserted — a version that never gets appended still reads as
    /// a finished tooltip.
    /// </summary>
    internal static string FormatUpdateBadgeTip(string? version)
        => version is { Length: > 0 }
            ? string.Format(HubStrings.Get("UpdateDotTooltipVersion"), version)
            : HubStrings.Get("UpdateDotTooltip");

    /// <summary>The update dot's tooltip text as it is installed right now, for the shell self-check.</summary>
    internal string UpdateDotTip => _updateDotTip.Text ?? "";

    /// <summary>
    /// Shows/hides the update dot on the settings gear. Passive signalling only — no prompt: the
    /// dot says "there's something new in Settings"; the user goes there to see and act on it.
    /// </summary>
    internal void ShowUpdateBadge(bool show) => SettingsUpdateDot.IsVisible = show;

    /// <summary>
    /// The conversation sidebar, built against the current chat workspace. It takes the preference store because
    /// which groups are folded is state the user left and expects back after a restart, and the status strip
    /// because re-pointing a workspace can fail on a fact about the disk — a folder that moved again while the
    /// dialog was open — and that answer belongs in the strip rather than in a card.
    /// </summary>
    private ChatSidebar BuildChatSidebar()
        => new(_chat, _preferencesStore, _preferences) { StatusReporter = SetStatus };

    /// <summary>
    /// Builds the conversation sidebar section, applies persisted sidebar state, and wires the collapse
    /// toggle, the drag grip and the log chevron.
    /// </summary>
    private void InitializeSidebar()
    {
        _chatSidebar = BuildChatSidebar();
        ChatSidebarHost.Content = _chatSidebar;

        SidebarToggle.Click += (_, _) => SetSidebarExpanded(!IsSidebarExpanded);
        ToolTip.SetTip(SidebarToggle, HubStrings.Get("SidebarToggleTip"));

        ResizeGrip.PointerPressed += OnGripPressed;
        ResizeGrip.PointerMoved += OnGripMoved;
        ResizeGrip.PointerReleased += OnGripReleased;

        LogToggle.Click += (_, _) => ShowLog(!LogBody.IsVisible);

        // Apply persisted state: width first, then collapsed so a collapsed sidebar really starts at zero.
        Sidebar.Width = Math.Clamp(_preferences.SidebarWidth, SidebarMin, SidebarMax);
        SetSidebarExpanded(!_preferences.SidebarCollapsed);
    }

    private bool IsSidebarExpanded => Sidebar.Width > 1;

    private void SetSidebarExpanded(bool expanded)
    {
        if (expanded)
        {
            var width = Sidebar.Width < SidebarMin ? _preferences.SidebarWidth : Sidebar.Width;
            Sidebar.Width = Math.Clamp(width, SidebarMin, SidebarMax);
            ResizeGrip.IsVisible = true;
        }
        else
        {
            Sidebar.Width = 0;
            ResizeGrip.IsVisible = false;
        }

        if (_preferences.SidebarCollapsed != !expanded)
        {
            _preferences.SidebarCollapsed = !expanded;
            _preferencesStore.Save(_preferences);
        }
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _grabbing = true;
        _grabStartX = e.GetPosition(this).X;
        _grabStartWidth = Sidebar.Width;
        e.Pointer.Capture(ResizeGrip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_grabbing) return;
        var width = _grabStartWidth + (e.GetPosition(this).X - _grabStartX);
        if (width < SidebarCollapseThreshold)
        {
            _grabbing = false;
            e.Pointer.Capture(null);
            SetSidebarExpanded(false);
            return;
        }
        Sidebar.Width = Math.Clamp(width, SidebarMin, SidebarMax);
    }

    private void OnGripReleased(object? sender, PointerEventArgs e)
    {
        if (!_grabbing) return;
        _grabbing = false;
        e.Pointer.Capture(null);

        if (Sidebar.Width >= SidebarCollapseThreshold)
        {
            _preferences.SidebarWidth = Math.Clamp(Sidebar.Width, SidebarMin, SidebarMax);
            _preferencesStore.Save(_preferences);
        }
    }

    /// <summary>Shows/hides the log body and flips the strip chevron.</summary>
    internal void ShowLog(bool show)
    {
        LogBody.IsVisible = show;
        LogToggleChevron.RenderTransform = new RotateTransform(show ? 180 : 0);
    }

    /// <summary>Updates the top-bar title for the current page.</summary>
    private void UpdatePageTitle()
    {
        PageTitle.Text = _currentKey switch
        {
            "Assistant" => _chat.ActiveConversation is { Title.Length: > 0 } active
                ? active.Title
                : HubStrings.Get("Assistant"),
            "Projects" => HubStrings.Get("Projects"),
            "Installs" => HubStrings.Get("Installs"),
            "Toolchains" => HubStrings.Get("Toolchains"),
            "Settings" => HubStrings.Get("Settings"),
            _ => "",
        };
    }

    private void InitializeChrome()    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        BrandVersion.Text = "v" + version;
        PreviewBadge.IsVisible = HubReleaseInfo.IsPrereleaseBuild;
        Title = "Axmol Hub " + BrandVersion.Text;
        // Installed once (see _updateDotTip); only its text moves, and it is rewritten on every
        // InitializeChrome — which ApplyLanguage calls — so a language switch reaches it too.
        ToolTip.SetTip(SettingsUpdateDot, _updateDotTip);
        _updateDotTip.Text = FormatUpdateBadgeTip(UpdateService.Instance.PendingVersion);

        // The settings gear is icon-only, so its tooltip is the only place the name "Settings" appears for it.
        // Set here rather than in ApplyLanguage so it is also in place at construction time.
        ToolTip.SetTip(SettingsButton, HubStrings.Get("Settings"));

        // The bottom text block's visible text names the machine, not the menu; the tooltip is where
        // "this opens settings and appearance" is spelled out. Same place for the same reason.
        ToolTip.SetTip(BottomMenuButton, HubStrings.Get("BottomMenuTip"));

        // The WPF version hard-codes "AXMOL 2.11 LTS" in the bottom-left. The Avalonia version
        // computes it from the **default engine** at runtime: hard-coding the version number would
        // become wrong the day v3 ships — exactly the A1 (single-engine version binding) class of bug.
        var state = _workspace.State;
        var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
        BrandLine.Text = engine is null ? "AXMOL" : "AXMOL " + engine.Version;

        // The WPF version hard-codes "Windows x64". The Avalonia version runs on three platforms and
        // computes it from the real host.
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        HostLine.Text = os + " " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Hands a directory to the system shell to open. Avalonia has no wrapper for WPF's
    /// <c>Process.Start(UseShellExecute)</c>, so just start the process directly. Failures go to
    /// the status bar instead of being thrown: clicking "open directory" shouldn't blow up the
    /// whole program.
    /// </summary>
    private void OpenFolder(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }
}
