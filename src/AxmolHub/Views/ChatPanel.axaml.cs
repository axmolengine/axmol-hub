using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
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
using ColorTextBlock.Avalonia;

namespace AxmolHub;

public partial class ChatPanel : UserControl
{
    /// <summary>How many of the newest assistant turns are rendered as full Markdown. Older turns fall back
    /// to plain selectable text. Every Markdown turn carries a MarkdownScrollViewer plus an AvaloniaEdit
    /// highlighter, so an unbounded count is what makes a long session expensive; bounding it keeps the
    /// heavy controls proportional to what a user actually reads rather than to the session's total length.
    /// The limit is high enough that short sessions (all of the self-check fixtures) render everything.</summary>
    private const int EagerMarkdownLimit = 40;

    /// <summary>How close to the bottom counts as "the user is at the bottom". A few pixels of slack stop
    /// sub-pixel scroll rounding from flipping the state on every scroll event.</summary>
    private const double StickEpsilon = 6;
    private readonly ChatWorkspace _chat;
    private readonly List<ContextAttachment> _contextAttachments = [];

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

    private string? _selectedComposerMode;
    private bool _stickToBottom = true;
    private Task? _contextCompressionTask;

    /// <summary>
    /// The bubble showing the reply arriving in the session on screen, or null while none is. Everything it
    /// displays is read off the run, so leaving and coming back rebuilds it from what actually arrived — the text,
    /// the tools it has been through, and the time it has been taking — instead of from a copy this view kept,
    /// which is also what lets a session keep streaming while it is hidden.
    /// </summary>
    private LiveBubble? _live;

    /// <summary>One attached run's worth of chrome: the row, the pieces of it that change while text arrives, and
    /// the timer that animates them. Created on attach, thrown away on detach; the run outlives both and carries
    /// every fact the chrome displays, so nothing here survives a detach and nothing here needs to.</summary>
    private sealed class LiveBubble
    {
        public required ConversationRun Run { get; init; }
        public required Control Row { get; init; }
        public required TextBlock Preview { get; init; }
        public required TextBlock Status { get; init; }
        public required TextBlock Elapsed { get; init; }
        public required ActivityGlyph Glyph { get; init; }
        public DispatcherTimer? Timer { get; set; }
        public int Frame { get; set; }

        /// <summary>How many tool calls this reply has finished, and whether one is in flight right now. Both come
        /// off the run this bubble is attached to — seeded when a switch rebuilds it, and advanced by
        /// <see cref="ChatWorkspace.ToolActivityChanged"/>, which is the only place the view learns that a call
        /// started or came back. The glyph's node count is exactly this number.</summary>
        public int ToolCount { get; set; }
        public bool ToolRunning { get; set; }

        /// <summary>Whether this segment has produced any text yet — the status line says "preparing" until it
        /// has, and a steer starts a new segment, so the flag has to be able to go back.</summary>
        public bool ShowedText { get; set; }
    }

    /// <summary>The conversation whose turns <see cref="MessageFlow"/> currently shows, and how many of its
    /// visible turns have already been laid down. Together they let <see cref="RenderMessages"/> append only what
    /// is new instead of tearing down and rebuilding the whole flow on every change. The prefix is counted in
    /// <i>turns</i>, not in rows: a group of machine turns paints fewer rows than it consumes — a plain call
    /// draws none, its result draws one between them — and a row count cannot point back into the transcript.</summary>
    private string? _renderedConversationId;
    private int _renderedCount;

    /// <summary>The per-turn states as they were when the flow was laid down. An approval decision — and a spent
    /// undo copy — rewrite what a turn that is already on screen looks like without changing how many turns there
    /// are, which is precisely what counting cannot see.</summary>
    private string _renderedApprovalStamp = "";

    /// <summary>Which turn was sitting in the last painted slot. Counting can see a tail append and can see the
    /// history shrink, but it cannot see a turn <i>inserted</i> before the end — and a tool result is filed beside
    /// the call it answers, which is an insert whenever other calls from the same response ran first. That shift
    /// used to paint the shifted tail twice and skip the inserted row entirely. The slot's own identity is enough:
    /// an insert anywhere before it moves a different turn into it.</summary>
    private string _renderedTailKey = "";

    /// <summary>The activity group whose run is still going, when one is: the machine turns that arrive next
    /// belong to it, so it grows where it stands instead of the flow being rebuilt around it. See
    /// <see cref="DeriveActivityGroup"/>.</summary>
    private ActivityGroupView? _openGroup;

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

    /// <summary>Drops the fold choices of sessions that no longer exist. Deleting a conversation raises
    /// <see cref="ChatWorkspace.Changed"/>, so this runs on the very repaint that follows it and the two sets
    /// never outlive the conversations they name.</summary>
    private void PruneFoldMemory()
    {
        if (_expandedGroups.Count == 0 && _expandedRows.Count == 0) return;
        var alive = _chat!.Conversations.Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _expandedGroups.Keys.Where(id => !alive.Contains(id)).ToArray()) _expandedGroups.Remove(gone);
        foreach (var gone in _expandedRows.Keys.Where(id => !alive.Contains(id)).ToArray()) _expandedRows.Remove(gone);
    }

    private static string SlotKey(ChatTurn turn)
        => turn.At.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + ":" + turn.Role + ":" + (turn.ToolCallId ?? "") + ":" + turn.Text.Length;

    private static string ApprovalStamp(Conversation? conversation)
    {
        if (conversation is null) return "";
        var stamp = new StringBuilder();
        foreach (var turn in conversation.Messages)
        {
            if (turn.ApprovalState is null && turn.UndoName is null && turn.PlanApprovalState is null) continue;
            stamp.Append(turn.ToolCallId).Append(':').Append(turn.ApprovalState ?? "")
                .Append('/').Append(turn.UndoName ?? "")
                .Append('/').Append(turn.PlanApprovalState ?? "").Append(';');
        }

        return stamp.ToString();
    }

    /// <summary>Raised when the active conversation or its title may have changed, so the shell can update
    /// its top-bar title (the panel no longer owns a title of its own).</summary>
    internal event Action? ConversationStateChanged;

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
        // Tool activity already arrives on the UI thread, and it carries the session it happened in: a reply
        // running out of sight must not rewrite the status line of the one on screen.
        _chat.ToolActivityChanged += (conversationId, name, completed) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = ToolActivityText(name, completed);
            // The glyph's node count is the number of calls this reply has finished; a call in flight is what
            // moves the phase off "waiting for a first token".
            if (completed)
            {
                live.ToolCount++;
                live.ToolRunning = false;
            }
            else live.ToolRunning = true;
            RefreshGlyphPhase(live);
        };
        _chat.RunTextChanged += conversationId =>
        {
            if (_live is { } live && live.Run.ConversationId == conversationId) RefreshLive(live);
        };
        // A search the endpoint ran for itself: no card, no call row, nothing the person can act on — so the one
        // status line the bubble already has is where the fact belongs. Same session guard as the tool row above,
        // for the same reason: a reply running out of sight must not rewrite the line of the one on screen.
        _chat.ServerSearchChanged += (conversationId, notice) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = SearchActivityText(notice);
        };
        _chat.RunsChanged += conversationId =>
        {
            if (conversationId == _chat.ViewedConversationId) RenderMessages();
        };
        _chat.RunCompleted += (conversationId, outcome) =>
        {
            if (conversationId != _chat.ViewedConversationId) return;
            RenderMessages();
            AppendRunNotice(outcome);
            UpdateSendState();
            ConversationStateChanged?.Invoke();
        };
        ModelPicker.Click += (_, _) => ShowModelMenu();
        ForkNotice.Click += (_, _) =>
        {
            if (ForkNotice.Tag is string sourceId) _chat.OpenConversation(sourceId);
        };
        ModeIndicatorButton.Click += (_, _) => ClearComposerMode();
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
        ScrollToBottomButton.Click += (_, _) => ScrollToEnd();
        MessageScroller.SizeChanged += (_, _) => UpdateMessageColumnWidth();
        MessageScroller.ScrollChanged += (_, _) =>
        {
            UpdateMessageColumnWidth();
            UpdateScrollAffordance();
        };
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                // Typing ends the walk: the next Up starts again from the newest thing sent, rather than
                // continuing to page through history under a sentence the person has begun writing themselves.
                if (!_recalling) _recallIndex = 0;
                UpdateSendState();
                UpdateContextRing();
            }
        };
        InputBox.GotFocus += (_, _) => SetComposerFocus(true);
        InputBox.LostFocus += (_, _) => SetComposerFocus(false);

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
            // A decision covering the composer is not a drop target: the event still bubbles from the host up
            // to the frame, so without this gate a capture released over the card would land in the draft.
            if (DecisionHost.IsVisible)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }
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
        // handlers live in MainWindow, above this one), then a decision covering the composer is answered, then
        // a steer waiting for its second tap goes back to being a draft, and only then does Escape mean "stop
        // the reply". The plan card's letter keys ride the same handler for the same reason. Tunnel, so this runs
        // before the composer's own key handling gets a turn.
        AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (TryChoosePlanOptionByKey(e)) return;
            if (e.Key != Key.Escape) return;
            if (DecisionHost.IsVisible)
            {
                if (e.Source is TextBox && _decisionPlanIndex >= 0)
                {
                    // Escape leaves the revision line with the sentence still in it; the next Escape answers the
                    // card. A key that deletes a paragraph is a key pressed by accident.
                    DecisionHost.Focus();
                    e.Handled = true;
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
        InputBox.PlaceholderText = HubStrings.Get("InputPlaceholder");
        GreetingLabel.Text = HubStrings.Get("AssistantGreeting");
        GreetingSubtitle.Text = HubStrings.Get("AssistantGreetingSubtitle");
        RenderSuggestions();
        UpdateSendState();

        RefreshModelPicker();
        RefreshComposerChoices();
        UpdateForkNotice();
        RenderMessages();
        UpdateContextRing();
        // The two composer-anchored surfaces read the transcript, not the run registry, so a repaint is the one
        // place both can be brought in step with whatever just changed.
        RefreshDecisionHost();
        RefreshSteerConfirm();
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
        SelectedReasoningLabel.IsVisible = supportsReasoning;
        // With routing on there is no single tier to name — Hub picks one per request — so the chip says who is
        // picking, and the tooltip carries the route this session last actually went out on. A setting that
        // spends money has to be readable without opening a log file.
        var routed = supportsReasoning && _chat.ActiveRouting == ChatRouting.Auto;
        SelectedReasoningLabel.Text = supportsReasoning
            ? routed ? HubStrings.Get("ChatRoutingAutoChip") : ReasoningChoiceLabel(_chat.ActiveReasoningEffort)
            : "";
        ToolTip.SetTip(SelectedReasoningLabel, routed && _chat.ActiveConversation is { } session
            && _chat.LastRouteFor(session.Id) is { } route
            ? $"{route.Model} · {route.Effort} — {route.Reason}"
            : null);
        ModelPicker.IsEnabled = choices.Length > 0;
        ToolTip.SetTip(ModelPicker, selected is null
            ? HubStrings.Get("NoAvailableChatModels")
            : selected.Provider.Name + " · " + selected.ModelName);
    }

    private void RefreshComposerChoices()
    {
        RefreshModelPicker();
        ModeIndicatorButton.IsVisible = _selectedComposerMode is not null;
        ModeIndicatorIcon.Data = ThemeGeometry(ComposerModeIconKey(_selectedComposerMode));
        ModeIndicatorLabel.Text = HubStrings.Get(_selectedComposerMode switch
        {
            ChatModes.Ask => "ChatModeAsk",
            ChatModes.Plan => "ChatModePlan",
            _ => "ChatModeGoal",
        });
        ToolTip.SetTip(ModeIndicatorButton, HubStrings.Get("ChatModeResetHint"));
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
        WorkspaceChipLabel.Text = root is { Length: > 0 } ? FolderNameOf(root) : HubStrings.Get("ChatWorkspaceNone");
        ToolTip.SetTip(WorkspaceChip, root is { Length: > 0 }
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWorkspaceChipHintFormat"), root)
            : HubStrings.Get("ChatWorkspaceChipHint"));
        // And it is on screen only while the choice is still open: no session, or one that has never received a
        // message. The directory is what a session is born into, so after the first turn it is history —
        // swapping it mid-conversation would leave every earlier reply about a different tree — and a control
        // that can no longer act is a claim that it still can.
        WorkspaceChip.IsVisible = _chat.ActiveConversation is not { Messages.Count: > 0 };
    }

    private static string FolderNameOf(string path)
        => System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

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
        UpdateContextProgressFill(usage);
        RefreshContextCompressionButton();
        RenderContextBreakdown(usage);
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
        ContextPopoverNote.Text = string.Join(Environment.NewLine, notes);
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
    /// Pick a directory, and — only while one is bound — clear it. The bound path is not repeated as a row: the
    /// chip reads it out and the tooltip gives it in full, so a third copy would be chrome saying a thing twice.
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

        return menu;
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
            default:
                AppendNotice(HubStrings.Get("ChatWorkspaceRejected") + result.Path, danger: true);
                return;
        }
    }

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
    /// The permission menu, hung off its own chip: three tiers, each stating what it lets through unasked, with
    /// the effective one ticked. A fourth row appears only when something overrides the app-wide default —
    /// otherwise it would be a second line on screen saying what the first already says. It works with no
    /// session on screen, where the pick lands on the one about to be started.
    /// </summary>
    private MenuFlyout BuildPermissionMenu()
    {
        var menu = new MenuFlyout();
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
            item.Click += (_, _) =>
            {
                _chat.SelectApprovalMode(mode);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        if ((_chat.ActiveConversation?.ApprovalMode ?? _chat.SelectedApprovalMode) is { Length: > 0 })
        {
            var follow = new MenuItem
            {
                Header = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatToolPermissionFollowFormat"),
                    HubStrings.Get(ToolApprovalModeKey(_chat.DefaultApprovalMode))),
                FontSize = 12,
            };
            follow.Click += (_, _) =>
            {
                _chat.SelectApprovalMode(null);
                menu.Hide();
            };
            menu.Items.Add(follow);
        }

        // A pick that differs from the app default is a per-session answer to what may be a standing preference,
        // and the setting that would make it stand lives on another page. One row here writes it: the same store,
        // the same repaint, the same rule the settings page's picker uses.
        if (effective != _chat.DefaultApprovalMode)
        {
            var makeDefault = new MenuItem
            {
                Header = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatToolPermissionSetDefaultFormat"),
                    HubStrings.Get(ToolApprovalModeKey(effective))),
                FontSize = 12,
                Tag = "ApprovalSetDefault",
            };
            makeDefault.Click += (_, _) =>
            {
                _chat.SetDefaultApprovalMode(effective);
                menu.Hide();
            };
            menu.Items.Add(makeDefault);
        }

        return menu;
    }

    /// <summary>Whether the menu would offer to make the current pick the app default. Built from the same menu
    /// the click builds, so the assertion is about the row a person can actually reach.</summary>
    internal bool ApprovalSetDefaultRowShownForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Any(item => "ApprovalSetDefault".Equals(item.Tag));

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
            new DynamicResourceExtension(danger ? "Hub.DangerText" : "Hub.TextSecondary"));

        var title = new TextBlock
        {
            Text = HubStrings.Get(ToolApprovalModeKey(mode)),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        };
        var hint = new TextBlock
        {
            Text = HubStrings.Get(ToolApprovalModeHintKey(mode)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        if (danger)
        {
            title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
            hint.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
        }
        else
        {
            hint.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextTertiary"));
        }

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(hint);

        var tick = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry("Hub.Icon.Tick"),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsVisible = checkedItem,
        };
        tick.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        // One width for every row, so the ticks land in a column rather than wherever each description happens
        // to end.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinWidth = 300 };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(tick, 2);
        header.Children.Add(icon);
        header.Children.Add(text);
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
        // Two modes rather than three. "提问" was the one with nothing behind it that the composer already
        // says: an assistant with no tools offered is what answering a question without acting on it means, and
        // a person who wants that simply does not pick 计划 or 目标. The mode itself stays — sessions that
        // recorded "ask" keep running without tools — it just has no menu item any more.
        foreach (var (mode, key) in new[]
                 {
                     (ChatModes.Plan, "ChatModePlan"),
                     (ChatModes.Agent, "ChatModeGoal"),
                 })
        {
            var item = new MenuItem
            {
                Header = BuildComposerModeHeader(mode, key),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _selectedComposerMode == mode,
                Tag = mode,
            };
            item.Click += (_, _) =>
            {
                if (_selectedComposerMode == mode)
                    ClearComposerMode();
                else
                    SetComposerMode(mode);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        var addFolder = new MenuItem { Header = HubStrings.Get("ChatAddLocalFolder") };
        addFolder.Click += async (_, _) => await AddLocalFolderAsync();
        menu.Items.Add(addFolder);

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

    private static string ComposerModeIconKey(string? mode) => mode switch
    {
        ChatModes.Ask => "Hub.Icon.ChatModeAsk",
        ChatModes.Plan => "Hub.Icon.ChatModePlan",
        _ => "Hub.Icon.ChatModeGoal",
    };

    private void SetComposerMode(string mode)
    {
        _selectedComposerMode = mode;
        _chat.SelectMode(mode);
        RefreshComposerChoices();
    }

    private void ClearComposerMode()
    {
        _selectedComposerMode = null;
        _chat.SelectMode(ChatModes.Agent);
        RefreshComposerChoices();
    }

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

            var knownEfforts = ModelCatalog.EffortsFor(choice.Provider, choice.ModelName);
            if (knownEfforts.Count > 0 || ModelCatalog.MayTryUnreportedEfforts(choice.Provider, choice.ModelName))
            {
                // A model that never reported a tier is offered the whole ladder — the first refusal trims it,
                // and the first thinking reply confirms it. Unknown must not read as impossible, or the only way
                // to find out is to ship a manifest entry for every model someone might connect to.
                var currentEffort = isSelected ? _chat.ActiveReasoningEffort : ChatReasoningEfforts.Default;
                foreach (var (effort, labelKey) in ReasoningChoices.Where(candidate =>
                             candidate.Value == ChatReasoningEfforts.Default
                             || knownEfforts.Count == 0
                             || knownEfforts.Contains(candidate.Value, StringComparer.OrdinalIgnoreCase)))
                {
                    var effortItem = new MenuItem
                    {
                        Header = HubStrings.Get(labelKey),
                        ToggleType = MenuItemToggleType.Radio,
                        IsChecked = currentEffort == effort,
                        Tag = effort,
                    };
                    effortItem.Click += (_, _) =>
                    {
                        if (!isSelected) _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);
                        _chat.SelectReasoningEffort(effort);
                    };
                    item.Items.Add(effortItem);
                }
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(BuildRoutingMenuItem());
        return menu;
    }

    /// <summary>
    /// Who picks the model and the tier for this session. It is a switch under the model list rather than another
    /// row in it, because "自动" as a menu entry would read exactly like the gateway's model id
    /// <c>orcarouter/auto</c> — the same word, three meanings, and this is the one that changes what gets spent.
    /// </summary>
    private MenuItem BuildRoutingMenuItem()
    {
        var conversationId = _chat.ActiveConversation?.Id ?? "";
        var auto = conversationId.Length > 0 && _chat.RoutingFor(conversationId) == ChatRouting.Auto;
        var item = new MenuItem { Header = HubStrings.Get("ChatRoutingMenu") };
        foreach (var (value, labelKey, isAuto) in new (string, string, bool)[]
                 {
                     (ChatRouting.Manual, "ChatRoutingManual", false),
                     (ChatRouting.Auto, "ChatRoutingAuto", true),
                 })
        {
            var choice = new MenuItem
            {
                Header = HubStrings.Get(labelKey),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = auto == isAuto,
            };
            choice.Click += (_, _) =>
            {
                if (conversationId.Length > 0) _chat.SetRouting(conversationId, value);
                RefreshComposerChoices();
            };
            item.Items.Add(choice);
        }
        return item;
    }

    private static readonly (string Value, string LabelKey)[] ReasoningChoices =
    [
        (ChatReasoningEfforts.Default, "ChatReasoningDefault"),
        (ChatReasoningEfforts.Low, "ChatReasoningLow"),
        (ChatReasoningEfforts.Medium, "ChatReasoningMedium"),
        (ChatReasoningEfforts.High, "ChatReasoningHigh"),
        (ChatReasoningEfforts.XHigh, "ChatReasoningXHigh"),
        (ChatReasoningEfforts.Max, "ChatReasoningMax"),
        (ChatReasoningEfforts.Ultra, "ChatReasoningUltra"),
    ];

    private static string ReasoningChoiceLabel(string effort)
    {
        var labelKey = ReasoningChoices.FirstOrDefault(choice => choice.Value == effort).LabelKey
                       ?? ReasoningChoices[0].LabelKey;
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
        ContextAttachmentPanel.Children.Clear();
        foreach (var attachment in _contextAttachments)
        {
            var label = new TextBlock
            {
                Text = (attachment.Kind == "project" ? HubStrings.Get("ChatProjectPrefix") : HubStrings.Get("ChatFolderPrefix"))
                       + attachment.Name,
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

    private Task<string?> ReadAttachmentContextAsync()
    {
        if (_contextAttachments.Count == 0) return Task.FromResult<string?>(null);
        var attachments = _contextAttachments.ToArray();
        // The window is read here rather than inside the task: the folder is priced against what the model can
        // actually hold, and a pool thread must not reach the workspace.
        var windowTokens = _chat?.SelectedChatModel is { } selected
            ? ContextBudget.For(selected.Provider, selected.ModelName).Tokens
            : ContextBudget.FallbackTokens;
        return Task.Run<string?>(() =>
        {
            var sections = attachments.Select(attachment =>
            {
                var contents = ChatContextReader.ReadFolder(attachment.Path, attachment.Name, windowTokens);
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
    private void AddPendingPicture(byte[] bytes, string name)
    {
        var verdict = ChatImageFormat.Admit(bytes, _pendingPictures.Count);
        if (verdict != ChatImageVerdict.Accepted)
        {
            AppendNotice(ImageNotice(verdict), danger: true);
            return;
        }

        _pendingPictures.Add(new PendingPicture(bytes, name));
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
    /// raw would print the brace.</summary>
    private static string NoticeFor(string? refusalKey) => refusalKey switch
    {
        "ChatImageEmpty" => ImageNotice(ChatImageVerdict.Empty),
        "ChatImageUnrecognized" => ImageNotice(ChatImageVerdict.Unrecognized),
        "ChatImageTooLarge" => ImageNotice(ChatImageVerdict.TooLarge),
        "ChatImageTooMany" => ImageNotice(ChatImageVerdict.TooMany),
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
        => transfer is not null && await TakePicturesAsync(transfer) > 0;

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
        => _pendingPictures.Count == 0 ? null : _pendingPictures.Select(picture => picture.Bytes).ToArray();

    /// <summary>
    /// The pictures one sent message carries, painted from the file Hub kept. Bounded so a 5K capture cannot push
    /// the chat column open, and a file that is no longer there says so rather than drawing nothing at all — the
    /// transcript claims a picture was sent, and that claim has to stay checkable.
    /// </summary>
    private Control BuildTurnPictures(string conversationId, IReadOnlyList<ChatImage> images)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Classes.Add("turn-pictures");
        foreach (var image in images)
        {
            var path = _chat.StoredImagePath(conversationId, image.File);
            var bitmap = path is null ? null : TryOpenBitmap(path);
            Control cell = bitmap is not null
                ? new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 260,
                    MaxHeight = 200,
                }
                : new TextBlock
                {
                    Classes = { "muted" },
                    Text = HubStrings.Get("ChatImageGone"),
                };
            ToolTip.SetTip(cell, $"{image.File} · {image.MediaType} · {image.Bytes} bytes");
            var frame = new Border { Child = cell, Classes = { "turn-picture" } };
            Control slot = frame;
            if (bitmap is not null)
            {
                // The spacing moves onto the button so the gap between two thumbnails is not itself a hit target.
                frame.Margin = new Thickness(0);
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
                slot = opener;
            }
            row.Children.Add(slot);
        }

        return row;
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
            if (turn.Role != ChatRoles.User) continue;
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

    private static Bitmap? TryOpenBitmap(string path)
    {
        try
        {
            return new Bitmap(path);
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
            "get_projects" => HubStrings.Get("ChatToolProjects"),
            "get_engines" => HubStrings.Get("ChatToolEngines"),
            "get_toolchain_status" => HubStrings.Get("ChatToolchains"),
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
            ToolTip.SetTip(rest, string.Join(Environment.NewLine, sources.Skip(shown)));
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
        if (_chat is null) return;
        PruneFoldMemory();
        var conversation = _chat.ActiveConversation;
        var run = conversation is null ? null : _chat.RunFor(conversation.Id);

        // The live bubble is not a stored turn. It comes off the flow before the stored rows are laid down and
        // goes back on the end afterwards, so a repaint in the middle of a reply cannot bury it mid-transcript.
        // A bubble belonging to another session is closed for good: its run keeps going, and coming back to it
        // rebuilds the bubble from what arrived while it was out of sight — text, tools and elapsed time alike,
        // because all three are read off the run and none of them is a number this view was keeping.
        // A bubble that outlives its run would keep animating a reply that has already been written to the
        // transcript, where it now belongs — so the absence of a streaming run for this session closes it,
        // whoever happened to ask for a repaint.
        if (_live is { } attached && (run is not { IsStreaming: true } || attached.Run != run))
            CloseLive();
        if (_live is { } held) MessageFlow.Children.Remove(held.Row);

        var visible = new List<(int Index, ChatTurn Turn, string? ToolName)>();
        if (conversation is not null)
        {
            for (var i = 0; i < conversation.Messages.Count; i++)
            {
                var turn = conversation.Messages[i];
                // A system turn is context the model was given, not something anybody said. A tool result stays
                // in the flow but not as a bubble — see <see cref="ToolResultLine"/> — and the name it prints is
                // the one its call carried, because the result turn itself does not keep it.
                if (turn.Role == ChatRoles.System) continue;
                visible.Add((i, turn, turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } answered
                    ? conversation.Messages.ElementAtOrDefault(conversation.IndexOfToolCall(answered))?.ToolName
                    : null));
            }
        }

        // A decision changes what an already-rendered turn looks like without changing how many turns there
        // are, which is precisely the case incremental rendering cannot see: the stamp is what lets a card
        // collapse into its one-line record instead of keeping buttons that no longer mean anything.
        var approvalStamp = ApprovalStamp(conversation);
        var approvalChanged = approvalStamp != _renderedApprovalStamp;
        _renderedApprovalStamp = approvalStamp;

        if (visible.Count == 0)
        {
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation?.Id;
            _renderedCount = 0;
            _renderedTailKey = "";
            _openGroup = null;
            EmptyState.IsVisible = run is not { IsStreaming: true };
            if (run is { IsStreaming: true }) MessageFlow.Children.Add(AttachLive(run).Row);
            RenderNotice();
            UpdateScrollAffordance();
            return;
        }

        // A teardown is for the cases an append cannot express: another session's turns, a history that shrank
        // under an edit or a regenerate, a decision that rewrote what an already-painted turn looks like, and a
        // turn that has moved into a slot it did not occupy when the flow was laid down. A growing activity run
        // is none of those: its new turns extend the group that is already the last row, which is what keeps a
        // head the person had opened from being swapped out for a fresh collapsed one.
        var aDifferentSession = _renderedConversationId != conversation!.Id;
        var historyShrank = visible.Count < _renderedCount;
        if (aDifferentSession || historyShrank || approvalChanged
            || (_renderedCount > 0 && SlotKey(visible[_renderedCount - 1].Turn) != _renderedTailKey))
        {
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation.Id;
            _renderedCount = 0;
            _openGroup = null;
            // A session opens on its newest message. The scroll offset belongs to the viewer rather than to the
            // conversation, so without this a switch inherits wherever the previous one had been read up to.
            // Revalidating the same transcript is not a switch, though: whoever had scrolled up to read something
            // stays where they put it.
            if (aDifferentSession || historyShrank) _stickToBottom = true;
        }

        var lastIndex = conversation.Messages.Count - 1;
        for (var i = _renderedCount; i < visible.Count;)
        {
            // A run of consecutive machine turns — calls, their results, a thinking-only answer — folds into one
            // collapsible group. Prose, user turns and system turns break the run. The fold is a paint-time
            // shape only: every folded turn still produces its own row inside the group, so the row count the
            // transcript checks key on is unchanged, and MessageRows expands groups to find them.
            if (IsActivityTurn(visible[i].Turn))
            {
                var start = i;
                while (i < visible.Count && IsActivityTurn(visible[i].Turn)) i++;
                var turns = visible.GetRange(start, i - start);
                // The run is still open while its group is the last thing on the page, so the turns that arrived
                // since are the same piece of work: grow that group where it stands. Rebuilding it would answer a
                // person reading the opened fold by closing it, once per tool call, for as long as the model
                // keeps calling tools.
                if (_openGroup is { } open && open.ConversationId == conversation.Id && open.EndVisible == start
                    && MessageFlow.Children.Count > 0
                    && ReferenceEquals(MessageFlow.Children[^1], open.Group))
                {
                    // Re-sliced from where the run began, not the turns that just arrived: a row is drawn from a
                    // call <i>and</i> the result that answered it, so handing the group only the new second half
                    // would draw every result as a call nobody had asked for.
                    open.Turns = visible.GetRange(open.FirstVisible, i - open.FirstVisible);
                    open.EndVisible = i;
                    DeriveActivityGroup(open, conversation.Id, lastIndex);
                }
                else
                {
                    _openGroup = CreateActivityGroup(conversation.Id, start, turns);
                    DeriveActivityGroup(_openGroup, conversation.Id, lastIndex);
                }

                _renderedCount = i;
                continue;
            }

            // Prose closes the run: the next piece of machine work is a different exchange and gets its own fold.
            _openGroup = null;
            var (index, turn, toolName) = visible[i];
            AppendRenderedTurn(conversation.Id, index, turn, toolName, isLast: index == lastIndex,
                markdown: i >= visible.Count - EagerMarkdownLimit, target: MessageFlow);
            _renderedCount = i + 1;
            i++;
        }

        // Where the painted prefix ends, in the conversation's own terms. The next render compares against this
        // rather than against the count, which is what lets a result filed beside its call — an insert, not an
        // append — rebuild the flow instead of drawing the shifted tail twice.
        _renderedTailKey = SlotKey(visible[^1].Turn);

        EmptyState.IsVisible = false;
        if (run is { IsStreaming: true }) MessageFlow.Children.Add(AttachLive(run).Row);
        RenderNotice();
        // The rows that just went in have not been measured yet, so this is posted: a view that was following
        // the reply keeps following it, and one the person had scrolled away from is left where they put it.
        ScrollToEndIfSticky();
        UpdateScrollAffordance();
    }

    /// <summary>Forces a full rebuild on the next <see cref="RenderMessages"/> — used after an edit or a
    /// regenerate shortened the history, where the visible prefix no longer matches the stored turns.</summary>
    private void ForceRebuildMessages()
    {
        _renderedConversationId = null;
        _renderedCount = 0;
        RenderMessages();
    }

    private void AppendRenderedTurn(
        string conversationId, int index, ChatTurn turn, string? toolName, bool isLast, bool markdown,
        Panel target)
    {
        var fromUser = turn.Role == ChatRoles.User;
        var body = new StackPanel { Spacing = 8 };

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
        // arrived; the payload itself stays one hover away.
        // A plan is the other case where a row's text is a document rather than a reply, and it is suppressed the
        // same way the tool payload is. The card built below stands in for it and names it; the turn keeps its
        // text, so the provider still gets the plan verbatim and this row's copy action still hands over all of it.
        var isPlanTurn = turn.PlanApprovalState is { Length: > 0 };
        if (turn.Role == ChatRoles.Tool) body.Children.Add(ToolResultLine(toolName, turn));
        else if (!isPlanTurn) body.Children.Add(new TextBlock { Classes = { "turn-text" }, Text = turn.Text });

        // What the person attached is shown from the file Hub kept, not from anything this view remembers: the
        // transcript is the only copy of "this message had a picture in it", and a row rebuilt after a restart
        // has to look the same as the one that was sent. A frame the assistant captured itself stays out of the
        // flow — its approval card already says which window was grabbed, and this row is about what the user said.
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
            else if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 })
                approvalSurface = BuildCallRecord(conversationId, callId, turn);
        }
        if (isPlanTurn) approvalSurface = BuildPlanCard(turn, index);

        // A function-call or tool-result turn gets no action bar: it is not a readable message, and acting on
        // half of a call/result pair orphans the other half (the pipeline sends them to the provider as one
        // exchange). `index` is the only thing that gates the bar, so nulling it here leaves stored indexes
        // untouched for every other turn.
        var actionableIndex = turn.ToolCallId is { Length: > 0 } ? (int?)null : index;
        target.Children.Add(BuildMessageRow(fromUser, body, actionableIndex, turn.Role, turn.Text, isLast, turn.At));

        // User text is plain by nature, and a tool payload is now a quiet line rather than a bubble; only the
        // assistant's own words carry Markdown worth rendering — and a plan's words do not render here at all.
        if (!fromUser && markdown && !isPlanTurn && turn.Role != ChatRoles.Tool && turn.Text.Length > 0)
            MarkdownMessageRenderer.RenderInto(body, turn.Text);

        // Added after the Markdown pass rather than before it, because <see cref="MarkdownMessageRenderer.RenderInto"/>
        // clears the panel it renders into: a line laid down first is not underneath the answer, it is gone. This is
        // also the order the person reads — the reply, then where it came from, then any record of a decision.
        if (!fromUser && ServerSearchLog.Read(turn.WebSearch) is { IsEmpty: false } searches)
            body.Children.Add(BuildSearchSources(searches));

        if (approvalSurface is not null) body.Children.Add(approvalSurface);
    }

    /// <summary>Whether a turn is machine work rather than something said: a tool result, a call, or a
    /// thinking-only answer. These are the turns a run folds into one group; prose and user turns break it.
    /// A thinking turn that also carries prose is prose — the answer is what was asked for, and folding it
    /// away would hide the reply.</summary>
    private static bool IsActivityTurn(ChatTurn turn) => turn.Role switch
    {
        ChatRoles.Tool => true,
        ChatRoles.Assistant => turn.ToolCallId is { Length: > 0 }
                               || (turn.Reasoning is { Length: > 0 } && turn.Text.Length == 0),
        _ => false,
    };

    /// <summary>
    /// <summary>
    /// The shell of one collapsible activity group: its head, the panel its rows live in, and the stretch of the
    /// transcript it stands for. Keeping those together is what lets a machine run that is still arriving grow the
    /// group it opened instead of replacing it — the head is the control a person may well have expanded to read,
    /// and a rebuilt group answers them with a collapsed one.
    /// </summary>
    private sealed class ActivityGroupView
    {
        public required string ConversationId { get; init; }

        /// <summary>Where the run begins in the visible turns, and where it has reached so far. A next turn whose
        /// index is exactly <see cref="EndVisible"/> continues this run; anything else is a different piece of
        /// work and opens its own fold.</summary>
        public required int FirstVisible { get; init; }

        public int EndVisible { get; set; }

        public required List<(int Index, ChatTurn Turn, string? ToolName)> Turns { get; set; }

        public required Border Group { get; init; }

        public required ToggleButton Head { get; init; }

        public required StackPanel Inner { get; init; }
    }

    /// <summary>Lays down an empty group over the first stretch of a machine run and puts it at the end of the
    /// flow. Its rows and its sentence come from <see cref="DeriveActivityGroup"/>.</summary>
    private ActivityGroupView CreateActivityGroup(string conversationId, int firstVisible,
        List<(int Index, ChatTurn Turn, string? ToolName)> turns)
    {
        var head = new ToggleButton { Classes = { "activity-group-head" } };
        var inner = new StackPanel { Spacing = 2 };
        var body = new StackPanel { Name = "ActivityGroupBody", Spacing = 0, IsVisible = false, Margin = new Thickness(0, 2, 0, 2) };
        body.Children.Add(inner);
        // The turn a run starts on is the group's identity: it does not move while the run grows, and a rebuild
        // slices the same run out of the same turn — so keying the fold to it is what lets an opened head come
        // back open.
        var foldKey = SlotKey(turns[0].Turn);
        // Driven off IsCheckedChanged rather than Click: a real click flips IsChecked (which fires this), and so
        // does a check that sets IsChecked directly — a raised Click never reaches a ToggleButton's OnClick, so
        // wiring the fold to Click would leave it untestable and half-broken. Wired before the remembered state is
        // applied, because the panel's visibility has to keep exactly one source.
        head.IsCheckedChanged += (_, _) =>
        {
            var expanded = head.IsChecked == true;
            body.IsVisible = expanded;
            RememberFold(_expandedGroups, conversationId, foldKey, expanded);
        };
        head.IsChecked = IsFoldOpen(_expandedGroups, conversationId, foldKey);

        var group = new Border { Classes = { "activity-group" }, Child = new StackPanel { Spacing = 2, Children = { head, body } } };
        MessageFlow.Children.Add(group);
        return new ActivityGroupView
        {
            ConversationId = conversationId,
            FirstVisible = firstVisible,
            EndVisible = firstVisible + turns.Count,
            Turns = turns,
            Group = group,
            Head = head,
            Inner = inner,
        };
    }

    /// <summary>
    /// Redraws one group from the turns it holds: every row inside it, then the full-width sentence over them.
    /// The head names the work (the first action, and how many followed) rather than a bare count, so a tools-only
    /// turn is a line of content and not a lonely pill. Collapsed by default: the charter hides secondary things.
    ///
    /// Inside, each tool exchange is folded into a single row. A read/search/list/find/command becomes one
    /// descriptive <c>activity-row</c> built from its call and result (the raw payload moves to the tooltip), so
    /// the AI lane reads as actions rather than a stack of result fragments. A write keeps its existing record
    /// line with the undo — that line is the write's one exit and its approval trace, and it is asserted
    /// elsewhere — so the group is a container that routes each turn to the row shape that fits it, not a
    /// re-implementation of the per-turn surfaces.
    ///
    /// The rows are re-derived rather than appended because a result is filed <i>beside</i> the call it answers
    /// (<see cref="Conversation.AppendFunctionResult"/>): the turn that just arrived is not always the new thing at
    /// the end, and rebuilding the slice from the transcript as it now stands is the only way the rows come out in
    /// the order the conversation holds. Only this group's own children move, so an opened head and the reading
    /// position are left alone — which is the whole point of extending rather than rebuilding.
    /// </summary>
    private void DeriveActivityGroup(ActivityGroupView view, string conversationId, int lastIndex)
    {
        var turns = view.Turns;
        // Pair each call with the result that answered it, so one row can say what was done and how it went.
        var calls = new Dictionary<string, ChatTurn>(StringComparer.Ordinal);
        foreach (var (_, turn, _) in turns)
        {
            if (turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 } id) calls[id] = turn;
        }

        var inner = view.Inner;
        inner.Children.Clear();
        foreach (var (index, turn, toolName) in turns)
        {
            // A call that owed a decision, or a write that left a copy, keeps its own surface (signpost / record
            // with undo) — those are functional and asserted, so route them through the ordinary turn renderer.
            if (turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 } callId)
            {
                if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 })
                    AppendRenderedTurn(conversationId, index, turn, toolName, isLast: index == lastIndex,
                        markdown: false, target: inner);
                // A plain read/command call renders no row of its own: its action row is drawn from the result
                // below, so emitting an empty row here would only add a gap.
                continue;
            }

            // A tool result folds into its call's row — except a write, whose record line above already speaks
            // for the exchange.
            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } answered)
            {
                var call = calls.TryGetValue(answered, out var paired) ? paired : null;
                if (call?.ToolName == ChatChanges.FileWriteTool) continue;
                inner.Children.Add(BuildActivityRowShell(conversationId, call, turn));
                continue;
            }

            // A thinking-only turn (or anything else machine-shaped) renders as before.
            AppendRenderedTurn(conversationId, index, turn, toolName, isLast: index == lastIndex,
                markdown: false, target: inner);
        }

        view.Head.Content = BuildActivityGroupHead(turns, calls.Count);
    }

    /// <summary>
    /// The head's full-width row: a chevron, a sentence naming the first action (and how many followed), and an
    /// aggregate <c>+N −M</c> when the run edited files. Naming the work rather than counting it is what keeps a
    /// tools-only turn from reading as a lonely pill stranded at the left edge with an empty middle — the AI lane
    /// stays a full-width line the eye can rest on.
    /// </summary>
    private Control BuildActivityGroupHead(List<(int Index, ChatTurn Turn, string? ToolName)> turns, int callCount)
    {
        // The head toggle carries the HubExpanderHeader theme, which supplies the same left `>`/`v` arrow the
        // sidebar's workspace groups use — so no chevron is built here, and the grid only lays out the sentence and
        // the aggregate diff chip.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var (text, added, removed) = GroupSummary(turns, callCount);
        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        if (added + removed > 0)
        {
            var chip = BuildDiffChip(added, removed);
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
        }

        return grid;
    }

    private static (string Text, int Added, int Removed) GroupSummary(
        List<(int Index, ChatTurn Turn, string? ToolName)> turns, int callCount)
    {
        if (callCount == 0)
        {
            var first = turns[0].Turn.At;
            var last = turns[^1].Turn.At;
            return (string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupThoughtFormat"), FormatDuration(last - first)), 0, 0);
        }

        var calls = turns
            .Where(t => t.Turn.Role == ChatRoles.Assistant && t.Turn.ToolCallId is { Length: > 0 })
            .ToList();
        var head = DescribeToolCall(calls[0].Turn);
        var text = calls.Count > 1
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupToolSummaryFormat"), head, calls.Count - 1)
            : head;
        var (added, removed) = AggregateDiff(calls);
        return (text, added, removed);
    }

    /// <summary>The aggregate line count of the run's edits, off the frozen previews only — a write that never
    /// parked froze nothing, and reading the disk to total a header would be the whole list paying for one line.
    /// When nothing was frozen the header simply carries no chip.</summary>
    private static (int Added, int Removed) AggregateDiff(
        List<(int Index, ChatTurn Turn, string? ToolName)> calls)
    {
        var added = 0;
        var removed = 0;
        foreach (var call in calls)
        {
            if (call.Turn.ApprovalPreview is { Length: > 0 } preview)
            {
                var (a, r) = ChatChanges.CountChanges(preview);
                added += a;
                removed += r;
            }
        }
        return (added, removed);
    }

    private static Control BuildDiffChip(int added, int removed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (added > 0) row.Children.Add(new TextBlock
        {
            Classes = { "file-chip-add" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorAddedFormat"), added),
        });
        if (removed > 0) row.Children.Add(new TextBlock
        {
            Classes = { "file-chip-del" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorRemovedFormat"), removed),
        });
        return new Border { Classes = { "file-chip" }, Child = row };
    }

    /// <summary>One folded tool exchange wrapped in its own expander. The collapsed line names the action; clicking
    /// it reveals the whole exchange — the arguments the call was made with and the payload that came back — in a
    /// detail panel that grows downward in place. The concrete operation is therefore on the page, not stranded one
    /// hover away in a tooltip, which a trackpad can't reach and a check can't read.</summary>
    private Control BuildActivityRowShell(string conversationId, ChatTurn? call, ChatTurn result)
    {
        var toggle = new ToggleButton { Classes = { "activity-row-toggle" }, Content = BuildActivityRow(call, result) };

        var detail = BuildActivityRowDetail(call, result);
        // The call is this row's identity, and it is the one thing a rebuild cannot move: the group re-derives its
        // rows from the transcript, so an opened exchange stays open through it.
        var foldKey = result.ToolCallId ?? "";

        // The fold is driven off IsChecked, never Click: a raised Click does not flip a ToggleButton, so wiring
        // Click would leave this both untestable and half-broken — the same reason the group head is wired this way.
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

    private static Border BuildActivityRowDetail(ChatTurn? call, ChatTurn result)
    {
        var body = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailInput"),
        });
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-body" },
            Text = FormatToolArguments(call),
            MaxHeight = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailOutput"),
        });
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-body" },
            Text = FormatToolResult(result),
            MaxHeight = 200,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return new Border
        {
            Classes = { "activity-detail" },
            IsVisible = false,
            Child = body,
        };
    }

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
    /// stderr can't stretch the transcript — a tool output that matters is read in full via the inspector, not here.
    /// A truncated tail is said so rather than cut silently.</summary>
    private static string FormatToolResult(ChatTurn result)
    {
        const int cap = 4096;
        var text = result.Text?.Replace("\r\n", "\n").Replace("\r", "\n") ?? "";
        if (text.Length == 0) return HubStrings.Get("ActivityDetailEmpty");
        return text.Length > cap ? text[..cap] + HubStrings.Get("ActivityDetailTruncated") : text;
    }

    /// <summary>One folded tool exchange as a single descriptive row: a status mark, the action named from its
    /// call, and — for a command — the command itself inline. This is the collapsed line inside
    /// <see cref="BuildActivityRowShell"/>; the full arguments and payload live in the detail it reveals. The left
    /// slot carries two glyphs in one cell — the execution status at rest, and the same `>`/`v` arrow the sidebar
    /// uses, which the row's styles swap in on hover and while expanded — so the affordance sits where the eye
    /// already is rather than stranded on the right edge.</summary>
    private Control BuildActivityRow(ChatTurn? call, ChatTurn result)
    {
        var grid = new Grid { Classes = { "activity-row" }, ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
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

        var command = call?.ToolName == "run_command" ? ArgField(call.ToolArguments, "command", "cmd") : null;
        if (!string.IsNullOrWhiteSpace(command))
        {
            var oneLine = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var chip = new Border
            {
                // file-chip for the look, source-chip so a check can tell these from the other chips in the same
                // panel — the run_command preview and the diff counters share the styling and nothing else.
                Classes = { "file-chip", "source-chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = oneLine.Length > 60 ? oneLine[..60] + "…" : oneLine },
            };
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
        }

        if (result.Text.Length > 0) ToolTip.SetTip(grid, result.Text);
        return grid;
    }

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
            "read_file" when path.Length > 0
                => string.Format(culture, HubStrings.Get("ActivityRowRead"), path),
            "file_write" when path.Length > 0
                => string.Format(culture, HubStrings.Get("ActivityRowEditFile"), path),
            "search_text"
                => string.Format(culture, HubStrings.Get("ActivityRowSearch"),
                    ArgField(args, "pattern", "query") ?? ""),
            "find_files"
                => string.Format(culture, HubStrings.Get("ActivityRowFind"),
                    ArgField(args, "pattern", "query", "glob") ?? ""),
            "list_directory"
                => string.Format(culture, HubStrings.Get("ActivityRowList"), path.Length > 0 ? path : "."),
            "run_command" => HubStrings.Get("ActivityRowRunCommand"),
            // Host and path only: the query is where a token hides, and this line is stored in the transcript.
            "web_fetch" => string.Format(culture, HubStrings.Get("ActivityRowFetch"),
                WebFetch.Shown(ArgField(args, "url") ?? "")),
            _ => string.Format(culture, HubStrings.Get("ActivityRowGeneric"), name),
        };
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
    private static string FormatDuration(TimeSpan span)
        => span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds:D2}s"
            : $"{Math.Max(0, (int)span.TotalSeconds)}s";

    /// <summary>What came back from one tool call, as a single muted line: the tool that answered, then the first
    /// line of its payload, with the whole thing on hover. A result that failed is said so, because the line is
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
        // The whole payload on hover, including the lines the one-line summary dropped. An empty result has
        // nothing to show, and an empty tooltip is simply no tooltip at all.
        if (turn.Text.Length > 0) ToolTip.SetTip(block, turn.Text);
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
    private Control BuildCallRecord(string conversationId, string callId, ChatTurn turn)
    {
        // A write that never had to ask still changed the file, so it gets a line of its own naming the file
        // rather than the tool: who was asked is a permission detail, what moved is the record.
        var line = turn.ApprovalState is { Length: > 0 } state
            ? $"{HubStrings.Get(state switch
                {
                    ChatApprovalStates.Approved => "ChatApprovalResolvedApproved",
                    ChatApprovalStates.Denied => "ChatApprovalResolvedDenied",
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
        row.Children.Add(new TextBlock
        {
            Classes = { "approval-record-text" },
            Text = line,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (turn.UndoName is { Length: > 0 })
        {
            row.Children.Add(BuildUndoButton(conversationId, callId,
                ChatUndoStore.WritePathOf(turn.ToolArguments)));
        }

        return new Border { Classes = { "approval-record" }, Child = row };
    }

    private Button BuildUndoButton(string conversationId, string callId, string path)
    {
        var button = new Button
        {
            Classes = { "undo-action" },
            Content = HubStrings.Get("ChatUndoButton"),
            Tag = "ChatUndoButton",
        };
        ToolTip.SetTip(button, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatUndoTip"), path));
        button.Click += (_, _) => RevertWrite(conversationId, callId, button);
        return button;
    }

    /// <summary>One click, one file back. A refusal is a line in the notice row rather than a dialog: the reason
    /// nothing moved has to be readable while the transcript stays exactly where it was.</summary>
    private async void RevertWrite(string conversationId, string callId, Button source)
    {
        source.IsEnabled = false;
        var outcome = await _chat.RevertWriteAsync(conversationId, callId);
        // A revert that worked raises Changed on its way out, and the rebuild that drops the spent button comes
        // with it — so only a refusal has anything left to re-enable.
        if (outcome.Succeeded) return;
        source.IsEnabled = true;
        var key = outcome.RefusalKey ?? outcome.Verdict switch
        {
            UndoVerdict.CopyMissing => "ChatUndoCopyMissing",
            UndoVerdict.ChangedSince => "ChatUndoChangedSince",
            UndoVerdict.TargetMissing => "ChatUndoTargetMissing",
            UndoVerdict.NotText => "ChatUndoNotText",
            UndoVerdict.RefusedPath => "ChatUndoRefusedPath",
            _ => "ChatUndoWriteFailed",
        };
        AppendNotice(HubStrings.Get(key), danger: key is "ChatUndoWriteFailed" or "ChatUndoRefusedPath");
    }

    /// <summary>Hands one decision to the workspace. A refusal is spoken out loud rather than swallowed: the card
    /// would otherwise sit there having done nothing, which reads as a broken button.</summary>
    private void ResolveApproval(string conversationId, string callId, bool approved, bool alwaysAllow)
    {
        if (_chat.TryResolveApproval(conversationId, callId, approved, alwaysAllow, out var refusalKey)) return;
        AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
    }

    private void ResolvePlanApproval(string conversationId, int turnIndex, string decision)
    {
        if (!_chat.TryResolvePlanApproval(conversationId, turnIndex, decision, out var refusalKey))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
            return;
        }

        // The revision's own text is sent by the review surface (CommitPlanReview) through the ordinary send
        // path; prefilling the composer here would put a prompt in front of a sentence the person already wrote.
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
        bool fromUser, StackPanel body, int? index, string role, string text, bool isLast, DateTimeOffset? at = null)
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
            var actions = BuildActionBar(messageIndex, role, text, isLast, at, body);
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

    private Control BuildActionBar(int index, string role, string text, bool isLast, DateTimeOffset? at, StackPanel body)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Classes.Add("message-actions");

        if (role == ChatRoles.User && at is { } timestamp)
        {
            var localTimestamp = timestamp.ToLocalTime();
            var relative = new TextBlock { Text = FormatRelativeTime(DateTimeOffset.Now - localTimestamp) };
            relative.Classes.Add("message-timestamp");
            ToolTip.SetTip(relative, localTimestamp.ToString(
                "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture));
            bar.Children.Add(relative);
        }

        if (role == ChatRoles.User)
        {
            bar.Children.Add(IconActionButton("CopyMessage", "Hub.Icon.Copy", () => CopyToClipboard(text)));
            bar.Children.Add(IconActionButton("EditMessage", "Hub.Icon.Edit", () => BeginInlineEdit(index, body, bar)));
            // A question that never got an answer is otherwise stuck: the notice that explained the failure is
            // not written to the transcript, and resubmitting the same text is refused as an unchanged edit. The
            // same ↻ that re-answers a reply re-asks this one, because Regenerate drops nothing when the
            // trailing turn is the question. It stops being the trailing turn the moment an answer lands, so
            // the button leaves on its own.
            if (isLast && ViewedRun is null)
                bar.Children.Add(IconActionButton("RetryMessage", "Hub.Icon.Refresh", RegenerateAsync));
        }
        else
        {
            bar.Children.Add(IconActionButton("CopyMessage", "Hub.Icon.Copy", () => CopyToClipboard(text)));
            if (isLast)
                bar.Children.Add(IconActionButton("RegenerateMessage", "Hub.Icon.Refresh", RegenerateAsync));
            bar.Children.Add(IconActionButton("BranchFromHere", "Hub.Icon.Branch", () => BranchFromHere(index)));
        }

        return bar;
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
        string textKey, string geometryKey, Action onClick, double size = 13, double thickness = 1.7)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        var button = new Button { Content = icon, Tag = textKey };
        button.Classes.Add("message-action");
        button.Classes.Add("message-action-icon");
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Appends a one-line notice: neutral (model changed, cancellation) or danger (errors).</summary>
    /// <summary>
    /// The last thing the app had to say about a run, and how many of the person's own messages had been sent
    /// when it was said. A notice is the tail of the transcript until the person speaks again — which is the rule
    /// that keeps it surviving a rebuild of that transcript without outliving the news it carried.
    /// </summary>
    private (string ConversationId, string Text, bool Danger, int OwnTurns)? _notice;

    private void AppendNotice(string text, bool danger)
    {
        var viewed = _chat.ActiveConversation;
        _notice = (viewed?.Id ?? "", text, danger,
            viewed?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0);
        RenderNotice();
        ScrollToEnd();
    }

    /// <summary>Paints the stored notice, if it still is news: same conversation, nothing of the person's said
    /// since. Called from the render path, so a rebuild cannot erase it and a new message cannot be answered by
    /// an old warning. With no conversation at all — the model list just emptied, say — there is nothing that can
    /// age it out, so the line stands until the next one replaces it.</summary>
    private void RenderNotice()
    {
        NoticeHost.Children.Clear();
        if (_notice is not { } notice) return;

        var viewed = _chat.ActiveConversation;
        var conversationId = viewed?.Id ?? "";
        var ownTurns = viewed?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0;
        if (conversationId != notice.ConversationId || ownTurns != notice.OwnTurns)
        {
            _notice = null;
            return;
        }

        NoticeHost.Children.Add(BuildNoticeRow(notice.Text, notice.Danger));
    }

    /// <summary>The notice row on screen, whichever check needs it. It lives below the flow rather than at the end
    /// of it, so it is never mistaken for a turn and never shifts an index.</summary>
    private Grid? CurrentNoticeRow
        => NoticeHost.Children.LastOrDefault() is Grid row && row.Classes.Contains("notice") ? row : null;

    private static Grid BuildNoticeRow(string text, bool danger)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
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
        return row;
    }

    // ───────────────────────── Actions ─────────────────────────

    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var data = new DataTransfer();
        data.Add(item);
        _ = clipboard.SetDataAsync(data);
    }

    /// <summary>
    /// Keeps the round button in step with the composer: the same button sends or stops, and it stays
    /// greyed out while the box is empty so "there is nothing to send" is visible before the click rather than
    /// after it.
    /// </summary>
    private void UpdateSendState()
    {
        SendStateUpdates++;
        // A decision covering the composer owns the send button: it is disabled with its parent, and repainting
        // its glyph or tooltip here would fight the cover for the same 34 pixels. The counter still moves —
        // an assertion counts these updates, and an early return that skipped it would read as a stuck button.
        if (DecisionHost.IsVisible)
        {
            UpdateContextRing();
            return;
        }
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
    private void SetComposerFocus(bool focused) => ComposerFrame.Classes.Set("focused", focused);

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

                // A steer cancels the segment being generated and is the routing table's strongest signal, yet
                // until now it looked exactly like an ordinary send. The first Enter therefore only shows what
                // would be interjected; the second one (or the strip's button) is the steer itself.
                ShowSteerConfirm(steerText, steerContext, steerPictures);
                return;
            }

            _chat.RequestStop(run.ConversationId);
            return;
        }

        var text = (InputBox.Text ?? "").Trim();
        var pictures = PendingPictureBytes();
        // A picture with no question under it is a message: "look at this" is what people actually send, and a
        // send button that stays grey because the box is empty would say otherwise.
        if (text.Length == 0 && pictures is null) return;
        if (_chat.SelectedChatModel is null)
        {
            AppendNotice(HubStrings.Get("NoAvailableChatModels"), danger: true);
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

        _contextAttachments.Clear();
        _pendingPictures.Clear();
        RenderContextAttachments();
        InputBox.Text = "";
        SendTextAsync(text, context, pictures);
    }

    /// <summary>Hands the message to the workspace and starts a run for it. The user's own turn is painted from
    /// the transcript like any other row, so there is no second copy of it here to keep in step.</summary>
    private void SendTextAsync(string text, string? attachedContext = null, IReadOnlyList<byte[]>? pictures = null)
    {
        if (text.Length == 0 && pictures is not { Count: > 0 }) return;
        if (_chat.ActiveConversation is null) _chat.StartConversation();
        if (_chat.ActiveConversation is not { } conversation) return;

        if (!_chat.TryEnqueueSend(conversation.Id, text, attachedContext, pictures, out var refusalKey))
        {
            AppendNotice(NoticeFor(refusalKey), danger: true);
            return;
        }

        RememberSentText(text);
        ConversationStateChanged?.Invoke();
    }

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
        if (!_chat.TryEnqueueContinuation(conversation.Id, out var refusalKey))
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatFailed"), danger: true);
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

        var untouched = original.Text ?? "";
        var normalActions = bar.Children.ToList();
        var closed = false;
        var editor = new TextBox
        {
            Text = untouched,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = original.FontSize,
        };
        editor.Classes.Add("message-edit");

        void Close()
        {
            if (closed) return;
            closed = true;
            body.Children.Remove(editor);
            body.Children.Insert(0, original);
            bar.Children.Clear();
            foreach (var action in normalActions) bar.Children.Add(action);
        }

        void Commit()
        {
            if (closed) return;
            var edited = editor.Text.Trim();
            var conversation = _chat.ActiveConversation;
            if (edited.Length == 0 || edited == untouched
                || IsViewedStreaming || conversation is null || !_chat.EditAndResend(conversation.Id, index, edited))
            {
                Close();
                return;
            }

            // The rebuild throws this whole subtree away, so there is nothing left to restore.
            closed = true;
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
        editor.CaretIndex = editor.Text.Length;

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
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(preview);
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
            Preview = preview,
            Status = status,
            Elapsed = elapsed,
            Glyph = glyph,
            ToolCount = run.CompletedToolCalls,
            ToolRunning = run.IsToolRunning,
        };
        bubble.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        bubble.Timer.Tick += (_, _) =>
        {
            if (_live is not { } current) return;
            current.Frame++;
            current.Elapsed.Text = FormatDuration(current.Run.Elapsed);
            RefreshGlyphPhase(current);
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
        MessageFlow.Children.Remove(live.Row);
        _live = null;
        UpdateSendState();
    }

    private void RefreshLive(LiveBubble bubble)
    {
        // Read off the run the bubble is attached to, not looked up by session id: the bubble and its run are the
        // same pairing from attach to detach, so a refresh can never land on a reply that started afterwards.
        var text = bubble.Run.LiveText;
        bubble.Preview.Text = text;
        if (text.Length == 0) bubble.ShowedText = false;
        else if (!bubble.ShowedText)
        {
            bubble.ShowedText = true;
            bubble.Status.Text = HubStrings.Get("ChatGenerating");
        }

        RefreshGlyphPhase(bubble);
        ScrollToEndIfSticky();
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

        // One call sets the phase, the count and the visibility and re-arms the clock together, so a refresh that
        // only confirms "still waiting" cannot leave the glyph visible but frozen (see ActivityGlyph.ApplyPhase).
        bubble.Glyph.ApplyPhase(phase, Math.Max(1, bubble.ToolCount));
    }

    /// <summary>Says how a run ended. The reason is recorded where it happened rather than inferred here from
    /// exception types, which is how "the user pressed stop" and "the stream stalled" used to blur together.</summary>
    private void AppendRunNotice(RunOutcome outcome)
    {
        if (outcome.NoticeKey is { } key)
        {
            AppendNotice(HubStrings.Get(key) + (outcome.Detail ?? ""), outcome.NoticeDanger);
            return;
        }

        if (!outcome.ReceivedText) AppendNotice(HubStrings.Get("ChatNoResponse"), danger: true);
    }

    private ConversationRun? ViewedRun
        => _chat.ActiveConversation is { } viewed ? _chat.RunFor(viewed.Id) : null;

    private bool IsViewedStreaming => ViewedRun is { IsStreaming: true };

    // ───────────────────────── Scrolling ─────────────────────────

    private void ScrollToEnd()
    {
        _stickToBottom = true;
        Dispatcher.UIThread.Post(
            () => { MessageScroller.ScrollToEnd(); UpdateScrollAffordance(); },
            DispatcherPriority.Background);
    }

    private void ScrollToEndIfSticky()
    {
        if (!_stickToBottom) return;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void UpdateScrollAffordance()
    {
        var extent = MessageScroller.Extent.Height;
        var viewport = MessageScroller.Viewport.Height;
        var atBottom = MessageScroller.Offset.Y + viewport >= extent - StickEpsilon;
        _stickToBottom = atBottom;
        ScrollToBottomButton.IsVisible = extent > viewport + StickEpsilon && !atBottom;
    }

    private void UpdateMessageColumnWidth()
    {
        var viewportWidth = MessageScroller.Viewport.Width;
        if (viewportWidth <= 0) return;

        var width = Math.Min(MessageColumn.MaxWidth, viewportWidth);
        if (double.IsNaN(MessageColumn.Width) || Math.Abs(MessageColumn.Width - width) > 0.1)
            MessageColumn.Width = width;
    }

    // ───────────────────────── Self-check hooks ─────────────────────────

    /// <summary>Message rows are Borders carrying the message-row class (both row kinds, since the container
    /// was unified); the class is what the hover-reveal rules and every row-counting check key on.</summary>
    private IEnumerable<Control> MessageRows
        => MessageFlow.Children.SelectMany(child => child.Classes.Contains("activity-group")
            ? child.GetLogicalDescendants().OfType<Control>().Where(row => row.Classes.Contains("message-row"))
            : Enumerable.Repeat(child, 1).Where(row => row.Classes.Contains("message-row")));

    internal string FlowText
    {
        get
        {
            // Every text block (plain bodies plus the Markdown viewer's CTextBlock descendants) in order…
            var blocks = MessageRows
                .SelectMany(row => row.GetLogicalDescendants().OfType<TextBlock>())
                .Select(block => block.Text ?? "")
                .Where(text => text.Length > 0);

            // …plus the raw markdown carried as each viewer's Tag, so fenced code text is present too.
            var rawMarkdown = MessageRows
                .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
                .Select(viewer => viewer.Tag as string ?? "")
                .Where(text => text.Length > 0);

            return string.Join("\n", blocks.Concat(rawMarkdown));
        }
    }

    /// <summary>One entry per painted row, holding the texts that row shows (a Markdown layer contributes the
    /// document it rendered, so a row can be named by what it says). The incremental renderer's whole claim is
    /// that each turn is painted once, in order — and that is only checkable from outside as a list of rows.</summary>
    internal IReadOnlyList<string> PaintedRowsForCheck => MessageRows
        .Select(row => string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "").Where(text => text.Length > 0)
                .Concat(row.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
                    .Select(viewer => viewer.Tag as string ?? ""))
                .Where(text => text.Length > 0)))
        .ToArray();

    // ── the folded activity runs and the live thinking glyph ──

    /// <summary>How many activity runs the transcript folded into a collapsible group. One run of tool calls and
    /// thinking is one head, so this counts heads, not turns.</summary>
    internal int ActivityGroupCountForCheck
        => MessageFlow.Children.Count(child => child.Classes.Contains("activity-group"));

    /// <summary>The Nth group's head sentence: the tool count when there were calls, otherwise the measured
    /// thinking duration — "执行工具 0 次" is a count of nothing, so a thought-only run says how long it thought.</summary>
    internal string ActivityGroupTitleForCheck(int index)
        => ActivityGroupAt(index)?.GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(button => button.Classes.Contains("activity-group-head"))
            ?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "";

    internal bool ActivityGroupExpandedForCheck(int index)
        => ActivityGroupBodyAt(index) is { IsVisible: true };

    /// <summary>Which group control the Nth fold is painted from, as an instance token. A run that grows its own
    /// group leaves the token alone; a run that tears the flow down and rebuilds it hands back a fresh number even
    /// when the restored head looks identical — and the person who had it open would see it close.</summary>
    internal int ActivityGroupInstanceTokenForCheck(int index)
        => ActivityGroupAt(index) is { } group
            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group)
            : 0;

    /// <summary>The description text of every folded action row, in order. A tool exchange now renders as one
    /// <c>activity-row</c> (not a message-row), so this — not <c>PaintedRowsForCheck</c> — is what a check reads to
    /// prove each call produced exactly one row and a late-inserted result rebuilt rather than duplicated it.</summary>
    internal string[] ActivityRowsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("activity-row"))
            .Select(grid => grid.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("activity-row-text"))?.Text ?? "")
            .ToArray();

    /// <summary>The address chips drawn under a reply that the endpoint searched for, in document order. Empty
    /// unless a search was reported — which is what makes the negative case assertable: an answer nobody looked
    /// anything up for must not grow a source line.</summary>
    internal string[] SearchChipsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("source-chip"))
            .Select(border => border.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "")
            .ToArray();

    /// <summary>The full addresses behind those chips, in the same order — the chip shows a host and the tooltip
    /// carries the URL, and a check has to be able to tell "shows the host" from "sent the wrong address".</summary>
    internal string[] SearchChipTargetsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("source-chip"))
            .Select(border => ToolTip.GetTip(border) as string ?? "")
            .ToArray();

    /// <summary>The queries the reply reports, from the tooltip on its source line — the part of the record that
    /// is deliberately one hover away rather than a paragraph over the answer.</summary>
    internal string[] SearchTooltipsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<StackPanel>()
            .Where(panel => panel.GetLogicalChildren().OfType<TextBlock>()
                .Any(text => text.Classes.Contains("muted")
                             && text.Text == HubStrings.Get("MessageSources")))
            .Select(panel => ToolTip.GetTip(panel) as string ?? "")
            .ToArray();

    /// <summary>The addresses the source line could not afford, read off its 「+N」 fold's tooltip. A check that
    /// wants to prove folding loses nothing has to read the folded list rather than count chips and hope.</summary>
    internal string SearchOverflowTipForCheck
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("source-fold"))
            .Select(text => ToolTip.GetTip(text) as string ?? "")
            .FirstOrDefault() ?? "";

    /// <summary>How many buttons live inside action rows — must stay zero: a tool exchange is not a readable
    /// message, and acting on one half of a call/result pair orphans the other, so the folded row carries none.</summary>
    internal int ActivityRowActionCountForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("activity-row"))
            .Sum(grid => grid.GetLogicalDescendants().OfType<Button>().Count());

    // ── the per-row detail each folded action reveals on click ──
    // A shell holds one toggle and one detail panel, created together and in the same order, so the Nth toggle pairs
    // with the Nth detail by index. The detail's IsVisible is what the toggle's IsCheckedChanged drives, so the
    // expanded state is read off the panel rather than guessed from the button.

    internal int ActivityRowToggleCountForCheck
        => MessageFlow.GetLogicalDescendants().OfType<ToggleButton>()
            .Count(button => button.Classes.Contains("activity-row-toggle"));

    internal bool ActivityRowExpandedForCheck(int index)
        => ActivityRowDetailAt(index) is { IsVisible: true };

    /// <summary>Opens/closes a row's detail the way a person does — by setting the toggle's IsChecked, which is what
    /// the IsCheckedChanged wiring reads. A raised Click never flips a ToggleButton, so a check must not use one.</summary>
    internal void SetActivityRowExpandedForCheck(int index, bool expanded)
    {
        var toggle = MessageFlow.GetLogicalDescendants().OfType<ToggleButton>()
            .Where(button => button.Classes.Contains("activity-row-toggle"))
            .ElementAtOrDefault(index);
        if (toggle is not null) toggle.IsChecked = expanded;
    }

    /// <summary>The whole text a row's detail panel shows (labels, the formatted arguments, the payload), joined so a
    /// check can prove both the full command and the returned line are present once the row is opened.</summary>
    internal string ActivityRowDetailTextForCheck(int index)
        => ActivityRowDetailAt(index) is { } detail
            ? string.Join(" ~ ", detail.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "").Where(text => text.Length > 0))
            : "";

    private Border? ActivityRowDetailAt(int index)
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("activity-detail"))
            .ElementAtOrDefault(index);

    // ── the left-slot expander arrow: where it sits, what glyph it is, and the status it swaps with ──

    private IEnumerable<Avalonia.Controls.Shapes.Path> ActivityRowChevronsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .Where(path => path.Classes.Contains("activity-row-chevron"));

    /// <summary>One arrow per action row — the affordance lives on the left, not stranded on the right edge.</summary>
    internal int ActivityRowChevronCountForCheck => ActivityRowChevronsForCheck.Count();

    /// <summary>The grid column the Nth row's arrow sits in: 0 (the status slot) is the whole point of the move.</summary>
    internal int ActivityRowChevronColumnForCheck(int index)
        => ActivityRowChevronsForCheck.ElementAtOrDefault(index) is { } chevron
            ? Grid.GetColumn(chevron)
            : -1;

    /// <summary>Whether the arrow is the sidebar's own glyph, so the chat's expander reads as the same affordance as
    /// the workspace groups in the left rail rather than a look-alike.</summary>
    internal bool ActivityRowChevronUsesSidebarGlyphForCheck
        => ActivityRowChevronsForCheck.Any(path => ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.ChevronRight")));

    /// <summary>The first row's status glyph size — asserted small so the ✓/✕ stay quiet under the 10px arrow.</summary>
    internal double ActivityRowStatusSizeForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("activity-status"))?.Width ?? -1;

    /// <summary>Whether the group head borrows the sidebar's Expander header theme. That theme is what keeps a
    /// checked head from painting the Fluent accent plate — the purple pill — and gives it the shared left arrow.</summary>
    internal bool ActivityGroupHeadUsesExpanderForCheck
    {
        get
        {
            var head = MessageFlow.GetLogicalDescendants().OfType<ToggleButton>()
                .FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
            if (head?.Theme is null) return false;
            var app = Application.Current;
            return app is not null
                   && app.TryGetResource("HubExpanderHeader", app.ActualThemeVariant, out var theme)
                   && ReferenceEquals(head.Theme, theme);
        }
    }

    /// <summary>Folds/unfolds a group the way a person does — by setting the head's IsChecked, which is what the
    /// IsCheckedChanged wiring reads. A raised Click never flips a ToggleButton, so a check must not use one.</summary>
    internal void SetActivityGroupExpandedForCheck(int index, bool expanded)
    {
        var head = ActivityGroupAt(index)?.GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
        if (head is not null) head.IsChecked = expanded;
    }

    private Control? ActivityGroupAt(int index)
        => MessageFlow.Children.Where(child => child.Classes.Contains("activity-group"))
            .ElementAtOrDefault(index);

    private StackPanel? ActivityGroupBodyAt(int index)
        => ActivityGroupAt(index)?.GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Name == "ActivityGroupBody");

    /// <summary>The live bubble's glyph: which phase it is morphing through, whether its clock is running, and how
    /// many tools the run has touched. A glyph stuck in one shape while a tool runs is the bug these read.</summary>
    internal ActivityPhase LiveGlyphPhaseForCheck => _live?.Glyph.Phase ?? ActivityPhase.ShowingText;
    internal bool LiveGlyphTickingForCheck => _live?.Glyph.IsTickingForCheck ?? false;
    internal int LiveGlyphToolCountForCheck => _live?.Glyph.ToolCount ?? 0;

    /// <summary>How many times the live bubble's 350ms tick has run. A check waits on this to prove the tick that
    /// drives both the elapsed label and the glyph's phase has actually fired, rather than reading the chrome the
    /// instant it was attached. The label cannot serve as that proof: it is seeded from the run, so a freshly
    /// attached bubble already reads whatever the answer has taken — which is exactly what a check switching
    /// sessions and back wants to assert, not what it wants to wait on.</summary>
    internal int LiveFrameForCheck => _live?.Frame ?? 0;
    internal bool LiveGlyphVisibleForCheck => _live?.Glyph.IsVisible ?? false;

    internal bool HasVisibleMarkdownCodeBlock(string code)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => viewer.Markdown?.Contains(code, StringComparison.Ordinal) == true
                           && viewer.Bounds.Width > 0
                           && viewer.Bounds.Height > 0);

    /// <summary>Whether any Markdown viewer on screen was handed this text. Read as the negative it is: a leaked
    /// tool call has to reach the reader as plain text, and the one proof of that is that no viewer ever took
    /// it — because a viewer is exactly where its pipes turn into a table.</summary>
    internal bool HasMarkdownHolding(string text)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => viewer.Markdown?.Contains(text, StringComparison.Ordinal) == true);

    internal bool HasRenderedMarkdownLink(string url)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => MarkdownMessageRenderer.HasLinkHandler(viewer, url));

    internal bool HasThemedMarkdownLink()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedLink);

    internal bool HasSyntaxHighlightedCode(string language)
        => MessageFlow.GetLogicalDescendants().OfType<TextEditor>()
            .Any(editor => string.Equals(editor.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase)
                           && editor.SyntaxHighlighting is not null);

    /// <summary>What a fenced block was actually handed. The definition's name travels with the answer because
    /// yes-or-no cannot tell a palette that was lifted for the dark well from a language that has no definition
    /// at all — and the second one is fine, while the first is the bug this reads for.</summary>
    internal (bool Found, bool Resolved, bool Adapted, string Definition) DarkSyntaxForCheck(string language)
    {
        foreach (var editor in MessageFlow.GetLogicalDescendants().OfType<TextEditor>())
        {
            if (!string.Equals(editor.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase)) continue;
            return editor.SyntaxHighlighting is { } definition
                ? (true, true, DarkSyntax.IsAdapted(definition), definition.Name)
                : (true, false, false, "（无定义）");
        }

        return (false, false, false, "（没有这个代码块）");
    }

    internal bool HasMarkdownCopyToolbar()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasCopyToolbar);

    internal bool HasScrollableMarkdownTable()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasScrollableTable);

    internal bool HasThemedMarkdownTable()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedTable);

    internal bool HasThemedMarkdownDocument()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedDocument);

    internal void ApplyMarkdownSyntaxHighlightingForCheck()
    {
        foreach (var viewer in MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
        {
            MarkdownMessageRenderer.ApplySyntaxHighlighting(viewer);
        }
    }

    /// <summary>The selected model formatted as text — the model no longer has a dedicated label, but the
    /// composer chip and this value still expose it.</summary>
    internal string ActiveModelText => _chat.SelectedChatModel is { } choice
        ? string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("ActiveModelFormat"),
            choice.Provider.Name,
            choice.ModelName)
        : HubStrings.Get("NoAvailableChatModels");

    internal int ModelChoiceCount => _chat.AvailableChatModels.Count;
    internal string SelectedModelText => SelectedModelLabel.Text ?? "";

    internal int BubbleCount => MessageRows.Count();

    internal int MessageActionCount => MessageFlow.GetLogicalDescendants().OfType<Button>()
        .Count(button => button.Classes.Contains("message-action"));

    internal bool ScrollToBottomVisible => ScrollToBottomButton.IsVisible;

    /// <summary>The floating button's shape: a square plate, not the pill it used to be. Read off the
    /// properties rather than <c>Bounds</c> because the button is collapsed until the flow scrolls.</summary>
    internal bool ScrollToBottomIsSquarePlateForCheck
        => ScrollToBottomButton is { Width: 28, Height: 28, CornerRadius: { TopLeft: 8 } };

    /// <summary>Whether the button carries the down-arrow glyph, matched against the resource itself so a
    /// different arrow cannot pass.</summary>
    internal bool ScrollToBottomShowsArrowForCheck
    {
        get
        {
            if (ScrollToBottomButton.GetLogicalDescendants()
                    .OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()?.Data is not { } data) return false;
            return Application.Current is { } app
                   && app.TryGetResource("Hub.Icon.ArrowDown", app.ActualThemeVariant, out var arrow)
                   && ReferenceEquals(data, arrow);
        }
    }

    /// <summary>The first rendered message row, so a check can prove a reload reuses it instead of rebuilding
    /// the whole flow (the incremental path's whole point).</summary>
    internal object? FirstBubbleForCheck => MessageRows.FirstOrDefault();

    // ── Approval card ──
    // The pending card lives in the composer's decision host now, not in the transcript: the host is the one
    // place a decision is answered, so it is the one place a check looks. The transcript keeps a signpost line
    // (class approval-pending-line) which is deliberately NOT counted here — these accessors mean "actionable
    // right now", and FlowApprovalCardCountForCheck below is the negative half that proves the move happened.
    private IEnumerable<Border> ApprovalCards
        => DecisionHost.IsVisible && _decisionCallId is not null ? [DecisionHost] : [];

    private IEnumerable<Border> PlanApprovalCards
        => DecisionHost.IsVisible && _decisionPlanIndex >= 0 ? [DecisionHost] : [];

    /// <summary>The old walk, kept as the negative half of "the card moved": a transcript that still paints an
    /// actionable approval card is a regression this number catches.</summary>
    internal int FlowApprovalCardCountForCheck
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Border>())
            .Count(card => card.Classes.Contains("approval-card"));

    /// <summary>How many calls are asking. A request the user has to answer is never hover-gated — a hidden
    /// actionable request looks exactly like a reply that stopped working — so this reads the flow as painted.</summary>
    internal int PendingApprovalCardsForCheck => ApprovalCards.Count();

    internal Border? ApprovalCardForCheck => ApprovalCards.FirstOrDefault();

    internal bool ApprovalCardOnScreenForCheck
        => ApprovalCards.FirstOrDefault() is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };

    internal string ApprovalCardTextForCheck
        => ApprovalCards.FirstOrDefault() is { } card
            ? string.Join(" | ", card.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "").Where(text => text.Length > 0))
            : "";

    internal string[] ApprovalCardActionsForCheck
        => ApprovalCards.FirstOrDefault() is { } card
            ? card.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("approval-action"))
                .Select(button => button.Tag as string ?? "").ToArray()
            : [];

    /// <summary>Whether any of the card's buttons is drawn as a destructive act. Refusing a tool call is an
    /// ordinary answer to an ordinary question, and painting it as a detonation is how a permission UI ends up
    /// trained on saying yes.</summary>
    internal bool ApprovalCardActionsAreCalmForCheck
        => ApprovalCards.FirstOrDefault() is { } card
           && !card.GetLogicalDescendants().OfType<Button>().Any(button => button.Classes.Contains("destructive"));

    internal void ClickApprovalActionForCheck(string actionKey)
    {
        var button = ApprovalCards.FirstOrDefault()?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == actionKey);
        if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal int PendingPlanApprovalCardsForCheck => PlanApprovalCards.Count();

    internal bool PlanApprovalCardOnScreenForCheck
        => PlanApprovalCards.FirstOrDefault() is { IsVisible: true } card
           && card.Bounds is { Width: > 0, Height: > 0 };

    internal string PlanApprovalMessageTextForCheck
        => string.Join(" ", MessageFlow.Children
            .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
            .Select(viewer => viewer.Tag as string ?? ""));

    internal bool PlanApprovalMarkdownOnScreenForCheck
        => MessageFlow.Children
            .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
            .FirstOrDefault(viewer => (viewer.Tag as string)?.Contains("Reviewed plan", StringComparison.Ordinal) == true)
            is { IsVisible: true, Bounds: { Width: > 0, Height: > 0 } };

    // ── the plan's card in the transcript ──

    private IEnumerable<Button> PlanCards
        => MessageRows.SelectMany(row => row.GetLogicalDescendants().OfType<Button>())
            .Where(card => card.Classes.Contains("plan-card"));

    internal int PlanCardCountForCheck => PlanCards.Count();

    /// <summary>What each card actually reads, in transcript order: the label and the title, joined. The glyph is
    /// a Path and carries no text, so this string is the whole of what a person sees on the card.</summary>
    internal string[] PlanCardTextsForCheck
        => PlanCards.Select(card => string.Join(" · ", card.GetLogicalDescendants().OfType<TextBlock>()
            .Select(block => block.Text ?? "").Where(text => text.Length > 0))).ToArray();

    internal string[] PlanCardStateKeysForCheck
        => PlanCards.Select(card => card.Tag as string ?? "").ToArray();

    /// <summary>The negative half, and the point of the card: a plan row that still paints its body — as
    /// rendered Markdown, or as the plain block the Markdown pass replaces — is the transcript back to one
    /// document per message. Whether the body survives at all is not this assertion's business and is already
    /// pinned where the stored turn text and the approved instruction are compared.</summary>
    internal bool PlanBodiesSuppressedForCheck
        => MessageRows
            .Where(row => row.GetLogicalDescendants().OfType<Button>()
                .Any(card => card.Classes.Contains("plan-card")))
            .All(row => !row.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Any()
                         && !row.GetLogicalDescendants().OfType<TextBlock>()
                             .Any(text => text.Classes.Contains("turn-text")));

    /// <summary>Whether a card is drawn rather than merely in the tree: a zero-bounds card passes a count and is
    /// still invisible.</summary>
    internal bool PlanCardOnScreenForCheck(int index)
        => PlanCards.ElementAtOrDefault(index) is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };

    /// <summary>Presses the nth card (transcript order) through its own Click, so a check drives the wiring from
    /// the card into the shell's inspector instead of calling the shell and proving nothing.</summary>
    internal void ClickPlanCardForCheck(int index)
        => PlanCards.ElementAtOrDefault(index)
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The plan review's two lettered choices, in the order offered: A approves, B asks for a revision.
    /// There is deliberately no third "exit plan mode" row — the owner removed it, and the workspace has no such
    /// decision to record.</summary>
    internal string[] PlanReviewChoicesForCheck
        => PlanApprovalCards.FirstOrDefault() is { } card
            ? card.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("plan-review-option"))
                .Select(button => button.Tag as string ?? "").ToArray()
            : [];

    internal bool PlanReviewHasContinueForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Name == "PlanContinueButton") == true;

    internal bool PlanReviewHasCancelForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Tag as string == "ChatPlanCancel") == true;

    private Button? PlanOptionRowForCheck(string tag)
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == tag
                                       && candidate.Classes.Contains("plan-review-option"));

    private TextBox? PlanFeedbackBoxForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "PlanFeedbackBox");

    /// <summary>The letter cap inside one option row, as painted. An armed cap once took the very fill the plate
    /// under it wore and its letter disappeared into the card; a build passes that, so the fills are what gets
    /// read back rather than the class names that produce them.</summary>
    internal Border? PlanCapForCheck(string tag)
        => PlanOptionRowForCheck(tag)?.GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(cap => cap.Classes.Contains("plan-review-key"));

    /// <summary>Which option is armed, or empty when none is. Nothing read the <c>selected</c> class before this,
    /// so a card that kept lighting the same cap whatever happened would pass every check that only clicks and
    /// counts.</summary>
    internal string PlanSelectedChoiceForCheck
        => PlanReviewChoicesForCheck.FirstOrDefault(tag =>
            PlanOptionRowForCheck(tag)?.Classes.Contains("selected") == true) ?? "";

    /// <summary>The card's painted height. The flat list's whole promise is that choosing a line moves nothing,
    /// and a hand-checked margin is exactly the kind of 8px thing that comes back, so the height is measured.</summary>
    internal double PlanReviewCardHeightForCheck => PlanApprovalCards.FirstOrDefault()?.Bounds.Height ?? 0;

    /// <summary>The card as a visual, for the frames a check photographs.</summary>
    internal Border? PlanReviewCardForCheck => PlanApprovalCards.FirstOrDefault();

    internal Button? PlanContinueButtonForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "PlanContinueButton");

    internal string PlanFeedbackTextForCheck => PlanFeedbackBoxForCheck?.Text ?? "";

    /// <summary>The revision line's painted height, and the height its own text asked for. A box arranged shorter
    /// than it measured is clipping what was typed into it — the one failure mode an inline line can have that a
    /// screenshot of an empty one never shows.</summary>
    internal double PlanFeedbackHeightForCheck => PlanFeedbackBoxForCheck?.Bounds.Height ?? 0;

    internal double PlanFeedbackDesiredHeightForCheck => PlanFeedbackBoxForCheck?.DesiredSize.Height ?? 0;

    internal bool PlanFeedbackClippedForCheck
        => PlanFeedbackBoxForCheck is { } box && box.Bounds.Height + 0.5 < box.DesiredSize.Height;

    /// <summary>
    /// Presses a key while the plan card is up, through the routed event the way
    /// <see cref="PressComposerKeyForCheck"/> does, so a check exercises the wiring rather than the private method
    /// the wiring happens to call. <paramref name="inRevisionLine"/> sends it from the caret's own control, which
    /// is how "A and B are letters while somebody is typing" and "Escape leaves the line" reach the handler.
    /// Returns whether the card took the key, so the negative half is asserted instead of assumed.
    /// </summary>
    internal bool PressPlanCardKeyForCheck(Key key, bool inRevisionLine = false,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var source = inRevisionLine ? (Control?)PlanFeedbackBoxForCheck : DecisionHost;
        if (source is null) return false;
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = source,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>Moves the caret into the revision line the way a tab does, so "nothing moves" is measured for
    /// focus as well as for clicks.</summary>
    internal void FocusPlanFeedbackForCheck() => PlanFeedbackBoxForCheck?.Focus();

    /// <summary>The revision line is option B itself now, so it is on screen from the first frame rather than
    /// opened by choosing something.</summary>
    internal bool PlanFeedbackVisibleForCheck
        => PlanFeedbackBoxForCheck is { IsVisible: true, Bounds: { Width: > 0 } };

    internal bool PlanContinueEnabledForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "PlanContinueButton")?.IsEnabled == true;

    internal void ClickPlanChoiceForCheck(string tag)
    {
        var button = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == tag
                                       && candidate.Classes.Contains("plan-review-option"));
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Types into the revision box through its own Text setter, so the "keep going is disabled until
    /// there is something to send" wiring is exercised rather than bypassed.</summary>
    internal void SetPlanFeedbackForCheck(string text)
    {
        var box = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(candidate => candidate.Name == "PlanFeedbackBox");
        if (box is not null) box.Text = text;
    }

    internal void ClickPlanContinueForCheck()
    {
        var button = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Name == "PlanContinueButton");
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal void ClickPlanCancelForCheck()
    {
        var button = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == "ChatPlanCancel");
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The one-line records of calls already decided, in the order they appear.</summary>
    internal string[] ApprovalRecordsForCheck
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Border>())
            .Where(record => record.Classes.Contains("approval-record"))
            .Select(record => record.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault()?.Text ?? "")
            .ToArray();

    // ── the undo on a write's record line ──
    private IEnumerable<Button> UndoButtons
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Button>())
            .Where(button => button.Classes.Contains("undo-action"));

    /// <summary>How many writes still have a copy behind them. One per spent revert is the whole design: the
    /// button is the record that the pre-image exists.</summary>
    internal int UndoButtonsForCheck => UndoButtons.Count();

    internal string[] UndoButtonTipsForCheck
        => UndoButtons.Select(button => ToolTip.GetTip(button)?.ToString() ?? "").ToArray();

    /// <summary>Presses the <paramref name="index"/>th undo the way a person presses it. A missing button raises
    /// nothing rather than throwing, so the assertion that follows reports a failed expectation instead of a
    /// crashed suite.</summary>
    internal void ClickUndoForCheck(int index)
    {
        var button = UndoButtons.ElementAtOrDefault(index);
        if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Appends a notice row through the real path, so the row's layout can be read back off the
    /// controls instead of inferred from code.</summary>
    internal void AppendNoticeForCheck(string text, bool danger = false) => AppendNotice(text, danger);

    /// <summary>The last notice row's column layout — (icon column, text column, declared column count) —
    /// or null when no notice is on screen. Notice rows live below the message flow, so this
    /// never disturbs the bubble assertions.</summary>
    internal (int IconColumn, int TextColumn, int Columns)? LastNoticeLayoutForCheck
    {
        get
        {
            if (CurrentNoticeRow is not { } row || row.Children.Count < 2)
            {
                return null;
            }

            return (Grid.GetColumn(row.Children[0]), Grid.GetColumn(row.Children[1]), row.ColumnDefinitions.Count);
        }
    }

    // ── Composer (send button state, chip, focus highlight) ──
    internal void SetComposerFocusForCheck(bool focused) => SetComposerFocus(focused);
    internal bool ComposerFocusedForCheck => ComposerFrame.Classes.Contains("focused");
    internal IBrush? ComposerBorderBrushForCheck => ComposerFrame.BorderBrush;
    internal bool SendButtonEnabledForCheck => SendButton.IsEnabled;
    internal bool PermissionChipEnabledForCheck => PermissionChip.IsEnabled;
    internal bool ChatActivityVisibleForCheck => _live is { Timer: not null };
    internal string ChatActivityTextForCheck => _live?.Status.Text ?? "";
    internal string ChatActivityElapsedForCheck => _live?.Elapsed.Text ?? "";
    internal double MessageFlowWidthForCheck => MessageFlow.Bounds.Width;
    internal double MessageRowWidthForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?.Bounds.Width ?? -1;
    internal double UserPillRightForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("user-pill"))?.Bounds.Right ?? -1;
    internal double LiveRowWidthForCheck => _live?.Row.Bounds.Width ?? -1;
    internal double LiveActivityLeftForCheck
        => _live?.Row.GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(stack => stack.Classes.Contains("chat-activity"))?.Bounds.X ?? -1;
    internal string? LastNoticeTextForCheck
        => CurrentNoticeRow?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

    /// <summary>Whether the notice strip is on screen at all — the rebuild-survival question, which the text alone
    /// cannot answer because the stored string and the painted row are two different facts.</summary>
    internal bool NoticeVisibleForCheck => CurrentNoticeRow is not null;

    /// <summary>Identity of the painted notice row. A repaint makes a new one, so two different tokens across a
    /// rebuild say the strip was cleared and filled again — which is the only way to tell "it survived" apart
    /// from "nothing ever erased it".</summary>
    internal int NoticeInstanceTokenForCheck
        => CurrentNoticeRow is { } row
            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(row)
            : 0;

    /// <summary>Whether the transcript is looking at its newest line. The plate below is derived from the same
    /// three numbers, so this is the fact and the plate is the symptom.</summary>
    internal bool ViewAtBottomForCheck
        => MessageScroller.Offset.Y + MessageScroller.Viewport.Height
           >= MessageScroller.Extent.Height - StickEpsilon;

    /// <summary>Moves the transcript's reading position the way a wheel or a drag on the bar would. A check that
    /// asks where a session reopens has to be able to leave it somewhere first.</summary>
    internal void ScrollTranscriptForCheck(double offset)
    {
        MessageScroller.Offset = new Point(0, offset);
        UpdateScrollAffordance();
    }

    /// <summary>Where the transcript is being read. The bottom flag says whether the newest line is in view; only
    /// the offset says whether a repaint left the reader where they had put themselves.</summary>
    internal double FlowOffsetForCheck => MessageScroller.Offset.Y;

    internal string InputTextForCheck => InputBox.Text ?? "";

    /// <summary>How many times the send button's state has been recomputed. A check asserts it grows when the
    /// input text changes, which is what proves the change notification is actually wired up.</summary>
    internal int SendStateUpdates { get; private set; }
    internal string SendButtonTooltipForCheck => ToolTip.GetTip(SendButton)?.ToString() ?? "";
    internal bool ModelPickerIsChipForCheck => ModelPicker.Classes.Contains("chip");
    internal bool ModelPickerUsesContentWidthForCheck => double.IsNaN(ModelPicker.Width);
    internal string SelectedModeForCheck => _chat.ActiveMode;
    internal string SelectedReasoningForCheck => _chat.ActiveReasoningEffort;
    internal string ReasoningChipTextForCheck => SelectedReasoningLabel.Text ?? "";
    internal bool ReasoningPickerEnabledForCheck => SelectedReasoningLabel.IsVisible;
    internal double ContextArcForCheck => ContextRing.Usage;

    /// <summary>The whole reading behind the ring, absolute numbers and all. A ratio alone cannot tell a session
    /// that is full from one that no longer fits, which is the distinction the panel exists to make.</summary>
    internal ContextUsage ContextReadingForCheck => CurrentContextUsage();

    internal int ContextUsedForCheck => CurrentContextUsage().Used;
    internal string[] ContextCategoryRowsForCheck => ContextCategoryList.Children.OfType<Grid>()
        .Select(row => string.Join("=", row.Children.OfType<TextBlock>().Select(cell => cell.Text)))
        .ToArray();
    internal string ContextPopoverSourceForCheck => ContextPopoverSource.Text ?? "";
    internal string ContextPopoverNoteForCheck => ContextPopoverNote.Text ?? "";
    internal string ContextTooltipForCheck => ToolTip.GetTip(ContextButton)?.ToString() ?? "";
    internal string ContextPopoverTitleForCheck => ContextPopoverTitle.Text ?? "";
    internal string ContextPopoverPercentForCheck => ContextPopoverPercent.Text ?? "";
    internal bool ContextPopoverOpenForCheck => ContextPopup.IsOpen;
    internal bool ContextCompressButtonEnabledForCheck => CompressContextButton.IsEnabled;
    internal string ContextCompressButtonTextForCheck => CompressContextButton.Content?.ToString() ?? "";
    internal Visual ContextPopoverContentForCheck => (Visual)ContextPopup.Child!;
    internal Task? ContextCompressionTaskForCheck => _contextCompressionTask;
    internal void OpenContextPopoverForCheck() => ShowContextMenu();
    internal void CloseContextPopoverForCheck() => ContextPopup.IsOpen = false;
    internal void ClickContextCompressForCheck()
        => CompressContextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal int ContextAttachmentCountForCheck => _contextAttachments.Count;

    /// <summary>Pictures the composer is holding, in the order they would be sent.</summary>
    internal int PendingPictureCountForCheck => _pendingPictures.Count;

    /// <summary>Drives the same entrance the file picker hands back, so a check can prove the chip, the send and
    /// the stored file without a dialog nobody can automate (the picker's own decision is asserted in
    /// <c>Pickers.Resolve</c>).</summary>
    internal void AddImageForCheck(string path) => AddPictureFromPath(path);

    /// <summary>Feeds the composer a picture that never was a file — which is what a clipboard capture becomes
    /// once it is encoded. Every entrance ends in the same admission, so this is the one that proves the shared
    /// path and the one that a pasted screenshot goes through.</summary>
    internal void AddImageBytesForCheck(byte[] bytes, string name) => AddPendingPicture(bytes, name);

    /// <summary>The composer's drop target: what "you can drop a screenshot here" is decided by.</summary>
    internal bool ComposerAcceptsDropForCheck => DragDrop.GetAllowDrop(ComposerFrame);

    /// <summary>Pictures painted into the message flow, counted by the frame each one sits in.</summary>
    internal int RenderedPictureCountForCheck
        => MessageFlow.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("turn-picture"));

    /// <summary>Chips in the composer that carry a thumbnail rather than only a name. Read off the shape of the
    /// chip rather than its visual descendants: a chip built a moment ago has not been through a layout pass
    /// yet, and "does this attachment show the picture" cannot depend on when the check happens to look.</summary>
    internal int PictureChipsForCheck
        => ContextAttachmentPanel.Children.OfType<Border>()
            .Count(border => border.Child is StackPanel panel && panel.Children.Any(IsThumbnailFace));

    private static bool IsThumbnailFace(Control face)
        => face is Image or Button { Content: Image };

    /// <summary>Presses one chip's own × rather than removing the entry here: what a check has to prove is that
    /// the button takes the picture out of the message, not that the list can be edited.</summary>
    internal void RemovePictureChipForCheck(int index)
    {
        var buttons = ContextAttachmentPanel.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Tag is PendingPicture)
            .ToList();
        if (index < buttons.Count) buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Hands the paste path a clipboard payload of a known shape. The real Ctrl+V reads the system clipboard,
    /// which a self-check cannot fill without overwriting what the person had copied — and the part worth
    /// asserting is what the composer decides once it has the payload, not whether Windows answered.
    /// </summary>
    internal Task<bool> PasteTransferForCheck(IAsyncDataTransfer transfer) => TakeClipboardAsync(transfer);

    /// <summary>Raises the composer's own drop event, so the drop target and the file extraction run exactly as
    /// they do when a screenshot is dragged in from Explorer.</summary>
    internal void DropForCheck(IDataTransfer transfer)
        => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, ComposerFrame,
            default, KeyModifiers.None));

    /// <summary>Sends exactly what the composer holds, which is how a picture with no text under it is sent.</summary>
    internal Task SendComposerForCheck() => SendAsync();

    // ── The drag ring and the picture preview ──
    /// <summary>Whether the composer is answering a drag right now. The ring is the only thing that says so, and
    /// it is decided by the same handler a real drag runs.</summary>
    internal bool ComposerDragOverForCheck => ComposerFrame.Classes.Contains("drag-over");

    /// <summary>Raises the composer's own DragOver handler with a payload of the asked-for shape.</summary>
    internal void RaiseComposerDragOverForCheck(IDataTransfer transfer)
        => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer, ComposerFrame,
            default, KeyModifiers.None));

    internal void RaiseComposerDragLeaveForCheck()
        => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, new DataTransfer(),
            ComposerFrame, default, KeyModifiers.None));

    /// <summary>The hint the composer gives before anyone types: pasting and dropping are interactions a person
    /// has to be told about once, and the placeholder is the only place that names them.</summary>
    internal string ComposerPlaceholderForCheck => InputBox.PlaceholderText ?? "";

    /// <summary>Clicks the Nth picture in the transcript through the button that opens it. The picture now
    /// opens in the window-level viewer, so the assertion of what opened lives on <c>MainWindow</c>; this only
    /// proves the click routes.</summary>
    internal void ClickRenderedPictureForCheck(int index)
    {
        var faces = MessageFlow.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("picture-face"))
            .ToList();
        if (index < faces.Count) faces[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Clicks the Nth chip's thumbnail in the composer — the draft's picture, which never was a file.
    /// Walked off the chip's shape for the same reason the count is: a chip added a moment ago may not have been
    /// laid out yet, and a click that depends on that is a click that depends on nothing.</summary>
    internal void ClickPendingPictureForCheck(int index)
    {
        var faces = ContextAttachmentPanel.Children.OfType<Border>()
            .Select(border => border.Child as StackPanel)
            .Where(panel => panel is not null)
            .SelectMany(panel => panel!.Children)
            .OfType<Button>()
            .Where(button => button.Classes.Contains("picture-face"))
            .ToList();
        if (index < faces.Count) faces[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Sends Escape down the composer's own route, so the preview-first ordering is the real one.</summary>
    internal void PressEscapeForCheck() => PressComposerKeyForCheck(Key.Escape);

    /// <summary>Presses a key on the composer through the routed event, so the bindings themselves are what a
    /// check exercises — not the private methods they happen to call.</summary>
    internal void PressComposerKeyForCheck(Key key, KeyModifiers modifiers = KeyModifiers.None)
        => InputBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = InputBox,
        });

    /// <summary>How far into the recall walk the composer stands, and the newest sentences it can walk back to.
    /// The tail is what a check reads: the queue is capped, so a count would say one thing on a fresh window and
    /// another after an hour of the suite having sent things.</summary>
    internal int RecallStepForCheck => _recallIndex;
    internal string[] RecentSentTextsForCheck(int count)
        => _sentTexts.TakeLast(Math.Max(count, 0)).ToArray();

    /// <summary>
    /// Moves the keyboard off the text box and onto the <c>+</c> button. Without this the focus-after-send cell
    /// would prove nothing: the box already has focus, so a handler that never gave it back would still pass.
    /// </summary>
    internal void MoveFocusOffComposerForCheck() => AddContextButton.Focus();

    /// <summary>The session's attachment directory, as the store sees it. A check that claims a deleted session
    /// took its pictures with it has to look at the disk, not at a list this view keeps.</summary>
    internal string SessionImageDirectoryForCheck(string conversationId)
        => _chat.SessionImageDirectory(conversationId);
    internal bool HasComposerAddMenuForCheck => AddContextButton is not null;

    /// <summary>What the <c>+</c> menu offers right now, read from the menu that is actually built on click. A
    /// menu item that exists only in a screenshot is not a feature.</summary>
    internal string[] ComposerMenuHeadersForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>()
            .Select(item => item.Header?.ToString() ?? "").ToArray();
    internal bool ModeIndicatorVisibleForCheck => ModeIndicatorButton.IsVisible;
    internal bool ComposerPlusCenteredForCheck
        => AddContextButton.HorizontalContentAlignment == HorizontalAlignment.Center
           && AddContextButton.VerticalContentAlignment == VerticalAlignment.Center;
    internal bool ContextRingPrecedesModelForCheck
        => ContextButton.Parent is Panel panel
           && panel.Children.IndexOf(ContextButton) < panel.Children.IndexOf(ModelPicker);
    internal bool PermissionChipFollowsPlusForCheck
        => PermissionChip.Parent is Panel panel
           && panel.Children.IndexOf(PermissionChip) == panel.Children.IndexOf(AddContextButton) + 1;
    internal bool ModeIndicatorFollowsPermissionForCheck
        => ModeIndicatorButton.Parent is Panel panel
           && panel.Children.IndexOf(ModeIndicatorButton) == panel.Children.IndexOf(PermissionChip) + 1;
    internal bool ModeIndicatorKeepsLabelVisibleForCheck
        => ModeIndicatorLabel.IsVisible && ModeIndicatorLabel.Parent is Grid grid
           && grid.ColumnDefinitions.Count == 2
           && Grid.GetColumn(ModeIndicatorLabel) == 1;
    internal bool ModeIndicatorIconMatchesSelectedModeForCheck
        => ReferenceEquals(ModeIndicatorIcon.Data, ThemeGeometry(ComposerModeIconKey(_selectedComposerMode)));
    internal bool ModeIndicatorCloseIsRedForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
           && close.Foreground is ISolidColorBrush closeBrush
           && closeBrush.Color.R > closeBrush.Color.G
           && closeBrush.Color.R > closeBrush.Color.B;
    internal string ModeIndicatorCloseColorForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close"))?.Foreground?.ToString() ?? "unset";
    internal bool ModeIndicatorCloseIsLeftAndCenteredForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
           && close.VerticalAlignment == VerticalAlignment.Center
           && close.Parent is Grid grid
           && Grid.GetColumn(close) == 0;
    internal (string Glyph, double FontSize, string FontFamily) ModeIndicatorCloseGlyphForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
            ? (close.Text ?? "", close.FontSize, close.FontFamily?.ToString() ?? "")
            : ("", 0, "");
    internal bool ComposerMenuModesHaveIconsForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => item.Header is StackPanel header
                         && header.Children.OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault() is { } icon
                         && ReferenceEquals(icon.Data, ThemeGeometry(ComposerModeIconKey(item.Tag?.ToString())))
                         && header.Children.OfType<TextBlock>().Any());
    internal string[] ComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>()
        .Where(IsComposerModeItem)
        .Select(item => (string)item.Tag!)
        .ToArray();
    internal string[] CheckedComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>()
        .Where(item => IsComposerModeItem(item) && item.IsChecked)
        .Select(item => (string)item.Tag!)
        .ToArray();
    internal bool ComposerMenuModeItemsCloseOnClickForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => !item.StaysOpenOnClick);
    internal bool ComposerMenuModesAreCheckboxesForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => item.ToggleType == MenuItemToggleType.CheckBox);
    internal string? SelectedComposerModeForCheck => _selectedComposerMode;

    // ── Permission chip and its menu ──
    // Each probe builds a fresh menu, which is the same object a user's click would have arrived on: the list
    // is composed from the session as it stands, not from state this view keeps.
    private static string MenuItemTitle(MenuItem item) => item.Header switch
    {
        Grid header => header.Children.OfType<StackPanel>().FirstOrDefault()?
            .Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
        string text => text,
        _ => "",
    };

    private static string MenuItemHint(MenuItem item)
        => item.Header is Grid header
            ? header.Children.OfType<StackPanel>().FirstOrDefault()?
                .Children.OfType<TextBlock>().ElementAtOrDefault(1)?.Text ?? ""
            : "";

    internal string PermissionChipLabelForCheck => PermissionChipLabel.Text ?? "";
    internal bool PermissionChipMarksDangerForCheck => PermissionChip.Classes.Contains("danger");
    internal bool PermissionChipIconIsDrawnForCheck => PermissionChipIcon.Data is not null;

    /// <summary>Both composer chips have to carry the same caret: on a chip that is otherwise plain text it is
    /// the only thing saying "this opens a menu", and a stroked triangle at this size collapses into a blob.
    /// </summary>
    internal bool ComposerChipsShareLineCaretForCheck => HasLineCaret(PermissionChip) && HasLineCaret(ModelPicker);

    private static bool HasLineCaret(Control anchor)
        => anchor.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("chip-caret")) is { } caret
           && ReferenceEquals(caret.Data, ThemeGeometry("Hub.Icon.Caret"))
           && caret.Fill is ISolidColorBrush { Color.A: 0 }
           && caret.Stroke is not null;

    /// <summary>Opens the permission menu through the same path the chip uses, and hands back an item of it:
    /// the flyout presents in its own top-level, so a screenshot has to be taken from inside.</summary>
    internal Control? OpenPermissionMenuForCheck()
    {
        ShowPermissionMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal Control? OpenComposerMenuForCheck()
    {
        ShowAddContextMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal string[] PermissionMenuTitlesForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Select(MenuItemTitle).ToArray();

    // ── Verification hooks for the workspace chip (used by --verify-shell) ──
    // The folder dialog is a modal and cannot be driven from a self-check, so these read the built state and
    // drive the workspace through ChatWorkspace instead — the same split AuthDialog's hooks use.

    internal string WorkspaceChipLabelForCheck => WorkspaceChipLabel.Text ?? "";
    internal bool WorkspaceChipVisibleForCheck => WorkspaceChip.IsVisible;
    internal string WorkspaceChipHintForCheck => ToolTip.GetTip(WorkspaceChip)?.ToString() ?? "";

    /// <summary>Where the picker sits, in the composer frame's own coordinates, and whether it is still inside
    /// that frame. The move is the change, so the placement is what gets pinned: (0, just under the frame) is
    /// "outside the chat box, bottom-left", and anything inside the frame is the old affordance back again.
    /// </summary>
    internal (double X, double Y, double FrameHeight, bool InsideFrame) WorkspaceChipPlateForCheck
    {
        get
        {
            var at = WorkspaceChip.TranslatePoint(new Point(0, 0), ComposerFrame) ?? default;
            return (at.X, at.Y, ComposerFrame.Bounds.Height, WorkspaceChip.GetLogicalAncestors()
                .Any(ancestor => ReferenceEquals(ancestor, ComposerFrame)));
        }
    }

    internal string[] WorkspaceMenuTitlesForCheck
        => BuildWorkspaceMenu().Items.OfType<MenuItem>().Select(MenuItemTitle).ToArray();

    internal Control? OpenWorkspaceMenuForCheck()
    {
        ShowWorkspaceMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    /// <summary>Clicks the "clear" row, which exists only while a directory is bound.</summary>
    internal bool ClickWorkspaceMenuClearForCheck()
    {
        var clear = BuildWorkspaceMenu().Items.OfType<MenuItem>()
            .FirstOrDefault(item => MenuItemTitle(item) == HubStrings.Get("ChatWorkspaceMenuClear"));
        if (clear is null) return false;
        clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    internal string[] PermissionMenuHintsForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Select(MenuItemHint).ToArray();

    internal int PermissionMenuCheckedCountForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Count(item => item.IsChecked);

    /// <summary>Which tier the menu marks as current, read off the tick's own visibility rather than a flag:
    /// the mark is what a person sees, so that is the thing that has to be right. -1 when nothing is marked.</summary>
    internal int PermissionMenuMarkedIndexForCheck
    {
        get
        {
            var items = BuildPermissionMenu().Items.OfType<MenuItem>().ToList();
            for (var i = 0; i < items.Count; i++)
            {
                // The row's two Paths are the tier's icon and then the tick; the text column is a panel.
                if (items[i].Header is Grid grid
                    && grid.Children.OfType<Avalonia.Controls.Shapes.Path>().LastOrDefault()
                        is { IsVisible: true } tick
                    && ReferenceEquals(tick.Data, ThemeGeometry("Hub.Icon.Tick"))) return i;
            }

            return -1;
        }
    }

    /// <summary>Whether the quiet "follow the default" row is offered — it exists only while this session has
    /// an override that could be dropped.</summary>
    internal bool PermissionMenuOffersFollowDefaultForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Any(item => item.Header is string);

    /// <summary>Clicks one entry of the permission menu, in the order the menu shows them.</summary>
    internal bool SelectPermissionMenuEntryForCheck(int index)
    {
        if (BuildPermissionMenu().Items.OfType<MenuItem>().ElementAtOrDefault(index) is not { } item) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    /// <summary>True when the round button currently shows the stop square. Compared by geometry identity
    /// rather than by string, because the icons are resolved from the same cached resource.</summary>
    internal bool SendIconIsStopForCheck
        => SendButton.Content is Avalonia.Controls.Shapes.Path path
           && ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.Stop"));

    /// <summary>The round button on its own, so a check can render just the circle and its glyph.</summary>
    internal Control SendButtonForCheck => SendButton;

    /// <summary>The colour the glyph is drawn in, read from its live brush. A pixel mask must not hardcode
    /// it: <c>Hub.Accent</c> is a different blue per theme, and a literal would fail on a theme switch.</summary>
    internal Color SendGlyphColorForCheck
        => SendButton.Content is Avalonia.Controls.Shapes.Path { Stroke: ISolidColorBrush brush }
            ? brush.Color
            : Colors.Transparent;

    /// <summary>
    /// Where the glyph's slot sits inside the button, in DIP, plus the width the hidden "…" affordance
    /// claims. Measured from layout bounds because it is the ink's placement *inside* the slot that goes
    /// wrong, not the slot's placement in the button — so the two together say whether a centring failure
    /// comes from layout or from Stretch. A negative affordance width means the template part was not
    /// found at all, which is a different failure from "it takes no space".
    /// </summary>
    internal (double Dx, double Dy, double SlotWidth, double AffordanceWidth) SendGlyphLayoutForCheck
    {
        get
        {
            var glyph = SendButton.Content as Visual;
            double x = 0, y = 0;
            for (var v = glyph; v is not null && v != SendButton; v = v.GetVisualParent())
            {
                x += v.Bounds.X;
                y += v.Bounds.Y;
            }
            var affordance = SendButton.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "PART_MoreAffordance");
            return (x + (glyph?.Bounds.Width ?? 0) / 2 - SendButton.Bounds.Width / 2,
                    y + (glyph?.Bounds.Height ?? 0) / 2 - SendButton.Bounds.Height / 2,
                    glyph?.Bounds.Width ?? 0,
                    affordance?.Bounds.Width ?? -1);
        }
    }

    internal void SetInputForCheck(string text) => InputBox.Text = text;

    internal bool SelectModeForCheck(string mode)
    {
        if (mode is not (ChatModes.Ask or ChatModes.Plan or ChatModes.Agent)) return false;
        SetComposerMode(mode);
        return true;
    }

    internal bool ClickComposerModeMenuForCheck(string mode)
    {
        var menu = BuildComposerMenu();
        var item = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), mode, StringComparison.Ordinal));
        if (item is null) return false;

        menu.ShowAt(AddContextButton);
        Dispatcher.UIThread.RunJobs();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return !menu.IsOpen;
    }

    internal void ResetModeForCheck()
        => ModeIndicatorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool IsComposerModeItem(MenuItem item)
        => item.Tag is string mode
           && (mode == ChatModes.Ask || mode == ChatModes.Plan || mode == ChatModes.Agent);

    internal bool SelectReasoningForCheck(string effort)
    {
        if (!ReasoningChoices.Any(choice => choice.Value == effort)) return false;
        return _chat.SelectReasoningEffort(effort);
    }

    internal bool SelectModelForCheck(string providerId, string modelName)
    {
        var choice = _chat.AvailableChatModels
            .FirstOrDefault(option => option.Provider.Id == providerId && option.ModelName == modelName);
        return choice is not null && _chat.SelectChatModel(providerId, modelName);
    }

    /// <summary>Types and sends, then waits for that session's run to be over. Sending no longer blocks a
    /// caller until the reply lands — that is the whole point of a run — so a check that wants the finished
    /// answer has to say so, one pump at a time.</summary>
    internal async Task SendForCheckAsync(string text)
    {
        InputBox.Text = text;
        await SendAsync();
        await WaitForRunToFinishForCheck();
    }

    /// <summary>Pumps the dispatcher until the viewed session's run is over. A check that wants the finished
    /// answer has to ask for it now: a reply arrives independently of whoever pressed send.</summary>
    internal async Task WaitForRunToFinishForCheck()
    {
        for (var wait = 0; wait < 200 && IsStreamingForCheck; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    /// <summary>Starts a send without awaiting it, so a check can inspect the mid-stream state (the button
    /// becomes a stop icon before the first token arrives).</summary>
    internal Task BeginSendForCheckAsync(string text)
    {
        InputBox.Text = text;
        return SendAsync();
    }

    private sealed record ContextAttachment(string Kind, string Name, string Path, string? Details = null);

    /// <summary>
    /// One picture the composer is holding. Bytes rather than a path because two of the three entrances have no
    /// file to point at — a pasted capture arrives as a bitmap, and what all three have in common is the bytes
    /// Hub would store.
    /// </summary>
    private sealed record PendingPicture(byte[] Bytes, string Name);

    internal bool BubbleHasAction(int visibleIndex, string textKey)
    {
        var row = MessageRows.ElementAtOrDefault(visibleIndex);
        return row is not null && row.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action")
                           && (string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal)
                               || string.Equals(button.Content?.ToString(), HubStrings.Get(textKey), StringComparison.Ordinal)));
    }

    internal bool BubbleHasIconAction(int visibleIndex, string textKey)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action-icon")
                           && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal)) == true;

    /// <summary>How many icon actions one bubble offers. Counted rather than sampled, because a button that
    /// quietly comes back is exactly the regression this row has already had once.</summary>
    internal int BubbleIconActionCount(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .Count(button => button.Classes.Contains("message-action-icon")) ?? -1;

    /// <summary>Rendered width of one icon action, so a plate that quietly grows back is caught.</summary>
    /// <summary>Raises one icon action by its string key, exactly as a click would.</summary>
    internal bool ClickBubbleAction(int visibleIndex, string textKey)
    {
        if (MessageRows.ElementAtOrDefault(visibleIndex)?
                .GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("message-action-icon")
                                          && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal))
            is not { } button) return false;

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>The in-place editor's cancel cross, measured the same way as the composer's: the two checks
    /// together are what keep one ✕ from being redrawn heavy while the other was softened.</summary>
    internal (string Glyph, double FontSize, string FontFamily) BubbleCancelGlyphForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("message-action-icon")
                                      && string.Equals(button.Tag?.ToString(), "Cancel", StringComparison.Ordinal))
            is { Content: TextBlock glyph }
            ? (glyph.Text ?? "", glyph.FontSize, glyph.FontFamily?.ToString() ?? "")
            : ("", 0, "");

    /// <summary>The in-place editor a bubble is hosting right now, or null while it shows plain text.</summary>
    internal TextBox? BubbleEditorForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Classes.Contains("message-edit"));

    internal bool IsStreamingForCheck => IsViewedStreaming;

    /// <summary>What the live bubble on screen says. It is read off the run, so this is also the proof that a
    /// reply kept arriving while its session was out of sight.</summary>
    internal string LivePreviewTextForCheck => _live?.Preview.Text ?? "";

    /// <summary>Presses the round button the way a click would, so stopping goes through the view instead of
    /// cancelling a run behind the panel's back.</summary>
    internal void ClickSendButtonForCheck() => SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal bool ForkNoticeVisibleForCheck => ForkNotice.IsVisible;
    internal string? ForkNoticeTextForCheck => ForkNotice.IsVisible ? ForkNotice.Content?.ToString() : null;
    internal void ClickForkNoticeForCheck() => ForkNotice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Moves focus out of an open inline editor, the way clicking anywhere else in the window would.</summary>
    internal void FocusComposerForCheck() => InputBox.Focus();

    // ── Empty state ──
    // The chips are asserted through the object graph and through a real ClickEvent, never by reading the text
    // table: what a person can act on is the button and the composer it fills, not the key behind it.
    internal bool EmptyStateVisibleForCheck => EmptyState.IsVisible;

    /// <summary>Effective, not local: the row keeps its own <c>IsVisible</c> when the empty state it lives in is
    /// hidden, and what a person needs to know is that the chips are neither drawn nor reachable.</summary>
    internal bool SuggestionRowVisibleForCheck => SuggestionPanel.IsEffectivelyVisible;
    internal int SuggestionChipCountForCheck => SuggestionPanel.Children.OfType<Button>().Count();

    internal string SuggestionChipTextForCheck(int index)
        => SuggestionPanel.Children.OfType<Button>().ElementAtOrDefault(index)?.Content as string ?? "";

    /// <summary>Clicks a chip the way a pointer would, so the fill-without-sending behaviour is what runs.</summary>
    internal void ClickSuggestionChipForCheck(int index)
    {
        if (SuggestionPanel.Children.OfType<Button>().ElementAtOrDefault(index) is { } chip)
            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal bool ComposerHasKeyboardFocusForCheck => InputBox.IsKeyboardFocusWithin;

    /// <summary>The keys the row is built from, so a check can compare the rendered chips against the text table
    /// instead of against a copy of the expected sentences — which would pass even if the table lost a key.</summary>
    internal string[] SuggestionKeysForCheck => SuggestionKeys;

    /// <summary>Sends one key to the in-place editor, so the Enter/Escape bindings themselves are what a check
    /// exercises rather than the method they call.</summary>
    internal bool SendKeyToBubbleEditor(int visibleIndex, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (BubbleEditorForCheck(visibleIndex) is not { } editor) return false;
        editor.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = editor,
        });
        return true;
    }

    internal double BubbleIconActionWidth(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("message-action-icon"))?.Bounds.Width ?? -1;

    internal double BubbleActionBarOpacity(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Classes.Contains("message-actions"))?.Opacity ?? -1;

    internal string? MessageTimestampTextForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("message-timestamp"))?.Text;

    internal string? MessageTimestampTooltipForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("message-timestamp")) is { } label
            ? ToolTip.GetTip(label)?.ToString()
            : null;

    /// <summary>The size a peer label is drawn at. <c>peer-origin</c> is the handle a check finds the line by; the
    /// quiet 12 it renders at is <c>muted</c>'s, so the two classes stay one decision rather than two.</summary>
    internal double PeerOriginFontSizeForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("peer-origin"))?.FontSize ?? 0;

    /// <summary>The size a turn's plain words are drawn at, and the size a Markdown body is drawn at. Only the
    /// last forty turns go through the Markdown layer, so the two have to agree or a message changes font as it
    /// scrolls away.</summary>
    internal double PlainTurnFontSizeForCheck
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("turn-text"))?.FontSize ?? 0;

    internal double MarkdownBodyFontSizeForCheck
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>().FirstOrDefault() is { } viewer
           && viewer.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>()
               .FirstOrDefault(text => !text.Classes.Any(name =>
                   name.StartsWith("Heading", StringComparison.Ordinal))) is { } body
            ? body.FontSize
            : 0;

    /// <summary>The font a rendered code block actually draws with — empty when the reply had no code to look at.
    /// The fenced block becomes a text editor rather than a text block, so this is the element that answers the
    /// question a person can see the answer to.</summary>
    internal string MarkdownCodeFontFamilyForCheck
        => MessageFlow.GetLogicalDescendants().OfType<TextEditor>().FirstOrDefault()
            ?.FontFamily?.ToString() ?? "";

    /// <summary>Who a bubble says wrote it — the peer label when the message came from another session, null when
    /// the user typed it. Asserted as text because an absent label is exactly the misreading it exists to stop.</summary>
    internal string? PeerOriginTextForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("peer-origin"))?.Text;

    /// <summary>How many bubbles on screen are labelled as another session's. Counted rather than sampled: a
    /// label on a turn nobody sent is the same bug as one that never appears.</summary>
    internal int PeerOriginCountForCheck => MessageFlow.GetLogicalDescendants()
        .OfType<TextBlock>().Count(block => block.Classes.Contains("peer-origin"));
}
