using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// The three decisions a parked run can owe — an action to approve, a plan to review, a question to answer — and
/// the one confirmation a running reply can owe, all of them asked in a strip above the composer rather than in a
/// transcript row a person had scrolled away from.
///
/// They live in one partial because they are one idea: something the person must answer before the composer
/// means what it looks like it means. A pending call, a pending plan and a pending question share a host (one
/// slot, so two cards can never stack); a steer confirmation gets its own strip because it must show the draft
/// while leaving the box the draft goes back to. Both strips sit above the frame and neither takes the input box
/// away: a decision is a question, and a question has never been a reason to throw away what somebody is writing.
/// </summary>
public partial class ChatPanel
{
    private string _planTasksIdentity = "";
    private string _planTasksConversationId = "";
    private bool _planTasksExpanded;

    /// <summary>The task row, plus the state it was last painted for so a repaint can replace only the glyphs that
    /// actually moved. Held because the card is repainted in place: a step changing state is news about one row,
    /// not a reason to build the list again and drop the offset the person had scrolled it to.</summary>
    private sealed class PlanTaskRow(int number, Grid row, TextBlock label, string state)
    {
        internal int Number { get; } = number;
        internal Grid Row { get; } = row;
        internal TextBlock Label { get; } = label;
        internal string State { get; set; } = state;
    }

    /// <summary>What the card on screen was built from — the plan it belongs to, whether it is unfolded, and its
    /// titles. Deliberately not the task states: those are painted, and a string that moved on every status change
    /// would put the list back to its first row under whoever was reading the last one.</summary>
    private string _planTasksShape = "";
    private TextBlock? _planTasksSummary;
    private readonly List<PlanTaskRow> _planTaskRows = [];

    private void RefreshPlanTasksHost()
    {
        var conversation = _chat?.ActiveConversation;
        var planTurn = conversation?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant
            && turn.PlanApprovalState == PlanApprovalStates.Pending && turn.Plan?.Tasks is { Count: > 0 });
        planTurn ??= ViewedRun is not null
            ? conversation?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant
                && turn.PlanApprovalState == PlanApprovalStates.Approved && turn.Plan?.Tasks is { Count: > 0 })
            : null;
        var plan = planTurn?.Plan;
        var tasks = plan?.Tasks;
        var identity = plan is null || conversation is null
            ? ""
            : conversation.Id + ":" + (plan.Id ?? planTurn!.At.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (identity != _planTasksIdentity)
        {
            if (conversation is not null && conversation.Id == _planTasksConversationId && _planTasksIdentity.Length > 0)
            {
                _planTasksExpanded = false;
                _chat?.SetPlanTasksExpanded(conversation.Id, false);
            }
            else
            {
                _planTasksExpanded = conversation?.PlanTasksExpanded ?? false;
            }
            _planTasksIdentity = identity;
            _planTasksConversationId = conversation?.Id ?? "";
        }

        // Saved task metadata is history; only current work or a pending decision needs composer space.
        if (tasks is not { Count: > 0 } || tasks.All(task => task.State == PlanTaskStates.Completed)
            || (ViewedRun is null && !DecisionHost.IsVisible))
        {
            PlanTasksHost.Child = null;
            PlanTasksHost.IsVisible = false;
            // The tree went with the child, so the next card is built from scratch rather than repainted into
            // rows nobody can see.
            ForgetPlanTaskCard();
            return;
        }

        // Everything the card is built from and nothing it merely shows. A status change leaves this alone, which
        // is what keeps the list — and its scroll offset — the one already on screen.
        var shape = identity + "\u001f" + (_planTasksExpanded ? "expanded" : "folded") + "\u001f"
                    + string.Join("\u001f", tasks.Select(task => task.Title));
        if (shape == _planTasksShape && _planTasksSummary is not null)
        {
            PaintPlanTasks(tasks);
            return;
        }

        var toggle = new Button
        {
            Name = "PlanTasksToggleButton",
            Classes = { "plan-task-summary" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") },
        };
        var summaryRow = (Grid)toggle.Content;
        var icon = new Path
        {
            Data = Geometry.Parse("M3 5H7M10 5H21M3 12H7M10 12H21M3 19H7M10 19H21"),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        // Center the drawing's 24-unit viewport, not the Path's stretched bounds.
        var iconViewport = new Viewbox
        {
            Classes = { "plan-task-summary-icon" },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { icon } },
        };
        summaryRow.Children.Add(iconViewport);
        var summary = new TextBlock
        {
            Name = "PlanTasksSummary",
            Classes = { "plan-task-summary-text" },
            Text = PlanTaskSummaryText(tasks),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(summary, 1);
        summaryRow.Children.Add(summary);
        var chevron = new Path
        {
            Name = "PlanTasksChevron",
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            Data = ThemeGeometry("Hub.Icon.Caret"),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            RenderTransform = new RotateTransform(_planTasksExpanded ? 180 : 0),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var chevronViewport = new Viewbox
        {
            Classes = { "plan-task-summary-chevron" },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { chevron } },
        };
        Grid.SetColumn(chevronViewport, 2);
        summaryRow.Children.Add(chevronViewport);
        toggle.Click += (_, _) =>
        {
            _planTasksExpanded = !_planTasksExpanded;
            if (conversation is not null)
                _chat?.SetPlanTasksExpanded(conversation.Id, _planTasksExpanded);
            RefreshPlanTasksHost();
        };

        var list = new StackPanel { Spacing = 5, Margin = new Thickness(10, 8) };
        _planTaskRows.Clear();
        foreach (var task in tasks)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
            row.Children.Add(PlanTaskGlyph(task.State));
            var label = new TextBlock
            {
                Classes = { "plan-task-title" },
                Text = task.Title,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            PaintPlanTaskLabel(label, task.State);
            row.Children.Add(label);
            Grid.SetColumn(label, 1);
            list.Children.Add(row);
            _planTaskRows.Add(new PlanTaskRow(task.Number, row, label, task.State));
        }

        var taskList = new ScrollViewer
        {
            Name = "PlanTasksList",
            Content = list,
            IsVisible = _planTasksExpanded,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ClipToBounds = true,
        };
        // Use actual row heights so eight tasks fit even when their titles wrap.
        // Re-evaluate after layout to follow column resizing and scrollbar width changes.
        list.LayoutUpdated += (_, _) =>
        {
            var visibleRows = list.Children.Take(8).ToArray();
            if (visibleRows.Length == 0 || visibleRows.Any(row => row.Bounds.Height <= 0))
                return;
            var height = visibleRows.Sum(row => row.Bounds.Height)
                + list.Spacing * (visibleRows.Length - 1) + list.Margin.Top + list.Margin.Bottom;
            if (Math.Abs(taskList.MaxHeight - height) > 0.5)
                taskList.MaxHeight = height;
        };
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        content.Children.Add(toggle);
        Grid.SetRow(taskList, 1);
        content.Children.Add(taskList);
        PlanTasksHost.Child = content;
        PlanTasksHost.IsVisible = true;
        _planTasksSummary = summary;
        _planTasksShape = shape;
    }

    /// <summary>Drops the paint handles together with the tree they point into.</summary>
    private void ForgetPlanTaskCard()
    {
        _planTasksShape = "";
        _planTasksSummary = null;
        _planTaskRows.Clear();
    }

    /// <summary>The one line above the list: how many tasks there are and where they stand.</summary>
    private static string PlanTaskSummaryText(IReadOnlyList<PlanTask> tasks)
    {
        var completed = tasks.Count(task => task.State == PlanTaskStates.Completed);
        var inProgress = tasks.Count(task => task.State == PlanTaskStates.InProgress);
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatPlanTaskSummaryFormat"), tasks.Count, completed, inProgress,
            tasks.Count - completed - inProgress);
    }

    /// <summary>The ring, the tick or the empty box a task's state is drawn as. Bound to theme resources rather than
    /// given brushes: a brush read while building would freeze the colour of the theme the card arrived in, and the
    /// card outlives a theme switch.</summary>
    private static Control PlanTaskGlyph(string state)
    {
        switch (state)
        {
            case PlanTaskStates.InProgress:
                var activity = new ActivityGlyph
                {
                    Width = 14,
                    Height = 14,
                    Phase = ActivityPhase.RunningTool,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                activity.Bind(ActivityGlyph.TrackBrushProperty, new DynamicResourceExtension("Hub.Accent"));
                activity.Bind(ActivityGlyph.DotBrushProperty, new DynamicResourceExtension("Hub.AccentBorder"));
                return activity;
            case PlanTaskStates.Completed:
                var tick = new Path
                {
                    Width = 13,
                    Height = 13,
                    Stretch = Stretch.Uniform,
                    Data = ThemeGeometry("Hub.Icon.Tick"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                tick.Bind(Path.FillProperty, new DynamicResourceExtension("Hub.Success"));
                return tick;
            default:
                var pending = new Border
                {
                    Width = 13,
                    Height = 13,
                    BorderThickness = new Thickness(1.4),
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                pending.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Hub.TextTertiary"));
                return pending;
        }
    }

    /// <summary>A finished task's title recedes; every other one reads at full strength.</summary>
    private static void PaintPlanTaskLabel(TextBlock label, string state)
        => label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(
            state == PlanTaskStates.Completed ? "Hub.TextTertiary" : "Hub.TextPrimary"));

    /// <summary>Repaints the card already on screen: the summary line, and per row only the glyph and the title
    /// colour whose state moved. Nothing is rebuilt, so the list keeps its scroll offset — whoever scrolled down to
    /// the step being worked on is still looking at it when the next progress update lands.</summary>
    private void PaintPlanTasks(IReadOnlyList<PlanTask> tasks)
    {
        if (_planTasksSummary is not null)
            _planTasksSummary.Text = PlanTaskSummaryText(tasks);
        foreach (var row in _planTaskRows)
        {
            var state = tasks.FirstOrDefault(task => task.Number == row.Number)?.State;
            if (state is null || state == row.State) continue;
            row.Row.Children[0] = PlanTaskGlyph(state);
            PaintPlanTaskLabel(row.Label, state);
            row.State = state;
        }
    }

    /// <summary>The call the host is currently asking about, or null. Kept beside the host rather than read
    /// back out of it so a refresh can tell "same decision, repaint" from "a different decision arrived".</summary>
    private string? _decisionCallId;

    /// <summary>The turn index of the plan the host is reviewing, or -1.</summary>
    private int _decisionPlanIndex = -1;
    private string? _decisionConversationId;
    private string? _decisionPlanToken;

    /// <summary>The session and the three numbers the band above the composer is currently showing. Same purpose as
    /// <see cref="_decisionCallId"/>: a repaint has to be able to tell "nothing moved" from "another file changed",
    /// and the transcript's length cannot tell them apart because a write is recorded by replacing a turn.</summary>

    // A draft keeps the document it was written against, not whatever happens to be pending at send time.
    private (string ConversationId, string Token)? _planDraftTarget;

    private void CapturePlanDraftTarget()
    {
        if (string.IsNullOrEmpty(InputBox.Text) && _pendingPictures.Count == 0 && _contextAttachments.Count == 0)
        {
            _planDraftTarget = null;
            return;
        }
        if (_planDraftTarget is null && _chat?.ActiveConversation is { } conversation
            && ChatWorkspace.PendingPlanToken(conversation) is { } token)
            _planDraftTarget = (conversation.Id, token);
    }

    private void ReportPlanDraftChanged()
    {
        AppendNotice(HubStrings.Get("ChatPlanChanged"), danger: true);
        // Retarget only after showing the refusal. The next explicit send follows the person's review;
        // this attempt never silently changes the meaning of the draft.
        _planDraftTarget = null;
        CapturePlanDraftTarget();
    }

    /// <summary>Which option of the question card is armed, or null when the armed answer is the free-text line —
    /// or nothing yet. Pre-arming one of four options would let a stray Enter commit a choice nobody made.</summary>
    private string? _questionSelectedLetter;

    /// <summary>A draft waiting for its second tap, with the session it was typed in: switching conversations
    /// must not carry it along, and coming back must not have lost it.</summary>
    private sealed record SteerDraft(string ConversationId, string Text, string? Context, IReadOnlyList<byte[]>? Pictures);

    private SteerDraft? _steerDraft;

    /// <summary>Set by the shell: how the panel asks for the inspector column, on a tab ("plan" or "changes")
    /// and for one specific plan turn (-1 for the newest). The plan's body lives nowhere else in the UI once the
    /// transcript row is a card, so this is the entrance — which is why the card hands over the index of the plan
    /// it names rather than letting the pane show a newer one under an older title.</summary>
    internal Func<string, int, bool>? OpenInspector { get; set; }

    /// <summary>Set by the shell: how the panel asks the inspector column to repaint with the transcript it is
    /// already showing. The panel knows when the transcript moved; only the shell knows whether a column is up at
    /// all, on which page, and whether the pane is currently covering the window.</summary>
    internal Action? RefreshInspector { get; set; }

    /// <summary>Set by the shell: which tab the reader is actually standing on, every time the pane changes it.
    /// A repaint asks for a tab by name, so a header click that the shell never heard about is undone by the next
    /// streamed token — the column comes back on the plan. The handler is one assignment and nothing else: it
    /// fires from inside <c>Reload</c>, and a repaint that repainted would be a loop.</summary>
    internal Action<string>? InspectorTabFollowed { get; set; }

    /// <summary>The transcript shape the open column was last painted from: which conversation, how many turns.
    /// A turn count rather than a token count, because everything the pane shows is a turn — a plan body, one row
    /// per write — and rebuilding it for each streamed character would re-render markdown for a change nobody
    /// can see.</summary>
    private string _inspectorSignature = "";

    private void RefreshInspectorIfTranscriptMoved()
    {
        var conversation = _chat?.ActiveConversation;
        var signature = (conversation is null ? "" : conversation.Id + ":" + conversation.Messages.Count)
            + ":" + (_chat?.ActiveWorkspaceRoot ?? "");
        if (signature == _inspectorSignature) return;
        // Nothing is wired yet while the constructor is still running — the shell assigns the seam after this
        // panel comes back — so nothing has been painted, and priming the signature here would promise a repaint
        // that never happened. The first real paint then re-primes it honestly.
        if (RefreshInspector is null) return;
        _inspectorSignature = signature;
        RefreshInspector?.Invoke();
    }

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
            // The pane draws no control of its own any more: the two it used to carry — shut the column, lift the
            // pane over the window — are the title bar's, wired straight to the shell's own methods. What is left
            // here is the pane's *content* contract.
            // The pane asks; this is where the ask turns into a git read, because the panel has no session, no
            // log and no thread to wait on — and a pane that did its own I/O could not be fed a fixture by a check.
            _inspector.RepoRefreshRequested += RequestRepositoryRead;
            // The history scopes ask the same way: the pane owns which scope and which commit, the workspace owns
            // the process, and a check can feed the pane an answer without a repository.
            _inspector.ReadReviewRequested = request => _chat is null
                ? Task.FromResult(new InspectorPanel.ReviewRead(null, null, null, null))
                : _chat.ReadReviewAsync(request);
            // A first open draws the page before any status read has landed, so the pane may have no root yet. It asks
            // for one instead of waiting for a repaint that only a changed conversation signature would trigger.
            _inspector.ResolveRootRequested = () => _chat?.ResolveActiveWorkspaceRepositoryRootAsync()
                ?? Task.FromResult<string?>(null);
            // A scope that reads history is the moment to be sure the tree is current: the commit list is only as
            // good as the status read that sits beside it, and the two tabs share one refresh.
            _inspector.ScopeChanged += scope =>
            {
                // The repository may have landed while Last turn was visible; give the newly selected scope the
                // cached snapshot, or request the missing one through the normal repaint path.
                if (scope != GitReviewScope.LastTurn) RefreshInspector?.Invoke();
                if (scope is GitReviewScope.Committed or GitReviewScope.Branch) RequestRepositoryRead();
            };
            // Repository file rows ask for a single HEAD-to-working-tree snapshot, including staged and unstaged edits.
            _inspector.WholeDiffRequested = async (state, entry) =>
            {
                var conversationId = _chat?.ActiveConversation?.Id;
                if (_chat is null || state.Root is null) return null;
                var comparison = conversationId is { Length: > 0 }
                    ? await _chat.ReadGitFileAsync(conversationId, state.Root, entry, GitFileRange.WorkingTreeAgainstHead)
                    : await _chat.ReadActiveWorkspaceGitFileAsync(state.Root, entry, GitFileRange.WorkingTreeAgainstHead);
                if (comparison.Old.Exists && !comparison.Old.IsText || comparison.New.Exists && !comparison.New.IsText)
                    return null;
                return FileDiff.Unified(
                    comparison.Old.Exists ? comparison.Old.Snapshot.Text ?? "" : "",
                    comparison.New.Exists ? comparison.New.Snapshot.Text ?? "" : "",
                    entry.RelativePath,
                    ChatChanges.InspectorLimits.Expanded());
            };
            _inspector.ReadComparisonRequested = async (state, entry, range) =>
            {
                var conversationId = _chat?.ActiveConversation?.Id;
                if (_chat is null || state.Root is null) return null;
                var comparison = conversationId is { Length: > 0 }
                    ? await _chat.ReadGitFileAsync(conversationId, state.Root, entry, range)
                    : await _chat.ReadActiveWorkspaceGitFileAsync(state.Root, entry, range);
                if (!comparison.IsReadable || comparison.Old.Exists && !comparison.Old.IsText
                    || comparison.New.Exists && !comparison.New.IsText) return null;
                return new ChatFileSnapshot(comparison.Old.Exists ? comparison.Old.Snapshot.Text ?? "" : "",
                    comparison.New.Exists ? comparison.New.Snapshot.Text ?? "" : "");
            };
            _inspector.OpenEditBuffer = (owner, path) =>
            {
                if (_chat is null) return null;
                var workspace = _chat.ActiveWorkspaceRoot;
                return ChangesEditBuffer.Open(path, () => _chat.EditorScopeFor(owner, workspace));
            };
            _inspector.FilesSaved += () =>
            {
                RequestRepositoryRead();
                RefreshInspector?.Invoke();
            };
            // An empty scope pointing at the one that is not goes out through the same door a run's chip uses:
            // landing on a tab can mean opening a column that is shut, and only the shell knows the page it is on.
            // And whichever tab the pane ends up showing, the shell is told, because the next repaint asks for a
            // tab by name. Without this the reader's own click is undone by the next streamed token.
            _inspector.TabSelected += tab => InspectorTabFollowed?.Invoke(tab);
            _inspector.OpenFileRequested += relativePath =>
            {
                if (_chat is null) return;
                var conversationId = _chat.ActiveConversation?.Id ?? "";
                var workspace = _chat.ActiveWorkspaceRoot;
                var failure = CodeFileWindow.ShowEditor(TopLevel.GetTopLevel(this) as Window, relativePath,
                    () => _chat.EditorScopeFor(conversationId, workspace), RequestRepositoryRead);
                if (failure is not null) StatusMessage?.Invoke(HubStrings.Get(failure));
            };
        }

        var planText = PlanTextFor(conversation, turnIndex);
        var conversationId = conversation?.Id ?? "";
        // The first scope of the tab is the turn itself: the rows come from the transcript and the revisions it
        // points at, so they say what the assistant did rather than what the tree happens to hold.
        var review = _chat?.TurnReviewFor(conversation) ?? TurnReview.Empty;
        var workspaceRoot = _chat?.ActiveWorkspaceRoot;
        var repository = _chat?.RepositoryFor(workspaceRoot);
        // A restored or repainted Changes tab can be the first view of this workspace too. A missing snapshot
        // always needs a read; concurrent requests coalesce and a landed snapshot keeps later repaints quiet.
        if (tab is "repo" or "changes"
            && workspaceRoot is { Length: > 0 } && repository is null)
            RequestRepositoryRead();
        _inspector.Reload(
            planText,
            review,
            (change, wholeFile) => _chat is null
                ? (null, UndoCopyState.Unreadable)
                : (_chat.DiffForChange(change, conversationId, out var state, wholeFile), state),
            workspaceRoot is { Length: > 0 } ? repository?.State : GitRepositoryState.Stopped(GitReadOutcome.NoWorkspace),
            tab,
            QuoteIntoComposer,
            change => _chat is null
                ? (null, UndoCopyState.MissingSnapshot)
                : (_chat.SnapshotForChange(change, change.SelectedRevisionId, out var state), state),
            conversationId, workspaceRoot);
        // Painting by any route leaves the pane in step with the transcript, so the repaint that follows a
        // click would be a second rebuild of the same thing.
        _inspectorSignature = (conversation is null ? "" : conversation.Id + ":" + conversation.Messages.Count)
            + ":" + (workspaceRoot ?? "");
        return _inspector;
    }

    /// <summary>Starts a repository read for the conversation on screen, and lets it finish on its own. The
    /// result arrives through <see cref="OnRepositoryChanged"/>, which repaints the pane if it is still up — a
    /// click on a tab should not hold the pointer waiting on four git calls.</summary>
    private void RequestRepositoryRead()
    {
        var conversation = _chat?.ActiveConversation;
        if (_chat is null || _chat.ActiveWorkspaceRoot is not { Length: > 0 }) return;
        _ = conversation is null ? _chat.RefreshActiveWorkspaceRepositoryAsync()
            : _chat.RefreshRepositoryAsync(conversation.Id);
    }

    /// <summary>A read that landed refreshes the repository pill and repaints the pane only while its repository
    /// tab is visible. The cached snapshot is also what the next visit will use.</summary>
    private void OnRepositoryChanged()
    {
        if (_inspector is not { IsRepositoryTabVisible: true }) return;
        // A landed snapshot, including a failure outcome, keeps the repaint from starting another read.
        RefreshInspector?.Invoke();
    }

    /// <summary>The markdown of one plan: the turn the caller pointed at when that turn is still a plan of this
    /// conversation, otherwise the newest plan it holds. The fallback is what a stale pointer degrades to — a
    /// message deleted or regenerated since the card was drawn should move the pane to the plan that is really
    /// there, not leave it showing nothing.</summary>
    private string? PlanTextFor(Conversation? conversation, int turnIndex)
    {
        if (conversation is null) return null;
        if (turnIndex >= 0 && turnIndex < conversation.Messages.Count
            && conversation.Messages[turnIndex] is { Role: ChatRoles.Assistant, PlanApprovalState: { Length: > 0 } } chosen)
        {
            return PlanMarkdown(chosen);
        }

        return LatestPlanText(conversation);
    }

    /// <summary>The markdown of the newest plan this conversation produced — the pending one if a plan is
    /// waiting, otherwise the last one it answered, so the card and 「查看计划」 still show something after
    /// approval.</summary>
    private string? LatestPlanText(Conversation? conversation)
    {
        if (conversation is null) return null;
        string? latest = null;
        foreach (var turn in conversation.Messages)
        {
            if (turn.Role != ChatRoles.Assistant) continue;
            if (turn.PlanApprovalState is not { Length: > 0 }) continue;
            if (PlanMarkdown(turn) is { Length: > 0 } markdown) latest = markdown;
        }
        return latest;
    }

    /// <summary>
    /// What the pane shows for one plan turn: its body, wherever the turn says it is.
    ///
    /// <para><see cref="ChatWorkspace.PlanBody"/> is the one reading of the reference rule, so a plan whose body
    /// was filed and a plan written before the store existed are drawn by the same three lines.</para>
    ///
    /// <para>A document that is not where the turn points gets a sentence saying so rather than <c>null</c>:
    /// <see cref="InspectorPanel.Reload"/> reads an empty input as 「还没有生成计划」, and telling somebody who has
    /// a plan on the transcript that none was generated is a different claim than the one that is true.</para>
    /// </summary>
    private string? PlanMarkdown(ChatTurn turn)
    {
        if (_chat is null) return turn.Text;
        var body = _chat.PlanBody(turn, out var state);
        if (state is PlanBodyState.Read or PlanBodyState.NoReference) return body;
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("InspectorPlanGoneFormat"),
            turn.Plan?.Chars ?? 0,
            turn.Plan?.File ?? "");
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
        // A command waiting on an answer about its own process outranks the three below, and cannot coexist with
        // them: the batch is judged before anything runs, so a command in flight means no parked approval or
        // question from the same response. Read from live state on purpose — see <see cref="BuildStalledProcessCard"/>
        // for why there is no transcript form of this question to read instead.
        var stall = conversation is null ? null : _chat?.PeekStallQuestion(conversation.Id);
        if (stall is not null)
        {
            if (_stallShownFor != conversation!.Id)
            {
                _stallShownFor = conversation.Id;
                _decisionCallId = null;
                _decisionPlanIndex = -1;
                DecisionHost.Child = BuildStalledProcessCard(conversation.Id, stall.Value);
            }
            DecisionHost.IsVisible = true;
            return;
        }

        _stallShownFor = null;
        string? callId = null;
        var planIndex = -1;
        ChatTurn? pendingCall = null;
        List<ChatTurn>? pendingGroup = null;
        if (conversation is not null)
        {
            // A call with its result anywhere in the transcript is settled business, whichever slot the result
            // sits in; the group is about what still has to happen, not about what is out of order.
            var answered = conversation.Messages
                .Where(other => other.Role == ChatRoles.Tool && other.ToolCallId is { Length: > 0 })
                .Select(other => other.ToolCallId!)
                .ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < conversation.Messages.Count; index++)
            {
                var turn = conversation.Messages[index];
                // A plan outranks a call only in the sense that the two cannot coexist in this build; the
                // branch is a clamp on a state a newer session file could carry, not a rule with teeth here.
                if (turn.PlanApprovalState == PlanApprovalStates.Pending) { planIndex = index; break; }
                if (turn.ApprovalState != ChatApprovalStates.Pending || callId is not null) continue;
                callId = turn.ToolCallId;
                pendingCall = turn;
                // One card for the response, not one per call. The group is every call of it still owed an answer,
                // because that is what the click starts: a call the mode let through is held back only while its
                // sibling waits, and a card naming one of two would be a promise about half of what is about to
                // run. A question is not part of it — nobody authorizes somebody else's answer, so it keeps its own
                // card and its own words. A call recorded before batches existed is a group of one.
                var batchId = turn.ApprovalBatchId ?? turn.ToolCallId;
                pendingGroup = turn.ToolName == AskUserTool.ToolName
                    ? [turn]
                    : conversation.Messages
                        .Where(other => other.Role == ChatRoles.Assistant && other.ToolCallId is { Length: > 0 } id
                                        && !answered.Contains(id)
                                        && (other.ApprovalBatchId ?? other.ToolCallId) == batchId
                                        && other.ToolName != AskUserTool.ToolName)
                        .ToList();
            }
        }

        // Which of the two a pending call is asking for is read off the call, not kept beside it: the arguments on
        // the turn are the whole record, and a card rebuilt after a restart has to reach the same answer the gate
        // reached. A question and an approval share the slot and the shape of the decision; they differ in every
        // word on the card, so the branch has to happen before anything is built.
        var asks = pendingCall?.ToolName == AskUserTool.ToolName;
        var planToken = ChatWorkspace.PendingPlanToken(conversation);
        var same = conversation?.Id == _decisionConversationId && callId == _decisionCallId
            && planIndex == _decisionPlanIndex && planToken == _decisionPlanToken;
        _decisionConversationId = conversation?.Id;
        _decisionCallId = callId;
        _decisionPlanIndex = planIndex;
        _decisionPlanToken = planToken;

        if (conversation is null || (callId is null && planIndex < 0))
        {
            DecisionHost.IsVisible = false;
            DecisionHost.Child = null;
            return;
        }

        if (!same)
        {
            DecisionHost.Child = planIndex >= 0
                ? BuildPlanReviewHost(conversation.Id, planIndex)
                : asks
                    ? BuildQuestionCard(conversation.Id, callId!, pendingCall!)
                    : BuildToolApprovalCard(conversation.Id, callId!, pendingCall!, pendingGroup!);
            _questionSelectedLetter = null;
            if (asks) MarkQuestionSelection();
        }

        DecisionHost.IsVisible = true;
        // The composer keeps working: the input box is not covered, its draft is not cleared, and Enter still
        // means what it meant a second ago. A decision is answered by clicking the card, so nothing here needs
        // the keyboard to make the point.
        // A decision that just arrived takes the keyboard only when the box is empty, or the letter keys on it are
        // only reachable after something has been clicked: a key the window itself holds never travels down this
        // panel's route. Never on top of a draft — taking that focus is the interruption this whole strip exists to
        // avoid. And never on a repaint, which must not move a caret somebody is using.
        if (!same && (planIndex >= 0 || asks) && (InputBox.Text ?? "").Length == 0) DecisionHost.Focus();
    }

    /// <summary>Escape on the host answers it the quiet way: a pending call is refused (one Denied result, the
    /// model is not asked again), a pending plan is rejected (recorded, plan mode kept), a pending question is
    /// <i>skipped</i> — which is not the same answer and does not get the same sentence, because a refusal tells
    /// the model to go back and ask. All three hand the keyboard back, which is the point of a keyboard exit.</summary>
    private void DismissDecisionByEscape()
    {
        var conversation = _chat.ActiveConversation;
        if (conversation is null) return;
        if (_decisionPlanIndex >= 0)
            ResolvePlanApproval(conversation.Id, _decisionPlanIndex, PlanApprovalStates.Rejected);
        else if (_decisionCallId is { } callId)
        {
            if (IsPendingQuestion(callId)) ResolveQuestion(conversation.Id, callId, null, null, skipped: true);
            else ResolveApproval(conversation.Id, callId, approved: false, alwaysAllow: false);
        }
        InputBox.Focus();
    }

    /// <summary>Whether the call the host is asking about is a question rather than an action wanting permission.
    /// Read from the stored turn so Escape, the letter keys and the card itself cannot disagree about which one
    /// they are acting on after a restart.</summary>
    private bool IsPendingQuestion(string callId)
        => _chat?.ActiveConversation?.Messages.FirstOrDefault(
            turn => turn.ToolCallId == callId)?.ToolName == AskUserTool.ToolName;

    // ── content: a pending tool call ──

    /// <summary>
    /// The question, its frozen evidence, and its answers. The title names the tool because "run file_write"
    /// is not the decision — what it writes is, and that is the body. The risk tier picks the glyph because the
    /// tier is what makes this call ask at all.
    ///
    /// <para><paramref name="group"/> is every call still waiting from the one model response that asked for the
    /// card, which is the unit the person is answering: a provider may ask for several tools in a single reply, and
    /// one click either runs that group or stops it. A group of one renders exactly the card this used to be.</para>
    /// </summary>
    private Control BuildToolApprovalCard(string conversationId, string callId, ChatTurn turn,
        IReadOnlyList<ChatTurn> group)
    {
        if (ComputerUseGrants.IsComputerUseTool(turn.ToolName))
            return BuildComputerUseApprovalCard(conversationId, callId, turn);

        // Not a StackPanel: the host clips at its own MaxHeight and the detail block grows past that on expand, so
        // the middle row has to be the one that gives way. A star row takes whatever the fixed rows leave, which
        // keeps the answer buttons inside the clip at every height — the footer is the part that must be reachable.
        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };

        var risks = group
            .Select(member => _chat.RiskFor(conversationId, member.ToolName ?? "", member.ToolArguments))
            .ToList();
        // The header carries the worst tier in the group, because that is what the answer agrees to: a card for two
        // writes and a screen capture has to read like a screen capture. Each row keeps its own evidence below.
        // A cache break is ranked above the reach tier for the one reason that matters on this card: what the person
        // is being asked to spend is already built, and nothing they do after this answer can put it back.
        var risk = risks.Contains(ToolRisk.CacheBreak) ? ToolRisk.CacheBreak
            : risks.Contains(ToolRisk.SystemCommand) ? ToolRisk.SystemCommand
            : risks.Contains(ToolRisk.WorkspaceWrite) ? ToolRisk.WorkspaceWrite
            : _chat.RiskFor(conversationId, turn.ToolName ?? "", turn.ToolArguments);
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var glyph = new Path
        {
            Classes = { "approval-risk" },
            // The tier picks the glyph, and the two extremes are the ones a person has to tell apart at a glance:
            // full-access geometry for the tiers that cannot be waived — what reaches past the sandbox, and what
            // clears what a build already wrote — the ask geometry for a write. Everything below that — a command
            // inside the session's own directory, a note, a peer session's history — takes the auto glyph, which
            // until this tier existed had nothing to draw.
            Data = ThemeGeometry(risk switch
            {
                ToolRisk.SystemCommand or ToolRisk.CacheBreak => "Hub.Icon.PermissionFull",
                // A write, and the same glyph a file write gets: the tier a commit lands on was carved out to say
                // "this is ordinary work", which it does by asking in 询问审批 — and a card that drew the auto
                // glyph while it waited for an answer would be contradicting the mode it is asking under.
                ToolRisk.WorkspaceWrite or ToolRisk.RepositoryWrite => "Hub.Icon.PermissionAsk",
                _ => "Hub.Icon.PermissionAuto",
            }),
        };
        // The class is the plate that says "no button here will make this stop asking", which is the message both
        // top tiers carry; naming it after one of them would be the older, narrower claim.
        if (risk is ToolRisk.SystemCommand or ToolRisk.CacheBreak) glyph.Classes.Add("system-command");
        title.Children.Add(glyph);
        title.Children.Add(new TextBlock
        {
            Classes = { "approval-title" },
            // The tool's name when there is one thing to say, the count when the model asked for several: the
            // decision is about the group, and the rows beneath it name the parts.
            Text = group.Count == 1
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatApprovalWantsFormat"), turn.ToolName ?? "")
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatApprovalBatchWantsFormat"), group.Count),
        });
        Grid.SetColumn(title.Children[1], 1);
        Grid.SetRow(title, 0);
        card.Children.Add(title);

        // The evidence, bounded: a 400-line diff must not push the transcript out of the window, and the input box
        // under the card must not be pushed out either. Past twelve lines the block says so and offers the rest.
        var detail = new StackPanel { Spacing = 4 };
        var longestPreview = 0;
        foreach (var member in group)
        {
            // Named per member only when the card has more than one to tell apart; a single call says its own name
            // in the title already, and repeating it under the title is the second label the charter forbids.
            if (group.Count > 1)
                detail.Children.Add(new TextBlock
                {
                    // Two classes: the label's own look, and the hook's handle. A card that quietly dropped a
                    // member row would still answer the batch correctly, so the rows are counted, not read.
                    Classes = { "approval-label", "approval-member" },
                    Text = member.ToolName ?? "",
                });
            AddApprovalDetail(detail, "ChatApprovalArguments", member.ToolArguments);
            AddApprovalDetail(detail, "ChatApprovalChangePreview", member.ApprovalPreview);
            longestPreview = Math.Max(longestPreview, (member.ApprovalPreview ?? "").Split('\n').Length);
        }

        var scroll = new ScrollViewer
        {
            Name = "ApprovalDetailScroll",
            Content = detail,
            MaxHeight = 120,
            Margin = new Thickness(0, 8, 0, 0),
            ClipToBounds = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        card.Children.Add(scroll);

        // One number for the whole card, and the ceiling it measures against is the scroll's own: a group whose
        // evidence does not fit has to say so once, the same way one write's diff does.
        if (longestPreview > ApprovalExpandThreshold)
        {
            var expand = new ToggleButton
            {
                Name = "ApprovalExpandButton",
                Classes = { "approval-action", "approval-secondary" },
                Content = HubStrings.Get("ChatApprovalExpand"),
                Tag = "ChatApprovalExpand",
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            expand.Click += (_, _) =>
            {
                // Unbounded on expand, because the star row is what enforces the ceiling now: asking the scroll for
                // a number here is how the footer ended up under the clip when the number was larger than the host.
                scroll.MaxHeight = expand.IsChecked == true ? double.PositiveInfinity : 120;
                expand.Content = HubStrings.Get(
                    expand.IsChecked == true ? "ChatApprovalCollapse" : "ChatApprovalExpand");
            };
            Grid.SetRow(expand, 2);
            card.Children.Add(expand);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        // "Always allow" renamed to its consequence: the grant is app-wide now, and the bare word promised more
        // than it does. It is not offered on a tier a grant cannot open — ToolApprovalPolicy.IsGrantable is the same
        // question the gate asks, so a button that changed nothing cannot appear on a card whose call the gate will
        // keep parking: what reaches past the sandbox, a question, or a cleared build directory. A button the gate
        // ignores would teach the ladder is decoration. And it is not offered on a group, because a grant is about
        // one tool while the card is about several: clicking it here would stop future questions for tools the
        // person has not seen one at a time.
        var secondary = new StackPanel
        {
            Name = "ApprovalSecondaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (group.Count == 1 && ToolApprovalPolicy.IsGrantable(risk))
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
        // The verb answer: the third way out of a git write card, and the one a tier cannot express. Drawn only where
        // trusting that single verb would move this call out of the asking rung — the gate asks the same predicate
        // (ChatTools.TrustCandidateVerb), so the button cannot promise a grant the gate would ignore: not on a
        // compound line writing two verbs, not on a write that only exists inside a shell, not on a subcommand this
        // build cannot name, and not on a card that is asking for a different reason (no sandbox root, a cache break).
        var verbKey = group.Count == 1 && risk == ToolRisk.SystemCommand
            ? _chat.CommandVerbForGrant(conversationId, turn.ToolName ?? "", turn.ToolArguments)
            : null;
        if (verbKey is not null)
        {
            var verb = new Button
            {
                Classes = { "approval-action", "approval-secondary" },
                Content = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ApprovalAllowAlwaysForTool"), verbKey),
                Tag = "ApprovalAllowAlwaysForVerb",
            };
            ToolTip.SetTip(verb, HubStrings.Get("ApprovalAllowAlwaysForVerbTip"));
            verb.Click += (_, _) => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: false,
                alwaysAllowVerb: true);
            secondary.Children.Add(verb);
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
            Classes = { "approval-action", "approval-plate" },
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
        Grid.SetRow(footer, 3);
        card.Children.Add(footer);

        return card;
    }

    private Control BuildComputerUseApprovalCard(string conversationId, string callId, ChatTurn turn)
    {
        if (turn.ApprovalComputerTarget is not { } target) return BuildApprovalPendingLine(turn);
        var app = target.WindowTitle;
        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        var icon = new Path
        {
            Name = "ComputerUseApplicationIcon",
            Data = ThemeGeometry("Hub.Icon.Application"),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
        };
        icon.Bind(Path.FillProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var appIcon = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Child = icon,
        };
        appIcon.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Hub.SurfaceRaised"));
        header.Children.Add(appIcon);
        var title = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Name = "ComputerUseApplicationTitle",
            Classes = { "approval-title" },
            Text = app,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        title.Children.Add(new TextBlock
        {
            Name = "ComputerUseQuestion",
            Text = HubStrings.Get("ComputerUseApprovalTitle"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        title.Children.Add(new TextBlock
        {
            Name = "ComputerUseGrantDescription",
            Classes = { "muted" },
            Text = target.Application.ExecutablePath + "\n" + HubStrings.Get("ComputerUseGrantHint"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        card.Children.Add(header);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        var always = new Button
        {
            Name = "ComputerUseAlwaysAllowButton",
            Classes = { "approval-action" },
            Content = HubStrings.Get("ComputerUseAlwaysAllow"),
            Tag = "ComputerUseAlwaysAllow",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        always.Click += (_, _) => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: true);
        footer.Children.Add(always);

        var actions = new StackPanel
        {
            Name = "ComputerUsePrimaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var deny = new Button
        {
            Name = "ComputerUseDenyButton",
            Classes = { "approval-action", "approval-plate" },
            Content = HubStrings.Get("ApprovalDeny"),
            Tag = "ComputerUseDeny",
        };
        deny.Click += (_, _) => ResolveApproval(conversationId, callId, approved: false, alwaysAllow: false);
        var allow = new Button
        {
            Name = "ComputerUseAllowConversationButton",
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("ComputerUseAllowConversation"),
            Tag = "ComputerUseAllowConversation",
        };
        allow.Click += (_, _) => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: false);
        actions.Children.Add(deny);
        actions.Children.Add(allow);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 1);
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
                // Two different waits, and the row has to say which. "等批准" over a question tells the person
                // their job is to permit something, when the run is actually stopped for information only they
                // have — and it is the only sentence about this decision a reader who scrolled away will see.
                HubStrings.Get(turn.ToolName == AskUserTool.ToolName
                    ? "ChatQuestionWaitingLine"
                    : "ChatApprovalWaitingLine"), turn.ToolName ?? ""),
        });
        Grid.SetColumn(row.Children[1], 1);
        line.Child = row;
        line.PointerPressed += (_, _) =>
        {
            DecisionHost.BringIntoView();
            DecisionHost.Focus();
        };
        return line;
    }

    // ── content: a question the model asked ──

    /// <summary>
    /// The question, its choices, and the two ways out of it. Same slot, same lettered list as the plan review,
    /// because it is the same kind of thing — a decision the run is stopped on — and a card that looked different
    /// each time the app asked would teach nobody anything.
    ///
    /// <para>Not a StackPanel: the host clips at its own MaxHeight, and four options under a wrapped question is
    /// already past it on a short window. The list is the row that gives way, so 「继续」 stays inside the clip at
    /// every height — the footer is the part that has to be reachable, and a card whose answer button was cut off
    /// is a run parked forever.</para>
    /// </summary>
    private Control BuildQuestionCard(string conversationId, string callId, ChatTurn turn)
    {
        // The gate only parks a question that parses, and the arguments on the turn are the whole record, so a
        // card rebuilt after a restart reaches the same answer here. If they no longer read as a question the
        // transcript has moved under us: fall back to the signpost line rather than draw an empty card.
        if (!AskUserQuestion.TryParse(turn.ToolArguments, out var question, out var choices, out _))
            return BuildApprovalPendingLine(turn);

        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var shown = AskUserQuestion.QuestionShown(question);
        var title = new TextBlock
        {
            Classes = { "question-title" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatQuestionTitleFormat"), shown),
            TextWrapping = TextWrapping.Wrap,
        };
        // The title wraps, but the row it sits in is the card's fixed top: a question long enough to need more room
        // than the host's ceiling would take that room from the options under it, so the card draws the head and the
        // tip is where the whole sentence lives. Armed only when something was left out — a tip that repeats the
        // line under the pointer is chrome. The plan card tips unconditionally because its title is always one
        // trimmed line, so there it is never the whole of anything.
        if (shown != question) ToolTip.SetTip(title, TipContent(question));
        Grid.SetRow(title, 0);
        card.Children.Add(title);

        // Both halves of the list wrap, so both halves need a column: see FitQuestionRowContent for why a row inside
        // a Button is handed none by the template and would otherwise draw one long line for the card to clip.
        var options = new StackPanel { Name = "QuestionOptions", Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var choice in choices) options.Children.Add(BuildQuestionOptionRow(conversationId, callId, choice));
        // The free-text line belongs to the list and not beside it: it is the fifth answer, and putting it outside
        // the scroll would make it the one thing that survives a clipped card — which reads as the only choice.
        options.Children.Add(BuildQuestionFreeRow(conversationId, callId));
        var scroll = new ScrollViewer
        {
            Name = "QuestionListScroll",
            Content = options,
            ClipToBounds = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        card.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        // The same neutral plate as the plan review's exits, with Margin 0 for the same reason: the row's Spacing
        // is the gap, not Spacing plus the theme's trailing margin.
        var skip = new Button
        {
            Name = "QuestionSkipButton",
            Classes = { "approval-plate" },
            Tag = "ChatQuestionSkip",
            Margin = new Thickness(0),
        };
        var skipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        skipRow.Children.Add(new TextBlock { Text = HubStrings.Get("ChatQuestionSkip"), VerticalAlignment = VerticalAlignment.Center });
        skipRow.Children.Add(new TextBlock { Classes = { "decision-kbd" }, Text = "Esc" });
        skip.Content = skipRow;
        skip.Click += (_, _) => ResolveQuestion(conversationId, callId, null, null, skipped: true);
        var continueButton = new Button
        {
            Name = "QuestionContinueButton",
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("Continue"),
            Tag = "ChatQuestionContinue",
            IsEnabled = false,
            Margin = new Thickness(0),
        };
        continueButton.Click += (_, _) => CommitQuestion(conversationId, callId);
        right.Children.Add(skip);
        right.Children.Add(continueButton);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        Grid.SetRow(footer, 2);
        card.Children.Add(footer);

        return card;
    }

    /// <summary>One lettered choice. The cap is the keyboard shortcut and the row is the click, and both land in
    /// <see cref="ChooseQuestionOption"/> so the two cannot drift apart.</summary>
    private Control BuildQuestionOptionRow(string conversationId, string callId, AskUserQuestion.Option choice)
    {
        var row = new Button
        {
            Classes = { "question-option" },
            // The letter, not the label: labels are model-authored text and a check that matched on one would
            // break the day a fixture reworded it.
            Tag = choice.Letter,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
        grid.Children.Add(new Border
        {
            Classes = { "question-key" },
            Child = new TextBlock { Text = choice.Letter },
        });
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Classes = { "question-label" }, Text = choice.LabelShown };
        // The same bargain the title makes, one level down: the row draws its bounded head so the list keeps its
        // height, and the tip carries the whole label. Armed only when the row genuinely had to leave something out.
        if (choice.LabelShown != choice.Label) ToolTip.SetTip(label, TipContent(choice.Label));
        text.Children.Add(label);
        // A detail the model did not write is not a row to leave in — an empty line under every label would be
        // chrome, and the charter's answer to that is to remove it.
        if (choice.Detail.Length > 0)
        {
            var detail = new TextBlock { Classes = { "question-detail" }, Text = choice.DetailShown };
            if (choice.DetailShown != choice.Detail) ToolTip.SetTip(detail, TipContent(choice.Detail));
            text.Children.Add(detail);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        row.Content = grid;
        FitQuestionRowContent(row, grid);
        row.Click += (_, _) => ChooseQuestionOption(choice.Letter);
        return row;
    }

    /// <summary>
    /// What a tip shows when the card had to draw less than was written. The app declares no <c>ToolTip</c> style, so
    /// the stock presenter has no ceiling on its width and a question — a sentence, not a label — would arrive as one
    /// line wider than the window. The wrap and the width are the whole of the treatment; ink and face are left
    /// alone on purpose, because they inherit from the presenter, which is why a holder like this can be a bare
    /// <see cref="TextBlock"/> at all.
    /// </summary>
    private static TextBlock TipContent(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
    };

    /// <summary>The line that is not one of the model's options. It carries no letter cap: it is not a fifth choice
    /// the assistant authored, and giving it a key would let a keyboard press pick an answer nobody wrote.</summary>
    private Control BuildQuestionFreeRow(string conversationId, string callId)
    {
        var row = new Button
        {
            Classes = { "question-option" },
            // The same token <see cref="ChooseQuestionOption"/> arms with, because the row's Tag is what
            // MarkQuestionSelection compares the armed answer against. A second spelling here would leave the
            // free line never lighting up no matter what was chosen, and every other cell would still pass.
            Tag = FreeAnswer,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
        grid.Children.Add(new Border { Classes = { "question-key" }, Child = new TextBlock { Text = "✎" } });
        var box = new TextBox
        {
            Name = "QuestionAnswerBox",
            Classes = { "question-free" },
            PlaceholderText = HubStrings.Get("ChatQuestionFreeLabel"),
        };
        // Enter inside the line is "send this answer" and Shift+Enter is a newline, exactly as in the plan's
        // revision line. The AcceptsReturn box swallows Enter before any bubble handler sees it, so this has to
        // be a tunnel handler.
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            CommitQuestion(conversationId, callId);
        }, RoutingStrategies.Tunnel);
        box.PropertyChanged += (_, e) =>
        {
            // The caret is the choosing, as in the plan card: focus arms the line, typed text only re-reads
            // Continue's enabled state — text left from a previous draft must not outvote an explicit click.
            if (e.Property == InputElement.IsFocusedProperty) ChooseQuestionOption(free: true);
            else if (e.Property == TextBox.TextProperty) MarkQuestionSelection();
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        row.Content = grid;
        FitQuestionRowContent(row, grid);
        row.Click += (_, _) => ChooseQuestionOption(free: true, focusBox: box);
        return row;
    }

    /// <summary>
    /// Gives a row's content the column the row itself was given.
    ///
    /// <para>Hub's Button template wraps its content in a horizontal <see cref="StackPanel"/>
    /// (Theme/HubControls.axaml), and a horizontal panel measures its children with no width at all — that is how it
    /// learns how wide a row of labels wants to be. A wrapping <see cref="TextBlock"/> inside such a button is
    /// therefore measured against nothing, lays its sentence out as one line, and every row here draws that line
    /// clipped by the card's <c>ClipToBounds</c>: it reads as a short sentence with no ellipsis and no tip, which is
    /// the whole bug this exists for. The row itself is stretched to the list's width, so its own bounds are the
    /// column the text should have; the padding comes off because the template's border already spent it.</para>
    ///
    /// <para>The same shape as <c>UpdateMessageColumnWidth</c>: an explicit width where a container measures against
    /// nothing. It is applied per row rather than to the list because the infinite measure happens inside the
    /// button, not above it — the rows were already the right width and the text inside them was not.</para>
    /// </summary>
    private static void FitQuestionRowContent(Button row, Control content)
    {
        void Fit(double rowWidth)
        {
            var width = rowWidth - row.Padding.Left - row.Padding.Right;
            if (width <= 0) return;
            // The same width is not a change, and re-assigning it would re-measure every row on every resize tick.
            if (double.IsNaN(content.Width) || Math.Abs(content.Width - width) > 0.1) content.Width = width;
        }

        row.SizeChanged += (_, args) => Fit(args.NewSize.Width);
    }

    /// <summary>Arms one answer. Every door — the pointer, the letter key, the caret landing in the free line —
    /// comes through here, so "the key does what the click does" is one implementation rather than two.</summary>
    private void ChooseQuestionOption(string? letter = null, bool free = false, TextBox? focusBox = null)
    {
        _questionSelectedLetter = free ? FreeAnswer : letter;
        MarkQuestionSelection();
        focusBox?.Focus();
    }

    /// <summary>The armed-answer marker for the free-text line, and the same string the row carries as its Tag.
    /// One constant because they are compared to each other. No model-authored letter collides with it: options
    /// are A to D.</summary>
    private const string FreeAnswer = "free";

    private TextBox? FindQuestionAnswerBox()
        => DecisionHost.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "QuestionAnswerBox");

    /// <summary>Paints the armed row and re-reads Continue. Nothing armed is a real state here, unlike the plan
    /// card: an empty answer would reach the model as a tool result with no content in it, which some bridges
    /// reject outright and the model reads as "no preference" at best.</summary>
    private void MarkQuestionSelection()
    {
        if (DecisionHost.Child is not Grid card) return;
        var options = card.Children.OfType<ScrollViewer>().FirstOrDefault(scroll => scroll.Name == "QuestionListScroll")
            ?.Content as StackPanel;
        if (options is null) return;
        foreach (var row in options.Children.OfType<Button>())
            row.Classes.Set("selected", Equals(row.Tag as string, _questionSelectedLetter));

        var continueButton = card.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "QuestionContinueButton");
        if (continueButton is null) return;
        var typed = (_questionSelectedLetter == FreeAnswer
            ? FindQuestionAnswerBox()?.Text ?? ""
            : "").Trim();
        continueButton.IsEnabled = _questionSelectedLetter is { Length: > 0 } armed
                                   && (armed != FreeAnswer || typed.Length > 0);
    }

    /// <summary>The letter keys arm a question's options. The same three gates as the plan card, and the third one
    /// is the one that matters more here: with the caret in the free-text line, A and B are letters.</summary>
    private bool TryChooseQuestionOptionByKey(KeyEventArgs e)
    {
        if (_decisionCallId is not { } callId || !DecisionHost.IsVisible) return false;
        if (!IsPendingQuestion(callId)) return false;
        if (e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Source is TextBox) return false;
        var letter = e.Key switch
        {
            Key.A => "A",
            Key.B => "B",
            Key.C => "C",
            Key.D => "D",
            _ => null,
        };
        if (letter is null) return false;
        // Only a key that names an option on this card arms anything. Without the test, D on a two-option question
        // would silently select nothing and eat the keystroke.
        if (!QuestionOffers(callId, letter)) return false;
        ChooseQuestionOption(letter);
        e.Handled = true;
        return true;
    }

    /// <summary>Whether the parked question actually offers this letter.</summary>
    private bool QuestionOffers(string callId, string letter)
    {
        var turn = _chat?.ActiveConversation?.Messages.FirstOrDefault(candidate => candidate.ToolCallId == callId);
        return turn is not null
               && AskUserQuestion.TryParse(turn.ToolArguments, out _, out var choices, out _)
               && choices.Any(choice => choice.Letter == letter);
    }

    /// <summary>Hands the armed answer to the workspace, which words it and resumes the run. Nothing armed is
    /// already kept from reaching here by Continue's disabled state; the empty check is the same rule one more
    /// time, because Enter on the free line arrives by a route that never passes the button.</summary>
    private void CommitQuestion(string conversationId, string callId)
    {
        if (_questionSelectedLetter == FreeAnswer)
        {
            var words = (FindQuestionAnswerBox()?.Text ?? "").Trim();
            if (words.Length == 0) return;
            ResolveQuestion(conversationId, callId, null, words, skipped: false);
            return;
        }

        if (_questionSelectedLetter is { Length: > 0 } letter)
            ResolveQuestion(conversationId, callId, letter, null, skipped: false);
    }

    /// <summary>A refusal is spoken out loud rather than swallowed, as everywhere else in this strip: a card that
    /// did nothing after a click reads as a broken button, and here it also leaves a parked run looking alive.</summary>
    private void ResolveQuestion(
        string conversationId, string callId, string? letter, string? ownWords, bool skipped)
    {
        if (_chat.TryResolveQuestion(conversationId, callId, letter, ownWords, skipped, out var refusalKey)) return;
        AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
    }

    // ── content: a pending plan ──

    /// <summary>Approval is an explicit action; revision is an ordinary chat message, including pictures.
    /// Focusing the composer does not consume the pending decision or spend a request. The card's own heading is
    /// the way into the plan: a "View plan" link on the same row said the same thing twice and owned the whole
    /// right-hand side to say it, while the heading is the one thing on the card that names the document.</summary>
    private Control BuildPlanReviewHost(string conversationId, int turnIndex)
    {
        var host = new StackPanel { Spacing = 6 };
        // The heading wears the neutral plate the exits wear, as a pill and a size smaller, so it reads as the
        // card's title being pressable instead of as a fourth action on the row below it.
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new TextBlock
        {
            Name = "PlanReviewTitle",
            Classes = { "plan-review-title" },
            Text = HubStrings.Get("ChatPlanReviewTitle"),
        });
        head.Children.Add(new Path { Classes = { "plan-review-chevron" }, Data = ThemeGeometry("Hub.Icon.ChevronRight") });
        var open = new Button
        {
            Name = "PlanReviewOpenButton",
            Classes = { "approval-plate", "plan-review-heading" },
            Tag = "ChatPlanReviewOpen",
            Content = head,
        };
        open.Click += (_, _) => OpenInspector?.Invoke("plan", turnIndex);
        // The key the removed link used still names the act, so the heading does not need a second label on screen.
        ToolTip.SetTip(open, HubStrings.Get("ChatPlanReviewOpen"));
        host.Children.Add(open);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
        // Both exits wear the neutral plate the permission card's refuse button wears: they are answers on the same
        // raised card, and the one accent-filled button on the row is the approval. Margin 0 because the theme's
        // trailing 8 would be a second gap on top of the row's own Spacing.
        var cancel = new Button
        {
            Name = "PlanCancelButton",
            Classes = { "approval-plate" },
            Tag = "ChatPlanCancel",
            Content = HubStrings.Get("ChatPlanCancel"),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        cancel.Click += (_, _) => ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Rejected);
        footer.Children.Add(cancel);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var revise = new Button
        {
            Classes = { "approval-plate" },
            Tag = "ChatPlanRevise",
            Content = HubStrings.Get("ChatPlanReviseInChat"),
            Margin = new Thickness(0),
        };
        revise.Click += (_, _) => InputBox.Focus();
        var approve = new Button
        {
            Name = "PlanApproveButton",
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("ChatPlanOptionApprove"),
            Tag = "ChatPlanApprove",
            Margin = new Thickness(0),
        };
        approve.Click += (_, _) => ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Approved);
        right.Children.Add(revise);
        right.Children.Add(approve);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        host.Children.Add(footer);
        return host;
    }

    /// <summary>
    /// The transcript's whole presence for a plan: one bordered card naming the plan and where it stands, and
    /// none of its body. A plan runs to screens, and its row used to run with it — the conversation got pushed
    /// out of view to display a document that is read once, in one sitting, somewhere with room to scroll. The
    /// body is filed in <see cref="PlanStore"/> and the turn carries its name, so the card is drawn from the
    /// title frozen at that moment and painting a transcript opens no file; this is a decision about painting,
    /// not about what was said.
    ///
    /// One card carries the disposition instead of a card plus a record line, because after a decision the two
    /// would be the same sentence twice. It is a button rather than a border: it has to take Enter and be
    /// reachable by Tab, and a check has to be able to press it through its own
    /// <see cref="Button.ClickEvent"/>.
    /// </summary>
    private Control BuildPlanCard(ChatTurn turn, int turnIndex)
    {
        var stateKey = PlanCardStateKey(turn.PlanApprovalState ?? PlanApprovalStates.Pending);
        var title = PlanCardTitle(turn);
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
        var session = _chat.ViewedConversationId;
        var key = _projection!.KeyAt(turnIndex);
        card.Click += (_, _) =>
        {
            if (session == _chat.ViewedConversationId && _projection is not null
                && _projection.TryResolve(key, out var current)) OpenInspector?.Invoke("plan", current);
        };
        return card;
    }

    private static string PlanCardStateKey(string state) => state switch
    {
        PlanApprovalStates.Pending => "ChatPlanPending",
        PlanApprovalStates.Approved => "ChatPlanApproved",
        PlanApprovalStates.RevisionRequested => "ChatPlanRevisionRequested",
        _ => "ChatPlanRejected",
    };

    /// <summary>What the card calls this plan: the title frozen on the turn when its body was filed, and the same
    /// derivation run over the turn's own text for a plan whose body never left it. Neither branch opens a file,
    /// so painting a transcript of a hundred turns costs no disk read — which is why the title is stored rather
    /// than read back out of the document every time.</summary>
    private static string PlanCardTitle(ChatTurn turn)
    {
        var title = turn.Plan?.Title ?? PlanStore.TitleOf(turn.Text);
        return title.Length > 0 ? title : HubStrings.Get("ChatPlanCardUntitled");
    }

    // ── the active goal capsule above the composer ──

    /// <summary>Shows the active thread goal where the next instruction is written, keeping its controls
    /// available without making the reader scroll back to the original message.</summary>
    private void RefreshGoalCapsule()
    {
        var chat = _chat;
        var conversation = chat?.ActiveConversation;
        if (chat is null || conversation is null)
        {
            GoalCapsuleHost.IsVisible = false;
            GoalCapsuleHost.Child = null;
            return;
        }
        var goal = conversation.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User
            && turn.IsGoal && chat.IsCurrentGoalTurn(conversation.Id, turn.At));
        var state = chat.GoalStateFor(conversation.Id);
        if (goal is null || state is not (ChatGoalStates.Active or ChatGoalStates.Paused or ChatGoalStates.BudgetLimited))
        {
            GoalCapsuleHost.IsVisible = false;
            GoalCapsuleHost.Child = null;
            return;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (state == ChatGoalStates.Active)
            actions.Children.Add(GoalActionButton("Ⅱ", "ChatGoalPause", () =>
            {
                chat.PauseGoal(conversation.Id);
                RefreshGoalCapsule();
            }));
        else
            actions.Children.Add(GoalActionButton("▶", "ChatGoalResume", () =>
            {
                chat.ResumeGoal(conversation.Id);
                RefreshGoalCapsule();
            }));
        actions.Children.Add(GoalActionButton("×", "ChatGoalClear", () =>
        {
            chat.ClearGoal(conversation.Id);
            RefreshGoalCapsule();
        }));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            Data = ThemeGeometry(ComposerModeIconKey(ChatModes.Agent)),
            Fill = Brushes.Transparent, StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        row.Children.Add(icon);
        var statusKey = state switch
        {
            ChatGoalStates.Paused when conversation.GoalStopReason == "no_activity" => "ChatGoalNoActivity",
            ChatGoalStates.Paused when conversation.GoalStopReason == "blocked" => "ChatGoalBlocked",
            ChatGoalStates.Paused => "ChatGoalPaused",
            ChatGoalStates.BudgetLimited => "ChatGoalBudgetLimited",
            _ => "ChatGoalActive",
        };
        var status = new TextBlock { Classes = { "goal-capsule-status" }, Text = HubStrings.Get(statusKey) };
        var reasonKey = conversation.GoalStopReason switch
        {
            "limit" => "ChatGoalLimitTip",
            "no_activity" => "ChatGoalNoActivityTip",
            "blocked" => "ChatGoalBlockedTip",
            _ => null,
        };
        if (reasonKey is not null) ToolTip.SetTip(status, HubStrings.Get(reasonKey));
        Grid.SetColumn(status, 1);
        row.Children.Add(status);
        var text = new TextBlock { Classes = { "goal-capsule-text" }, Text = goal.Text };
        ToolTip.SetTip(text, goal.Text);
        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        var run = chat.RunFor(conversation.Id);
        if (run is { IsStreaming: true })
        {
            var elapsed = new TextBlock
            {
                Classes = { "goal-capsule-elapsed" },
                Text = "· " + FormatDuration(run.Elapsed),
            };
            Grid.SetColumn(elapsed, 3);
            row.Children.Add(elapsed);
        }
        Grid.SetColumn(actions, 4);
        row.Children.Add(actions);
        GoalCapsuleHost.Child = row;
        GoalCapsuleHost.IsVisible = true;
    }

    // ── the steer-confirm strip ──

    /// <summary>
    /// Puts a draft on the strip instead of steering with it. A steer is the routing table's strongest signal and
    /// it joins a conversation already in progress, so the first Enter only shows what would be said and the
    /// second says it; the draft leaves the box so the composer reads as free again, exactly like the reference
    /// it was drawn from. Nothing is cancelled either way — the strip is a review, not a warning.
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
        // Three columns rather than a row of stacked panels, because the draft is the run that gives way and only a
        // finite measure constraint makes it give way. A horizontal StackPanel measures its children with infinity,
        // so the trimming the style sets never fired: a long draft reported its whole sentence as its width and drew
        // straight over the commit button's own label, which is transparent and therefore showed the two at once.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var text = new TextBlock { Classes = { "steer-confirm-text" }, Text = draft.Text };
        // One line cannot hold a long steer and the strip is a review, not a reading pane, so the tip carries the
        // whole sentence — the same bargain the plan card's title makes.
        ToolTip.SetTip(text, draft.Text);
        row.Children.Add(text);
        if (draft.Pictures is { Count: > 0 } pictures)
        {
            var suffix = new TextBlock
            {
                Classes = { "steer-confirm-suffix" },
                Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatSteerPicturesSuffix"), pictures.Count),
            };
            Grid.SetColumn(suffix, 1);
            row.Children.Add(suffix);
        }

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
        Grid.SetColumn(actions, 2);
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
                // The tap spent nothing, so the sentence goes back on the strip that held it: this is a review, not
                // a cheque. Reachable because the box stays live under a waiting plan, so a steer can be armed and
                // then refused by the very plan the strip now shares its space with.
                _steerDraft = draft;
                RefreshSteerConfirm();
                UpdateSendState();
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

    // ── A command that went quiet while it is still running ──

    /// <summary>How much of the quiet command this card's title draws. The line is the model's own, and it can be a
    /// compound of any length: the title is the Auto row of a card the host clips, so the part that does not fit is
    /// drawn from the tip rather than taken out of the height the three answers need.</summary>
    private const int StallCommandShown = 120;

    /// <summary>
    /// The card Hub raises when one of the assistant's commands stops producing output but has not stopped running.
    /// Nobody pressed anything here, so there is no tool call to approve: what is on the card is a live process, and
    /// the three answers are three different acts on it — keep waiting, stop waiting and leave it alone, or end it
    /// and run again.
    ///
    /// <para>It takes the host's slot rather than sitting beside the approval card, and it can: a response is judged
    /// as a batch before anything runs, so while a command is in flight nothing from the same response is parked on
    /// a decision. Unlike the other three cards this one reads live state instead of the transcript, because there
    /// is no durable form of the question — a process cannot outlive Hub, and a card that survived a restart would
    /// be asking about something that is certainly gone.</para>
    /// </summary>
    private Control BuildStalledProcessCard(string conversationId,
        (string Command, int? Pid, TimeSpan SilentFor, string Output) stall)
    {
        // The same row shape as the approval card: the detail gives way, the footer stays reachable at every height.
        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var glyph = new Path
        {
            Classes = { "approval-risk" },
            // The geometry of the tiers that cannot be waived. Nothing here makes the asking stop: the next silent
            // command asks again, because the question was about a process, not about a permission.
            Data = ThemeGeometry("Hub.Icon.PermissionFull"),
        };
        glyph.Classes.Add("system-command");
        title.Children.Add(glyph);
        var command = stall.Command.Length <= StallCommandShown
            ? stall.Command
            : stall.Command[..StallCommandShown].TrimEnd() + "…";
        var heading = new TextBlock
        {
            Classes = { "approval-title" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("StallQuestionFormat"), command, $"{(int)stall.SilentFor.TotalSeconds}s"),
            TextWrapping = TextWrapping.Wrap,
        };
        // What the command actually was, whole, for the pointer: the title draws as much of it as a fixed row can
        // hold, and the answer buttons underneath are the part of this card that may never be pushed out of the clip.
        if (command != stall.Command) ToolTip.SetTip(heading, TipContent(stall.Command));
        title.Children.Add(heading);
        Grid.SetColumn(title.Children[1], 1);
        Grid.SetRow(title, 0);
        card.Children.Add(title);

        var detail = new StackPanel { Spacing = 4 };
        if (stall.Pid is { } pid)
            detail.Children.Add(new TextBlock
            {
                Classes = { "approval-label" },
                Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("StallPidFormat"), pid),
            });
        AddApprovalDetail(detail, "StallOutputLabel", stall.Output);

        var scroll = new ScrollViewer
        {
            Name = "ApprovalDetailScroll",
            Content = detail,
            MaxHeight = 120,
            Margin = new Thickness(0, 8, 0, 0),
            ClipToBounds = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        card.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var secondary = new StackPanel
        {
            Name = "ApprovalSecondaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // On the left, away from the affirmative plate, because what this answer gives up is the watching: Hub stops
        // waiting, stops holding the change slot, and stops knowing anything about the process. The tip has to say
        // that, or a person meaning "not now" picks the answer that lets their next build collide with it.
        secondary.Children.Add(StallButton(conversationId, StalledProcessFate.LeaveItRunning,
            "StallLeaveRunning", "StallLeaveRunningTip", "approval-secondary"));
        footer.Children.Add(secondary);

        var primary = new StackPanel
        {
            Name = "ApprovalPrimaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // On the refusing plate even though it is the most decisive of the three: the palette reserves red for
        // nothing up here — the style file says so out loud — and stopping a command then running again is closer to
        // refusing the wait than to allowing something. Its tooltip is where the honest limit belongs, because the
        // kill reaches the tree Hub started and not the build services that detach from it.
        primary.Children.Add(StallButton(conversationId, StalledProcessFate.KillAndRetry,
            "StallKillRetry", "StallKillRetryTip", "approval-plate"));
        primary.Children.Add(StallButton(conversationId, StalledProcessFate.KeepWaiting,
            "StallKeepWaiting", "StallKeepWaitingTip", "approval-allow"));
        Grid.SetColumn(primary, 1);
        footer.Children.Add(primary);
        Grid.SetRow(footer, 2);
        card.Children.Add(footer);
        return card;
    }

    private Button StallButton(string conversationId, StalledProcessFate fate, string labelKey, string tipKey,
        string plate)
    {
        var button = new Button
        {
            Classes = { "approval-action", plate },
            Content = HubStrings.Get(labelKey),
            Tag = labelKey,
        };
        ToolTip.SetTip(button, HubStrings.Get(tipKey));
        button.Click += (_, _) =>
        {
            // False means the question is already gone — the command finished in the moment before the press. Either
            // way the host is rebuilt, so a card that outlived its own premise cannot stay clickable.
            _chat.TryResolveStall(conversationId, fate);
            RefreshDecisionHost();
        };
        return button;
    }

    /// <summary>Which conversation's stall question is on the host, so a repaint does not rebuild a card that is
    /// already showing the same live process.</summary>
    private string? _stallShownFor;
}
