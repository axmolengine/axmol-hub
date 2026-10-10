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
using AxmolHub.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace AxmolHub;

/// <summary>
/// The two decisions a parked run can owe, and the one confirmation a running reply can owe — all three asked
/// in the composer rather than in a transcript row.
///
/// They live in one partial because they are one idea: something the person must answer before the composer
/// means what it looks like it means. A pending call and a pending plan share a host (one slot, so two cards
/// can never stack); a steer confirmation gets its own strip above the frame because it must show the draft
/// while leaving the box the draft goes back to.
/// </summary>
public partial class ChatPanel
{
    /// <summary>The call the host is currently asking about, or null. Kept beside the host rather than read
    /// back out of it so a refresh can tell "same decision, repaint" from "a different decision arrived".</summary>
    private string? _decisionCallId;

    /// <summary>The turn index of the plan the host is reviewing, or -1.</summary>
    private int _decisionPlanIndex = -1;

    /// <summary>Which plan option row is selected. Enter executes it, so there is always exactly one.</summary>
    private string _planSelectedOption = ChatPlanStates.Approve;

    /// <summary>A draft waiting for its second tap, with the session it was typed in: switching conversations
    /// must not carry it along, and coming back must not have lost it.</summary>
    private sealed record SteerDraft(string ConversationId, string Text, string? Context, IReadOnlyList<byte[]>? Pictures);

    private SteerDraft? _steerDraft;

    /// <summary>Set by the shell: how the panel asks for the inspector column, on a tab ("plan" or "changes")
    /// and for one specific plan turn (-1 for the newest). The plan's body lives nowhere else in the UI once the
    /// transcript row is a card, so this is the entrance — which is why the card hands over the index of the plan
    /// it names rather than letting the pane show a newer one under an older title.</summary>
    internal Func<string, int, bool>? OpenInspector { get; set; }

    /// <summary>Set by the shell: how the panel asks the inspector column to hide. The panel builds the pane's
    /// content but never owns whether the column is up — that is the shell's, gated on the page.</summary>
    internal Action? CloseInspector { get; set; }

    /// <summary>Set by the shell: how the pane asks the column to cover the whole window, and to go back. The
    /// same division as <see cref="CloseInspector"/> — the panel knows a tap happened, only the shell knows
    /// whether that means a wider column, an overlay, or nothing at all on this page.</summary>
    internal Action? ToggleInspectorExpanded { get; set; }

    /// <summary>The one inspector pane, kept for the window's life so switching tabs or conversations does not
    /// rebuild it and lose which file a person had expanded.</summary>
    private InspectorPanel? _inspector;

    /// <summary>
    /// Builds (once) and refreshes the inspector's content for the conversation on screen, landing on
    /// <paramref name="tab"/> and showing the plan <paramref name="turnIndex"/> names, or the newest one at -1.
    /// The pane is fed a snapshot rather than reaching into the workspace: the plan text
    /// is a plan turn's own markdown, and each file's diff is resolved lazily through
    /// <see cref="ChatWorkspace.DiffForChange"/> so a list of twelve writes reads no file until one is opened.
    /// </summary>
    internal Control? BuildInspectorContent(string tab, int turnIndex)
    {
        var conversation = _chat?.ActiveConversation;
        if (_inspector is null)
        {
            _inspector = new InspectorPanel();
            _inspector.CloseRequested += () => CloseInspector?.Invoke();
            _inspector.ExpandRequested += () => ToggleInspectorExpanded?.Invoke();
        }

        var planText = PlanTextFor(conversation, turnIndex);
        var changes = ChatChanges.Of(conversation);
        var conversationId = conversation?.Id ?? "";
        _inspector.Reload(
            planText,
            changes,
            change => _chat is null
                ? (null, UndoCopyState.Unreadable)
                : (_chat.DiffForChange(change, conversationId, out var state), state),
            tab);
        return _inspector;
    }

    /// <summary>The markdown of one plan: the turn the caller pointed at when that turn is still a plan of this
    /// conversation, otherwise the newest plan it holds. The fallback is what a stale pointer degrades to — a
    /// message deleted or regenerated since the card was drawn should move the pane to the plan that is really
    /// there, not leave it showing nothing.</summary>
    private static string? PlanTextFor(Conversation? conversation, int turnIndex)
    {
        if (conversation is null) return null;
        if (turnIndex >= 0 && turnIndex < conversation.Messages.Count
            && conversation.Messages[turnIndex] is { Role: ChatRoles.Assistant, PlanApprovalState: { Length: > 0 } } chosen
            && chosen.Text is { Length: > 0 } chosenText)
        {
            return chosenText;
        }

        return LatestPlanText(conversation);
    }

    /// <summary>The markdown of the newest plan this conversation produced — the pending one if a plan is
    /// waiting, otherwise the last one it answered, so the card and 「查看计划」 still show something after
    /// approval.</summary>
    private static string? LatestPlanText(Conversation? conversation)
    {
        if (conversation is null) return null;
        string? latest = null;
        foreach (var turn in conversation.Messages)
        {
            if (turn.Role != ChatRoles.Assistant) continue;
            if (turn.PlanApprovalState is not { Length: > 0 }) continue;
            if (turn.Text is { Length: > 0 } text) latest = text;
        }
        return latest;
    }

    // ── the host ──

    /// <summary>
    /// Brings the host in step with the transcript. Reads <b>only</b> persisted state — approval states, the
    /// frozen preview, the arguments — never <c>RunFor</c> or the run's phase: a decision can outlive its run
    /// (a restart, another window answering it), and the host has to show it either way. The temptation to gate
    /// on the run appears the moment the card becomes "the composer's", which is exactly when it would be wrong.
    /// </summary>
    private void RefreshDecisionHost()
    {
        var conversation = _chat?.ActiveConversation;
        string? callId = null;
        var planIndex = -1;
        if (conversation is not null)
        {
            for (var index = 0; index < conversation.Messages.Count; index++)
            {
                var turn = conversation.Messages[index];
                // A plan outranks a call only in the sense that the two cannot coexist in this build; the
                // branch is a clamp on a state a newer session file could carry, not a rule with teeth here.
                if (turn.PlanApprovalState == PlanApprovalStates.Pending) { planIndex = index; break; }
                if (turn.ApprovalState == ChatApprovalStates.Pending && callId is null) callId = turn.ToolCallId;
            }
        }

        var same = callId == _decisionCallId && planIndex == _decisionPlanIndex && DecisionHost.IsVisible;
        _decisionCallId = callId;
        _decisionPlanIndex = planIndex;

        if (conversation is null || (callId is null && planIndex < 0))
        {
            DecisionHost.IsVisible = false;
            DecisionHost.Child = null;
            ComposerContent.IsEnabled = true;
            return;
        }

        if (!same)
        {
            DecisionHost.Child = planIndex >= 0
                ? BuildPlanReviewHost(conversation.Id, planIndex)
                : BuildToolApprovalCard(conversation.Id, callId!, conversation.Messages.First(
                    turn => turn.ToolCallId == callId));
            _planSelectedOption = ChatPlanStates.Approve;
            MarkPlanSelection();
        }

        DecisionHost.IsVisible = true;
        // One flag, one meaning: the covered content leaves the tab order and hit-testing in a single step.
        ComposerContent.IsEnabled = false;
    }

    /// <summary>Escape on the host answers it the quiet way: a pending call is refused (one Denied result, the
    /// model is not asked again), a pending plan is rejected (recorded, plan mode kept). Both hand the keyboard
    /// back, which is the point of a keyboard exit.</summary>
    private void DismissDecisionByEscape()
    {
        var conversation = _chat.ActiveConversation;
        if (conversation is null) return;
        if (_decisionPlanIndex >= 0)
            ResolvePlanApproval(conversation.Id, _decisionPlanIndex, PlanApprovalStates.Rejected);
        else if (_decisionCallId is { } callId)
            ResolveApproval(conversation.Id, callId, approved: false, alwaysAllow: false);
        InputBox.Focus();
    }

    // ── content: a pending tool call ──

    /// <summary>
    /// The question, its frozen evidence, and its answers. The title names the tool because "run file_write"
    /// is not the decision — what it writes is, and that is the body. The risk tier picks the glyph because the
    /// tier is what makes this call ask at all.
    /// </summary>
    private Control BuildToolApprovalCard(string conversationId, string callId, ChatTurn turn)
    {
        var card = new StackPanel { Spacing = 8 };

        var risk = _chat.RiskFor(conversationId, turn.ToolName ?? "", turn.ToolArguments);
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var glyph = new Path
        {
            Classes = { "approval-risk" },
            // The tier picks the glyph, and the two extremes are the ones a person has to tell apart at a glance:
            // full-access geometry for something that reaches past the sandbox, the ask geometry for a write.
            // Everything below that — a command inside the session's own directory, a note, a peer session's
            // history — takes the auto glyph, which until this tier existed had nothing to draw.
            Data = ThemeGeometry(risk switch
            {
                ToolRisk.SystemCommand => "Hub.Icon.PermissionFull",
                ToolRisk.WorkspaceWrite => "Hub.Icon.PermissionAsk",
                _ => "Hub.Icon.PermissionAuto",
            }),
        };
        if (risk == ToolRisk.SystemCommand) glyph.Classes.Add("system-command");
        title.Children.Add(glyph);
        title.Children.Add(new TextBlock
        {
            Classes = { "approval-title" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatApprovalWantsFormat"), turn.ToolName ?? ""),
        });
        Grid.SetColumn(title.Children[1], 1);
        card.Children.Add(title);

        // The evidence, bounded: the card and the composer share a row, and a 400-line diff would push the
        // transcript out of the window. Past twelve lines the block says so and offers the rest.
        var detail = new StackPanel { Spacing = 4 };
        AddApprovalDetail(detail, "ChatApprovalArguments", turn.ToolArguments);
        AddApprovalDetail(detail, "ChatApprovalChangePreview", turn.ApprovalPreview);
        var scroll = new ScrollViewer
        {
            Name = "ApprovalDetailScroll",
            Content = detail,
            MaxHeight = 120,
            ClipToBounds = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        card.Children.Add(scroll);

        var previewLines = (turn.ApprovalPreview ?? "").Split('\n').Length;
        if (previewLines > ApprovalExpandThreshold)
        {
            var expand = new ToggleButton
            {
                Name = "ApprovalExpandButton",
                Classes = { "approval-action", "approval-secondary" },
                Content = HubStrings.Get("ChatApprovalExpand"),
                Tag = "ChatApprovalExpand",
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            expand.Click += (_, _) =>
            {
                scroll.MaxHeight = expand.IsChecked == true ? 340 : 120;
                expand.Content = HubStrings.Get(
                    expand.IsChecked == true ? "ChatApprovalCollapse" : "ChatApprovalExpand");
            };
            card.Children.Add(expand);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        // "Always allow" renamed to its consequence: the grant is app-wide now, and the bare word promised more
        // than it does. It is not offered on the tier that reaches past the sandbox, because a button that changed
        // nothing is a button that teaches the ladder is decoration — the gate ignores a grant on that tier.
        var secondary = new StackPanel
        {
            Name = "ApprovalSecondaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (risk != ToolRisk.SystemCommand)
        {
            var always = new Button
            {
                Classes = { "approval-action", "approval-secondary" },
                Content = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ApprovalAllowAlwaysForTool"), turn.ToolName ?? ""),
                Tag = "ApprovalAllowAlways",
            };
            ToolTip.SetTip(always, HubStrings.Get("ApprovalAllowAlwaysTip"));
            always.Click += (_, _) => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: true);
            secondary.Children.Add(always);
        }
        footer.Children.Add(secondary);

        var primary = new StackPanel
        {
            Name = "ApprovalPrimaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var deny = new Button
        {
            Classes = { "approval-action", "approval-deny" },
            Content = HubStrings.Get("ApprovalDeny"),
            Tag = "ApprovalDeny",
        };
        deny.Click += (_, _) => ResolveApproval(conversationId, callId, approved: false, alwaysAllow: false);
        var allow = new Button
        {
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("ApprovalAllow"),
            Tag = "ApprovalAllow",
        };
        allow.Click += (_, _) => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: false);
        primary.Children.Add(deny);
        primary.Children.Add(allow);
        Grid.SetColumn(primary, 1);
        footer.Children.Add(primary);
        card.Children.Add(footer);

        return card;
    }

    /// <summary>Twelve lines is where the bounded block stops being a glance. Below it an expand button would
    /// be an affordance for content that already fits, which is a lie with a border.</summary>
    private const int ApprovalExpandThreshold = 12;

    /// <summary>The transcript's signpost for a decision still owed: one muted line, pressable, pointing at the
    /// composer. Not a second card — the buttons live in one place or they live in none.</summary>
    private Control BuildApprovalPendingLine(ChatTurn turn)
    {
        var line = new Border { Classes = { "approval-pending-line" } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(new Path { Data = ThemeGeometry("Hub.Icon.Chevron") });
        row.Children.Add(new TextBlock
        {
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatApprovalWaitingLine"), turn.ToolName ?? ""),
        });
        Grid.SetColumn(row.Children[1], 1);
        line.Child = row;
        line.PointerPressed += (_, _) =>
        {
            ComposerFrame.BringIntoView();
            DecisionHost.Focus();
        };
        return line;
    }

    // ── content: a pending plan ──

    /// <summary>
    /// The review surface: two lettered options, an inline feedback box under B, and a footer whose left half
    /// is deliberately empty — A and B already say everything, and a third label there would be the charter's
    /// second telling. There is no "exit plan mode and I will prompt myself" row: the owner removed it, and
    /// <see cref="ChatWorkspace.TryResolvePlanApproval"/> has no such decision either.
    /// </summary>
    private Control BuildPlanReviewHost(string conversationId, int turnIndex)
    {
        var host = new StackPanel { Spacing = 8 };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Name = "PlanReviewTitle",
            Classes = { "plan-review-title" },
            Text = HubStrings.Get("ChatPlanReviewTitle"),
        });
        var open = new Button
        {
            Name = "PlanReviewOpenButton",
            Classes = { "plan-review-open" },
            Tag = "ChatPlanReviewOpen",
        };
        var openLabel = new TextBlock { Text = HubStrings.Get("ChatPlanReviewOpen") + " ↗" };
        openLabel.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("Hub.Font.Ui"));
        open.Content = openLabel;
        // The one invitation to go and read it. The transcript's card opens the same pane, but this row is where
        // the decision is being asked and the card may already be scrolled out of sight — and it carries the
        // index of the plan under review, so the two entrances can never disagree about which plan they show.
        open.Click += (_, _) => OpenInspector?.Invoke("plan", turnIndex);
        Grid.SetColumn(open, 1);
        header.Children.Add(open);
        host.Children.Add(header);

        var options = new StackPanel { Name = "PlanReviewOptions", Spacing = 4 };
        options.Children.Add(BuildPlanOptionRow(conversationId, turnIndex, "A",
            ChatPlanStates.Approve, "ChatPlanOptionApprove", feedback: false));
        options.Children.Add(BuildPlanOptionRow(conversationId, turnIndex, "B",
            ChatPlanStates.Revise, "ChatPlanOptionRevise", feedback: true));
        host.Children.Add(options);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var cancel = new Button
        {
            Name = "PlanCancelButton",
            Classes = { "plan-review-cancel" },
            Tag = "ChatPlanCancel",
        };
        var cancelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        cancelRow.Children.Add(new TextBlock { Text = HubStrings.Get("Cancel"), VerticalAlignment = VerticalAlignment.Center });
        cancelRow.Children.Add(new TextBlock { Classes = { "plan-review-kbd" }, Text = "Esc" });
        cancel.Content = cancelRow;
        cancel.Click += (_, _) =>
            ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Rejected);
        var continueButton = new Button
        {
            Name = "PlanContinueButton",
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("ChatPlanReviewContinue"),
            Tag = "ChatPlanContinue",
        };
        continueButton.Click += (_, _) => CommitPlanReview(conversationId, turnIndex);
        right.Children.Add(cancel);
        right.Children.Add(continueButton);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        host.Children.Add(footer);

        return host;
    }

    private Control BuildPlanOptionRow(string conversationId, int turnIndex, string letter, string option,
        string labelKey, bool feedback)
    {
        var row = new Button
        {
            Classes = { "plan-review-option" },
            Tag = option == ChatPlanStates.Approve ? "ChatPlanApprove" : "ChatPlanRevise",
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("26,*") };
        var key = new Border { Classes = { "plan-review-key" }, Child = new TextBlock { Text = letter } };
        grid.Children.Add(key);
        grid.Children.Add(new TextBlock { Classes = { "plan-review-label" }, Text = HubStrings.Get(labelKey) });
        Grid.SetColumn(grid.Children[1], 1);

        TextBox? box = null;
        if (feedback)
        {
            box = new TextBox
            {
                Name = "PlanFeedbackBox",
                Classes = { "plan-feedback" },
                PlaceholderText = HubStrings.Get("ChatPlanFeedbackPlaceholder"),
                IsVisible = false,
            };
            // Enter inside the feedback is "send this revision"; Shift+Enter is a newline. The AcceptsReturn
            // box swallows Enter before any bubble handler sees it, so this is a tunnel handler like the
            // composer's own.
            box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
                e.Handled = true;
                CommitPlanReview(conversationId, turnIndex);
            }, RoutingStrategies.Tunnel);
            // Typing into the feedback is what enables continue; without this the button would stay disabled
            // until something else happened to repaint the host.
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty) MarkPlanSelection();
            };
            Grid.SetRow(box, 1);
            Grid.SetColumnSpan(box, 2);
            grid.Children.Add(box);
        }

        row.Content = grid;
        row.Click += (_, _) =>
        {
            _planSelectedOption = option;
            MarkPlanSelection();
            box?.Focus();
        };
        return row;
    }

    /// <summary>Applies the selected option's look and the continue button's enabled state. B with an empty
    /// feedback box disables continue rather than sending nothing: a tap that looks like focusing a text box
    /// must not be the tap that spends a request.</summary>
    private void MarkPlanSelection()
    {
        if (DecisionHost.Child is not StackPanel host) return;
        var options = host.Children.OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Name == "PlanReviewOptions");
        if (options is null) return;
        foreach (var row in options.Children.OfType<Button>())
        {
            var isApprove = row.Tag as string == "ChatPlanApprove";
            var selected = (_planSelectedOption == ChatPlanStates.Approve) == isApprove;
            row.Classes.Set("selected", selected);
            var box = row.GetLogicalDescendants().OfType<TextBox>()
                .FirstOrDefault(candidate => candidate.Name == "PlanFeedbackBox");
            if (box is not null) box.IsVisible = selected && !isApprove;
        }

        var continueButton = host.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "PlanContinueButton");
        if (continueButton is null) return;
        var feedback = host.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(candidate => candidate.Name == "PlanFeedbackBox");
        continueButton.IsEnabled = _planSelectedOption == ChatPlanStates.Approve
                                   || (feedback?.Text ?? "").Trim().Length > 0;
    }

    /// <summary>
    /// Executes the selected row. Approve hands the plan to the workspace, which flips the session into Agent
    /// mode and starts the run; the composer's plan chip then has to go too, and it goes through the panel-only
    /// clear rather than <see cref="ClearComposerMode"/> — that one rewrites <c>conversation.Mode</c> and raises
    /// <c>Changed</c> again inside the window where the run has just started.
    ///
    /// Revise records the decision and then sends the feedback as an ordinary user message through
    /// <see cref="ChatWorkspace.TryEnqueueSend"/>: the pending-plan refusal inside it keys on
    /// <c>PlanApprovalState == Pending</c>, which the decision has just cleared, so the whole send path — busy,
    /// queue, parallel limit, picture admission — comes for free instead of being re-implemented here.
    /// </summary>
    private void CommitPlanReview(string conversationId, int turnIndex)
    {
        if (_planSelectedOption == ChatPlanStates.Approve)
        {
            ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Approved);
            ClearComposerModeSelection();
            return;
        }

        var feedback = DecisionHost.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "PlanFeedbackBox")?.Text ?? "";
        if (feedback.Trim().Length == 0) return;

        ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.RevisionRequested);
        if (!_chat.TryEnqueueSend(conversationId, feedback.Trim(), null, null, out var refusalKey))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
            return;
        }
        RememberSentText(feedback.Trim());
        InputBox.Focus();
    }

    /// <summary>Panel-side only: clears the composer's mode selection without touching the conversation. The
    /// workspace has already set the session's mode; writing it again here would be a second transcript write
    /// inside the run-start window.</summary>
    private void ClearComposerModeSelection()
    {
        _selectedComposerMode = null;
        RefreshComposerChoices();
    }

    /// <summary>
    /// The transcript's whole presence for a plan: one bordered card naming the plan and where it stands, and
    /// none of its body. A plan runs to screens, and its row used to run with it — the conversation got pushed
    /// out of view to display a document that is read once, in one sitting, somewhere with room to scroll. The
    /// body stays on the turn and still goes to the provider unchanged, and the row's own copy action still hands
    /// it over in full; this is a decision about painting, not about what was said.
    ///
    /// One card carries the disposition instead of a card plus a record line, because after a decision the two
    /// would be the same sentence twice. It is a button rather than a border: it has to take Enter and be
    /// reachable by Tab, and a check has to be able to press it through its own
    /// <see cref="Button.ClickEvent"/>.
    /// </summary>
    private Control BuildPlanCard(ChatTurn turn, int turnIndex)
    {
        var stateKey = PlanCardStateKey(turn.PlanApprovalState ?? PlanApprovalStates.Pending);
        var title = PlanTitleOf(turn.Text);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        row.Children.Add(new Path { Classes = { "plan-card-icon" }, Data = ThemeGeometry("Hub.Icon.ChatModePlan") });
        row.Children.Add(new TextBlock { Classes = { "plan-card-label" }, Text = HubStrings.Get(stateKey) });
        Grid.SetColumn(row.Children[1], 1);
        row.Children.Add(new TextBlock { Classes = { "plan-card-title" }, Text = title });
        Grid.SetColumn(row.Children[2], 2);

        var card = new Button
        {
            Name = "PlanCard",
            Classes = { "plan-card" },
            Tag = stateKey,
            Content = row,
        };
        // The title is trimmed on the card, so the tooltip is where the whole of it lives — and it says no more
        // than that. The card's affordance is a hand cursor and a brightening frame; a "click to open" line would
        // be the charter's second telling, since 「查看计划」 in the composer is the one place that invites.
        ToolTip.SetTip(card, title);
        card.Click += (_, _) => OpenInspector?.Invoke("plan", turnIndex);
        return card;
    }

    private static string PlanCardStateKey(string state) => state switch
    {
        PlanApprovalStates.Pending => "ChatPlanPending",
        PlanApprovalStates.Approved => "ChatPlanApproved",
        PlanApprovalStates.RevisionRequested => "ChatPlanRevisionRequested",
        _ => "ChatPlanRejected",
    };

    private static readonly System.Text.RegularExpressions.Regex PlanHeadingPattern = new(
        @"^[ \t]{0,3}#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$",
        System.Text.RegularExpressions.RegexOptions.Compiled
        | System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex PlanListMarkerPattern = new(
        @"^[ \t]*(?:[-*+]|\d+[.)])[ \t]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PlanQuoteMarkerPattern = new(
        @"^[ \t]>+[ \t]?", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PlanFencePattern = new(
        @"^[ \t]*(?:```|~~~)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Inline markdown that is not part of a title's wording: a link's target, and paired emphasis
    /// marks. Matched in pairs rather than deleting every <c>*</c> and <c>_</c>, so a name like
    /// <c>axslcc_parse_file</c> keeps its underscores instead of becoming one long word.</summary>
    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Replacement)[] PlanInlineMarks =
    {
        (new(@"\[([^\]]*)\]\([^)]*\)", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"\*\*(.+?)\*\*", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"__(.+?)__", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"~~(.+?)~~", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"[*_`](.+?)[*_`]", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
    };

    /// <summary>How much of a title is worth a glance. The card trims at its own width anyway; this cap is what
    /// stops a model that titled a plan with a paragraph from producing a card that is nothing but title.</summary>
    private const int PlanTitleCharacters = 80;

    /// <summary>The plan's own heading when it has one, its first real line when it does not, and a named
    /// fallback when there is nothing to name. A plan is written by a model, so none of the three can be
    /// assumed: it may open with a heading, with a preamble, or with a fenced block.</summary>
    private static string PlanTitleOf(string markdown)
    {
        var heading = PlanHeadingPattern.Match(markdown);
        var raw = heading.Success ? heading.Groups[1].Value : FirstPlanProseLine(markdown);
        foreach (var (pattern, replacement) in PlanInlineMarks) raw = pattern.Replace(raw, replacement);
        raw = raw.Trim();
        if (raw.Length == 0) return HubStrings.Get("ChatPlanCardUntitled");
        return raw.Length <= PlanTitleCharacters
            ? raw
            : raw[..PlanTitleCharacters].TrimEnd() + "…";
    }

    private static string FirstPlanProseLine(string markdown)
    {
        var insideFence = false;
        foreach (var line in markdown.Split('\n'))
        {
            // A fenced block's opening line is the fence and its body is code: neither is a title, and the fence
            // has to be tracked rather than skipped so everything inside it stays out of the running too.
            if (PlanFencePattern.IsMatch(line))
            {
                insideFence = !insideFence;
                continue;
            }
            if (insideFence) continue;

            var text = PlanListMarkerPattern.Replace(PlanQuoteMarkerPattern.Replace(line.Trim(), ""), "").Trim();
            if (text.Length > 0 && !text.StartsWith("---", StringComparison.Ordinal)) return text;
        }
        return "";
    }

    // ── the steer-confirm strip ──

    /// <summary>
    /// Puts a draft on the strip instead of steering with it. A steer cancels the segment being generated and
    /// is the routing table's strongest signal, so the first Enter only shows what would be sent; the draft
    /// leaves the box so the composer reads as free again, exactly like the reference it was drawn from.
    /// </summary>
    private void ShowSteerConfirm(string text, string? context, IReadOnlyList<byte[]>? pictures)
    {
        var conversation = _chat.ActiveConversation;
        if (conversation is null) return;
        _steerDraft = new SteerDraft(conversation.Id, text, context, pictures);
        InputBox.Text = "";
        _pendingPictures.Clear();
        _contextAttachments.Clear();
        RenderContextAttachments();
        RefreshSteerConfirm();
        InputBox.Focus();
    }

    /// <summary>Rebuilds the strip from <see cref="_steerDraft"/>. Also the place where a draft whose reply
    /// finished while it waited stops reading as an interjection: nothing is being interrupted any more, so
    /// the primary action says send.</summary>
    private void RefreshSteerConfirm()
    {
        var draft = _steerDraft;
        if (draft is null || _chat?.ActiveConversation?.Id != draft.ConversationId)
        {
            SteerConfirmHost.IsVisible = false;
            SteerConfirmHost.Child = null;
            return;
        }

        var stillStreaming = _chat.RunFor(draft.ConversationId) is { IsStreaming: true };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Classes = { "steer-confirm-text" }, Text = draft.Text });
        if (draft.Pictures is { Count: > 0 } pictures)
            left.Children.Add(new TextBlock
            {
                Classes = { "steer-confirm-suffix" },
                Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatSteerPicturesSuffix"), pictures.Count),
            });
        row.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var commit = new Button
        {
            Name = "SteerCommitButton",
            Classes = { "steer-commit" },
            Tag = "SteerCommit",
        };
        var commitRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var keyGlyph = new TextBlock { Classes = { "steer-commit-key" }, Text = "↵" };
        keyGlyph.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("Hub.Font.Ui"));
        commitRow.Children.Add(keyGlyph);
        commitRow.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(stillStreaming ? "ChatSteer" : "Send"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        commit.Content = commitRow;
        ToolTip.SetTip(commit, HubStrings.Get(stillStreaming ? "ChatSteerConfirmTip" : "Send"));
        commit.Click += (_, _) => CommitSteer();
        actions.Children.Add(commit);
        actions.Children.Add(IconActionButton("ChatSteerEdit", "Hub.Icon.Edit", EditSteerDraft));
        actions.Children.Add(IconActionButton("ChatSteerDiscard", "Hub.Icon.Trash", DiscardSteer));
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        SteerConfirmHost.Child = row;
        SteerConfirmHost.IsVisible = true;
    }

    /// <summary>The second tap. If the reply finished in the meantime this is an ordinary send — the strip has
    /// already relabelled itself for that case, and falling through to the send path is what keeps the label
    /// honest rather than decorative.</summary>
    private void CommitSteer()
    {
        var draft = _steerDraft;
        if (draft is null) return;
        _steerDraft = null;

        if (_chat.RunFor(draft.ConversationId) is not { IsStreaming: true })
        {
            SteerConfirmHost.IsVisible = false;
            SteerConfirmHost.Child = null;
            if (!_chat.TryEnqueueSend(draft.ConversationId, draft.Text, draft.Context, draft.Pictures,
                    out var refusalKey))
            {
                AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
                return;
            }
            RememberSentText(draft.Text);
            UpdateSendState();
            return;
        }

        if (!_chat.TrySteer(draft.ConversationId, draft.Text, draft.Context, draft.Pictures))
        {
            // Lost the race to a finished run: the draft goes back to the box rather than vanishing.
            _steerDraft = draft;
            RefreshSteerConfirm();
            UpdateSendState();
            return;
        }

        RememberSentText(draft.Text);
        SteerConfirmHost.IsVisible = false;
        SteerConfirmHost.Child = null;
        if (_live is { } live) live.Status.Text = HubStrings.Get("ChatSteering");
        UpdateSendState();
    }

    /// <summary>Puts the draft back where it was typed, caret at the end, and closes the strip. The only way a
    /// draft returns to the box; the only way it is destroyed is <see cref="DiscardSteer"/>.</summary>
    private void EditSteerDraft()
    {
        var draft = _steerDraft;
        if (draft is null) return;
        _steerDraft = null;
        InputBox.Text = draft.Text;
        InputBox.CaretIndex = draft.Text.Length;
        SteerConfirmHost.IsVisible = false;
        SteerConfirmHost.Child = null;
        InputBox.Focus();
        UpdateSendState();
    }

    /// <summary>The one action that destroys text, and the only one with no keyboard path: a key that deletes
    /// a sentence is a key pressed by accident.</summary>
    private void DiscardSteer()
    {
        _steerDraft = null;
        SteerConfirmHost.IsVisible = false;
        SteerConfirmHost.Child = null;
        UpdateSendState();
    }

    // ── the strip, as a check reads it ──

    internal bool SteerConfirmVisibleForCheck => SteerConfirmHost.IsVisible;

    /// <summary>The draft the strip is holding up for its second tap. Read off the strip rather than the box:
    /// showing the draft is the whole point of the confirmation, and the box has already been cleared.</summary>
    internal string SteerConfirmTextForCheck
        => SteerConfirmHost.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("steer-confirm-text"))?.Text ?? "";

    /// <summary>The commit button's own label: 「插话」 while it interrupts a reply, 「发送」 once the reply has
    /// finished and the same tap is an ordinary send.</summary>
    internal string SteerConfirmCommitLabelForCheck
        => SteerConfirmHost.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "SteerCommitButton")
            ?.GetLogicalDescendants().OfType<TextBlock>()
            .LastOrDefault()?.Text ?? "";

    internal void ClickSteerCommitForCheck() => ClickSteerButtonForCheck("SteerCommitButton", null);
    internal void ClickSteerEditForCheck() => ClickSteerButtonForCheck(null, "ChatSteerEdit");
    internal void ClickSteerDiscardForCheck() => ClickSteerButtonForCheck(null, "ChatSteerDiscard");

    private void ClickSteerButtonForCheck(string? name, string? tag)
    {
        var button = SteerConfirmHost.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => (name is not null && candidate.Name == name)
                                       || (tag is not null && candidate.Tag as string == tag));
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Option tags for the plan review, in the order the rows read. Kept as constants so the
    /// transcript's decision strings and the composer's row tags cannot drift apart.</summary>
    private static class ChatPlanStates
    {
        public const string Approve = "approve";
        public const string Revise = "revise";
    }
}
