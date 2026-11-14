using static AxmolHub.ChatProviderCoordinator;
using static AxmolHub.ChatExecutionCoordinator;
using AxmolHub.Core;
using static AxmolHub.ChatWorkspace;

namespace AxmolHub;
/// <summary>Owns the durable session registry, workspace membership and current selection.</summary>
internal sealed partial class ChatSessionCoordinator
{
    internal ChatProviderCoordinator Providers { private get; set; } = null!;
    internal ChatExecutionCoordinator Execution { private get; set; } = null!;

    internal ChatSessionCoordinator(string dataRoot)
    {
        _dataRoot = dataRoot;
        _sessions = new ConversationRegistry(new ConversationStore(dataRoot));
        foreach (var summary in _sessions.List().Where(summary => summary.SpawnedState is SpawnedSessionStates.Queued or SpawnedSessionStates.Running))
            _sessions.TryUpdate(summary.Id, child => child.SpawnedState = SpawnedSessionStates.Interrupted);
        _workspaces = new WorkspaceStore(dataRoot);
        _workspaces.SeedFrom(_sessions.List().Select(summary => summary.WorkspaceRoot));
    }

    private readonly string _dataRoot;
    private readonly ConversationRegistry _sessions;
    internal ConversationRegistry Registry => _sessions;
    internal Conversation? Active => _active;
    internal string? SelectedApprovalMode { get => _selectedApprovalMode; set => _selectedApprovalMode = value; }
    private readonly WorkspaceStore _workspaces;
    private Conversation? _active;
    private string _selectedMode = ChatModes.Ask;
    private bool _selectedModeExplicit;
    private string _selectedReasoningEffort = ChatReasoningEfforts.Default;
    private string _selectedRouting = ChatRouting.Manual;
    private string? _selectedApprovalMode;
    private string? _selectedWorkspaceRoot;
    internal Conversation? ActiveConversation => _active;
    internal string ActiveMode => _active is null ? _selectedMode : NormalizeMode(_active.Mode);
    internal string ActiveReasoningEffort
    {
        get
        {
            var stored = _active?.ReasoningEffort ?? _selectedReasoningEffort;
            return Providers.SelectedChatModel is { } choice
                ? ModelCatalog.ResolveReasoningEffort(choice.Provider, choice.ModelName, stored)
                : ChatReasoningEfforts.Normalize(stored);
        }
    }
    internal bool SupportsReasoningEffort
        => Providers.SelectedChatModel is { } choice && ModelCatalog.SupportsReasoningEffort(choice.Provider, choice.ModelName);
    internal IReadOnlyList<ConversationSummary> Conversations
        => [.. _sessions.List().Where(summary => IsListed(summary) && !summary.Archived)];
    internal IReadOnlyList<ConversationSummary> ArchivedConversations
        => [.. _sessions.List().Where(summary => summary.Archived)];

    internal bool SelectMode(string mode)
    {
        if (mode is not (ChatModes.Ask or ChatModes.Plan or ChatModes.Agent)) return false;
        _selectedMode = mode;
        _selectedModeExplicit = true;
        if (_active is not null) _sessions.TryUpdate(_active.Id, conversation => conversation.Mode = mode);
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool ActiveAskUserEnabled => Execution.PreferencesProvider?.Invoke().AllowAssistantQuestions != false;

    internal bool SelectAskUserEnabled(bool enabled)
    {
        var preferences = Execution.PreferencesProvider?.Invoke();
        if (preferences is null) return false;
        preferences.AllowAssistantQuestions = enabled;
        Execution.PreferencesPersist?.Invoke(preferences);
        Execution.Changed?.Invoke();
        return true;
    }

    internal int ActiveReasoningLevel
    {
        get
        {
            var choice = Providers.SelectedChatModel;
            if (choice is null) return 0;
            var efforts = ModelCatalog.SelectableReasoningEfforts(choice.Provider, choice.ModelName);
            var current = ActiveReasoningEffort;
            var exact = Enumerable.Range(0, efforts.Count).FirstOrDefault(level =>
                string.Equals(efforts[level], current, StringComparison.OrdinalIgnoreCase), -1);
            if (exact >= 0) return exact;
            var medium = efforts.ToList().FindIndex(effort =>
                string.Equals(effort, ChatReasoningEfforts.Medium, StringComparison.OrdinalIgnoreCase));
            return medium >= 0 ? medium : efforts.Count / 2;
        }
    }

    internal bool SelectReasoningLevel(int level)
    {
        var choice = Providers.SelectedChatModel;
        var effort = choice is null ? null : ModelCatalog.EffortAtLevel(choice.Provider, choice.ModelName, level);
        return effort is not null && SelectReasoningEffort(effort);
    }

    internal string SelectModelReasoningEffort(ChatModelOption choice)
    {
        return _selectedReasoningEffort = ModelCatalog.ResolveReasoningEffort(
            choice.Provider, choice.ModelName, ActiveReasoningEffort);
    }

    private void NormalizeSessionReasoningEffort(Conversation conversation)
    {
        var choice = Providers.ModelFor(conversation.Id);
        var effort = choice is null
            ? ChatReasoningEfforts.Normalize(conversation.ReasoningEffort)
            : ModelCatalog.ResolveReasoningEffort(choice.Provider, choice.ModelName, conversation.ReasoningEffort);
        _selectedReasoningEffort = effort;
        if (!string.Equals(conversation.ReasoningEffort, effort, StringComparison.Ordinal))
            _sessions.TryUpdate(conversation.Id, item => item.ReasoningEffort = effort);
    }

    internal bool SelectReasoningEffort(string effort)
    {
        if (effort is not (ChatReasoningEfforts.Low or ChatReasoningEfforts.Medium
            or ChatReasoningEfforts.High or ChatReasoningEfforts.XHigh
            or ChatReasoningEfforts.Max or ChatReasoningEfforts.Ultra)) return false;
        // Choosing a tier is allowed unless this model refused that one; "the manifest never mentioned it" is
        // not a refusal, and treating it as one is what made the menu unreachable on the default gateway.
        if (Providers.SelectedChatModel is not { } choice
            || !ModelCatalog.MaySendEffort(choice.Provider, choice.ModelName, effort)) return false;
        _selectedReasoningEffort = effort;
        if (_active is not null) _sessions.TryUpdate(_active.Id, conversation =>
        {
            conversation.ReasoningEffort = effort;
            // Selecting a tier explicitly opts this session out of automatic effort selection.
            conversation.Routing = ChatRouting.Manual;
        });
        _selectedRouting = ChatRouting.Manual;
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool SetPlanTasksExpanded(string conversationId, bool expanded)
        => _sessions.TryUpdate(conversationId, conversation => conversation.PlanTasksExpanded = expanded);

    internal bool SetRouting(string conversationId, string routing)
    {
        var normalized = ChatRouting.Normalize(routing);
        if (conversationId.Length == 0)
        {
            _selectedRouting = normalized;
            Execution.Changed?.Invoke();
            return true;
        }
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Routing = normalized))
            return false;
        if (normalized == ChatRouting.Manual)
            Execution.ClearRoute(conversationId);
        _selectedRouting = normalized;
        Execution.Changed?.Invoke();
        return true;
    }

    internal string RoutingFor(string conversationId)
        => ChatRouting.Normalize((_sessions.Peek(conversationId) ?? _sessions.Load(conversationId))?.Routing);
    internal string ActiveRouting => ChatRouting.Normalize(_active?.Routing ?? _selectedRouting);

    internal string? WorkspaceRootFor(string conversationId)
        => (_sessions.Peek(conversationId) ?? _sessions.Load(conversationId))?.WorkspaceRoot;
    internal string? SessionTitleFor(string conversationId)
        => (_sessions.Peek(conversationId) ?? _sessions.Load(conversationId))?.Title;
    internal string? ActiveWorkspaceRoot => _active is { } session ? WorkspaceRootFor(session.Id) : _selectedWorkspaceRoot;
    internal IReadOnlyList<string> Workspaces => _workspaces.Roots;

    internal bool ForgetWorkspace(string root)
    {
        if (!_workspaces.Remove(root)) return false;
        Execution.Changed?.Invoke();
        return true;
    }

    private void RegisterWorkspace(string? root) => _workspaces.Add(root);

    internal WorkspacePathVerdict? AddWorkspace(string path)
    {
        var (verdict, full) = ValidateWorkspacePath(path);
        if (verdict is not null) return verdict;
        if (_workspaces.Add(full)) Execution.Changed?.Invoke();
        return null;
    }
    internal WorkspacePathVerdict? SelectWorkspaceRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (_active is { } clearing) SetWorkspaceRoot(clearing.Id, null);
            else _selectedWorkspaceRoot = null;
            Execution.Changed?.Invoke();
            return null;
        }

        var (verdict, full) = ValidateWorkspacePath(path);
        if (verdict is not null) return verdict;

        if (_active is { } session) SetWorkspaceRoot(session.Id, full);
        else
        {
            // A folder picked in the chip while nothing is on screen is a designation, not just a pending value: it
            // names the workspace the next session will work in, and the group the user asked for is that directory.
            _selectedWorkspaceRoot = full;
            RegisterWorkspace(full);
            Execution.Changed?.Invoke();
        }

        return null;
    }

    private (WorkspacePathVerdict? Verdict, string Full) ValidateWorkspacePath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            return (WorkspacePathVerdict.EscapesWorkspace, "");
        }

        if (!Path.IsPathRooted(path)) return (WorkspacePathVerdict.EscapesWorkspace, "");
        if (!Directory.Exists(full)) return (WorkspacePathVerdict.MissingWorkspace, "");
        if (WorkspacePaths.IsProtected(full, new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? [])))
            return (WorkspacePathVerdict.ProtectedRoot, "");

        return (null, full);
    }

    internal bool SetWorkspaceRoot(string conversationId, string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        var switchDefaultAskToAgent = _active?.Id == conversationId && normalized is not null
            && _active.Mode == ChatModes.Ask && !_selectedModeExplicit;
        if (!_sessions.TryUpdate(conversationId, opened =>
            {
                opened.WorkspaceRoot = normalized;
                // A workspace attached to an Ask-default chat should make the newly available Agent mode effective.
                if (switchDefaultAskToAgent) opened.Mode = ChatModes.Agent;
            })) return false;
        if (switchDefaultAskToAgent) _selectedMode = ChatModes.Agent;
        // The session took the directory, so the directory is now a workspace. Clearing one (null) never takes a
        // workspace away: other sessions may still work there, and only ForgetWorkspace decides a place is finished.
        RegisterWorkspace(normalized);
        Execution.Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Records one grant, in whichever of the session's two lists the guard says it belongs to, and answers the
    /// model with what it may now do there. The list is the guard's decision rather than the caller's: this is one
    /// of the two doors into the grant and the other one already went through
    /// <see cref="WorkspacePaths.ClassifyGrant"/>, so a protected location is read-only whichever door it came
    /// through — and <see cref="WorkspacePaths.SplitRoots"/> demotes it if a caller ever says otherwise.
    /// </summary>
    internal async Task<string> ApplyGrantedRootAsync(string conversationId, string path)
    {
        var guards = new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? []);
        var readOnly = WorkspacePaths.IsProtected(path, guards);
        var added = false;
        await Execution.ApplyOnUiAsync(() =>
        {
            added = _sessions.TryUpdate(conversationId, opened =>
            {
                // Through the shape rules and with the guards in hand: a duplicate spelling, a directory that has
                // vanished, or a protected location arriving in the writable half cannot reach the session file —
                // the card was asked about a directory, the session keeps a directory.
                var (write, read) = WorkspacePaths.SplitRoots(
                    readOnly ? opened.GrantedRoots : [.. opened.GrantedRoots, path],
                    readOnly ? [.. opened.ReadOnlyRoots, path] : opened.ReadOnlyRoots,
                    guards);
                opened.GrantedRoots = write;
                opened.ReadOnlyRoots = read;
            });
            if (added) Execution.Changed?.Invoke();
        }).ConfigureAwait(false);

        if (!added)
            return $"Refused: session {conversationId} is gone, so nothing could be granted. Do not retry this call.";
        Execution.Audit(conversationId, $"Granted directory: {path}{(readOnly ? " (read-only)" : "")}");
        return WorkspacePaths.GrantedResultFor(path, readOnly);
    }

    internal IReadOnlyList<string> ActiveGrantedRoots
        => _active is { } session ? (_sessions.Peek(session.Id) ?? session).GrantedRoots : [];

    internal IReadOnlyList<string> ActiveReadOnlyRoots
        => _active is { } session ? (_sessions.Peek(session.Id) ?? session).ReadOnlyRoots : [];

    /// <summary>
    /// The same record for a card on one file: one exact path on the session, and the answer the model reads says
    /// what it may do with that file and what the grant does not cover. The permission is asked here rather than
    /// stored, for the reason <see cref="WorkspacePaths.FileGrantIsReadOnly"/> gives — the audit line and the answer
    /// agree with every later resolution because they all ask the guard.
    /// </summary>
    internal async Task<string> ApplyGrantedFileAsync(string conversationId, string path)
    {
        var guards = new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? []);
        var readOnly = WorkspacePaths.FileGrantIsReadOnly(path, guards);
        var added = false;
        await Execution.ApplyOnUiAsync(() =>
        {
            added = _sessions.TryUpdate(conversationId, opened =>
            {
                // Through the shape rules: one exact path, deduped, bounded, and never a directory.
                opened.GrantedFiles = WorkspacePaths.NormalizeFiles([.. opened.GrantedFiles, path]);
            });
            if (added) Execution.Changed?.Invoke();
        }).ConfigureAwait(false);

        if (!added)
            return $"Refused: session {conversationId} is gone, so nothing could be granted. Do not retry this call.";
        Execution.Audit(conversationId, $"Granted file{(readOnly ? " (read-only)" : "")}: {path}");
        return WorkspacePaths.FileGrantedResultFor(path, readOnly);
    }

    internal IReadOnlyList<(string Path, bool ReadOnly)> ActiveGrantedFiles
        => _active is { } session
           && (_sessions.Peek(session.Id) ?? session).GrantedFiles is { Count: > 0 } files
            ? WorkspacePaths.FileGrants(files,
                new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? []))
            : [];

    internal bool RevokeGrantedFile(string conversationId, string path)
    {
        if (WorkspacePaths.CanonicalRoot(path) is not { } key) return false;
        var revoked = false;
        _sessions.TryUpdate(conversationId, opened =>
        {
            var kept = opened.GrantedFiles
                .Where(file => !string.Equals(WorkspacePaths.CanonicalRoot(file), key, StringComparison.Ordinal))
                .ToList();
            if (kept.Count == opened.GrantedFiles.Count) return;
            opened.GrantedFiles = kept;
            revoked = true;
        });
        if (!revoked) return false;

        Execution.Audit(conversationId, $"Revoked file: {path}");
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool RevokeGrantedRoot(string conversationId, string path)
    {
        if (WorkspacePaths.CanonicalRoot(path) is not { } key) return false;
        var revoked = false;
        _sessions.TryUpdate(conversationId, opened =>
        {
            // Both lists, because the revoke row names a directory and remembering which permission it came with
            // is not something a person should have to do to take it back.
            var write = opened.GrantedRoots
                .Where(granted => !string.Equals(WorkspacePaths.CanonicalRoot(granted), key, StringComparison.Ordinal))
                .ToList();
            var read = opened.ReadOnlyRoots
                .Where(granted => !string.Equals(WorkspacePaths.CanonicalRoot(granted), key, StringComparison.Ordinal))
                .ToList();
            if (write.Count == opened.GrantedRoots.Count && read.Count == opened.ReadOnlyRoots.Count) return;
            opened.GrantedRoots = write;
            opened.ReadOnlyRoots = read;
            revoked = true;
        });
        if (!revoked) return false;

        Execution.Audit(conversationId, $"Revoked directory: {path}");
        Execution.Changed?.Invoke();
        return true;
    }

    internal async Task<(GrantVerdict? Verdict, string Detail)> GrantDirectoryAsync(string conversationId,
        string? path)
    {
        var guards = new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? []);
        if (_sessions.Peek(conversationId) is not { } session) return (GrantVerdict.SessionGone, "");

        var (write, read) = WorkspacePaths.SplitRoots(session.GrantedRoots, session.ReadOnlyRoots, guards);
        var (verdict, full, detail) = WorkspacePaths.ClassifyGrant(
            new WorkspaceSandbox(session.WorkspaceRoot, write, read), guards, path);
        if (verdict is not (GrantVerdict.Granted or GrantVerdict.GrantedReadOnly)) return (verdict, detail);

        // The record an approved card writes, guards and all: one grant, whichever door it came through. The
        // write is read back rather than assumed, because the session can be closed between the click and here.
        await ApplyGrantedRootAsync(conversationId, full).ConfigureAwait(false);
        var reopened = _sessions.Peek(conversationId) ?? session;
        var wanted = WorkspacePaths.CanonicalRoot(full);
        var landed = reopened.GrantedRoots.Concat(reopened.ReadOnlyRoots).Any(granted =>
            string.Equals(WorkspacePaths.CanonicalRoot(granted), wanted, StringComparison.Ordinal));
        // The verdict that landed rather than a bare "it worked": the panel says 「已授权」 or 「已授权（只读）」 from
        // the same value the tool answers the model with, so the two doors describe one record one way.
        return landed ? (verdict, "") : (GrantVerdict.SessionGone, "");
    }

    internal Conversation StartConversation(string? providerId = null)
    {
        // A permission mode picked while nothing was on screen was meant for the session started right then, so
        // it is taken only from that state: a session started from a live one follows the app default again.
        var approvalMode = _active is null ? _selectedApprovalMode : null;
        _selectedApprovalMode = null;
        // Same rule for the sandbox: a directory picked before the first message was meant for the session that
        // follows, and a session started from a live one gets its own.
        var workspaceRoot = _active is null ? _selectedWorkspaceRoot : null;
        _selectedWorkspaceRoot = null;
        if (_active is not null)
        {
            _selectedMode = ChatModes.Ask;
            _selectedModeExplicit = false;
            _selectedReasoningEffort = ChatReasoningEfforts.Default;
            _selectedRouting = ChatRouting.Manual;
        }
        var mode = _selectedModeExplicit
            ? _selectedMode
            : workspaceRoot is null ? ChatModes.Ask : ChatModes.Agent;
        var choice = Providers.SelectedChatModel;
        var selectedProvider = providerId is null
            ? choice?.Provider
            : Providers.Providers.FirstOrDefault(provider => provider.Id == providerId);
        var modelName = selectedProvider is not null && choice is not null && selectedProvider.Id == choice.Provider.Id
            ? choice.ModelName
            : selectedProvider?.Model ?? "";
        var conversation = Conversation.Create(selectedProvider?.Id ?? "");
        conversation.ModelName = modelName;
        conversation.Mode = mode;
        _selectedMode = mode;
        _selectedModeExplicit = false;
        conversation.ReasoningEffort = ModelCatalog.ResolveReasoningEffort(
            selectedProvider, modelName, _selectedReasoningEffort);
        conversation.Routing = ChatRouting.Normalize(_selectedRouting);
        _selectedReasoningEffort = conversation.ReasoningEffort;
        conversation.ApprovalMode = approvalMode;
        conversation.WorkspaceRoot = workspaceRoot;
        _sessions.Adopt(conversation);
        _active = conversation;
        Execution.Changed?.Invoke();
        return conversation;
    }

    private ConversationSummary? FindEmptyDraft(string? workspaceRoot)
    {
        var wanted = SessionGroupKey.Workspace(workspaceRoot);
        return _sessions.List()
            .Where(summary => summary.MessageCount == 0 && !summary.Archived)
            .Where(summary => SessionGroupKey.Workspace(summary.WorkspaceRoot) is not { } at || at == wanted)
            .OrderByDescending(summary => summary.UpdatedAt)
            .FirstOrDefault();
    }

    private Conversation OpenDraft(ConversationSummary draft)
    {
        var conversation = _sessions.Load(draft.Id)
            ?? throw new InvalidOperationException("Conversation index listed an id that no longer exists.");
        _active = conversation;
        _selectedMode = NormalizeMode(conversation.Mode);
        NormalizeSessionReasoningEffort(conversation);
        _selectedRouting = ChatRouting.Normalize(conversation.Routing);
        Execution.Changed?.Invoke();
        return conversation;
    }

    /// <summary>New chat from anywhere with no directory to name: it keeps whatever workspace the selection already
    /// carries.</summary>
    internal Conversation StartOrOpenEmptyConversation()
        => FindEmptyDraft(null) is { } draft ? OpenDraft(draft) : StartConversation();

    /// <summary>
    /// A draft in a directory <see cref="StartOrOpenEmptyConversationInWorkspace"/> has already validated — the only
    /// door that brings a workspace of its own, and deliberately not public: a group outlives the directory it was
    /// named after, so a caller passing that root unchecked could open a session in a protected location, one that
    /// reads everything and writes nothing in every approval mode.
    /// </summary>
    private Conversation StartDraftIn(string workspaceRoot)
    {
        // The draft this group already owns if it has one, otherwise an unclaimed one, otherwise a new session:
        // two workspaces ＋ in a row get two drafts, because the second click may not move the first group.
        var draft = FindEmptyDraft(workspaceRoot);
        var conversation = draft is not null ? OpenDraft(draft) : StartConversation();
        if (conversation.Messages.Count > 0) return conversation;
        // SetWorkspaceRoot normalizes and persists through the registry, and treats blank as "no directory" — so
        // the group's own spelling is passed straight through rather than second-guessed here.
        SetWorkspaceRoot(conversation.Id, workspaceRoot);
        // StartConversation cannot see this target yet when invoked from a workspace group's ＋: it creates the
        // session before this method attaches the group root. Apply the workspace default after attachment, but
        // leave an existing empty draft's chosen mode alone.
        if (draft is null && !string.IsNullOrWhiteSpace(workspaceRoot))
        {
            conversation.Mode = ChatModes.Agent;
            _selectedMode = ChatModes.Agent;
            _selectedModeExplicit = false;
            _sessions.TryUpdate(conversation.Id, opened => opened.Mode = ChatModes.Agent);
            Execution.Changed?.Invoke();
        }
        return conversation;
    }

    internal (Conversation? Conversation, WorkspacePathVerdict? Verdict) StartOrOpenEmptyConversationInWorkspace(
        string workspaceRoot)
    {
        var (verdict, full) = ValidateWorkspacePath(workspaceRoot);
        if (verdict is not null) return (null, verdict);
        return (StartDraftIn(full), null);
    }

    internal (double Milliseconds, bool CameFromCache) LastSessionLoad { get; private set; }
    internal SessionOpenMeasurement? CurrentSessionOpen { get; private set; }

    internal Conversation? OpenConversation(string id)
    {
        var measurement = new SessionOpenMeasurement(id, "OpenConversation");
        CurrentSessionOpen = measurement;
        // The registry hands back the one instance, so opening a session that is streaming does not swap in a
        // copy that a later write would silently replace.
        var cachedBefore = _sessions.Peek(id)is not null;
        var readClock = System.Diagnostics.Stopwatch.StartNew();
        _active = _sessions.Load(id);
        LastSessionLoad = (readClock.Elapsed.TotalMilliseconds, cachedBefore);
        measurement.LoadMs = LastSessionLoad.Milliseconds;
        measurement.Cached = cachedBefore;
        var approvalStarted = measurement.ElapsedMs;
        _selectedMode = _active is null ? ChatModes.Ask : NormalizeMode(_active.Mode);
        _selectedModeExplicit = false;
        _selectedReasoningEffort = ChatReasoningEfforts.Normalize(_active?.ReasoningEffort);
        _selectedRouting = ChatRouting.Normalize(_active?.Routing);
        if (_active is { } opened)
        {
            NormalizeSessionReasoningEffort(opened);
            _selectedRouting = ChatRouting.Normalize(opened.Routing);
            // Asked before writing, because the write is not free: it replaces the whole session file, and for a
            // long conversation that file is the largest thing an open touches — larger than the read that just
            // happened, and entirely inside the open's own path. A card whose flag is already set has nothing to
            // say, and re-stamping it would also hand every reader a turn that is no longer the object it was.
            var unseen = opened.Messages.Any(turn => (turn.ApprovalState == ChatApprovalStates.Pending || turn.PlanApprovalState == PlanApprovalStates.Pending) && !turn.ApprovalSeen);
            if (unseen)
            {
                _sessions.TryUpdate(opened.Id, conversation =>
                {
                    for (var index = 0; index < conversation.Messages.Count; index++)
                    {
                        var turn = conversation.Messages[index];
                        if (turn.ApprovalSeen)
                            continue;
                        if (turn.ApprovalState == ChatApprovalStates.Pending || turn.PlanApprovalState == PlanApprovalStates.Pending)
                            conversation.Messages[index] = turn with
                            {
                                ApprovalSeen = true
                            };
                    }
                });
            }
        }

        measurement.ApprovalMs = measurement.ElapsedMs - approvalStarted;
        var notifyStarted = measurement.ElapsedMs;
        Execution.Changed?.Invoke();
        measurement.NotifyMs = measurement.ElapsedMs - notifyStarted;
        if (_active is { Goal: { Length: > 0 } } goalThread && NormalizeMode(goalThread.Mode) == ChatModes.Agent
            && Execution.RunFor(goalThread.Id) is null)
            Execution.RestoreGoal(goalThread.Id);
        if (_active is { } selected) _ = Execution.RefreshGitPresentationAsync(selected.Id);
        return _active;
    }

    internal bool TryResolvePlanApproval(string conversationId, int turnIndex, string decision, out string? refusalKey,
        bool isGoal = false)
    {
        refusalKey = null;
        if (decision is not (PlanApprovalStates.Approved or PlanApprovalStates.RevisionRequested or PlanApprovalStates.Rejected))
            throw new ArgumentOutOfRangeException(nameof(decision));
        var conversation = _sessions.Peek(conversationId) ?? _sessions.Load(conversationId);
        if (conversation is null || turnIndex < 0 || turnIndex >= conversation.Messages.Count)
        {
            refusalKey = "ChatApprovalGone";
            return false;
        }

        var pending = conversation.Messages[turnIndex];
        if (pending.Role != ChatRoles.Assistant || pending.PlanApprovalState != PlanApprovalStates.Pending)
        {
            refusalKey = "ChatApprovalGone";
            return false;
        }

        if (decision == PlanApprovalStates.Approved)
        {
            // The body is read before anything is judged, because a filed plan's text is not on the turn: every
            // test below would pass on an empty string, and an empty string is what would then be sent.
            var body = PlanStore.BodyOf(pending, _dataRoot, out var bodyState);
            // The document is named and not on disk: approving would start an Agent run on an instruction with no
            // plan in it. Refusing says so, and the card stays where the person can still revise or reject it.
            if (bodyState is not (PlanBodyState.Read or PlanBodyState.NoReference) || string.IsNullOrEmpty(body))
            {
                refusalKey = "ChatPlanBodyGone";
                return false;
            }

            if (Providers.ResolvedModel(conversationId, out refusalKey)is null)
                return false;
            if (Execution.RunningCount >= MaxConcurrentRuns)
            {
                refusalKey = "ChatParallelLimit";
                return false;
            }
        }

        var resolved = false;
        var updated = _sessions.TryUpdate(conversationId, opened =>
        {
            if (turnIndex >= opened.Messages.Count || opened.Messages[turnIndex].PlanApprovalState != PlanApprovalStates.Pending)
                return;
            opened.Messages[turnIndex] = opened.Messages[turnIndex] with
            {
                PlanApprovalState = decision
            };
            if (decision == PlanApprovalStates.Approved)
            {
                opened.Mode = ChatModes.Agent;
                var userTurn = ChatTurn.User(PlanApprovalInstruction, isGoal: isGoal) with { Plan = pending.Plan };
                if (userTurn.IsGoal)
                    Execution.InitializeGoal(opened, userTurn);
                opened.Append(userTurn);
            }
            else if (decision == PlanApprovalStates.RevisionRequested)
            {
                opened.Mode = ChatModes.Plan;
            }
            resolved = true;
        });
        if (!updated || !resolved)
        {
            refusalKey = "ChatApprovalGone";
            return false;
        }

        if (_active?.Id == conversationId)
            _selectedMode = decision == PlanApprovalStates.Approved ? ChatModes.Agent : ChatModes.Plan;
        Execution.Changed?.Invoke();
        if (decision == PlanApprovalStates.Approved)
            Execution.StartRun(conversationId);
        return true;
    }

    internal void DeleteConversation(string id)
    {
        Execution.StopRunFor(id);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var liveConversations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var summary in _sessions.List())
        {
            if (summary.Id == id) continue;
            liveConversations.Add(summary.Id);
            var conversation = _sessions.Peek(summary.Id) ?? _sessions.Load(summary.Id);
            if (conversation is null) continue;
            foreach (var turn in conversation.Messages)
            {
                if (turn.ChangeId is { Length: > 0 } changeId) referenced.Add(changeId);
                if (turn.UndoRevisionId is { Length: > 0 } undoId) referenced.Add(undoId);
                if (turn.ManagementBatchId is { Length: > 0 } batchId) referenced.Add(batchId);
                foreach (var reference in turn.ChangeRefs) referenced.Add(reference.RevisionId);
            }
            foreach (var changeId in conversation.ChangeHistoryIds)
                if (changeId is { Length: > 0 }) referenced.Add(changeId);
        }
        _sessions.Delete(id);
        ChatUndoStore.DeleteConversationCopies(_dataRoot, id);
        ChatChangeStore.PruneSession(_dataRoot, liveConversations, referenced);
        CloseIfActive(id);
        Execution.Changed?.Invoke();
    }

    private void CloseIfActive(string id)
    {
        if (_active?.Id != id) return;
        _active = null;
        _selectedMode = ChatModes.Agent;
        _selectedReasoningEffort = ChatReasoningEfforts.Default;
    }

    internal bool RenameConversation(string conversationId, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return false;
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Title = trimmed)) return false;
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool SetPinned(string conversationId, bool pinned)
    {
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Pinned = pinned)) return false;
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool SetArchived(string conversationId, bool archived)
    {
        // Before the write, not after: a run still streaming into a session the user just put away is the same
        // reply nobody will read, and stopping it is what makes the wake queue and the run registry agree with
        // the list on screen.
        if (archived) Execution.StopRunFor(conversationId);
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Archived = archived)) return false;
        if (archived) CloseIfActive(conversationId);
        Execution.Changed?.Invoke();
        return true;
    }

    internal int ArchiveWorkspace(string? workspaceRoot, bool archived)
    {
        var key = SessionGroupKey.Workspace(workspaceRoot);
        if (key is null) return 0;

        var changed = 0;
        foreach (var summary in _sessions.List())
        {
            if (summary.Archived == archived) continue;
            if (SessionGroupKey.Workspace(summary.WorkspaceRoot) != key) continue;
            if (archived) Execution.StopRunFor(summary.Id);
            if (!_sessions.TryUpdate(summary.Id, conversation => conversation.Archived = archived)) continue;
            if (archived) CloseIfActive(summary.Id);
            changed++;
        }

        if (changed > 0) Execution.Changed?.Invoke();
        return changed;
    }

    internal int RestoreArchivedSessions()
    {
        var restored = 0;
        foreach (var summary in _sessions.List())
        {
            if (!summary.Archived) continue;
            if (_sessions.TryUpdate(summary.Id, conversation => conversation.Archived = false)) restored++;
        }

        if (restored > 0) Execution.Changed?.Invoke();
        return restored;
    }

    internal (int Moved, WorkspacePathVerdict? Verdict) RepointWorkspace(string? fromRoot, string toPath)
    {
        var key = SessionGroupKey.Workspace(fromRoot);
        if (key is null) return (0, WorkspacePathVerdict.EscapesWorkspace);
        var (verdict, full) = ValidateWorkspacePath(toPath);
        if (verdict is not null) return (0, verdict);

        var moved = 0;
        foreach (var summary in _sessions.List())
        {
            if (SessionGroupKey.Workspace(summary.WorkspaceRoot) != key) continue;
            if (_sessions.TryUpdate(summary.Id, conversation => conversation.WorkspaceRoot = full)) moved++;
        }

        // A directory picked while nothing was on screen is the same place under its old spelling. Left alone,
        // the next session would start somewhere the user has just stopped pointing at.
        if (SessionGroupKey.Workspace(_selectedWorkspaceRoot) == key) _selectedWorkspaceRoot = full;

        // The registry entry moves with the group rather than being re-derived from the sessions: a workspace that
        // has no sessions left still has a path the person in front of the screen just corrected, and it has to
        // repaint for that.
        var renamed = _workspaces.Replace(fromRoot, full);
        if (moved > 0 || renamed) Execution.Changed?.Invoke();
        return (moved, null);
    }

    internal Conversation? BranchFrom(string conversationId, int index)
    {
        var source = _sessions.Peek(conversationId) ?? _sessions.Load(conversationId);
        if (source is null) return null;

        var title = NextBranchTitle(source.Title);
        // The cut point is decided with the source locked: the branch must not capture a turn that arrived
        // after the click, and the list a running session is growing must not be enumerated unlocked.
        var prefix = _sessions.Snapshot(source.Id, index + 1);
        if (prefix is null) return null;

        var branch = new Conversation
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            ProviderId = source.ProviderId,
            ModelName = source.ModelName,
            Mode = source.Mode,
            ReasoningEffort = source.ReasoningEffort,
            // A fork continues the same task, so it continues in the same directory. Copied rather than left to be
            // picked again: the person asked for the same tree, not for the same question a second time.
            WorkspaceRoot = source.WorkspaceRoot,
            // And the directories granted to it, for the same reason: a fork that kept the workspace but dropped
            // the grant would be a copy of the task that has to ask the same question again, and the copy is the
            // one the person already answered. The single files travel with them — one file is a grant like any
            // other, and the folder a fork works in has nothing to do with which files were opened by name.
            GrantedRoots = [.. source.GrantedRoots],
            ReadOnlyRoots = [.. source.ReadOnlyRoots],
            GrantedFiles = [.. source.GrantedFiles],
            Messages = prefix,
            ChangeHistoryIds = [.. source.ChangeHistoryIds
                .Concat(prefix.SelectMany(turn => new[] { turn.ChangeId, turn.UndoRevisionId }))
                .Where(id => id is { Length: > 0 })
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)],
            ContextSummary = prefix.Count >= SummaryMessageCount(source) ? source.ContextSummary : "",
            ContextSummaryThroughMessageCount = prefix.Count >= SummaryMessageCount(source)
                ? SummaryMessageCount(source)
                : 0,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            // A fork is the person starting a conversation, so it sorts by the fork moment, not by the
            // inherited prefix's last turn.
            EngagedAt = DateTimeOffset.Now,
            BranchSourceId = source.Id,
            BranchSourceIndex = index,
        };

        _sessions.Adopt(branch);
        _active = branch;
        _selectedMode = NormalizeMode(branch.Mode);
        NormalizeSessionReasoningEffort(branch);
        _selectedRouting = ChatRouting.Normalize(branch.Routing);
        Execution.Changed?.Invoke();
        return branch;
    }

    private string NextBranchTitle(string sourceTitle)
    {
        var cut = sourceTitle.LastIndexOf(" (", StringComparison.Ordinal);
        var baseTitle = cut > 0 && sourceTitle.EndsWith(')')
                        && int.TryParse(sourceTitle.AsSpan(cut + 2, sourceTitle.Length - cut - 3), out _)
            ? sourceTitle[..cut]
            : sourceTitle;
        if (baseTitle.Length == 0) return sourceTitle;

        var prefix = baseTitle + " (";
        var highest = 0;
        foreach (var title in _sessions.List().Select(summary => summary.Title))
        {
            if (!title.StartsWith(prefix, StringComparison.Ordinal) || !title.EndsWith(')')) continue;
            if (int.TryParse(title.AsSpan(prefix.Length, title.Length - prefix.Length - 1), out var number)
                && number > highest) highest = number;
        }

        return baseTitle + " (" + (highest + 1) + ")";
    }

    internal string SessionFilePath(string conversationId) => _sessions.Store.SessionFilePath(conversationId);
    internal string? StoredImagePath(string conversationId, string file)
        => _sessions.Store.ImagePath(conversationId, file);
    internal string? PlanFilePath(ChatTurn turn) => PlanStore.PathOf(turn, _dataRoot);
    internal string? PlanBody(ChatTurn turn, out PlanBodyState state) => PlanStore.BodyOf(turn, _dataRoot, out state);
    internal string? ViewedConversationId => _active?.Id;

    private RepositorySnapshot? _repository;
    private sealed class RepositoryRead
    {
        internal bool RefreshAgain;
        internal TaskCompletionSource<RepositorySnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, RepositoryRead> _repositoryReads = new(StringComparer.Ordinal);
    /// <summary>Raised when a read lands, so an open pane repaints. Nothing raises it on a timer.</summary>
    internal Action? RepositoryChanged;
    private static bool IsRepositoryForWorkspace(string? workspace, string? repository)
    {
        if (workspace is null || repository is null) return false;
        var relative = Path.GetRelativePath(repository, workspace);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal async Task<GitFileComparison> ReadGitCommitFileAsync(string conversationId, string repositoryRoot,
        string relativePath, string commit, CancellationToken cancellationToken = default)
    {
        var scope = Execution.ScopeFor(conversationId).Workspace;
        var expected = WorkspacePaths.CanonicalRoot(scope.WorkspaceRoot);
        var actual = WorkspacePaths.CanonicalRoot(repositoryRoot);
        if (expected is null || actual is null || !IsRepositoryForWorkspace(expected, actual))
            return new GitFileComparison(relativePath, null, GitFileRange.Commit,
                GitFileVersion.Absent(), GitFileVersion.Absent(), "Repository root is outside the current session workspace.");
        var runner = new ProcessRunner(line => scope.Log?.Write(line));
        return await GitRepository.ReadCommitFileAsync(actual, relativePath, commit, runner,
            scope.SensitiveValues, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DiffHunkActionResult> ApplyAiHunkAsync(string conversationId, string relativePath,
        string revisionId, int hunkIndex, DiffHunkAction action, CancellationToken cancellationToken = default)
    {
        if (action is not (DiffHunkAction.Accept or DiffHunkAction.Reject))
            return new(false, "This is not an AI revision action.");
        var revision = ChatChangeStore.Read(_dataRoot, revisionId);
        if (revision is null || revision.ConversationId != conversationId || revision.State != ChatChangeStore.Complete)
            return new(false, "The selected AI revision is missing, pending or belongs to another conversation.");
        if (string.IsNullOrWhiteSpace(relativePath))
            return new(false, "The selected AI revision path is empty.");
        var snapshot = ChatChangeStore.ReadSnapshot(_dataRoot, revisionId, out var snapshotState);
        if (snapshot is null || snapshotState != UndoCopyState.Read)
            return new(false, "The selected AI revision snapshot is unavailable.");
        var initialScope = Execution.ScopeFor(conversationId).Workspace;
        var initialRoot = WorkspacePaths.CanonicalRoot(initialScope.WorkspaceRoot);
        var revisionRoot = WorkspacePaths.CanonicalRoot(revision.Root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (initialRoot is null || revisionRoot is null || !string.Equals(initialRoot, revisionRoot, comparison))
            return new(false, "The conversation workspace changed since this AI revision.");
        var initialPath = WorkspacePaths.ResolveWrite(initialScope.Sandbox, relativePath, initialScope.Guards);
        if (!initialPath.IsAllowed || !string.Equals(initialPath.Full, revision.Path, comparison))
            return new(false, "Current write permission does not include this revision path.");

        using var lease = TextFile.AcquireWrite(initialPath.Full);
        var baseline = TextFile.Read(initialPath.Full, TextFile.MaxEditorBytes);
        if (baseline.State != TextFileState.Text)
            return new(false, baseline.Detail ?? "The current file is not supported text.");
        if (!AiHunkOperations.TryApply(snapshot.Before, snapshot.After, baseline.Text!, hunkIndex,
                action == DiffHunkAction.Accept ? AiHunkAction.Accept : AiHunkAction.Reject,
                out var updated, out var detail)) return new(false, detail);
        if (updated == TextEol.ToLf(baseline.Text!)) return new(true, detail);

        ChatFileRevision pending;
        try { pending = ChatChangeStore.Prepare(_dataRoot, conversationId, initialPath.Full, initialRoot, baseline.Text, updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(false, "Could not record the new revision: " + ex.Message); }

        var result = lease.Save(() =>
        {
            try
            {
                var currentScope = Execution.ScopeFor(conversationId).Workspace;
                var currentRoot = WorkspacePaths.CanonicalRoot(currentScope.WorkspaceRoot);
                if (currentRoot is null || !string.Equals(currentRoot, initialRoot, comparison))
                    return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relativePath);
                var resolved = WorkspacePaths.ResolveWrite(currentScope.Sandbox, relativePath, currentScope.Guards);
                return resolved.IsAllowed && string.Equals(resolved.Full, initialPath.Full, comparison)
                    ? resolved : WorkspacePath.Refuse(resolved.IsAllowed ? WorkspacePathVerdict.ReparsePoint : resolved.Verdict, relativePath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                { return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relativePath); }
        }, baseline, updated);
        if (result.Verdict == TextReplaceVerdict.Written)
        {
            try { ChatChangeStore.CompleteRevision(_dataRoot, pending); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return new(false, "The file changed, but revision completion needs recovery: " + ex.Message, true); }
            Execution.Changed?.Invoke();
            return new(true, "The selected AI hunk was applied as a new revision.", true);
        }
        ChatChangeStore.Abandon(_dataRoot, pending);
        return new(false, result.Detail ?? result.Verdict.ToString());
    }

    internal async Task<DiffHunkActionResult> ApplyGitHunkAsync(string conversationId, string repositoryRoot,
        GitStatusEntry entry, GitFileComparison displayed, int hunkIndex, DiffHunkAction action,
        CancellationToken cancellationToken = default)
    {
        var scope = Execution.ScopeFor(conversationId).Workspace;
        var expected = WorkspacePaths.CanonicalRoot(scope.WorkspaceRoot);
        var actual = WorkspacePaths.CanonicalRoot(repositoryRoot);
        if (expected is null || actual is null || !IsRepositoryForWorkspace(expected, actual))
            return new(false, "Repository root is outside the current session workspace.");
        var target = WorkspacePaths.ResolveWrite(scope.Sandbox, Path.Combine(actual, entry.RelativePath), scope.Guards);
        if (!target.IsAllowed) return new(false, "Current session permission does not allow this Git hunk operation.");
        var runner = new ProcessRunner(line => scope.Log?.Write(line));
        return await GitHunkOperations.ApplyAsync(actual, entry, displayed, hunkIndex, action, runner,
            scope.SensitiveValues, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<GitFileComparison> ReadGitFileAsync(string conversationId, string repositoryRoot,
        GitStatusEntry entry, GitFileRange range, CancellationToken cancellationToken = default)
    {
        var scope = Execution.ScopeFor(conversationId).Workspace;
        var expected = WorkspacePaths.CanonicalRoot(scope.WorkspaceRoot);
        var actual = WorkspacePaths.CanonicalRoot(repositoryRoot);
        if (expected is null || actual is null || !IsRepositoryForWorkspace(expected, actual))
            return new GitFileComparison(entry.RelativePath, entry.OriginalPath, range,
                GitFileVersion.Absent(), GitFileVersion.Absent(), "Repository root is outside the current session workspace.");
        var runner = new ProcessRunner(line => scope.Log?.Write(line));
        return await GitRepository.ReadFileAsync(actual, entry.RelativePath, range, runner,
            entry.OriginalPath, scope.SensitiveValues, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<GitFileComparison> ReadActiveWorkspaceGitFileAsync(string repositoryRoot,
        GitStatusEntry entry, GitFileRange range, CancellationToken cancellationToken = default)
    {
        var workspace = WorkspacePaths.CanonicalRoot(ActiveWorkspaceRoot);
        var root = WorkspacePaths.CanonicalRoot(repositoryRoot);
        if (workspace is null || root is null || !IsRepositoryForWorkspace(workspace, root))
            return new GitFileComparison(entry.RelativePath, entry.OriginalPath, range,
                GitFileVersion.Absent(), GitFileVersion.Absent(), "Repository root is outside the current workspace.");
        var runner = new ProcessRunner(line => Execution.LogProvider?.Invoke()?.Write(line));
        return await GitRepository.ReadFileAsync(root, entry.RelativePath, range, runner,
            entry.OriginalPath, Providers.SensitiveValues(), cancellationToken).ConfigureAwait(false);
    }

    internal RepositorySnapshot? RepositoryFor(string? workspaceRoot)
        => _repository is { } snapshot
           && string.Equals(RepositoryKey(snapshot.WorkspaceRoot), RepositoryKey(workspaceRoot), StringComparison.Ordinal)
            ? snapshot
            : null;
    internal Task<RepositorySnapshot> RefreshRepositoryAsync(string conversationId, bool refresh = true)
    {
        var scope = Execution.ScopeFor(conversationId).Workspace;
        return RefreshRepositoryAsync(scope.WorkspaceRoot ?? "", scope.Log, scope.SensitiveValues, conversationId, refresh);
    }

    internal Task<RepositorySnapshot> RefreshActiveWorkspaceRepositoryAsync(bool refresh = true)
        => RefreshRepositoryAsync(ActiveWorkspaceRoot ?? "", Execution.LogProvider?.Invoke(),
            Providers.SensitiveValues(), null, refresh);

    private Task<RepositorySnapshot> RefreshRepositoryAsync(string root, HubLog? log,
        IReadOnlyList<string> sensitiveValues, string? conversationId, bool refresh)
    {
        var key = RepositoryKey(root);
        RepositoryRead reading;
        lock (_repositoryReads)
        {
            if (_repositoryReads.TryGetValue(key, out var inFlight))
            {
                // Loading a missing snapshot shares the current read. Explicit refreshes need one more pass;
                // any number of clicks during that pass coalesce instead of losing the latest filesystem state.
                if (refresh) inFlight.RefreshAgain = true;
                return inFlight.Completion.Task;
            }
            reading = new RepositoryRead();
            _repositoryReads.Add(key, reading);
        }
        _ = ReadRepositoryAsync(root, log, sensitiveValues, conversationId, key, reading);
        return reading.Completion.Task;
    }

    private async Task ReadRepositoryAsync(string workspaceRoot, HubLog? log,
        IReadOnlyList<string> sensitiveValues, string? conversationId, string key, RepositoryRead reading)
    {
        try
        {
            while (true)
            {
                RepositoryReadsForCheck++;
                var runner = new ProcessRunner(line => log?.Write(line));
                var state = await GitRepository.ReadAsync(workspaceRoot, runner, sensitiveValues)
                    .ConfigureAwait(false);
                var snapshot = new RepositorySnapshot(workspaceRoot, state);
                var delivered = false;
                await Execution.ApplyOnUiAsync(() =>
                {
                    // A refresh requested during the read makes this answer obsolete. Do not release the pane's
                    // totals gate until the follow-up status arrives, or it would read new totals on old rows.
                    lock (_repositoryReads)
                    {
                        if (reading.RefreshAgain) return;
                        _repository = snapshot;
                        delivered = true;
                    }
                    RepositoryChanged?.Invoke();
                }).ConfigureAwait(false);
                if (delivered && conversationId is not null)
                    await Execution.RefreshGitPresentationAsync(conversationId, state, workspaceRoot).ConfigureAwait(false);
                lock (_repositoryReads)
                {
                    if (reading.RefreshAgain)
                    {
                        reading.RefreshAgain = false;
                        continue;
                    }
                    _repositoryReads.Remove(key);
                    reading.Completion.TrySetResult(snapshot);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_repositoryReads) _repositoryReads.Remove(key);
            reading.Completion.TrySetException(ex);
        }
    }


    private static bool IsListed(ConversationSummary summary) => summary.MessageCount > 0;
    /// <summary>What approving a plan writes as the user's instruction, <b>without</b> the plan under it. The body
    /// rides on the turn as a <see cref = "PlanReference"/> like the plan turn's own, and the request boundary puts
    /// the text back (<see cref = "MaterializePlans"/>), so the bytes the model is handed are the ones it is handed
    /// today while the session file names one document instead of holding two copies of it.</summary>


    private static string RepositoryKey(string? root) => WorkspacePaths.CanonicalRoot(root) ?? "";

    internal static string NormalizeMode(string? mode) => mode is ChatModes.Ask or ChatModes.Plan or ChatModes.Agent
        ? mode
        : ChatModes.Agent;

    internal int PruneEmptyConversations()
    {
        var emptyIds = _sessions.List().Where(summary => summary.MessageCount == 0 && Execution.RunFor(summary.Id)is null).Select(summary => summary.Id).ToList();
        foreach (var id in emptyIds)
            _sessions.Delete(id);
        if (_active is not null && emptyIds.Contains(_active.Id))
            _active = null;
        if (emptyIds.Count > 0)
            Execution.Changed?.Invoke();
        return emptyIds.Count;
    }

    internal string SessionImageDirectory(string conversationId) => _sessions.Store.SessionImageDirectory(conversationId);

}
