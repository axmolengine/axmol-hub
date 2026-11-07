using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
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
    private TaskbarInProgress? _taskbarProgress;
    private readonly Dictionary<string, Control> _pages = [];
    private bool? _attentionDiagnosticBadge;
    private string? _attentionDiagnosticConversationId;
    private bool _windowIsOpen;
    private bool _trayCloseEnabled;
    private readonly List<ToastEntry> _toasts = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _toastPointerInside;

    private sealed class ToastEntry(UserFeedback feedback, DateTimeOffset expiresAt)
    {
        public UserFeedback Feedback { get; } = feedback;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public Border? View { get; set; }
    }

    /// <summary>Whether this window has lost the foreground at least once since it was shown. A notification click
    /// arrives as a *return* to the foreground, and a window that never left cannot be returning: without this,
    /// the activation that finishes start-up consumes the pending conversation before any click exists — measured
    /// on GNOME 46, where it fired 13 ms after the notification was armed.</summary>
    private bool _foregroundHasLeftWindow;

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
    /// The task slot's tooltip content. Installed once and rewritten **in place**: re-assigning
    /// <c>ToolTip.Tip</c> closes an open tooltip, and a new value assigned while it is closed does not
    /// re-open it (the update dot used this same holder, and the settings page's progress tip still does).
    /// The slot's tip moves on every progress tick, so a fresh string each time would make the tooltip
    /// impossible to read while a download runs.
    /// </summary>
    private readonly TextBlock _taskSlotTip = new();

    private string _currentKey = "";

    /// <summary>The press that is armed on the title bar, or null when none is. Holding the whole event rather
    /// than a point is what lets the move handler hand the platform the press it actually received; and a
    /// null/non-null test is the whole "is a drag armed" state, which is what a check reads — it cannot drive the
    /// arm that consumes this, because that one enters the platform's move loop.</summary>
    private PointerPressedEventArgs? _titleBarPress;

    /// <summary>Where that press landed, in the window's own coordinates — the origin the drag threshold is
    /// measured from.</summary>
    private Point _titleBarPressOrigin;

    /// <summary>The client size the window has when it is not maximized, which is the size a restore-drag has to
    /// place against. Avalonia 12.1.3 remembers nothing of the kind for the app (there is no
    /// <c>Window.RestoreBounds</c>), and <c>Width</c>/<c>Height</c> cannot serve: <c>Window.HandleResized</c>
    /// overwrites them with whatever the platform last reported, so while the window is maximized they *are* the
    /// screen size. Recorded from <c>Resized</c>, and only while the state is neither maximized nor filled.</summary>
    private NormalGeometry? _normalGeometry;

    /// <summary>Where the window sits and how big its client is when the window manager is not holding it — the
    /// geometry a restore (from the maximized state or from the work-area fill) goes back to.</summary>
    internal readonly record struct NormalGeometry(PixelPoint Position, Size ClientSize);

    /// <summary>Whether the shell is filling the work area itself instead of asking the window manager to maximize
    /// (see <see cref="WslWorkAreaFill"/>: on WSLg a window the window manager maximizes loses the pointer over its
    /// own caption). Both the caption chrome and the drag read this instead of <c>WindowState</c>, because on this
    /// host the maximized state is never the filled state.</summary>
    private bool _fillsWorkArea;

    /// <summary>Armed while the window manager is asked to maximize once, so the size it chooses can be recorded —
    /// it is the only number on this host that says how much of the screen a maximized window really gets.</summary>
    private bool _learningFillRect;

    /// <summary>Set once the size is known and the window has been handed back to the window manager: the fill is
    /// written a beat later, on a timer (see <see cref="LearnFillRect"/>), and this is what keeps a toggle that
    /// arrives in the meantime from writing the geometry into the handover, where it would be erased.</summary>
    private bool _fillPending;

    /// <summary>The work area to fill, in physical pixels of the window's *content*: origin from the screen, size
    /// from the window manager. Learned once per run.</summary>
    private PixelRect? _fillRect;

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
        // Diagnostic before Initialize: the macOS channel reports its environment note (for example "not inside
        // an .app bundle") from Initialize, and a sink attached afterwards would lose the one line that explains
        // why no banner will ever arrive.
        _attention.Diagnostic += message => Dispatcher.UIThread.Post(
            () => WriteLog("[System attention] " + message));
        _attention.NotificationActivated += NotificationActivated;
        _toastTimer.Tick += (_, _) => ExpireToasts();
        _attention.Initialize(id => NotificationActivated(id));
        // The route GNOME gives a click on a notification's body: the shell raises the already-running window
        // instead of re-running the desktop entry, so no deeplink and no second instance ever reach the broker.
        // That raise produces nothing but this event, and the conversation it belongs to is the one the
        // attention service remembered when it sent the notification. It has to be a *return*: the activation
        // that finishes start-up arrives on its own, ~13 ms after the first notification is armed in the
        // measured run, and consuming there would spend the click before the user ever had one.
        Activated += (_, _) => OnWindowActivated();
        Deactivated += (_, _) => _foregroundHasLeftWindow = true;
        WireChatSeams();
        WireChatRuns();

        InitializeComponent();
        ConfigureLinuxWindowSurface();
        InitializeSidebar();
        InitializeTitleBar();
        InitializeInspector();
        InitializePictureViewer();
        InitializeSettings();
        InitializeMacComputerPermissions();
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

        // Settings is no longer a nav item and no longer a page: the gear at the bottom of the rail lifts the
        // overlay card. It toggles rather than only opens, because the gear is the one control that stays on
        // screen whatever the card is doing, and a button that can never undo itself invites a second way out.
        SettingsButton.Click += (_, _) => ToggleSettings();

        // The update's own button, between the host block and the gear — the update's only home. The press
        // means one of three things and the state decides which (see OnUpdateActionClicked): fetch it, open
        // the queue it is downloading in, or arm/apply the restart.
        UpdateActionButton.Click += (_, _) => OnUpdateActionClicked();

        // The brand/host text block beside the gear opens the popup menu (Settings + Appearance + language).
        // The menu is rebuilt on every open, so its labels and ticks are always current without any sync
        // machinery.
        BottomMenuButton.Click += (_, _) => ToggleBottomMenu();

        // Default to the "Projects" page, matching the WPF version (WPF uses NavProjects IsChecked="True").
        NavigateTo("Projects");

        // Font conclusions must be handled only after the window is on screen: a modal's owner
        // must be shown first.
        Opened += (_, _) => ReportFonts();
        Opened += (_, _) =>
        {
            _windowIsOpen = true;
            if (!App.Options.IsAutomation) _taskbarProgress ??= TaskbarInProgress.ForWindow(this);
            UpdateChatRunsStatus();
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
        if (ShouldShowForRequest(!IsVisible, App.Options.IsAutomation)) Show();
        if (ConsumeActivationTarget()) return;
        Activate();
    }

    internal static bool ShouldShowForRequest(bool isHidden, bool isAutomation) => isHidden && !isAutomation;

    /// <summary>The window came back to the foreground — the only signal a desktop gives for a click on a
    /// notification when it raises the running window instead of naming a conversation. Gated on the window
    /// having genuinely left the foreground, see <see cref="_foregroundHasLeftWindow"/>.</summary>
    private void OnWindowActivated()
    {
        SyncMacComputerPermissions();
        if (_foregroundHasLeftWindow) _ = ConsumeActivationTarget();
    }

    /// <summary>Opens the conversation a notification was about when the window is being raised for one, and
    /// reports whether it did. A raise with nothing pending — Alt-Tab, the taskbar, a second instance asked to
    /// show itself — stays the plain raise it always was.</summary>
    private bool ConsumeActivationTarget()
    {
        if (_attention.TryTakeActivationTarget(DateTimeOffset.UtcNow) is not { } conversationId) return false;
        WriteLog($"[System attention] The window was raised with a notification pending; "
                 + $"opening conversation {conversationId}.");
        OpenConversationFromAttention(conversationId);
        return true;
    }

    internal async Task HandleInstallLinkAsync(string value)
    {
        if (EngineInstallLink.IsActivation(value))
        {
            ActivateFromRequest();
            return;
        }

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

        LeaveMaximizedForActivation();
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
    /// destination has a real page. <c>Assistant</c> is included: it is a full page now, not a drawer.
    /// <c>Settings</c> is not: it left the page system for the overlay layer, which no navigation lands on and
    /// which the page-shot sweep therefore no longer photographs — see <see cref="OpenSettings"/>.</summary>
    internal static string[] PageKeys => ["Projects", "Installs", "Toolchains", "Assistant"];

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
        // Both window-level layers are answers about the page underneath them: a picture left open over a page
        // you navigated away from is a picture you can no longer reach the transcript of, and a settings card
        // that outlives a navigation is a scrim sitting over the page the reader just asked for.
        if (PictureViewerHost.IsVisible) ClosePictureViewer();
        CloseSettings();
        UpdatePageTitle();
        return page;
    }

    private void OpenMarkdownFile(string conversationId, string path, int? line, int? column)
    {
        var root = conversationId.Length > 0 ? _chat.WorkspaceRootFor(conversationId) : _chat.ActiveWorkspaceRoot;
        var relative = Path.IsPathRooted(path) ? path
            : Path.GetFullPath(path, root ?? Environment.CurrentDirectory);
        var scope = _chat.EditorScopeFor(conversationId, root);
        var failure = CodeFileWindow.ShowEditor(this, relative, () => scope, RefreshInspector);
        if (failure is not null)
        {
            _ = HubDialog.ShowAsync(this, HubStrings.Get("WorkspaceFiles"), HubStrings.Get(failure));
            return;
        }

        Dispatcher.UIThread.Post(() => _workspaceView?.GoToPosition(relative, line, column), DispatcherPriority.Loaded);
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
        // opens in the inspector column beside the page. Which of the column's two homes that pane is in, and
        // whether the column is up at all, is not asked of the panel: both now live on the title bar's own
        // buttons, which are wired straight to the two methods here (see InitializeInspector).
        panel.ShowPictureViewer = (set, index) => OpenPictureViewer(set, index);
        panel.StatusMessage = ShowFeedback;
        panel.OpenLocalFile = OpenMarkdownFile;
        panel.OpenInspector = (tab, turnIndex) => OpenInspector(tab, turnIndex);
        panel.RefreshInspector = RefreshInspector;
        // The pane's header tabs are the reader's choice, and this column repaints on a schedule of its own —
        // every transcript move, every repository read that lands. The shell asks for a tab by name on each of
        // those, so it has to be told which one the reader is on, or their click is undone a token later.
        panel.InspectorTabFollowed = tab => _inspectorTab = tab;
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
        // Read through the field on every call, not captured: switching the data root replaces _workspace, and a
        // seam that kept the old one would have the assistant hold the gate of a workspace nobody is looking at.
        _chat.ChangeGateProvider = subject => _workspace.TakeChangeGate(subject);
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
    /// Settings is not in this loop because it is not a page any more — it is the overlay layer, and an overlay
    /// has no nav item to light up. The four radio buttons are the whole navigation, and exactly one of them is
    /// ever checked.
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

    private long? _activeTaskStatusToken;
    private long _latestTaskStatusToken;
    private long _lastFinishedTaskStatusToken;

    internal void SetStatus(string text)
    {
        // Kept for task-independent shell callers that have not yet moved to structured feedback.
        if (_activeTaskStatusToken is null) Status.Text = text;
    }

    internal void ShowFeedback(UserFeedback feedback) => ShowToast(feedback);

    private void ShowToast(UserFeedback feedback)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowFeedback(feedback));
            return;
        }

        var existing = _toasts.FirstOrDefault(toast => toast.Feedback.Key == feedback.Key);
        if (existing is not null)
        {
            existing.ExpiresAt = DateTimeOffset.UtcNow + ToastDuration(feedback.Kind);
            return;
        }

        while (_toasts.Count >= 2)
        {
            RemoveToast(_toasts[0]);
        }

        var entry = new ToastEntry(feedback, DateTimeOffset.UtcNow + ToastDuration(feedback.Kind));
        entry.View = BuildToast(entry);
        _toasts.Add(entry);
        ToastStack.Children.Add(entry.View);
        _toastTimer.Start();
    }

    private Border BuildToast(ToastEntry entry)
    {
        var border = new Border { Classes = { "toast", entry.Feedback.Kind.ToString().ToLowerInvariant() }, IsHitTestVisible = true };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        var mark = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        mark.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new DynamicResourceExtension(entry.Feedback.Kind switch
        {
            UserFeedbackKind.Success => "Hub.Success",
            UserFeedbackKind.Warning => "Hub.WarningText",
            UserFeedbackKind.Error => "Hub.DangerText",
            _ => "Hub.AccentBorder",
        }));
        row.Children.Add(mark);

        var text = new TextBlock { Text = entry.Feedback.Text, Classes = { "toast-text" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var close = new Button { Content = "×", Classes = { "toast-close" }, Tag = entry };
        close.Click += (_, _) => RemoveToast(entry);
        Grid.SetColumn(close, 2);
        row.Children.Add(close);
        border.Child = row;
        border.PointerEntered += (_, _) => _toastPointerInside = true;
        border.PointerExited += (_, _) => _toastPointerInside = _toasts.Any(toast => toast.View?.IsPointerOver == true);
        border.GotFocus += (_, _) => _toastPointerInside = true;
        border.LostFocus += (_, _) => _toastPointerInside = _toasts.Any(toast => toast.View?.IsPointerOver == true || toast.View?.IsFocused == true);
        return border;
    }

    private static TimeSpan ToastDuration(UserFeedbackKind kind) => kind switch
    {
        UserFeedbackKind.Success or UserFeedbackKind.Info => TimeSpan.FromSeconds(3),
        UserFeedbackKind.Warning => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(8),
    };

    private void ExpireToasts()
    {
        if (_toastPointerInside) return;
        foreach (var toast in _toasts.Where(toast => toast.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
            RemoveToast(toast);
        if (_toasts.Count == 0) _toastTimer.Stop();
    }

    private void RemoveToast(ToastEntry entry)
    {
        _toasts.Remove(entry);
        if (entry.View is not null) ToastStack.Children.Remove(entry.View);
        if (_toasts.Count == 0) _toastTimer.Stop();
    }

    private void ApplyWorkspaceStatus(HubWorkspace.StatusUpdate update)
    {
        if (update.TaskToken is { } taskToken)
        {
            // A late progress or completion callback from an older task must never become the new owner
            // after the current task has finished. Tokens are monotonic across workspace instances. A
            // fallback task is the one exception: it can have a lower token because a newer task just ended.
            if (update.TaskActivated)
            {
                if (_activeTaskStatusToken is not null && taskToken < _latestTaskStatusToken) return;
                _activeTaskStatusToken = taskToken;
                _latestTaskStatusToken = Math.Max(_latestTaskStatusToken, taskToken);
                Status.Text = update.Text;
                return;
            }

            if (_activeTaskStatusToken != taskToken
                && (taskToken < _latestTaskStatusToken
                    || !update.TaskFinished && taskToken <= _lastFinishedTaskStatusToken)) return;

            if (update.TaskFinished)
            {
                if (_activeTaskStatusToken != taskToken) return;
                Status.Text = update.Text;
                _activeTaskStatusToken = null;
                _latestTaskStatusToken = taskToken;
                _lastFinishedTaskStatusToken = taskToken;
                return;
            }

            if (_activeTaskStatusToken is null || _activeTaskStatusToken == taskToken)
            {
                _activeTaskStatusToken = taskToken;
                _latestTaskStatusToken = taskToken;
                Status.Text = update.Text;
            }

            return;
        }

        if (update.Feedback is { } feedback)
        {
            ShowToast(feedback);
            return;
        }

        SetStatus(update.Text);
    }

    /// <summary>
    /// Puts how many sessions are answering right now on the strip. Called for every <see cref="ChatWorkspace"/>
    /// the shell builds, including the one a data-root switch replaces — an unresubscribed strip would quietly
    /// stop reporting replies, which is exactly the kind of failure nobody sees.
    /// </summary>
    private void WireChatRuns()
    {
        _chat.RunsChanged += _ => UpdateChatRunsStatus();
        _chat.Changed += SyncApprovalBadge;
        // The top-bar page title is the active conversation's title, and opening another session or renaming the
        // open one raises nothing but Changed - so the refresh belongs here too. Same contract as
        // SyncApprovalBadge: repaint from state already in memory (no reads hung off the event), and see
        // SyncPageTitle for the hop and the null test this needs on the way in.
        _chat.Changed += SyncPageTitle;
        _chat.AttentionRequired += OnChatAttentionRequired;
        _chat.RunCompleted += OnChatRunCompleted;
        SyncApprovalBadge();
    }

    private void OnChatAttentionRequired(string conversationId, ChatAttentionKind kind)
    {
        // A question that arrives underneath an answer surface is a question nobody sees. The settings card
        // covers the chat column, so it steps aside the moment anything needs a decision — and stepping aside
        // is lossless here, because every picker in the card commits as it changes and there is no half-typed
        // state to lose. This runs ahead of the automation and visibility gates below on purpose: those decide
        // whether to bother the user with a *notification*, and a card that keeps covering the approval has
        // the same problem whether or not anyone is watching the window.
        CloseSettings();

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
        // A switch rather than a chain of ternaries: there are three kinds now, and the third one is the case a
        // person most needs told apart — being asked to decide is not the same interruption as being asked to
        // allow, and a notification that says "approval" for a question teaches them to ignore the wrong one.
        var (titleKey, bodyKey) = kind switch
        {
            ChatAttentionKind.PlanApproval => ("ChatPlanNotificationTitle", "ChatPlanNotificationBody"),
            ChatAttentionKind.UserQuestion => ("ChatQuestionNotificationTitle", "ChatQuestionNotificationBody"),
            _ => ("ChatToolApprovalNotificationTitle", "ChatToolApprovalNotificationBody"),
        };
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
            RunResult.Incomplete => "ChatRunIncompleteNotificationTitle",
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

    /// <summary>
    /// Repaints the bar's two names after any <see cref="ChatWorkspace.Changed"/> raise. On the assistant page the
    /// page's name is fixed and the conversation's title is what moves; switching to another session and renaming
    /// the open one are visible only through that event, and a title left on the previous session is a title that
    /// lies - so this is where the refresh hangs. The hop and the null test are
    /// <see cref="SyncApprovalBadge"/>'s, for the same reasons: not every raise site is provably on the UI thread,
    /// and <c>WireChatRuns</c> subscribes before <c>InitializeComponent</c> has built <c>NavTitle</c>.
    /// </summary>
    private void SyncPageTitle()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncPageTitle);
            return;
        }
        if (NavTitle is null) return;
        UpdatePageTitle();
    }

    private void NotificationActivated(string conversationId)
        => Dispatcher.UIThread.Post(() => OpenConversationFromAttention(conversationId));

    private void OpenConversationFromAttention(string conversationId)
    {
        // Every route that lands here named its own conversation — an action key, a body link, a deeplink — so
        // the remembered target has been overtaken. Left standing, it would replay this jump on the next
        // ordinary raise, long after the notification it belonged to.
        _attention.ClearActivationTarget();
        if (string.Equals(conversationId, _attentionDiagnosticConversationId, StringComparison.Ordinal))
        {
            NavigateTo("Assistant");
            LeaveMaximizedForActivation();
            Activate();
            WriteLog("The test notification was activated; the app returned to the Assistant page.");
            return;
        }

        if (_chat.OpenConversation(conversationId) is null) return;
        NavigateTo("Assistant");
        LeaveMaximizedForActivation();
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
        _attention.Report("A test notification was requested."
                          + (MacUserNotifications.DescribeState() is { Length: > 0 } state ? " " + state : "")
                          + " OS notification settings or focus mode may still suppress its display.");

        DispatcherTimer.RunOnce(() =>
        {
            _attentionDiagnosticBadge = null;
            _attentionDiagnosticConversationId = null;
            SyncApprovalBadge();
            WriteLog("System attention diagnostic ended; the badge returned to the real approval state.");
        }, TimeSpan.FromSeconds(30));
    }

    internal void StopTaskbarProgress() => _taskbarProgress?.Dispose();

    private void UpdateChatRunsStatus()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateChatRunsStatus);
            return;
        }
        _taskbarProgress?.SetInProgress(_chat.HasExecutingRuns);
        if (ChatRuns is null) return;
        var running = _chat.RunningCount;
        ChatRuns.IsVisible = running > 0;
        ChatRuns.Text = running > 0 ? string.Format(HubStrings.Get("RunningSessionsFormat"), running) : "";
    }

    // ───────────────────────── The task queue's slot ─────────────────────────

    /// <summary>
    /// Repaints the strip's task slot, and the popover's rows while that popover is open.
    ///
    /// The rows are only rebuilt when the *set* of transfers changes; a progress tick repainting the handful
    /// of numbers it moved. Re-creating the row controls on every 80 KB report is how a list that works in a
    /// unit test stutters on a real download.
    /// </summary>
    private void UpdateTaskSlot()
    {
        // Same construction-window as SyncApprovalBadge: the queue can report before the strip exists.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateTaskSlot);
            return;
        }

        if (TaskSlotText is null) return;

        // The strip's cancel follows the queue rather than an "operation started" announcement: the button's
        // question is 「is there a change to stop」, and that is the queue's answer. Read from one place, it
        // cannot be wired to a moment when the honest answer was still no — which is exactly how this button
        // ended up disabled for every operation when the busy flag was raised before the task existed.
        CancelButton.IsEnabled = _workspace.CanCancel;

        var items = TaskQueue.Instance.Items;
        var (visible, label, action) = TaskSlotStateFor(items);
        TaskSlot.IsVisible = visible;
        TaskSlotText.Text = visible ? label : "";

        var active = items.FirstOrDefault(item => item.IsMoving);
        StatusProgress.IsVisible = active is not null;
        if (active is { } running)
        {
            var fill = running.Fill;
            var determinate = running.State != TaskQueue.Phase.Finalizing && fill is not null;
            StatusProgress.IsIndeterminate = !determinate;
            StatusProgress.Value = fill ?? 0;
            StatusProgressPercent.IsVisible = determinate;
            StatusProgressPercent.Text = determinate
                ? ((int)Math.Clamp(fill!.Value, 0, 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%"
                : "";
        }
        else
        {
            StatusProgress.IsIndeterminate = false;
            StatusProgress.Value = 0;
            StatusProgressPercent.IsVisible = false;
            StatusProgressPercent.Text = "";
        }
        // The slot speaks for the queue and nothing else: the update's decisions (fetch, restart) live on
        // the sidebar's update button, so the slot's tooltip is the queue's own sentence in every state it
        // can still show — it no longer holds a version, which is what the button's tip carries instead.
        _taskSlotTip.Text = HubStrings.Get("TaskSlotTip");
        if (!visible)
        {
            // Nothing left to act on and nothing left to read: the popover goes with its own anchor rather
            // than staying open over an empty list — and its rows go with it, because a closed popover still
            // holding rows for transfers nobody queued is the same report of the past, just unseen.
            TaskPopup.IsOpen = false;
            TaskRows.Children.Clear();
            _taskRowViews.Clear();
        }
        else if (TaskPopup.IsOpen)
        {
            RenderTaskRows();
        }
    }

    /// <summary>
    /// What a press on the slot does. The slot is the queue's own line now: the update's fetch/restart
    /// decisions live on the sidebar's update button, so one action is all that is left besides silence.
    /// </summary>
    internal enum TaskSlotAction
    {
        /// <summary>Nothing to act on, so nothing is shown.</summary>
        None,
        /// <summary>Transfers are moving, or one failed and still unread: lift the popover.</summary>
        OpenQueue,
    }

    /// <summary>
    /// What the slot says, as a function of the queue — the shape a check asserts on, because every one of
    /// these branches otherwise needs a real download to reach.
    ///
    /// <para>Two lights, one slot. While transfers are moving the slot is the <b>queue</b>: a count, a press
    /// that lifts the popover, and the one moving transfer's own percentage. A failure that went unread
    /// keeps the slot lit for the same reason — the row it belongs to is still reachable and still wants an
    /// answer.</para>
    ///
    /// <para>The update's decisions are deliberately <b>not</b> here any more (the sidebar's update button
    /// holds them): a 「重启完成更新」 in the strip beside a 「重启完成更新」 in the rail would be one fact
    /// told twice on one screen. Silence is what is left when nothing is moving and nothing failed — a strip
    /// that lit for work already finished would be reporting the past.</para>
    /// </summary>
    internal static (bool Visible, string Label, TaskSlotAction Action) TaskSlotStateFor(
        IReadOnlyList<TaskQueue.Item> items)
    {
        var active = items.Where(item => item.IsActive).ToList();
        if (active.Count > 0)
        {
            var moving = active.Count(item => item.IsMoving);
            // The progress bar and its percentage belong to the strip's main status lane. The task button
            // only reports the queue shape, so one task cannot repeat the same percentage beside the bar and
            // multiple tasks cannot make one percentage look like a total for the whole queue.
            return (true, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("TaskSlotFormat"), moving, active.Count), TaskSlotAction.OpenQueue);
        }

        var failed = items.Count(item => item.State == TaskQueue.Phase.Failed);
        if (failed > 0)
        {
            return (true, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("TaskSlotFailed"), failed), TaskSlotAction.OpenQueue);
        }

        return (false, "", TaskSlotAction.None);
    }

    /// <summary>The slot's one press: lift the popover that holds the rows, the measurements and the
    /// per-item cancel. Every other press the strip used to carry moved to the update button
    /// (see <see cref="OnUpdateActionClicked"/>).</summary>
    private void OnTaskSlotClicked()
    {
        var (_, _, action) = TaskSlotStateFor(TaskQueue.Instance.Items);
        if (action == TaskSlotAction.OpenQueue)
        {
            ToggleTaskPopup();
        }
    }

    // ───────────────────── The sidebar's update button ─────────────────────

    /// <summary>The update button's own states, named because the shape, the dot and the press all move
    /// together: an unnamed condition would only ever tell "is it visible" apart from the rest.</summary>
    internal enum UpdateActionKind
    {
        /// <summary>No update, no usable check result, or the transfer is moving (the strip reports it
        /// for itself): the button is not on screen.</summary>
        Hidden,
        /// <summary>An update is known and not on disk: accent circle + dot, press fetches it.</summary>
        Found,
        /// <summary>The bytes have landed: accent pill + dot, press arms (or applies when armed).</summary>
        Ready,
    }

    /// <summary>
    /// What the sidebar's update button is, as a function of the update's own state — the shape a check
    /// asserts on, because every one of these branches otherwise needs a real download to reach.
    ///
    /// <para>The keys are keys rather than sentences so the caller re-resolves them on every language
    /// switch; a label captured once would keep the language it was built in.</para>
    ///
    /// <para>The dot rides both states that wait for a person (<see cref="UpdateActionKind.Found"/> after a
    /// check — or after a cancel, which is exactly when auto-download will not fetch again by itself — and
    /// <see cref="UpdateActionKind.Ready"/> when the process swap is owed) and stands down while the
    /// transfer reports for itself — while it moves the button is <see cref="UpdateActionKind.Hidden"/>, the
    /// strip's percentage and cancel being the only surface that needs to exist.</para>
    /// </summary>
    internal static (UpdateActionKind Kind, string? LabelKey, string? TipKey) UpdateActionStateFor(
        UpdateService.CheckOutcome? last, UpdateService.DownloadState download, bool armed)
    {
        if (last is not { Result: UpdateService.CheckResult.UpdateAvailable })
        {
            return (UpdateActionKind.Hidden, null, null);
        }

        return download switch
        {
            UpdateService.DownloadState.Ready =>
                (UpdateActionKind.Ready, armed ? "TaskRestartArmed" : "TaskRestartAction", "UpdateRestartActionTip"),
            UpdateService.DownloadState.Downloading =>
                (UpdateActionKind.Hidden, null, null),
            _ =>
                (UpdateActionKind.Found, null, "TaskDownloadSlotTip"),
        };
    }

    /// <summary>
    /// Renders <see cref="UpdateActionStateFor"/> onto the button: one of the three shape classes, which
    /// glyph (or label) shows, whether the dot is up, and the state's tooltip with the pending version
    /// spelled out. Called from the update's own <see cref="UpdateService.Changed"/> hop, from the press
    /// itself (the arm flips the label), and from <see cref="InitializeChrome"/> (a language switch has to
    /// re-resolve the keys, and a tip captured once keeps its old language forever).
    /// </summary>
    private void SyncUpdateAction()
    {
        // Same construction-window guard as UpdateTaskSlot: the update can report before the XAML exists.
        if (UpdateActionHost is null) return;

        var service = UpdateService.Instance;
        // The arm is the service's, not this window's: the settings card's button offers the same press, and
        // two flags would let the two buttons disagree about whether the next click restarts. The service
        // clears it as soon as the update leaves the Ready phase, so there is nothing to un-arm here.
        var (kind, labelKey, tipKey) = UpdateActionStateFor(service.Last, service.Download, service.RestartArmed);

        UpdateActionHost.IsVisible = kind != UpdateActionKind.Hidden;
        if (kind == UpdateActionKind.Hidden)
        {
            // The host's own hiding takes the dot off the screen, but the dot's flag has to follow: a check
            // (and any future code) reads IsVisible on the dot directly, and a hidden host is not something
            // that resets its children's state.
            UpdateActionDot.IsVisible = false;
            return;
        }

        // Exactly one shape class decides the plate; Classes.Set keeps the others off without a rebuild.
        UpdateActionButton.Classes.Set("icon", kind == UpdateActionKind.Found);
        UpdateActionButton.Classes.Set("pill", kind == UpdateActionKind.Ready);

        UpdateActionGlyph.IsVisible = kind == UpdateActionKind.Found;
        UpdateActionText.IsVisible = kind == UpdateActionKind.Ready;
        UpdateActionText.Text = labelKey is null ? "" : HubStrings.Get(labelKey);
        UpdateActionDot.IsVisible = kind is UpdateActionKind.Found or UpdateActionKind.Ready;

        // The tip names the version, which is the one thing the button cannot show while it is a circle:
        // "↑" says an update waits, only the sentence says which. TaskDownloadAction / TaskRestartAction are
        // the fallbacks for the shape of the sentence when no version is known (no check yet can't reach
        // here — Hidden covers that — but a version the feed refused to name still gets a usable tip).
        var version = service.PendingVersion;
        var tip = (kind, version) switch
        {
            (UpdateActionKind.Ready, not null) =>
                string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get(tipKey!), version),
            (UpdateActionKind.Found, not null) =>
                string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get(tipKey!), version),
            (UpdateActionKind.Ready, null) => HubStrings.Get("TaskRestartAction"),
            (UpdateActionKind.Found, null) => HubStrings.Get("TaskDownloadAction"),
            _ => HubStrings.Get(tipKey!),
        };
        ToolTip.SetTip(UpdateActionButton, tip);
    }

    /// <summary>
    /// The button's one press, decided by the state it was showing: fetch it (which is also the retry after
    /// a cancelled download — the reason <see cref="UpdateService.StartPendingDownload"/> does not care
    /// about auto-download), or — past the switch — hand the press to the restart's own gate. While the
    /// download moves the button is hidden, so there is no press to route.
    ///
    /// <para>That gate is <see cref="UpdateService.PressUpdateRestart"/> rather than a copy of the two-tap
    /// rule kept here: replacing the process is the one thing this app cannot undo from the inside, and the
    /// settings card's button offers the same press, so the rule and the arm it uses have to be one thing
    /// (see <see cref="UpdateService.RestartArmed"/>).</para>
    /// </summary>
    private void OnUpdateActionClicked()
    {
        var service = UpdateService.Instance;
        var (kind, _, _) = UpdateActionStateFor(service.Last, service.Download, service.RestartArmed);
        switch (kind)
        {
            case UpdateActionKind.Hidden:
                return;
            case UpdateActionKind.Found:
                // Fetch it, and stop there. The transfer ends in Ready and the button then becomes the
                // pill, so the process swap stays behind a press the user meant.
                service.StartPendingDownload();
                return;
        }

        service.PressUpdateRestart();
    }
    /// <summary>
    /// What a restart would interrupt — a workspace operation, or a running answer. The arm and the decision
    /// live in the service both restart buttons press (see <see cref="UpdateService.RestartArmed"/>), so this
    /// is a forwarding view of <see cref="UpdateService.RestartInterruptsWork"/> rather than a second answer.
    /// A probe rather than an inline read because the armed second tap is a rule worth asserting on its own,
    /// and parking a live stream behind it to reach one branch is not a fixture anyone could read. The window
    /// installs the real answer where it wires the button.
    /// </summary>
    internal Func<bool> RestartInterruptsWork
    {
        get => UpdateService.Instance.RestartInterruptsWork;
        set => UpdateService.Instance.RestartInterruptsWork = value;
    }

    /// <summary>Set once the user has answered "quit anyway" to running transfers, so the re-issued
    /// <see cref="Window.Close"/> is not met by the same question.</summary>
    private bool _quitConfirmed;

    /// <summary>
    /// Asks about quitting while transfers are moving, and closes on the answer.
    ///
    /// <para>The question cannot be awaited from <c>Closing</c> — that handler is synchronous, so this runs
    /// <c>e.Cancel</c> first and re-issues the close afterwards. <see cref="_quitConfirmed"/> is what keeps
    /// it a single question rather than a loop, and a dialog dismissed by its own X counts as "stay"
    /// (<see cref="HubDialogResult.Cancel"/> is deliberately the zero value).</para>
    ///
    /// <para>Worth asking because quitting is not destructive to the bytes: <c>DownloadManager</c> keeps a
    /// <c>.partial</c>, and a later install of the same package picks up with a Range request. Nothing
    /// re-enqueues by itself — a restart empties the list — so the question says only what the press does.
    /// Promising a continuation the app would not carry out is how a copy line becomes a bug report.</para>
    /// </summary>
    private async Task ConfirmQuitAsync()
    {
        if (!await AskBeforeQuittingAsync()) return;
        _quitConfirmed = true;
        Close();
    }

    /// <summary>
    /// The same question for a quit the platform started (Cmd+Q, the Dock's or the application menu's Quit): the
    /// answer cannot be a <see cref="Window.Close"/> here — the platform already gave up on this shutdown, and a
    /// hidden window would never set off the lifetime's <c>OnLastWindowClose</c> — so the confirmed answer is an
    /// explicit <see cref="IClassicDesktopStyleApplicationLifetime.Shutdown"/>. That call is programmatic, which is
    /// why it cannot come back through this question a second time.
    /// </summary>
    private async Task ConfirmPlatformQuitAsync()
    {
        if (!await AskBeforeQuittingAsync()) return;
        _quitConfirmed = true;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>The one question both quit paths ask, so the copy and the button set cannot drift apart.</summary>
    private async Task<bool> AskBeforeQuittingAsync()
    {
        var running = TaskQueue.Instance.ActiveCount;
        var answer = await HubDialog.ShowAsync(this,
            HubStrings.Get("QuitDuringDownloadsTitle"),
            string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("QuitDuringDownloadsBody"), running),
            HubDialogButtons.OkCancel, danger: true);
        if (answer != HubDialogResult.Ok) return false;
        return await CodeFileWindow.ConfirmCloseDirtyAsync(this);
    }

    /// <summary>A quit question is asked by, and owned by, this window — so a window the close button hid has to come
    /// back first, or the modal has no owner on screen and the person who pressed Cmd+Q sees nothing happen.</summary>
    private void RestoreForQuitQuestion() => RestoreFromActivation();

    internal void RestoreFromTray()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    /// <summary>
    /// The macOS Dock (or Cmd-Tab) brought the app forward — <see cref="MacDockActivation"/> turned AppKit's
    /// did-become-active notification into this call. It restores a hidden window and stops there.
    ///
    /// <para>Deliberately not <see cref="ActivateFromRequest"/>: that one also consumes a pending notification
    /// target, and an ordinary raise must not jump to a conversation an earlier alert was about — the existing
    /// gate for that lives in the window's own <c>Activated</c> handler
    /// (<c>_foregroundHasLeftWindow</c>), and this path has no business bypassing it. When nothing is hidden
    /// there is nothing to do, so a plain Cmd-Tab to a visible window changes nothing at all.</para>
    /// </summary>
    internal void RestoreFromActivation()
    {
        if (!ShouldShowForRequest(!IsVisible, App.Options.IsAutomation)) return;
        RestoreFromTray();
    }

    internal void ExitFromTray()
    {
        if (_workspace.IsBusy)
        {
            _workspace.Cancel();
            SetStatus(HubStrings.Get("Cancelling"));
            return;
        }

        if (TaskQueue.Instance.ActiveCount > 0)
        {
            RestoreFromTray();
            _ = ConfirmQuitAsync();
            return;
        }

        _quitConfirmed = true;
        Close();
    }

    /// <summary>The tray icon was created, so a close has somewhere to hide to. Windows only: macOS needs no
    /// integration object, because the Dock belongs to every app and the Dock click that restores the window is
    /// wired in <see cref="MacDockActivation"/> (see also <see cref="ShouldHideOnCloseForCheck"/>).</summary>
    internal void EnableTrayCloseBehavior() => _trayCloseEnabled = true;

    /// <summary>
    /// A close the platform started to end the process — Cmd+Q, the Dock's Quit, the application menu's Quit, the
    /// system logging out — as opposed to the window's own close button.
    ///
    /// <para>The distinction is not cosmetic. <c>ClassicDesktopStyleApplicationLifetime.DoShutdown</c> closes every
    /// window with a cancelable <c>Closing</c> and, if any of them refused, cancels the whole shutdown because
    /// <c>Windows.Count</c> is still non-zero. So hiding on one of these closes would not "keep Hub in the Dock", it
    /// would make Quit permanently impossible: the window disappears, the process stays, and the next Quit hides a
    /// window that is already hidden. Answering it with <see cref="DecidePlatformQuit"/> instead keeps the questions
    /// the close button asks — a live operation is cancelled, live transfers are questioned — while the answer that
    /// ends the run is a real close, which is also what disposes the workspace, the chat store and the attention
    /// service, exactly as the tray's Exit does on Windows. Automation never reaches here (it calls
    /// <c>Shutdown</c>, which closes with <c>ignoreCancel</c>).</para>
    /// </summary>
    internal static bool IsPlatformQuit(WindowCloseReason reason)
        => reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;

    /// <summary>What a platform quit does, decided in one place so the rules the close button follows are the same
    /// ones and can be asserted without a window.</summary>
    internal enum PlatformQuitVerdict
    {
        /// <summary>End the process now.</summary>
        Quit,

        /// <summary>An exclusive operation is mid-write: cancel it and stay, exactly as the close button does.</summary>
        CancelOperation,

        /// <summary>Work is in flight that a quit would cut short: ask before ending it.</summary>
        Ask,
    }

    /// <summary>
    /// <see cref="PlatformQuitVerdict"/> for a quit the platform started. The order matters: an already-confirmed
    /// quit and an automation run never ask again (a dialog in a check run would hang it), and a half-written
    /// operation is cancelled rather than questioned — the question is about transfers, not about the operation the
    /// cancel button exists for.
    ///
    /// <para>A system shutdown or logout is the exception among platform quits: nobody is there to answer, and a modal
    /// would stall the logout rather than inform anyone — the system's own "Hub prevented shutdown" flow is not a
    /// question Hub can ask inside. So that one goes straight through, and it still ends the run cleanly because the
    /// close is a real close.</para>
    /// </summary>
    internal static PlatformQuitVerdict DecidePlatformQuit(
        WindowCloseReason reason, bool quitConfirmed, bool isAutomation, bool workspaceBusy, int activeTasks)
    {
        if (quitConfirmed || isAutomation) return PlatformQuitVerdict.Quit;
        if (reason == WindowCloseReason.OSShutdown) return PlatformQuitVerdict.Quit;
        if (workspaceBusy) return PlatformQuitVerdict.CancelOperation;
        return activeTasks > 0 ? PlatformQuitVerdict.Ask : PlatformQuitVerdict.Quit;
    }


    private void ToggleTaskPopup()
    {
        if (TaskPopup.IsOpen)
        {
            TaskPopup.IsOpen = false;
            return;
        }

        RenderTaskRows();
        TaskPopup.IsOpen = true;
    }

    /// <summary>The rows, reconciled against the queue. The list is short and changes rarely, so the
    /// reconciliation is "same shape → repaint in place, otherwise build it again".</summary>
    private void RenderTaskRows()
    {
        var items = TaskQueue.Instance.Items;
        if (_taskRowViews.Count != items.Count
            || items.Where((item, index) => !ReferenceEquals(_taskRowViews[index].Bound, item)).Any())
        {
            TaskRows.Children.Clear();
            _taskRowViews.Clear();
            foreach (var item in items)
            {
                var row = new TaskRowView(
                    item,
                    () => TaskQueue.Instance.Cancel(item.Identity),
                    () => TaskQueue.Instance.Retry(item),
                    () => TaskQueue.Instance.Dismiss(item),
                    () => ShowLog(true));
                TaskRows.Children.Add(row.Control);
                _taskRowViews.Add(row);
            }

            return;
        }

        foreach (var view in _taskRowViews)
        {
            view.Update();
        }
    }

    private readonly List<TaskRowView> _taskRowViews = [];

    /// <summary>One row of the popover: what is being fetched, how far it has come, and the press that
    /// answers it — stop it while it runs, run it again once it failed, and forget it when neither is
    /// wanted. Held as controls rather than rebuilt, so a progress tick moves three strings.</summary>
    private sealed partial class TaskRowView
    {
        private readonly TaskQueue.Item _item;
        private readonly TextBlock _title = new() { Classes = { "task-row-title" } };
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6 };
        private readonly TextBlock _measure = new() { Classes = { "task-row-measure" } };
        private readonly TextBlock _error = new() { Classes = { "task-row-error" }, IsVisible = false };
        /// <summary>The row's tooltip body, held rather than re-set: assigning a new tooltip closes the one on
        /// screen, and a rate that updates every 500 ms would dismiss itself mid-read.</summary>
        private readonly TextBlock _tip = new();
        private readonly Button _cancel = StripButton();
        private readonly Button _retry = StripButton();
        private readonly Button _dismiss = StripButton();
        /// <summary>For a change whose detail lives in the log drawer rather than on this row: the row says
        /// which stage, the drawer says what happened. The one press that leaves the popover to go read.</summary>
        private readonly Button _log = StripButton();
        private readonly StackPanel _actions = new()
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        public TaskRowView(TaskQueue.Item item, Action cancel, Action retry, Action dismiss, Action showLog)
        {
            _item = item;
            _cancel.Click += (_, _) => cancel();
            _retry.Click += (_, _) => retry();
            _dismiss.Click += (_, _) => dismiss();
            _log.Click += (_, _) => showLog();
            _actions.Children.Add(_cancel);
            _actions.Children.Add(_retry);
            _actions.Children.Add(_dismiss);
            _actions.Children.Add(_log);
            Control = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel { Spacing = 4, Children = { _title, _bar, _measure, _error } },
                    _actions,
                },
            };
            Grid.SetColumn(_actions, 1);
            Update();
        }

        public Control Control { get; }

        public TaskQueue.Item Bound => _item;

        public void Update()
        {
            _title.Text = _item.Title + " · " + _item.PhaseText;
            // An unmeasurable phase gets the indeterminate bar rather than a number held at 99: the local
            // work after the bytes (unzipping, patching) is real work with no percentage in it. A change that
            // is running but cannot name a percentage is the same case — a determinate bar sitting at zero
            // reads as work that stopped, which is the one thing a row must never say about a live process.
            _bar.IsIndeterminate = _item.State == TaskQueue.Phase.Finalizing
                                   || (_item.Kind == TaskQueue.TaskKind.Mutating && _item.IsMoving && _item.Fill is null);
            _bar.Value = _item.Fill ?? 0;
            _measure.Text = _item.MeasureText;
            // The estimate reaches the user through the tooltip and nowhere else: the measure column is kept
            // for bytes the transport actually counted, so a guessed total can never read as a confirmed one.
            _tip.Text = _item.HintText ?? "";
            ToolTip.SetTip(Control, _item.HintText is null ? null : _tip);
            _error.Text = _item.Error ?? "";
            _error.IsVisible = _item.Error is { Length: > 0 };
            // Re-read on every repaint, not once at construction: these are the shell's own buttons, and the
            // row that survived a language switch would otherwise keep saying 取消 in an English client.
            _cancel.Content = HubStrings.Get("Cancel");
            _retry.Content = HubStrings.Get("TaskRetry");
            _dismiss.Content = HubStrings.Get("TaskDismiss");
            _log.Content = HubStrings.Get("TaskLogAction");
            // A change's detail is in the drawer; a transfer's whole story is its byte count, and a fourth
            // button on every row would be the shell asking permission to scroll.
            _log.IsVisible = _item.Kind == TaskQueue.TaskKind.Mutating;
            // A finished row keeps no cancel: there is nothing left to stop, and a button that answers to
            // nothing is the thing this shell already refused to do in the version picker.
            _cancel.IsVisible = _item.IsActive;
            // A cancelled row used to get *neither* button — no retry and no dismiss — which left it as a
            // row nothing could act on or remove (it only ever left on the next Enqueue's cleanup). Cancel
            // settles work the same way failure does, and both settle states offer the same two ways out.
            _retry.IsVisible = _item.State is TaskQueue.Phase.Failed or TaskQueue.Phase.Cancelled;
            _dismiss.IsVisible = _item.State is TaskQueue.Phase.Failed or TaskQueue.Phase.Cancelled;
        }

        private static Button StripButton() => new()
        {
            Classes = { "strip" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

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
        ShowFeedback(UserFeedback.Success(HubStrings.Get("DataChanged"), "data-root-changed"));
        return true;
    }

    private void ReplaceWorkspace(HubWorkspace next)
    {
        _workspace.Dispose();
        _workspace = next;
        _workspace.Owner = this;

        // Events are wired on the new instance, so re-subscribing here won't append the log twice.
        WireWorkspace();

        // The settings card is not in the page cache, so the line below would not drop it — and a card built
        // against the old workspace would keep answering for a data root nobody is looking at. Close it and let
        // the next gear press rebuild it against the new one. The category is the shell's to keep: which row
        // you were reading is a fact about the reader, not about the directory.
        CloseSettings();
        _settings = null;

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
        UpdateChatRunsStatus();
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
        ClearActivityLog();
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
        if (BottomMenuFlyout is { IsOpen: true } open)
        {
            open.Hide();
            return;
        }

        FlyoutBase.SetAttachedFlyout(BottomMenuButton, BuildBottomMenu());
        FlyoutBase.ShowAttachedFlyout(BottomMenuButton);
    }

    private void ToggleLogActionsMenu()
    {
        if (LogActionsFlyout is { IsOpen: true } open)
        {
            open.Hide();
            return;
        }

        FlyoutBase.SetAttachedFlyout(LogActionsButton, BuildLogActionsMenu());
        FlyoutBase.ShowAttachedFlyout(LogActionsButton);
    }

    private MenuFlyout BuildLogActionsMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var copy = new MenuItem { Header = HubStrings.Get("CopyError") };
        copy.Click += async (_, _) =>
        {
            menu.Hide();
            var text = _workspace.LastError.Length == 0 ? ReadRetainedLog() : _workspace.LastError;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        };
        menu.Items.Add(copy);

        var open = new MenuItem { Header = HubStrings.Get("OpenLogs") };
        open.Click += (_, _) =>
        {
            menu.Hide();
            _workspace.Open(_workspace.Log.Folder);
        };
        menu.Items.Add(open);

        var clear = new MenuItem { Header = HubStrings.Get("ClearLog") };
        clear.Click += (_, _) =>
        {
            menu.Hide();
            ClearActivityLog();
        };
        menu.Items.Add(clear);
        return menu;
    }

    /// <summary>
    /// The bottom entry's popup: Settings, Appearance, License and About. Appearance opens the two
    /// questions it answers, Language and Theme, and each of those opens its own answers. Every row is one
    /// line: a name, and a tick when it is the answer in force. Built fresh on every open, so the labels follow
    /// the UI language and exactly one tick per question is drawn without any sync machinery.
    ///
    /// Two levels rather than six flat rows: this popup is a switchboard, and which language and which theme are
    /// in force is not what a reader opens it for — it is what they come back to change, so the answers sit one
    /// step in and the questions stay visible. (Avalonia 12 has no <c>MenuFlyoutItem</c> family: the flyout hosts
    /// <see cref="MenuItem"/>s directly, and a MenuItem with children <em>is</em> the submenu.)
    ///
    /// Ticks are drawn inside the row rather than by a toggle column — the column parks its marker left of the
    /// row's own icon and the two read as one glyph doing a job twice. Same reasoning, and the same shape, as
    /// the composer's permission menu (<c>ChatPanel.BuildPermissionHeader</c>). <c>IsChecked</c> is still set, so
    /// the state lives on the item and not only in its pixels, and no radio group has to be re-synced by hand.
    /// </summary>
    internal MenuFlyout BuildBottomMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };

        var settings = new MenuItem
        {
            Header = MenuRow.Build("Hub.Icon.Settings", HubStrings.Get("Settings"), current: false),
        };
        settings.Click += (_, _) =>
        {
            menu.Hide();
            OpenSettings();
        };
        menu.Items.Add(settings);

        var appearance = new MenuItem
        {
            Header = MenuRow.Build("Hub.Icon.Contrast", HubStrings.Get("Appearance"), current: false),
            Tag = MenuRowTag.Appearance,
        };

        var language = new MenuItem
        {
            Header = MenuRow.Build("Hub.Icon.Globe", HubStrings.Get("Language"), current: false),
            Tag = MenuRowTag.Language,
        };
        foreach (var (tag, display) in SettingsPage.LanguageChoices)
        {
            var current = tag == HubStrings.Language;
            var item = new MenuItem
            {
                // No glyph: the name is written in its own script, which is the whole sentence, and the tick
                // says which one is in force.
                Header = MenuRow.Build(null, display, current),
                IsChecked = current,
                Tag = tag,
            };
            item.Click += (_, _) =>
            {
                // Hide first: UseLanguage repaints the trigger block this popup is anchored to, and a flyout
                // whose anchor was rewritten underneath it is a flyout that has to be dismissed by hand.
                menu.Hide();
                UseLanguage(tag);
            };
            language.Items.Add(item);
        }

        var theme = new MenuItem
        {
            Header = MenuRow.Build("Hub.Icon.Sun", HubStrings.Get("Theme"), current: false),
            Tag = MenuRowTag.Theme,
        };
        foreach (var value in HubTheme.All)
        {
            var current = value == ThemeService.Current;
            var item = new MenuItem
            {
                Header = MenuRow.Build(null, HubStrings.Get(SettingsPage.ThemeTextKey(value)), current),
                IsChecked = current,
                Tag = value,
            };
            item.Click += (_, _) =>
            {
                var error = UseTheme(value);
                if (error is null)
                {
                    menu.Hide();
                    return;
                }

                // A switch that failed and rolled back must leave the menu up: the reader asked for something
                // that did not happen, and closing the surface they asked from hides the reason. The tick stays
                // on the theme that is actually current, because this whole menu is rebuilt on the next open.
                ShowFeedback(UserFeedback.Error(error, "theme-change"));
            };
            theme.Items.Add(item);
        }

        appearance.Items.Add(language);
        appearance.Items.Add(theme);
        menu.Items.Add(appearance);

        var license = new MenuItem
        {
            Header = MenuRow.Build("Hub.Icon.Application", "License", current: false),
            Tag = MenuRowTag.License,
        };
        license.Click += async (_, _) =>
        {
            menu.Hide();
            await LicenseWindow.ShowAsync(this);
        };
        menu.Items.Add(license);

        var about = new MenuItem
        {
            Header = MenuRow.Build("Hub.Icon.Info", "About", current: false),
            Tag = MenuRowTag.About,
        };
        about.Click += async (_, _) =>
        {
            menu.Hide();
            await App.ShowAboutAsync(this);
        };
        menu.Items.Add(about);
        return menu;
    }

    /// <summary>The <c>Tag</c>s the bottom menu's own rows carry, so the self-check finds a question by what it
    /// is rather than by where it happens to sit. Kept distinct from the leaf tags — a theme value and a
    /// language tag are data, and a parent row answering to one of them would be found twice.</summary>
    internal static class MenuRowTag
    {
        internal const string Appearance = "menu:appearance";
        internal const string Language = "menu:language";
        internal const string Theme = "menu:theme";
        internal const string License = "menu:license";
        internal const string About = "menu:about";
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
        // Same for the task slot, and for the rows if the popover happens to be open: every one of those
        // strings is composed in code from a key.
        UpdateTaskSlot();
        SyncMacComputerPermissions();

        // The settings card is not in the page cache any more, so it gets told on its own — and told even while
        // it is closed, because its ComboBox labels are written in code and a stale English dropdown would be
        // waiting there the moment the gear is pressed again.
        _settings?.Reload();

        foreach (var page in _pages.Values)
        {
            // The assistant's copy is written in code (it composes values), so it only follows a language
            // switch if it is told to — the same reason the settings card reloads above.
            if (page is ChatPanel chat)
            {
                chat.Reload();
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

    /// <summary>Guards the retained log tail: the output pump writes it from a worker thread while the drain reads
    /// it from the UI thread.</summary>
    private readonly object _logGate = new();
    private readonly StringBuilder _logText = new();
    private bool _logDirty;

    /// <summary>How often the box is repainted while the drawer is open. A per-line drain is the bug this timer
    /// exists to stop, so the interval is the cost: five writes a second whatever the line rate.</summary>
    private readonly DispatcherTimer _logDrain = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>The retained tail — what the drawer shows, whether or not it is open. The box itself lags one
    /// drain behind and holds nothing while the drawer is closed, so anything that reads the log reads this.</summary>
    private string ReadRetainedLog()
    {
        lock (_logGate) return _logText.ToString();
    }

    private void WireWorkspace()
    {
        // The log's retained tail, kept off the UI thread. A build has been measured at 111 lines a second, and
        // appending each one to TextBox.Text copies the whole retained string and re-measures the box: a flood of
        // those held the dispatcher for 216 seconds, which is a frozen shell with no message beside it. The pump
        // appends here; DrainActivityLog is the only writer of the box.
        _workspace.Logged += line =>
        {
            lock (_logGate)
            {
                _logText.Append(line).Append('\n');
                if (_logText.Length > 120000) _logText.Remove(0, _logText.Length - 80000);
                _logDirty = true;
            }
        };
        _logDrain.Tick += (_, _) => DrainActivityLog();

        _workspace.StatusChanged += ApplyWorkspaceStatus;

        // The rail's bottom-left line names the default engine, and every gated change ends in a Refresh that
        // raises this. Without the subscription the line kept the version it was built with until the next
        // construction, data-root switch or language change — "set default engine" moved the state and the
        // engines page, and left the shell's own chrome saying the previous version.
        _workspace.Changed += SyncRailEngineLine;

        // Nothing here disables a surface while work runs, and nothing here announces it either: the strip's
        // cancel reads the queue itself (UpdateTaskSlot), which is the single place that knows whether there
        // is a change to stop. The gate answers a refused press with a sentence instead of grey.

        _workspace.Failed += () => ShowLog(true);

        // Dialogs (e.g. "prebuilt library settings") can't reference the main window directly, so
        // page navigation is forwarded here through the workspace.
        _workspace.NavigateRequested += key =>
        {
            // Settings still answers the name, but it is no longer a page: whoever asks gets the overlay.
            if (key == "Settings")
            {
                OpenSettings();
                return;
            }

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

    private Geometry? _maximizeGlyph;
    private Geometry? _restoreGlyph;

    /// <summary>
    /// The assistant's state, for the shell self-check to assert against — and to install a scripted chat
    /// client so the page can be verified with no network and no API key.
    /// </summary>
    internal ChatWorkspace Chat => _chat;

    /// <summary>Whether the assistant page is the one on screen.</summary>
    internal bool AssistantVisible => _currentKey == "Assistant";

    /// <summary>Whether the window is in front of the user: focused, and not minimized. Read live rather than
    /// cached, because a cached copy could become stale across a data-root switch or a recreated window.</summary>
    private bool WindowVisibleNow
        => _windowVisibleOverride ?? (IsActive && WindowState != WindowState.Minimized);

    /// <summary>The visibility answer used to suppress redundant system notifications while the user is already
    /// looking at the session that needs approval.</summary>
    internal AttentionVisibility AttentionNow => new(WindowVisibleNow, AssistantVisible);

    private void WireChrome()
    {
        // The task slot mirrors the queue. Marshalled because a transfer reports from whatever thread
        // its transport reports on, and the strip is owned by this one. Subscribed once — WireChrome only
        // runs from the constructor.
        TaskQueue.Instance.Changed += () => Dispatcher.UIThread.Post(UpdateTaskSlot);
        // The update's own phase is both surfaces' business — the slot counts the transfer, the sidebar
        // button is the fetch/restart — and it moves without the queue moving: a check that finds nothing
        // new, a cancel, a channel switch. One hop repaints both, so they can never disagree by a frame.
        UpdateService.Instance.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            UpdateTaskSlot();
            SyncUpdateAction();
        });
        TaskPopup.PlacementTarget = TaskSlot;
        TaskPopup.Placement = PlacementMode.TopEdgeAlignedLeft;
        // The holder is installed once, for the reason recorded on _taskSlotTip: only its text moves.
        ToolTip.SetTip(TaskSlot, _taskSlotTip);
        // What a restart would cut short: a Hub operation, or replies still being written in the
        // background. Both are the user's work, and neither gets destroyed by one stray click.
        RestartInterruptsWork = () => _workspace.IsBusy || _chat.RunningCount > 0;
        TaskSlot.Click += (_, _) => OnTaskSlotClicked();
        UpdateTaskSlot();

        CancelButton.Click += (_, _) => _workspace.Cancel();
        LogActionsButton.Click += (_, _) => ToggleLogActionsMenu();

        // Closing is forbidden while an operation is running (handled in WPF's Closing), otherwise
        // child processes would be left behind.
        Closing += (_, e) =>
        {
            // A Quit the platform started — Cmd+Q, the Dock's or the application menu's Quit, the system logging
            // out — must end the process, and refusing it in the wrong place cancels the whole shutdown (see
            // IsPlatformQuit). So it gets the same questions the close button asks, and the answer that ends the
            // run is an explicit Shutdown rather than a close: a hidden window never raises the last-window close
            // the lifetime's default OnLastWindowClose waits for.
            if (IsPlatformQuit(e.CloseReason))
            {
                switch (DecidePlatformQuit(e.CloseReason, _quitConfirmed, App.Options.IsAutomation,
                            _workspace.IsBusy, TaskQueue.Instance.ActiveCount))
                {
                    case PlatformQuitVerdict.Quit:
                        return;
                    case PlatformQuitVerdict.CancelOperation:
                        e.Cancel = true;
                        RestoreForQuitQuestion();
                        _workspace.Cancel();
                        SetStatus(HubStrings.Get("Cancelling"));
                        return;
                    default:
                        e.Cancel = true;
                        RestoreForQuitQuestion();
                        _ = ConfirmPlatformQuitAsync();
                        return;
                }
            }

            // On Windows and macOS the close button hides the window instead of ending the run; see
            // ShouldHideOnCloseForCheck for why and for the automation / confirmed-quit exceptions. What brings
            // it back is the tray icon on Windows and the Dock icon (or Cmd-Tab) on macOS, wired in
            // MacDockActivation.
            if (ShouldHideOnClose(
                    OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), App.Options.IsAutomation,
                    _quitConfirmed, _trayCloseEnabled))
            {
                e.Cancel = true;
                Hide();
                return;
            }

            if (_workspace.IsBusy)
            {
                // An exclusive operation is mid-write and must not be left half-done: the close becomes a
                // cancel and the window stays. Unchanged from before the queue existed.
                e.Cancel = true;
                _workspace.Cancel();
                SetStatus(HubStrings.Get("Cancelling"));
                return;
            }

            if (_quitConfirmed || TaskQueue.Instance.ActiveCount == 0 || App.Options.IsAutomation)
            {
                return;
            }

            e.Cancel = true;
            _ = ConfirmQuitAsync();
        };

        Closed += (_, _) => StopTaskbarProgress();
        Closed += (_, _) => _workspace.Dispose();
        Closed += (_, _) => _chat.Dispose();
        Closed += (_, _) => _attention.Dispose();
    }

    /// <summary>
    /// The conversation sidebar, built against the current chat workspace. It takes the preference store because
    /// which groups are folded is state the user left and expects back after a restart, and the status strip
    /// because re-pointing a workspace can fail on a fact about the disk — a folder that moved again while the
    /// dialog was open — and that answer belongs in the strip rather than in a card.
    /// </summary>
    private ChatSidebar BuildChatSidebar()
        => new(_chat, _preferencesStore, _preferences) { StatusReporter = ShowFeedback };

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

    /// <summary>
    /// Wires the app's own title bar: dragging the window by its background, and the three window controls.
    ///
    /// The bar is the app's because <c>ExtendClientAreaToDecorationsHint</c> hands the platform's chrome to the
    /// client area — see the comment in MainWindow.axaml. The three controls are therefore not decorative: each
    /// one has to ask the same object the platform asked, so nothing here goes around <see cref="Window"/>.
    /// Close goes through <see cref="Window.Close()"/> rather than tearing anything down directly, which is what
    /// keeps the running-operation guard in <c>Closing</c> the *only* way out.
    /// </summary>
    private void InitializeTitleBar()
    {
        // The whole bar moves the window; the buttons sit above this layer and take their own input first.
        // BeginMoveDrag is not called here any more, and that is what makes the bar's second click reachable at
        // all: it enters the platform's own modal move loop, so a press that started one would spend the rest of
        // the double-click moving the window (see HubDialogChrome.DragThreshold).
        TitleBarDragArea.PointerPressed += OnTitleBarPressed;
        TitleBarDragArea.PointerMoved += OnTitleBarMoved;
        TitleBarDragArea.PointerReleased += OnTitleBarReleased;

        // The conversation's title is hit-test visible so its tooltip has something to hover — which would make it
        // exactly the hole this bar must not have if it kept the press to itself. It gets the same handler instead:
        // every press captures the pointer on TitleBarDragArea, so a press that lands on the title still starts a
        // window drag, and the moves and the release are delivered to the layer that owns them.
        ConversationTitle.PointerPressed += OnTitleBarPressed;

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();

        // Windows 11 Snap Layouts on hover. Ours is a drawn button, so the system has to be told where it is
        // (see TitleBarSnapHook). It cannot be installed here: this runs from the constructor, and a window that
        // has never been shown has no Win32 handle to subclass yet — the self-check builds exactly such a window,
        // which is how that was caught. Installing on Opened is the first moment a handle is guaranteed; the call
        // is idempotent, so a window shown twice cannot install twice.
        //
        // The same hook also runs the plate's click: answering HTMAXBUTTON turns that point into a non-client
        // press, which Avalonia never sees as a click, so the flip is handed to it as the one thing both callers
        // do (and the glyph and the tooltip follow it through the window's own state change).
        Opened += (_, _) => TitleBarSnapHook.Install(this, MaximizeButton, ToggleMaximized);

        // The system keeps a few pixels of resize frame between the client area and the window's outer edge, and
        // that strip is exactly the gap a caption button leaves at the right end. The platform's own close button
        // is centred on the outer edge, so the app's has to reach it: the control strip is extended right by the
        // measured frame width. A negative margin rather than a wider plate, because the plate's own size is what
        // the 46x32 platform proportion is about - only its origin moves.
        SetCaptionStripInset();

        // The maximize plate's glyph and tooltip both say what the *next* click does, so they are rewritten
        // from the real state rather than from anything remembered here. Both glyphs are resolved once, from
        // this window's own resources, because these are looked up on every window-state change - and they must
        // be the 12x12 caption glyphs, not the 0-24 grid icons of the same name, which are drawn scaled.
        _maximizeGlyph = this.FindResource("Hub.Icon.CaptionMaximize") as Geometry;
        _restoreGlyph = this.FindResource("Hub.Icon.CaptionRestore") as Geometry;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) SyncWindowStateChrome();
        };
        // The one place the normal geometry is remembered, and the one place the window manager's maximized rect is
        // picked up. A tracker rather than a snapshot taken at the maximize call, because that would only cover this
        // app's three ways in and would miss a window the window manager maximized by itself.
        Resized += (_, e) => OnShellResized(e.ClientSize);
        SyncWindowStateChrome();
    }

    /// <summary>
    /// Flips the window between maximized and normal. One method for all three of its callers — the plate's own
    /// click, the plate's non-client click (see <see cref="TitleBarSnapHook"/>) and the bar's double-click —
    /// because the window state is what the glyph and the tooltip follow, and a second copy of the flip would be a
    /// second place for them to fall out of step. On WSLg the same three callers end in the shell's own work-area
    /// fill instead (see <see cref="WslWorkAreaFill"/>), because there a window the window manager maximizes stops
    /// receiving the pointer over its own caption.
    /// </summary>
    private void ToggleMaximized()
    {
        MaximizeToggleRequestsForCheck++;
        if (WslWorkAreaFill.AppliesHere)
        {
            ToggleWorkAreaFill();
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Enters or leaves the filled work area. The first entry asks the window manager to maximize
    /// <em>once</em>, because its own rect is the only number on this host that knows how much of the screen a
    /// maximized window really gets — the display's working area over-reports it by the band the host's taskbar
    /// costs (measured: 2560x1440 reported, 2560x1368 used, host at 150%). Only the **size** is taken from that
    /// detour; the origin comes from the screen. Every later toggle applies the cached rect straight away, in the
    /// window manager's normal state, and the maximized state is never shown again.</summary>
    private void ToggleWorkAreaFill()
    {
        // A second click inside the handover (the plate double-clicked, a double-click on the bar arriving twice)
        // is dropped rather than honoured: the fill it would apply is the one already on its way, and writing the
        // geometry now is what the wait exists to avoid.
        if (_learningFillRect || _fillPending)
        {
            return;
        }

        if (_fillsWorkArea)
        {
            LeaveWorkAreaFill();
            return;
        }

        if (_fillRect is { } known)
        {
            ApplyWorkAreaFill(known);
            return;
        }

        _learningFillRect = true;
        WindowState = WindowState.Maximized;
    }

    /// <summary>Parks the window's content on the work area, in the window manager's normal state — the state in
    /// which paint and input agree here (see <see cref="WslWorkAreaFill"/>). The rect is written against the
    /// content, while <see cref="Window.Position"/> is the origin of whatever the platform wraps the client in: 9 px
    /// on every side while Avalonia's X11 backend was drawing the window's frame for us (and none at all while the
    /// window manager held the window maximized), nothing since Linux asks for none of that. The inset is therefore
    /// measured here rather than assumed, and the rect's own origin is moved back by it before it is written.</summary>
    private void ApplyWorkAreaFill(PixelRect rect)
    {
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        _fillsWorkArea = true;
        Position = FillOriginForContent(rect, Position, this.PointToScreen(new Point(0, 0)));
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
        SyncWindowStateChrome();
    }

    private void LeaveWorkAreaFill()
    {
        _fillsWorkArea = false;
        if (_normalGeometry is { } normal)
        {
            Position = normal.Position;
            Width = normal.ClientSize.Width;
            Height = normal.ClientSize.Height;
        }

        SyncWindowStateChrome();
    }

    /// <summary>Where to put the window so that its content starts at <paramref name="contentRect"/>'s origin:
    /// whatever sits between the window's origin and its content today sits between them after the move too, so it
    /// is subtracted. Pure, because the two origins are exactly the numbers a check cannot obtain without a real
    /// window on a real X server.</summary>
    internal static PixelPoint FillOriginForContent(PixelRect contentRect, PixelPoint windowOrigin, PixelPoint contentOrigin)
        => new(contentRect.X - (contentOrigin.X - windowOrigin.X),
               contentRect.Y - (contentOrigin.Y - windowOrigin.Y));

    /// <summary>The work-area rect the shell fills: the origin a screen publishes for itself, the size the window
    /// manager just used. Pure for the same reason — on every other platform the pair is never asked for, and a
    /// check cannot make the window manager maximize.</summary>
    internal static PixelRect WorkAreaRectFor(PixelPoint contentOrigin, Size maximizedClientSize, double scaling)
    {
        var factor = scaling <= 0 ? 1 : scaling;
        return new PixelRect(contentOrigin.X, contentOrigin.Y,
            (int)Math.Round(maximizedClientSize.Width * factor, MidpointRounding.AwayFromZero),
            (int)Math.Round(maximizedClientSize.Height * factor, MidpointRounding.AwayFromZero));
    }

    /// <summary>Records the size the window manager maximized to and hands the window back. Nothing is applied from
    /// here: the window manager is about to put the window back where it found it, and a geometry written into that
    /// handover comes off under ours (measured on WSLg — the resize that announces the restore arrives while
    /// <c>Position</c> still reports the maximized origin, and the window manager's own configure lands after it, so
    /// the size we asked for stayed and the position did not). Waiting a beat after the state change is what makes
    /// the write the last one: measured to land and hold at 120 ms, with this 250 ms chosen as the margin. The flash
    /// it costs is paid once per run, because the rect is cached.</summary>
    private void LearnFillRect(Size clientSize)
    {
        var screen = Screens?.Primary;
        _learningFillRect = false;
        _fillRect = WorkAreaRectFor(screen?.WorkingArea.Position ?? default, clientSize, RenderScaling);
        _fillPending = true;
        WindowState = WindowState.Normal;
        DispatcherTimer.RunOnce(WritePendingFill, TimeSpan.FromMilliseconds(250));
    }

    private void WritePendingFill()
    {
        if (!_fillPending || _fillRect is not { } rect)
        {
            return;
        }

        _fillPending = false;
        ApplyWorkAreaFill(rect);
    }

    /// <summary>
    /// Whether a press on the bar means "maximize or restore" instead of "start moving the window". Pure, and
    /// internal, so the self-check can drive every click count: a mouse cannot be scripted, and this arm is
    /// exactly the one that used to fall through to the drag.
    /// </summary>
    internal static bool TitleBarPressToggles(int clickCount) => clickCount >= 2;

    /// <summary>Records where the window sits and how wide its client is while neither the window manager nor the
    /// shell holds it filled — the geometry both a restore and a restore-drag go back to. A size under the window's
    /// own minimum is filtered out because it is not a geometry a reader ever asked for: it is the platform
    /// mid-restore, or a configure that landed before the frame was applied, and remembering it would restore the
    /// next drag to the wrong width.</summary>
    private void OnShellResized(Size clientSize)
    {
        if (_learningFillRect && WindowState == WindowState.Maximized)
        {
            // Anything not visibly bigger than the window's own normal size is an intermediate configure on the way
            // to the maximized one; stay armed and wait for the resize that actually filled the screen.
            if (_normalGeometry is { } before && clientSize.Width <= before.ClientSize.Width + 1)
            {
                return;
            }

            LearnFillRect(clientSize);
            return;
        }

        // The window manager is still putting the window back. Nothing is recorded from these resizes: they are its
        // own geometry on the way out, and the fill arrives on the timer (see <see cref="LearnFillRect"/>).
        if (_fillPending) return;

        if (WindowState == WindowState.Maximized || _fillsWorkArea) return;
        if (clientSize.Width < MinWidth || clientSize.Height < MinHeight) return;
        _normalGeometry = new NormalGeometry(Position, clientSize);
    }

    /// <summary>Whether a drag that began on the bar has to put the window back on its own geometry before the
    /// platform moves it. Pure — the platform, the state and the fill come in — so the self-check can drive both
    /// arms, the one that acts ending in <see cref="Window.BeginMoveDrag"/>, which is where a check would hang.
    /// Windows is excluded on purpose: DWM un-maximizes there by itself and <see cref="TitleBarSnapHook"/> is built
    /// on that. Everywhere else the shell does it, because a reader who pulls a caption means "take this window off
    /// the screen", and a window the window manager still holds does not follow the pointer the way the press
    /// expects. On WSLg the state a drag leaves is normally the shell's own fill (see <see cref="WslWorkAreaFill"/>)
    /// — there, a window the window manager maximizes never receives the press at all — while the maximized arm is
    /// what a window manager that maximized without being asked leaves behind.</summary>
    internal static bool DragRestoresMaximized(bool isWindows, WindowState state, bool fillsWorkArea)
        => !isWindows && (state == WindowState.Maximized || fillsWorkArea);

    /// <summary>Where to put the window once the maximized state is gone so that the bar is still under the pointer,
    /// at the same proportion of its width the pointer was at when the press landed — the semantics DWM gives on
    /// Windows. The proportion is DIP over DIP and therefore unit-free, while every length that reaches the window
    /// manager is a physical pixel, which is why the restored width and the grab offset are scaled. The frame term
    /// is the window's origin measured against its client origin: the pointer sits in the client area, and
    /// <see cref="Window.Position"/> is the window's own top-left, which a window manager that reparents the window
    /// keeps a few pixels outside it.</summary>
    internal static PixelPoint RestoredOriginForDrag(
        PixelPoint maximizedClientOrigin,
        PixelPoint maximizedWindowOrigin,
        double maximizedBarWidth,
        Point press,
        Size restoredSize,
        double scaling)
    {
        var scale = scaling <= 0 ? 1 : scaling;
        var fractionX = Math.Clamp(press.X / Math.Max(maximizedBarWidth, 1), 0, 1);
        var cursorX = maximizedClientOrigin.X + press.X * scale;
        var cursorY = maximizedClientOrigin.Y + press.Y * scale;
        var frameX = maximizedClientOrigin.X - maximizedWindowOrigin.X;
        var frameY = maximizedClientOrigin.Y - maximizedWindowOrigin.Y;
        // Where inside the 32px bar the press landed is where the pointer stays; a press below the bar (the check
        // drives one at its centre, and a reader's press cannot leave it) never lifts the window past its own top.
        var grabY = Math.Min(press.Y, HubDialogChrome.Height) * scale;

        return new PixelPoint(
            (int)Math.Round(cursorX - restoredSize.Width * scale * fractionX - frameX, MidpointRounding.AwayFromZero),
            (int)Math.Round(cursorY - grabY - frameY, MidpointRounding.AwayFromZero));
    }

    /// <summary>Takes the window out of the maximized state (or out of the shell's own work-area fill) and places it
    /// under the pointer, so the <see cref="Window.BeginMoveDrag"/> the caller hands the platform right after starts
    /// from where the reader's cursor already is. Deliberately not <see cref="ToggleMaximized"/>: this is a restore,
    /// not a flip, and had the state already changed under us a toggle would maximize the window instead. Everything
    /// is read before anything is written, because setting <see cref="Window.WindowState"/> can re-configure the
    /// window from inside the call — and the order is the whole point: the state removal and the move request are
    /// client messages on one X connection, which the window manager answers in send order, so a move handed over
    /// first is a move for a window it still has maximized.</summary>
    private void RestoreForDrag(Point press)
    {
        var remembered = _normalGeometry;
        var clientOrigin = this.PointToScreen(new Point(0, 0));
        var windowOrigin = Position;
        var barWidth = Bounds.Width;
        var scaling = RenderScaling;

        if (_fillsWorkArea) LeaveWorkAreaFill();
        WindowState = WindowState.Normal;

        // With nothing remembered there is nothing honest to place the window against. Leaving the maximized state
        // is the part that makes the window movable at all, so it still happens and the window manager does the
        // placing.
        if (remembered is not { } normal) return;
        Position = RestoredOriginForDrag(clientOrigin, windowOrigin, barWidth, press, normal.ClientSize, scaling);
    }

    /// <summary>How far the pointer has to travel before this bar moves the window. The number and the reason for
    /// it live once, on <see cref="HubDialogChrome.DragThreshold"/>, because a dialog's drawn bar has to answer to
    /// the same platform move loop; the shell's bar and every dialog's bar make the decision through
    /// <see cref="HubDialogChrome.DragExceeded"/>.</summary>

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed) return;

        // The second click of a double-click maximizes or restores, and it is decided before anything reads the
        // pointer: a toggle has no business asking the input pipeline where the cursor is, and keeping it that way
        // is what lets the self-check raise this press with a pointer of its own.
        if (TitleBarPressToggles(e.ClickCount))
        {
            _titleBarPress = null;
            e.Pointer.Capture(null);
            ToggleMaximized();
            return;
        }

        _titleBarPressOrigin = e.GetPosition(this);
        _titleBarPress = e;
        e.Pointer.Capture(TitleBarDragArea);
    }

    private void OnTitleBarMoved(object? sender, PointerEventArgs e)
    {
        if (_titleBarPress is not { } press) return;
        if (!HubDialogChrome.DragExceeded(_titleBarPressOrigin, e.GetPosition(this))) return;

        // Let go of the press before handing the platform its move loop: that loop runs until the button comes up
        // and nothing after this line in the handler may be relied on, so the state has to be settled first.
        _titleBarPress = null;
        e.Pointer.Capture(null);

        // The press is what the placement is measured from, not the current pointer: the two differ by up to the
        // drag threshold, and the window has to end up where the platform's move loop thinks the cursor is.
        if (DragRestoresMaximized(OperatingSystem.IsWindows(), WindowState, _fillsWorkArea)) RestoreForDrag(_titleBarPressOrigin);

        BeginMoveDrag(press);
    }

    private void OnTitleBarReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_titleBarPress is null) return;
        _titleBarPress = null;
        e.Pointer.Capture(null);
    }

    /// <summary>Moves the caption strip out to the window's real right edge (see <c>InitializeTitleBar</c>). The
    /// frame width comes from Win32 in physical pixels, so it is converted through the render scaling; on a
    /// non-Windows host, or where the frame is not a Win32 one, it is zero and nothing moves.</summary>
    private void SetCaptionStripInset()
    {
        var frame = TitleBarSnapHook.ResizeFrameWidth;
        if (frame <= 0) return;

        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        WindowControls.Margin = new Thickness(0, 0, -(frame / scaling), 0);
    }

    private void SyncWindowStateChrome()
    {
        // Either way the caption says "restore next": the window manager's maximized state, or the shell's own fill
        // of the work area on a host that must not ask for the first (see WslWorkAreaFill).
        var maximized = WindowState == WindowState.Maximized || _fillsWorkArea;
        if (OperatingSystem.IsLinux())
        {
            var radius = maximized
                ? new CornerRadius(0)
                : new CornerRadius(WindowCornerRadius, WindowCornerRadius, 0, 0);
            // Written with the radius because the two are one look: round means "a window floating on the desktop",
            // square means "this is the screen". A maximized window floats above nothing, so it keeps neither the
            // rounding nor the room its shadow needed — and that shadow would be drawn off the screen anyway.
            var margin = maximized ? new Thickness(0) : new Thickness(WindowShadowMargin);
            WindowSurface.CornerRadius = radius;
            WindowSurface.Margin = margin;
            // The shadow's own layer is the same rectangle as the surface — that is what makes the shadow follow the
            // window's rounding instead of boxing it in — so the two are always written together.
            WindowShadowLayer.CornerRadius = radius;
            WindowShadowLayer.Margin = margin;
            // The grips live in that margin, so they follow it: a maximized window has no room for them and nothing
            // to drag, and leaving them up would put eight invisible strips over the shell's own edges.
            WindowResizeGrips.IsVisible = !maximized;
        }

        if ((maximized ? _restoreGlyph : _maximizeGlyph) is { } glyph) MaximizeGlyph.Data = glyph;
        ToolTip.SetTip(MaximizeButton, HubStrings.Get(maximized ? "RestoreWindow" : "MaximizeWindow"));
    }

    private const double WindowCornerRadius = 12;

    /// <summary>The Linux window surface: a transparent window, with the rounded top corners drawn by
    /// <see cref="WindowSurface"/> (see <see cref="SyncWindowStateChrome"/>). Both halves of that are
    /// requests: <c>TransparencyLevelHint</c> is what this app asks for, and the platform may answer
    /// <c>None</c> anyway — a window manager that does not composite, or a 24-bit visual, leaves those
    /// corner pixels opaque and the rounding can never be seen. No assertion can reach that answer,
    /// because it exists only once a window is really on screen, so the grant is written to the log
    /// instead.</summary>
    private void ConfigureLinuxWindowSurface()
    {
        if (!OperatingSystem.IsLinux()) return;

        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        // BorderOnly is the right request on Windows, where the frame it keeps is DWM's: a shadow and a hairline
        // that both follow the rounded window. On Linux the same request is answered by the X11 backend painting the
        // frame *itself* — a square 1px frame plus a square gradient shadow, neither of which any theme can shape
        // (measured: zeroing the frame's thickness through the decoration theme shortens the band by exactly that
        // thickness, while the shadow's outline stays a rectangle, and Avalonia 12.1.3 marks the whole app-drawn
        // path AVALONIA_X11_CSD "for evaluation purposes only"). The frame is what hid the window surface's rounded
        // corners; the square shadow is what then left a rectangular ghost at them. So Linux asks for none of it and
        // draws the one part a rounded window needs — its shadow — itself, in the margin it keeps for that
        // (see WindowShadowMargin and SyncWindowStateChrome).
        WindowDecorations = WindowDecorations.None;
        WindowShadowLayer.BoxShadow = WindowShadow;

        // The resize edges are the app's here for the same reason: with no frame of the platform's there is nothing
        // to grab, and dragging an edge would do nothing at all. Each grip is sized to the shadow margin — the
        // corners to twice it, so a diagonal drag starts at the corner itself instead of a few pixels off it — and
        // hands its press to the window manager's own resize, which is what leaves MinWidth/MinHeight, the
        // work-area rules and Escape-to-cancel the platform's business rather than this app's.
        foreach (var grip in new[] { ResizeNorth, ResizeSouth }) grip.Height = WindowShadowMargin;
        foreach (var grip in new[] { ResizeWest, ResizeEast }) grip.Width = WindowShadowMargin;
        foreach (var grip in new[] { ResizeNorthWest, ResizeNorthEast, ResizeSouthWest, ResizeSouthEast })
        {
            grip.Width = WindowShadowMargin * 2;
            grip.Height = WindowShadowMargin * 2;
        }
        foreach (var (grip, edge) in new (Border Grip, WindowEdge Edge)[]
                 {
                     (ResizeNorth, WindowEdge.North), (ResizeSouth, WindowEdge.South),
                     (ResizeWest, WindowEdge.West), (ResizeEast, WindowEdge.East),
                     (ResizeNorthWest, WindowEdge.NorthWest), (ResizeNorthEast, WindowEdge.NorthEast),
                     (ResizeSouthWest, WindowEdge.SouthWest), (ResizeSouthEast, WindowEdge.SouthEast),
                 })
        {
            grip.PointerPressed += (_, e) => BeginResizeFrom(edge, e);
        }
        WindowResizeGrips.IsVisible = true;

        Opened += (_, _) => WriteLog(
            "Window surface: hint=[" + string.Join(",", TransparencyLevelHint) + "]"
            + " granted=" + ActualTransparencyLevel
            + " handle=" + (TryGetPlatformHandle()?.ToString() ?? "none")
            + " client=" + ClientSize);
    }

    /// <summary>The room the window keeps around its surface for the shadow the app draws itself, and that shadow.
    /// Two reasons it is not the platform's: on X11 that one is a rectangle, which leaves a square ghost at the
    /// rounded corners (the whole reason the corner rounding was invisible), and Windows has DWM's, which does
    /// follow the window. The margin belongs to the window rather than to the surface, so the shell's content is
    /// inset by it — and the window manager's resize edge sits this far outside the visible window, the same
    /// trade every client-side-decorated toolkit makes.</summary>
    private const double WindowShadowMargin = 8;

    /// <summary>Soft, slightly low, and a touch spread, the way a floating window reads: a BoxShadow is drawn around
    /// the border's own geometry, so it follows <see cref="WindowSurface"/>' CornerRadius instead of boxing it in.
    /// Blur + spread add up to <see cref="WindowShadowMargin"/> exactly, which is what makes the shadow fade to
    /// nothing right at the window's own edge — any more and the outer part would be cut off there, any less and the
    /// room kept for it goes unused.</summary>
    private static readonly BoxShadows WindowShadow = BoxShadows.Parse("0 2 6 2 #66000000");

    /// <summary>Hands a press on one of the edge grips to the platform's own resize. The window manager does the
    /// resizing rather than this app: it owns the size limits, the work-area rules and the gesture (Escape cancels
    /// one), and a drag loop written here would be re-implementing all three from the outside.</summary>
    private void BeginResizeFrom(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        BeginResizeDrag(edge, e);
    }

    /// <summary>Brings the window back onto its own geometry for the routes that ask for a usable window: a
    /// deeplink, a notification activation. Writing <c>Normal</c> is how the window manager's maximized state is
    /// left; on a host where the shell holds the work area itself that write changes nothing, so the fill is undone
    /// here too — otherwise the window would stay covering the screen while the glyph said "maximize".</summary>
    private void LeaveMaximizedForActivation()
    {
        WindowState = WindowState.Normal;
        if (_fillsWorkArea) LeaveWorkAreaFill();
    }

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

        // The width above already moves the bar's nav zone — that is a binding, no code needed. What the bar cannot
        // see through a binding is whether there is still a border worth continuing into it, so the hairline's
        // visibility is asked for here.
        SyncTitleZones();

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

    /// <summary>Shows/hides the log body and flips the strip chevron. The drain belongs to the body: while nobody
    /// is looking at the drawer a flood costs the UI thread nothing at all, and the retained tail is painted the
    /// moment it opens.</summary>
    internal void ShowLog(bool show)
    {
        LogBody.IsVisible = show;
        LogToggleChevron.RenderTransform = new RotateTransform(show ? 180 : 0);
        if (!show)
        {
            _logDrain.Stop();
            return;
        }

        DrainActivityLog();
        _logDrain.Start();
    }

    /// <summary>Paints everything the pump appended since the last pass, in one write.</summary>
    private void DrainActivityLog()
    {
        string text;
        lock (_logGate)
        {
            if (!_logDirty) return;
            text = _logText.ToString();
            _logDirty = false;
        }

        ActivityLog.Text = text;
        // Moving the caret to the end is how we "scroll to bottom". Avalonia's TextBox has no ScrollToEnd.
        ActivityLog.CaretIndex = text.Length;
        ActivityLogWritesForCheck++;
    }

    /// <summary>Drops the retained tail and the painted box together. Only a data-root switch does this: the old
    /// root's lines point at a directory nobody is looking at anymore.</summary>
    private void ClearActivityLog()
    {
        lock (_logGate)
        {
            _logText.Clear();
            _logDirty = false;
        }

        ActivityLog.Text = "";
    }

    /// <summary>Writes the bar's two names for the current page: the page's own name in the nav zone, and — on the
    /// assistant page only — the open conversation's title in the conversation zone.</summary>
    private void UpdatePageTitle()
    {
        NavTitle.Text = _currentKey switch
        {
            "Assistant" => HubStrings.Get("Assistant"),
            "Projects" => HubStrings.Get("Projects"),
            "Installs" => HubStrings.Get("Installs"),
            "Toolchains" => HubStrings.Get("Toolchains"),
            _ => "",
        };

        // The assistant page is the only one with a second name to show, and a conversation without a title
        // contributes none: the nav zone already says "AI 助手", so a placeholder here would say it twice.
        var title = _currentKey == "Assistant" && _chat.ActiveConversation is { Title.Length: > 0 } active
            ? active.Title
            : "";
        ConversationTitle.Text = title;
        // The hint is the whole title that the truncation may hide; it is the only reason the label is hit-test
        // visible (see InitializeTitleBar).
        ToolTip.SetTip(ConversationTitle, title.Length == 0 ? null : title);
        SyncTitleZones();
    }

    /// <summary>
    /// Decides what the bar's zones show: the two names and the hairline between them. Three facts go in — the page
    /// and the open conversation's title, which <see cref="UpdatePageTitle"/> has just written, and the two drawers'
    /// state — and this is their only writer, so the hairline cannot end up drawn beside an empty zone.
    ///
    /// That hairline is the sidebar's own border continued into the bar (MainWindow.axaml), so it exists only while
    /// there is a border to continue and a second name to separate from the first.
    /// </summary>
    private void SyncTitleZones()
    {
        // The lifted pane is a surface over the whole window and its strip takes the bar's left end; both names
        // stand aside there rather than print under a tab strip.
        var chrome = !_inspectorExpanded;
        NavTitle.IsVisible = chrome;
        ConversationTitle.IsVisible = chrome && ConversationTitle.Text is { Length: > 0 };
        TitleZoneDivider.IsVisible = ConversationTitle.IsVisible && IsSidebarExpanded;
    }

    private void InitializeChrome()    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        BrandVersion.Text = "v" + version;
        PreviewBadge.IsVisible = HubReleaseInfo.IsPrereleaseBuild;
        Title = "Axmol Hub " + BrandVersion.Text;

        // The settings gear is icon-only, so its tooltip is the only place the name "Settings" appears for it.
        // Set here rather than in ApplyLanguage so it is also in place at construction time.
        ToolTip.SetTip(SettingsButton, HubStrings.Get("Settings"));
        ToolTip.SetTip(LogActionsButton, HubStrings.Get("LogActions"));
        // The update button's tip and pill label are state-dependent, so re-resolving the current state is the
        // whole language switch for them: SyncUpdateAction reads HubStrings fresh on every call, and a label
        // assigned once at construction would keep saying last language's sentence forever.
        SyncUpdateAction();
        // Same rule for the two buttons at the title bar's right end, and this one is in InitializeChrome rather
        // than InitializeInspector because ApplyLanguage calls this: an icon-only control whose name lives only in
        // a tooltip has to be rewritten when the language changes. The drawer's sentence depends on which surface
        // is up, so that half goes through the sync that owns it rather than being written here — a language
        // switch while the pane is lifted has to leave it saying "back to the column", not "show or hide".
        ToolTip.SetTip(InspectorExpandToggle, HubStrings.Get("InspectorExpand"));
        SyncInspectorDrawerChrome();

        // The bottom text block's visible text names the machine, not the menu; the tooltip is where
        // "this opens settings and appearance" is spelled out. Same place for the same reason.
        ToolTip.SetTip(BottomMenuButton, HubStrings.Get("BottomMenuTip"));

        // The WPF version hard-codes "AXMOL 2.11 LTS" in the bottom-left. The Avalonia version
        // computes it from the **default engine** at runtime — see <see cref="SyncRailEngineLine"/>.
        SyncRailEngineLine();

        // The WPF version hard-codes "Windows x64". The Avalonia version runs on three platforms and
        // computes it from the real host.
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        HostLine.Text = os + " " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The rail's bottom line names the default engine and its version, so it is derived state rather than
    /// chrome: it is rewritten by the repaint that follows any state change (<see cref="WireWorkspace"/>
    /// subscribes it to <see cref="HubWorkspace.Changed"/>) instead of only by <see cref="InitializeChrome"/>,
    /// which runs at construction, at a data-root switch and at a language change and nowhere else.
    ///
    /// The WPF version hard-codes "AXMOL 2.11 LTS" here. Reading the version off the default engine is what
    /// keeps that line from becoming wrong the day v3 ships — exactly the A1 (single-engine version binding)
    /// class of bug.
    /// </summary>
    private void SyncRailEngineLine()
    {
        var state = _workspace.State;
        var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
        BrandLine.Text = engine is null ? "AXMOL" : "AXMOL " + engine.Version;
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
            ShowFeedback(UserFeedback.Error(ex.Message, "open-folder-error"));
        }
    }
private static bool ShouldHideOnClose(bool isWindows, bool isMacOS, bool isAutomation, bool quitConfirmed, bool trayAvailable) => !isAutomation && !quitConfirmed && (isMacOS || (isWindows && trayAvailable));
private MenuFlyout? BottomMenuFlyout=> FlyoutBase.GetAttachedFlyout(BottomMenuButton) as MenuFlyout;
private MenuFlyout? LogActionsFlyout=> FlyoutBase.GetAttachedFlyout(LogActionsButton) as MenuFlyout;
}
