using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AxmolHub;

/// <summary>
/// The shell's two right-hand surfaces: the inspector column (the plan in full, the diffs of what the assistant
/// changed, and the repository's own state against HEAD) and the window-level picture viewer. Split out of
/// <c>MainWindow.axaml.cs</c> because both are self-contained interactions with their own state, and the shell
/// file's job is navigation and workspace plumbing — neither of these is that.
/// </summary>
public partial class MainWindow
{
    // ───────────────────────── Inspector column ─────────────────────────

    /// <summary>Inspector width bounds. The floor is what keeps a diff's two columns legible; the ceiling is
    /// what keeps the chat column from being squeezed past its own floor on a narrow window.</summary>
    private const double InspectorMin = 300;
    private const double InspectorMax = 1000;
    private const double InspectorCollapseThreshold = 200;
    private const double InspectorDefault = 360;

    /// <summary>Which tab and which plan the column is currently holding. Remembered rather than re-asked, so a
    /// repaint while a run keeps writing lands where the reader was looking instead of bouncing them back to the
    /// plan they left two minutes ago. Two writers, deliberately: <see cref="OpenInspector"/> when somebody asks
    /// for a tab, and the pane itself through <c>InspectorTabFollowed</c> whenever it shows one — including from
    /// a header click, which is the only way the reader can say "not this page" without closing the column. Not
    /// persisted: which tab is a momentary decision, and a restored 仓库 would be either a git run during cold
    /// start or a tab stranded on 「正在读仓库…」.</summary>
    private string _inspectorTab = "plan";
    private int _inspectorTurn = -1;

    /// <summary>Drag state for the inspector grip, mirroring the sidebar's: pointer x and width at grab.</summary>
    private double _inspectorGrabStartX;
    private double _inspectorGrabStartWidth;
    private bool _inspectorGrabbing;

    /// <summary>Whether the inspector is open. Separate from the column's <c>IsVisible</c>, which is also gated
    /// on the current page — the inspector belongs to the assistant, and follows it off-screen.</summary>
    private bool _inspectorOpen;

    /// <summary>Whether the pane is lifted over the shell. Transient by design and deliberately not persisted: it
    /// is a way of reading one document, not a statement about how the person wants the app laid out tomorrow, and
    /// a restored window that opens on a full-screen plan with no chat in sight is a window that has to be escaped
    /// out of before anything can be said.</summary>
    private bool _inspectorExpanded;

    /// <summary>The drawer button's two faces. Read once in <see cref="InitializeInspector"/> rather than bound per
    /// state: the swap is driven from code (see <see cref="SyncInspectorDrawerChrome"/>), and the pair has to come
    /// from the same dictionary the glyph in MainWindow.axaml came from or the two states would be drawn at
    /// different scales.</summary>
    private Geometry? _drawerGlyph;
    private Geometry? _drawerRestoreGlyph;

    /// <summary>Wires the inspector grip and applies the persisted width/open state. Called once from the
    /// constructor after <c>InitializeComponent</c>, beside <see cref="InitializeSidebar"/>.</summary>
    private void InitializeInspector()
    {
        InspectorResizeGrip.PointerPressed += OnInspectorGripPressed;
        InspectorResizeGrip.PointerMoved += OnInspectorGripMoved;
        InspectorResizeGrip.PointerReleased += OnInspectorGripReleased;

        // The drawer's two glyphs, resolved once from this window's resources: the button is repainted on every
        // open, close and lift, and a lookup per repaint would be three dictionary walks to answer a question whose
        // answer never changes. They are the 0-24 grid icons, drawn Uniform in a 16px slot like the ☰ beside them.
        _drawerGlyph = this.FindResource("Hub.Icon.PanelRight") as Geometry;
        _drawerRestoreGlyph = this.FindResource("Hub.Icon.Restore") as Geometry;

        Inspector.Width = InspectorClamp(_preferences.InspectorWidth > 0
            ? _preferences.InspectorWidth
            : InspectorDefault);
        _inspectorOpen = _preferences.InspectorOpen;
        SetInspectorColumnVisible();

        // The title bar's two right-hand buttons. The drawer is the column's way in and out that does not depend on
        // the pane being built, which is the difference between a drawer and a dead end; the arrow beside it is the
        // same pane's "give me the window" control, which used to be drawn inside the pane itself — where it was
        // reachable only while there was a column to draw it in, and where the reader could not press it while the
        // pane was up. Both are wired here, and the glyph each one swaps between is resolved just below.
        InspectorToggle.Click += (_, _) => ToggleInspectorColumn();
        InspectorExpandToggle.Click += (_, _) => ToggleInspectorExpanded();
        TitleBarRightActions.SizeChanged += (_, _) => SyncInspectorTabInset();
        TitleBar.SizeChanged += (_, _) => SyncInspectorTabInset();
        Inspector.PropertyChanged += (_, e) =>
        {
            if (e.Property == WidthProperty) SyncInspectorTabInset();
        };

        // Esc on the window's own tunnel, registered here rather than after the picture viewer's and ordered by
        // an explicit guard instead: which overlay is up is a fact this handler can read, while handler order is
        // a fact about the constructor that a later edit can change without noticing.
        AddHandler(KeyDownEvent, OnInspectorExpandKeyDown, RoutingStrategies.Tunnel);
    }

    private static double InspectorClamp(double width) => Math.Clamp(width, InspectorMin, InspectorMax);

    /// <summary>
    /// The top bar's one control, in three states rather than two. While the pane is lifted over the shell the
    /// button is wearing its restore face (see <see cref="SyncInspectorDrawerChrome"/>), so the first branch puts
    /// the pane back into the column — the state check has to come before the open one, because lifting never
    /// clears <c>_inspectorOpen</c> and an open-first test reads a lifted pane as "open, therefore shut it",
    /// which closed the whole column instead of restoring its width. Otherwise it opens the column if it is shut
    /// and hides it if it is open. Both arms are methods that already have their own assertions — opening
    /// re-enters through <see cref="OpenInspector"/>, so the column comes back on the tab the reader last stood
    /// on (and if that was 仓库, it asks for its one read, because asking again is what pressing the button
    /// means), and closing is <see cref="CloseInspector"/>, so the persisted flag, the overlay and the grip all
    /// come down together rather than one of them being left behind.
    /// </summary>
    private void ToggleInspectorColumn()
    {
        if (_inspectorExpanded)
        {
            RestoreInspector();
            return;
        }
        if (_inspectorOpen)
        {
            CloseInspector();
            return;
        }
        BuildWorkspaceContent();
        ShowWorkspace();
    }

    /// <summary>The pane's two possible homes: the column, and the overlay it is lifted into. Exactly one may
    /// hold it — a control with a live visual parent cannot be adopted by a second <c>ContentControl</c>, which
    /// is an exception rather than a quiet failure — so every move clears both first, and nothing else in this
    /// file assigns to either host. The tabs are the one thing that moves the other way: they are never inside the
    /// pane, and this is the single place they are handed to the bar's slot.</summary>
    private Control? InspectorPane
        => InspectorHost.Content as Control ?? InspectorExpandContent.Content as Control;

    private void SetInspectorPane(Control? pane)
    {
        var target = _inspectorExpanded ? InspectorExpandContent : InspectorHost;
        if (ReferenceEquals(target.Content, pane))
        {
            (pane as WorkspaceView)?.RefreshLayout();
            return;
        }
        using var browserReparenting = (InspectorPane as WorkspaceView)?.PreserveBrowsersForReparenting();
        InspectorHost.Content = null;
        InspectorExpandContent.Content = null;
        // The plan/changes strip is the one part of the pane that does not travel with it: both homes put it in the
        // same title-bar slot, so the tabs stay where the reader left them while the pane is lifted. Handing it over
        // here rather than in each of the two calls is what keeps one entry point for "the pane changed hands".
        InspectorTabSlot.Content = (pane as WorkspaceView)?.TabStrip;
        if (_inspectorExpanded) InspectorExpandContent.Content = pane;
        else InspectorHost.Content = pane;
        // Visibility-dependent observers must be restored after reparenting, not while the view is detached.
        (pane as WorkspaceView)?.RefreshLayout();
    }

    /// <summary>Opens the inspector on a tab ("plan" or "changes") for one specific plan turn (-1 for the newest)
    /// and returns true so the caller knows the surface it asked for is now up. The content is hosted in the
    /// column, or in the overlay if the pane is currently expanded; a conversation with nothing to show still
    /// opens, because "there is no plan yet" is itself an answer.</summary>
    internal bool OpenInspector(string tab, int turnIndex = -1)
    {
        _inspectorTab = tab;
        _inspectorTurn = turnIndex;
        _inspectorOpen = true;
        SetInspectorPane(BuildWorkspaceContent());
        _workspaceView?.OpenTool(tab);
        SetInspectorColumnVisible();
        if (_preferences.InspectorOpen != true)
        {
            _preferences.InspectorOpen = true;
            _preferencesStore.Save(_preferences);
        }
        return true;
    }

    /// <summary>
    /// Re-derives what is already on screen, for a transcript that keeps growing while the column is open.
    ///
    /// It does nothing when the column is closed or on another page. The pane requests a missing repository
    /// snapshot when needed, coalesces in-flight reads, and reuses the result on subsequent repaints.
    /// </summary>
    internal void RefreshInspector()
    {
        if (!_inspectorOpen || _currentKey != "Assistant" || _chatPanel is null) return;
        SetInspectorPane(BuildWorkspaceContent());
    }

    internal void CloseInspector()
    {
        // First out of the overlay, so a close can never leave a full-screen pane with no column to go back to.
        RestoreInspector();
        _inspectorOpen = false;
        SetInspectorColumnVisible();
        if (_preferences.InspectorOpen)
        {
            _preferences.InspectorOpen = false;
            _preferencesStore.Save(_preferences);
        }
    }

    /// <summary>Applies the three gates at once: the inspector shows only on the assistant page, only while
    /// open, and only while it is not lifted out of the column — where the column would hand the same pane to two
    /// hosts at once. The grip rides with the column, so there is never a floating handle beside a hidden pane.
    ///
    /// This is also the one place the title bar's two right-hand buttons are told what they are. The arrow shows
    /// exactly when there is a column to lift (so it is absent both when the column is shut and when the pane has
    /// already covered the shell), and the drawer is the column's mirror image: while the pane is lifted there is
    /// no column to shut, so the same button becomes the way back down (see
    /// <see cref="SyncInspectorDrawerChrome"/>). And it is the one place the bar's *left* end is told what it
    /// holds: the tabs' slot, and the controls — the drawer, the page's name and the conversation's title — that
    /// stand aside while the pane is lifted so the strip can take their place.</summary>
    private void SetInspectorColumnVisible()
    {
        var show = _inspectorOpen && _currentKey == "Assistant" && !_inspectorExpanded;
        // The one rule the column may not break: a column on screen has a pane in it. Every way of getting here
        // except an explicit <see cref="OpenInspector"/> — a restored open flag, a page navigation, coming back
        // out of the lifted state — used to be able to show the chrome with nothing inside, and the header and the
        // tabs all stem from the pane, so what was on screen was an empty strip at the window's edge.
        if (show) EnsureInspectorPane();
        InspectorColumn.IsVisible = show;
        InspectorResizeGrip.IsVisible = show;
        // The pane's left border, continued up through the bar at the pane's own x (see the InspectorEdgeDivider
        // comment in MainWindow.axaml). It rides with the column for the reason the grip does — and lifted there is
        // no column for a border to be the edge of.
        InspectorEdgeDivider.IsVisible = show;
        // The tabs outlive both homes, so they are placed here and not by the move. In the column the slot *is* the
        // column's left edge — a five-pixel margin for the grip stands in for the column's own first column — and
        // lifted it becomes the bar's left end across all three columns, which is the drawer button's place. That
        // is the whole reason the two controls beside it stand aside: the strip is drawn where they were, and a
        // drawer the reader could not see is worse than one that comes back with the column it belongs to. Every
        // route in and out of the lifted state ends here, so leaving the assistant page puts all three back.
        InspectorTabSlot.IsVisible = _inspectorOpen && _currentKey == "Assistant";
        Grid.SetColumn(InspectorTabSlot, _inspectorExpanded ? 0 : 2);
        Grid.SetColumnSpan(InspectorTabSlot, _inspectorExpanded ? 3 : 1);
        SyncInspectorTabInset();
        SidebarToggle.IsVisible = !_inspectorExpanded;
        // The bar's two names and the hairline between them ride with the same fact, and SyncTitleZones is their
        // only writer — so it is asked here rather than the three elements being poked from the outside.
        SyncTitleZones();
        // The drawer button belongs to the page, not to the column: while the pane is lifted the column is
        // legitimately empty, and a button that flipped off there would leave the person no way back to the column
        // at all. On another page there is nothing for it to show or hide, and nothing to lift either.
        InspectorToggle.IsVisible = _currentKey == "Assistant";
        InspectorExpandToggle.IsVisible = show;
        SyncInspectorDrawerChrome();
    }

    // The sibling tab host must stop before the actual action plates, including platform-specific captions.
    // Reserving their measured width prevents tabs and menus from painting or receiving clicks over them.
    private void SyncInspectorTabInset()
    {
        var actions = TitleBarRightActions.Bounds.Width;
        var grip = _inspectorExpanded ? 0 : 5;
        InspectorTabSlot.Margin = new Thickness(grip, 0, actions, 0);
        // An Auto column otherwise measures the tabs at infinity and lets their desired width widen the
        // column again after a grip drag. The content pane is the width authority, never the tab captions.
        InspectorTabSlot.Width = Math.Max(0, (_inspectorExpanded ? TitleBar.Bounds.Width : Inspector.Width) - actions);
        // The title bar already reserves the action plates in its own Auto column, but its title lane does
        // not share the shell's inspector column. Reserve the rest here so ellipsis and hit testing stop
        // at the grip's left edge instead of letting a growing tab strip paint over the title.
        var titleInset = InspectorColumn.IsVisible ? Math.Max(0, Inspector.Width + grip - actions) : 0;
        ConversationTitleZone.Margin = new Thickness(0, 0, titleInset, 0);
    }

    /// <summary>
    /// Gives the drawer button the face and the sentence that match what pressing it would do now. In the column it
    /// opens and shuts the column; in the lifted state there is no column on screen to shut, so it is the way back
    /// down — the same slot, the same plate, a different meaning, which is why this is one button and not two.
    /// Nothing here is remembered: both readings come from <c>_inspectorExpanded</c>, which is the same field the
    /// shell consults to decide which surface the pane is in.
    ///
    /// Called from <see cref="SetInspectorColumnVisible"/> on every state change and from <c>InitializeChrome</c>,
    /// which is what a language switch re-runs — an icon-only control whose name lives only in a tooltip has to
    /// have that tooltip rewritten, and it has to be rewritten to the *current* state's sentence rather than to
    /// the column's.
    /// </summary>
    private void SyncInspectorDrawerChrome()
    {
        var lifted = _inspectorExpanded;
        if ((lifted ? _drawerRestoreGlyph : _drawerGlyph) is { } glyph) InspectorToggleGlyph.Data = glyph;
        ToolTip.SetTip(InspectorToggle, HubStrings.Get(lifted ? "InspectorRestore" : "InspectorToggleTip"));
    }

    /// <summary>Builds the pane if nothing has yet, through the same door <see cref="OpenInspector"/> uses, and
    /// never for a column that already has one — the pane is kept for the window's life precisely so that
    /// repainting does not fold back the diff rows a person had opened. Which tab is remembered is not persisted, so what
    /// lands here is the plan tab of whatever conversation is on screen — including the honest
    /// 「还没有生成计划」 when it has none.</summary>
    private void EnsureInspectorPane()
    {
        if (InspectorPane is not null || _chatPanel is null) return;
        SetInspectorPane(BuildWorkspaceContent());
    }

    /// <summary>Called from <see cref="NavigateTo"/>: the inspector follows the assistant page on and off, and
    /// so does its lifted state — a pane that outlived the page it belongs to would be a surface with no
    /// conversation behind it, covering the settings page.</summary>
    private void SyncInspectorForPage()
    {
        if (_currentKey != "Assistant") RestoreInspector();
        SetInspectorColumnVisible();
    }

    /// <summary>Lifts the pane over the shell, or brings it back down. The title bar's arrow and the drawer it
    /// becomes while the pane is up both land here, so "which one is showing" never has to be known by the
    /// buttons.</summary>
    internal void ToggleInspectorExpanded()
    {
        if (_inspectorExpanded) RestoreInspector();
        else ExpandInspector();
    }

    private void ExpandInspector()
    {
        var pane = InspectorPane;
        if (_inspectorExpanded || pane is null) return;
        _inspectorExpanded = true;
        SetInspectorPane(pane);
        _workspaceView?.SetExpanded(true);
        InspectorExpandHost.IsVisible = true;
        SetInspectorColumnVisible();
    }

    /// <summary>Puts the pane back in the column. Idempotent, because three callers can race to it — the title
    /// bar's arrow (or the drawer standing in for it while the pane is up), Esc, and leaving the assistant page —
    /// and none of them should have to know what the others did. The column's own width is never touched by any of
    /// this, which is what makes the dragged width survive the round trip without anything having to remember
    /// it.</summary>
    internal void RestoreInspector()
    {
        if (!_inspectorExpanded) return;
        var pane = InspectorPane;
        _inspectorExpanded = false;
        SetInspectorPane(pane);
        _workspaceView?.SetExpanded(false);
        InspectorExpandHost.IsVisible = false;
        SetInspectorColumnVisible();
    }

    private void OnInspectorExpandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // The layers above this one own Escape first, and the order is stated here rather than left to handler
        // registration: settings is the topmost layer of the window, the picture viewer sits above the expanded
        // inspector, and only then does an expanded inspector get the key — before the composer's decision host,
        // which is a window away from being on top.
        if (SettingsOverlayHost.IsVisible || NewWorkspaceOverlayHost.IsVisible) return;
        if (PictureViewerHost.IsVisible) return;
        if (!_inspectorExpanded) return;
        RestoreInspector();
        e.Handled = true;
    }

    private void OnInspectorGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _inspectorGrabbing = true;
        _inspectorGrabStartX = e.GetPosition(this).X;
        _inspectorGrabStartWidth = Inspector.Width;
        e.Pointer.Capture(InspectorResizeGrip);
        e.Handled = true;
    }

    private void OnInspectorGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_inspectorGrabbing) return;
        // The grip is on the right, so dragging left grows the pane: the delta is inverted against the sidebar's.
        var width = _inspectorGrabStartWidth - (e.GetPosition(this).X - _inspectorGrabStartX);
        if (width < InspectorCollapseThreshold)
        {
            _inspectorGrabbing = false;
            e.Pointer.Capture(null);
            CloseInspector();
            return;
        }
        Inspector.Width = InspectorClamp(width);
    }

    private void OnInspectorGripReleased(object? sender, PointerEventArgs e)
    {
        if (!_inspectorGrabbing) return;
        _inspectorGrabbing = false;
        e.Pointer.Capture(null);
        _preferences.InspectorWidth = InspectorClamp(Inspector.Width);
        _preferencesStore.Save(_preferences);
    }

    // ───────────────────────── Picture viewer ─────────────────────────

    /// <summary>The set the viewer pages through and where it stands in it. Empty when shut; the loader on each
    /// entry stays unread until the viewer lands on that entry (see <see cref="PictureRef"/>).</summary>
    private IReadOnlyList<PictureRef> _viewerSet = Array.Empty<PictureRef>();
    private int _viewerIndex;
    private Bitmap? _viewerBitmap;

    /// <summary>Builds the caption-row buttons once. They never change, so rebuilding them per open would only
    /// churn the object graph a check counts.</summary>
    private void InitializePictureViewer()
    {
        PicturePrevButton.Click += (_, _) => StepPictureViewer(-1);
        PictureNextButton.Click += (_, _) => StepPictureViewer(+1);
        PictureViewerScrim.PointerPressed += (_, e) => { ClosePictureViewer(); e.Handled = true; };

        PictureViewerBar.Children.Add(ViewerCopyActionButton());
        PictureViewerBar.Children.Add(ViewerActionButton(
            "PictureViewerClose", "Hub.Icon.Close", ClosePictureViewer));

        // Window-level keys, on the tunnel so a focused composer or transcript cannot eat them first: while the
        // viewer is up it owns ←/→/Esc, and only while it is up.
        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);
    }

    private static Button ViewerActionButton(string textKey, string geometryKey, Action? onClick)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        var button = new Button { Content = icon, Tag = textKey };
        button.Classes.Add("viewer-action");
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        if (onClick is not null)
            button.Click += (_, _) => onClick();
        return button;
    }

    private Button ViewerCopyActionButton()
    {
        var button = ViewerActionButton("PictureViewerCopy", "Hub.Icon.Copy", null);
        var icon = (Avalonia.Controls.Shapes.Path)button.Content!;
        var feedbackVersion = 0;
        button.Click += async (_, _) =>
        {
            if (!await CopyViewerPictureAsync()) return;
            var version = ++feedbackVersion;
            if (Application.Current?.TryGetResource("Hub.Icon.CopySuccess", null, out var success) == true
                && success is Geometry successGeometry)
                icon.Data = successGeometry;
            DispatcherTimer.RunOnce(() =>
            {
                if (feedbackVersion == version
                    && Application.Current?.TryGetResource("Hub.Icon.Copy", null, out var original) == true
                    && original is Geometry originalGeometry)
                    icon.Data = originalGeometry;
            }, TimeSpan.FromSeconds(1.2));
        };
        return button;
    }

    /// <summary>Opens the viewer on one picture of a set. A set with nothing in it opens nothing: an empty frame
    /// would read as "the picture is gone", which is a different claim from the one the caller is making.</summary>
    internal void OpenPictureViewer(IReadOnlyList<PictureRef> set, int index)
    {
        if (set is null || set.Count == 0) return;
        _viewerSet = set;
        PictureViewerHost.IsVisible = true;
        ShowPictureAt(index);
    }

    /// <summary>Lands the viewer on one entry: decodes it, captions it, and arms the paging buttons. Wraps, so
    /// the last picture's "next" is the first — a viewer you can page out of is a viewer you have to close.</summary>
    private void ShowPictureAt(int index)
    {
        var count = _viewerSet.Count;
        if (count == 0) { ClosePictureViewer(); return; }
        _viewerIndex = ((index % count) + count) % count;

        var reference = _viewerSet[_viewerIndex];
        var bytes = SafeLoad(reference);
        _viewerBitmap?.Dispose();
        _viewerBitmap = Decode(bytes);
        PictureViewerImage.Source = _viewerBitmap;

        var position = string.Format(
            HubStrings.Get("PictureViewerPositionFormat"), _viewerIndex + 1, count);
        PictureViewerCaption.Text = _viewerBitmap is null
            ? $"{reference.Caption} · {HubStrings.Get("PictureViewerMissing")}"
            : $"{reference.Caption} · {position}";

        // Paging is meaningless with one picture; hiding the arrows says so without a disabled-looking control.
        var many = count > 1;
        PicturePrevButton.IsVisible = many;
        PictureNextButton.IsVisible = many;
    }

    private void StepPictureViewer(int delta) => ShowPictureAt(_viewerIndex + delta);

    internal void ClosePictureViewer()
    {
        PictureViewerHost.IsVisible = false;
        PictureViewerImage.Source = null;
        _viewerBitmap?.Dispose();
        _viewerBitmap = null;
        _viewerSet = Array.Empty<PictureRef>();
        _viewerIndex = 0;
    }

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (!PictureViewerHost.IsVisible) return;
        // Settings is the one layer stacked above the viewer, so a card on screen owns Escape: closing the
        // picture underneath it would take away a frame the reader is not looking at and leave the card up.
        if (SettingsOverlayHost.IsVisible || NewWorkspaceOverlayHost.IsVisible) return;
        switch (e.Key)
        {
            case Key.Escape:
                ClosePictureViewer();
                e.Handled = true;
                break;
            case Key.Left:
                StepPictureViewer(-1);
                e.Handled = true;
                break;
            case Key.Right:
                StepPictureViewer(+1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Hands the on-screen picture to the clipboard as a bitmap, not a filename or a run of text.</summary>
    private async System.Threading.Tasks.Task<bool> CopyViewerPictureAsync()
    {
        var payload = BuildViewerCopyPayload();
        if (payload is null) return false;
        var clipboard = this.Clipboard;
        if (clipboard is null) return false;
        try
        {
            await clipboard.SetDataAsync(payload);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Runs the entry's loader without letting one bad file take the viewer down: a picture that will
    /// not decode is shown as "gone" in the caption, and the rest of the set still pages.</summary>
    private static byte[]? SafeLoad(PictureRef reference)
    {
        try { return reference.Load(); }
        catch (Exception) { return null; }
    }

    private static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try { return new Bitmap(new System.IO.MemoryStream(bytes)); }
        catch (Exception) { return null; }
    }

    /// <summary>The transfer the copy button would hand the clipboard, built but not written — so a check can
    /// read the payload without disturbing whatever the person last copied.</summary>
    internal DataTransfer? BuildViewerCopyPayload()
    {
        var bytes = _viewerIndex < _viewerSet.Count ? SafeLoad(_viewerSet[_viewerIndex]) : null;
        var bitmap = Decode(bytes);
        if (bitmap is null) return null;
        var item = new DataTransferItem();
        item.Set(DataFormat.Bitmap, bitmap);
        var data = new DataTransfer();
        data.Add(item);
        return data;
    }
}
