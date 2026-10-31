using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using AvaloniaEdit;
using Markdown.Avalonia;

namespace AxmolHub;

public partial class ChatPanel : UserControl
{
    // Virtual rows use their final Markdown shape from the first measure. A history-dependent plain/Markdown
    // boundary makes variable-height virtualization oscillate when a measured row is recycled across that boundary.
    private const int EagerMarkdownLimit = int.MaxValue;
    private readonly Button ForkNotice = new() { Classes = { "fork-notice" }, IsVisible = false,
        MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel NoticeHost = new();
    private bool _updatingReasoningControls;

    /// <summary>How many plain rows one promotion pass may upgrade. A scroll arrives as a burst and a pass that
    /// swallowed the whole burst would stall the very frame it exists to keep smooth, so the rest wait for the
    /// next pass — which is queued from the one that filled its quota.</summary>
    private const int MarkdownPromotionsPerPass = 3;

    /// <summary>How many transcripts that were left behind keep their rows. Two, because coming back is a single
    /// step either way (the session just left, and the one before it) — a longer chain would keep Markdown
    /// viewers and their editors alive for sessions nobody is going back to.</summary>
    private const int ParkedTranscriptDepth = 2;

    /// <summary>The class a row wears while its Markdown is still held back. It is also what makes such a row
    /// findable without a side table, so a check can count what a session opened with from outside.</summary>
    private const string PlainTurnClass = "plain-turn";

    /// <summary>How many rows a transcript may park when it is left. A row holds its whole Markdown document, its
    /// editors and its decoded pictures, so a session somebody expanded all the way to the top is a session whose
    /// rows are all there — past this, drawing them again is cheaper than keeping them.</summary>
    private const int ParkedRowLimit = 200;

    /// <summary>How close to the bottom counts as "the user is at the bottom". A few pixels of slack stop
    /// sub-pixel scroll rounding from flipping the state on every scroll event.</summary>
    private const double StickEpsilon = 6;
    private readonly ChatWorkspace _chat;
    private readonly List<ContextAttachment> _contextAttachments = [];
    private string? _replyToId;

    /// <summary>
    /// The pictures this composer is holding, in the order they were added, still in memory. A draft that is
    /// abandoned leaves nothing on disk, and the conversation the pictures would be stored into may not exist
    /// yet — Hub creates it when the message is sent, which is also when these bytes get a file name.
    /// </summary>
    private readonly List<PendingPicture> _pendingPictures = [];

    /// <summary>The shell's window-level picture viewer, handed over as a delegate the same way the inspector
    /// is: the viewer covers the whole window, so it cannot live in this page, but the sets it pages through
    /// are this page's business (the transcript's pictures, or this composer's draft).</summary>
    internal Action<IReadOnlyList<PictureRef>, int>? ShowPictureViewer { get; set; }
    internal Action<string>? StatusMessage { get; set; }

    /// <summary>
    /// What this composer has already sent, oldest first, for the Up arrow to walk back through — along with the
    /// steer that a running reply swallowed. Panel-wide rather than per conversation on purpose: Hub creates the
    /// conversation at the moment the first message is sent, so a per-conversation history would lose exactly the
    /// draft a person reaches Up for.
    /// </summary>
    private readonly List<string> _sentTexts = [];

    private int _recallIndex;
    private bool _recalling;
    private const int MaxRecalledTexts = 20;

    private bool _stickToBottom = true;
    // True while a hand holds the scrollbar's thumb: the window in which automatic scrolling and streamed
    // re-measuring both stand down. Wired in the constructor off Thumb.DragStarted/DragCompleted; see ScrollToEnd
    // for what it guards and RefreshLive for what it holds back.
    private bool _thumbDragging;
    private Task? _contextCompressionTask;

    /// <summary>
    /// The bubble showing the reply arriving in the session on screen, or null while none is. Everything it
    /// displays is read off the run, so leaving and coming back rebuilds it from what actually arrived — the text,
    /// the tools it has been through, and the time it has been taking — instead of from a copy this view kept,
    /// which is also what lets a session keep streaming while it is hidden.
    /// </summary>
    private LiveBubble? _live;
    // How often the speed label is allowed to re-render. The rate is a session average held on the conversation,
    // so it is always on screen once measured; this only stops the digits being rewritten every dispatcher tick.
    private long _speedDrawnAt;
    private static readonly long SpeedRefreshTicks = System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The build count of the bubble that was last on screen, kept because the bubble itself is
    /// discarded the moment the answer lands — and the number is only interesting once the stream is over.</summary>
    private int _lastLiveBuilds;

    /// <summary>Set when a live repaint was skipped because the composer was being typed into on Linux; the next
    /// repaint or focus loss clears it. Keeps the message column from re-arranging — and the in-progress ibus
    /// composition from jumping — while the user is composing a sentence during a streamed reply.</summary>
    private bool _liveRepaintPending;

    /// <summary>Where an open IME composition cannot survive a sibling re-arrange (Avalonia's X11 backend owns the
    /// preedit in the control's own text presenter), so the streamed reply must not repaint the message column
    /// while a draft is being composed. Gated on non-empty text, not focus alone: the composer keeps focus after
    /// a send, and an empty box means no composition is open — so a just-sent reply must still stream normally.
    /// Windows/macOS hand composition to the OS IME and need no such deferral.</summary>
    private bool DeferringLiveRepaint =>
        OperatingSystem.IsLinux() && InputBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(InputBox.Text);

    /// <summary>One attached run's worth of chrome: the row, the pieces of it that change while text arrives, and
    /// the timer that animates them. Created on attach, thrown away on detach; the run outlives both and carries
    /// every fact the chrome displays, so nothing here survives a detach and nothing here needs to.</summary>
    private sealed class LiveBubble
    {
        public required ConversationRun Run { get; init; }
        public required Control Row { get; init; }
        /// <summary>The arriving answer, laid down as Markdown the whole way: finished blocks stacked above the
        /// one still being typed. There is no plain-text copy of it any more — showing the source while the model
        /// spoke was the thing this replaced.</summary>
        public required StackPanel Prose { get; init; }

        /// <summary>How much of <see cref="ConversationRun.LiveText"/> is already a finished block, the source
        /// the open block was last rendered from, and when that happened. Together they let a frame that changed
        /// nothing inside the current block do no building at all.</summary>
        public int Settled { get; set; }
        public string SettledSource { get; set; } = "";
        public string OpenBlockSource { get; set; } = "";

        /// <summary>How many documents this bubble has built. Not a diagnostic curiosity: rebuilding the whole
        /// reply on every arriving chunk is the one way this path could be silently as expensive as the plain
        /// text block it replaced, and a count is the only part of it a check can assert on without a stopwatch.</summary>
        public int Builds { get; set; }
        public long OpenBlockPaintedAt { get; set; }

        /// <summary>The text the bubble last drew, kept only so a deferred frame can tell whether anything
        /// arrived while the repaint was held back.</summary>
        public string PaintedText { get; set; } = "";

        /// <summary>The thinking, as the very same row the stored turn will use. Drawn while reasoning is the only
        /// thing arriving and folded when the answer starts — never hidden, because a reader who watched forty
        /// seconds of it did not ask for it to vanish the moment the result turned up.</summary>
        public required ThoughtRowView Thought { get; init; }

        /// <summary>Whether the thinking body is following the newest line. Its own copy of the transcript's
        /// stick-to-bottom rule, because a box that always follows is a box nobody can read the top of.</summary>
        public bool ThoughtStick { get; set; } = true;

        public required TextBlock Status { get; init; }
        public required TextBlock Elapsed { get; init; }
        public required ActivityGlyph Glyph { get; init; }
        public DispatcherTimer? Timer { get; set; }
        public int Frame { get; set; }

        /// <summary>Whether a tool owns the run right now. Seeded off the run when a switch rebuilds the bubble
        /// and moved by <see cref="ChatWorkspace.ToolActivityChanged"/>, the only place the view learns that a
        /// call started or came back; it is what tells the two waiting shapes apart.</summary>
        public bool ToolRunning { get; set; }

        /// <summary>Whether this segment has produced any text yet — the status line says "preparing" until it
        /// has, and a steer starts a new segment, so the flag has to be able to go back.</summary>
        public bool ShowedText { get; set; }
    }

    /// <summary>The conversation whose virtual display items currently own the reading surface.</summary>
    private string? _renderedConversationId;

    /// <summary>The rows that were drawn as plain text although their turn holds Markdown, waiting for a reader
    /// to scroll them into reach. Panel-wide rather than per control for the same reason the fold memory is: a
    /// rebuild can put a row somewhere else, and the record has to survive that or follow it.</summary>
    private List<PendingMarkdownRow> _pendingMarkdownRows = [];

    // Weak ownership follows the registry's live Conversation objects. Switching away does not reset identities;
    // deleting or evicting a conversation does not leave a second strong copy of its history in this panel.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Conversation, TranscriptProjectionState>
        _projectionStates = new();
    private TranscriptProjectionState? _projection;

    private string RowKey(int sourceIndex, string facet)
        => facet + ":" + _projection!.KeyAt(sourceIndex).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The current operation stays alive until its own Loaded observation, even if another session
    /// replaces it in this panel. Callback closures own their measurements, never the next session's fields.</summary>
    private SessionOpenMeasurement? _panelOpen;
    private bool _openReusedRows;

    /// <summary>A row drawn as plain text although its turn holds Markdown, with everything needed to draw it
    /// properly in place: the panel it sits in and the control itself, because a transcript that grew a group
    /// since it was painted did not move this row's parent even though its position in the flow changed.</summary>
    private sealed class PendingMarkdownRow
    {
        public required Panel Parent { get; init; }
        public required Control Row { get; init; }
        public required string ConversationId { get; init; }
        public required long TurnKey { get; init; }
        public string? ToolName { get; init; }
        public bool IsLast { get; init; }
        public bool InGroup { get; init; }
    }

    /// <summary>Which folds the person opened, held as data rather than as a control's state. Extending a group
    /// in place keeps its head alive, but not every rebuild can be avoided: a decision rewrites what an
    /// already-painted turn looks like, and a result approved late is filed beside its call rather than at the
    /// end. A group is keyed by the turn it starts on and a row by the call it reports — both survive a teardown,
    /// so the fold comes back where the person left it. Nothing is in these sets on its own: a fold nobody opened
    /// stays collapsed, which is what the charter asks of secondary detail.</summary>
    private readonly Dictionary<string, HashSet<string>> _expandedGroups = new();
    private readonly Dictionary<string, HashSet<string>> _expandedRows = new();

    private static bool IsFoldOpen(Dictionary<string, HashSet<string>> memory, string conversationId, string key)
        => memory.TryGetValue(conversationId, out var open) && open.Contains(key);

    private static void RememberFold(Dictionary<string, HashSet<string>> memory, string conversationId,
        string key, bool opened)
    {
        if (!memory.TryGetValue(conversationId, out var set))
        {
            if (!opened) return;
            memory[conversationId] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        if (opened) set.Add(key);
        else set.Remove(key);
    }

    /// <summary>Drops the fold choices — and the parked rows — of sessions that no longer exist. Deleting a
    /// conversation raises <see cref="ChatWorkspace.Changed"/>, so this runs on the very repaint that follows it
    /// and none of them outlives the conversations they name.</summary>
    private void PruneFoldMemory()
    {
        if (_expandedGroups.Count == 0 && _expandedRows.Count == 0) return;
        var alive = _chat!.Conversations.Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);
        if (_chat.ActiveConversation is { } current) alive.Add(current.Id);
        foreach (var gone in _expandedGroups.Keys.Where(id => !alive.Contains(id)).ToArray()) _expandedGroups.Remove(gone);
        foreach (var gone in _expandedRows.Keys.Where(id => !alive.Contains(id)).ToArray()) _expandedRows.Remove(gone);
    }

    internal event Action? ConversationStateChanged;

    private bool _markdownPassQueued;
    private bool _goalDraftSelected;
    private string? _goalDraftConversationId;
    private bool _startingGoalConversationForSend;

    public ChatPanel()
    {
        _chat = null!;
        InitializeComponent();
    }

    public ChatPanel(ChatWorkspace chat)
    {
        _chat = chat;
        InitializeComponent();

        _chat.Changed += Reload;
        ComposerStatusRow.SizeChanged += (_, _) => RefreshComposerStatusDensity();
        // A command that went quiet raises its own question, and it is raised from inside the tool body — nowhere on
        // the token stream, so nothing else would ever tell this panel the card exists. The conversation id rides
        // along: a question in a session that is not on screen must not repaint the one that is.
        _chat.StallChanged += conversationId =>
        {
            if (conversationId != _chat?.ActiveConversation?.Id) return;
            RefreshDecisionHost();
        };
        // A repository read lands whenever git finishes, which is nowhere on the token stream. The pane decides
        // whether it still wants the answer; nothing here polls, and nothing here runs a git.
        _chat.RepositoryChanged += OnRepositoryChanged;

        // Tool activity already arrives on the UI thread, and it carries the session it happened in: a reply
        // running out of sight must not rewrite the status line of the one on screen.
        _chat.ToolActivityChanged += (conversationId, name, completed) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = ToolActivityText(name, completed);
            // Which of the two waiting shapes to wear is the whole thing this event decides; how many calls have
            // come back is counted in the group row the transcript already draws, not on the glyph.
            live.ToolRunning = !completed;
            RefreshGlyphPhase(live);
        };
        _chat.RunTextChanged += conversationId =>
        {
            if (_live is { } live && live.Run.ConversationId == conversationId) RefreshLive(live);
            // Paint the speed on the same dispatcher frame as the incoming delta. A burst can finish before
            // the bubble's 350ms timer ticks even once, leaving the status stuck at "waiting" otherwise.
            if (_chat.ActiveConversation?.Id == conversationId) RenderComposerMetrics();
        };
        // A command that printed another line. Its card is not a turn and not a token: the transcript grows nothing
        // until the command ends, so what changes is the text inside one row that is already on screen. Refreshed
        // from the run rather than rebuilt, and guarded by the session for the reason the two events above are — a
        // build running in a window nobody is looking at must not paint into this one.
        _chat.ToolOutputChanged += conversationId =>
        {
            if (_runningCommand is not { } card || card.ConversationId != conversationId) return;
            RefreshRunningCommand(card, _chat.RunFor(conversationId));
        };
        // A search the endpoint ran for itself: no card, no call row, nothing the person can act on — so the one
        // status line the bubble already has is where the fact belongs. Same session guard as the tool row above,
        // for the same reason: a reply running out of sight must not rewrite the line of the one on screen.
        _chat.ServerSearchChanged += (conversationId, notice) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = SearchActivityText(notice);
        };
        // A retry wait is the provider saying "not yet" and the pipeline obeying, which is work rather than a
        // stall — so it takes the same one line the tool and the search use, and gives it back when the attempt
        // goes out. Out of sight it is still the run that holds the fact, for the bubble rebuilt later.
        _chat.RetryWaitChanged += (conversationId, waiting) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = waiting ? RetryActivityText(live.Run.RetryWaitNotice())
                : live.ShowedText ? HubStrings.Get("ChatGenerating") : HubStrings.Get("ChatPreparing");
        };
        // A compaction is the one stretch of a run where no token arrives and no tool runs, so the line that exists
        // to say the turn is alive would go on claiming tokens are coming. Both brackets write the same line the
        // retry wait uses, and give it back the same way. The strip below the flow is repainted as well, because
        // that is the surface the button path has: compressing a session nobody has sent to leaves no bubble to
        // correct, and RenderNotice is where the two surfaces take turns.
        _chat.CompactionChanged += (conversationId, compacting) =>
        {
            if (_live is { } live && live.Run.ConversationId == conversationId)
            {
                live.Status.Text = compacting ? HubStrings.Get("ChatCompactingContext")
                    : live.ShowedText ? HubStrings.Get("ChatGenerating") : HubStrings.Get("ChatPreparing");
            }
            RenderNotice();
        };
        _chat.RunsChanged += conversationId =>
        {
            if (conversationId == _chat.ViewedConversationId)
            {
                RenderMessages();
                RefreshTurnChangesPill();
            }
        };
        _chat.RunCompleted += (conversationId, outcome) =>
        {
            var viewed = conversationId == _chat.ViewedConversationId;
            if (viewed)
            {
                RenderMessages();
                RefreshTurnChangesPill();
            }
            // Filed for whichever session ended, ahead of the guard below: a run that finished while the person
            // was reading another conversation still owes them the reason when they come back.
            AppendRunNotice(conversationId, outcome);
            if (!viewed) return;
            UpdateSendState();
            ConversationStateChanged?.Invoke();
            // The repository pill is a live workspace summary, not a transcript tally. A completed run may have
            // changed files through tools, a build, formatting, or a checkout, so refresh the snapshot even when
            // the repository tab is closed; RepositoryChanged updates the pill without repainting transcript rows.
            if (_chat.ActiveConversation?.Id == conversationId)
                RequestRepositoryRead();
        };
        TurnChangesPill.Click += (_, _) => OpenTurnReview(_chat.ActiveConversation);
        ModelPicker.Click += (_, _) => ShowModelSettings();
        ModelSettingsPopup.PlacementTarget = ModelPicker;
        ModelSettingsPopup.Placement = PlacementMode.TopEdgeAlignedRight;
        AutoReasoningSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_updatingReasoningControls) return;
            _chat.SetRouting(_chat.ActiveConversation?.Id ?? "", AutoReasoningSwitch.IsChecked == true
                ? ChatRouting.Auto : ChatRouting.Manual);
            RefreshModelPicker();
            UpdateModelSettingsPopup();
        };
        ReasoningLevelSlider.ValueChanged += (_, _) =>
        {
            if (_updatingReasoningControls || AutoReasoningSwitch.IsChecked == true) return;
            var level = (int)Math.Round(ReasoningLevelSlider.Value);
            _chat.SelectReasoningLevel(level);
            UpdateModelSettingsPopup();
            RefreshModelPicker();
        };
        OpenModelMenuButton.Click += (_, _) =>
        {
            ModelSettingsPopup.IsOpen = false;
            ShowModelMenu();
        };
        ForkNotice.Click += (_, _) =>
        {
            if (ForkNotice.Tag is string sourceId) _chat.OpenConversation(sourceId);
        };
        ModeIndicatorButton.Click += (_, _) =>
        {
            if (GoalDraftSelectedForActiveConversation)
                ClearGoalDraftSelection();
            else if (_chat.ActiveMode is ChatModes.Ask or ChatModes.Plan)
                _chat.SelectMode(ChatModes.Agent);
            RefreshComposerChoices();
        };
        ContextButton.Click += (_, _) => ShowContextMenu();
        ContextPopup.PlacementTarget = ContextButton;
        ContextPopup.Placement = PlacementMode.TopEdgeAlignedRight;
        ContextProgressTrack.SizeChanged += (_, _) => UpdateContextProgressFill();
        CompressContextButton.Click += (_, _) => _contextCompressionTask = CompressContextAsync();
        ToolTip.SetTip(AddContextButton, HubStrings.Get("ChatAddContext"));
        AddContextButton.Click += (_, _) => ShowAddContextMenu();
        PermissionChip.Click += (_, _) => ShowPermissionMenu();
        WorkspaceChip.Click += (_, _) => ShowWorkspaceMenu();
        // The round button is the thing being pressed, and a press moves the keyboard to it. The reply is what
        // the person is waiting on, and the composer is where the next sentence goes, so focus comes back here.
        SendButton.Click += (_, _) =>
        {
            _ = SendAsync();
            InputBox.Focus();
        };
        InitializeVirtualTranscript();
        ScrollToBottomButton.Click += (_, _) => ScrollToEnd();
        MessageScroller.SizeChanged += (_, _) => UpdateMessageColumnWidth();
        MessageScroller.ScrollChanged += (_, change) =>
        {
            if (_mappingTranscriptBar) return;
            var wasAtBottom = MessageScroller.Offset.Y - change.OffsetDelta.Y
                + MessageScroller.Viewport.Height - change.ViewportDelta.Y
                >= MessageScroller.Extent.Height - change.ExtentDelta.Y - StickEpsilon;
            // Only a viewport change (a resize, the band rising) re-follows. An event that also moved the
            // offset is a deliberate reading move or a follow of its own, and snapping it back to the bottom
            // would overrule whoever asked for it.
            if (!_programmaticScroll && !_thumbDragging && Math.Abs(change.OffsetDelta.Y) < 0.01
                && Math.Abs(change.ViewportDelta.Y) > 0.1 && wasAtBottom) QueueVirtualFollow();
            // Stickiness is decided by gestures, never here: ScrollIntoView's BringIntoView can land a frame
            // after the call that asked for it, so an offset change is not proof that a hand is driving. The
            // wheel and thumb handlers clear it; the follow and the plate set it.
            UpdateMessageColumnWidth();
            UpdateScrollAffordance();
            // The transcript of a long session is largely plain until this runs, and a scroll is exactly the
            // news that some of it is now in front of someone.
            ScheduleMarkdownPromotion();
        };
        // A hand on the thumb owns the reading position. While it is held, no automatic scroll may move what it
        // holds and the streamed reply must not re-measure the column under it; the moment it lets go, the
        // held-back paint flushes. Both drag events bubble out of the ScrollBar's template (Thumb's drag events
        // are Bubble routed events), so the wiring survives a template re-apply and stays out of the template.
        TranscriptScrollBar.AddHandler(Thumb.DragStartedEvent,
            (_, change) => BeginTranscriptThumbDrag(change));
        TranscriptScrollBar.AddHandler(Thumb.DragCompletedEvent,
            (_, _) => EndTranscriptThumbDrag());
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                // Typing ends the walk: the next Up starts again from the newest thing sent, rather than
                // continuing to page through history under a sentence the person has begun writing themselves.
                if (!_recalling) _recallIndex = 0;
                CapturePlanDraftTarget();
                UpdateSendState();
                UpdateContextRing();
            }
        };
        InputBox.GotFocus += (_, _) => SetComposerFocus(true);
        InputBox.LostFocus += (_, _) => SetComposerFocus(false);
        // The clipboard verbs a text box is expected to have on right-click. Avalonia's TextBox has none of its
        // own, so without this the box a person writes in has no right-click at all — while the transcript beside
        // it does.
        SelectionMenu.AttachTextBox(InputBox);

        // KeyDown is registered for both tunnel and bubble. Subscribe on the tunnel: it runs before the
        // TextBox's own Enter handling, which (with AcceptsReturn=true) inserts a newline and marks the
        // bubble handled, silently swallowing a normal KeyDown subscriber.
        InputBox.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            // While an IME composition is open, Enter arrives as Key.ImeProcessed (confirm candidate), so it
            // falls through; only a bare Enter sends. Shift+Enter is left alone so it still inserts a newline.
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            // A steer waiting for its second tap owns Enter: the box is empty by then (the draft moved to the
            // strip), so without this the key would read as an ordinary send of nothing.
            if (SteerConfirmHost.IsVisible) CommitSteer();
            else _ = SendAsync();
        }, RoutingStrategies.Tunnel);

        // Ctrl+V is claimed here rather than left to the box, and the text paste is then asked for by name: a
        // clipboard holding a screenshot has to become a chip, and a clipboard holding text has to keep pasting
        // text. Reading the clipboard is async, so the alternative — peek and decide — would either drop the
        // picture or paste the file path a copied image also carries.
        InputBox.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.V) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            e.Handled = true;
            if (!await TryPastePictureAsync()) InputBox.Paste();
        }, RoutingStrategies.Tunnel);

        // Escape stops the reply this composer is watching, and Up walks back through what has already been sent.
        // Both are claimed only while the text box holds the keyboard: the in-place editor on a bubble keeps its
        // own Escape, and a caret in a multi-line draft keeps its own Up.
        InputBox.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape when ViewedRun is { IsStreaming: true } streaming:
                    e.Handled = true;
                    _chat.RequestStop(streaming.ConversationId);
                    break;
                case Key.Up when e.KeyModifiers == KeyModifiers.None && CanRecall():
                    e.Handled = true;
                    RecallText(backwards: true);
                    break;
                case Key.Down when _recallIndex > 0:
                    e.Handled = true;
                    RecallText(backwards: false);
                    break;
            }
        }, RoutingStrategies.Tunnel);

        // Dropping a capture onto the composer is how a picture arrives while the answer is still being written.
        // The frame is the target rather than the whole page, so a drop beside it does nothing at all.
        DragDrop.SetAllowDrop(ComposerFrame, true);
        DragDrop.AddDragOverHandler(ComposerFrame, (_, e) =>
        {
            // Both halves matter. `None` is what stops the shell from offering a drop this page cannot use, and
            // the ring is what tells the person *here* before they let go — a target that looks identical to the
            // rest of the window is a target nobody finds.
            var carries = DragCarriesFiles(e);
            e.DragEffects = carries ? DragDropEffects.Copy : DragDropEffects.None;
            ComposerFrame.Classes.Set("drag-over", carries);
        });
        DragDrop.AddDragLeaveHandler(ComposerFrame, (_, _) => ComposerFrame.Classes.Remove("drag-over"));
        DragDrop.AddDropHandler(ComposerFrame, OnComposerDrop);

        // Escape answers the thing on top, and the order is written in exactly one place per layer: the window
        // closes its picture viewer first, then puts an expanded inspector back in its column (both of those
        // handlers live in MainWindow, above this one), then a decision on the card above the composer is answered,
        // then a steer waiting for its second tap goes back to being a draft, and only then does Escape mean "stop
        // the reply". The question card's letter keys ride the same handler for the same reason. Tunnel, so this runs
        // before the composer's own key handling gets a turn.
        AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (TryChooseQuestionOptionByKey(e)) return;
            if (e.Key != Key.Escape) return;
            if (DecisionHost.IsVisible)
            {
                if (e.Source is TextBox box)
                {
                    // A caret in a text box outranks the card. The card sits above the draft rather than in front
                    // of it precisely so a decision cannot interrupt what is being written, and a key that answers
                    // the question while the person is mid-sentence is that interruption again. Only the card's own
                    // answer line turns Escape into "leave this line"; the composer's box falls through to the
                    // layers under this one, where Escape still stops the reply.
                    if (box.Name == "QuestionAnswerBox")
                    {
                        DecisionHost.Focus();
                        e.Handled = true;
                    }
                    return;
                }
                DismissDecisionByEscape();
                e.Handled = true;
                return;
            }
            if (SteerConfirmHost.IsVisible)
            {
                // Escape on a draft is the way back to editing it, never the way to destroy it.
                EditSteerDraft();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        Reload();
    }

    public void Reload()
    {
        if (_chat is null) return;
        var viewed = _chat.ViewedConversationId;
        if (viewed != _renderedConversationId && _replyToId is not null) ClearReplyTarget();
        if (_goalDraftSelected && !_startingGoalConversationForSend
            && _goalDraftConversationId != viewed)
            ClearGoalDraftSelection();
        var requested = _chat.CurrentSessionOpen;
        if (requested is { Completed: false } && requested.ConversationId == viewed)
            _panelOpen = requested;
        else if (viewed is not null && viewed != _renderedConversationId)
            _panelOpen = new SessionOpenMeasurement(viewed, "panel-switch");
        var measurement = _panelOpen is { Completed: false } pending && pending.ConversationId == viewed
            ? pending : null;
        if (measurement is not null) measurement.Reloads++;
        var reloadStarted = measurement?.ElapsedMs ?? 0;
        _openReusedRows = false;

        CapturePlanDraftTarget();
        InputBox.PlaceholderText = HubStrings.Get(ChatWorkspace.PendingPlanToken(_chat.ActiveConversation) is not null
            ? "ChatPlanFeedbackPlaceholder" : "InputPlaceholder");
        GreetingLabel.Text = HubStrings.Get("AssistantGreeting");
        GreetingSubtitle.Text = HubStrings.Get("AssistantGreetingSubtitle");
        RenderSuggestions();
        UpdateSendState();

        RefreshModelPicker();
        RefreshComposerChoices();
        UpdateForkNotice();
        var renderStarted = measurement?.ElapsedMs ?? 0;
        var previousMeasurement = SessionOpenMeasurement.Rendering;
        SessionOpenMeasurement.Rendering = measurement;
        try { RenderMessages(); }
        finally { SessionOpenMeasurement.Rendering = previousMeasurement; }
        if (measurement is not null)
        {
            measurement.RenderMs += measurement.ElapsedMs - renderStarted;
            measurement.Reused = _openReusedRows;
        }
        UpdateContextRing();
        // The two composer-anchored surfaces read the transcript, not the run registry, so a repaint is the one
        // place both can be brought in step with whatever just changed.
        RefreshDecisionHost();
        RefreshPlanTasksHost();
        RefreshSteerConfirm();
        RefreshTurnChangesPill();
        // The right-hand column is the third surface that reads the transcript rather than the run registry. It
        // repaints only when the transcript grew, so a reply streaming text costs no pane rebuild.
        RefreshInspectorIfTranscriptMoved();
        if (measurement is not null)
        {
            measurement.ReloadMs += measurement.ElapsedMs - reloadStarted;
            NoteSessionOpen(measurement);
        }
        // Rows that came up already in front of the reader are promoted without waiting for a scroll: a session
        // short enough to fit a screen is a session whose whole transcript can be upgraded at once.
        ScheduleMarkdownPromotion();
    }

    /// <summary>Observes dispatcher latency, not presentation. Snapshot counts synchronously so a rapid switch
    /// cannot attribute the next conversation's controls to this operation. Subsequent reloads update the same
    /// operation until this callback completes it; superseded opens are labeled, not mistaken for visible frames.</summary>
    private void NoteSessionOpen(SessionOpenMeasurement measurement)
    {
        measurement.TotalTurns = _chat.ActiveConversation?.Messages.Count ?? 0;
        if (measurement.ObservationQueued) return;
        measurement.ObservationQueued = true;
        var chat = _chat;
        Dispatcher.UIThread.Post(() =>
        {
            if (chat.ViewedConversationId == measurement.ConversationId)
            {
                measurement.PaintedTurns = RealizedItemCount;
                measurement.Rows = MessageRows.Count();
                measurement.ActivityRows = RealizedTranscriptRows
                    .SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Control>()
                    .Count(control => control.Classes.Contains("activity-row"));
                measurement.MarkdownDocuments = MarkdownDocumentCount;
                measurement.Editors = RealizedTranscriptRows
                    .SelectMany(row => new Visual[] { row }.Concat(row.GetVisualDescendants())).OfType<TextEditor>().Count();
                measurement.FlowChildren = RealizedTranscriptRows.Count;
                measurement.Reused = _openReusedRows;
            }
            var line = measurement.Complete(chat.ViewedConversationId == measurement.ConversationId);
            chat.LogProvider?.Invoke()?.Write(line);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The four starting points the empty state offers, in the order they read best.</summary>
    private static readonly string[] SuggestionKeys =
    [
        "AssistantSuggestReadCodebase",
        "AssistantSuggestFixBug",
        "AssistantSuggestWriteScript",
        "AssistantSuggestRefactor",
    ];

    /// <summary>
    /// Rebuilds the suggestion chips from the language table. Rebuilt rather than declared in the markup because
    /// a language switch must not leave English prompts under a Chinese greeting, and because a chip whose key
    /// vanished from the table disappears with it — the alternative is a pill reading "AssistantSuggestFixBug".
    /// </summary>
    private void RenderSuggestions()
    {
        SuggestionPanel.Children.Clear();
        foreach (var key in SuggestionKeys)
        {
            var prompt = HubStrings.Get(key);
            if (string.IsNullOrWhiteSpace(prompt) || string.Equals(prompt, key, StringComparison.Ordinal)) continue;

            var chip = new Button { Classes = { "suggestion-chip" }, Content = prompt, Tag = key };
            // Fill, do not send. A chip is an offer the person may want to narrow ("…this function"), and sending
            // it would start a run that spends their quota on a tap that looked like focusing a text box.
            chip.Click += (_, _) =>
            {
                InputBox.Text = prompt;
                InputBox.CaretIndex = prompt.Length;
                InputBox.Focus();
                UpdateSendState();
            };
            SuggestionPanel.Children.Add(chip);
        }
        SuggestionPanel.IsVisible = SuggestionPanel.Children.Count > 0;
    }

    /// <summary>
    /// Names the session the current one was forked from, above the transcript. Hidden for a normal session
    /// and for a fork whose source has since been deleted — a link to nothing would be worse than no link.
    /// </summary>
    private void UpdateForkNotice()
    {
        var source = _chat.ActiveConversation?.BranchSourceId is { Length: > 0 } sourceId
            ? _chat.Conversations.FirstOrDefault(summary => summary.Id == sourceId)
            : null;

        ForkNotice.IsVisible = source is not null;
        if (source is null) return;

        ForkNotice.Tag = source.Id;
        ForkNotice.Content = string.Format(
            System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get("ForkedFrom"), source.Title);
    }

    private void RefreshModelPicker()
    {
        var choices = _chat.AvailableChatModels.ToArray();
        var selected = _chat.SelectedChatModel is { } active
            ? choices.FirstOrDefault(choice =>
                choice.Provider.Id == active.Provider.Id
                && string.Equals(choice.ModelName, active.ModelName, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedModelLabel.Text = selected?.ModelName ?? HubStrings.Get("NoAvailableChatModels");
        var supportsReasoning = selected is not null
            && ModelCatalog.SupportsReasoningEffort(selected.Provider, selected.ModelName);
        var routed = supportsReasoning && _chat.ActiveRouting == ChatRouting.Auto;
        var route = routed && _chat.ActiveConversation is { } session
            ? _chat.LastRouteFor(session.Id)
            : null;
        var displayedEffort = _chat.ActiveReasoningEffort;
        SelectedReasoningLabel.IsVisible = supportsReasoning;
        SelectedReasoningLabel.Text = !supportsReasoning ? ""
            : routed ? $"{HubStrings.Get("ChatRoutingAutoChip")} · {ReasoningChoiceLabel(displayedEffort)}"
            : ReasoningChoiceLabel(displayedEffort);
        ToolTip.SetTip(SelectedReasoningLabel, routed
            ? route is { } chosen
                ? $"{chosen.Model} · {ReasoningEffortLabel(chosen.Effort)} — {chosen.Reason}"
                : HubStrings.Get("ChatRoutingAuto")
            : null);
        ModelPicker.IsEnabled = choices.Length > 0;
        ToolTip.SetTip(ModelPicker, selected is null
            ? HubStrings.Get("NoAvailableChatModels")
            : selected.Provider.Name + " · " + selected.ModelName);
        UpdateModelSettingsPopup();
    }

    private void ShowModelSettings()
    {
        UpdateModelSettingsPopup();
        ModelSettingsPopup.IsOpen = true;
    }

    private static string ReasoningEffortLabel(string effort)
        => HubStrings.Get(effort.ToLowerInvariant() switch
        {
            ChatReasoningEfforts.Low => "ChatEffortLow",
            ChatReasoningEfforts.Medium => "ChatEffortMedium",
            ChatReasoningEfforts.High => "ChatEffortHigh",
            ChatReasoningEfforts.XHigh => "ChatEffortXHigh",
            ChatReasoningEfforts.Max => "ChatEffortMax",
            ChatReasoningEfforts.Ultra => "ChatEffortUltra",
            _ => "ChatEffortHigh",
        });

    private void UpdateModelSettingsPopup()
    {
        var selected = _chat.SelectedChatModel;
        var hasModel = selected is not null;
        var supportsReasoning = hasModel && ModelCatalog.SupportsReasoningEffort(
            selected!.Provider, selected.ModelName);
        var efforts = selected is null
            ? Array.Empty<string>()
            : ModelCatalog.SelectableReasoningEfforts(selected.Provider, selected.ModelName).ToArray();
        var routingAuto = _chat.ActiveRouting == ChatRouting.Auto;
        _updatingReasoningControls = true;
        try
        {
            AutoReasoningSwitch.IsEnabled = supportsReasoning;
            AutoReasoningSwitch.IsChecked = routingAuto;
            ReasoningLevelSlider.IsEnabled = supportsReasoning && !routingAuto;
            ReasoningLevelSlider.IsVisible = supportsReasoning;
            ReasoningEffortLabels.IsVisible = supportsReasoning;
            ReasoningLevelSlider.Minimum = 0;
            ReasoningLevelSlider.Maximum = Math.Max(0, efforts.Length - 1);
            ReasoningLevelSlider.TickFrequency = 1;
            ReasoningLevelSlider.Value = supportsReasoning
                ? Math.Clamp(_chat.ActiveReasoningLevel, 0, efforts.Length - 1)
                : 0;
            ReasoningEffortLabels.Children.Clear();
            ReasoningEffortLabels.ColumnDefinitions.Clear();
            for (var index = 0; index < efforts.Length; index++)
            {
                ReasoningEffortLabels.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                var caption = new TextBlock
                {
                    Text = ReasoningEffortLabel(efforts[index]),
                    HorizontalAlignment = index == 0 ? HorizontalAlignment.Left
                        : index == efforts.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                    Classes = { "model-level-caption" },
                };
                Grid.SetColumn(caption, index);
                ReasoningEffortLabels.Children.Add(caption);
            }
        }
        finally
        {
            _updatingReasoningControls = false;
        }
        if (!supportsReasoning)
        {
            ReasoningLevelLabel.Text = HubStrings.Get("ChatReasoningUnavailable");
            ToolTip.SetTip(ReasoningLevelSlider, null);
        }
        else
        {
            var level = (int)Math.Round(ReasoningLevelSlider.Value);
            var actual = efforts[level];
            ReasoningLevelLabel.Text = HubStrings.Get("ChatReasoningLevelFormat").Replace("{0}",
                ReasoningEffortLabel(actual));
            ToolTip.SetTip(ReasoningLevelSlider, actual);
        }
        ModelSettingsSelectedModel.Text = selected is null
            ? HubStrings.Get("NoAvailableChatModels")
            : selected.Provider.Name + " · " + selected.ModelName;
        OpenModelMenuButton.IsEnabled = hasModel || _chat.AvailableChatModels.Count > 0;
        // Only a plan-backed model has something to say here, and it says exactly one true thing: whose quota pays
        // for the answers. The wording is the sentence the first sign-in dialog already uses, so the two surfaces
        // cannot drift apart.
        PlanUsageNote.IsVisible = selected?.Provider.IsOpenAiPlan == true;
        if (PlanUsageNote.IsVisible) PlanUsageNote.Text = HubStrings.Get("OpenAiPlanWelcome");
    }

    private bool GoalDraftSelectedForActiveConversation
        => _goalDraftSelected && _goalDraftConversationId == _chat.ActiveConversation?.Id;

    private void ClearGoalDraftSelection()
    {
        _goalDraftSelected = false;
        _goalDraftConversationId = null;
    }

    private void RefreshComposerChoices()
    {
        RefreshModelPicker();
        // A goal chip is a one-message draft choice, not the session's Agent mode. It is consumed only after a
        // successful send and follows the conversation currently being composed in; a session switch drops it.
        var mode = _chat.ActiveMode;
        var goalSelected = GoalDraftSelectedForActiveConversation && mode == ChatModes.Agent;
        ModeIndicatorButton.IsVisible = goalSelected || mode is ChatModes.Ask or ChatModes.Plan;
        var indicatorMode = goalSelected ? ChatModes.Agent : mode;
        ModeIndicatorIcon.Data = ThemeGeometry(ComposerModeIconKey(indicatorMode));
        ModeIndicatorLabel.Text = HubStrings.Get(goalSelected ? "ChatModeGoal" : mode switch
        {
            ChatModes.Ask => "ChatModeAsk",
            _ => "ChatModePlan",
        });
        ToolTip.SetTip(ModeIndicatorButton, goalSelected
            ? HubStrings.Get("ChatGoalDraftResetHint")
            : mode is ChatModes.Ask or ChatModes.Plan ? HubStrings.Get("ChatModeResetHint") : null);
        // The chip names the *effective* mode, not the one someone clicked: a session that merely follows a
        // permissive default is in it, and reading back the override would call that mode safe. With no session
        // on screen yet it names the mode the first one starts under, because before the first message is the
        // moment to decide how much to allow.
        var permission = _chat.ActiveApprovalMode;
        PermissionChipIcon.Data = ThemeGeometry(ToolApprovalIconKey(permission));
        PermissionChipLabel.Text = HubStrings.Get(ToolApprovalModeKey(permission));
        PermissionChip.Classes.Set("danger", permission == ToolApprovalModes.Full);
        ToolTip.SetTip(PermissionChip, HubStrings.Get(ToolApprovalModeHintKey(permission)));
        // The picker reads out the directory itself rather than a label for it: "which folder may this assistant
        // write to" has one answer and the folder name is it. When nothing is bound it says so, because that is
        // the state where every file tool refuses and the reason is still one click away.
        var root = _chat.ActiveWorkspaceRoot;
        // The directories this session holds beyond its workspace. They ride on the chip because a grant is a
        // permission a person gave and has to be able to take back: the tooltip names them, the menu revokes one.
        // Two lists, because a grant of a protected location is reading only, and a tooltip that read them as one
        // would promise writing the file tools refuse.
        var granted = _chat.ActiveGrantedRoots;
        var readOnly = _chat.ActiveReadOnlyRoots;
        // And the single files, which are their own line because their grant is one path wide: a tooltip that folded
        // them in with the directories would read as "listing and searching work there", which those cards never gave.
        var grantedFiles = _chat.ActiveGrantedFiles;
        WorkspaceChipLabel.Text = root is { Length: > 0 } ? FolderNameOf(root) : HubStrings.Get("ChatWorkspaceNone");
        var hint = root is { Length: > 0 }
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWorkspaceChipHintFormat"), root)
            : HubStrings.Get("ChatWorkspaceChipHint");
        ToolTip.SetTip(WorkspaceChip,
            hint
            + (granted.Count == 0
                ? ""
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatWorkspaceGrantedHintFormat"), granted.Count, string.Join(" · ", granted)))
            + (readOnly.Count == 0
                ? ""
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatWorkspaceReadOnlyHintFormat"), readOnly.Count, string.Join(" · ", readOnly)))
            + (grantedFiles.Count == 0
                ? ""
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatWorkspaceGrantedFilesHintFormat"), grantedFiles.Count,
                    string.Join(" · ", grantedFiles.Select(FileGrantLabel)))));
        // And it is on screen whenever a session is. The directory is what a session works in, and both things the
        // chip carries are needed for as long as the session lasts: reading out which folder the tools are confined
        // to, and taking back a granted directory. Picking another one is the person's own move at any point —
        // nothing about it is the model's — so there is no state where the control promises what it cannot do.
        WorkspaceChip.IsVisible = _chat.ActiveConversation is not null;
    }

    private static string FolderNameOf(string path)
        => System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>One granted file as the chip reads it out: its full path, and the read-only marker when the guard
    /// says the grant is a reading — the same derivation the tools ask at every resolution, so the tooltip cannot
    /// promise a write the next call refuses.</summary>
    private static string FileGrantLabel((string Path, bool ReadOnly) file)
        => file.Path + (file.ReadOnly ? HubStrings.Get("ChatWorkspaceFileReadOnlySuffix") : "");

    private void UpdateContextRing()
    {
        if (_chat is null) return;
        var usage = CurrentContextUsage();
        // The arc is a picture and a picture cannot draw past its own circle, so the arc clamps. The reading
        // beside it does not: a conversation that no longer fits is at more than 100%, and reporting that as a
        // full ring turns "this session has to be compressed" into the same look as a normal long one.
        ContextRing.Usage = Math.Clamp(usage.Fill, 0, 1);
        var percent = (int)Math.Round(usage.Fill * 100);
        ContextPopoverPercent.Text = percent.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
        ContextPercentLabel.Text = ContextPopoverPercent.Text;
        UpdateContextProgressFill(usage);
        RefreshContextCompressionButton();
        RenderCacheHit();
        RenderContextBreakdown(usage);
        RenderContextBurn();
        RenderComposerMetrics();
        // Two different claims, so two different sentences. "预计" over a number the model itself reported is a
        // lie, and the measured reading is the one thing on this screen a person can hold the gateway to.
        ToolTip.SetTip(ContextButton, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(usage.IsMeasured ? "ChatContextReportedFormat" : "ChatContextEstimateFormat"),
            usage.Used.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            usage.Room.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            percent));
    }

    private ContextUsage CurrentContextUsage() => _chat.EstimateContextUsage(InputBox.Text ?? "");

    private void RefreshComposerStatusDensity()
    {
        var width = ComposerStatusRow.Bounds.Width;
        if (width <= 0) return;
        var tight = width < 300;
        WorkspaceChipLabel.IsVisible = !tight;
        WorkspaceChipCaret.IsVisible = !tight;
        WorkspaceChip.MaxWidth = tight ? 28 : width < 420 ? 95 : 160;
        WorkspaceChipLabel.MaxWidth = width < 420 ? 55 : 120;
        ContextPercentLabel.IsVisible = !tight;
        // Column definitions and spacing are never rewritten here: this runs on a size change, and a grid template
        // that changes while the page is being measured re-measures the transcript above it — the composer's own
        // row must stay one fixed shape in every state.
        RenderComposerMetrics();
    }

    private void RenderComposerMetrics()
    {
        var rate = _chat.ActiveConversationSpeed();
        // One refresh a second, and the previous text stands between refreshes: the rate itself is a session
        // average, and a label that re-lays itself on every dispatcher tick reads as noise even when the number
        // is right. An empty slot is filled at once, so a new reading is never held back by the throttle.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (rate is > 0)
        {
            if (RequestSpeedLabel.Text is not { Length: > 0 } || now - _speedDrawnAt >= SpeedRefreshTicks)
            {
                RequestSpeedLabel.Text = FormatRequestSpeed(rate.Value);
                _speedDrawnAt = now;
            }
        }
        else
        {
            RequestSpeedLabel.Text = "";
            _speedDrawnAt = 0;
        }
        RequestSpeedLabel.IsVisible = RequestSpeedLabel.Text.Length > 0;
        ToolTip.SetTip(RequestSpeedLabel, RequestSpeedLabel.Text.Length > 0
            ? HubStrings.Get("ChatRequestSpeedHint") : null);

        var (send, session) = _chat.ActiveConversationBurn();
        // The stored session total excludes a run until it closes. A filed send is already inside that total.
        var live = ViewedRun is { BurnCommitted: false } ? send : null;
        var input = (session?.Input ?? 0) + (live?.Input ?? 0);
        var output = (session?.Output ?? 0) + (live?.Output ?? 0);
        var cached = (session?.CachedInput ?? 0) + (live?.CachedInput ?? 0);
        var hasReading = session is not null || live is not null;
        var showSpend = hasReading && (ComposerStatusRow.Bounds.Width == 0 || ComposerStatusRow.Bounds.Width >= 300);
        SessionBurnLabel.IsVisible = showSpend;
        if (!hasReading)
        {
            SessionBurnLabel.Text = "";
            return;
        }
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var total = input + output;
        var cacheText = input > 0 && cached > 0
            ? " · " + string.Format(culture, HubStrings.Get("ChatSessionCacheFormat"),
                Math.Clamp(cached * 100d / input, 0d, 100d).ToString("F1", culture))
            : "";
        SessionBurnLabel.Text = string.Format(culture, HubStrings.Get("ChatSessionTokensFormat"),
            CompactTokenCount(total, culture)) + cacheText;
        // Keep the unrounded reading available when the short label is hovered.
        ToolTip.SetTip(SessionBurnLabel, string.Format(culture, HubStrings.Get("ChatSessionTokensFormat"),
            total.ToString("N0", culture)) + cacheText);
    }

    private static string FormatRequestSpeed(double rate)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatRequestSpeedFormat"),
            rate.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));

    private static string CompactTokenCount(long tokens, System.Globalization.CultureInfo culture)
    {
        if (tokens < 1_000) return tokens.ToString("N0", culture);
        var divisor = tokens >= 1_000_000_000 ? 1_000_000_000d
            : tokens >= 1_000_000 ? 1_000_000d : 1_000d;
        var suffix = divisor == 1_000_000_000d ? "B" : divisor == 1_000_000d ? "M" : "K";
        var rounded = Math.Round(tokens / divisor, 2);
        if (rounded >= 1_000 && suffix != "B")
        {
            divisor *= 1_000;
            suffix = divisor == 1_000_000d ? "M" : "B";
            rounded = Math.Round(tokens / divisor, 2);
        }
        return rounded.ToString("0.##", culture) + suffix;
    }

    /// <summary>
    /// The session's input cache hit rate, drawn as the panel's first figure: bolder than every row under it, and
    /// never larger than the title's own percentage, so the card keeps one headline number and this one is a second
    /// reading rather than a second headline.
    ///
    /// <para>A ratio over the session's own bill, not a slice of the window: the rows below add up to the occupancy
    /// reading, and this one divides two numbers an endpoint reported. That is why it is a block of its own above
    /// them rather than one more row — a column that no longer adds up is worse than a figure nobody can find.</para>
    ///
    /// <para>The session total, not the last request's: one request's cache count is a sample, and the question this
    /// figure answers — did this conversation's prefix survive — is about the whole conversation. While a run is
    /// still streaming the session total does not include it yet, so the live reading stands in; it is the number
    /// the reader is watching climb.</para>
    ///
    /// <para>Hidden when nothing reported a cache split. A gateway that never sends one is not an endpoint with a
    /// 0.000% hit rate, and a headline is the last place to blur that.</para>
    /// </summary>
    private void RenderCacheHit()
    {
        if (_chat is null) return;
        var (send, session) = _chat.ActiveConversationBurn();
        var burned = session ?? send;
        if (burned is null || burned.Input <= 0 || burned.CachedInput <= 0)
        {
            // Cleared, not just hidden: a hidden block keeping the last session's reading would be one edit away
            // from being shown again on a gateway that never reported anything.
            CacheHitValue.Text = "";
            CacheHitDetail.Text = "";
            CacheHitBlock.IsVisible = false;
            return;
        }
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        // Same three decimals and same clamp as the burn line below, because the two must never disagree about the
        // same reading — a vendor that reports more cached than input is shown as 100.000%, not as 104%.
        var ratio = Math.Clamp(burned.CachedInput * 100d / burned.Input, 0d, 100d);
        CacheHitValue.Text = ratio.ToString("F3", culture) + "%";
        CacheHitDetail.Text = string.Format(culture, HubStrings.Get("ChatContextCacheHitDetailFormat"),
            burned.Input.ToString("N0", culture), burned.CachedInput.ToString("N0", culture));
        CacheHitBlock.IsVisible = true;
    }

    /// <summary>
    /// The measured spend, in the endpoint's own tokens: what this reply cost, and what the session has cost.
    ///
    /// <para>Its own block rather than more rows in the breakdown above, because those rows are one estimate that
    /// adds up to the occupancy figure while these are readings of money spent — dropping a measurement into the
    /// column would stop the column adding up, and the two answers legitimately differ (this session's rows can
    /// total more than the transcript the model actually read, or less).</para>
    ///
    /// <para>Nothing is drawn when the gateway never reported. A zero would read as "this answer cost nothing",
    /// which is a lie in the direction that makes people keep talking; the estimate-vs-measured distinction the
    /// button's own tooltip already carries is the only place that story needs telling.</para>
    /// </summary>
    private void RenderContextBurn()
    {
        if (_chat is null) return;
        var (send, session) = _chat.ActiveConversationBurn();
        var lines = new List<string>();
        if (send is { } thisSend)
            lines.Add(HubStrings.Get("ChatContextBurnSendLabel") + DescribeBurn(thisSend, countRequests: true));
        // The second line earns its row only by saying something the first did not: a session with one answer in
        // it does not need the same figure twice.
        if (session is { } total && !Equals(total, send))
            lines.Add(HubStrings.Get("ChatContextBurnSessionLabel") + DescribeBurn(total, countRequests: false));
        ContextBurnNote.Text = string.Join('\n', lines);
        ContextBurnNote.IsVisible = lines.Count > 0;
    }

    /// <summary>One line of the spend, in the order the bill arrives: what was read, what was written, of what the
    /// thinking was, how much of the read came from cache, what share of the read that was, and over how many
    /// requests.</summary>
    private static string DescribeBurn(ContextBurn burned, bool countRequests)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var parts = new List<string>
        {
            string.Format(culture, HubStrings.Get("ChatContextBurnInFormat"),
                burned.Input.ToString("N0", culture)),
        };
        if (burned.Output > 0)
        {
            // Reasoning is a clause inside the output, never a term beside it: the wire bills the chain of thought
            // as part of the reply, and two numbers side by side invite a reader to add them.
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnOutFormat"),
                burned.Output.ToString("N0", culture))
                + (burned.Reasoning > 0
                    ? string.Format(culture, HubStrings.Get("ChatContextBurnReasoningFormat"),
                        burned.Reasoning.ToString("N0", culture))
                    : ""));
        }
        // Left out entirely when the endpoint reports no cache split — silence is not a claim that nothing was
        // cached, and a 0 here would say it was.
        if (burned.CachedInput > 0)
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnCachedFormat"),
                burned.CachedInput.ToString("N0", culture)));
        // The share the two numbers above already carry, said out loud: cached over summed input is the figure a
        // reader would otherwise do in their head, and it is the one that says whether the prefix survived. Three
        // decimals because the interesting differences live below one percent. Nothing to divide when either side
        // is silent, and no ratio at all when the endpoint reported no cache split — for the same reason the
        // cached figure itself is left out.
        if (burned.CachedInput > 0 && burned.Input > 0)
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnCacheHitFormat"),
                Math.Clamp(burned.CachedInput * 100d / burned.Input, 0d, 100d).ToString("F3", culture)));
        // The request count is what makes a summed input readable against the window's single-request figure:
        // five requests over the same transcript is five charges, and the line says so.
        if (countRequests && burned.Requests > 0)
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnRequestsFormat"),
                burned.Requests.ToString(culture)));
        return string.Join(" · ", parts);
    }

    private void UpdateContextProgressFill(ContextUsage? usage = null)
    {
        if (ContextProgressTrack is null || ContextProgressFill is null) return;
        var reading = usage ?? CurrentContextUsage();
        // A bar has an end, so it fills up to it; the percentage next to it is allowed to say 118%.
        ContextProgressFill.Width = ContextProgressTrack.Bounds.Width
                                     * Math.Clamp((double)reading.Used / Math.Max(1, reading.Room), 0, 1);
    }

    /// <summary>
    /// The popover's breakdown: every heading that holds something, then the room that is left, then the share of
    /// the window held back for the answer. The rows and the ring's number come out of one reading, so the column
    /// adds up to what the ring claims — the identity that has been missing since the meter existed, and the one
    /// that says a category was left out of the total rather than quietly showing a smaller session.
    /// </summary>
    private void RenderContextBreakdown(ContextUsage usage)
    {
        ContextCategoryList.Children.Clear();
        foreach (var category in usage.Categories)
            AddContextCategoryRow(HubStrings.Get(ContextCategoryKey(category.Kind)), category.Tokens);
        AddContextCategoryRow(HubStrings.Get("ChatContextCatFree"), usage.Free);
        AddContextCategoryRow(HubStrings.Get("ChatContextCatReserve"), usage.OutputReserve);

        ContextPopoverSource.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatContextSourceFormat"),
            HubStrings.Get(ContextBudget.SourceLabelKey(usage.Source)),
            usage.EffectiveWindow.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

        // What the reading cannot say by itself: that its floor came from a response rather than a guess, that
        // turns this session still holds never went out, that the window was corrected down from what the
        // provider published, and that the conversation is past the room it has.
        var notes = new List<string>();
        if (usage.IsMeasured)
            notes.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextMeasuredNote"),
                usage.MeasuredInputTokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        if (usage.DriftPermille > ContextReport.UncalibratedPermille
            && usage.EffectiveWindow != usage.RawWindow)
            notes.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextDriftNote"),
                usage.RawWindow.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                usage.EffectiveWindow.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        if (usage.DroppedTurns > 0)
            notes.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextDroppedFormat"), usage.DroppedTurns));
        var overfull = usage.Free == 0 && usage.Used > usage.Room;
        if (overfull) notes.Add(HubStrings.Get("ChatContextOverfull"));
        ContextPopoverNote.Text = string.Join('\n', notes);
        ContextPopoverNote.IsVisible = notes.Count > 0;
        // Bound rather than set, and cleared when it no longer applies: a brush read by value would keep the
        // danger colour of whatever theme the popover happened to be built in.
        if (overfull)
            ContextPopoverNote.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
        else ContextPopoverNote.ClearValue(TextBlock.ForegroundProperty);
    }

    private void AddContextCategoryRow(string label, int tokens)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Classes = { "context-category-label" },
            Text = label,
        });
        var value = new TextBlock
        {
            Classes = { "context-category-value" },
            Text = tokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        ContextCategoryList.Children.Add(row);
    }

    /// <summary>The heading a cost row is filed under. The keys live with the rest of the wording, because a row
    /// named in code rather than in the string table is a row that stays Chinese when the app turns English.</summary>
    private static string ContextCategoryKey(ContextCostKind kind) => kind switch
    {
        ContextCostKind.SystemPrompt => "ChatContextCatSystem",
        ContextCostKind.ProjectCharter => "ChatContextCatCharter",
        ContextCostKind.Summary => "ChatContextCatSummary",
        ContextCostKind.MemoryIndex => "ChatContextCatMemory",
        ContextCostKind.ToolSchema => "ChatContextCatTools",
        ContextCostKind.Messages => "ChatContextCatMessages",
        ContextCostKind.Attachments => "ChatContextCatAttachments",
        _ => "ChatContextCatImages",
    };

    private void RefreshContextCompressionButton()
    {
        if (_chat is null) return;
        var conversation = _chat.ActiveConversation;
        var compressing = conversation is not null && _chat.IsCompressingContext(conversation.Id);
        CompressContextButton.IsEnabled = conversation is not null
                                          && !compressing
                                          && _chat.CanCompressContext(conversation.Id);
        CompressContextButton.Content = HubStrings.Get(
            compressing ? "ChatCompressingContext" : "ChatCompressContext");
    }

    private void ShowContextMenu()
    {
        UpdateContextRing();
        ContextPopup.IsOpen = true;
    }

    private async Task CompressContextAsync()
    {
        if (_chat.ActiveConversation is not { } conversation) return;
        CompressContextButton.IsEnabled = false;
        CompressContextButton.Content = HubStrings.Get("ChatCompressingContext");
        try
        {
            if (!await _chat.CompressContextAsync(conversation.Id))
                AppendNotice(HubStrings.Get("ChatContextCompressUnavailable"), danger: false);
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatContextCompressFailed") + ex.Message, danger: true);
        }
        finally
        {
            UpdateContextRing();
        }
    }

    private void ShowAddContextMenu() => ShowMenu(BuildComposerMenu(), AddContextButton);

    private void ShowPermissionMenu() => ShowMenu(BuildPermissionMenu(), PermissionChip);

    private void ShowWorkspaceMenu() => ShowMenu(BuildWorkspaceMenu(), WorkspaceChip);

    /// <summary>
    /// Pick a directory, clear it while one is bound, and take back a grant. The bound path is not repeated as a
    /// row: the chip reads it out and the tooltip gives it in full, so a third copy would be chrome saying a thing
    /// twice. A granted directory is repeated, because the row is the only way to revoke it — each item names the
    /// folder and carries the whole path as its tooltip, so two grants with the same folder name are still two
    /// different buttons.
    /// </summary>
    private MenuFlyout BuildWorkspaceMenu()
    {
        var menu = new MenuFlyout();
        var choose = new MenuItem { Header = HubStrings.Get("ChatWorkspaceMenuChoose") };
        choose.Click += async (_, _) =>
        {
            menu.Hide();
            await PickWorkspaceFolderAsync();
        };
        menu.Items.Add(choose);

        // The person's own door to a grant, above the model's: a task that needs a second directory must not be
        // parked on the assistant being willing to ask for one. Same ruling as the card's body, different words.
        var grant = new MenuItem { Header = HubStrings.Get("ChatWorkspaceMenuGrant") };
        grant.Click += async (_, _) =>
        {
            menu.Hide();
            await GrantFolderAsync();
        };
        menu.Items.Add(grant);

        if (_chat.ActiveWorkspaceRoot is { Length: > 0 })
        {
            var clear = new MenuItem { Header = HubStrings.Get("ChatWorkspaceMenuClear") };
            clear.Click += (_, _) =>
            {
                _chat.SelectWorkspaceRoot(null);
                menu.Hide();
            };
            menu.Items.Add(clear);
        }

        var granted = _chat.ActiveGrantedRoots;
        var readOnly = _chat.ActiveReadOnlyRoots;
        if (granted.Count > 0 || readOnly.Count > 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = HubStrings.Get("ChatWorkspaceGrantedMenu"),
                IsEnabled = false,
            });
            AddRevokeRows(granted, readOnly: false);
            // The read-only ones sit under the same heading and say what they are, because "revoke access to logs"
            // and "revoke access to logs, read-only" are answers to two different questions — and the row is still
            // the only way to take either back.
            AddRevokeRows(readOnly, readOnly: true);
        }

        // The single files: another heading, because the row here takes back one path and not a folder. Nothing
        // else on the menu can name such a grant, so without these the assistant holds a permission the person
        // can see in the tooltip and never take back.
        var files = _chat.ActiveGrantedFiles;
        if (files.Count > 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = HubStrings.Get("ChatWorkspaceGrantedFilesMenu"),
                IsEnabled = false,
            });
            foreach (var file in files)
            {
                var target = file.Path;
                var revokeFile = new MenuItem
                {
                    Header = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get(file.ReadOnly
                            ? "ChatWorkspaceFileRevokeReadOnlyFormat"
                            : "ChatWorkspaceFileRevokeFormat"),
                        System.IO.Path.GetFileName(target)),
                    Tag = target,
                };
                ToolTip.SetTip(revokeFile, target);
                revokeFile.Click += (_, _) =>
                {
                    menu.Hide();
                    if (_chat.ActiveConversation is not { } session) return;
                    if (_chat.RevokeGrantedFile(session.Id, target))
                        AppendNotice(HubStrings.Get("ChatWorkspaceFileRevoked"), danger: false);
                };
                menu.Items.Add(revokeFile);
            }
        }

        return menu;

        void AddRevokeRows(IReadOnlyList<string> directories, bool readOnly)
        {
            foreach (var directory in directories)
            {
                var target = directory;
                var revoke = new MenuItem
                {
                    Header = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get(readOnly ? "ChatWorkspaceRevokeReadOnlyFormat" : "ChatWorkspaceRevokeFormat"),
                        FolderNameOf(target)),
                    Tag = target,
                };
                ToolTip.SetTip(revoke, target);
                revoke.Click += (_, _) =>
                {
                    menu.Hide();
                    if (_chat.ActiveConversation is not { } session) return;
                    if (_chat.RevokeGrantedRoot(session.Id, target))
                        AppendNotice(HubStrings.Get("ChatWorkspaceRevoked"), danger: false);
                };
                menu.Items.Add(revoke);
            }
        }
    }

    /// <summary>Refusals come back as a verdict, not as text: the sentences in <c>WorkspacePaths</c> are written
    /// for the model, and the panel has its own words for the same fact.</summary>
    private async Task PickWorkspaceFolderAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatWorkspacePickTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFolderNotLocal"), danger: true);
            return;
        }

        switch (_chat.SelectWorkspaceRoot(result.Path))
        {
            case null:
                return;
            case WorkspacePathVerdict.ProtectedRoot:
                AppendNotice(HubStrings.Get("ChatWorkspaceProtected"), danger: true);
                return;
            // Named apart from the catch-all even though no picker produces it today: the panel and the gate must
            // answer one verdict with one sentence, and a case that fell through here would read as "that directory
            // cannot be used" for a fact that is about which directories this session holds.
            case WorkspacePathVerdict.OutsideRoots:
                AppendNotice(HubStrings.Get("ChatWorkspaceOutside"), danger: true);
                return;
            default:
                AppendNotice(HubStrings.Get("ChatWorkspaceRejected") + result.Path, danger: true);
                return;
        }
    }

    /// <summary>
    /// Grant a folder to this session from the workspace menu — the same ruling the approval card's body runs
    /// (<see cref="WorkspacePaths.ClassifyGrant"/>, through the workspace), so the two doors cannot disagree about
    /// what a directory has to be. Only the words are the panel's own: the sentences the tool hands back are
    /// written for the model.
    /// </summary>
    private async Task GrantFolderAsync()
    {
        if (_chat is null || _chat.ActiveConversation is not { } session) return;
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatWorkspaceGrantPickTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFolderNotLocal"), danger: true);
            return;
        }

        // The picker answers a folder or nothing; an empty path here would be a picker bug, and it is answered by
        // saying nothing rather than by a notice about a directory nobody named.
        if (result.Path is not { Length: > 0 } picked) return;

        var (verdict, detail) = await _chat.GrantDirectoryAsync(session.Id, picked);
        switch (verdict)
        {
            // The two that landed. A protected location lands read-only — the same answer the model's card gets —
            // and the notice has to say so, or the next refusal to write there reads as a bug rather than as the
            // grant it actually was.
            case GrantVerdict.Granted:
                AppendNotice(Notice("ChatWorkspaceGrantDoneFormat", picked), danger: false);
                return;
            case GrantVerdict.GrantedReadOnly:
                AppendNotice(Notice("ChatWorkspaceGrantReadOnlyFormat", picked), danger: true);
                return;
            // The four that answer "you already have it": said plainly and without the danger colour, because
            // nothing went wrong — the folder the person named is simply reachable by another spelling.
            case GrantVerdict.SameAsWorkspace:
                AppendNotice(Notice("ChatWorkspaceGrantSameFormat", picked), danger: false);
                return;
            case GrantVerdict.InsideWorkspace:
                AppendNotice(Notice("ChatWorkspaceGrantInsideFormat", picked), danger: false);
                return;
            case GrantVerdict.AlreadyGranted:
                AppendNotice(Notice("ChatWorkspaceGrantAlreadyFormat", picked), danger: false);
                return;
            case GrantVerdict.InsideGrant:
                AppendNotice(Notice("ChatWorkspaceGrantInsideGrantFormat", picked, detail), danger: false);
                return;
            case GrantVerdict.ContainsGrant:
                AppendNotice(Notice("ChatWorkspaceGrantContainsGrantFormat", detail), danger: true);
                return;
            case GrantVerdict.ContainsWorkspace:
                AppendNotice(HubStrings.Get("ChatWorkspaceGrantContainsWorkspace"), danger: true);
                return;
            case GrantVerdict.SessionGone:
                AppendNotice(HubStrings.Get("ChatWorkspaceGrantSessionGone"), danger: true);
                return;
            default:
                // NoPath, NotAbsolute, Unusable, Missing: the picker hands back an absolute folder that exists, so
                // this arm is what a directory deleted between the picker and the call looks like.
                AppendNotice(HubStrings.Get("ChatWorkspaceGrantUnusable"), danger: true);
                return;
        }
    }

    /// <summary>One localized sentence with its placeholders filled, in the reader's own culture.</summary>
    private static string Notice(string key, params object[] arguments)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get(key), arguments);

    /// <summary>
    /// The flyout this panel last opened. A popup is its own top-level, so a screenshot of one has to start
    /// from an item inside it — which is the only reason the instance is kept.
    /// </summary>
    private MenuFlyout? _openMenu;

    private void ShowMenu(MenuFlyout menu, Control anchor)
    {
        _openMenu = menu;
        menu.ShowAt(anchor);
    }

    /// <summary>
    /// The permission menu, hung off its own chip: a concise title and the three tiers, with the effective one
    /// ticked. The fuller risk explanation is a tooltip on each choice; keeping it out of the rows follows the
    /// Codex-style menu and leaves this as a quick choice rather than a wall of text. It works with no session on
    /// screen, where the pick lands on the one about to be started.
    /// </summary>
    private MenuFlyout BuildPermissionMenu()
    {
        var menu = new MenuFlyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            // Keep the requested side and alignment instead of flipping below the composer.
            PlacementConstraintAdjustment = Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.None,
        };
        menu.Items.Add(new MenuItem
        {
            Header = HubStrings.Get("ChatToolPermissionMenuTitle"),
            FontSize = 12,
            IsEnabled = false,
        });

        var effective = _chat.ActiveApprovalMode;
        foreach (var mode in new[] { ToolApprovalModes.Ask, ToolApprovalModes.Auto, ToolApprovalModes.Full })
        {
            var checkedItem = effective == mode;
            var item = new MenuItem
            {
                // The tick is drawn inside the row rather than by a toggle column: the column would sit to the
                // left of the tier's own icon and the two would read as one glyph doing a job twice. IsChecked
                // is still set, so the state stays on the item and not only in its pixels.
                Header = BuildPermissionHeader(mode, checkedItem),
                IsChecked = checkedItem,
            };
            ToolTip.SetTip(item, HubStrings.Get(ToolApprovalModeHintKey(mode)));
            item.Click += (_, _) =>
            {
                _chat.SelectApprovalMode(mode);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private static Control BuildPermissionHeader(string mode, bool checkedItem)
    {
        var danger = mode == ToolApprovalModes.Full;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Data = ThemeGeometry(ToolApprovalIconKey(mode)),
        };
        // Bound rather than read once: a brush taken by value here does not follow a theme switch.
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension(danger ? "Hub.WarningText" : "Hub.TextSecondary"));

        var title = new TextBlock
        {
            Text = HubStrings.Get(ToolApprovalModeKey(mode)),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (danger)
        {
            title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.WarningText"));
        }

        var tick = MenuRow.Tick(checkedItem);
        tick.Margin = new Thickness(12, 0, 0, 0);

        // One width for every row, so the ticks land in a column rather than wherever each title happens to end.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinWidth = 220 };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(tick, 2);
        header.Children.Add(icon);
        header.Children.Add(title);
        header.Children.Add(tick);
        return header;
    }

    internal static string ToolApprovalIconKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "Hub.Icon.PermissionAuto",
        ToolApprovalModes.Full => "Hub.Icon.PermissionFull",
        _ => "Hub.Icon.PermissionAsk",
    };

    internal static string ToolApprovalModeKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "ToolApprovalAuto",
        ToolApprovalModes.Full => "ToolApprovalFull",
        _ => "ToolApprovalAsk",
    };

    internal static string ToolApprovalModeHintKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "ToolApprovalAutoHint",
        ToolApprovalModes.Full => "ToolApprovalFullHint",
        _ => "ToolApprovalAskHint",
    };

    private MenuFlyout BuildComposerMenu()
    {
        var menu = new MenuFlyout();
        foreach (var (mode, key) in new[]
                 {
                     (ChatModes.Ask, "ChatModeAsk"),
                     (ChatModes.Plan, "ChatModePlan"),
                     (ChatModes.Agent, "ChatModeGoal"),
                 })
        {
            // Ask and Plan are durable session modes. Goal starts a durable thread objective on send while
            // retaining Agent as the tool-enabled working mode, so its check follows the pending draft selection.
            var ticks = mode == ChatModes.Agent
                ? GoalDraftSelectedForActiveConversation
                : _chat.ActiveMode == mode;
            var item = new MenuItem
            {
                Header = BuildComposerModeHeader(mode, key),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = ticks,
                Tag = mode,
            };
            item.Click += (_, _) =>
            {
                if (mode is ChatModes.Ask or ChatModes.Plan)
                {
                    ClearGoalDraftSelection();
                    _chat.SelectMode(ticks ? ChatModes.Agent : mode);
                }
                else if (ticks)
                {
                    ClearGoalDraftSelection();
                    _chat.SelectMode(ChatModes.Agent);
                }
                else
                {
                    _goalDraftSelected = true;
                    _goalDraftConversationId = _chat.ActiveConversation?.Id;
                    _chat.SelectMode(ChatModes.Agent);
                }
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        // Whether the model may stop and ask is neither of the two choices above it, and the row says so by where
        // it sits and what it is ticked by: 计划 and 目标 pick what this session can reach, this picks whether a
        // person is willing to be interrupted to settle something the repository cannot answer. It reads the
        // session on the way in and writes the session on the way out, so the tick cannot disagree with the
        // conversation it belongs to — which is the failure this menu's mode rows used to have.
        var addFolder = new MenuItem { Header = HubStrings.Get("ChatAddLocalFolder") };
        addFolder.Click += async (_, _) => await AddLocalFolderAsync();
        menu.Items.Add(addFolder);

        var addFile = new MenuItem { Header = HubStrings.Get("ChatAddLocalFile") };
        addFile.Click += async (_, _) => await AddLocalFileAsync();
        menu.Items.Add(addFile);

        var addPicture = new MenuItem { Header = HubStrings.Get("ChatAddImage") };
        addPicture.Click += async (_, _) => await AddLocalPictureAsync();
        menu.Items.Add(addPicture);

        var projects = _chat.HubSnapshotProvider?.Invoke()?.Projects ?? [];
        var addProject = new MenuItem
        {
            Header = HubStrings.Get("ChatAddHubProject"),
            IsEnabled = projects.Count > 0,
        };
        foreach (var project in projects)
        {
            var projectItem = new MenuItem { Header = project.Name, Tag = project };
            projectItem.Click += (_, _) =>
            {
                if (projectItem.Tag is ChatWorkspace.HubProjectSummary selected)
                    AddProjectAttachment(selected);
            };
            addProject.Items.Add(projectItem);
        }

        if (projects.Count == 0)
        {
            addProject.Items.Add(new MenuItem
            {
                Header = HubStrings.Get("ChatNoHubProjects"),
                IsEnabled = false,
            });
        }
        menu.Items.Add(addProject);
        return menu;
    }

    private static Control BuildComposerModeHeader(string mode, string labelKey)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(BuildModeIcon(mode, 14));
        header.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(labelKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }

    private static Avalonia.Controls.Shapes.Path BuildModeIcon(string mode, double size)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry(ComposerModeIconKey(mode)),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(
            Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension("Hub.TextSecondary"));
        return icon;
    }

    private static string ComposerModeIconKey(string mode) => mode switch
    {
        ChatModes.Ask => "Hub.Icon.ChatModeAsk",
        ChatModes.Plan => "Hub.Icon.ChatModePlan",
        _ => "Hub.Icon.ChatModeGoal",
    };

    private void ShowModelMenu()
    {
        BuildModelMenu().ShowAt(ModelPicker);
    }

    private MenuFlyout BuildModelMenu()
    {
        var menu = new MenuFlyout();
        var choices = _chat.AvailableChatModels;
        var selected = _chat.SelectedChatModel;
        foreach (var choice in choices)
        {
            var isSelected = selected is not null
                && choice.Provider.Id == selected.Provider.Id
                && string.Equals(choice.ModelName, selected.ModelName, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = choice.Provider.Name + " · " + choice.ModelName,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = isSelected,
                Tag = choice,
            };
            item.Click += (_, _) => _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);

            menu.Items.Add(item);
        }

        return menu;
    }

    private static readonly (string Value, string LabelKey)[] ReasoningChoices =
    [
        (ChatReasoningEfforts.Low, "ChatReasoningLow"),
        (ChatReasoningEfforts.Medium, "ChatReasoningMedium"),
        (ChatReasoningEfforts.High, "ChatReasoningHigh"),
        (ChatReasoningEfforts.XHigh, "ChatReasoningXHigh"),
        (ChatReasoningEfforts.Max, "ChatReasoningMax"),
        (ChatReasoningEfforts.Ultra, "ChatReasoningUltra"),
    ];

    private static string ReasoningChoiceLabel(string effort)
    {
        var labelKey = ReasoningChoices.FirstOrDefault(choice => choice.Value == effort).LabelKey;
        if (labelKey is null) return "";
        var label = HubStrings.Get(labelKey);
        var separator = label.IndexOfAny(['：', ':']);
        return separator >= 0 ? label[(separator + 1)..].Trim() : label;
    }

    private async Task AddLocalFolderAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatPickFolderTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFolderNotLocal"), danger: true);
            return;
        }

        var path = result.Path!;
        AddContextAttachment(new ContextAttachment(
            "folder",
            System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
            path));
    }

    /// <summary>One file as context — the same channel a folder goes down, for the case where the person knows
    /// exactly which file they mean. This is where a vector document enters: its markup is what a model can be
    /// handed, and the picture admission refuses it by its header.</summary>
    private async Task AddLocalFileAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFileAsync(owner, HubStrings.Get("ChatPickFileTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFileNotLocal"), danger: true);
            return;
        }

        AddFileAttachment(result.Path!);
    }

    /// <summary>Offers one file to the text channel, and says which rule refused it when one does: "that is not
    /// text" and "that is too large" are different fixes, and a notice that only says no gets the same file
    /// offered again. The answer is also what lets the drop path fall through to the picture refusal.</summary>
    private bool AddFileAttachment(string path)
    {
        var verdict = ChatContextReader.AdmitFile(path);
        if (verdict != ChatContextReader.FileAttachmentVerdict.Accepted)
        {
            AppendNotice(FileNotice(verdict), danger: true);
            return false;
        }

        AddContextAttachment(new ContextAttachment("file", System.IO.Path.GetFileName(path), path));
        return true;
    }

    private static string FileNotice(ChatContextReader.FileAttachmentVerdict verdict) => verdict switch
    {
        ChatContextReader.FileAttachmentVerdict.TooLarge => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatFileTooLarge"), ChatContextReader.MaxAttachedFileBytes / (1024 * 1024)),
        _ => HubStrings.Get("ChatFileNotText"),
    };

    private void AddProjectAttachment(ChatWorkspace.HubProjectSummary project)
    {
        var details = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatProjectContextFormat"),
            project.EngineVersion,
            project.Platform,
            project.Configuration,
            project.BuildStatus);
        AddContextAttachment(new ContextAttachment("project", project.Name, project.Path, details));
    }

    private void AddContextAttachment(ContextAttachment attachment)
    {
        if (_contextAttachments.Any(existing =>
                string.Equals(existing.Path, attachment.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            return;
        _contextAttachments.Add(attachment);
        RenderContextAttachments();
        UpdateContextRing();
    }

    private void RenderContextAttachments()
    {
        CapturePlanDraftTarget();
        ContextAttachmentPanel.Children.Clear();
        foreach (var attachment in _contextAttachments)
        {
            var label = new TextBlock
            {
                Text = ContextAttachmentPrefix(attachment) + attachment.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240,
            };
            label.Classes.Add("context-attachment-label");
            var remove = new Button { Content = "×", Tag = attachment };
            remove.Classes.Add("context-attachment-remove");
            ToolTip.SetTip(label, attachment.Path);
            remove.Click += (_, _) =>
            {
                if (remove.Tag is ContextAttachment selected)
                {
                    _contextAttachments.Remove(selected);
                    RenderContextAttachments();
                    UpdateContextRing();
                }
            };
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (VectorCover(attachment) is { } cover) chip.Children.Add(cover);
            chip.Children.Add(label);
            chip.Children.Add(remove);
            var border = new Border { Child = chip };
            border.Classes.Add("context-attachment");
            ContextAttachmentPanel.Children.Add(border);
        }

        foreach (var picture in _pendingPictures)
        {
            var remove = new Button { Content = "×", Tag = picture };
            remove.Classes.Add("context-attachment-remove");
            remove.Click += (_, _) =>
            {
                if (remove.Tag is PendingPicture selected)
                {
                    _pendingPictures.Remove(selected);
                    RenderContextAttachments();
                    UpdateSendState();
                }
            };

            // The thumbnail is the label: a picture's file name says nothing about it, and a person deciding
            // whether this is the capture they meant has to see it. Bytes rather than a path because nothing of
            // this draft has reached the disk yet.
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var thumb = TryOpenBitmap(picture.Bytes);
            Control face = thumb is null
                ? new TextBlock { Classes = { "muted" }, Text = picture.Name, MaxWidth = 120 }
                : new Button
                {
                    Classes = { "picture-face" },
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Content = new Image
                    {
                        Source = thumb,
                        Width = 34,
                        Height = 34,
                        Stretch = Stretch.Uniform,
                    },
                };
            if (face is Button opener)
            {
                // Thirty-four pixels of a screen capture is enough to recognise it and not enough to read it, and
                // this is the moment before the message goes out — the only one where "that is the wrong window"
                // is still cheap to act on. The draft's pictures are their own set: they have not been sent, so
                // paging from one into the transcript's would page into pictures this message does not have.
                var index = _pendingPictures.IndexOf(picture);
                opener.Click += (_, _) => ShowPictureViewer?.Invoke(DraftPictureSet(), index < 0 ? 0 : index);
            }
            chip.Children.Add(face);
            chip.Children.Add(remove);
            var frame = new Border { Child = chip };
            frame.Classes.Add("context-attachment");
            ToolTip.SetTip(frame, $"{picture.Name} · {picture.Bytes.LongLength} bytes");
            ContextAttachmentPanel.Children.Add(frame);
        }

        ContextAttachmentPanel.IsVisible = _contextAttachments.Count > 0 || _pendingPictures.Count > 0;
    }

    private static string ContextAttachmentPrefix(ContextAttachment attachment) => attachment.Kind switch
    {
        "project" => HubStrings.Get("ChatProjectPrefix"),
        "file" => HubStrings.Get("ChatFilePrefix"),
        _ => HubStrings.Get("ChatFolderPrefix"),
    };

    /// <summary>
    /// What an .svg attachment looks like, or null when there is nothing to draw with. The attachment is still
    /// text — the markup is what the model receives — and this is only the chip's face: a vector document drawn
    /// at chip size by the package the markdown renderer already brought in, so no raster is produced, stored or
    /// sent. Anything that fails to parse falls back to the name, because a chip that cannot say which file it is
    /// has stopped being a chip.
    /// </summary>
    private static Control? VectorCover(ContextAttachment attachment)
    {
        if (attachment.Kind != "file"
            || !System.IO.Path.GetExtension(attachment.Path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(attachment.Path)) return null;
        try
        {
            // A null base URI on purpose: the cover is a 34-pixel face, not a browser, and a document that names
            // a sibling file or a URL must not be able to make the composer read or fetch anything.
            return new Image
            {
                Source = new Avalonia.Svg.SvgImage
                {
                    Source = Avalonia.Svg.SvgSource.Load(attachment.Path, null, null),
                },
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
            };
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException
                                   or System.Xml.XmlException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private Task<string?> ReadAttachmentContextAsync()
    {
        if (_contextAttachments.Count == 0) return Task.FromResult<string?>(null);
        var attachments = _contextAttachments.ToArray();
        // The window is read here rather than inside the task: the attachment is priced against what the model
        // can actually hold, and a pool thread must not reach the workspace.
        var windowTokens = _chat?.SelectedChatModel is { } selected
            ? ContextBudget.For(selected.Provider, selected.ModelName).Tokens
            : ContextBudget.FallbackTokens;
        return Task.Run<string?>(() =>
        {
            var sections = attachments.Select(attachment =>
            {
                var contents = ChatContextReader.Read(attachment.Path, attachment.Name, windowTokens);
                return attachment.Details is { Length: > 0 }
                    ? attachment.Details + "\n\n" + contents
                    : contents;
            });
            return string.Join("\n\n", sections);
        });
    }

    // ───────────────────────── Pictures ─────────────────────────
    //
    // A picture is a second channel next to the text attachments: a folder becomes prompt text, a picture stays
    // bytes on the wire. They share the chip row and nothing else — reading one through the other would either
    // send a PNG as source code or quietly drop it.

    /// <summary>Offers one picture to the composer. The rules are Core's, the same four a captured frame is
    /// admitted by, and the notice names which one fired: "capture a smaller region" and "attach fewer at once"
    /// are different fixes, and a notice that only says "no" gets the same file picked again.</summary>
    private void AddPendingPicture(byte[] bytes, string name, ChatImage? reusedAttachment = null,
        string? sourceConversationId = null)
    {
        var verdict = ChatImageFormat.Admit(bytes, _pendingPictures.Count);
        if (verdict != ChatImageVerdict.Accepted)
        {
            AppendNotice(ImageNotice(verdict), danger: true);
            return;
        }

        _pendingPictures.Add(new PendingPicture(bytes, name, reusedAttachment, sourceConversationId));
        RenderContextAttachments();
        UpdateSendState();
    }

    /// <summary>Same admission for a picture that arrives as a file — picked, dropped, or copied from Explorer.
    /// The size is checked before the bytes are read, so dropping a folder of raw captures cannot freeze the
    /// window on a gigabyte Hub was going to refuse anyway.</summary>
    private void AddPictureFromPath(string path)
    {
        byte[]? bytes;
        ChatImageVerdict verdict;
        try
        {
            bytes = ConversationStore.ReadCandidateFile(path, out verdict);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
            return;
        }

        if (bytes is null)
        {
            // A file that is not a picture is not automatically nothing: an .svg, a .md, a source file dragged in
            // belongs to the text channel, which is the only one that can carry it. The text channel is asked
            // only when it could take the file — a .png that happens to hold text is a picture someone
            // mis-exported, and answering it with "not a text file" would name the wrong problem — and whatever
            // it answers is the last word, because two notices for one gesture read as two problems.
            if (verdict == ChatImageVerdict.Unrecognized
                && ChatContextReader.AdmitFile(path) != ChatContextReader.FileAttachmentVerdict.Unreadable)
            {
                AddFileAttachment(path);
                return;
            }

            AppendNotice(ImageNotice(verdict), danger: true);
            return;
        }

        AddPendingPicture(bytes, System.IO.Path.GetFileName(path));
    }

    private static string ImageNotice(ChatImageVerdict verdict) => verdict switch
    {
        ChatImageVerdict.Empty => HubStrings.Get("ChatImageEmpty"),
        ChatImageVerdict.Unrecognized => HubStrings.Get("ChatImageUnrecognized"),
        ChatImageVerdict.TooLarge => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatImageTooLarge"), ChatImageFormat.MaxImageBytes / (1024 * 1024)),
        _ => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatImageTooMany"), ChatImageFormat.MaxImagesPerMessage),
    };

    /// <summary>What a refused send should say. The picture refusals are worded with the limit in them, so they
    /// go through the same formatter the composer used when it refused the picture on the way in — a key shown
    /// raw would print the brace.
    ///
    /// <para><c>NoAvailableChatModels</c> splits in two, because one sentence cannot describe both states: the
    /// picker genuinely empty, which is a settings problem, and this session naming a model its provider stopped
    /// offering while other models sit in the list right there. The second is fixable in the chip — provided the
    /// line says which name went missing, rather than claiming nothing is available when plenty is.</para></summary>
    private string NoticeFor(string? refusalKey, string? conversationId) => refusalKey switch
    {
        "ChatImageEmpty" => ImageNotice(ChatImageVerdict.Empty),
        "ChatImageUnrecognized" => ImageNotice(ChatImageVerdict.Unrecognized),
        "ChatImageTooLarge" => ImageNotice(ChatImageVerdict.TooLarge),
        "ChatImageTooMany" => ImageNotice(ChatImageVerdict.TooMany),
        "NoAvailableChatModels" when _chat.UnresolvableSessionModel(conversationId ?? "") is { } gone
            => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatSessionModelGoneFormat"), gone),
        _ => HubStrings.Get(refusalKey ?? "ChatFailed"),
    };

    private async Task AddLocalPictureAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFileAsync(owner, HubStrings.Get("ChatPickImageTitle"), PictureFileTypes);
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatImageNotLocal"), danger: true);
            return;
        }

        if (result.Outcome != PickOutcome.Picked || result.Path is not { } path) return;
        AddPictureFromPath(path);
    }

    /// <summary>The picker's filter is a courtesy, not a rule: what a picture *is* stays a decision about the
    /// bytes' header, because a pasted capture has no file name to match.</summary>
    private static IReadOnlyList<FilePickerFileType> PictureFileTypes { get; } =
    [
        new("PNG") { Patterns = ["*.png"] },
        new("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
        new("GIF") { Patterns = ["*.gif"] },
        new("WebP") { Patterns = ["*.webp"] },
    ];

    /// <summary>Ctrl+V in the composer: a picture on the clipboard becomes a chip, and everything else keeps
    /// being the text paste the box already does. Claiming the keystroke only once there is a picture to take is
    /// what keeps an ordinary paste working — a paste handler that eats text is a worse bug than no paste.</summary>
    private async Task<bool> TryPastePictureAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        IAsyncDataTransfer? transfer;
        try
        {
            transfer = await clipboard.TryGetDataAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return await TakeClipboardAsync(transfer);
    }

    /// <summary>The paste's one decision: did a picture get taken, or should the box paste text as it always
    /// has? Split out because it is the half a check can be handed a payload for.</summary>
    private async Task<bool> TakeClipboardAsync(IAsyncDataTransfer? transfer)
    {
        if (transfer is null) return false;
        foreach (var item in transfer.Items)
        {
            if (!item.Formats.Contains(ChatMessageClipboard.Format)
                || await item.TryGetRawAsync(ChatMessageClipboard.Format) is not byte[] raw
                || ChatMessageClipboard.TryDeserialize(raw) is not { } payload) continue;
            var currentId = _chat.ViewedConversationId;
            foreach (var image in payload.Images)
            {
                var sameSession = currentId == payload.ConversationId
                    && _chat.StoredImagePath(currentId!, image.Attachment.File) is { } path
                    && ReadPictureBytes(path) is { } stored
                    && stored.AsSpan().SequenceEqual(image.Bytes);
                if (sameSession)
                    AddPendingPicture(image.Bytes, image.Attachment.File, image.Attachment, payload.ConversationId);
                else
                    AddPendingPicture(image.Bytes, image.Attachment.File);
            }
            if (payload.Text.Length > 0)
            {
                var existing = InputBox.Text ?? "";
                InputBox.Text = existing.Length == 0 ? payload.Text : existing + Environment.NewLine + payload.Text;
                InputBox.CaretIndex = InputBox.Text.Length;
                UpdateSendState();
            }
            return true;
        }
        return await TakePicturesAsync(transfer) > 0;
    }

    /// <summary>
    /// The paste's actual decision, separated from the clipboard so it can be handed a transfer of a known shape.
    /// A screenshot arrives as a bitmap with no file name, a copied file arrives as a storage item, and a copy
    /// from some applications arrives as both — the loop takes what is a picture and leaves the rest alone.
    /// </summary>
    private async Task<int> TakePicturesAsync(IAsyncDataTransfer transfer)
    {
        var taken = 0;
        foreach (var item in transfer.Items)
        {
            if (item.Formats.Contains(DataFormat.Bitmap)
                && await item.TryGetRawAsync(DataFormat.Bitmap) is Bitmap bitmap)
            {
                AddPendingPicture(EncodePng(bitmap), HubStrings.Get("ChatPastedImageName"));
                taken++;
            }
            else if (item.Formats.Contains(DataFormat.File)
                     && await item.TryGetRawAsync(DataFormat.File) is { } raw)
            {
                foreach (var path in LocalPathsOf(raw)) AddPictureFromPath(path);
                taken++;
            }
        }

        return taken;
    }

    /// <summary>A clipboard or drop payload hands back one item, a list of them, or an array — the shape is the
    /// platform's business, so the view accepts all three rather than betting on one.</summary>
    private static IEnumerable<string> LocalPathsOf(object raw)
    {
        IReadOnlyList<IStorageItem> items = raw switch
        {
            IStorageItem one => [one],
            IEnumerable<IStorageItem> many => many.ToArray(),
            _ => [],
        };
        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    /// <summary>A bitmap has to become the bytes Hub keeps, and PNG is the only honest answer: a capture loses
    /// nothing to it, and the header is what says so on the wire.</summary>
    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new System.IO.MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static bool DragCarriesFiles(DragEventArgs e)
        => e.DataTransfer.Items.Any(item => item.Formats.Contains(DataFormat.File));

    private void OnComposerDrop(object? sender, DragEventArgs e)
    {
        // The ring is cleared whatever the drop carried: a leave does not necessarily follow a drop, so a ring
        // that only comes off on DragLeave would stay lit for the rest of the session.
        ComposerFrame.Classes.Remove("drag-over");
        if (!DragCarriesFiles(e)) return;
        e.Handled = true;
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.File) is { } raw)
                foreach (var path in LocalPathsOf(raw)) AddPictureFromPath(path);
    }

    /// <summary>The bytes of every picture waiting in the composer, in chip order, or null when there are none —
    /// the shape the send and steer paths take so an empty draft stays exactly the request it was before.</summary>
    private IReadOnlyList<byte[]>? PendingPictureBytes()
        => _pendingPictures.Where(picture => picture.ReusedAttachment is null
                || picture.SourceConversationId != _chat.ViewedConversationId)
            .Select(picture => picture.Bytes).ToArray() is { Length: > 0 } fresh ? fresh : null;

    private IReadOnlyList<ChatImage>? PendingReusedPictures()
        => _pendingPictures.Where(picture => picture.ReusedAttachment is not null
                && picture.SourceConversationId == _chat.ViewedConversationId)
            .Select(picture => picture.ReusedAttachment!).ToArray() is { Length: > 0 } reused ? reused : null;

    /// <summary>
    /// The pictures one sent message carries, painted from the file Hub kept. Bounded so a 5K capture cannot push
    /// the chat column open, and a file that is no longer there says so rather than drawing nothing at all — the
    /// transcript claims a picture was sent, and that claim has to stay checkable.
    ///
    /// <para>Every cell is built at once but its pixels are not: the decode runs off the UI thread and to the
    /// width the column shows (see <see cref="DecodeTurnPictureAsync"/>), because a session with screenshots in
    /// it would otherwise pay several megapixels of decoding inside the frame that opens it.</para>
    /// </summary>
    private Control BuildTurnPictures(string conversationId, IReadOnlyList<ChatImage> images)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Classes.Add("turn-pictures");
        foreach (var image in images)
        {
            var path = _chat.StoredImagePath(conversationId, image.File);
            // A missing file is answered from the name alone. That is a fact about the disk rather than about
            // the pixels, and waiting for a decode to fail would say it one frame later.
            if (path is null)
            {
                row.Children.Add(GonePictureCell(image));
                continue;
            }

            var picture = new Image { Stretch = Stretch.Uniform, MaxWidth = 260, MaxHeight = 200 };
            ToolTip.SetTip(picture, $"{image.File} · {image.MediaType} · {image.Bytes} bytes");
            // The spacing moves onto the button so the gap between two thumbnails is not itself a hit target.
            var frame = new Border { Child = picture, Classes = { "turn-picture" }, Margin = new Thickness(0) };
            var opener = new Button
            {
                Classes = { "picture-face" },
                Content = frame,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            // The set is the conversation's pictures in transcript order and this one's place in it; the
            // bytes stay unread until the viewer lands on them (see PictureRef).
            var key = $"{conversationId}#{image.File}";
            opener.Click += (_, _) =>
            {
                var set = TranscriptPictureSet(conversationId);
                var at = 0;
                while (at < set.Count && set[at].Key != key) at++;
                ShowPictureViewer?.Invoke(set, at >= set.Count ? 0 : at);
            };
            row.Children.Add(opener);
            _ = DecodeTurnPictureAsync(path, picture, opener, image);
        }

        return row;
    }

    /// <summary>One frame saying the picture is gone, in the shape a transcript row gives it. Shared by the two
    /// ways a picture can be missing — no file, and a file no decoder will take.</summary>
    private static Border GonePictureCell(ChatImage image)
    {
        var gone = new TextBlock { Classes = { "muted" }, Text = HubStrings.Get("ChatImageGone") };
        ToolTip.SetTip(gone, $"{image.File} · {image.MediaType} · {image.Bytes} bytes");
        return new Border { Child = gone, Classes = { "turn-picture" } };
    }

    /// <summary>
    /// Fills one transcript thumbnail, off the UI thread and at the size the column draws.
    ///
    /// <para>Width rather than height because the row is a horizontal wrap of pictures capped at
    /// <c>260</c> logical pixels: a screenshot keeps everything a 260-wide slot can show and nothing above it, so
    /// decoding the full 4K frame would be twelve megapixels to make a two-hundred-pixel image. The scale factor
    /// is the window's, so a 2x display still gets 2x of pixels.</para>
    /// </summary>
    private async Task DecodeTurnPictureAsync(string path, Image target, ContentControl holder, ChatImage image)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var width = (int)Math.Clamp(Math.Ceiling(260 * scale), 64, 1560);
        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(() => TryDecodeBitmap(path, width)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Off the UI thread there is no frame to fail: a decoder that throws something the narrow list in
            // TryDecodeBitmap did not name still ends as a cell that says the picture is gone, which is the same
            // answer the renderer gives a file it cannot read at all.
            bitmap = null;
        }

        if (bitmap is not null)
        {
            target.Source = bitmap;
            return;
        }

        // A file the header promised and the decoder refused. It says so where the picture would have been,
        // rather than leaving an empty frame that reads as a picture Hub has not finished loading.
        holder.Content = GonePictureCell(image);
    }

    /// <summary>
    /// The conversation's pictures in transcript order, which is the set the window-level viewer pages through.
    /// A picture whose stored file is gone still gets an entry: the transcript claims it was sent, and a viewer
    /// that says "the picture is gone" keeps that claim checkable, where dropping the entry would silently
    /// renumber every picture after it.
    /// </summary>
    private IReadOnlyList<PictureRef> TranscriptPictureSet(string conversationId)
    {
        var list = new List<PictureRef>();
        if (_chat.ActiveConversation is not { } conversation || conversation.Id != conversationId) return list;
        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var turn = conversation.Messages[index];
            foreach (var image in turn.Images)
            {
                var file = image.File;
                list.Add(new PictureRef(
                    Key: $"{conversationId}#{file}",
                    Caption: $"{image.File} · {image.MediaType} · {image.Bytes} bytes",
                    Bytes: image.Bytes,
                    Load: () => ReadPictureBytes(_chat.StoredImagePath(conversationId, file))));
            }
        }
        return list;
    }

    /// <summary>The composer's unsent pictures as their own set: they have not reached the disk, and paging
    /// from a draft into the transcript would page into pictures this message does not carry.</summary>
    private IReadOnlyList<PictureRef> DraftPictureSet()
        => _pendingPictures.Select((picture, index) => new PictureRef(
            Key: $"draft#{index}",
            Caption: $"{picture.Name} · {picture.Bytes.LongLength} bytes",
            Bytes: picture.Bytes.LongLength,
            Load: () => picture.Bytes)).ToList();

    /// <summary>The stored bytes of one attached picture, or null when they are no longer readable — which the
    /// viewer answers by saying so in the caption row rather than drawing an empty frame.</summary>
    private static byte[]? ReadPictureBytes(string? path)
    {
        if (path is not { Length: > 0 }) return null;
        try
        {
            return System.IO.File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>One transcript thumbnail, decoded to the width the column can show. Read from a stream rather
    /// than from a file name because that is the form of the call that takes a width — and the width is the whole
    /// point: the reader is shown 260 logical pixels of a capture that may be 4000 wide.</summary>
    private static Bitmap? TryDecodeBitmap(string path, int width)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The same preview from bytes, because a draft has not reached the disk. A picture whose header
    /// Hub recognizes but whose pixels it cannot decode stays in the queue under its file name rather than
    /// vanishing — what the model is going to receive is the bytes, not this preview.</summary>
    private static Bitmap? TryOpenBitmap(byte[] bytes)
    {
        try
        {
            return new Bitmap(new System.IO.MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static string ToolActivityText(string name, bool completed)
    {
        var tool = name switch
        {
            "project_list" or "get_projects" => HubStrings.Get("ChatToolProjects"),
            "engine_list" or "get_engines" => HubStrings.Get("ChatToolEngines"),
            "toolchain_status" or "get_toolchain_status" => HubStrings.Get("ChatToolchains"),
            _ => name,
        };
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(completed ? "ChatToolCompletedFormat" : "ChatToolRunningFormat"),
            tool);
    }

    /// <summary>The bubble's status line while the endpoint is searching on its own. A start event arrives with no
    /// query in it — that is the whole meaning of "in progress" on this wire — so the line says only that it is
    /// looking, and the first query replaces it when the service reports one.</summary>
    private static string SearchActivityText(ServerSearchNotice notice)
    {
        var queries = notice.Queries;
        if (queries.Count == 0) return HubStrings.Get("ActivityRowWebSearching");
        var first = queries[0];
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ActivityRowWebSearched"),
            first.Length <= 48 ? first : first[..48] + "…");
    }

    /// <summary>The bubble's status line while the pipeline sits out a provider's retry delay. The denominator is
    /// the policy's own number rather than a guess, so the line cannot drift from how many attempts exist.</summary>
    private static string RetryActivityText((int Attempt, TimeSpan Delay) wait)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatRetryWaitFormat"), wait.Attempt, Agent.ChatClientFactory.MaxRetries + 1,
            wait.Delay.TotalSeconds < 1 ? wait.Delay.TotalSeconds.ToString("0.#")
                : Math.Round(wait.Delay.TotalSeconds).ToString("0"));

    /// <summary>
    /// Where the answer came from, under the reply that used it. One quiet line: 「来源」 and as many hosts as the
    /// line can afford, each opening in the browser, the rest folded into a +N whose tooltip carries the whole list.
    ///
    /// <para>It sits with the answer rather than inside the collapsed action group because a citation is not an
    /// action: the person reads the sentence, then decides whether to check it, and making that require opening a
    /// fold they had to notice first is how a client teaches people not to read sources. The queries go in the
    /// line's tooltip — the interesting half of "what did it do" is the address it ended up trusting.</para>
    ///
    /// <para><b>Why a character budget rather than "four and done":</b> this line shares the chat column with the
    /// answer above it, and the column is as narrow as the 560px floor an open inspector leaves. Four chips of long
    /// hostnames is a row wider than that, and the charter is that a surface clips — a clipped source is a source
    /// nobody can click. So the line spends <see cref="SourceChipBudget"/> characters of label, always at least one
    /// chip, never more than four, and everything it cannot afford folds into the +N. Nothing is lost: the fold's
    /// tooltip lists the remaining addresses in the order the endpoint reported them.</para>
    /// </summary>
    private Control BuildSearchSources(ServerSearchLog searches)
    {
        const int SourceChipBudget = 64;
        const int SourceChipLimit = 4;
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        line.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("MessageSources"),
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var sources = searches.Sources();
        var shown = 0;
        var used = 0;
        while (shown < sources.Count && shown < SourceChipLimit
               && (shown == 0 || used + HostOf(sources[shown]).Length + 2 <= SourceChipBudget))
        {
            var address = sources[shown];
            var host = HostOf(address);
            var chip = new Border
            {
                // file-chip for the look, source-chip so a check can tell these from the other chips in the same
                // panel — the run_command preview and the diff counters share the styling and nothing else.
                Classes = { "file-chip", "source-chip" },
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = host, FontSize = 12 },
            };
            ToolTip.SetTip(chip, address);
            // Through the workspace's opener rather than UrlLauncher directly: the same seam the rest of the
            // browser-bound traffic uses, so a self-check can count what a click would have opened without
            // opening anything.
            var target = address;
            chip.PointerPressed += (_, _) => _chat.BrowserOpener(target);
            line.Children.Add(chip);
            used += host.Length + 2;
            shown++;
        }

        if (sources.Count > shown)
        {
            var rest = new TextBlock
            {
                Text = "+" + (sources.Count - shown),
                Classes = { "muted", "source-fold" },
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(rest, string.Join('\n', sources.Skip(shown)));
            line.Children.Add(rest);
        }

        var queries = searches.Queries();
        if (queries.Count > 0)
            ToolTip.SetTip(line, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageSearchedFor"), string.Join(" · ", queries)));
        return line;
    }

    /// <summary>The label a source chip gets: the host, plus its path when the path is short enough to be worth
    /// the room. It is a label, not a URL — the full address lives in the tooltip and in what a click opens — so
    /// an over-long one is cut rather than allowed to push the rest of the line off the column.</summary>
    private static string HostOf(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var target)) return Shorten(address);
        var path = target.AbsolutePath.Trim('/');
        return Shorten(path.Length > 0 && path.Length <= 24 ? $"{target.Host}/{path}" : target.Host);
    }

    /// <summary>The one place a chip's text is measured, so the line's budget and the chip's own cap cannot drift
    /// into two different notions of how wide a source is allowed to be.</summary>
    private static string Shorten(string label) => label.Length <= 34 ? label : label[..33] + "…";

    // ───────────────────────── Messages ─────────────────────────

    private void RenderMessages()
    {
        if (_reconcilingTranscript) return;
        _reconcilingTranscript = true;
        try { RenderVirtualTranscript(); }
        finally { _reconcilingTranscript = false; }
        RefreshGoalCapsule();
    }

    /// <summary>Reconciles display items after an edit or regeneration changed the stored history.</summary>
    private void ForceRebuildMessages()
    {
        RenderMessages();
    }

    private void ScheduleMarkdownPromotion()
    {
        if (_markdownPassQueued || _pendingMarkdownRows.Count == 0) return;
        _markdownPassQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _markdownPassQueued = false;
                PromoteMarkdownInView();
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Renders the Markdown of the plain rows the reader has scrolled within reach of, and queues the next pass
    /// when it spent its quota.
    ///
    /// <para>Which rows: those whose bottom is at or below the top edge of the viewport and whose top is inside
    /// two screens below it. Rows <b>above</b> the top edge are deliberately left alone — drawing one properly
    /// changes its height, and a height that changes above the reading position pushes everything under it down,
    /// which is a transcript jumping under a hand that is scrolling it. Below the top edge the same growth only
    /// moves what has not been read yet. A row that is above the edge today enters the window the moment it is
    /// scrolled back to, so nothing is left permanently plain.</para>
    /// </summary>
    private void PromoteMarkdownInView()
    {
        // A held thumb owns the reading position and the column's geometry: promoting a row changes its
        // height and re-maps the Track under the hand. The release schedules the pass again.
        if (_thumbDragging || _heldItems is not null) return;
        if (_pendingMarkdownRows.Count == 0) return;
        var offset = 0d;
        var reach = MessageScroller.Viewport.Height;
        var ceiling = 2 * reach;
        var promoted = 0;
        for (var i = 0; i < _pendingMarkdownRows.Count && promoted < MarkdownPromotionsPerPass;)
        {
            var pending = _pendingMarkdownRows[i];
            var position = pending.Parent.Children.IndexOf(pending.Row);
            // The row is gone: the piece it lived in was drawn again, or the flow was rebuilt around it. The
            // record goes with the control it named, which is what keeps this list from filling up with rows
            // nobody can reach.
            if (position < 0 || _chat.ActiveConversation is not { } current
                || current.Id != pending.ConversationId || _projection is null
                || !_projection.TryResolve(pending.TurnKey, out var sourceIndex))
            {
                _pendingMarkdownRows.RemoveAt(i);
                continue;
            }

            var top = pending.Row.TranslatePoint(new Point(0, 0), MessageScroller)?.Y;
            if (top is not { } y || !pending.Row.IsEffectivelyVisible
                || y + pending.Row.Bounds.Height < offset || y > ceiling)
            {
                i++;
                continue;
            }

            _pendingMarkdownRows.RemoveAt(i);
            _promotedTranscriptKeys.Add(pending.TurnKey);
            // Drawn the ordinary way into a panel of its own and then moved into place: a turn has exactly one
            // way of being drawn, and a promotion that assembled a second one would be a second thing to keep
            // in step with the first.
            var staging = new StackPanel { Spacing = 8 };
            AppendRenderedTurn(pending.ConversationId, sourceIndex, current.Messages[sourceIndex], pending.ToolName,
                sourceIndex == current.Messages.Count - 1, markdown: true, target: staging, inGroup: pending.InGroup);
            if (staging.Children.Count != 1) continue;
            var promotedRow = staging.Children[0];
            staging.Children.RemoveAt(0);
            promotedRow.Classes.Remove(PlainTurnClass);
            pending.Parent.Children[position] = promotedRow;
            promoted++;
        }

        // A pass that spent its whole quota has more rows waiting in the same window; one that did not has
        // nothing left in reach and will be asked again by the next scroll.
        if (promoted == MarkdownPromotionsPerPass) ScheduleMarkdownPromotion();
    }

    /// <summary>
    /// Draws one turn into <paramref name="target"/> and answers with the row it built.
    ///
    /// <para>The row comes back because the caller that held its Markdown back has to be able to find it again:
    /// a plain row carries <see cref="PlainTurnClass"/> and is filed in <see cref="_pendingMarkdownRows"/>, and
    /// <see cref="PromoteMarkdownInView"/> replaces it in place — which needs the control, not just the turn.
    /// A Markdown row is drawn here or nowhere: the promotion pass re-enters this method with
    /// <c>markdown: true</c> rather than assembling a second, different way of drawing a turn.</para>
    /// </summary>
    private Control AppendRenderedTurn(
        string conversationId, int index, ChatTurn turn, string? toolName, bool isLast, bool markdown,
        Panel target, bool inGroup)
    {
        var fromUser = turn.Role == ChatRoles.User;
        var body = new StackPanel { Spacing = 8 };
        if (turn.ReplyToId is { Length: > 0 } replyId
            && _chat.ActiveConversation is { } activeConversation
            && activeConversation.Id == conversationId
            && activeConversation.Messages.FirstOrDefault(candidate => candidate.Id == replyId) is { } cited)
        {
            var speaker = cited.Role == ChatRoles.User ? HubStrings.Get("ReplyUser") : HubStrings.Get("ReplyAssistant");
            body.Children.Add(new Border
            {
                Classes = { "reply-quote" },
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Classes = { "reply-speaker" }, Text = speaker },
                        new TextBlock { Text = cited.Text, TextWrapping = TextWrapping.Wrap, MaxHeight = 54 },
                    },
                },
            });
        }

        // Whose words these are is not optional information: a peer session's message would otherwise read as
        // something the user typed. The stored turn carries the source's id, so the title is looked up here — and
        // a session deleted since still gets a line rather than no label at all.
        if (turn.InjectedFrom is { Length: > 0 } peer)
        {
            var name = _chat.SessionTitleFor(peer);
            var origin = new TextBlock
            {
                Classes = { "muted", "peer-origin" },
                Text = name is { Length: > 0 } titled
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("ChatPeerOriginFormat"), titled)
                    : HubStrings.Get("ChatPeerOriginGone"),
            };
            ToolTip.SetTip(origin, peer);
            body.Children.Add(origin);
        }

        // The machine half of a tool exchange is not prose. What came back is a JSON array, a directory listing,
        // or a compiler's stderr, and painting it in the assistant's own face makes the transcript read as though
        // the model had recited it — which is exactly what a person cannot un-see. One quiet line says what
        // arrived, trimmed to what a glance holds.
        // A plan is the other case where a row's text is a document rather than a reply, and it is suppressed the
        // same way the tool payload is. The card built below stands in for it and names it. Nothing was taken away
        // from the provider either: the body is filed in its own document and put back on the wire where the
        // request is assembled, and this row's copy action resolves it the same way — a decision about painting,
        // not about what was said.
        var isPlanTurn = turn.PlanApprovalState is { Length: > 0 };
        // A turn that only thought has no prose to paint, and an empty block is not an empty line: it carries the
        // body's own line height, so it used to leave a gap under the 「已思考 Ns」 head with nothing in it. The
        // thinking itself is what belongs there, and it is added below once the answer has been rendered.
        if (turn.Role == ChatRoles.Tool) body.Children.Add(ToolResultLine(toolName, turn));
        else if (!isPlanTurn && turn.Text.Length > 0)
            // Selectable, not a bare TextBlock: this is the person's own words, and words somebody wrote are words
            // they copy — a TextBlock cannot be selected at all, which made the one message they certainly care
            // about the only one on screen that could not be. It carries the same `turn-text` class, so its size,
            // ink and leading stay the ones the style already states for this row, and it is given the same
            // right-click menu a rendered answer carries, so selecting one's own message is as useful as
            // selecting the reply to it.
            body.Children.Add(OwnWordsBlock(turn.Text));

        // Pictures are painted from the file Hub kept, not from anything this view remembers: the transcript is
        // the record that a picture exists, and a row rebuilt after a restart has to show the same frame. User
        // attachments sit under their message; assistant captures sit in the tool exchange row that produced them.
        if (fromUser && turn.Images.Count > 0) body.Children.Add(BuildTurnPictures(conversationId, turn.Images));

        Control? approvalSurface = null;

        // A call that needed permission carries its own record in the transcript: one quiet line while the
        // decision is owed (the buttons live in the composer's decision host, not here), one quiet line once it
        // is not. A call that never needed asking gets nothing drawn here, which is why the approval state —
        // not the presence of a tool call — is what decides. A write is the exception: it changed a file
        // whether or not anybody was asked, and the line is where its undo lives.
        // The pending line must not disappear altogether: the incremental renderer invalidates on a stamp of
        // these states, and a scrolled-back reader still needs to see that a decision was owed at this turn.
        if (turn.ToolCallId is { Length: > 0 } callId)
        {
            if (turn.ApprovalState == ChatApprovalStates.Pending)
                approvalSurface = BuildApprovalPendingLine(turn);
            else if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 }
                     || (turn.ChangeId is { Length: > 0 } && !turn.UndoConsumed)
                     || (turn.ManagementBatchId is { Length: > 0 } && !turn.UndoConsumed))
                approvalSurface = BuildCallRecord(conversationId, callId, turn, index);
        }
        if (isPlanTurn) approvalSurface = BuildPlanCard(turn, index);

        // A function-call or tool-result turn gets no action bar: it is not a readable message, and acting on
        // half of a call/result pair orphans the other half (the pipeline sends them to the provider as one
        // exchange). `index` is the only thing that gates the bar, so nulling it here leaves stored indexes
        // untouched for every other turn.
        var actionableIndex = turn.ToolCallId is { Length: > 0 } ? (int?)null : index;
        // 「复制」 hands over what was said, and for a plan what was said is the document the card stands in for.
        // Resolved per row rather than per keystroke: a session holds a handful of plans, and this runs once for
        // each turn the transcript paints. A body no longer on disk copies as nothing, which beats copying the
        // card's title back as though it were the plan.
        var copyText = isPlanTurn ? _chat.PlanBody(turn, out _) ?? "" : turn.Text;
        var row = BuildMessageRow(fromUser, body, actionableIndex, turn.Role, copyText, isLast, turn.At,
            isGoal: fromUser && turn.IsGoal);
        target.Children.Add(row);

        // User text is plain by nature, and a tool payload is now a quiet line rather than a bubble; only the
        // assistant's own words carry Markdown worth rendering — and a plan's words do not render here at all.
        // A prose turn whose Markdown was held back keeps the plain block added above, wears the mark that says
        // so, and is filed with what a later pass needs to draw it properly once a reader scrolls to it.
        if (!fromUser && !isPlanTurn && turn.Role != ChatRoles.Tool && turn.Text.Length > 0)
        {
            if (markdown && !IsLiteralTranscriptProse(turn.Text))
            {
                MarkdownMessageRenderer.RenderInto(body, turn.Text, QuoteIntoComposer);
            }
            else if (!markdown)
            {
                row.Classes.Add(PlainTurnClass);
                _pendingMarkdownRows.Add(new PendingMarkdownRow
                {
                    Parent = target,
                    Row = row,
                    ConversationId = conversationId,
                    TurnKey = _projection!.KeyAt(index),
                    ToolName = toolName,
                    IsLast = isLast,
                    InGroup = inGroup,
                });
            }
        }

        // The thinking sits above the answer, which is the order it happened in and the order every client of
        // this shape puts it — but it has to be <i>inserted</i>, not added, because the pass just above clears
        // the panel it renders into. Laid down before that call it would not sit under the answer, it would be
        // gone with everything else the panel held.
        if (!fromUser && !isPlanTurn && turn.Role != ChatRoles.Tool
            && BuildThoughtRowForTurn(conversationId, index, turn, inGroup) is { } reasoning)
            body.Children.Insert(0, reasoning);

        // Run metadata belongs to the final assistant output, after its body and before any ancillary sources.
        if (!fromUser && turn.Role != ChatRoles.Tool && turn.RunElapsed is { } took)
        {
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(turn.RunModel)) details.Add(turn.RunModel);
            if (!string.IsNullOrWhiteSpace(turn.RunReasoningEffort))
            {
                var effort = turn.RunReasoningEffort == ChatReasoningEfforts.Default
                    ? HubStrings.Get("ChatReasoningDefault")
                    : turn.RunReasoningEffort;
                details.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatRunEffortFormat"), effort));
            }
            details.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatRunElapsedFormat"), FormatDuration(took)));
            body.Children.Add(new TextBlock
            {
                Classes = { "run-elapsed" },
                Text = string.Join("  ·  ", details),
            });
        }

        // Added after the Markdown pass rather than before it, because <see cref="MarkdownMessageRenderer.RenderInto"/>
        // clears the panel it renders into: a line laid down first is not underneath the answer, it is gone. This is
        // also the order the person reads — the reply, then where it came from, then any record of a decision.
        if (!fromUser && ServerSearchLog.Read(turn.WebSearch) is { IsEmpty: false } searches)
            body.Children.Add(BuildSearchSources(searches));

        if (isLast && !fromUser && turn.Role != ChatRoles.Tool
            && _chat.ActiveConversation is { } active && active.Id == conversationId)
        {
            var apiCalls = active.Messages.Select(message => message.WebApiCall)
                .Where(call => call is not null).ToArray();
            if (apiCalls.Length > 0)
            {
                var searchesCount = apiCalls.Count(call => call!.Operation == "search");
                var fetchesCount = apiCalls.Count(call => call!.Operation == "fetch");
                var billableSearches = apiCalls.Count(call => call!.Operation == "search" && call.Billable);
                var billableFetches = apiCalls.Count(call => call!.Operation == "fetch" && call.Billable);
                var estimated = (billableSearches + billableFetches) * 0.005m;
                body.Children.Add(new TextBlock
                {
                    Classes = { "muted", "tool-result-line" },
                    Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("DeepSeekWebUsageFormat"), searchesCount, billableSearches,
                        fetchesCount, billableFetches, estimated),
                });
            }
        }

        if (approvalSurface is not null) body.Children.Add(approvalSurface);
        return row;
    }

    /// <summary>The person's own words, as something they can select, copy and quote.</summary>
    private SelectableTextBlock OwnWordsBlock(string text)
    {
        var block = new SelectableTextBlock { Classes = { "turn-text" }, Text = text };
        // The same menu a rendered answer carries, read off the block instead of off a document: a plain turn is
        // one string, so its selection needs no extractor.
        SelectionMenu.Attach(block, () => block.SelectedText ?? "", QuoteIntoComposer);
        return block;
    }

    private Button GoalActionButton(string glyph, string tooltipKey, Action action)
    {
        var button = new Button { Content = glyph, Padding = new Thickness(6, 1), MinWidth = 22, MinHeight = 22 };
        ToolTip.SetTip(button, HubStrings.Get(tooltipKey));
        button.Click += (_, _) => action();
        return button;
    }

    private void SetReplyTarget(int index)
    {
        if (_chat.ActiveConversation is not { } conversation || index < 0 || index >= conversation.Messages.Count) return;
        var target = conversation.Messages[index];
        if (target.Role is not (ChatRoles.User or ChatRoles.Assistant) || target.Text.Length == 0
            || target.ToolCallId is not null || target.PlanApprovalState is not null) return;
        _replyToId = target.Id;
        var speaker = target.Role == ChatRoles.User ? HubStrings.Get("ReplyUser") : HubStrings.Get("ReplyAssistant");
        var preview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        preview.Children.Add(new TextBlock
        {
            Text = $"{speaker}: {target.Text}",
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 680,
        });
        preview.Children.Add(IconActionButton("CancelReply", "Hub.Icon.Close", ClearReplyTarget));
        ReplyPreview.Child = preview;
        ReplyPreview.IsVisible = true;
        InputBox.Focus();
    }

    private void ClearReplyTarget()
    {
        _replyToId = null;
        ReplyPreview.Child = null;
        ReplyPreview.IsVisible = false;
    }

    /// <summary>Puts a quoted block in the composer — where the right-click menu's 引用 hands a selection.
    ///
    /// <para>The quote is Markdown, one <c>&gt; </c> per line, because that is what the box is for: what arrives is
    /// something the person can answer underneath, and the fence is what tells the model those words are a quote
    /// rather than the start of their own sentence. It is inserted at the caret and takes nothing over — the draft
    /// around it stays where it was, and the caret lands after the quote, which is where the reply goes.</para>
    /// </summary>
    internal void QuoteIntoComposer(string text)
    {
        if (text.Length == 0) return;

        var quoted = string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => "> " + line));
        var draft = InputBox.Text ?? "";
        var caret = Math.Clamp(InputBox.CaretIndex, 0, draft.Length);
        var before = draft[..caret];
        var after = draft[caret..];
        // A quote is its own block: it opens on a fresh line, and what follows it gets a line of its own to be
        // written on rather than continuing the quoted paragraph.
        var inserted = (before.Length > 0 && !before.EndsWith('\n') ? "\n" : "")
                       + quoted
                       + (after.StartsWith('\n') ? "\n" : "\n\n");
        InputBox.Text = before + inserted + after;
        InputBox.CaretIndex = before.Length + inserted.Length;
        InputBox.Focus();
    }

    /// <summary>Whether a turn is machine work rather than something said: a tool result, a call, or a
    /// thinking-only answer. These are the turns a run folds into one group; prose and user turns break it.
    /// A thinking turn that also carries prose is prose — the answer is what was asked for, and folding it
    /// away would hide the reply.
    ///
    /// <para>A plan is prose whatever its text says, so it is excepted by name. Its body lives in its own document
    /// and its turn's text is legitimately empty, which is the shape this test would otherwise read as
    /// "thought and said nothing" — and the review card would go inside the collapsed 「Thought for Ns」 fold, on a
    /// model whose answers always carry thinking.</para></summary>
    private static bool IsActivityTurn(ChatTurn turn) => turn.Role switch
    {
        ChatRoles.Tool => true,
        ChatRoles.Assistant => turn.ToolCallId is { Length: > 0 }
                               || (turn.Reasoning is { Length: > 0 } && turn.Text.Length == 0
                                   && turn.PlanApprovalState is null),
        _ => false,
    };

    /// <summary>Whether this turn is a call the model asked for <i>while saying why</i>, and owns no surface of
    /// its own: an auto-allowed read or command, whose prose would otherwise be filed on the call turn and drawn by
    /// nobody. A call that owed a decision, or a write that left a copy, is excluded — it already paints its prose
    /// through its own row, inside the fold, and asserting that is <c>d7c3f9f</c>'s job. Whitespace does not count:
    /// a blank sentence must not buy a blank row, which is the gap this whole branch used to be argued on.
    ///
    /// <para>These are what the flat pieces of the transcript are made of — the sentence goes in the flow and its
    /// one action sits under it, because the words are the thread the person is reading and hiding them behind a
    /// collapsed head is indistinguishable from the model having said nothing.</para></summary>
    private static bool IsNarratedCall(ChatTurn turn) => turn.Role == ChatRoles.Assistant
                                                         && turn.ToolCallId is { Length: > 0 }
                                                         && !string.IsNullOrWhiteSpace(turn.Text)
                                                         && turn.ApprovalState is null && turn.UndoName is null
                                                         && (turn.ChangeId is null || turn.UndoConsumed)
                                                         && (turn.ManagementBatchId is null || turn.UndoConsumed);

    /// <summary>
    /// <summary>
    /// The head's row: a chevron, a sentence naming the first action (and how many followed), and an aggregate
    /// <c>+N −M</c> that trails that sentence. Naming the work rather than counting it is what keeps a tools-only
    /// turn from reading as a lonely pill stranded at the left edge; trailing the count rather than pinning it to
    /// the right edge is what keeps the head one line the eye finishes in order, rather than a second column whose
    /// left edge moves with every label width.
    /// </summary>
    private static Control BuildActivityGroupHead(List<(int Index, ChatTurn Turn, string? ToolName)> turns,
        int callCount)
    {
        // The head toggle carries the HubExpanderHeader theme, which supplies the same left `>`/`v` arrow the
        // sidebar's workspace groups use — so no chevron is built here, and the grid only lays out the sentence and
        // the aggregate count. The slack goes to the last column, after the count.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };

        var (text, added, removed, exact) = GroupSummary(turns, callCount);
        // The sentence embeds the first call's description, so its length is not bounded — and an Auto column is
        // measured against the whole row, where trimming could never bite. The cap is what makes the ellipsis
        // reachable, the same way tool-result-line carries one.
        var label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        if (!exact || added + removed > 0)
        {
            var total = DiffCounts(added, removed, exact);
            total.Margin = CountsGap;
            Grid.SetColumn(total, 1);
            grid.Children.Add(total);
        }

        return grid;
    }

    private static (string Text, int Added, int Removed, bool Exact) GroupSummary(
        List<(int Index, ChatTurn Turn, string? ToolName)> turns, int callCount)
    {
        if (callCount == 0)
        {
            var first = turns[0].Turn.At;
            var last = turns[^1].Turn.At;
            return (string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupThoughtFormat"), FormatDuration(last - first)), 0, 0, true);
        }

        var calls = turns
            .Where(t => t.Turn.Role == ChatRoles.Assistant && t.Turn.ToolCallId is { Length: > 0 })
            .ToList();
        // One action gets named, because with one action the name is the whole answer and a count would hide it
        // behind a click. Two or more get counted, because the first of a hundred and twenty-seven is a sample
        // rather than a summary — naming it tells the reader this run was about <c>workspace_map</c> when it was
        // about something else entirely. The names are not lost: they are the rows, and the row order is the
        // order the model asked for them.
        var text = calls.Count > 1
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupToolCountFormat"), calls.Count)
            : DescribeToolCall(calls[0].Turn);
        var counts = AggregateDiff(calls);
        return (text, counts.Added, counts.Removed, counts.Exact);
    }

    /// <summary>The aggregate line count of the run's edits. It reads <see cref="ChatChanges.LinesOf"/> per call
    /// rather than the frozen previews alone, because a write the mode allowed without asking froze nothing and yet
    /// still moved the file — totalling only the previews made this head go silent for exactly the runs that never
    /// interrupted anybody. Nothing is opened to work it out: reading the disk to total a header would be the whole
    /// list paying for one line. When no call moved anything, the header simply carries no chip.</summary>
    private static ChangeLineCounts AggregateDiff(
        List<(int Index, ChatTurn Turn, string? ToolName)> calls)
        => ChangeLineCounts.Sum(calls.Select(call => DiffOf(call.Turn)));

    /// <summary>What one call moved, wherever it moved it. A file the tool wrote carries its counts in the changes
    /// ledger; a patch reaches the disk through a command, and its counts are the lines its own envelope spells. Both
    /// are asked here so a run of patches totals like a run of writes, and so a row's number cannot disagree with the
    /// head above it.</summary>
    private static ChangeLineCounts DiffOf(ChatTurn call)
    {
        var lines = ChatChanges.LinesOf(call);
        if (!lines.Exact || lines.Added + lines.Removed > 0) return lines;
        var patch = PatchText.ForCall(call);
        return patch is null ? new(0, 0) : new(patch.Added, patch.Removed);
    }

    /// <summary>The gap between a sentence and the numbers that follow it, and the reason it is a measurement
    /// rather than a character: a space in the string would be the first thing a trimmed label eats, and a tab in a
    /// grid column is not a tab stop — both end up as "Edit src/x.cs+3 −1", which reads as one token.</summary>
    private static readonly Thickness CountsGap = new(8, 0, 0, 0);

    /// <summary>The pair of numbers an edit is read by, in the ink the inspector already gives them: <c>+3</c> in the
    /// added tone, <c>−1</c> in the removed one, and nothing at all for a count that is zero — a write that only
    /// added must not also claim it removed nothing. The ink lives in the stylesheet beside the numbers' own class,
    /// so a count reads the same wherever it is drawn.
    ///
    /// <para>It is a label, not a button. The number is the thing being read, the way into a diff is the row of the
    /// edit itself where the fold already lives, and a plate on the head of a folded run is exactly the chrome the
    /// transcript has no room for.</para></summary>
    private static StackPanel UnavailableDiffCounts(bool budgetExceeded = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = HubStrings.Get(budgetExceeded ? "ChangeCountsBudgetExceeded" : "ChangeCountsUnavailable"), FontSize = 12,
        };
        label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        row.Children.Add(label);
        ToolTip.SetTip(row, HubStrings.Get(budgetExceeded ? "ChangeCountsBudgetDetail" : "InspectorCountsUnavailable"));
        return row;
    }

    private static StackPanel DiffCounts(int added, int removed, bool exact = true)
    {
        if (!exact) return UnavailableDiffCounts();
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "edit-counts" },
        };
        if (added > 0) row.Children.Add(new TextBlock
        {
            Classes = { "edit-count-added" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorAddedFormat"), added),
        });
        if (removed > 0) row.Children.Add(new TextBlock
        {
            Classes = { "edit-count-removed" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorRemovedFormat"), removed),
        });
        return row;
    }

    /// <summary>How tall a thinking body may get before it scrolls. The bound is the existing detail panel's, not
    /// a new number: nothing in the pipeline caps a chain of thought, and an activity group re-derives every row
    /// of itself on each arriving turn, so an unbounded block would be re-measured once per tool call for the
    /// length of a run.</summary>
    private const double ThoughtBodyMaxHeight = 200;

    /// <summary>
    /// The model's thinking as one row: a head naming it, a bounded body holding the words.
    ///
    /// <para>One builder serves the streaming bubble and the stored turn, because they are the same thing seen at
    /// two moments. Two controls is what let a reader watch forty seconds of reasoning and then land on a
    /// different widget that had forgotten it — and a card that hides when the answer starts is not the same thing
    /// as a row that folds.</para>
    ///
    /// <para>It is a row rather than a card because the transcript already has a vocabulary for machine work you
    /// can peek at: the same chevron, the same hover, the same remembered fold. Inventing a second idiom for the
    /// second kind of machine work is how you end up with a fold inside a fold.</para>
    /// </summary>
    private sealed class ThoughtRowView
    {
        internal required Border Shell { get; init; }
        internal required ToggleButton Head { get; init; }
        internal required Border Body { get; init; }
        internal required TextBlock Text { get; init; }
        internal required ScrollViewer Scroll { get; init; }
        internal required TextBlock Label { get; init; }

        /// <summary>Open or closed. The head's checkbox mirrors this rather than owning it, because a press and
        /// the answer's arrival both arrive as the same property change on that checkbox.</summary>
        internal bool Expanded { get; set; }

        /// <summary>Set only around a fold the code performs. From the first press onward nothing automatic
        /// touches the row: a reader who chose a position was using it, and the answer turning up is not a reason
        /// to take it back.</summary>
        internal bool TouchedByUser { get; set; }

        internal bool Syncing { get; set; }

        internal void SetExpanded(bool expanded)
        {
            if (Expanded == expanded) return;
            Expanded = expanded;
            Body.IsVisible = expanded;
            Syncing = true;
            try { Head.IsChecked = expanded; }
            finally { Syncing = false; }
        }
    }

    /// <summary>Lays one thinking row out. <paramref name="onUserToggle"/> fires only for a fold the reader made,
    /// which is what lets a stored row remember its fold across the group's rebuilds while the live row — which
    /// has nothing to remember it — stays out of that memory entirely.</summary>
    private ThoughtRowView BuildThoughtRow(string label, Action<bool>? onUserToggle)
    {
        var text = new TextBlock { Classes = { "turn-reasoning" } };
        var scroll = BoundedDetailScroll(text, ThoughtBodyMaxHeight);
        var body = new Border { Classes = { "activity-detail" }, IsVisible = false, Child = scroll };

        var grid = new Grid
        {
            Classes = { "activity-row", "thought" },
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-status" },
            Data = ThemeGeometry("Hub.Icon.Assistant"),
        });
        // The same cell as the mark, so the hover swap never slides the label sideways. The tool rows are built
        // this way; matching them is the point of the exercise, or the column reads as two different lists.
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-row-chevron" },
            Data = ThemeGeometry("Hub.Icon.ChevronRight"),
        });
        var labelText = new TextBlock { Classes = { "activity-row-text" }, Text = label };
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(labelText);

        var head = new ToggleButton { Classes = { "activity-row-toggle" }, Content = grid };
        var shell = new Border { Classes = { "activity-row-shell" } };
        var row = new ThoughtRowView
        {
            Text = text, Scroll = scroll, Body = body, Head = head, Label = labelText, Shell = shell,
        };
        shell.Child = new StackPanel { Spacing = 0, Children = { head, body } };

        // IsChecked, never Click — a raised Click does not flip a ToggleButton, so wiring it would leave the fold
        // both half-broken and unreadable to a check. Same reason the row shell and the group head are wired so.
        head.IsCheckedChanged += (_, _) =>
        {
            if (row.Syncing) return;
            row.TouchedByUser = true;
            var want = head.IsChecked == true;
            if (want == row.Expanded) return;
            row.Expanded = want;
            body.IsVisible = want;
            onUserToggle?.Invoke(want);
        };
        return row;
    }

    /// <summary>The head's sentence. The duration is said only when nothing above the row has already said it:
    /// inside an activity group whose own head reads 已思考 3s, a row repeating the number is one fact told twice.
    /// Out of a group — and in the live bubble, where there is no group yet — this row is the only place the
    /// number exists, so it carries it.</summary>
    private static string ThoughtLabel(TimeSpan? elapsed, bool inGroup)
        => !inGroup && elapsed is { } span
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupThoughtFormat"), FormatDuration(span))
            : HubStrings.Get("ActivityRowThought");

    /// <summary>The stored form of the row: this turn's thinking, folded as the reader left it.</summary>
    private Control? BuildThoughtRowForTurn(string conversationId, int index, ChatTurn turn, bool inGroup)
    {
        if (!_chat.ShowThinking || turn.Reasoning is not { Length: > 0 } thought) return null;

        // The thought facet belongs to this occurrence, so a rebuild or an inserted result cannot hand its
        // fold state to the turn that happens to occupy the old source index.
        var foldKey = RowKey(index, "thought");
        var row = BuildThoughtRow(
            ThoughtLabel(turn.ReasoningElapsed, inGroup),
            expanded => RememberFold(_expandedRows, conversationId, foldKey, expanded));
        row.Text.Text = thought;
        row.SetExpanded(IsFoldOpen(_expandedRows, conversationId, foldKey));
        return row.Shell;
    }

    /// <summary>One folded tool exchange wrapped in its own expander. The collapsed line names the action; clicking
    /// it reveals the whole exchange — the arguments the call was made with and the payload that came back — in a
    /// detail panel that grows downward in place. That panel is the operation's only readable surface: a hover is
    /// not one, because a trackpad cannot reach it, a check cannot read it, and a session transcript the model was
    /// handed is far too long to fit in one anyway.</summary>
    private Control BuildActivityRowShell(string conversationId, int index, ChatTurn? call, ChatTurn result)
    {
        var toggle = new ToggleButton { Classes = { "activity-row-toggle" }, Content = BuildActivityRow(call, result) };

        var detail = BuildActivityRowDetail(call, result);
        // Each landed result owns its fold, including orphan results and repeated results with the same call id.
        var foldKey = RowKey(index, "exchange");
        // Transfer an opened live command to its first landed result, once. Other results own their own folds.
        if (call is not null && _chat.ActiveConversation is { } conversation)
        {
            var callIndex = conversation.Messages.FindIndex(turn => ReferenceEquals(turn, call));
            if (callIndex >= 0)
            {
                var runningKey = RowKey(callIndex, "command");
                if (IsFoldOpen(_expandedRows, conversationId, runningKey))
                {
                    RememberFold(_expandedRows, conversationId, foldKey, true);
                    RememberFold(_expandedRows, conversationId, runningKey, false);
                }
            }
        }

        // The fold is driven off IsChecked, never Click: a raised Click does not flip a ToggleButton, so wiring
        // Click would leave this both untestable and half-broken — the same reason the group head is wired this way.
        toggle.IsCheckedChanged += (_, _) =>
        {
            var expanded = toggle.IsChecked == true;
            detail.IsVisible = expanded;
            RememberFold(_expandedRows, conversationId, foldKey, expanded);
        };
        // …and a row that was left open is restored as one, rather than being rebuilt closed over the reader's
        // choice the moment the group is folded and unfolded again.
        toggle.IsChecked = IsFoldOpen(_expandedRows, conversationId, foldKey);

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(toggle);
        if (result.Images.Count > 0)
            content.Children.Add(BuildTurnPictures(conversationId, result.Images));
        content.Children.Add(detail);

        return new Border
        {
            Classes = { "activity-row-shell" },
            Child = content,
        };
    }

    // ── the command card: the call that is working right now ──
    // A transcript used to say nothing at all until a call came back: the row it grows is drawn from a result, so
    // the whole of a ten-minute build was a status line in the live bubble and nothing a person could read. This
    // card is that missing stretch — the call itself, the directory it runs in, the state, and the shell's own
    // lines — and it is an ephemeral tail like the live bubble beside it, not a transcript row: it exists only
    // while a command is in flight and it is drawn in the same shape and classes as the row the finished exchange
    // gets, so what the person watched and what the transcript keeps are one surface in two tenses.

    /// <summary>The command card at the foot of the reply, while one is printing. One at a time because that is what
    /// the tier table allows: a command changes state, so a second one waits for the first.
    ///
    /// <para>Its panel is shut until somebody opens it, and the <c>_expandedRows</c> memory it reads and writes is
    /// transferred to the first finished result's occurrence key. That is what makes the two
    /// tenses one surface — a person who opened the output while the build ran finds it open in the transcript
    /// afterwards, and a person who left it alone gets the same one line the row would have drawn anyway.</para>
    /// </summary>
    private RunningCommandRow? _runningCommand;

    /// <summary>One command card, and the pieces a repaint has to reach without rebuilding it.</summary>
    private sealed class RunningCommandRow
    {
        public required string ConversationId { get; init; }
        public required string CallId { get; init; }
        public required Border Shell { get; init; }
        public required ToggleButton Toggle { get; init; }
        public required Border Detail { get; init; }
        public required TextBlock State { get; init; }
        public required TextBlock Elapsed { get; init; }
        public required TextBlock Output { get; init; }
        public required ScrollViewer OutputScroll { get; init; }
        public required TextBlock Dropped { get; init; }

        /// <summary>The output text last written, so a refresh that finds the same lines does not reassign a
        /// TextBlock — which would re-measure a box the person may be reading.</summary>
        public string Painted { get; set; } = "";

        /// <summary>Whether the output box follows its newest line. Its own copy of the transcript's stick rule,
        /// for the same reason the thinking box has one. Nothing is written into a shut panel, so this only starts
        /// mattering the moment somebody opens it — and from then on it keeps the box where they left it.</summary>
        public bool Stick { get; set; } = true;
    }

    /// <summary>Puts the command card at the foot of the reply, refreshes the one already there, or takes it away.
    /// Called wherever the stored rows are laid down, because they are what it has to sit under.</summary>
    private void SyncRunningCommandRow(string? conversationId, ConversationRun? run)
    {
        if (PendingCommandCall(conversationId, run) is not { } call)
        {
            ForgetRunningCommand();
            return;
        }

        var callId = call.ToolCallId!;
        if (_runningCommand is not { } card || card.ConversationId != conversationId
            || !string.Equals(card.CallId, callId, StringComparison.Ordinal))
        {
            ForgetRunningCommand();
            card = BuildRunningCommandRow(conversationId!, call);
            _runningCommand = card;
        }

        RefreshRunningCommand(card, run);
        SyncVirtualTails();
    }

    /// <summary>Drops the card. What the person chose about its panel needs no work here: that decision lives in the
    /// fold memory transferred to the finished result, so an output opened while the command printed is
    /// already open in the transcript afterwards — and one left shut stays the one line it always was.</summary>
    private void ForgetRunningCommand()
    {
        if (_runningCommand is not { } card) return;
        _runningCommand = null;
        SyncVirtualTails();
    }

    /// <summary>The command this conversation is running right now, or null.
    ///
    /// <para>Three facts have to agree before a card is drawn, and each of them is a lie avoided. The call must have
    /// no result yet, or the finished row is the surface and this one would duplicate it. It must not be waiting for
    /// a decision: an approval card is already asking, and "running" over it would say a person's click had already
    /// happened. And the run must actually own it — a stopped run or a restart leaves unanswered calls behind, and a
    /// card claiming a command is printing for a run that is gone would outlive the process it named.</para>
    /// </summary>
    private ChatTurn? PendingCommandCall(string? conversationId, ConversationRun? run)
    {
        if (conversationId is null || run is null) return null;
        var conversation = _chat.ActiveConversation;
        if (conversation is null || !string.Equals(conversation.Id, conversationId, StringComparison.Ordinal))
            return null;

        var messages = conversation.Messages;
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in messages)
        {
            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } id) answered.Add(id);
        }

        // Newest first, and an unanswered call the run is actually inside wins over a newer one that is only
        // recorded: a response can ask for two commands, both brackets open at the same gate, and the card has to
        // follow the one printing rather than the one the model happened to name last.
        ChatTurn? newest = null;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var turn = messages[index];
            if (turn.Role != ChatRoles.Assistant || turn.ToolCallId is not { Length: > 0 } callId) continue;
            if (!string.Equals(turn.ToolName, WorkspaceTools.RunCommandTool, StringComparison.Ordinal)) continue;
            if (answered.Contains(callId)) continue;
            if (turn.ApprovalState == ChatApprovalStates.Pending) return null;
            if (run.IsCallRunning(callId)) return turn;
            newest ??= turn;
        }

        // Nothing has started yet: the card is still owed, because the call is filed and the run is between a
        // request and its first tool. Outside a run there is nothing to say, and that is the answer that keeps a
        // stopped session's unanswered call from claiming a command is printing.
        return newest is not null && run.IsStreaming ? newest : null;
    }

    /// <summary>The card itself: the same row a finished exchange gets, with the state and the clock where that row
    /// keeps its chips, and the output underneath in the box the finished row's detail uses.
    /// </summary>
    private RunningCommandRow BuildRunningCommandRow(string conversationId, ChatTurn call)
    {
        var command = ArgField(call.ToolArguments, "command", "cmd") ?? "";
        var grid = new Grid
        {
            Classes = { "activity-row", "running" },
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
        };
        // The status slot holds a terminal prompt while a command is in flight, not a success or failure
        // verdict. The expander arrow trades places with it on hover in the same cell, so nothing moves sideways
        // when this row becomes a finished one.
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-status" },
            Data = ThemeGeometry("Hub.Icon.CommandPrompt"),
        });
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-row-chevron" },
            Data = ThemeGeometry("Hub.Icon.ChevronRight"),
        });

        var action = new TextBlock { Classes = { "activity-row-text" }, Text = DescribeToolCall(call) };
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        if (!string.IsNullOrWhiteSpace(command))
        {
            var oneLine = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var chip = new Border
            {
                Classes = { "file-chip", "inline-chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = oneLine.Length > CommandPreviewCharacters
                        ? oneLine[..CommandPreviewCharacters] + "…"
                        : oneLine,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
        }

        var state = new TextBlock { Classes = { "activity-row-meta" } };
        Grid.SetColumn(state, 3);
        grid.Children.Add(state);

        var elapsed = new TextBlock { Classes = { "activity-row-meta" } };
        Grid.SetColumn(elapsed, 4);
        grid.Children.Add(elapsed);

        // The detail: where the command runs, what it was asked with, and what it has said so far. The workspace is
        // the session's own root — the tool runs in it, and naming it here is what makes a command's effect on a
        // directory legible before it prints anything.
        var body = new StackPanel { Orientation = Orientation.Vertical };
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailWorkspace"),
        });
        body.Children.Add(BoundedDetailScroll(
            new TextBlock { Classes = { "activity-detail-body" }, Text = _chat.ActiveConversation?.WorkspaceRoot ?? "" },
            60));
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailInput"),
        });
        body.Children.Add(BoundedDetailScroll(FormatToolArguments(call), 120));
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailCommandOutput"),
        });
        var output = new TextBlock { Classes = { "activity-detail-body" } };
        var outputScroll = BoundedDetailScroll(output, 180);
        body.Children.Add(outputScroll);
        var dropped = new TextBlock
        {
            Classes = { "activity-detail-label" },
            IsVisible = false,
        };
        body.Children.Add(dropped);

        var detail = new Border
        {
            Classes = { "activity-detail" },
            Child = body,
        };

        var toggle = new ToggleButton { Classes = { "activity-row-toggle" }, Content = grid };
        var shell = new Border
        {
            Classes = { "activity-row-shell" },
            Child = new StackPanel { Spacing = 0, Children = { toggle, detail } },
        };

        var callId = call.ToolCallId!;
        var foldKey = RowKey(_chat.ActiveConversation!.Messages.FindIndex(turn => ReferenceEquals(turn, call)), "command");
        // Shut by default, like every other activity row: the head says what is running and where, and the output is
        // there for whoever wants it. Opening it is the person's call, not the card's — a build that pushed its log
        // under the conversation as soon as it started was the card deciding for them.
        var expanded = IsFoldOpen(_expandedRows, conversationId, foldKey);
        detail.IsVisible = expanded;
        toggle.IsChecked = expanded;
        // Driven off IsChecked, never Click, exactly as the finished row is: a raised Click does not flip a
        // ToggleButton, so a check could not drive it and half the wiring would be dead.
        toggle.IsCheckedChanged += (_, _) =>
        {
            var open = toggle.IsChecked == true;
            detail.IsVisible = open;
            // The first landed result consumes this command occurrence's choice when its row is built.
            RememberFold(_expandedRows, conversationId, foldKey, open);
        };

        var card = new RunningCommandRow
        {
            ConversationId = conversationId,
            CallId = callId,
            Shell = shell,
            Toggle = toggle,
            Detail = detail,
            State = state,
            Elapsed = elapsed,
            Output = output,
            OutputScroll = outputScroll,
            Dropped = dropped,
        };
        // Wired after the card exists, because what it records is a field of the card: a command that keeps printing
        // must not pull the box away from a person who scrolled up to read what went past.
        outputScroll.ScrollChanged += (_, _) => card.Stick =
            outputScroll.Offset.Y + outputScroll.Viewport.Height >= outputScroll.Extent.Height - StickEpsilon;
        return card;
    }

    /// <summary>Redraws one card from the run: the state, the clock, and the lines printed so far.
    ///
    /// <para>Refreshed in place rather than rebuilt because a build prints faster than a transcript can be laid
    /// down: the row, the open panel and the reading position all survive a line, and only two TextBlocks change.
    /// </para></summary>
    private void RefreshRunningCommand(RunningCommandRow card, ConversationRun? run)
    {
        var running = run?.IsCallRunning(card.CallId) == true;
        card.State.Text = HubStrings.Get(running ? "ActivityRowCommandRunning" : "ActivityRowCommandQueued");
        card.Elapsed.Text = running && run!.ToolElapsed(card.CallId) is { } spent ? FormatDuration(spent) : "";
        card.Elapsed.IsVisible = card.Elapsed.Text.Length > 0;

        IReadOnlyList<string> lines = [];
        var dropped = 0;
        if (run is not null) (lines, dropped) = run.CommandOutputOf(card.CallId);
        var text = lines.Count == 0
            ? HubStrings.Get("ActivityDetailCommandSilent")
            : string.Join("\n", lines);
        if (!string.Equals(text, card.Painted, StringComparison.Ordinal))
        {
            card.Painted = text;
            card.Output.Text = text;
            // The box follows the newest line only while the reader has left it at the bottom — a command whose
            // output pushed itself up under somebody reading the top is the one thing this box must not do. A shut
            // panel has nothing to follow and queues no work: the text still lands, so the first click shows
            // everything printed so far in one frame.
            if (card.Stick && card.Detail.IsVisible) FollowNewestLine(card.OutputScroll);
        }

        card.Dropped.Text = dropped > 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityDetailCommandDroppedFormat"), dropped)
            : "";
        card.Dropped.IsVisible = dropped > 0;
    }

    private static Border BuildActivityRowDetail(ChatTurn? call, ChatTurn result)
    {
        var body = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // A patch's arguments *are* the change, so printing them back would show the file twice and the edit
        // nowhere. The files it names are drawn as the change instead, by the same renderer the inspector reads a
        // recorded write with — a fold that showed the envelope verbatim was the one edit in the transcript that
        // could not be read as a diff.
        var patch = PatchText.ForCall(call);
        if (patch is not null)
        {
            foreach (var section in patch.Sections) body.Children.Add(BuildPatchSection(section));
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Classes = { "activity-detail-label" },
                Text = HubStrings.Get("ActivityDetailInput"),
            });
            body.Children.Add(BoundedDetailScroll(FormatToolArguments(call), 160));
        }

        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailOutput"),
        });
        body.Children.Add(BoundedDetailScroll(FormatToolResult(result), 200));

        return new Border
        {
            Classes = { "activity-detail" },
            IsVisible = false,
            Child = body,
        };
    }

    /// <summary>The unfold of an edit row: what that write moved, as the diff of the file it moved in it. It is the
    /// same change the inspector reads, drawn by the same renderer, and it stays inside the row because the fold the
    /// reader opened is the row's own — opening the right-hand pane instead would take the diff away from the line
    /// that names the file.
    ///
    /// <para>The body fills on the first expand and never before: resolving a diff opens the file, and a run of
    /// twelve writes must not read twelve files to draw twelve closed rows.</para></summary>
    private Border BuildEditRowDetail(string conversationId, ChatTurn call)
    {
        var body = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Classes = { "edit-diff" },
        };

        var detail = new Border
        {
            Classes = { "activity-detail" },
            IsVisible = false,
            Child = body,
        };

        var filled = false;
        void Fill()
        {
            if (filled) return;
            filled = true;
            if ((call.ChangeRefs.Count > 1 || call.ManagedEntries.Count > 1)
                && call.ToolCallId is { Length: > 0 } callId && _chat is not null)
            {
                var files = _chat.ChangesForCalls(_chat.ActiveConversation, new HashSet<string>(StringComparer.Ordinal) { callId });
                var list = new StackPanel { Spacing = 8 };
                foreach (var file in files)
                {
                    var item = new Expander { Header = file.RelativePath };
                    item.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == Expander.IsExpandedProperty && item.IsExpanded && item.Content is null)
                            item.Content = BuildEditDiff(conversationId, call, file);
                    };
                    list.Children.Add(item);
                }
                body.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list });
            }
            else body.Children.Add(BuildEditDiff(conversationId, call));
        }

        // The fold is driven off visibility rather than off the toggle, because opening one is not the only way it
        // opens: a row the reader left expanded is restored as one they never touched, and that path has to fill it
        // too.
        detail.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && detail.IsVisible) Fill();
        };
        return detail;
    }

    /// <summary>One write's diff, under the name of the file it moved. The header is not decoration: the fold is
    /// where a reader goes to find out what a write did, and a diff with no path over it is a change nobody can
    /// place. A revision that cannot be resolved says why in the inspector's own words rather than drawing an
    /// empty block.</summary>
    private Control BuildEditDiff(string conversationId, ChatTurn call, ChangedFile? selected = null)
    {
        var body = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 2,
        };

        var change = selected ?? (call.ToolCallId is { Length: > 0 } callId && _chat is not null
            ? _chat.ChangesForCalls(_chat.ActiveConversation, new HashSet<string>(StringComparer.Ordinal) { callId })
                .FirstOrDefault()
            : null);
        if (change is null)
        {
            body.Children.Add(new TextBlock
            {
                Classes = { "activity-detail-body" },
                Text = HubStrings.Get("InspectorDiffUnreadable"),
            });
            return body;
        }

        var diff = (string?)null;
        var state = UndoCopyState.Unreadable;
        if (_chat is not null) diff = _chat.DiffForChange(change, conversationId, out state);
        if (diff is null)
        {
            body.Children.Add(new TextBlock
            {
                Classes = { "activity-detail-body" },
                Text = HubStrings.Get(InspectorPanel.DiffUnavailableKey(change, state)),
            });
            return body;
        }

        var snapshot = _chat?.SnapshotForChange(change, change.SelectedRevisionId, out _);
        using var syntax = new ChangesDiffSyntax(change.RelativePath, diff, snapshot);
        var block = InspectorPanel.BuildUnifiedDiffBlock(diff, syntax, compact: true);
        // The bound is the fold's, not the diff's: a six-hundred-line change inside a transcript row would push
        // every row after it off the screen, and the box already scrolls both ways.
        block.MaxHeight = 320;
        body.Children.Add(block);
        return body;
    }

    /// <summary>One file of a patch, as the fold shows it: the path the envelope named, then the lines it moved in
    /// it. A move says where it goes; a delete says it is one, because the envelope spells no lines for a file it
    /// removes and an empty block would look like a failed read.</summary>
    private static Control BuildPatchSection(PatchSection section)
    {
        var card = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 2,
            Margin = new Thickness(0, 0, 0, 6),
        };

        card.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = section.MovedTo is { Length: > 0 } moved ? $"{section.Path} → {moved}" : section.Path,
        });

        if (section.Operation == PatchFileOperation.Delete)
        {
            card.Children.Add(new TextBlock
            {
                Classes = { "activity-detail-body" },
                Text = HubStrings.Get("PatchDeletedFile"),
            });
        }
        else if (section.Lines.Count > 0)
        {
            // There is no snapshot to highlight from — the envelope carries the change and never the file — so these
            // rows keep the tint and the marker and go without a lexer, rather than colouring a file nobody read.
            var diff = string.Join('\n', section.Lines);
            using var syntax = new ChangesDiffSyntax(section.Path, diff, null);
            var block = InspectorPanel.BuildUnifiedDiffBlock(diff, syntax, numbered: false);
            block.MaxHeight = 320;
            card.Children.Add(block);
        }

        return card;
    }

    /// <summary>One half of the exchange in a scroll window of its own. The bound is the point: a compiler's whole
    /// stderr or a peer session's transcript must not stretch the transcript, and clipping it silently would strand
    /// the part that matters — so the box holds still and its content scrolls inside it. Horizontal scrolling stays
    /// off, because the column is where the wrap happens.</summary>
    private static ScrollViewer BoundedDetailScroll(string text, double maxHeight)
        => BoundedDetailScroll(new TextBlock { Classes = { "activity-detail-body" }, Text = text }, maxHeight);

    /// <summary>The same window for content that is not a tool payload — a model's thinking, which wants the
    /// body's own face rather than the monospace the exchange is drawn in.</summary>
    private static ScrollViewer BoundedDetailScroll(Control content, double maxHeight) => new()
    {
        Content = content,
        MaxHeight = maxHeight,
        ClipToBounds = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>The call's arguments as a small readable block: each top-level JSON field on its own
    /// <c>key: value</c> line, strings unquoted and everything else verbatim. This is where a long command shows in
    /// full — the collapsed row's chip truncates for a glance, but the detail does not. Unparseable arguments are
    /// shown as their raw text rather than dropped.</summary>
    private static string FormatToolArguments(ChatTurn? call)
    {
        var args = call?.ToolArguments;
        if (string.IsNullOrWhiteSpace(args)) return HubStrings.Get("ActivityDetailEmpty");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(args);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return args;
            var lines = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
                lines.Add($"{property.Name}: {value}");
            }
            // An empty object parses fine but says nothing; show the raw text so the reader sees what was sent.
            return lines.Count > 0 ? string.Join("\n", lines) : args;
        }
        catch (System.Text.Json.JsonException)
        {
            return args;
        }
    }

    /// <summary>The payload the tool returned, normalized to one line ending style and capped so a compiler's whole
    /// stderr can't stretch the transcript. The cap is what the row's detail scrolls inside; nothing else in the
    /// shell shows a tool payload, so past this bound the tail is said to be missing rather than cut silently.</summary>
    private static string FormatToolResult(ChatTurn result)
    {
        const int cap = 4096;
        var text = result.Text?.Replace("\r\n", "\n").Replace("\r", "\n") ?? "";
        if (text.Length == 0) return HubStrings.Get("ActivityDetailEmpty");
        return text.Length > cap ? text[..cap] + HubStrings.Get("ActivityDetailTruncated") : text;
    }

    /// <summary>One folded tool exchange as a single descriptive row: a status mark, the action named from its
    /// call, and — for a command — the command itself trailing that verb in the same reading order. This is the
    /// collapsed line inside <see cref="BuildActivityRowShell"/>; the full arguments and payload live in the detail
    /// it reveals. The left slot carries two glyphs in one cell — the execution status at rest, and the same
    /// `>`/`v` arrow the sidebar uses, which the row's styles swap in on hover and while expanded — so the
    /// affordance sits where the eye already is rather than stranded on the right edge.</summary>
    private Control BuildActivityRow(ChatTurn? call, ChatTurn result)
    {
        var command = call?.ToolName == "run_command" ? ArgField(call.ToolArguments, "command", "cmd") : null;
        var patch = call is null ? null : PatchText.ForCall(call);
        // An edit made through a patch names its files in the verb and its size in the counts, so it does not echo
        // the command: the command line holds the patch itself, and the folded one-liner of it that used to sit
        // here was several hundred characters of envelope with nothing readable in it.
        var showsCommand = !string.IsNullOrWhiteSpace(command) && patch is null;
        // What this call moved, wherever it moved it: a write keeps its counts in the changes ledger and a patch
        // spells them in its own envelope. Reading both through one function is what keeps a row's number and the
        // total of the run above it from disagreeing about the same call.
        var moved = call is null ? new ChangeLineCounts(0, 0) : DiffOf(call);
        var showsCounts = !moved.Exact || moved.Added + moved.Removed > 0;
        // What a command ended with, when anything recorded it: the exit code and how long it ran. They trail the
        // preview rather than replacing it — one line still reads in order — and they are absent for every other
        // tool, because a read has no exit code and a duration for one answers nothing anybody asked.
        var facts = result.ToolFacts;
        var marks = facts is null ? 0 : (facts.ExitCode is null ? 0 : 1) + (facts.Elapsed is null ? 0 : 1);
        // The preview owns the star column, so it reads immediately after the verb. A star on the label is what
        // used to eat all the slack and pin the preview to the row's right edge, which made the eye cross the
        // whole column to finish the sentence. Without a preview the label keeps the star, because that is where
        // a long 「读取 <path>」 gets trimmed.
        var columns = showsCommand || showsCounts ? "Auto,Auto,*" : "Auto,*";
        for (var trailing = 0; trailing < marks; trailing++) columns += ",Auto";
        var grid = new Grid
        {
            Classes = { "activity-row" },
            ColumnDefinitions = new ColumnDefinitions(columns),
        };
        if (result.ToolFailed) grid.Classes.Add("failed");

        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-status" },
            Data = ThemeGeometry(result.ToolFailed ? "Hub.Icon.Close" : "Hub.Icon.CopySuccess"),
        });

        // Same cell as the status, so the hover swap never shifts the row's text sideways; both glyphs are 10×10.
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-row-chevron" },
            Data = ThemeGeometry("Hub.Icon.ChevronRight"),
        });

        var text = new TextBlock { Classes = { "activity-row-text" } };
        text.Text = call is not null
            ? DescribeToolCall(call)
            : FirstLine(result.Text);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (showsCommand)
        {
            var oneLine = command!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var chip = new Border
            {
                // file-chip for the look, source-chip so a check can tell these from the other chips in the same
                // panel — the run_command preview and the diff counters share the styling and nothing else.
                Classes = { "file-chip", "source-chip", "inline-chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = oneLine.Length > CommandPreviewCharacters
                        ? oneLine[..CommandPreviewCharacters] + "…"
                        : oneLine,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
        }

        // A patch's or a write's own size, in the same place and the same ink the head of a run uses: the two
        // numbers are what tells an edit of three lines from an edit of three hundred without opening either.
        if (showsCounts)
        {
            var counts = DiffCounts(moved.Added, moved.Removed, moved.Exact);
            counts.Margin = CountsGap;
            Grid.SetColumn(counts, 2);
            grid.Children.Add(counts);
        }

        // The verdict of a command, in the two numbers a person would ask for. The code is drawn as a chip like the
        // command it follows, and takes the danger ink when it is not zero: the row already carries a red mark for a
        // failed call, and a `0` in green beside one would be the row disagreeing with itself.
        var trailingColumn = showsCommand || showsCounts ? 3 : 2;
        if (facts?.ExitCode is { } exitCode && call is not null
            && string.Equals(call.ToolName, WorkspaceTools.RunCommandTool, StringComparison.Ordinal))
        {
            var exit = new Border
            {
                // inline-chip for the layout it shares with the command preview, exit-chip so a check can tell the
                // two apart in the same row.
                Classes = { "file-chip", "inline-chip", "exit-chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("ActivityRowExitCodeFormat"), exitCode),
                },
            };
            if (exitCode != 0) exit.Classes.Add("failed");
            Grid.SetColumn(exit, trailingColumn++);
            grid.Children.Add(exit);
        }

        if (facts?.Elapsed is { } elapsed)
        {
            var spent = new TextBlock
            {
                Classes = { "activity-row-meta" },
                Text = FormatDuration(elapsed),
            };
            Grid.SetColumn(spent, trailingColumn);
            grid.Children.Add(spent);
        }

        return grid;
    }

    /// <summary>How much of a command the collapsed row pre-truncates. The visual cut is the trimming's, at whatever
    /// width the column has left; this only keeps a megabyte-long one-liner out of the measure. Sized so the row
    /// still reads as one line at the chat column's 560px floor.</summary>
    private const int CommandPreviewCharacters = 96;

    private static string FirstLine(string text)
    {
        var at = text.IndexOf('\n');
        var line = (at < 0 ? text : text[..at]).Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    /// <summary>One tool call, said the way the reference says it: a verb naming the work plus the file or
    /// pattern it touched, so the row is about the action rather than the payload that came back.</summary>
    private static string DescribeToolCall(ChatTurn call)
    {
        var name = call.ToolName ?? "";
        var args = call.ToolArguments;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var path = ChatUndoStore.WritePathOf(args);
        if (path.Length == 0) path = ArgField(args, "path", "file_path", "dir") ?? "";

        return name switch
        {
            LegacyFileToolNames.ReadFile or ToolNames.FsRead when path.Length > 0
                => string.Format(culture, HubStrings.Get("ActivityRowRead"), path),
            LegacyFileToolNames.FileWrite or ToolNames.FsWrite when path.Length > 0
                => string.Format(culture, HubStrings.Get("ActivityRowEditFile"), path),
            ToolNames.FsReplace => DescribeReplacement(call),
            ToolNames.FsDelete => string.Format(culture, HubStrings.Get("ActivityRowDelete"), path),
            ToolNames.FsMove => string.Format(culture, HubStrings.Get("ActivityRowMove"),
                ArgField(args, "source") ?? "", ArgField(args, "destination") ?? ""),
            LegacyFileToolNames.SearchText or ToolNames.FsGrep
                => string.Format(culture, HubStrings.Get("ActivityRowSearch"),
                    ArgField(args, "pattern", "query") ?? ""),
            LegacyFileToolNames.FindFiles or ToolNames.FsFind
                => string.Format(culture, HubStrings.Get("ActivityRowFind"),
                    ArgField(args, "pattern", "query", "glob") ?? ""),
            LegacyFileToolNames.ListDirectory or ToolNames.FsList
                => string.Format(culture, HubStrings.Get("ActivityRowList"), path.Length > 0 ? path : "."),
            ToolNames.RunCommand => PatchText.ForCall(call) is { } patch
                ? DescribePatch(patch)
                : HubStrings.Get("ActivityRowRunCommand"),
            // Host and path only: the query is where a token hides, and this line is stored in the transcript.
            ToolNames.WebFetch => string.Format(culture, HubStrings.Get("ActivityRowFetch"),
                WebFetch.Shown(ArgField(args, "url") ?? "")),
            _ => string.Format(culture, HubStrings.Get("ActivityRowGeneric"), name),
        };
    }

    /// <summary>A patch named by what it does: one file by its path, several by the count. The wording is the
    /// wording a write gets, because how an edit reached the disk is the tool's business rather than the reader's —
    /// the same three-line change must not read as "edit" in one row and "run command" in the next.</summary>
    private static string DescribePatch(PatchSummary patch)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(patch.Sections.Count == 1 ? "ActivityRowEditFile" : "ActivityRowEditFilesFormat"),
            patch.Sections.Count == 1 ? patch.Sections[0].Path : patch.Sections.Count);

    private static string DescribeReplacement(ChatTurn call)
    {
        string[] paths = [];
        if (!string.IsNullOrWhiteSpace(call.ToolArguments))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(call.ToolArguments);
                if (doc.RootElement.TryGetProperty("edits", out var edits) && edits.ValueKind == System.Text.Json.JsonValueKind.Array)
                    paths = edits.EnumerateArray().Where(edit => edit.ValueKind == System.Text.Json.JsonValueKind.Object
                            && edit.TryGetProperty("path", out var path) && path.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(edit => edit.GetProperty("path").GetString() ?? "").Distinct(StringComparer.Ordinal).ToArray();
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (paths.Length == 0) paths = call.ChangeRefs.Select(reference => reference.Path).Distinct(StringComparer.Ordinal).ToArray();
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(paths.Length == 1 ? "ActivityRowEditFile" : "ActivityRowEditFilesFormat"),
            paths.Length == 1 ? paths[0] : paths.Length);
    }

    /// <summary>The first string-valued field among <paramref name="keys"/> from a tool's JSON arguments, or null.
    /// A malformed payload is treated as absent rather than thrown: a description is not worth failing a paint.</summary>
    private static string? ArgField(string? json, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var key in keys)
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return value.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return null;
    }

    /// <summary>The one shape every duration in this panel is written in, shared by the live bubble's counter and a
    /// folded group's title. The two spans are measured differently on purpose: the bubble reads the run's clock,
    /// which banks and stops while a person is deciding on an approval card, and the group title spans the stored
    /// turns, so it counts that decision too. A group that agreed to the second with the bubble it replaced would
    /// mean the reply suddenly got shorter the moment it finished.</summary>
    internal static string FormatDuration(TimeSpan span)
        => span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds:D2}s"
            : $"{Math.Max(0, (int)span.TotalSeconds)}s";

    /// <summary>What came back from one tool call, as a single muted line: the tool that answered, then the first
    /// line of its payload, trimmed and said to be trimmed. A result that failed is said so, because the line is
    /// otherwise the only place a refused or crashed call is visible at all.</summary>
    private static TextBlock ToolResultLine(string? toolName, ChatTurn turn)
    {
        var firstLine = turn.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lineBreak = firstLine.IndexOf('\n');
        if (lineBreak >= 0) firstLine = firstLine[..lineBreak];
        firstLine = firstLine.Trim();
        if (firstLine.Length > ResultLineCharacters) firstLine = firstLine[..ResultLineCharacters] + "…";

        var line = new StringBuilder();
        // Most of Hub's read-only tools already say which tool answered, because their result text is written for
        // a reader ("list_directory · '.agents/' · depth 2"). Prefixing that with the name again breaks the line
        // the transcript is read by.
        if (toolName is { Length: > 0 } named && !firstLine.StartsWith(named + " ", StringComparison.Ordinal))
            line.Append(toolName).Append(" → ");
        line.Append(firstLine.Length > 0 ? firstLine : "—");

        // A refused or crashed call has no other trace once the card is gone, and the danger ink is the one the
        // shell already uses for it — same metrics as muted, so the line never re-wraps.
        var block = new TextBlock
        {
            Classes = { turn.ToolFailed ? "danger" : "muted", "tool-result-line" },
            Text = line.ToString(),
        };
        return block;
    }

    /// <summary>How much of a result's first line is worth a glance. Long enough to read a path or a status,
    /// short enough that the line stays one line at the chat column's width.</summary>
    private const int ResultLineCharacters = 120;

    private static void AddApprovalDetail(StackPanel card, string labelKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        card.Children.Add(new TextBlock { Classes = { "approval-label" }, Text = HubStrings.Get(labelKey) });
        card.Children.Add(new TextBlock
        {
            Classes = { "approval-detail" },
            Text = value,
            MaxHeight = 180,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static Button CloseActionButton(Action onClick)
    {
        var glyph = new TextBlock
        {
            Text = "×",
            FontSize = 15,
            Width = 15,
            Height = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The app's own UI stack rather than a bare "Segoe UI": this glyph has to come from a font that is there
        // on every host, and the stack is the one place that decision is written down.
        glyph.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("Hub.Font.Ui"));
        glyph.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var button = new Button { Content = glyph, Tag = "Cancel" };
        button.Classes.Add("message-action");
        button.Classes.Add("message-action-icon");
        ToolTip.SetTip(button, HubStrings.Get("Cancel"));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The decided call, still in the record and no longer actionable: what became of it is the only
    /// thing worth the space — and, for a write, the one exit it has while a copy is left behind it.</summary>
    private Control BuildCallRecord(string conversationId, string callId, ChatTurn turn, int index)
    {
        // A write that never had to ask still changed the file, so it gets a line of its own naming the file
        // rather than the tool: who was asked is a permission detail, what moved is the record.
        var asked = turn.ToolName == AskUserTool.ToolName;
        var line = ((turn.ChangeId is { Length: > 0 } && (ChatChanges.IsSingleFileWrite(turn.ToolName)
                       || turn.ToolName == ToolNames.FsReplace)) || turn.ManagementBatchId is { Length: > 0 }) ? DescribeToolCall(turn)
            : turn.ApprovalState is { Length: > 0 } state
            ? $"{HubStrings.Get(state switch
                {
                    ChatApprovalStates.Approved => "ChatApprovalResolvedApproved",
                    ChatApprovalStates.Denied => "ChatApprovalResolvedDenied",
                    ChatApprovalStates.Answered => "ChatQuestionResolved",
                    // A question nobody answered was still asked, so "not executed" is not a fact about it — the
                    // thing that did not happen was the reply.
                    ChatApprovalStates.Superseded when asked => "ChatQuestionResolvedSuperseded",
                    _ => "ChatApprovalResolvedSuperseded",
                })} · {turn.ToolName ?? ""}"
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWriteRecordFormat"), ChatUndoStore.WritePathOf(turn.ToolArguments));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if ((ChatChanges.IsSingleFileWrite(turn.ToolName) || turn.ToolName == ToolNames.FsReplace || ChatChanges.IsManagement(turn.ToolName))
            && turn.ApprovalState != ChatApprovalStates.Denied
            && (turn.ChangeId is { Length: > 0 } || turn.UndoName is { Length: > 0 }
                || turn.ManagementBatchId is { Length: > 0 }))
        {
            var completed = new Avalonia.Controls.Shapes.Path
            {
                Data = ThemeGeometry("Hub.Icon.CopySuccess"),
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform,
                Fill = Brushes.Transparent,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
            };
            completed.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.Success"));
            row.Children.Add(completed);
        }
        row.Children.Add(new TextBlock
        {
            Classes = { "approval-record-text" },
            Text = line,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return BuildEditRecord(conversationId, callId, turn, RowKey(index, "write"), row);
    }

    /// <summary>A landed write, as the row that can open its own diff. The record line stays the row's face —
    /// whether anybody was asked is still part of what happened — and the numbers of what moved sit at its right
    /// edge, the same <c>+N −M</c> the run above it totals, read from this call rather than from the run. Its own
    /// diff opens under it, so a reader who wants to see the change never has to leave the line that names the file
    /// for the right-hand pane. A call that moved no file keeps the plain record line: it has nothing to unfold.</summary>
    private Control BuildEditRecord(string conversationId, string callId, ChatTurn turn, string foldKey,
        StackPanel line)
    {
        var record = new Border { Classes = { "approval-record" }, Child = line };
        if (_chat is null
            || _chat.ChangesForCalls(_chat.ActiveConversation, new HashSet<string>(StringComparer.Ordinal) { callId })
                .Count == 0)
            return record;

        var face = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        face.Classes.Add("activity-row");
        face.Classes.Add("edit");
        face.Children.Add(record);
        var counts = DiffOf(turn);
        if (!counts.Exact || counts.Added > 0 || counts.Removed > 0)
        {
            var numbers = DiffCounts(counts.Added, counts.Removed, counts.Exact);
            numbers.Margin = CountsGap;
            foreach (var label in numbers.Children.OfType<TextBlock>()) label.FontSize = 11;
            Grid.SetColumn(numbers, 1);
            face.Children.Add(numbers);
        }

        var toggle = new ToggleButton { Classes = { "activity-row-toggle" }, Content = face };
        var detail = BuildEditRowDetail(conversationId, turn);
        // Each write owns its fold, keyed by the occurrence it answers: two writes to one file are two rows, and
        // opening one must not open the other.
        toggle.IsCheckedChanged += (_, _) =>
        {
            var expanded = toggle.IsChecked == true;
            detail.IsVisible = expanded;
            RememberFold(_expandedRows, conversationId, foldKey, expanded);
        };
        toggle.IsChecked = IsFoldOpen(_expandedRows, conversationId, foldKey);

        return new Border
        {
            Classes = { "activity-row-shell" },
            Child = new StackPanel { Spacing = 0, Children = { toggle, detail } },
        };
    }

    /// <summary>Hands one decision to the workspace. A refusal is spoken out loud rather than swallowed: the card
    /// would otherwise sit there having done nothing, which reads as a broken button. <paramref name="alwaysAllowVerb"/>
    /// is the third answer, the one that grants a single git verb instead of the whole tool.</summary>
    private void ResolveApproval(string conversationId, string callId, bool approved, bool alwaysAllow,
        bool alwaysAllowVerb = false)
    {
        if (_chat.TryResolveApproval(conversationId, callId, approved, alwaysAllow, out var refusalKey,
                alwaysAllowVerb)) return;
        AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
    }

    private void ResolvePlanApproval(string conversationId, int turnIndex, string decision)
    {
        if (!_chat.TryResolvePlanApproval(conversationId, turnIndex, decision, out var refusalKey))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
            return;
        }

        // Revision goes through the ordinary composer send, not a decision that consumes the plan first.
        ConversationStateChanged?.Invoke();
    }

    /// <summary>
    /// Builds one message row. Both row kinds are a single Border: the user row's child column right-aligns
    /// its pill, the assistant row's carries borderless plain text. One container type keeps the hover-reveal
    /// rules in the markup to two instead of four, and both carry the message-row class and, when
    /// <paramref name="index"/> is non-null, a hover-revealed action row.
    /// A null <paramref name="index"/> (the live streaming bubble / just-sent user pill) carries no
    /// action bar: there is nothing stable to act on until the turn is persisted.
    /// </summary>
    private Control BuildMessageRow(
        bool fromUser, StackPanel body, int? index, string role, string text, bool isLast, DateTimeOffset? at = null,
        bool isGoal = false)
    {
        var column = new StackPanel { Spacing = 6 };

        if (fromUser)
        {
            column.Children.Add(new Border
            {
                Classes = { "user-pill" },
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = body,
            });
        }
        else
        {
            column.Children.Add(body);
        }

        if (index is { } messageIndex)
        {
            var actions = BuildActionBar(messageIndex, role, text, isLast, at, body, isGoal);
            actions.HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            column.Children.Add(actions);
        }

        // One container type for both row kinds. The hover-reveal rules in the markup key on
        // Border.message-row, and having two container types meant writing every reveal twice; a user row is
        // now a Border whose child column right-aligns the pill, which is all the Grid ever added.
        // MessageRows selects by the message-row class rather than by type, so nothing downstream notices.
        var row = new Border { ClipToBounds = true, Child = column };
        row.Classes.Add("message-row");
        row.Classes.Add(fromUser ? "user-row" : "assistant-msg");
        return row;
    }

    private Control BuildActionBar(int index, string role, string text, bool isLast, DateTimeOffset? at, StackPanel body,
        bool isGoal = false)
    {
        var actionSession = _chat.ViewedConversationId;
        var actionKey = _projection!.KeyAt(index);
        int CurrentIndex() => actionSession == _chat.ViewedConversationId && _projection is not null
            && _projection.TryResolve(actionKey, out var current) ? current : -1;
        async Task<bool> CopyCurrentAsync()
        {
            var current = CurrentIndex();
            if (current < 0 || _chat.ActiveConversation is not { } conversation) return false;
            var turn = conversation.Messages[current];
            var text = turn.PlanApprovalState is not null ? _chat.PlanBody(turn, out _) ?? "" : turn.Text;
            return await CopyMessageToClipboardAsync(conversation.Id, turn, text);
        }
        void RegenerateCurrent()
        {
            var current = CurrentIndex();
            if (current >= 0 && _chat.ActiveConversation is { } conversation && current == conversation.Messages.Count - 1)
                RegenerateAsync();
        }
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Classes.Add("message-actions");

        if (role == ChatRoles.User && at is { } timestamp)
            bar.Children.Add(BuildMessageTimestamp(timestamp));

        if (_chat.ActiveConversation is { } replyConversation && replyConversation.Id == actionSession
            && index < replyConversation.Messages.Count
            && replyConversation.Messages[index] is { Text.Length: > 0, ToolCallId: null, PlanApprovalState: null } replyTurn
            && replyTurn.Role is ChatRoles.User or ChatRoles.Assistant)
            bar.Children.Add(IconActionButton("ReplyToMessage", "Hub.Icon.Reply", () =>
            {
                var current = CurrentIndex();
                if (current >= 0) SetReplyTarget(current);
            }));

        if (role == ChatRoles.User)
        {
            bar.Children.Add(CopyMessageActionButton(CopyCurrentAsync));
            if (isGoal)
                bar.Children.Add(new TextBlock
                {
                    Classes = { "message-timestamp", "goal-sent-marker" },
                    Text = HubStrings.Get("ChatGoalSentAsGoal"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                });
            bar.Children.Add(IconActionButton("EditMessage", "Hub.Icon.Edit", () => { if (CurrentIndex() is var current && current >= 0) BeginInlineEdit(current, body, bar); }));
            // A question that never got an answer is otherwise stuck: the notice that explained the failure is
            // not written to the transcript, and resubmitting the same text is refused as an unchanged edit. The
            // same ↻ that re-answers a reply re-asks this one, because Regenerate drops nothing when the
            // trailing turn is the question. It stops being the trailing turn the moment an answer lands, so
            // the button leaves on its own.
            if (isLast && ViewedRun is null)
                bar.Children.Add(IconActionButton("RetryMessage", "Hub.Icon.Refresh", RegenerateCurrent));
        }
        else
        {
            bar.Children.Add(CopyMessageActionButton(CopyCurrentAsync));
            if (isLast)
                bar.Children.Add(IconActionButton("RegenerateMessage", "Hub.Icon.Refresh", RegenerateCurrent));
            bar.Children.Add(IconActionButton("BranchFromHere", "Hub.Icon.Branch", () => { if (CurrentIndex() is var current && current >= 0) BranchFromHere(current); }));
            if (_chat.ActiveConversation is { } active && active.Id == actionSession
                && index >= 0 && index < active.Messages.Count
                && active.Messages[index] is { GitActions: not GitActions.None,
                    GitWorkspaceRoot: { Length: > 0 } root } gitTurn)
            {
                void AddGitButton(GitActions action, string textKey)
                {
                    if (!gitTurn.GitActions.HasFlag(action)) return;
                    var button = new Button
                    {
                        Content = HubStrings.Get(textKey),
                        Tag = textKey,
                        Classes = { "quiet", "message-action" },
                        Padding = new Thickness(8, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    ToolTip.SetTip(button, action == GitActions.Commit ? gitTurn.CommitSummary
                        : HubStrings.Get("GitPushAuthorizationTip"));
                    button.Click += async (_, _) =>
                    {
                        var current = CurrentIndex();
                        if (current < 0 || _chat.ActiveConversation is not { } conversation
                            || conversation.Id != actionSession || current >= conversation.Messages.Count) return;
                        var currentTurn = conversation.Messages[current];
                        if (currentTurn.At != gitTurn.At || !currentTurn.GitActions.HasFlag(action)) return;
                        button.IsEnabled = false;
                        try
                        {
                            var outcome = action == GitActions.Commit
                                ? await _chat.CommitSuggestedAsync(actionSession!, root, currentTurn.CommitSummary ?? "", currentTurn.At)
                                : await _chat.PushSuggestedAsync(actionSession!, root, currentTurn.CommitSummary ?? "", currentTurn.At,
                                    commitFirst: action == GitActions.CommitAndPush);
                            StatusMessage?.Invoke(outcome.Message);
                        }
                        finally { button.IsEnabled = true; }
                    };
                    bar.Children.Add(button);
                }
                AddGitButton(GitActions.Commit, "CommitSummaryAction");
                AddGitButton(GitActions.Push, "PushSummaryAction");
                AddGitButton(GitActions.CommitAndPush, "CommitPushSummaryAction");
            }
        }

        if (role == ChatRoles.Assistant && at is { } assistantTimestamp)
            bar.Children.Add(BuildMessageTimestamp(assistantTimestamp));

        if (role == ChatRoles.User && actionSession is not null && _inlineDrafts.ContainsKey((actionSession, actionKey)))
        {
            bar.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_inlineDrafts.ContainsKey((actionSession, actionKey)) && CurrentIndex() is var current && current >= 0
                    && body.Children.FirstOrDefault() is not TextBox)
                    BeginInlineEdit(current, body, bar);
            }, DispatcherPriority.Loaded);
        }
        return bar;
    }

    private static TextBlock BuildMessageTimestamp(DateTimeOffset timestamp)
    {
        var localTimestamp = timestamp.ToLocalTime();
        var relative = new TextBlock { Text = FormatRelativeTime(DateTimeOffset.Now - localTimestamp) };
        relative.Classes.Add("message-timestamp");
        ToolTip.SetTip(relative, localTimestamp.ToString(
            "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture));
        return relative;
    }

    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        if (seconds < 60) return HubStrings.Get("MessageTimeJustNow");
        if (seconds < 3600)
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageTimeMinutesAgo"),
                (int)elapsed.TotalMinutes);
        if (seconds < 86400)
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageTimeHoursAgo"),
                (int)elapsed.TotalHours);

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("MessageTimeDaysAgo"),
            (int)elapsed.TotalDays);
    }

    /// <summary>One icon in a message's action row. The size and pen are per-glyph because a cross is two
    /// strokes meeting in the middle: at the weight an outline icon carries it, the crossing fills its plate
    /// and reads as one solid mark.</summary>
    private static Button IconActionButton(
        string textKey, string geometryKey, Action? onClick, double size = 13, double thickness = 1.7)
    {
        var icon = geometryKey == "Hub.Icon.Edit" ? HubActionIcon.Create(geometryKey) : new Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        if (geometryKey != "Hub.Icon.Edit")
        {
            icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension(geometryKey));
            icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        }

        var button = new Button { Content = icon, Tag = textKey };
        button.Classes.Add("message-action");
        button.Classes.Add("message-action-icon");
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        if (onClick is not null)
            button.Click += (_, _) => onClick();
        return button;
    }

    private Button CopyMessageActionButton(Func<Task<bool>> copy)
    {
        var button = IconActionButton("CopyMessage", "Hub.Icon.Copy", null);
        var icon = (Avalonia.Controls.Shapes.Path)button.Content!;
        var feedbackVersion = 0;
        button.Click += async (_, _) =>
        {
            if (!await copy()) return;
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

    /// <summary>
    /// The last thing the app had to say about each conversation, the run it was said about, and how many of the
    /// person's own messages had been sent when it was said. A notice is the tail of the transcript until either of
    /// those two facts stops holding — which is what keeps it surviving a rebuild of that transcript without
    /// outliving the news it carried.
    ///
    /// <para>It is keyed by conversation rather than a single slot because a run does not end in step with what is
    /// on screen: the reason a session stopped while the person read another one is still news when they come
    /// back, and holding it in one shared field would either lose it or paint it onto the wrong transcript.</para>
    ///
    /// <para>The run is the half the message count cannot stand in for. A continuation — an approved call, an
    /// answered question, a ↻ — starts a fresh run and appends no message of the person's, so a stop pressed on the
    /// round before it would otherwise go on answering a round that is already arriving. That is a rule about what
    /// a run said; a folder verdict or an attachment refusal is news about the conversation rather than about the
    /// round that ended, and keeps the older lifetime.</para>
    ///
    /// <para><c>Retryable</c> marks the endings where the same round is worth asking for again — a timeout, a
    /// stalled stream, a refused connection. Those carry the ↻ on the line itself, because the alternative was a
    /// sentence telling the person to retry and the only way to do it hidden on the last message row, revealed on
    /// hover, three pixels from a notice that is not part of the transcript.</para>
    /// </summary>
    private readonly Dictionary<string, (string Text, bool Danger, int OwnTurns, bool Retryable, bool Continuable,
        bool FromRun, ConversationRun? Run)> _notices = new(StringComparer.Ordinal);

    private void AppendNotice(string text, bool danger)
    {
        var viewed = _chat.ActiveConversation;
        RememberNotice(viewed?.Id ?? "", text, danger,
            viewed?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0);
        RenderNotice();
        ScrollToEnd();
    }

    /// <summary>Files what a run or an action had to say for one conversation. The line reaches the screen through
    /// <see cref="RenderNotice"/>, which paints only the conversation being looked at.</summary>
    private void RememberNotice(string conversationId, string text, bool danger, int ownTurns,
        bool retryable = false, bool continuable = false, bool fromRun = false, ConversationRun? run = null)
        => _notices[conversationId] = (text, danger, ownTurns, retryable, continuable, fromRun, run);

    /// <summary>Which endings are worth asking for again. Everything on this list is a round that never got an
    /// answer through no fault of the question: a window ran out, the stream stopped, the connection or the limit
    /// got in the way, the model closed the response with nothing in it. A refusal the person made (ChatCancelled,
    /// an approval that went away) is not on the list — pressing ↻ after saying "no" would ask for the same thing
    /// they just declined. One rule, read both by the ending as it arrives and by the stored line as it is painted
    /// back, so a retry button cannot appear at one door and be missing at the other.</summary>
    private static bool NoticeIsRetryable(string key)
        => key is "ChatNoFirstFrame" or "ChatTimedOut" or "ChatTimedOutAfterRetries"
            or "ChatStreamInterrupted" or "ChatConnectionFailed" or "ChatRateLimited"
            or "ChatFailed" or "ChatNoResponse" or "ChatReasoningOnly" or "ChatUndeclaredTool";

    private static bool NoticeIsContinuable(string key) => key == "ChatResponseTruncated";

    /// <summary>What the strip below the transcript says. Two painters rather than one, because the stored notice
    /// can decline to speak — it ages out — while a compaction is only ever happening or over.</summary>
    private void RenderNotice()
    {
        RenderStoredNotice();
        RenderCompactingRow();
        SyncVirtualTails();
    }

    /// <summary>Adds the line a compaction owns, while one runs and no bubble is on screen. Read from the workspace
    /// rather than remembered here, so a rebuild of the transcript paints it again for as long as it is true — the
    /// reason this strip lives outside the flow at all. A run gets the same fact on its own status line instead:
    /// saying it in both places would be one thing said twice.</summary>
    private void RenderCompactingRow()
    {
        var conversationId = _chat.ActiveConversation?.Id;
        if (conversationId is null || !_chat.IsCompressingContext(conversationId)) return;
        // A run on this same session already says it on its own status line; a bubble belonging to another
        // session is not this session's news, and must not silence the strip.
        if (_live?.Run.ConversationId == conversationId) return;
        NoticeHost.Children.Add(BuildNoticeRow(HubStrings.Get("ChatCompactingContext"), danger: false,
            actionName: null, actionIcon: null, action: null));
    }

    /// <summary>Paints the stored notice, if it still is news: same conversation, nothing of the person's said
    /// since, and — for the news a run brought — no newer run begun. Called from the render path, so a rebuild
    /// cannot erase it and a new message cannot be answered by an old warning. With no conversation at all — the
    /// model list just emptied, say — there is nothing that can age it out, so the line stands until the next one
    /// replaces it.</summary>
    private void RenderStoredNotice()
    {
        NoticeHost.Children.Clear();
        var viewed = _chat.ActiveConversation;
        var conversationId = viewed?.Id ?? "";
        // A notice is news about a conversation; when that conversation is gone the line has no subject left, so
        // it leaves with it rather than keeping a key nothing will ever view again. The sidebar list is not the
        // test for "gone" — a session nobody has spoken in holds no row there, and that is exactly the session an
        // attachment refusal gets filed on.
        foreach (var stale in _notices.Keys
                     .Where(id => id.Length > 0 && id != conversationId
                                  && !_chat.Conversations.Any(session => session.Id == id))
                     .ToList())
            _notices.Remove(stale);
        var ownTurns = viewed?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0;
        if (!_notices.TryGetValue(conversationId, out var notice))
        {
            // Nothing in memory for the session on screen, so read back how its last run ended. This is the half
            // that makes the difference observable: a line that lives only in a dictionary is a line that
            // disappears on a conversation switch, and "the model returned no content" is news somebody may still
            // want to act on after they go and come back. Never while a round is in flight — the newest ending
            // owns the strip, and a stored one must not speak over a reply that is arriving.
            if (viewed?.LastNotice is not { } stored || _chat.RunFor(conversationId) is not null
                || ownTurns != stored.UserTurns) return;
            notice = (HubStrings.Get(stored.Key) + (stored.Detail ?? ""), stored.Danger, stored.UserTurns,
                NoticeIsRetryable(stored.Key), NoticeIsContinuable(stored.Key), false, null);
        }
        else if (notice.FromRun && _chat.RunFor(conversationId) is { } current
                 && !ReferenceEquals(current, notice.Run))
        {
            // The registry is the authority on which round this conversation is in. A finished run leaves it
            // before the notice is filed, so a stored null and a live instance both mean "a newer round has
            // begun" — and a continuation that begins one without adding a message of the person's is exactly the
            // case the count below cannot see. Only a run's own news is measured this way; what an action said
            // about the conversation outlives the round it was said in.
            _notices.Remove(conversationId);
            return;
        }

        if (ownTurns != notice.OwnTurns)
        {
            _notices.Remove(conversationId);
            return;
        }

        var actionName = notice.Continuable ? "ContinueTruncated" : notice.Retryable ? "RetryMessage" : null;
        var actionIcon = notice.Continuable ? "Hub.Icon.Send" : notice.Retryable ? "Hub.Icon.Refresh" : null;
        Action? action = notice.Continuable ? ContinueTruncatedAsync : notice.Retryable ? RegenerateAsync : null;
        NoticeHost.Children.Add(BuildNoticeRow(notice.Text, notice.Danger, actionName, actionIcon, action));
    }

    private static Grid BuildNoticeRow(string text, bool danger, string? actionName, string? actionIcon, Action? action)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*" + (action is null ? "" : ",Auto")) };
        row.Classes.Add("notice");
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.MaxWidth = 820;
        if (danger) row.Classes.Add("danger");

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry("Hub.Icon.Notice"),
        };
        icon.Classes.Add("notice-icon");
        // The row declares "Auto,*", and a child without an explicit column lands in column 0 — both of
        // them there meant the text sat on top of the icon, which in a real run read like "the tip is
        // smudged over something". Icon owns column 0, text column 1.
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var label = new TextBlock { Text = text };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        // The same button the last message row uses, built by the same helper: the tooltip, the hover reveal and
        // the glyph's offset inside its own plate are already settled there, and a second copy of an action button
        // is how the notice ends up looking like somebody else's widget.
        if (action is not null && actionName is { Length: > 0 } name
            && actionIcon is { Length: > 0 } actionGlyph)
        {
            var button = IconActionButton(name, actionGlyph, action);
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }

        return row;
    }

    // ───────────────────────── Actions ─────────────────────────

    private async Task<bool> CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return false;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var data = new DataTransfer();
        data.Add(item);
        try
        {
            await clipboard.SetDataAsync(data);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> CopyMessageToClipboardAsync(string conversationId, ChatTurn turn, string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return false;
        var images = new List<ChatMessageClipboardImage>();
        var bitmaps = new List<Bitmap>();
        try
        {
            foreach (var image in turn.Images)
            {
                var bytes = ReadPictureBytes(_chat.StoredImagePath(conversationId, image.File));
                if (bytes is not { Length: > 0 }) continue;
                try
                {
                    var bitmap = new Bitmap(new System.IO.MemoryStream(bytes));
                    bitmaps.Add(bitmap);
                    images.Add(new ChatMessageClipboardImage(image, bytes));
                }
                catch (Exception)
                {
                    // An unavailable or undecodable attachment must not prevent the message text from being copied.
                }
            }

            var payload = new ChatMessageClipboardPayload(text, conversationId, images);
            var textItem = new DataTransferItem();
            textItem.Set(DataFormat.Text, text);
            textItem.Set(ChatMessageClipboard.Format, ChatMessageClipboard.Serialize(payload));
            var data = new DataTransfer();
            data.Add(textItem);
            foreach (var bitmap in bitmaps) data.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
            await clipboard.SetDataAsync(data);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    /// <summary>
    /// Keeps the round button in step with the composer: the same button sends or stops, and it stays
    /// greyed out while the box is empty so "there is nothing to send" is visible before the click rather than
    /// after it.
    /// </summary>
    private void UpdateSendState()
    {
        SendStateUpdates++;
        // A decision on the card above does not own this button any more: the run it belongs to is parked, so
        // IsViewedStreaming is false and the button reads as an ordinary send of whatever is in the box — which is
        // exactly what pressing it does.
        var streaming = IsViewedStreaming;
        var hasText = (InputBox.Text ?? "").Trim().Length > 0;
        var hasPicture = _pendingPictures.Count > 0;
        // A picture in the chip row is as much "there is something to send" as text is, and it says so in the
        // tooltip too: while a reply is streaming, sending this means steering it rather than stopping it.
        var saysSomething = hasText || hasPicture;
        // A steer already waiting is spent: the button must not offer a second one before the first is taken.
        SendButton.IsEnabled = ViewedRun?.HasQueuedSteer != true && (streaming || saysSomething);
        SendButton.Content = BuildSendIcon(streaming && !saysSomething);
        ToolTip.SetTip(SendButton, HubStrings.Get(streaming
            ? saysSomething ? "ChatSteer" : "Stop"
            : "Send"));
        UpdateContextRing();
    }

    /// <summary>Builds the arrow (send) or square (stop) glyph. Colours are bound as DynamicResource rather
    /// than looked up once because this runs from the constructor, before the control is attached — a brush
    /// captured there comes back null and the glyph would be invisible.</summary>
    private static Control BuildSendIcon(bool streaming)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
        };
        path.Data = ThemeGeometry(streaming ? "Hub.Icon.Stop" : "Hub.Icon.Send");

        if (streaming)
        {
            // The stop square is solid; the fill is what makes it read differently from the arrow.
            path.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new DynamicResourceExtension("Hub.TextOnAccent"));
            return path;
        }

        path.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextOnAccent"));
        path.StrokeThickness = 2;
        path.StrokeLineCap = PenLineCap.Round;
        path.StrokeJoin = PenLineJoin.Round;
        return path;
    }

    private static Geometry? ThemeGeometry(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) ? value as Geometry : null;

    /// <summary>Highlights the composer frame while the input has focus so the whole rounded box reads as the
    /// thing being typed into.</summary>
    private void SetComposerFocus(bool focused)
    {
        ComposerFrame.Classes.Set("focused", focused);
        // Leaving the composer is the point where re-arranging the column is safe again, so the reply the
        // deferred ticks held back paints here (and the timer resumes on its own once focus is gone).
        if (!focused) FlushLiveRepaintIfPending();
    }

    /// <summary>Whether Up is free to take the key: the box is empty, or the person is already part-way through a
    /// recall. Anything else is a caret moving inside a draft, which is the text box's business and not this one.</summary>
    private bool CanRecall() => _recallIndex > 0 || (InputBox.Text ?? "").Length == 0;

    /// <summary>Steps through what has been sent. Down at the newest end lands back on an empty box, which is the
    /// only way out of the walk that does not throw away the sentence being looked at.</summary>
    private void RecallText(bool backwards)
    {
        _recallIndex = backwards
            ? Math.Min(_recallIndex + 1, _sentTexts.Count)
            : Math.Max(_recallIndex - 1, 0);
        if (_sentTexts.Count == 0) return;

        var picked = _recallIndex == 0 ? "" : _sentTexts[^_recallIndex];
        _recalling = true;
        InputBox.Text = picked;
        _recalling = false;
        InputBox.CaretIndex = picked.Length;
        UpdateSendState();
    }

    /// <summary>Records a message that really left the composer. A send that was refused, and a steer that lost
    /// the race to a finished run, both keep the draft out of here — history is what was said, not what was typed.</summary>
    private void RememberSentText(string text)
    {
        if (text.Length == 0) return;
        _sentTexts.Add(text);
        if (_sentTexts.Count > MaxRecalledTexts) _sentTexts.RemoveAt(0);
        _recallIndex = 0;
    }

    private async Task SendAsync()
    {
        CapturePlanDraftTarget();
        if (_planDraftTarget is { } target
            && (target.ConversationId != _chat.ViewedConversationId
                || target.Token != ChatWorkspace.PendingPlanToken(_chat.ActiveConversation)))
        {
            ReportPlanDraftChanged();
            return;
        }
        var sourceConversationId = _chat.ViewedConversationId;
        var expectedPlanToken = _planDraftTarget?.Token;
        if (ViewedRun is { IsStreaming: true } run)
        {
            if (run.HasQueuedSteer) return;
            var steerText = (InputBox.Text ?? "").Trim();
            var steerPictures = PendingPictureBytes();
            if (steerText.Length > 0 || steerPictures is { Count: > 0 })
            {
                string? steerContext;
                try
                {
                    steerContext = await ReadAttachmentContextAsync();
                }
                catch (Exception ex)
                {
                    AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
                    return;
                }

                // A steer is the routing table's strongest signal and it joins a reply already on its way, yet
                // until now it looked exactly like an ordinary send. The first Enter therefore only shows what
                // would be injected; the second one (or the strip's button) is the steer itself. It waits for the
                // step in flight rather than cancelling it.
                ShowSteerConfirm(steerText, steerContext, steerPictures);
                return;
            }

            _chat.RequestStop(run.ConversationId);
            return;
        }

        var text = (InputBox.Text ?? "").Trim();
        var pictures = PendingPictureBytes();
        var reusedPictures = PendingReusedPictures();
        // A picture with no question under it is a message: "look at this" is what people actually send, and a
        // send button that stays grey because the box is empty would say otherwise.
        if (text.Length == 0 && _pendingPictures.Count == 0) return;
        if (_chat.SelectedChatModel is null)
        {
            // One sentence, one door: the pre-check and the refusal behind <c>TryEnqueueSend</c> both come through
            // <see cref="NoticeFor"/>, so a session missing its own model says its name here and there alike.
            AppendNotice(NoticeFor("NoAvailableChatModels", _chat.ActiveConversation?.Id), danger: true);
            return;
        }

        string? context;
        try
        {
            context = await ReadAttachmentContextAsync();
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
            return;
        }

        // Attachment reads can yield. Never send into a different session or silently attach feedback to
        // a different plan after the await; keep both text and pictures on every refusal.
        if (sourceConversationId != _chat.ViewedConversationId)
        {
            ReportPlanDraftChanged();
            return;
        }
        if (!SendTextAsync(text, context, pictures, expectedPlanToken, _replyToId, reusedPictures)) return;
        ClearReplyTarget();
        _contextAttachments.Clear();
        _pendingPictures.Clear();
        RenderContextAttachments();
        InputBox.Text = "";
    }

    /// <summary>Hands the message to the workspace and starts a run for it. The user's own turn is painted from
    /// the transcript like any other row, so there is no second copy of it here to keep in step. Reports whether
    /// the message got in, because the caller owns the draft and must not empty the box on a refusal.</summary>
    private bool SendTextAsync(string text, string? attachedContext = null, IReadOnlyList<byte[]>? pictures = null,
        string? expectedPlanToken = null, string? replyToId = null, IReadOnlyList<ChatImage>? reusedImages = null)
    {
        if (text.Length == 0 && pictures is not { Count: > 0 } && reusedImages is not { Count: > 0 }) return false;
        var isGoal = GoalDraftSelectedForActiveConversation;
        if (_chat.ActiveConversation is null)
        {
            _startingGoalConversationForSend = isGoal;
            _chat.StartConversation();
            _startingGoalConversationForSend = false;
            if (isGoal) _goalDraftConversationId = _chat.ActiveConversation?.Id;
        }
        if (_chat.ActiveConversation is not { } conversation) return false;

        if (!_chat.TryEnqueueSend(conversation.Id, text, attachedContext, pictures, isGoal, out var refusalKey,
                expectedPlanToken, replyToId, reusedImages))
        {
            if (refusalKey == "ChatPlanChanged") ReportPlanDraftChanged();
            else AppendNotice(NoticeFor(refusalKey, conversation.Id), danger: true);
            return false;
        }

        RememberSentText(text);
        if (isGoal)
        {
            ClearGoalDraftSelection();
            RefreshComposerChoices();
        }
        ConversationStateChanged?.Invoke();
        return true;
    }

    /// <summary>Asks for the same round again. Nothing new is appended — the workspace rebuilds the request from
    /// the transcript, so the question that went unanswered goes back out as the turn it already was. The notice
    /// that explained the failure is retired here rather than by the turn-count rule, because a continuation adds
    /// no turn of the person's and the old line would otherwise sit above an answer that is now arriving.</summary>
    private void RegenerateAsync()
    {
        if (_chat.ActiveConversation is not { } conversation) return;
        if (IsViewedStreaming)
        {
            // The button is only built while nothing is answering, so reaching here means a run started
            // between the click and this line. A refusal the user can read beats a button that appears dead.
            AppendNotice(HubStrings.Get("ChatSessionBusy"), danger: true);
            return;
        }

        if (!_chat.Regenerate(conversation.Id)) return;

        ForceRebuildMessages();
        if (!_chat.TryEnqueueContinuation(conversation.Id, out var refusalKey,
                ToolApprovalResults.NotAnswered))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatFailed"), danger: true);
            return;
        }

        ForgetNotice(conversation.Id);
    }

    /// <summary>Turns an explicit continuation click into a user turn, so the model is told to continue the
    /// partial answer instead of silently treating the truncated stream as complete or replaying the same prompt.</summary>
    private void ContinueTruncatedAsync()
    {
        if (_chat.ActiveConversation is not { } conversation) return;
        if (IsViewedStreaming)
        {
            AppendNotice(HubStrings.Get("ChatSessionBusy"), danger: true);
            return;
        }
        if (!SendTextAsync(HubStrings.Get("ContinueTruncatedPrompt"))) return;
        ForgetNotice(conversation.Id);
    }

    /// <summary>Drops the notice a retryable ending filed on one conversation, and repaints if that conversation is
    /// the one on screen. Only a <b>retryable</b> line goes: an informational notice is the tail of the transcript
    /// until the person speaks again, or a newer run begins where the news came from a run, and a rebuild — or a ↻ pressed for another reason — must
    /// not erase it as collateral, which is a rule this suite already holds. The line offering this button is the
    /// exception because it is the answer to the button: the failure it describes is the thing now being retried.
    /// A run notice that is not retryable needs no call here — <see cref="RenderNotice"/> ages it out the moment the
    /// conversation has a different run. Only the viewed session is redrawn — a background session's news never
    /// moves the reader's position.</summary>
    private void ForgetNotice(string conversationId)
    {
        if (!_notices.TryGetValue(conversationId, out var notice) || (!notice.Retryable && !notice.Continuable)) return;
        _notices.Remove(conversationId);
        if (conversationId != _chat.ViewedConversationId) return;
        RenderNotice();
    }

    /// <summary>Forks the conversation at this message into a fresh session and switches to it. Refused while
    /// a reply is streaming, because the history is still growing and the cut point would not be the one that
    /// was clicked. The message flow and the sidebar both repaint through the workspace's Changed event.</summary>
    private void BranchFromHere(int index)
    {
        if (_chat.ActiveConversation is not { } conversation || IsViewedStreaming) return;
        _chat.BranchFrom(conversation.Id, index);
    }

    /// <summary>
    /// Puts a user message back into an editable box where it stands, and swaps the row's hover actions for
    /// cancel and confirm so both exits are visible instead of something to remember. Enter commits,
    /// Shift+Enter adds a line, Escape or a click anywhere outside abandons the edit.
    ///
    /// Committing drops everything after this turn. That used to be gated by a confirmation dialog that only
    /// appeared once the retyping was already done; the edit icon's tooltip carries the consequence now, at
    /// the moment the edit is chosen instead.
    /// </summary>
    private void BeginInlineEdit(int index, StackPanel body, StackPanel bar)
    {
        if (IsViewedStreaming || body.Children.FirstOrDefault() is not TextBlock original) return;

        var editSession = _chat.ViewedConversationId;
        var editKey = _projection!.KeyAt(index);
        var untouched = original.Text ?? "";
        var draftId = (editSession!, editKey);
        var normalActions = bar.Children.ToList();
        var closed = false;
        var editor = new TextBox
        {
            Text = _inlineDrafts.GetValueOrDefault(draftId) ?? untouched,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = original.FontSize,
        };
        editor.Classes.Add("message-edit");
        _inlineDrafts[draftId] = editor.Text ?? "";
        editor.PropertyChanged += (_, change) =>
        {
            if (!closed && change.Property == TextBox.TextProperty) _inlineDrafts[draftId] = editor.Text ?? "";
        };

        void Close()
        {
            if (closed) return;
            closed = true;
            _inlineDrafts.Remove(draftId);
            body.Children.Remove(editor);
            body.Children.Insert(0, original);
            bar.Children.Clear();
            foreach (var action in normalActions) bar.Children.Add(action);
        }

        void Commit()
        {
            if (closed) return;
            var edited = (editor.Text ?? "").Trim();
            var conversation = _chat.ActiveConversation;
            if (edited.Length == 0 || edited == untouched
                || IsViewedStreaming || conversation is null || conversation.Id != editSession || _projection is null
                || !_projection.TryResolve(editKey, out var currentIndex)
                || !_chat.EditAndResend(conversation.Id, currentIndex, edited))
            {
                Close();
                return;
            }

            // The rebuild throws this whole subtree away, so there is nothing left to restore.
            closed = true;
            _inlineDrafts.Remove(draftId);
            ForceRebuildMessages();
            if (!_chat.TryEnqueueContinuation(conversation.Id, out var refusalKey))
                AppendNotice(HubStrings.Get(refusalKey ?? "ChatFailed"), danger: true);
        }

        body.Children.Remove(original);
        body.Children.Insert(0, editor);
        bar.Children.Clear();
        bar.Children.Add(CloseActionButton(Close));
        bar.Children.Add(IconActionButton("ConfirmEdit", "Hub.Icon.Tick", Commit));
        editor.Focus();
        editor.CaretIndex = editor.Text?.Length ?? 0;

        // Registered on the tunnel for the same reason the composer is: a TextBox with AcceptsReturn marks
        // Enter handled in order to insert a newline, so a plain subscriber never sees the key.
        editor.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                Commit();
            }
        }, RoutingStrategies.Tunnel);

        // Clicking away abandons the edit — but not when focus only moved to this editor's own two buttons,
        // which have to be allowed to receive the click, and not when the window itself lost focus, which
        // would discard a draft nobody meant to drop.
        editor.LostFocus += (_, e) =>
        {
            if (e.NewFocusedElement is not Visual next || ReferenceEquals(next, editor)) return;
            if (next.GetLogicalAncestors().Any(ancestor
                    => ReferenceEquals(ancestor, bar) || ReferenceEquals(ancestor, body))) return;
            Close();
        };
    }

    /// <summary>
    /// The bubble for the reply now arriving in this session, created when the session was not on screen a
    /// moment ago. Everything on it is read off the run — the text that arrived while it was hidden, the calls it
    /// has been through, the time it has been taking — so switching away and back shows the same reply the user
    /// left, not one restarted from zero.
    /// </summary>
    private LiveBubble AttachLive(ConversationRun run)
    {
        if (_live is { } existing && existing.Run == run)
        {
            RefreshLive(existing);
            return existing;
        }

        CloseLive();
        // The thinking first, so it reads above the answer the way it arrived. It starts collapsed because most
        // replies never think at all, and an empty row would be chrome describing nothing. No fold memory is
        // passed: this row belongs to one answer in flight, and the stored turn's row takes over from it.
        var thought = BuildThoughtRow(ThoughtLabel(null, inGroup: false), onUserToggle: null);
        thought.Shell.IsVisible = false;
        // The answer's own slot is a panel rather than a run of text: every finished Markdown block is built once
        // and then left alone, and only the one still being typed is replaced, at the bottom of the stack.
        var prose = new StackPanel { Spacing = 8 };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(thought.Shell);
        body.Children.Add(prose);
        var activity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        activity.Classes.Add("chat-activity");
        // The glyph replaces the three blinking dots: it draws the phase the run is actually in rather than a
        // generic wait, and it stops its own clock the moment it is hidden or the phase needs no motion.
        var glyph = new ActivityGlyph();
        glyph.Bind(ActivityGlyph.TrackBrushProperty, new DynamicResourceExtension("Hub.Border"));
        glyph.Bind(ActivityGlyph.DotBrushProperty, new DynamicResourceExtension("Hub.Accent"));
        activity.Children.Add(glyph);
        var status = new TextBlock { Text = HubStrings.Get("ChatPreparing") };
        status.Classes.Add("chat-activity-label");
        activity.Children.Add(status);
        activity.Children.Add(new TextBlock
        {
            Text = "·",
            Classes = { "chat-activity-elapsed" },
        });
        // Seeded from the run rather than started at "0s": a bubble rebuilt a minute into a reply has to say a
        // minute, and it should say it on the frame it appears, not after the next tick.
        var elapsed = new TextBlock
        {
            Text = FormatDuration(run.Elapsed),
            Classes = { "chat-activity-elapsed" },
        };
        activity.Children.Add(elapsed);
        body.Children.Add(activity);

        var bubble = new LiveBubble
        {
            Run = run,
            Row = BuildMessageRow(false, body, null, ChatRoles.Assistant, "", isLast: true),
            Prose = prose,
            Thought = thought,
            Status = status,
            Elapsed = elapsed,
            Glyph = glyph,
            ToolRunning = run.IsToolRunning,
        };
        // The thinking body follows its newest line only while the reader has left it at the bottom — the same
        // rule the transcript applies to the whole column, and for the same reason. A box that always follows is a
        // box nobody can read the top of, and a position somebody took is not ours to take back.
        bubble.Thought.Scroll.ScrollChanged += (_, _) => bubble.ThoughtStick =
            bubble.Thought.Scroll.Offset.Y + bubble.Thought.Scroll.Viewport.Height
            >= bubble.Thought.Scroll.Extent.Height - StickEpsilon;
        // A wait already in progress is the reason this reply is late, and the event that said so fired while the
        // session was out of sight: read the fact off the run rather than wait for it to happen again.
        if (run.RetryWaiting) status.Text = RetryActivityText(run.RetryWaitNotice());
        bubble.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        bubble.Timer.Tick += (_, _) =>
        {
            if (_live is not { } current) return;
            current.Frame++;
            current.Elapsed.Text = FormatDuration(current.Run.Elapsed);
            RenderComposerMetrics();
            RefreshGlyphPhase(current);
            // The command card's clock ticks with the bubble's rather than on one of its own: it is a number about
            // the same run, and a second timer would only be a second thing to stop.
            if (_runningCommand is { } printing) RefreshRunningCommand(printing, current.Run);
        };
        _live = bubble;
        bubble.Timer.Start();
        RefreshLive(bubble);
        UpdateSendState();
        ScrollToEnd();
        return bubble;
    }

    /// <summary>Throws the bubble away, not the reply: the run keeps streaming and a later attach reads the text,
    /// the tool count and the elapsed time back off it. The timer goes with the row, because nothing is being
    /// animated any more — and it is only the animation, never the clock the answer is measured by.</summary>
    private void CloseLive()
    {
        if (_live is not { } live) return;
        live.Timer?.Stop();
        live.Timer = null;
        // The bubble is about to be the only record of what streaming cost, and it is thrown away here.
        _lastLiveBuilds = live.Builds;
        _live = null;
        SyncVirtualTails();
        _liveRepaintPending = false;
        UpdateSendState();
        UpdateScrollAffordance();
    }

    private void RefreshLive(LiveBubble bubble)
    {
        // Read off the run the bubble is attached to, not looked up by session id: the bubble and its run are the
        // same pairing from attach to detach, so a refresh can never land on a reply that started afterwards.
        var text = bubble.Run.LiveText;
        var thought = bubble.Run.LiveReasoning;

        // Painting the arriving text re-measures the whole message column, and the sticky scroll that follows it
        // flips the column width, re-wrapping every completed bubble — both re-arrange the focused composer and
        // interrupt an open Linux composition, so the caret jumps. Defer just those two while the user composes;
        // the thinking glyph keeps running (cheap, and the suite guards against freezing it), and run.LiveText
        // accrues server-side, so nothing is lost — focus loss flushes the held-back paint.
        // A held thumb needs the same protection an open composition gets: a repaint re-measures the message
        // column (see PaintStreamingMarkdown), and a column that changes height under the hand moves the Track's
        // mapping mid-drag, which is a thumb that shrinks back from one hand and drifts off the other. Held back
        // the same way, flushed the same way, when the hand lets go.
        if (DeferringLiveRepaint || _thumbDragging)
        {
            if (bubble.PaintedText != text || (bubble.Thought.Text.Text ?? "") != thought) _liveRepaintPending = true;
            RefreshGlyphPhase(bubble);
            UpdateScrollAffordance();
            return;
        }

        _liveRepaintPending = false;
        PaintStreamingMarkdown(bubble, text, QuoteIntoComposer);
        bubble.PaintedText = text;
        if (text.Length == 0) bubble.ShowedText = false;
        else if (!bubble.ShowedText)
        {
            bubble.ShowedText = true;
            bubble.Status.Text = HubStrings.Get("ChatGenerating");
        }

        // The row appears the first time a run thinks and does not leave again while the answer is on screen: it
        // folds when the prose starts rather than vanishing, because nobody who watched forty seconds of reasoning
        // asked for it to be taken away the moment the result turned up.
        //
        // A tool loop thinks before every round, though, and showing it each time would flash the card above the
        // group a hundred and twenty-seven times in a run that made that many calls. So one run gets one live
        // view — unless a hand has opened the row, which is a standing request to keep seeing it. The later rounds
        // are not a dead screen either: the glyph and the elapsed line keep moving, and their words go straight to
        // the row inside the group.
        var showRow = _chat.ShowThinking && thought.Length > 0
                      && (!bubble.Run.ThoughtFiledBefore || bubble.Thought.TouchedByUser);
        bubble.Thought.Shell.IsVisible = showRow;
        if (showRow)
        {
            bubble.Thought.Text.Text = thought;
            // The same sentence the stored row will carry, and off the same clock — which is the point of
            // recording the duration at all: the number the reader watched must still be there when the bubble is
            // thrown away and the real turn takes its place.
            bubble.Thought.Label.Text = ThoughtLabel(bubble.Run.ReasoningElapsed, inGroup: false);
            // Open while it is the only thing arriving, folded once the answer speaks — and never folded against
            // a reader who moved it. This is the same boundary the status line and the glyph turn on, so the
            // three cannot disagree about which phase the run is in.
            if (!bubble.Thought.TouchedByUser) bubble.Thought.SetExpanded(!bubble.ShowedText);
            if (bubble.Thought.Expanded && bubble.ThoughtStick) FollowNewestLine(bubble.Thought.Scroll);
        }

        RefreshGlyphPhase(bubble);
        ScrollToEndIfSticky();
        UpdateScrollAffordance();
    }

    /// <summary>How often the block still being typed is rebuilt. Every chunk would be a rebuild per frame —
    /// a fresh document, a fresh parse, a fresh code editor for each fence — and the transcript is not
    /// virtualized, so each one re-measures the whole column. At this interval the answer keeps up with a fast
    /// model while a long reply costs a handful of small builds a second.</summary>
    private const long OpenBlockMilliseconds = 120;

    /// <summary>
    /// Lays the arriving answer down as Markdown, building each finished block exactly once.
    ///
    /// <para>The whole reply is never re-rendered. <see cref="MarkdownMessageRenderer.SettlePoint"/> says how
    /// much of it is made of blocks that can no longer change; those are appended and then never touched again,
    /// and only the open block underneath them is replaced — and only when its text actually moved and the
    /// throttle has expired. A frame that arrives inside the same block therefore does no building at all.</para>
    ///
    /// <para>The open block is rendered from a repaired copy, so a fence or an emphasis the model has not closed
    /// yet still reads as formatting rather than as markup. The run's own buffer is never touched: what gets
    /// filed in the transcript is what was actually said.</para>
    ///
    /// <para>Static because a bubble is painted from the run's own progress, not from the panel's state — so the
    /// one thing a painted block still needs from the panel, where a 引用 from its right-click menu goes, arrives
    /// as <paramref name="quote"/> rather than being reached for.</para>
    /// </summary>
    private static void PaintStreamingMarkdown(LiveBubble bubble, string text, Action<string>? quote = null)
    {
        // A run clears its buffer between tool-loop rounds, so a bubble that survives the switch can be handed a
        // shorter string than the one it last drew from — and a longer one that merely starts the same is the
        // same answer only by luck. Anything that is no longer a prefix of what is on screen belongs to a reply
        // already filed in the transcript, and this slot starts over.
        if (bubble.Settled > text.Length || !text.StartsWith(bubble.SettledSource, StringComparison.Ordinal))
        {
            bubble.Prose.Children.Clear();
            bubble.Settled = 0;
            bubble.SettledSource = "";
            bubble.OpenBlockSource = "";
        }

        var settled = MarkdownMessageRenderer.SettlePoint(text);
        var force = false;
        if (settled > bubble.Settled)
        {
            // The open block's slot is about to be filled by the finished version of itself. Whatever the repair
            // had guessed at is discarded and the real text, closed or not, is laid down once.
            DropOpenBlock(bubble);
            var finished = text[bubble.Settled..settled];
            bubble.Prose.Children.Add(MarkdownMessageRenderer.Render(finished, quote));
            bubble.Builds++;
            bubble.Settled = settled;
            bubble.SettledSource += finished;
            force = true;
        }

        var open = text[bubble.Settled..];
        if (open == bubble.OpenBlockSource) return;
        // The throttle bounds how often a block that is <i>still growing</i> gets rebuilt. An empty remainder is a
        // removal, and a removal builds nothing, so it never waits: holding it back for one window left the last
        // segment's sentence painted in the bubble beside the transcript row that had just taken it over, which
        // read as the model saying the same thing twice — and since a parked tool emits no further delta, nothing
        // came after to correct it until the next round spoke.
        if (open.Length > 0 && !force && bubble.OpenBlockSource.Length > 0
            && Environment.TickCount64 - bubble.OpenBlockPaintedAt < OpenBlockMilliseconds) return;

        DropOpenBlock(bubble);
        if (open.Length > 0)
        {
            bubble.Prose.Children.Add(
                MarkdownMessageRenderer.Render(MarkdownMessageRenderer.CloseOpenConstructs(open), quote));
            bubble.Builds++;
        }
        bubble.OpenBlockSource = open;
        bubble.OpenBlockPaintedAt = Environment.TickCount64;
    }

    /// <summary>Takes the still-being-typed block out of the stack. It is always the last child when there is
    /// one, because finished blocks only ever go underneath nothing.</summary>
    private static void DropOpenBlock(LiveBubble bubble)
    {
        if (bubble.OpenBlockSource.Length == 0 || bubble.Prose.Children.Count == 0) return;
        bubble.Prose.Children.RemoveAt(bubble.Prose.Children.Count - 1);
        bubble.OpenBlockSource = "";
    }

    /// <summary>Takes a thinking body to its newest line, after this frame has measured it. Posted rather than
    /// done inline because the text was assigned a moment ago and the extent is not known until layout runs — the
    /// same reason the transcript's own follow is posted at the same priority. The two then land in one pass
    /// instead of racing over it, and neither one re-measures anything the frame has not measured already.</summary>
    private static void FollowNewestLine(ScrollViewer scroll)
        => Dispatcher.UIThread.Post(
            () => scroll.Offset = new Point(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)),
            DispatcherPriority.Background);

    /// <summary>Paints the reply the deferred ticks were holding back, once it is safe to re-arrange the column:
    /// the composer just lost focus (so no composition is open) or the live row is being torn down.</summary>
    private void FlushLiveRepaintIfPending()
    {
        if (!_liveRepaintPending) return;
        _liveRepaintPending = false;
        if (_live is { } live) RefreshLive(live);
    }

    /// <summary>
    /// Puts the glyph in the phase the run is actually in, and hides it entirely once prose is arriving or the
    /// run is parked: the prose and the decision host are already saying those two, and a waiting symbol
    /// beside them would be chrome repeating a fact.
    /// </summary>
    private static void RefreshGlyphPhase(LiveBubble bubble)
    {
        var phase = bubble.ShowedText
            ? ActivityPhase.ShowingText
            : bubble.ToolRunning
                ? ActivityPhase.RunningTool
                : ActivityPhase.WaitingFirstToken;

        // One call sets the phase and the visibility and re-arms the clock together, so a refresh that only
        // confirms "still waiting" cannot leave the glyph visible but frozen (see ActivityGlyph.ApplyPhase).
        bubble.Glyph.ApplyPhase(phase);
    }

    /// <summary>Says how a run ended, on the transcript of the session that ended — not on whichever one happens
    /// to be on screen. The reason is recorded where it happened rather than inferred here from exception types,
    /// which is how "the user pressed stop" and "the stream stalled" used to blur together.</summary>
    private void AppendRunNotice(string conversationId, RunOutcome outcome)
    {
        // Which endings are worth asking for again is one rule, read the same way here and when a stored line is
        // painted back (<see cref="NoticeIsRetryable"/>).
        if (outcome.NoticeKey is { } key)
            RememberNotice(conversationId, HubStrings.Get(key) + (outcome.Detail ?? ""),
                outcome.NoticeDanger, outcome.UserTurns, NoticeIsRetryable(key), NoticeIsContinuable(key),
                // The run that just ended has already left the registry, so this reads null: the line belongs to a
                // round that is over, and the next one to appear retires it however that round began.
                fromRun: true, run: _chat.RunFor(conversationId));

        // A session that finished out of sight is filed and left alone: repainting or scrolling it would move
        // the reading position of the conversation the person is actually looking at.
        if (conversationId != _chat.ViewedConversationId) return;
        RenderNotice();
        ScrollToEnd();
    }

    private ConversationRun? ViewedRun
        => _chat.ActiveConversation is { } viewed ? _chat.RunFor(viewed.Id) : null;

    private bool IsViewedStreaming => ViewedRun is { IsStreaming: true };

    // ───────────────────────── Scrolling ─────────────────────────

    private void ScrollToEnd()
    {
        _stickToBottom = true;
        _pendingTailFollow = true;
        _repairFailures = 0;
        QueueVirtualFollow();
    }

    private void ScrollToEndIfSticky()
    {
        if (_stickToBottom && !_thumbDragging) QueueVirtualFollow();
    }

    private void UpdateScrollAffordance()
    {
        var extent = MessageScroller.Extent.Height;
        var viewport = MessageScroller.Viewport.Height;
        var atBottom = MessageScroller.Offset.Y + viewport >= extent - StickEpsilon && TranscriptTailVisible();
        // Stickiness belongs to the person, not to the extent: a reply growing the transcript while they are at
        // the bottom must not read as "they scrolled away". Gestures set it in their own handlers; a real scroll
        // change is interpreted by the ScrollChanged handler above.
        var scrollable = TranscriptActuallyScrollable();
        ScrollToBottomButton.IsVisible = scrollable && !atBottom;
        ScrollAttentionMarquee.IsActive = scrollable && !atBottom && IsViewedStreaming;
        // The band's height is reserved by whether the transcript <i>can</i> scroll, never by where the reading
        // position happens to have stopped. The plate inside it does appear and disappear with the reading position,
        // and measured without the reservation the row grows from 22 to 28 as it rises — retaking from the
        // transcript the height a person is reading through, every time the bottom edge is crossed. Reserving on
        // capacity pays for that once, when content first overflows, and never again while the session is read.
        ComposerBand.MinHeight = scrollable && !double.IsNaN(ScrollToBottomButton.Height)
            ? ScrollToBottomButton.Height
            : 0;
    }

    private void UpdateMessageColumnWidth()
    {
        // Hub's ScrollBar is opaque, always visible once the transcript overflows, and painted over the content
        // it belongs to rather than given a column of its own (Theme/HubControls.axaml; InspectorPanel and
        // MarkdownMessageRenderer clear it the same way on their own surfaces). A column measured against the raw
        // viewport therefore keeps its last bar-width — the user pill's tail, an activity row's trailing chip —
        // behind the track. Measure the strip the reader can see instead; the inset is the same on both sides, so
        // the column stays concentric with the composer (ChatPanel.axaml carries the matching gutter).
        var viewportWidth = MessageScroller.Viewport.Width;
        if (viewportWidth <= 0) return;

        var width = Math.Min(MessageColumn.MaxWidth,
            Math.Max(0, viewportWidth - (2 * HubMetrics.ScrollBarThickness)));
        if (width <= 0)
        {
            // A zero-width column is an invisible transcript. Keep the last usable width and let the coverage
            // repair re-measure once the real viewport is back.
            RequestTranscriptRepair("width-zero");
            return;
        }
        if (double.IsNaN(MessageColumn.Width) || Math.Abs(MessageColumn.Width - width) > 0.1)
        {
            MessageColumn.Width = width;
            foreach (var row in _realizedTranscript.Values) row.Host.Width = width;
            // Re-wrapping every row changes their heights; the tail may now sit below where the viewport stopped.
            RequestTranscriptRepair("width");
        }
    }

    // ───────────────────────── Self-check hooks ─────────────────────────

    /// <summary>Message rows are Borders carrying the message-row class (both row kinds, since the container
    /// was unified); the class is what the hover-reveal rules and every row-counting check key on. An opened
    /// fold is entered — a row the person can read without clicking anything is a row of the transcript — while
    /// a folded one realizes no rows at all, so nothing has to be skipped by hand.</summary>
    private static bool IsMachinePanel(Control child) => child.Classes.Contains("activity-group");

    private IEnumerable<Control> MessageRows => MessageRowsOf(RealizedTranscriptRows);

    private static IEnumerable<Control> MessageRowsOf(IEnumerable<Control> rows)
        => rows.SelectMany(child => IsMachinePanel(child)
            ? child.GetLogicalDescendants().OfType<Control>().Where(row => row.Classes.Contains("message-row"))
            : Enumerable.Repeat(child, 1).Where(row => row.Classes.Contains("message-row")));

    /// <summary>Whether any Markdown viewer on screen was handed this text. Read as the negative it is: a leaked
    /// tool call has to reach the reader as plain text, and the one proof of that is that no viewer ever took
    /// it — because a viewer is exactly where its pipes turn into a table.</summary>
    internal bool HasMarkdownHolding(string text)
        => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>()
            .Any(viewer => viewer.Markdown?.Contains(text, StringComparison.Ordinal) == true);

    /// <summary>How many times the send button's state has been recomputed. A check asserts it grows when the
    /// input text changes, which is what proves the change notification is actually wired up.</summary>
    internal int SendStateUpdates { get; private set; }

    private sealed record ContextAttachment(string Kind, string Name, string Path, string? Details = null);

    /// <summary>
    /// One picture the composer is holding. Bytes rather than a path because two of the three entrances have no
    /// file to point at — a pasted capture arrives as a bitmap, and what all three have in common is the bytes
    /// Hub would store.
    /// </summary>
    private sealed record PendingPicture(byte[] Bytes, string Name, ChatImage? ReusedAttachment = null,
        string? SourceConversationId = null);

    internal double BubbleIconActionWidth(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("message-action-icon"))?.Bounds.Width ?? -1;
private int RealizedItemCount=> _realizedTranscript.Count;
private int MarkdownDocumentCount=> RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Count();
private List<Control> RealizedTranscriptRows    {
        get
        {
            if (!_reconcilingTranscript)
                MessageFlow.UpdateLayout();
            return _realizedTranscript.Values.OrderBy(row => row.Item.Ordinal).SelectMany(row => row.Host.Children).ToList();
        }
    }
}
