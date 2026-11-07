using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;
public partial class MainWindow
{
    // ───────────────────────── Seams for the shell self-check ─────────────────────────
    internal bool SettingsOverlayVisibleForCheck => SettingsOverlayHost.IsVisible;
    /// <summary>The page behind the card, or null before anything opened it.</summary>
    internal SettingsPage? SettingsPageForCheck => _settings;
    internal Rect SettingsOverlayBoundsForCheck => InClient(SettingsOverlayHost);
    internal Rect SettingsCardBoundsForCheck => InClient(SettingsCard);
    internal Rect SettingsScrimBoundsForCheck => InClient(SettingsScrim);
    internal Rect StatusStripBoundsForCheck => InClient(StatusStrip);
    /// <summary>The card as a visual, so a render check can photograph **it** rather than the window: a frame
    /// of the window reads as non-blank off the shell behind the scrim, which would let a card that painted
    /// nothing pass.</summary>
    internal Control SettingsCardForCheck => SettingsCard;

    /// <summary>Where the layer sits in the stacking order. Nothing sets ZIndex, so document order is the
    /// whole contract, and a layer in the wrong slot still covers the shell just as well — which is exactly
    /// why a bounds check cannot catch it and this can (same argument as
    /// <see cref = "InspectorOverlayOrderForCheck"/>).</summary>
    internal bool SettingsOverlayOrderForCheck
    {
        get
        {
            var shell = ClientRoot.Children.IndexOf(ShellRoot);
            var inspector = ClientRoot.Children.IndexOf(InspectorExpandHost);
            var viewer = ClientRoot.Children.IndexOf(PictureViewerHost);
            var settings = ClientRoot.Children.IndexOf(SettingsOverlayHost);
            return shell >= 0 && shell < inspector && inspector < viewer && viewer < settings;
        }
    }

    /// <summary>Whether the scrim actually blocks: a Border with no brush hit-tests nothing, so a scrim that
    /// lost its background would leave the shell behind it clickable while every geometry check still passed.
    /// The check that reads this is paired with the one that measures the scrim's rect — together they say
    /// "the shell is covered", which neither half can say alone.</summary>
    internal bool SettingsScrimBlocksForCheck => SettingsScrim.Background is not null;
    /// <summary>The category the card is on, for the cross-page and deep-link assertions.</summary>
    internal string SettingsCategoryForCheck => _settingsCategory;
    /// <summary>The scrim itself, so a check can raise a real press on it the way it does on the title bar's
    /// drag layer — exercising the wiring rather than calling <see cref = "CloseSettings"/>.</summary>
    internal Border SettingsScrimForCheck => SettingsScrim;

    /// <summary>Closes the card through the header's own button rather than the method behind it.</summary>
    internal void ClickSettingsCloseForCheck() => SettingsCloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>Sends a key down the same tunnel route <see cref = "OnSettingsKeyDown"/> listens on.</summary>
    internal void PressSettingsKeyForCheck(Key key) => OnSettingsKeyDown(this, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None, Source = this, });
    /// <summary>Opens the card on a named category and hands back the page, so a check does not have to reach
    /// for the field separately and can fail on a null page rather than on a null reference inside one.</summary>
    internal SettingsPage? OpenSettingsForCheck(string? category)
    {
        OpenSettings(category);
        return _settings;
    }

    /// <summary>Runs the shell's answer to "a session needs a decision" — the same handler the
    /// <c>AttentionRequired</c> event calls — so a check can prove the card steps aside for a question without
    /// parking a real approval behind a model call.</summary>
    internal void RaiseAttentionForCheck(ChatAttentionKind kind) => OnChatAttentionRequired(_chat.ViewedConversationId ?? "", kind);
    /// <summary>The category the page is showing, read off the page rather than the shell's copy.</summary>
    internal string SettingsSelectedCategoryForCheck => _settings?.SelectedCategory ?? "";
    /// <summary>Whether the strip still offers the repository entry. It must not: the Changes column is the one
    /// place repository state is read, and a second entry in the strip was a duplicate door onto the same tab.
    /// Asked of the live strip rather than a constant, so undoing the removal fails this check.</summary>
    internal bool RepoChangesEntryInStripForCheck => StatusStrip.GetLogicalDescendants()
        .OfType<Control>()
        .Any(control => control.Tag is "RepoChangesEntry" || control.Name == "RepoIndicator");

    /// <summary>
    /// A close hides the window instead of ending the process whenever the host has a way back to it:
    /// Windows' tray icon (once it was created) and the macOS Dock, which every app has.
    ///
    /// <para>Hiding, not "closing the window but staying alive": <see cref = "Window.Closed"/> disposes the
    /// workspace, the chat session store and the attention service, so a real close would end background
    /// replies and the task queue as well — which is not what "close the window" means on either host, and
    /// not what the tray behaviour has done on Windows all along. A hidden window never raises the last-window
    /// close, so the desktop lifetime's default <c>OnLastWindowClose</c> never fires either, and the process
    /// stays. A quit the platform started is a different close and never reaches this rule — see
    /// <see cref = "IsPlatformQuit"/>, without which it would cancel the shutdown instead of performing it.
    /// Automation is excluded because the check and screenshot runs close their window to exit, and
    /// <paramref name = "quitConfirmed"/> because an answered "quit anyway" must really quit.</para>
    /// </summary>
    internal static bool ShouldHideOnCloseForCheck(bool isWindows, bool isMacOS, bool isAutomation, bool quitConfirmed, bool trayAvailable) => ShouldHideOnClose(isWindows, isMacOS, isAutomation, quitConfirmed, trayAvailable);
    internal bool TaskSlotVisibleForCheck => TaskSlot.IsVisible;
    internal string TaskSlotTextForCheck => TaskSlotText.Text ?? "";
    /// <summary>The two surfaces the busy gate used to switch off. A change running now has to leave both of
    /// them pressable — the page it was started from and the settings card — and answer the press that cannot
    /// go through with a sentence instead. Reading IsEnabled here is the only way to tell "the gate is gone"
    /// from "the gate moved somewhere invisible".</summary>
    internal bool PageEnabledForCheck => PageHost.IsEnabled;
    internal bool SettingsHostEnabledForCheck => SettingsHost.IsEnabled;
    /// <summary>What the strip's own line says — where a refused press leaves its reason.</summary>
    internal string StatusTextForCheck => Status.Text ?? "";
    internal string ToastTextForCheck => string.Join(" / ", ToastStack.Children.OfType<Border>()
        .SelectMany(border => border.GetLogicalDescendants().OfType<TextBlock>())
        .Where(block => block.Classes.Contains("toast-text"))
        .Select(block => block.Text ?? "")
        .Where(text => text.Length > 0));
    /// <summary>The strip's cancel, as the user can press it. An unlisted second-scale change has no row to
    /// cancel from, so this button is the only answer it gets — and it had better be enabled.</summary>
    internal bool CanCancelForCheck => CancelButton.IsEnabled;

    /// <summary>Presses the strip's cancel through the button, so a check proves the wiring and not the
    /// workspace method the button happens to call.</summary>
    internal void ClickCancelButtonForCheck() => CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>What the slot's tooltip says — the queue's own sentence, in every state the slot can show.
    /// The update's version lives on the update button's tip now, so a check on "which update is this"
    /// reads that button rather than the strip.</summary>
    internal string TaskSlotTipForCheck => _taskSlotTip.Text ?? "";
    internal bool TaskPopupOpenForCheck => TaskPopup.IsOpen;
    internal int TaskRowCountForCheck => TaskRows.Children.Count;
    /// <summary>The words on each row's visible buttons, rows in order: a running row says 取消, a failed one
    /// says 重试+忽略. Read straight off the affordance, because the phase would pass while the button behind
    /// it is missing or mis-wired.</summary>
    internal string TaskRowActionsForCheck => string.Join(" / ", _taskRowViews.Select(view => view.ActionsForCheck));

    /// <summary>One row's tooltip and its measure column, as "tip|measure" — so a check can prove the
    /// estimate stayed out of the column that carries measured bytes.</summary>
    internal string TaskRowMeasureForCheck(string identity)
    {
        var view = _taskRowViews.FirstOrDefault(v => v.Bound.Identity == identity);
        return view is null ? "" : view.TipForCheck + "|" + view.Bound.MeasureText;
    }

    /// <summary>One row's title line as drawn: <c>标题 · 阶段</c>. The stage a build printed about itself is
    /// the thing a check has to see, and it is only on this line.</summary>
    internal string TaskRowTitleForCheck(string identity) => _taskRowViews.FirstOrDefault(v => v.Bound.Identity == identity)?.TitleForCheck ?? "";
    /// <summary>Presses one row's button by its word, through the button rather than the queue method it
    /// ends up calling. False when the row does not offer that word right now — which is itself the answer a
    /// check wants about a failed row that stopped being retryable.</summary>
    internal bool ClickTaskRowActionForCheck(string identity, string text)
    {
        var button = _taskRowViews.FirstOrDefault(view => view.Bound.Identity == identity)?.ActionButtons.FirstOrDefault(candidate => candidate.IsVisible && string.Equals(candidate.Content as string, text, StringComparison.Ordinal));
        if (button is null)
        {
            return false;
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>The slot and the popover as visuals, for a frame capture. The popover needs this rather
    /// than a region of the window's frame because a Popup presents in its own PopupRoot — it is a
    /// top-level visual, and not part of the window's pixels.</summary>
    internal Visual TaskSlotForCheck => TaskSlot;
    internal Visual? TaskPopoverForCheck => TaskPopup.Child;

    internal void OpenTaskPopupForCheck() => ToggleTaskPopup();
    internal void CloseTaskPopupForCheck() => TaskPopup.IsOpen = false;
    /// <summary>The update button's armed state (the second tap of the restart), read and written through the
    /// service that owns it: the settings card's button presses the same gate, so a check that read a field
    /// here would be reading half of the state. Setting it repaints, so a check reads what the user would see
    /// rather than what a field happens to hold. A test reaches the busy half through
    /// <see cref = "RestartInterruptsWork"/>, not through this.</summary>
    internal bool RestartArmedForCheck { get => UpdateService.Instance.RestartArmed; set => UpdateService.Instance.RestartArmed = value; }

    /// <summary>Presses the slot the way the user does, so a check walks the same routing.</summary>
    internal void ClickTaskSlotForCheck() => TaskSlot.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    // ── The sidebar's update button, for the shell self-check ──
    // The button is icon-only in two of its three states, so every readable fact about it has to be a
    // dedicated reading: what shape it took, what the dot is doing, what the tip says, and where the two
    // sit next to the gear. None of these infer from UpdateService — they read the rendered control.
    /// <summary>Whether the button is on screen at all (the idle sidebar must not carry it).</summary>
    internal bool UpdateActionVisibleForCheck => UpdateActionHost.IsVisible;
    /// <summary>The pill's word — empty in the circle states, where the glyph speaks instead.</summary>
    internal string UpdateActionLabelForCheck => UpdateActionText.Text ?? "";
    /// <summary>Which of the two shapes is on the button right now, read off the classes the style
    /// selector actually matches.</summary>
    internal string UpdateActionShapeForCheck => UpdateActionButton.Classes.Contains("pill") ? "pill" : UpdateActionButton.Classes.Contains("icon") ? "icon" : "?";
    /// <summary>Whether the red dot is up. A check reads this and then the dot's pixels — IsVisible alone
    /// proves the decision, not the paint.</summary>
    internal bool UpdateActionDotVisibleForCheck => UpdateActionDot.IsVisible;
    /// <summary>The button's tooltip, where the pending version is spelled out in every state that owes one.</summary>
    internal string UpdateActionTipForCheck => ToolTip.GetTip(UpdateActionButton) as string ?? "";
    /// <summary>Which glyph is in the content grid: "send" (found), "label" (pill) —
    /// the shape a frame capture cannot name for itself.</summary>
    internal string UpdateActionGlyphForCheck => UpdateActionText.IsVisible ? "label" : UpdateActionGlyph.IsVisible ? "send" : "none";

    /// <summary>The three pieces of the bottom row in the <b>window's own</b> coordinates — the space a
    /// frame capture reads pixels in, so "the button sits left of the gear" and "the dot paints red at the
    /// button's top-right" are decided in one coordinate system instead of two. Raw <c>Bounds</c> would be
    /// three different parent spaces (the dot is a child of the host, the host and the gear are siblings).</summary>
    internal (Rect Button, Rect Dot, Rect Gear) UpdateActionGeometryForCheck() => (InWindow(UpdateActionButton), InWindow(UpdateActionDot), InWindow(SettingsButton));
    /// <summary>The dot paints above the button rather than under it: a child index is the only thing that
    /// decides that — two elements sharing a grid pass every bounds check in either order.</summary>
    internal bool UpdateActionDotAboveButtonForCheck => UpdateActionHost.Children.IndexOf(UpdateActionDot) > UpdateActionHost.Children.IndexOf(UpdateActionButton);
    /// <summary>The update button as a visual, for a frame capture (the pill's plate, the circle, the dot).</summary>
    internal Visual UpdateActionHostForCheck => UpdateActionHost;
    /// <summary>The dot itself as a visual. Capturing the dot rather than a window region is deliberate:
    /// a region read off a window capture has to agree with the capture's pixel origin, and a mismatch
    /// reads as "no red" no matter what the screen shows — the dot's own frame has no coordinates to get
    /// wrong, and a dot whose Fill went blank comes back IsBlank, which is exactly the failure to catch.</summary>
    internal Visual UpdateActionDotForCheck => UpdateActionDot;

    /// <summary>Presses the update button the way the user does, so a check walks the same routing.</summary>
    internal void ClickUpdateActionForCheck() => UpdateActionButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    /// <summary>The restart pill's corner radius, and the gear's beside it. Two readings rather than one
    /// constant so the assertion can be the relation the design actually wants — the pill is rounded like the
    /// control it stands next to, not like a value written down here (see Button.update-action.pill).</summary>
    internal double UpdateActionRadiusForCheck => UpdateActionButton.CornerRadius.TopLeft;
    internal double SettingsRadiusForCheck => SettingsButton.CornerRadius.TopLeft;

    internal MenuFlyout? LogActionsMenuForCheck => LogActionsFlyout;

    /// <summary>
    /// What a row draws, for the self-check: the name it shows, the hint line under it — always empty, and the
    /// lookup is kept as a sentinel so a sentence cannot come back under a row without the suite saying so — and
    /// whether the tick is painted.
    ///
    /// **Read off the header control this window built, not off the item's own visual tree**: a row inside a
    /// submenu nobody has opened has no template realized, so a visual-tree walk that starts at the item finds
    /// nothing, and an unopened row would read as a broken one.
    /// </summary>
    internal static (string Title, string Hint, bool Ticked) MenuRowShapeForCheck(MenuItem item)
    {
        if (item.Header is not Control header)
        {
            return ("<null>", "", false);
        }

        return (Named<TextBlock>("MenuRowTitle")?.Text ?? "<null>", Named<TextBlock>("MenuRowHint")?.Text ?? "", Named<Avalonia.Controls.Shapes.Path>("MenuRowTick")?.IsVisible == true);
        T? Named<T>(string name)
            where T : Control => header.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);
    }

    /// <summary>How many times the retained tail has been painted into the box. The suite reads it to prove a
    /// flood was coalesced — the honest figure is one per drain, not one per line.</summary>
    internal int ActivityLogWritesForCheck { get; private set; }
    internal string ActivityLogTextForCheck => ReadRetainedLog();
    internal Button AssistantSearchButtonForCheck => AssistantSearchButton;
    internal Button AssistantNewButtonForCheck => AssistantNewButton;
    internal bool AssistantActionsRightmostForCheck => NavAssistant.Parent is Grid grid && AssistantSearchButton.Parent == grid && AssistantNewButton.Parent == grid && Grid.GetColumnSpan(NavAssistant) == grid.ColumnDefinitions.Count && Grid.GetColumn(AssistantSearchButton) == grid.ColumnDefinitions.Count - 2 && Grid.GetColumn(AssistantNewButton) == grid.ColumnDefinitions.Count - 1;

    internal void ClickAssistantSearchForCheck() => AssistantSearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void ClickAssistantNewForCheck() => AssistantNewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>Takes the change gate the way an assistant command does — through the seam, not by calling the
    /// workspace directly — so a self-check can prove the seam points at the workspace that is live <b>now</b>
    /// rather than the one it was wired to at startup. Null means the seam is not wired at all.</summary>
    internal IDisposable? TakeAssistantGateForCheck(string subject) => _chat.ChangeGateProvider?.Invoke(subject);
    // ── Title bar, for the shell self-check ──
    /// <summary>The app draws its own title bar, so a self-check has to be able to read back that the client
    /// area really was extended (a box that is 32px tall over a 32px native caption strip would look identical
    /// in a screenshot and be wrong by one row).</summary>
    internal bool ClientAreaExtendedForCheck => ExtendClientAreaToDecorationsHint;
    /// <summary>Whether the Snap Layouts hit test really subclassed this window. False on a non-Windows host and
    /// also false if the platform handle was not a Win32 one, so it distinguishes "not this platform" from "the
    /// hook silently declined" — which no screenshot can.</summary>
    internal bool SnapHookInstalledForCheck => TitleBarSnapHook.InstalledForCheck;
    /// <summary><c>BorderOnly</c>, not the <c>Full</c> default: only <c>Full</c> makes Avalonia draw a title bar
    /// over the client area (Window.ComputeDecorationParts gates the TitleBar part on it), and that title bar
    /// wrote the window title straight over this app's own bar. BorderOnly keeps the shadow, the border and the
    /// resize grips, which this app does not draw itself. Whether the drawn decorations are really gone can only
    /// be seen in a shown window (the self-check never shows this one), so this pin exists to keep the property
    /// that selects the behaviour from drifting back to the default.</summary>
    internal WindowDecorations WindowDecorationsForCheck => WindowDecorations;
    internal bool LinuxTransparencyForCheck => TransparencyLevelHint.Contains(WindowTransparencyLevel.Transparent) && Background == Brushes.Transparent;
    internal CornerRadius WindowSurfaceCornerRadiusForCheck => WindowSurface.CornerRadius;
    /// <summary>The window surface itself, for the checks whose coordinates are the shell's: rendering the surface
    /// gives that coordinate space directly, with the window's shadow margin outside the frame (the margin is not
    /// part of the surface) and the rounded corners, which are the surface's own geometry, still in view.</summary>
    internal Border WindowSurfaceForCheck => WindowSurface;
    /// <summary>The room the window surface keeps for the shadow the app draws itself. Exposed because a shadow is
    /// drawn *outside* the surface it belongs to, so no rendered frame of the surface can show whether the room for
    /// it is still there — while losing the room loses the shadow silently.</summary>
    internal Thickness WindowSurfaceMarginForCheck => WindowSurface.Margin;
    /// <summary>Whether the eight edge grips are sized to the shadow margin — which is what keeps a grip from ever
    /// covering a pixel the shell draws — and whether they are up at all. Read because "this window cannot be
    /// resized by dragging its edge" is a defect that neither a rendered frame nor any other assertion can see.</summary>
    internal bool WindowResizeGripsSizedForCheck => WindowResizeGrips.Children.Count == 8 && Math.Abs(ResizeWest.Width - WindowShadowMargin) <= 0.5 && Math.Abs(ResizeNorth.Height - WindowShadowMargin) <= 0.5 && Math.Abs(ResizeNorthWest.Width - (WindowShadowMargin * 2)) <= 0.5;
    internal bool WindowResizeGripsVisibleForCheck => WindowResizeGrips.IsVisible;
    internal double TitleBarHeightForCheck => TitleBar.Bounds.Height;
    internal double TitleBarHintForCheck => ExtendClientAreaTitleBarHeightHint;
    /// <summary>The platform's own caption-strip height. On Windows the three plates are centred in it, so a bar
    /// that disagrees makes the glyphs sit off the bar's centre line.</summary>
    internal double WindowDecorationTopForCheck => WindowDecorationMargin.Top;
    internal Thickness WindowDecorationMarginForCheck => WindowDecorationMargin;
    internal Thickness OffScreenMarginForCheck => OffScreenMargin;
    internal Rect WindowControlsBoundsForCheck => InClient(WindowControls);
    internal double MinimizeButtonHeightForCheck => MinimizeButton.Bounds.Height;
    internal string MaximizeTipForCheck => ToolTip.GetTip(MaximizeButton) as string ?? "";
    internal string CloseTipForCheck => ToolTip.GetTip(CloseButton) as string ?? "";
    internal bool MaximizeGlyphIsRestoreForCheck => ReferenceEquals(MaximizeGlyph.Data, _restoreGlyph);

    internal void SetWorkAreaFillForCheck(bool fillsWorkArea)
    {
        _fillsWorkArea = fillsWorkArea;
        SyncWindowStateChrome();
    }

    /// <summary>Whether the maximize plate is painted as pressed. The class is written by the Win32 hook rather
    /// than by a pseudo-class, so reading it back is the only way to tell "the press was seen" from "the press was
    /// swallowed": the two look identical in the object graph and differ only in these pixels.</summary>
    internal bool MaximizePressedForCheck => MaximizeButton.Classes.Contains("ncpressed");
    /// <summary>Whether a press is armed on the bar and has not yet become a drag. Exposed because the arming is
    /// the half of the protocol that cannot be driven to its end in a check (see
    /// <see cref = "HubDialogChrome.DragExceeded"/>), so its start is asserted instead.</summary>
    internal bool TitleBarDragArmedForCheck => _titleBarPress is not null;
    /// <summary>The client size remembered as the window's normal one, which is the size a restore-drag places
    /// against. Exposed because the one thing a check cannot assert about a drag is the drag: it ends in
    /// <see cref = "Window.BeginMoveDrag"/>. What it can prove is that the geometry the placement is computed from is
    /// the pre-maximize one and not the screen size the platform reports while maximized.</summary>
    internal Size NormalClientSizeForCheck => _normalGeometry?.ClientSize ?? default;
    /// <summary>The position that size belongs to — a restore goes back to the pair, not to a size alone.</summary>
    internal PixelPoint NormalPositionForCheck => _normalGeometry?.Position ?? default;
    /// <summary>Whether the shell currently holds the work area itself. Read because on WSLg this, not
    /// <c>WindowState</c>, is the state the caption plate toggles.</summary>
    internal bool FillsWorkAreaForCheck => _fillsWorkArea;
    /// <summary>The work-area rect the shell fills, learned on the first toggle and cached for every later one:
    /// origin from the screen, size from the window manager's own maximize.</summary>
    internal PixelRect FillRectForCheck => _fillRect ?? default;
    /// <summary>
    /// How many times the maximize toggle has been asked for. Read because the request is the half of the
    /// protocol a check can hold on a host that will not honour it: this check window is never shown, so on X11
    /// no window manager acts on the request and there is no maximized state to read back — while "the second
    /// click of the double-click reached the toggle" is true on every host and is the part that would break
    /// silently.
    /// </summary>
    internal int MaximizeToggleRequestsForCheck { get; private set; }

    /// <summary>Runs the plate's own flip, so a check can drive the fill and the un-fill through the same door a
    /// click goes through.</summary>
    internal void ToggleMaximizedForCheck() => ToggleMaximized();
    /// <summary>The half of the restore-drag that does not need a pointer: take the window out of the maximized
    /// state and put it where the formula says. The check drives this with the press point a bar press would have
    /// carried, and stops before the <see cref = "Window.BeginMoveDrag"/> that would otherwise not return.</summary>
    internal void RestoreForDragForCheck(Point press) => RestoreForDrag(press);
    /// <summary>Whether the bar's page name takes pointer input. It must not: the name is a label in the middle of
    /// the one press surface the window has, and a label that answers hit tests punches a dead hole in it — exactly
    /// where a reader would double-click to maximize. The conversation's title is the one exception, and the bar
    /// answers the press it takes through its own handler (see <c>InitializeTitleBar</c>), so it is not a hole.</summary>
    internal bool NavTitleHitTestableForCheck => NavTitle.IsHitTestVisible;
    /// <summary>Whether the conversation's title takes pointer input. It must: a tooltip only appears over an
    /// element the pointer can hit, and the tooltip is where the whole title stays readable.</summary>
    internal bool ConversationTitleHitTestableForCheck => ConversationTitle.IsHitTestVisible;

    /// <summary>The bar's two names, and the geometry of the zones they sit in. The nav zone's own box is here
    /// because its right edge *is* the sidebar's border — the hairline inside it is that border's pixel column, and
    /// a check reading only the hairline could be satisfied by a second line drawn somewhere near the first.
    /// </summary>
    internal Rect TitleNavZoneBoundsForCheck => InClient(TitleNavZone);
    internal string NavTitleText => NavTitle.Text ?? "";
    internal bool NavTitleVisibleForCheck => NavTitle.IsVisible;
    internal string ConversationTitleText => ConversationTitle.Text ?? "";
    internal string ConversationTitleTip => ToolTip.GetTip(ConversationTitle) as string ?? "";
    internal bool ConversationTitleVisibleForCheck => ConversationTitle.IsVisible;
    internal Rect ConversationTitleBoundsForCheck => InClient(ConversationTitle);
    internal Rect TitleZoneDividerBoundsForCheck => InClient(TitleZoneDivider);
    internal bool TitleZoneDividerVisibleForCheck => TitleZoneDivider.IsVisible;
    /// <summary>The pane's border continued into the bar. Measured the way the pane's own border is
    /// (<see cref="InspectorBorderBoundsForCheck"/>), so the two can be compared as numbers.</summary>
    internal Rect InspectorEdgeDividerBoundsForCheck => InClient(InspectorEdgeDivider);
    internal bool InspectorEdgeDividerVisibleForCheck => InspectorEdgeDivider.IsVisible;

    /// <summary>One of the title bar's two drawer plates, measured in the bar. Read by name so the check can
    /// compare the pair without this file exporting a member per button.</summary>
    internal (double Width, double Height) TitlebarTogglePlateForCheck(string name) => this.FindControl<Button>(name)is { } button ? (button.Bounds.Width, button.Bounds.Height) : default;
    /// <summary>Writes the window state the way the platform would, so the check can prove the maximise plate
    /// follows it. A check window is never actually maximised by the user, so the property is the only lever —
    /// and it is the same one the platform sets, which is what <c>SyncWindowStateChrome</c> listens to.</summary>
    internal void SetWindowStateForCheck(WindowState state) => WindowState = state;
    /// <summary>The bar carries no product name: the title block in the sidebar and the window's Title property
    /// (which the taskbar reads) are where "Axmol Hub" lives. A second copy up here is the chrome this change
    /// removed, so it is asserted rather than merely deleted.</summary>
    internal string TitleBarTextForCheck => string.Join("|", TitleBar.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").Where(text => text.Length > 0));

    /// <summary>Whether the ☰ drawer is the leading control of the bar: nothing interactive may sit left of it,
    /// or "the first position in the title bar" is no longer true.</summary>
    internal bool SidebarToggleIsLeftmostForCheck
    {
        get
        {
            var toggle = InClient(SidebarToggle).X;
            return !TitleBar.GetVisualDescendants().OfType<Button>().Any(button => button != SidebarToggle && button.IsVisible && InClient(button).X < toggle - 0.5);
        }
    }

    /// <summary>Whether the ▯ drawer ends where the window controls begin: its right edge must sit to the left of
    /// the caption plates' left edge, and close enough that it reads as "just before minimize".</summary>
    internal (double Gap, bool Before) InspectorToggleMeetsWindowControlsForCheck
    {
        get
        {
            var toggle = InClient(InspectorToggle);
            var plates = InClient(WindowControls);
            return (plates.X - toggle.Right, toggle.Right <= plates.X + 0.5);
        }
    }

    // ── Sidebar collapse, for the shell self-check ──
    internal bool SidebarExpandedForCheck => IsSidebarExpanded;
    internal double SidebarWidthForCheck => Sidebar.Width;
    internal bool SidebarClipsForCheck => Sidebar.ClipToBounds;
    internal bool ResizeGripVisibleForCheck => ResizeGrip.IsVisible;

    internal void ToggleSidebarForCheck() => SidebarToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>The title block (sidebar top), for the shell self-check. <c>BrandHeaderHasLogoForCheck</c> is what
    /// keeps the brand mark from quietly disappearing again: it reads the image's own source and geometry rather
    /// than trusting that the element exists, and it requires the image to sit *before* the title text, which is
    /// what "the logo precedes the name" means.</summary>
    internal bool BrandHeaderHasLogoForCheck
    {
        get
        {
            var logo = InClient(BrandLogo);
            var title = InClient(BrandTitle);
            return BrandLogo.Source is not null && BrandLogo.Stretch == Stretch.Uniform && BrandLogo.Bounds.Width > 1 && Math.Abs(BrandLogo.Bounds.Width - BrandLogo.Bounds.Height) <= 1 && logo.Right <= title.X + 0.5;
        }
    }

    /// <summary>Why <see cref = "BrandHeaderHasLogoForCheck"/> said no: the resolved source (or a marker for the
    /// state it is in), and the mark's own box. A bare bool cannot tell "the resource key is missing" from "the
    /// image has no size", and those are two different bugs.</summary>
    internal string BrandLogoReportForCheck
    {
        get
        {
            var source = BrandLogo.Source switch
            {
                null => "null",
                Avalonia.Media.Imaging.Bitmap bitmap => "bitmap " + bitmap.PixelSize,
                DrawingImage drawing => "drawing image (" + drawing.Drawing + ")",
                var other => other.GetType().Name,
            };
            var appHas = Application.Current?.TryGetResource("Hub.Image.Brand", null, out var value) == true ? value?.GetType().Name ?? "null" : "missing";
            return $"source={source}, app-resource={appHas}, " + $"size={BrandLogo.Bounds.Width:0.#}x{BrandLogo.Bounds.Height:0.#}, " + $"stretch={BrandLogo.Stretch}, " + $"left-of-title={InClient(BrandLogo).Right <= InClient(BrandTitle).X + 0.5}";
        }
    }

    internal int BrandHeaderLinesForCheck => new[]
    {
        BrandTitle.Text,
        BrandVersion.Text
    }.Count(text => text is { Length: > 0 });
    internal string BrandTitleTextForCheck => BrandHeader.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").FirstOrDefault(text => text == "Axmol Hub") ?? "";
    internal string BrandVersionTextForCheck => BrandVersion.Text ?? "";
    internal bool PreviewBadgeVisibleForCheck => PreviewBadge.IsVisible;
    internal string PreviewBadgeTextForCheck => PreviewBadge.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
    internal bool PreviewBadgeUsesThemeTokensForCheck => ThemeProbe.IsToken(PreviewBadge.Background, "Hub.PreviewBadgeBackground") && ThemeProbe.IsToken(PreviewBadge.BorderBrush, "Hub.PreviewBadgeBorder") && PreviewBadge.GetVisualDescendants().OfType<TextBlock>().Any(text => ThemeProbe.IsToken(text.Foreground, "Hub.PreviewBadgeForeground"));

    internal void SetPreviewBadgeVisibleForCheck(bool visible) => PreviewBadge.IsVisible = visible;
    /// <summary>The rail's bottom-left line (the default engine's version), for the shell self-check. It lives
    /// beside the brand hooks because it is the same footer the ⋯ menu sits in, and it is read by name so the
    /// check can prove the line follows <c>DefaultEnginePath</c> without reaching for the field.</summary>
    internal string RailEngineLineForCheck => BrandLine.Text ?? "";
    /// <summary>The OS attention channel, for the shell self-check: the pending notification target has to be
    /// armed on the same object the production notification path arms it on, or the check proves nothing.</summary>
    internal SystemAttentionService AttentionForCheck => _attention;

    /// <summary>Drives the foreground transition a notification click is made of, for the self-check: the window a
    /// check run shows never really leaves the foreground, so both the return that consumes a pending conversation
    /// and the start-up activation that must not consume one can only be asserted through this. The argument is
    /// the state being tested — whether this window had left the foreground before coming back.</summary>
    internal void ReturnToForegroundForCheck(bool hadLeftForeground)
    {
        _foregroundHasLeftWindow = hadLeftForeground;
        OnWindowActivated();
    }

    /// <summary>The strip's background-reply counter, read as the user sees it.</summary>
    internal bool ChatRunsVisibleForCheck => ChatRuns.IsVisible;
    internal string ChatRunsTextForCheck => ChatRuns.Text ?? "";
    /// <summary>The strip row and the log button's own plate: a 28-pixel row may not grow and the square glyph may
    /// not become a rectangle, and neither is visible in an assertion that only reads strings.</summary>
    internal double StatusStripHeightForCheck => StatusStrip.Bounds.Height;
    internal (double Width, double Height) LogTogglePlateForCheck => (LogToggle.Bounds.Width, LogToggle.Bounds.Height);
    /// <summary>The drawer's own caption. A task row points here, so the surface it lands on has to say what
    /// it holds rather than leaving the text at the top of a box.</summary>
    internal string LogTitleForCheck => LogTitleText.Text ?? "";
    internal double StatusWidthForCheck => Status.Bounds.Width;

    /// <summary>Forces the desktop half of <see cref = "AttentionNow"/> for the self-check; <c>null</c> hands the
    /// answer back to the platform. The suite must reset it — an override left behind would stop the notification
    /// gate from reading the desktop.</summary>
    internal void SetWindowVisibleForCheck(bool? visible) => _windowVisibleOverride = visible;
    /// <summary>Raises the gear's click path as a real click would — the self-check walks the same code the
    /// user's click does instead of calling <see cref = "OpenSettings"/> directly.</summary>
    internal void ClickSettingsGearForCheck() => SettingsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    /// <summary>Opens the bottom text block's popup the way the user's click does — the self-check walks
    /// the same code instead of calling <see cref = "BuildBottomMenu"/> directly.</summary>
    internal void ClickBottomMenuForCheck() => BottomMenuButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    /// <summary>The popup the bottom text block last attached, for the shell self-check.</summary>
    internal MenuFlyout? BottomMenuForCheck => BottomMenuFlyout;

    private Rect InWindow(Visual target) => target.TranslatePoint(new Point(0, 0), this)is { } origin ? new Rect(origin, target.Bounds.Size) : default;
    internal bool InspectorVisibleForCheck => InspectorColumn.IsVisible;
    internal double InspectorWidthForCheck => Inspector.Width;
    internal double InspectorPreferenceWidthForCheck => _preferences.InspectorWidth;
    internal bool InspectorPreferenceOpenForCheck => _preferences.InspectorOpen;
    /// <summary>The title bar's drawer button, as a check drives and reads it. Its tooltip is asserted rather than
    /// assumed because the button carries no text of its own: if that one string is not wired, the control has no
    /// name anywhere on the screen.</summary>
    internal bool InspectorToggleVisibleForCheck => InspectorToggle.IsVisible;
    internal bool InspectorGripVisibleForCheck => InspectorResizeGrip.IsVisible;
    internal string InspectorToggleTipForCheck => ToolTip.GetTip(InspectorToggle) as string ?? "";
    internal (double Width, double Height) InspectorTogglePlateForCheck => (InspectorToggle.Bounds.Width, InspectorToggle.Bounds.Height);
    internal Rect InspectorToggleBoundsForCheck => InClient(InspectorToggle);
    /// <summary>The arrow beside the drawer: shown only while there is a column to lift. Read by name and by rect,
    /// because "it is there" and "it is to the left of the drawer" are two different claims and the second one is
    /// what the layout is for.</summary>
    internal bool InspectorExpandToggleVisibleForCheck => InspectorExpandToggle.IsVisible;
    internal Rect InspectorExpandToggleBoundsForCheck => InClient(InspectorExpandToggle);

    /// <summary>The gap between the arrow and the drawer, and whether the arrow really is the left one of the pair.
    /// Mirrors <see cref = "InspectorToggleMeetsWindowControlsForCheck"/>: the two plates are the same size and sit
    /// one small margin apart, so the pair reads as one group of two rather than as two unrelated buttons.</summary>
    internal (double Gap, bool Before) InspectorExpandMeetsDrawerForCheck
    {
        get
        {
            var expand = InClient(InspectorExpandToggle);
            var drawer = InClient(InspectorToggle);
            return (drawer.X - expand.Right, expand.Right <= drawer.X + 0.5);
        }
    }

    /// <summary>Whether the drawer is painted with the given glyph. The rect says where the button is; this says
    /// which of its two faces is on it, which is the half of "the drawer became the way back" that a tooltip
    /// assertion cannot see. Compared by reference against the resource, so a glyph that merely looks similar —
    /// the two share a diagonal and differ by which end the arrowhead is on — does not pass.</summary>
    internal bool InspectorDrawerGlyphIsForCheck(string geometryKey) => Application.Current?.TryGetResource(geometryKey, null, out var value) == true && value is Geometry geometry && ReferenceEquals(InspectorToggleGlyph.Data, geometry);
    /// <summary>Presses the title bar's own button, so a cell drives the wiring instead of calling
    /// <see cref = "ToggleInspectorColumn"/> and proving nothing.</summary>
    internal void ClickInspectorToggleForCheck() => InspectorToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>The same for the arrow: the lift has to be reachable from the bar the reader actually presses,
    /// which is the whole point of moving it out of the pane.</summary>
    internal void ClickInspectorExpandToggleForCheck() => InspectorExpandToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>Writes the width the grip writes, so a check can start from a width that is not the default and
    /// tell "the column kept what you dragged it to" from "both numbers happen to be 360".</summary>
    internal void SetInspectorWidthForCheck(double width) => Inspector.Width = InspectorClamp(width);
    /// <summary>Drives the inspector's real grip handlers with a synthetic horizontal drag for the shell check.</summary>
    internal void DragInspectorForCheck(double horizontalDelta)
    {
        var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
        var origin = new Point(100, 100);
        InspectorResizeGrip.RaiseEvent(new PointerPressedEventArgs(
            InspectorResizeGrip, pointer, this, origin, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        InspectorResizeGrip.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, InspectorResizeGrip, pointer, this,
            origin + new Vector(horizontalDelta, 0), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        if (_inspectorGrabbing)
            InspectorResizeGrip.RaiseEvent(new PointerReleasedEventArgs(
                InspectorResizeGrip, pointer, this, origin + new Vector(horizontalDelta, 0), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
    }
    /// <summary>The pane itself, so a check can press the header's own arrow rather than the method behind it.</summary>
    internal InspectorPanel? InspectorPaneForCheck => _workspaceView?.SelectedContentForCheck as InspectorPanel;
    /// <summary>Which tab the <em>shell</em> last asked for. The pane's own visibility cannot tell the two routes
    /// apart — a jump that switched tabs inside the panel would leave exactly the same three booleans — so a cell
    /// that means "the ask went out to the shell and the shell landed it" has to read the shell's answer.</summary>
    internal string InspectorTabForCheck => _inspectorTab;
    internal bool InspectorExpandedForCheck => _inspectorExpanded;
    internal bool InspectorOverlayVisibleForCheck => InspectorExpandHost.IsVisible;
    internal Rect InspectorOverlayBoundsForCheck => InClient(InspectorExpandSurface);
    /// <summary>The slot the bar keeps the tabs in, and the strip inside it, both in the client root's space. Two
    /// rects because they answer two different claims: the slot is the column (so it says *which* column the tabs
    /// belong to), and the strip inside it is where the twelve-pixel inset lands, which is what "the tabs start
    /// where the pane starts" is actually measured against.</summary>
    internal Rect InspectorTabSlotBoundsForCheck => InClient(InspectorTabSlot);
    internal Rect InspectorTabsBoundsForCheck => InspectorTabSlot.Content is Visual strip ? InClient(strip) : default;
    internal bool InspectorTabSlotVisibleForCheck => InspectorTabSlot.IsVisible;
    /// <summary>Whether the slot holds the same object the pane exposes. The pane draws the tabs and the bar only
    /// says where they are, so a slot holding a second strip would look right and press nothing.</summary>
    internal bool InspectorTabsAreThePaneStripForCheck => InspectorPaneForCheck is { } pane && ReferenceEquals(InspectorTabSlot.Content, _workspaceView?.TabStrip);
    /// <summary>The pane's own border, whose left edge is the line the tabs are aligned to.</summary>
    internal Rect InspectorBorderBoundsForCheck => InClient(Inspector);
    /// <summary>The controls that stand aside while the pane is lifted, read by visibility because no geometry can
    /// see a control a sibling was painted over.</summary>
    internal bool SidebarToggleVisibleForCheck => SidebarToggle.IsVisible;
    internal Rect SidebarBoundsForCheck => InClient(Sidebar);
    internal Rect TitleBarBoundsForCheck => InClient(TitleBar);
    internal Rect PageBoundsForCheck => InClient(PageHost);
    internal Rect ClientBoundsForCheck => InClient(ClientRoot);
    internal string InspectorPlanMarkdownForCheck => InspectorPaneForCheck?.PlanMarkdownForCheck ?? "";

    /// <summary>Whether the overlay is the layer the design says it is. Nothing here sets <c>ZIndex</c>, so
    /// document order is the whole stacking contract — and an overlay in the wrong slot still covers the shell
    /// just as well, which is exactly why a bounds check cannot catch it and this can.</summary>
    internal bool InspectorOverlayOrderForCheck
    {
        get
        {
            var shell = ClientRoot.Children.IndexOf(ShellRoot);
            var overlay = ClientRoot.Children.IndexOf(InspectorExpandHost);
            var viewer = ClientRoot.Children.IndexOf(PictureViewerHost);
            return shell >= 0 && overlay > shell && overlay < viewer;
        }
    }

    /// <summary>Sends a key down the same tunnel route <see cref = "OnInspectorExpandKeyDown"/> listens on.</summary>
    internal void PressInspectorKeyForCheck(Key key) => OnInspectorExpandKeyDown(this, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None, Source = this, });
    // ── Check accessors ──
    internal bool PictureViewerOpenForCheck => PictureViewerHost.IsVisible;
    internal string PictureViewerCaptionForCheck => PictureViewerCaption.Text ?? "";
    internal int PictureViewerButtonCountForCheck => PictureViewerBar.Children.OfType<Button>().Count();
    internal int PictureViewerIndexForCheck => _viewerIndex;
    internal int PictureViewerCountForCheck => _viewerSet.Count;
    internal DataTransfer? PictureViewerCopyPayloadForCheck => BuildViewerCopyPayload();
    /// <summary>The on-screen picture's own size, or -1 when the frame shows nothing: a viewer that "opened" on
    /// a blank image would pass a visibility check and still be useless.</summary>
    internal (int Width, int Height) PictureViewerSizeForCheck => PictureViewerImage.Source is Bitmap bitmap ? (bitmap.PixelSize.Width, bitmap.PixelSize.Height) : (-1, -1);

    /// <summary>Clicks a caption-row button (0 = copy, 1 = close) through its own Click, so the wiring is what
    /// a check exercises.</summary>
    internal void ClickPictureViewerButtonForCheck(int index)
    {
        var buttons = PictureViewerBar.Children.OfType<Button>().ToList();
        if (index >= 0 && index < buttons.Count)
            buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Sends a key down the window's own tunnel, the same route <see cref = "OnViewerKeyDown"/> listens
    /// on, so a check drives the real handler rather than the method it happens to call.</summary>
    internal void PressViewerKeyForCheck(Key key) => OnViewerKeyDown(this, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None, Source = this, });
    /// <summary>A control's own <c>Bounds</c> is relative to <i>its parent</i>, and the surfaces a coverage check
    /// compares sit at four different depths of the tree — raw Bounds would be four unrelated coordinate systems,
    /// and a pane that covered nothing could still "contain" the sidebar in them. Every rect a check reads off
    /// this file is therefore brought into the client root's space first.</summary>
    private Rect InClient(Visual target) => target.TranslatePoint(new Point(0, 0), ClientRoot)is { } origin ? new Rect(origin, target.Bounds.Size) : default;
    internal HubWorkspace Workspace => _workspace;
    internal Control? CurrentPage => PageHost.Content as Control;

    private sealed partial class TaskRowView
    {
        /// <summary>The row's title line as drawn — what the work is and which stage it says it is in. A check
        /// on "the row speaks for its own process" has to read this, not the field behind it.</summary>
        public string TitleForCheck => _title.Text ?? "";
        /// <summary>The row's buttons, in the order they appear. A check presses these rather than calling
        /// the queue method behind them, so broken wiring stays detectable.</summary>
        public IReadOnlyList<Button> ActionButtons => [_cancel, _retry, _dismiss, _log];
        /// <summary>The words on the row's visible buttons. This is the affordance read directly, not a phase
        /// inferred from a percentage.</summary>
        public string ActionsForCheck => string.Join("+", ActionButtons.Where(button => button.IsVisible).Select(button => button.Content as string ?? "?"));
        /// <summary>What the row's tooltip currently says, whether or not anybody is hovering it.</summary>
        public string TipForCheck => _tip.Text ?? "";
    }

    /// <summary>The conversation sidebar section, for the shell self-check.</summary>
    internal ChatSidebar ChatSidebarSection => _chatSidebar!;
    /// <summary>The settings gear's tooltip, for the shell self-check: the button is icon-only, so the tip is
    /// the only place its name is spelled out.</summary>
    internal string SettingsLabel => ToolTip.GetTip(SettingsButton) as string ?? "";
    /// <summary>The bottom text block's tooltip, for the shell self-check: the block's visible text names
    /// the machine, so the tip is where "this opens settings and appearance" is spelled out.</summary>
    internal string BottomMenuLabel => ToolTip.GetTip(BottomMenuButton) as string ?? "";
}
