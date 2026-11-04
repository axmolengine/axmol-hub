using System.Text;
using System.Text.Json;
using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

internal static class AiSessionsChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // Existing continuation coverage now tests evidence, not the number of tool rows.
        var firstEmpty = GoalContinuation.Decide("partial", false, 0, 0);
        var secondEmpty = GoalContinuation.Decide("partial again", false, firstEmpty.EmptyRuns, 1, noProgressLimit: 2);
        var working = GoalContinuation.Decide("partial", true, firstEmpty.EmptyRuns, 1);
        var limited = GoalContinuation.Decide("partial", true, 0, 7, continuationLimit: 7);
        var blocked = GoalContinuation.Decide("need access\n[GOAL_BLOCKED]", true, 0, 0);
        var completed = GoalContinuation.Decide("done\n[GOAL_COMPLETE]", false, 1, 700);
        if (!firstEmpty.Continue || secondEmpty.State != ChatGoalStates.Paused || secondEmpty.Reason != "no_progress"
            || !working.Continue || working.EmptyRuns != 0 || limited.State != ChatGoalStates.BudgetLimited
            || limited.Reason != "limit" || blocked.State != ChatGoalStates.Blocked || blocked.Reason != "blocked"
            || completed.State != ChatGoalStates.Complete || completed.Continue)
            throw new Exception("Goal continuation did not stop on inactivity, blockers, completion or its automatic limit.");
        for (var run = 0; run < 1000; run++)
            if (!GoalContinuation.Decide("new evidence", true, 0, run, continuationLimit: 0).Continue)
                throw new Exception("Zero continuation ceiling still stopped a progressing goal.");
        foreach (var literal in new[] { "quoted [GOAL_COMPLETE]", "> [GOAL_COMPLETE]", "    [GOAL_COMPLETE]",
                     "```text\n[GOAL_COMPLETE]", "~~~\n[GOAL_COMPLETE]", "[GOAL_COMPLETE]\nnot done",
                     "```text\n[GOAL_COMPLETE]\nCommit summary: fix: sample",
                     "[GOAL_COMPLETE]\nnot done\nCommit summary: fix: sample" })
            if (GoalContinuation.ParseFinalMarker(literal).Marker is not null)
                throw new Exception("Literal marker completed a goal: " + literal);
        if (GoalContinuation.ParseFinalMarker("```\n[GOAL_COMPLETE]\n```\n[GOAL_COMPLETE]").Text != "```\n[GOAL_COMPLETE]\n```"
            || GoalContinuation.Decide("[GOAL_COMPLETE]", true, 0, 0, completionAllowed: false).State == ChatGoalStates.Complete)
            throw new Exception("Completion stripped prose or ignored failed verification.");
        // Optional commit metadata cannot mask completion, in either order or with a configured custom prefix.
        foreach (var answer in new[] { "Done.\n[GOAL_COMPLETE]\nCommit summary: fix: finish the task",
                     "Done.\n[GOAL_COMPLETE]\nCommit summary: chore: finish the task",
                     "Done.\nCommit summary: fix: finish the task\n[GOAL_COMPLETE]", "Done.\n[GOAL_COMPLETE]" })
        {
            var parsed = GoalContinuation.ParseFinalMarker(answer);
            if (GoalContinuation.Decide(answer, false, 2, 700).State != ChatGoalStates.Complete
                || parsed.Text.Contains(GoalContinuation.CompleteMarker, StringComparison.Ordinal)
                || CommitSummary.Parse(parsed.Text) != CommitSummary.Parse(answer))
                throw new Exception("Optional commit metadata masked completion or was removed with the marker.");
        }
        var legacyGoal = JsonSerializer.Deserialize<Conversation>("""{"Goal":"legacy","GoalState":"paused","GoalStopReason":"limit","GoalContinuationCount":3}""")!;
        var legacyPolicy = GoalPolicy.For(legacyGoal);
        if (!legacyPolicy.CanContinue(legacyGoal) || legacyGoal.GoalContinuationCount != 3
            || legacyGoal.GoalReportedTokens != 0 || legacyGoal.GoalState != ChatGoalStates.Paused)
            throw new Exception("Legacy goals lost their state or inherited the retired three-run ceiling.");
        var goalSession = Conversation.Create("orcarouter");
        goalSession.GoalEmptyRunCount = secondEmpty.EmptyRuns;
        goalSession.GoalStopReason = secondEmpty.Reason;
        goalSession.GoalState = ChatGoalStates.Waiting;
        goalSession.GoalWakeAt = DateTimeOffset.UtcNow.AddMinutes(2);
        goalSession.GoalContinuationLimit = 0;
        goalSession.GoalTokenBudget = 200;
        goalSession.GoalReportedTokens = 90;
        goalSession.GoalEstimatedTokens = 10;
        if (!GoalProgress.Observe(goalSession, ToolNames.FsRead, "{\"path\":\"a\",\"limit\":2}", "evidence", false)
            || GoalProgress.Observe(goalSession, ToolNames.FsRead, "{\"limit\":2,\"path\":\"a\"}", "evidence", false)
            || GoalProgress.Observe(goalSession, ToolNames.FsRead, "{\"path\":\"a\",\"limit\":3}", "evidence", false)
            || GoalProgress.Observe(goalSession, ToolNames.RunCommand, "{}", "shell: /bin/sh · exit: 1\nfailed", false))
            throw new Exception("A duplicate read or failed command counted as progress.");
        var policy = GoalPolicy.For(goalSession);
        if (!policy.CanRequest(goalSession, 100) || policy.CanRequest(goalSession, 101))
            throw new Exception("Goal request estimate escaped its remaining budget.");
        var goalStore = new ConversationStore(root);
        goalStore.Save(goalSession);
        var restoredGoal = goalStore.Load(goalSession.Id);
        goalSession.GoalEmptyRunCount = 2;
        goalStore.Save(goalSession);
        restoredGoal = goalStore.Load(goalSession.Id);
        if (restoredGoal?.GoalState != ChatGoalStates.Waiting || restoredGoal.GoalWakeAt != goalSession.GoalWakeAt
            || restoredGoal.GoalReportedTokens != 90 || restoredGoal.GoalEstimatedTokens != 10
            || restoredGoal.GoalProgressFingerprints.Count != 4)
            throw new Exception("Goal checkpoint lost its cooldown, evidence or spend.");
        if (restoredGoal?.GoalEmptyRunCount != 2 || restoredGoal.GoalStopReason != "no_progress")
            throw new Exception("Goal stop reason and inactivity count did not round-trip.");
        goalStore.Delete(goalSession.Id);
        Console.WriteLine("PASS: goal continuation bounds inactive runs and persists its stop reason.");

        // Conversation model: title is derived from the first user turn, and the first line only.
        var conversation = Conversation.Create("orcarouter");
        if (conversation.Mode != ChatModes.Agent)
            throw new Exception($"A new conversation should start in the default agent mode, got '{conversation.Mode}'.");
        Console.WriteLine("PASS: new conversations default to agent mode.");
        conversation.Append(ChatTurn.User("How do I add a sprite?\nSecond line ignored"));
        conversation.Append(ChatTurn.Assistant("Use Sprite::create."));
        if (conversation.Title != "How do I add a sprite?")
            throw new Exception($"Wrong derived title: {conversation.Title}");
        if (Conversation.DeriveTitle(new string ('a', 80)).Length != 49)
            throw new Exception("Long title was not truncated to 48 chars plus ellipsis.");
        Console.WriteLine("PASS: conversation derives its title from the first user turn.");
        // Persistence: save, reload, and the index must agree without reading message bodies.
        var store = new ConversationStore(root);
        store.Save(conversation);
        var reloaded = store.Load(conversation.Id) ?? throw new Exception("Saved conversation did not reload.");
        if (reloaded.Messages.Count != 2 || reloaded.Messages[1].Text != "Use Sprite::create.")
            throw new Exception("Conversation turns did not round-trip.");
        var listed = store.List().Single(summary => summary.Id == conversation.Id);
        if (listed.MessageCount != 2 || listed.Title != conversation.Title)
            throw new Exception("Index entry disagrees with the conversation.");
        Console.WriteLine("PASS: ConversationStore round-trips turns and maintains the index.");
        // What is on disk has to stay readable: the default JSON encoder escapes quotes, backticks, angle
        // brackets and every non-ASCII character, turning a Chinese transcript into escaped-code-unit soup.
        var readable = Conversation.Create("orcarouter");
        readable.Append(ChatTurn.User("列出当前 Hub 登记的项目"));
        readable.Append(ChatTurn.Assistant("| 项目 | 状态 |\n|---|---|\n| HelloCpp | `Configured` | \"ok\" +1 'x' <b>"));
        store.Save(readable);
        var savedText = File.ReadAllText(Path.Combine(root, "ai", "sessions", readable.Id + ".json"));
        if (!savedText.Contains("项目", StringComparison.Ordinal) || !savedText.Contains("`Configured`", StringComparison.Ordinal) || !savedText.Contains("\\\"ok\\\"", StringComparison.Ordinal) || !savedText.Contains("<b>", StringComparison.Ordinal) || savedText.Contains("\\u00", StringComparison.Ordinal))
            throw new Exception("The session file escaped characters that should stay literal.");
        store.Delete(readable.Id);
        Console.WriteLine("PASS: session files keep CJK, backticks, quotes and angle brackets readable.");
        // A tool call's arguments are stored as a JSON *string* and printed verbatim on the approval card, so the
        // same readability has to survive there — and the undo reader still has to parse that form back.
        var callTurn = ChatTurn.FunctionCall("call-cjk", "file_write", "{\"path\":\"笔记/note.txt\",\"old_string\":\"第二行\",\"new_string\":\"第二行（已改）\"}");
        var callArguments = Conversation.Create("orcarouter");
        callArguments.Append(callTurn);
        store.Save(callArguments);
        var callText = File.ReadAllText(Path.Combine(root, "ai", "sessions", callArguments.Id + ".json"));
        if (!callText.Contains("第二行", StringComparison.Ordinal) || callText.Contains("\\u00", StringComparison.Ordinal))
            throw new Exception("A stored tool call's arguments arrived as escaped code units.");
        if (ChatUndoStore.WritePathOf(callTurn.ToolArguments) != "笔记/note.txt")
            throw new Exception("The undo reader could not take the unescaped form of a stored call's arguments.");
        store.Delete(callArguments.Id);
        Console.WriteLine("PASS: a stored tool call keeps its arguments readable and still parseable.");
        conversation.Mode = ChatModes.Plan;
        conversation.ReasoningEffort = ChatReasoningEfforts.High;
        conversation.Messages.Add(ChatTurn.User("Inspect this file", "file content"));
        store.Save(conversation);
        reloaded = store.Load(conversation.Id) ?? throw new Exception("Conversation with mode/context did not reload.");
        if (reloaded.Mode != ChatModes.Plan || reloaded.ReasoningEffort != ChatReasoningEfforts.High || reloaded.Messages[^1].AttachedContext != "file content")
            throw new Exception("Composer settings or attached context did not round-trip.");
        Console.WriteLine("PASS: Conversation mode, reasoning effort, and attached context persist.");
        // "auto" is what this tier used to be called. Session files are hand-editable and are written by every older
        // build, so the old spelling has to load and read as the neutral tier rather than strand the session or pick
        // a strength nobody chose.
        File.WriteAllText(Path.Combine(root, "ai", "sessions", "legacy-effort.json"), """{"Id":"legacy-effort","Title":"legacy","ProviderId":"orcarouter","Messages":[],"ReasoningEffort":"auto"}""");
        var legacyEffort = store.Load("legacy-effort") ?? throw new Exception("A session written before the reasoning tier was renamed no longer loads.");
        if (legacyEffort.ReasoningEffort != "auto" || ChatReasoningEfforts.Normalize(legacyEffort.ReasoningEffort) != ChatReasoningEfforts.Default || ChatReasoningEfforts.Normalize("a tier this build has never heard of") != ChatReasoningEfforts.Default || ChatReasoningEfforts.Normalize(null) != ChatReasoningEfforts.Default || ChatReasoningEfforts.Normalize(ChatReasoningEfforts.XHigh) != ChatReasoningEfforts.XHigh)
            throw new Exception("The renamed reasoning tier did not fall back to Default for the old and unknown spellings.");
        if (Conversation.Create("orcarouter").ReasoningEffort != ChatReasoningEfforts.Default)
            throw new Exception("A new session does not start on the neutral reasoning tier.");
        Console.WriteLine("PASS: a reasoning effort stored as \"auto\" reads as the neutral Default tier.");
        // Branch provenance is optional on purpose: a branch records where it was cut, and a session written
        // before the field existed must still load (the store ignores absent properties).
        var branch = Conversation.Create("orcarouter");
        branch.Append(ChatTurn.User("从这条提问分叉"));
        branch.BranchSourceId = conversation.Id;
        branch.BranchSourceIndex = 1;
        store.Save(branch);
        var reloadedBranch = store.Load(branch.Id) ?? throw new Exception("Branched conversation did not reload.");
        if (reloadedBranch.BranchSourceId != conversation.Id || reloadedBranch.BranchSourceIndex != 1)
            throw new Exception("Branch provenance did not round-trip through the session file.");
        File.WriteAllText(Path.Combine(root, "ai", "sessions", "legacy-no-provenance.json"), """{"Id":"legacy-no-provenance","Title":"legacy","ProviderId":"orcarouter","ModelName":"","Messages":[]}""");
        if (store.Load("legacy-no-provenance")is not { BranchSourceId: null, BranchSourceIndex: null })
            throw new Exception("A session file predating branch provenance no longer loads.");
        store.Delete("legacy-no-provenance");
        store.Delete(branch.Id);
        Console.WriteLine("PASS: branch provenance round-trips, and pre-branch session files still load.");
        // A hostile id may not escape the sessions directory.
        var escaped = false;
        try
        {
            store.Delete("../../evil");
        }
        catch (ArgumentException)
        {
            escaped = true;
        }

        if (!escaped && File.Exists(Path.Combine(root, "evil.json")))
            throw new Exception("Conversation id escaped the sessions directory.");
        Console.WriteLine("PASS: ConversationStore sanitizes ids (no path traversal).");
        // Deletion removes both the file and the index entry.
        store.Delete(conversation.Id);
        if (store.Load(conversation.Id)is not null)
            throw new Exception("Deleted conversation still loads.");
        if (store.List().Any(summary => summary.Id == conversation.Id))
            throw new Exception("Deleted conversation is still listed.");
        Console.WriteLine("PASS: ConversationStore deletes conversations and their index entries.");
        // Pinned sessions sort above more recently engaged ones, and the pin survives an index round-trip.
        var older = Conversation.Create("orcarouter");
        older.Append(ChatTurn.User("older"));
        older.UpdatedAt = DateTimeOffset.Now.AddHours(-1);
        older.EngagedAt = older.UpdatedAt;
        store.Save(older);
        var newer = Conversation.Create("orcarouter");
        newer.Append(ChatTurn.User("newer"));
        store.Save(newer);
        if (store.List()[0].Id != newer.Id)
            throw new Exception("The most recently engaged conversation did not sort first.");
        older.Pinned = true;
        store.Save(older);
        var byPin = store.List();
        if (byPin[0].Id != older.Id || !byPin[0].Pinned)
            throw new Exception("A pinned conversation did not sort first, or lost its pin through the index.");
        Console.WriteLine("PASS: ConversationStore sorts pinned conversations first.");
        store.Delete(older.Id);
        store.Delete(newer.Id);
        // The sidebar orders by engagement, not transcript activity: a model reply or tool round landing while
        // parallel runs answer must not reshuffle the list. The person's own turn is the key; machine turns are not.
        var engagementBase = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var background = Conversation.Create("orcarouter");
        background.Append(new ChatTurn(ChatRoles.User, "asked first", engagementBase));
        // The answer lands much later — tool call, tool result, assistant reply — every one with a newer timestamp.
        background.Append(ChatTurn.FunctionCall("bg-call", "read_file", "{}")with { At = engagementBase.AddMinutes(10) });
        background.Append(ChatTurn.FunctionResult("bg-call", "ok")with { At = engagementBase.AddMinutes(11) });
        background.Append(new ChatTurn(ChatRoles.Assistant, "done", engagementBase.AddMinutes(12)));
        var foreground = Conversation.Create("orcarouter");
        foreground.Append(new ChatTurn(ChatRoles.User, "asked second", engagementBase.AddMinutes(5)));
        store.Save(background);
        store.Save(foreground);
        var engagementOrder = store.List();
        if (engagementOrder[0].Id != foreground.Id || engagementOrder[1].Id != background.Id)
            throw new Exception("Mid-run model/tool turns moved a conversation above one the person engaged more recently.");
        if (background.EngagedAt != engagementBase || background.UpdatedAt != engagementBase.AddMinutes(12))
            throw new Exception("Machine turns must advance UpdatedAt but leave the engagement key untouched.");
        // The person's next turn reopens the conversation; a turn injected by a peer session is machine-authored.
        background.Append(new ChatTurn(ChatRoles.User, "again", engagementBase.AddMinutes(20)));
        background.Append(new ChatTurn(ChatRoles.User, "peer note", engagementBase.AddMinutes(30)) { InjectedFrom = foreground.Id });
        store.Save(background);
        if (store.List()[0].Id != background.Id || background.EngagedAt != engagementBase.AddMinutes(20))
            throw new Exception("Only a person-authored turn may advance the engagement key; a machine turn did.");
        if (store.List().First(summary => summary.Id == background.Id).EngagedAt != engagementBase.AddMinutes(20))
            throw new Exception("The engagement key did not survive into the index header.");
        // MarkEngaged is the run-ending/parking door: it advances the list key without touching transcript time.
        var beforeMark = background.UpdatedAt;
        background.MarkEngaged(engagementBase.AddMinutes(40));
        if (background.EngagedAt != engagementBase.AddMinutes(40) || background.UpdatedAt != beforeMark)
            throw new Exception("MarkEngaged must advance the list key without touching UpdatedAt.");
        Console.WriteLine("PASS: the session list orders by engagement, so parallel replies do not reshuffle it.");
        store.Delete(background.Id);
        store.Delete(foreground.Id);
        // A session file written before the engagement key existed falls back to its last update time, instead of
        // sorting at the epoch and losing its place forever.
        var legacy = Conversation.Create("orcarouter");
        legacy.Append(new ChatTurn(ChatRoles.User, "old", engagementBase.AddMinutes(-30)));
        legacy.EngagedAt = null;
        legacy.UpdatedAt = engagementBase.AddMinutes(15);
        store.Save(legacy);
        var pivot = Conversation.Create("orcarouter");
        pivot.Append(new ChatTurn(ChatRoles.User, "pivot", engagementBase.AddMinutes(10)));
        store.Save(pivot);
        var fallbackOrder = store.List();
        var legacyRow = fallbackOrder.First(summary => summary.Id == legacy.Id);
        if (fallbackOrder[0].Id != legacy.Id || legacyRow.EngagedAt is not null)
            throw new Exception("A pre-engagement session did not fall back to UpdatedAt for its place.");
        Console.WriteLine("PASS: sessions without an engagement key fall back to their update time.");
        store.Delete(legacy.Id);
        store.Delete(pivot.Id);
        // Workspace grouping. The sidebar groups sessions by the directory they work in, so two spellings of one
        // directory have to make one group, and the header row has to carry enough to do that without opening a
        // single transcript.
        var workspaceFolder = Path.Combine(Path.GetTempPath(), "AxmolHub-Checks-Workspace");
        var workspaceKey = WorkspacePaths.CanonicalRoot(workspaceFolder);
        if (workspaceKey != WorkspacePaths.CanonicalRoot(workspaceFolder + Path.DirectorySeparatorChar))
            throw new Exception("A trailing separator changed the canonical form of one workspace directory.");
        if (OperatingSystem.IsWindows())
        {
            if (WorkspacePaths.CanonicalRoot(@"d:\dev\ws") != WorkspacePaths.CanonicalRoot(@"D:\DEV\WS\"))
                throw new Exception("Two spellings of one Windows directory produced two workspace keys.");
        }
        else
        {
            if (WorkspacePaths.CanonicalRoot("/dev/WS") == WorkspacePaths.CanonicalRoot("/dev/ws"))
                throw new Exception("A case-sensitive host folded two different directories into one workspace key.");
        }

        // A drive root is the one path whose trailing separator is part of its name: "D:\" trimmed to "D:" is the
        // current directory on D, so the canonical form of a root has to keep the separator it arrived with.
        var driveRoot = Path.GetPathRoot(Path.GetTempPath());
        if (!string.IsNullOrEmpty(driveRoot) && Path.EndsInDirectorySeparator(driveRoot) && WorkspacePaths.CanonicalRoot(driveRoot) != (OperatingSystem.IsWindows() ? driveRoot.ToUpperInvariant() : driveRoot))
            throw new Exception("Canonicalizing a drive root changed which directory it names.");
        if (SessionGroupKey.Workspace(null)is not null || SessionGroupKey.Workspace("   ")is not null)
            throw new Exception("A session with no workspace produced a workspace key instead of the plain-chat bucket.");
        if (SessionGroupKey.Workspace(workspaceFolder) != "ws:" + workspaceKey)
            throw new Exception("The workspace group key is not the canonical directory under its own prefix.");
        if (SessionGroupKey.Workspace(@"D:\other") == SessionGroupKey.Workspace(workspaceFolder))
            throw new Exception("Two different workspace directories share one group key.");
        if (SessionGroupKey.LabelFor(workspaceFolder) != "AxmolHub-Checks-Workspace")
            throw new Exception("A workspace group's label is not the folder it names.");
        Console.WriteLine("PASS: workspace canonical keys fold casing and separators into one spelling per directory.");
        var workspaceSession = Conversation.Create("orcarouter");
        workspaceSession.Append(ChatTurn.User("works inside a folder"));
        workspaceSession.WorkspaceRoot = workspaceFolder;
        workspaceSession.Archived = true;
        store.Save(workspaceSession);
        var header = store.List().Single(summary => summary.Id == workspaceSession.Id);
        if (!string.Equals(header.WorkspaceRoot, workspaceFolder, StringComparison.Ordinal))
            throw new Exception("The index row lost the session's workspace directory, so grouping would need every transcript open.");
        if (!header.Archived)
            throw new Exception("The index row lost the archive flag, so an archived session would still be listed as live.");
        Console.WriteLine("PASS: session summaries carry the workspace root and the archive flag.");
        // The shape on disk, read as bytes rather than through the API over it. Every repair below depends on a
        // legacy bare array failing to bind, which is only true while the store writes an envelope: write the array
        // again and this is the assertion that says so, on the same file the next reader will open.
        var indexPath = Path.Combine(root, "ai", "sessions", "index.json");
        if (!File.ReadAllText(indexPath).TrimStart().StartsWith('{'))
            throw new Exception("index.json is a bare array again, so an index written before these columns existed would be obeyed rather than rebuilt.");
        // The migration is the shape change. An index written as a bare array — every file on disk before these
        // columns existed — cannot bind to the envelope, so the store rebuilds it from the session files, which do
        // carry them. Obeying it instead would file every project's sessions under plain chats, silently.
        File.WriteAllText(indexPath, "[{\"id\":\"" + workspaceSession.Id + "\",\"title\":\"旧索引\",\"providerId\":\"orcarouter\"," + "\"messageCount\":1,\"updatedAt\":\"2026-10-01T00:00:00+08:00\",\"pinned\":false}]");
        var rebuilt = store.List().Single(summary => summary.Id == workspaceSession.Id);
        if (!string.Equals(rebuilt.WorkspaceRoot, workspaceFolder, StringComparison.Ordinal) || !rebuilt.Archived)
            throw new Exception("A legacy array index was obeyed instead of rebuilt.");
        if (!File.ReadAllText(indexPath).TrimStart().StartsWith('{'))
            throw new Exception("The rebuild wrote the legacy shape back, so the same file will be misread again next start.");
        Console.WriteLine("PASS: an index written before workspace grouping existed is rebuilt, not obeyed.");
        // Archived is a listing rule, not a delete: the row stays in the store, with its workspace binding intact,
        // and comes back to the same group when it is restored.
        var stillThere = store.Load(workspaceSession.Id) ?? throw new Exception("Archiving removed the session file rather than the sidebar row.");
        if (!stillThere.Archived || !string.Equals(stillThere.WorkspaceRoot, workspaceFolder, StringComparison.Ordinal))
            throw new Exception("An archived session lost its own record of being archived, or of where it works.");
        workspaceSession.Archived = false;
        store.Save(workspaceSession);
        if (!store.List().Any(summary => summary.Id == workspaceSession.Id && !summary.Archived))
            throw new Exception("Restoring an archived session did not put it back on the live list.");
        store.Delete(workspaceSession.Id);
        Console.WriteLine("PASS: archiving keeps the session and its workspace binding, and restoring undoes it.");
        // ContextTrimmer: system turns survive, oldest turns are dropped, and order is preserved.
        var history = new List<ChatTurn>
        {
            ChatTurn.System("You are Axmol's assistant.")
        };
        for (var index = 0; index < 20; index++)
            history.Add(ChatTurn.User($"message number {index} " + new string ('x', 300)));
        var trimmed = ContextTrimmer.Trim(history, budget: 400);
        if (trimmed.Count == 0)
            throw new Exception("Trimmer produced an empty prompt.");
        if (trimmed[0].Role != ChatRoles.System)
            throw new Exception("Trimmer dropped the system turn.");
        if (trimmed.Count >= history.Count)
            throw new Exception("Trimmer did not drop anything under a tight budget.");
        var kept = trimmed.Where(turn => turn.Role == ChatRoles.User).ToList();
        if (kept[^1].Text != history[^1].Text)
            throw new Exception("Trimmer did not keep the newest turn.");
        if (trimmed.Select(ContextTrimmer.EstimateTokens).Sum() > 470)
            throw new Exception("Trimmer exceeded the budget by more than one message.");
        Console.WriteLine("PASS: ContextTrimmer keeps the system turn and the newest turns within budget.");
        // A single oversized message must not produce an empty prompt.
        var oversize = ContextTrimmer.Trim([ChatTurn.User(new string ('y', 4000))], budget: 10);
        if (oversize.Count != 1)
            throw new Exception("Trimmer dropped an oversized newest turn, leaving no prompt.");
        Console.WriteLine("PASS: ContextTrimmer keeps at least the newest turn.");
        if (ContextTrimmer.EstimateTokens(ChatTurn.User("question", "attached file contents")) <= ContextTrimmer.EstimateTokens("question"))
            throw new Exception("Context estimator ignored attached file contents.");
        Console.WriteLine("PASS: ContextTrimmer accounts for attached context.");
        var openAiProvider = AiProviderManifest.CreateBuiltIn("openai")!;
        var deepSeekProvider = AiProviderManifest.CreateBuiltIn("deepseek")!;
        // The built-in manifest describes no tiers for these models any more: the service publishes the ladder at
        // runtime, so what can be checked here is that nothing is *claimed*. The list this used to restate — low,
        // medium, high, xhigh, max — came off the manifest in the 2026-10-13 retune, and a restatement that outlives
        // the thing it restates is how a green check starts lying about the product.
        if (ModelCatalog.EffortsFor(openAiProvider, "gpt-6.1-sol").Count != 0
            || !ModelCatalog.MaySendEffort(openAiProvider, "gpt-6.1-sol", ChatReasoningEfforts.High))
            throw new Exception("A model the manifest leaves undescribed claimed declared tiers, or stopped being askable with one.");
        // "Nobody told us" is no longer the same as "it cannot". The assertion this replaces demanded that an
        // unknown model report no support at all — and that is precisely why the tier menu never appeared for the
        // 205 models on the default gateway: the manifest names one model, the gateway publishes nothing, and a
        // capability that can only be demonstrated after it has been granted stays hidden forever.
        if (!ModelCatalog.MaySendEffort(openAiProvider, "unknown-model", ChatReasoningEfforts.High) || ModelCatalog.EffortsFor(openAiProvider, "unknown-model").Count != 0)
            throw new Exception("An undescribed model must stay askable while still offering nothing as known.");
        if (ModelCatalog.MaySendEffort(openAiProvider, "unknown-model", ChatReasoningEfforts.Default))
            throw new Exception("The 'default' tier is not a strength; it must never go on the wire as one.");
        var openAiReasoning = ModelCatalog.ReasoningFor(openAiProvider, "gpt-6.1-sol");
        if (openAiReasoning is not null || ModelCatalog.DefaultReasoningEffort(openAiProvider, "gpt-6.1-sol") != ChatReasoningEfforts.Default || ModelCatalog.DefaultReasoningEffort(openAiProvider, "unknown-model") != ChatReasoningEfforts.Default)
            throw new Exception("GPT-6.1-Sol without a manifest default must defer to the service.");
        var parsedReasoning = ModelList.ParseMetadata(Encoding.UTF8.GetBytes("""{"object":"list","data":[{"id":"deepseek-flash","effort":{"supported_levels":["low","high","max"],"default_level":"high"}},{"id":"deepseek-v4-pro","effort":{"supported_levels":["low","high","max"],"default_level":"high"}}]}"""));
        if (!parsedReasoning.Models.SequenceEqual(["deepseek-flash", "deepseek-v4-pro"]) || !parsedReasoning.ReasoningModels["deepseek-flash"].Efforts.SequenceEqual(["low", "high", "max"]) || parsedReasoning.ReasoningModels["deepseek-flash"].DefaultEffort != "high")
            throw new Exception("DeepSeek /models effort metadata was not parsed as declared.");
        deepSeekProvider.ReasoningModels["deepseek-flash"] = new AiModelReasoning
        {
            Efforts = [..parsedReasoning.ReasoningModels["deepseek-flash"].Efforts],
            DefaultEffort = parsedReasoning.ReasoningModels["deepseek-flash"].DefaultEffort,
            RequestOptions = deepSeekProvider.ReasoningModels["deepseek-flash"].RequestOptions,
        };
        if (!ModelCatalog.SupportsReasoningEffort(deepSeekProvider, "deepseek-flash", ChatReasoningEfforts.Max) || ModelCatalog.SupportsReasoningEffort(deepSeekProvider, "deepseek-flash", ChatReasoningEfforts.Medium))
            throw new Exception("DeepSeek effort choices should match the live /models metadata.");
        if (!ModelCatalog.SelectableReasoningEfforts(deepSeekProvider, "deepseek-flash")
                .SequenceEqual([ChatReasoningEfforts.Low, ChatReasoningEfforts.High, ChatReasoningEfforts.Max])
            || ModelCatalog.EffortAtLevel(deepSeekProvider, "deepseek-flash", 0) != ChatReasoningEfforts.Low
            || ModelCatalog.EffortAtLevel(deepSeekProvider, "deepseek-flash", 1) != ChatReasoningEfforts.High
            || ModelCatalog.EffortAtLevel(deepSeekProvider, "deepseek-flash", 2) != ChatReasoningEfforts.Max
            || ModelCatalog.EffortAtLevel(deepSeekProvider, "deepseek-flash", 3) is not null)
            throw new Exception("The slider did not expose and select the model's declared tiers directly.");
        if (!ModelCatalog.SelectableReasoningEfforts(openAiProvider, "unknown-model")
                .SequenceEqual([ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High])
            || ModelCatalog.EffortAtLevel(openAiProvider, "unknown-model", 2) != ChatReasoningEfforts.High)
            throw new Exception("An unrecognized model did not fall back to the Low, Medium, High ladder.");
        if (!ModelCatalog.SelectableReasoningEfforts(openAiProvider, "gpt-6-luna")
                .SequenceEqual([ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                    ChatReasoningEfforts.XHigh])
            || !ModelCatalog.SelectableReasoningEfforts(openAiProvider, "gpt-6.1-sol")
                .SequenceEqual([ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                    ChatReasoningEfforts.XHigh, ChatReasoningEfforts.Ultra]))
            throw new Exception("GPT Luna and non-Luna models did not expose their distinct reasoning tiers.");
        var gptLunaWithMax = ProviderWith("gpt-6-luna", new AiModelReasoning
        {
            Efforts = [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                ChatReasoningEfforts.XHigh, ChatReasoningEfforts.Max],
        });
        var gptOtherWithMax = ProviderWith("gpt-6.1-sol", new AiModelReasoning
        {
            Efforts = [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                ChatReasoningEfforts.XHigh, ChatReasoningEfforts.Ultra, ChatReasoningEfforts.Max],
        });
        if (!ModelCatalog.SelectableReasoningEfforts(gptLunaWithMax, "gpt-6-luna")
                .SequenceEqual([ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                    ChatReasoningEfforts.XHigh])
            || !ModelCatalog.SelectableReasoningEfforts(gptOtherWithMax, "gpt-6.1-sol")
                .SequenceEqual([ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                    ChatReasoningEfforts.XHigh, ChatReasoningEfforts.Ultra]))
            throw new Exception("GPT metadata leaked Max into the model-specific reasoning ladder.");
        Console.WriteLine("PASS: reasoning efforts come from explicit model metadata, including DeepSeek /models.");
        // What a model was seen to do outranks what a file said, and what it refused closes only that one tier.
        static ModelProvider ProviderWith(string model, AiModelReasoning profile)
        {
            var provider = new ModelProvider
            {
                Id = "evidence",
                BaseUrl = "https://evidence.test/v1"
            };
            provider.ReasoningModels[model] = profile;
            return provider;
        }

        var observed = ProviderWith("m", new AiModelReasoning { ObservedEfforts = [ChatReasoningEfforts.High] });
        if (ModelCatalog.EffortsFor(observed, "m")is not ["high"] || !ModelCatalog.SupportsReasoningEffort(observed, "m"))
            throw new Exception("A tier a reply proved is not offered, so evidence never reaches the menu.");
        var partialRefusal = ProviderWith("m", new AiModelReasoning { RejectedEfforts = [ChatReasoningEfforts.Ultra] });
        if (ModelCatalog.MaySendEffort(partialRefusal, "m", ChatReasoningEfforts.Ultra) || !ModelCatalog.MaySendEffort(partialRefusal, "m", ChatReasoningEfforts.Low))
            throw new Exception("A refusal closed the wrong tier — it must close the one that was refused, no more.");
        var everyRefusal = ProviderWith("m", new AiModelReasoning { RejectedEfforts = [..ModelCatalog.ReasoningEffortLadder] });
        if (ModelCatalog.MayTryUnreportedEfforts(everyRefusal, "m") || ModelCatalog.SupportsReasoningEffort(everyRefusal, "m"))
            throw new Exception("A model that refused every tier is still being offered the menu.");
        // The reader has to be able to tell a refusal from an unrelated failure: a bad credential must not quietly
        // delete a model's whole tier menu, which is what a loose match would do on the next 401.
        if (ModelCatalog.RejectedEffortOf("Error: value 'ultra' is not supported for field reasoning_effort", ["ultra", "max"]) != "ultra")
            throw new Exception("A refusal that named the field and the value was not read as one.");
        if (ModelCatalog.RejectedEffortOf("Incorrect API key provided: sk-abcdef", ["ultra", "max"])is not null)
            throw new Exception("An authentication failure was read as a model declining a reasoning tier.");
        if (ModelCatalog.RejectedEffortOf("reasoning_effort is not supported by this model", ["low"])is not null)
            throw new Exception("A refusal naming the field but not the asked-for value invented an answer.");
        Console.WriteLine("PASS: reasoning tiers come from evidence — proved by a reply, closed by a refusal, never by an unrelated error.");
        // Pipeline: turn <-> ChatMessage conversion is lossless, and streaming yields the fake text.
        var turns = new List<ChatTurn>
        {
            ChatTurn.System("sys"),
            ChatTurn.User("hello"),
            ChatTurn.Assistant("hi"),
            new(ChatRoles.Tool, "result", DateTimeOffset.Now)
        };
        var wireMessages = ChatPipeline.ToChatMessages(turns);
        if (wireMessages.Count != 4)
            throw new Exception("ToChatMessages lost or added messages.");
        for (var index = 0; index < turns.Count; index++)
            if (wireMessages[index].Text != turns[index].Text)
                throw new Exception($"Conversion lost text at index {index}.");
        if (wireMessages[3].Role != ChatRole.Tool)
            throw new Exception("Tool role did not map to ChatRole.Tool.");
        var toolTurns = new[]
        {
            ChatTurn.FunctionCall("call-1", "get_projects", "{\"filter\":\"active\"}"),
            ChatTurn.FunctionResult("call-1", "[{\"name\":\"Demo\"}]"),
        };
        var toolMessages = ChatPipeline.ToChatMessages(toolTurns);
        if (toolMessages[0].Contents.Single()is not FunctionCallContent || toolMessages[1].Contents.Single()is not FunctionResultContent)
            throw new Exception("Persisted tool protocol did not convert back to function call/result contents.");
        var attachedMessage = ChatPipeline.ToChatMessage(ChatTurn.User("Question", "attached reference"));
        if (!attachedMessage.Text.Contains("attached reference", StringComparison.Ordinal))
            throw new Exception("Attached context was not included in the model-facing user message.");
        Console.WriteLine("PASS: ChatPipeline converts turns to messages losslessly.");
        var fake = new FakeChatClient(["Hel", "lo!"]);
        var pipeline = new ChatPipeline(fake);
        var streamed = new StringBuilder();
        var provider = AiProviderManifest.CreateBuiltIn("orcarouter")!;
        provider.MaxContextTokens = 8192;
        foreach (var provider2 in new[]
        {
            provider
        }

        )
            await foreach (var chunk in pipeline.SendAsync(provider2, turns))
                streamed.Append(chunk);
        if (streamed.ToString() != "Hello!")
            throw new Exception($"Streamed text was '{streamed}' instead of 'Hello!'.");
        if (fake.LastMessages is null || fake.LastMessages.Count != 4)
            throw new Exception("Pipeline did not forward the full trimmed history.");
        Console.WriteLine("PASS: ChatPipeline streams assistant text through the fake client.");
        var embeddedThoughtClient = new FakeChatClient(["<thinking>private thought</thinking><thi", "nk>second thought</think>Visible <widget>xml</widget>"]);
        var embeddedThought = new StringBuilder();
        var embeddedVisible = new StringBuilder();
        await foreach (var chunk in new ChatPipeline(embeddedThoughtClient).SendAsync(provider,
                           [ChatTurn.User("test")], onReasoning: said => embeddedThought.Append(said)))
            embeddedVisible.Append(chunk);
        if (embeddedThought.ToString() != "private thoughtsecond thought"
            || embeddedVisible.ToString() != "Visible <widget>xml</widget>")
            throw new Exception($"Embedded reasoning was not separated from visible text (reasoning='{embeddedThought}', text='{embeddedVisible}').");
        Console.WriteLine("PASS: ChatPipeline separates embedded reasoning tags across streamed chunks.");
        var reasoningClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(reasoningClient).SendAsync(deepSeekProvider, [ChatTurn.User("test")], reasoningEffort: ChatReasoningEfforts.Max, modelName: "deepseek-flash"))
        {
        }

        var rawReasoningOptions = reasoningClient.LastOptions?.RawRepresentationFactory?.Invoke(reasoningClient) as OpenAI.Chat.ChatCompletionOptions;
#pragma warning disable SCME0001 // Assert the provider-specific JSON extension passed to the OpenAI adapter.

        var reasoningPatch = rawReasoningOptions?.Patch.ToString();
#pragma warning restore SCME0001
        if (reasoningPatch is null || !reasoningPatch.Contains("/reasoning_effort", StringComparison.Ordinal) || !reasoningPatch.Contains("\"max\"", StringComparison.Ordinal) || !reasoningPatch.Contains("/thinking", StringComparison.Ordinal) || !reasoningPatch.Contains("\"enabled\"", StringComparison.Ordinal))
            throw new Exception($"DeepSeek reasoning effort and thinking mode were not sent as raw request fields: {reasoningPatch ?? "<null>"}");
        Console.WriteLine("PASS: DeepSeek effort and thinking settings reach the OpenAI-compatible request.");
        // The same declaration has to reach a model no table entry names: a custom endpoint serving a DeepSeek id
        // the shipped preset never lists is exactly the case this fallback exists for, so both the ladder and the
        // thinking field have to arrive without one — including when the gateway put its own namespace in front of
        // the id or a variant marker behind it. deepseek-chat, bare or namespaced or dated, is the family's
        // non-thinking id and gets neither.
        if (ModelCatalog.SelectableReasoningEfforts(deepSeekProvider, "deepseek-v4.1-flash")
                is not [ChatReasoningEfforts.Low, ChatReasoningEfforts.High, ChatReasoningEfforts.Max]
            || !ModelCatalog.IsThinkingMode(deepSeekProvider, "deepseek-v4.1-flash")
            || !ModelCatalog.IsThinkingMode(deepSeekProvider, "zhongkey/deepseek-v4.1-flash")
            || !ModelCatalog.IsThinkingMode(deepSeekProvider, "deepseek-v4.1-flash:free")
            || ModelCatalog.IsThinkingMode(deepSeekProvider, "deepseek-chat")
            || ModelCatalog.IsThinkingMode(deepSeekProvider, "zhongkey/deepseek-chat")
            || ModelCatalog.IsThinkingMode(deepSeekProvider, "deepseek-chat-0324"))
            throw new Exception("An unlisted DeepSeek model did not fall back to its family's ladder and thinking field.");
        var familyClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(familyClient).SendAsync(deepSeekProvider, [ChatTurn.User("test")], reasoningEffort: ChatReasoningEfforts.High, modelName: "deepseek-v4.1-flash"))
        {
        }

#pragma warning disable SCME0001
        var familyPatch = (familyClient.LastOptions?.RawRepresentationFactory?.Invoke(familyClient) as OpenAI.Chat.ChatCompletionOptions)?.Patch.ToString();
#pragma warning restore SCME0001
        if (familyClient.LastOptions?.Reasoning?.Effort != ReasoningEffort.High
            || familyPatch is null || !familyPatch.Contains("/thinking", StringComparison.Ordinal))
            throw new Exception($"An unlisted DeepSeek model did not send its thinking field beside the chosen tier ({familyPatch ?? "<null>"}).");
        Console.WriteLine("PASS: a DeepSeek model with no table entry still travels with its family's tiers and thinking field.");
        // And the case that made the family fallback necessary in two halves: a model whose own catalog declared its
        // tiers arrives as an entry with efforts and no request fields, so reading the switch off that entry alone
        // is what silently took thinking back off a request after one model-list refresh. The declared ladder keeps
        // the tiers; the family supplies the field they switch on.
        var tiersOnly = ProviderWith("deepseek-v4-flash", new AiModelReasoning
        {
            Efforts = [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High],
        });
        var tiersOnlyClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(tiersOnlyClient).SendAsync(tiersOnly, [ChatTurn.User("test")], reasoningEffort: ChatReasoningEfforts.Medium, modelName: "deepseek-v4-flash"))
        {
        }

#pragma warning disable SCME0001
        var tiersOnlyPatch = (tiersOnlyClient.LastOptions?.RawRepresentationFactory?.Invoke(tiersOnlyClient) as OpenAI.Chat.ChatCompletionOptions)?.Patch.ToString();
#pragma warning restore SCME0001
        if (ModelCatalog.SelectableReasoningEfforts(tiersOnly, "deepseek-v4-flash")
                is not [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High]
            || tiersOnlyClient.LastOptions?.Reasoning?.Effort != ReasoningEffort.Medium
            || tiersOnlyPatch is null || !tiersOnlyPatch.Contains("/thinking", StringComparison.Ordinal))
            throw new Exception($"Tiers a model list declared displaced the field that switches reasoning on ({tiersOnlyPatch ?? "<null>"}).");
        Console.WriteLine("PASS: tiers a model list declared keep the family's thinking field on the request.");
        // The entry an endpoint that publishes nothing leaves behind is evidence-only, and evidence must not narrow the
        // menu: a tier the slider cannot reach is never sent, so it is never confirmed either. The family's ladder is
        // the floor it joins.
        var evidenceOnly = ProviderWith("deepseek-v4.1-flash", new AiModelReasoning
        {
            ObservedEfforts = [ChatReasoningEfforts.Medium, ChatReasoningEfforts.High],
        });
        if (ModelCatalog.SelectableReasoningEfforts(evidenceOnly, "deepseek-v4.1-flash")
                is not [ChatReasoningEfforts.Low, ChatReasoningEfforts.High, ChatReasoningEfforts.Max]
            || !ModelCatalog.IsThinkingMode(evidenceOnly, "deepseek-v4.1-flash"))
            throw new Exception("An evidence-only DeepSeek entry did not settle on the family's three tiers: an observed tier the family does not list was offered as a strength.");
        Console.WriteLine("PASS: a DeepSeek model's menu is its family's three tiers, whatever tiers replies were asked for.");
        // Where no family is known, an observed tier is all there is and stays on the menu: the fence above is the
        // family's, not a rule that evidence never counts.
        var observedElsewhere = ProviderWith("qwen-max", new AiModelReasoning
        {
            ObservedEfforts = [ChatReasoningEfforts.Medium],
        });
        if (ModelCatalog.SelectableReasoningEfforts(observedElsewhere, "qwen-max") is not [ChatReasoningEfforts.Medium])
            throw new Exception("An observed tier was dropped for a model whose family says nothing about tiers.");
        Console.WriteLine("PASS: where no family ladder exists, a tier a reply proved is the menu.");
        // The same declared fields, read off the options rather than off the connector. The patch above is how a body
        // built by the OpenAI SDK gets a field the SDK never modelled; a wire whose body this app writes itself has no
        // patch to reach, so the declaration has to arrive as data on the request. Both carriers are asserted because
        // they can drift apart, and only one of them reaches any wire today.
        if (reasoningClient.LastOptions?.AdditionalProperties?.GetValueOrDefault(ChatPipeline.RequestOptionsKey)is not IReadOnlyDictionary<string, System.Text.Json.JsonElement> carried || !carried.ContainsKey("thinking"))
            throw new Exception("The declared body fields reached the OpenAI patch but not the options a body built here would read.");
        Console.WriteLine("PASS: the declared body fields also ride the options, where a wire with no SDK connector can read them.");
        // And the negative half: a model with nothing declared gains no bag, so a body builder cannot invent a field
        // from a manifest that never named one.
        var quietCarrierClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(quietCarrierClient).SendAsync(deepSeekProvider, [ChatTurn.User("test")], modelName: "undeclared-model"))
        {
        }

        if (quietCarrierClient.LastOptions?.AdditionalProperties is { } quietExtras && quietExtras.ContainsKey(ChatPipeline.RequestOptionsKey))
            throw new Exception("An undescribed model arrived carrying declared request fields nobody declared.");
        Console.WriteLine("PASS: nothing is declared onto a request for a model that was never described.");
        var defaultDeepSeekClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(defaultDeepSeekClient).SendAsync(deepSeekProvider, [ChatTurn.User("test")], modelName: "deepseek-flash"))
        {
        }

        if (defaultDeepSeekClient.LastOptions?.Reasoning?.Effort != ReasoningEffort.High)
            throw new Exception("DeepSeek on the default tier did not honor the /models default effort.");
        Console.WriteLine("PASS: DeepSeek's default tier uses the effort declared by /models.");
        var defaultOpenAiClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(defaultOpenAiClient).SendAsync(openAiProvider, [ChatTurn.User("test")], modelName: "gpt-6.1-sol"))
        {
        }

        if (defaultOpenAiClient.LastOptions?.Reasoning is not null)
            throw new Exception("GPT-6.1-Sol without a declared default must leave the effort to the service.");
        Console.WriteLine("PASS: GPT-6.1-Sol without a declared default sends no reasoning effort.");
        // The other half of what "default" means: a model that lists effort levels but declares none of them as its
        // default gets *no* reasoning field at all. "Default" is not a strength, and mapping it to one — Minimal, say
        // — would send a bytes-level instruction the user never chose and this assertion would catch.
        openAiProvider.ReasoningModels["gpt-plain"] = new AiModelReasoning
        {
            Efforts = ["low", "high"]
        };
        var undeclaredDefaultClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(undeclaredDefaultClient).SendAsync(openAiProvider, [ChatTurn.User("test")], reasoningEffort: ChatReasoningEfforts.Default, modelName: "gpt-plain"))
        {
        }

        if (undeclaredDefaultClient.LastOptions?.Reasoning is not null)
            throw new Exception("The default tier invented a reasoning effort for a model that declares none.");
        Console.WriteLine("PASS: the default tier sends no reasoning field when the model has no declared default.");
        var extraHighClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(extraHighClient).SendAsync(openAiProvider, [ChatTurn.User("test")], reasoningEffort: ChatReasoningEfforts.XHigh, modelName: "gpt-6.1-sol"))
        {
        }

        if (extraHighClient.LastOptions?.Reasoning?.Effort != ReasoningEffort.ExtraHigh)
            throw new Exception("GPT-6.1-Sol xhigh was not mapped to Microsoft.Extensions.AI ExtraHigh.");
        Console.WriteLine("PASS: GPT-6.1-Sol xhigh maps to the OpenAI adapter's ExtraHigh effort.");
        var ultraClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(ultraClient).SendAsync(openAiProvider, [ChatTurn.User("test")], reasoningEffort: ChatReasoningEfforts.Ultra, modelName: "gpt-6.1-sol"))
        {
        }

        var rawUltraOptions = ultraClient.LastOptions?.RawRepresentationFactory?.Invoke(ultraClient) as OpenAI.Chat.ChatCompletionOptions;
#pragma warning disable SCME0001
        var ultraPatch = rawUltraOptions?.Patch.ToString();
#pragma warning restore SCME0001
        if (ultraPatch is null || !ultraPatch.Contains("\"ultra\"", StringComparison.Ordinal))
            throw new Exception("GPT-6.1-Sol ultra was not forwarded as an API effort value.");
        Console.WriteLine("PASS: GPT-6.1-Sol ultra is forwarded through the provider-specific request field.");
        // Asking for the token report is a field in the same body as the reasoning tier, and the pinned OpenAI SDK
        // exposes neither as a property — both go through one JSON patch on one raw representation. Two assignments
        // to RawRepresentationFactory would compile and the second would win, so the request would quietly stop
        // carrying the tier while the screen still showed it. This is the assertion that catches that shape.
        var wireClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(wireClient).SendAsync(openAiProvider, [ChatTurn.User("hi")], reasoningEffort: ChatReasoningEfforts.Ultra, modelName: "gpt-6.1-sol"))
        {
        }

#pragma warning disable SCME0001
        var wirePatch = (wireClient.LastOptions?.RawRepresentationFactory?.Invoke(wireClient) as OpenAI.Chat.ChatCompletionOptions)?.Patch.ToString();
#pragma warning restore SCME0001
        if (wirePatch is null || !wirePatch.Contains("stream_options", StringComparison.Ordinal) || !wirePatch.Contains("include_usage", StringComparison.Ordinal) || !wirePatch.Contains("\"ultra\"", StringComparison.Ordinal))
            throw new Exception($"The usage ask and the reasoning field did not both reach one request ({wirePatch}).");
        Console.WriteLine("PASS: the request asks for a usage report without displacing the reasoning field beside it.");
        var declined = AiProviderManifest.CreateBuiltIn("openai")!;
        declined.ExtraOptions[ModelProvider.StreamUsageOption] = "false";
        var declinedClient = new FakeChatClient(["ok"]);
        await foreach (var _ in new ChatPipeline(declinedClient).SendAsync(declined, [ChatTurn.User("hi")], reasoningEffort: ChatReasoningEfforts.Ultra, modelName: "gpt-6.1-sol"))
        {
        }

#pragma warning disable SCME0001
        var declinedPatch = (declinedClient.LastOptions?.RawRepresentationFactory?.Invoke(declinedClient) as OpenAI.Chat.ChatCompletionOptions)?.Patch.ToString();
#pragma warning restore SCME0001
        if (declined.WantsStreamUsage || declinedPatch is null || declinedPatch.Contains("stream_options", StringComparison.Ordinal) || !declinedPatch.Contains("reasoning_effort", StringComparison.Ordinal))
            throw new Exception($"include_stream_usage=false did not take just the usage field out ({declinedPatch}).");
        Console.WriteLine("PASS: a provider that was told not to ask gets no usage field and keeps the rest of its request.");
        // The read-back itself: the report is a sibling content on the stream, so the pipeline has to catch it where
        // it catches the text. The largest input is the window; the outputs add up.
        ContextReport? handed = null;
        var measuredClient = new FakeChatClient(["a", "b"], usage: [(900, 10, 4, 1), (1_200, 20, 8, 2)]);
        await foreach (var _ in new ChatPipeline(measuredClient).SendAsync(openAiProvider, [ChatTurn.User("hello there")], modelName: "gpt-plain", onUsage: report => handed = report))
        {
        }

        if (handed is null || !handed.IsMeasured || handed.InputTokens != 1_200 || handed.OutputTokens != 30 || handed.ReasoningTokens != 12 || handed.CachedInputTokens != 2 || handed.EstimatedInputTokens <= 0)
            throw new Exception($"A reported usage chunk did not reach the caller ({handed?.InputTokens ?? -1} in, " + $"{handed?.OutputTokens ?? -1} out).");
        if (handed.DriftPermille <= ContextReport.UncalibratedPermille)
            throw new Exception("The reading was paired with nothing, so the estimate could not be corrected by it.");
        // Both readings out of the same two chunks, in one assertion: the window is the larger input, the spend is
        // their sum. Whichever of the two a surface reaches for, this is the line that says they are not the same claim.
        if (handed.SummedInputTokens != 2_100 || handed.SummedCachedInputTokens != 3)
            throw new Exception($"The stream's two reports did not add up to what they cost " + $"({handed.SummedInputTokens} in, {handed.SummedCachedInputTokens} cached).");
        Console.WriteLine("PASS: the token report on the stream is handed to the caller, largest input and summed output.");
        // The negative control: a gateway that never sends the chunk. Silent is not zero — the session keeps
        // estimating, and a meter that read 0 would say an overflowing conversation was empty.
        var silent = new FakeChatClient(["ok"]);
        ContextReport? neverHanded = null;
        await foreach (var _ in new ChatPipeline(silent).SendAsync(openAiProvider, [ChatTurn.User("hi")], modelName: "gpt-plain", onUsage: report => neverHanded = report))
        {
        }

        if (neverHanded is not null)
            throw new Exception("A client that reported nothing produced a measurement anyway.");
        Console.WriteLine("PASS: a gateway that reports no usage leaves the session estimating instead of reading zero.");
        // The pipeline applies the budget it is given: an oversized history reaches the client trimmed.
        var longHistory = new List<ChatTurn>();
        for (var index = 0; index < 40; index++)
            longHistory.Add(ChatTurn.User($"turn {index} " + new string ('z', 400)));
        var tiny = AiProviderManifest.CreateBuiltIn("orcarouter")!;
        tiny.MaxContextTokens = 300;
        await foreach (var _ in new ChatPipeline(fake).SendAsync(tiny, longHistory))
        {
        }

        if (fake.LastMessages is null || fake.LastMessages.Count >= longHistory.Count)
            throw new Exception("Pipeline did not trim the oversized history before calling the client.");
        Console.WriteLine("PASS: ChatPipeline trims oversized history before the model call.");
        var toolClient = new ToolLoopChatClient();
        var startedTools = 0;
        var completedTools = 0;
        var toolResult = new StringBuilder();
        var toolIdentity = "";
        var readOnlyTool = AIFunctionFactory.Create((Func<string>)(() => "[{\"name\":\"Demo\"}]"), new AIFunctionFactoryOptions { Name = "get_projects", Description = "Return registered projects.", });
        await foreach (var chunk in new ChatPipeline(toolClient).SendAsync(provider, [ChatTurn.User("List projects")], tools: [readOnlyTool], onToolStarted: info =>
        {
            startedTools++;
            // The transcript is written from this triple, so a call the pipeline cannot identify
            // is a call whose result can never be paired with it.
            toolIdentity = $"{info.Name}|{info.CallId}|{info.ArgumentsJson}";
            return Task.CompletedTask;
        }, onToolCompleted: (_, result, failed) =>
        {
            if (failed)
                throw new Exception("Read-only tool unexpectedly failed.");
            completedTools++;
            toolResult.Append(result);
            return Task.CompletedTask;
        }))
            streamed.Append(chunk);
        if (toolClient.CallCount != 2 || startedTools != 1 || completedTools != 1 || toolResult.ToString() != "[{\"name\":\"Demo\"}]")
            throw new Exception($"Tool call → execution → result → final answer loop did not complete. Result handed back: {toolResult}");
        if (toolIdentity != "get_projects|call-1|{}")
            throw new Exception($"The pipeline reported the call as 「{toolIdentity}」.");
        if (!toolClient.SecondRequest.Any(message => message.Contents.Any(content => content is FunctionCallContent)) || !toolClient.SecondRequest.Any(message => message.Contents.Any(content => content is FunctionResultContent)))
            throw new Exception("The tool loop did not send the function call and result back to the model.");
        if (!toolClient.LastOptions!.Tools!.Any(tool => tool.Name == "get_projects"))
            throw new Exception("The allowed read-only tool was not exposed in chat options.");
        // The one knob in this file that used to tell the endpoint "at most one call per response". It is not sent any
        // more — the gateway that ignored it is the same one that answers with three calls today, and a flag nobody
        // obeys is a flag that only lies about what the client asked for. Pinned as absent rather than as a value, so
        // the request reads as leaving the choice where it belongs: with the provider.
        if (toolClient.LastOptions.AllowMultipleToolCalls is not null)
            throw new Exception($"A request went out suppressing parallel tool calls: {toolClient.LastOptions.AllowMultipleToolCalls}.");
        streamed.Clear();
        Console.WriteLine("PASS: ChatPipeline completes the read-only function-call loop.");
        // What the model streamed before asking for a call is stored on the call turn, and must come back as the
        // one assistant message it was sent as: text then call in a single message, not two assistant messages.
        var spoken = ChatPipeline.ToChatMessages([ChatTurn.User("改之前先看一眼"), ChatTurn.FunctionCall("c1", "get_projects", "{}", "先说的话"), ]);
        var spokenAssistant = spoken.Single(message => message.Role == ChatRole.Assistant);
        if (spoken.Count != 2 || spokenAssistant.Contents.Count != 2 || spokenAssistant.Contents[0] is not TextContent { Text: "先说的话" } || spokenAssistant.Contents[1] is not FunctionCallContent { CallId: "c1" })
            throw new Exception("The text streamed before a tool call did not replay as one assistant message.");
        var bareCall = ChatPipeline.ToChatMessages([ChatTurn.FunctionCall("c2", "get_projects", "{}")]);
        if (bareCall.Single().Contents.Count != 1 || bareCall.Single().Contents[0] is not FunctionCallContent)
            throw new Exception("A call turn without leading text gained an empty text content.");
        Console.WriteLine("PASS: pre-call text replays inside the assistant's tool-call message.");
        // Cancellation propagates as OperationCanceledException.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await foreach (var _ in new ChatPipeline(new FakeChatClient(["x"], honorCancellation: true)).SendAsync(provider, [ChatTurn.User("hi")], cancellationToken: cancellation.Token))
            {
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (!cancelled)
            throw new Exception("Cancellation was not propagated.");
        Console.WriteLine("PASS: ChatPipeline propagates cancellation.");
        // The workspace registry is what keeps a session-less workspace on screen, so the list it stores has to be
        // one entry per *place* rather than one per spelling, and it has to survive being read back by a process that
        // rebuilt every store from disk.
        var workspaceBase = Path.Combine(root, "registry-fixtures");
        var wsOne = Path.Combine(workspaceBase, "ws-one");
        var wsTwo = Path.Combine(workspaceBase, "ws-two");
        var registry = new WorkspaceStore(root);
        if (!registry.Add(wsOne) || !registry.Add(wsTwo))
            throw new Exception("A designated workspace was not accepted.");
        if (registry.Add(wsOne + Path.DirectorySeparatorChar))
            throw new Exception("Two spellings of one directory became two workspaces.");
        if (registry.Add("   ") || registry.Add(null))
            throw new Exception("A blank path was recorded as a workspace.");
        if (registry.Roots.Count != 2)
            throw new Exception($"The registry holds {registry.Roots.Count} entries, expected 2.");
        if (!string.Equals(registry.Roots[0], wsOne, StringComparison.Ordinal))
            throw new Exception($"Re-adding a workspace rewrote its spelling as 「{registry.Roots[0]}」.");
        if (!registry.Contains(wsOne) || !registry.Contains(Path.Combine(workspaceBase, "ws-one", "..", "ws-one")))
            throw new Exception("A workspace on the list does not resolve through the canonical key.");
        Console.WriteLine("PASS: WorkspaceStore records a workspace once per place, in the spelling the user gave.");
        // Order is the promise the sidebar keeps: session-derived groups come first, and registry-only ones follow in
        // the order they were named, so a suite (or a person) that added two workspaces sees them the same way twice.
        if (registry.Remove(wsTwo) != true || registry.Remove(wsTwo))
            throw new Exception("Removing a workspace was not reported once.");
        if (registry.Roots.Count != 1 || !string.Equals(registry.Roots[0], wsOne, StringComparison.Ordinal))
            throw new Exception("Removing one workspace took or renamed another.");
        registry.Add(wsTwo);
        registry.Replace(wsOne, Path.Combine(workspaceBase, "ws-moved"));
        if (registry.Roots.Count != 2 || !string.Equals(registry.Roots[0], Path.Combine(workspaceBase, "ws-moved"), StringComparison.Ordinal) || !string.Equals(registry.Roots[1], wsTwo, StringComparison.Ordinal))
            throw new Exception($"Re-pathing a workspace left the list as 「{string.Join(" | ", registry.Roots)}」.");
        registry.Replace(Path.Combine(workspaceBase, "never-registered"), Path.Combine(workspaceBase, "ws-three"));
        if (registry.Roots.Count != 3 || !string.Equals(registry.Roots[2], Path.Combine(workspaceBase, "ws-three"), StringComparison.Ordinal))
            throw new Exception("Re-pathing a workspace its sessions were only holding up did not register the destination.");
        Console.WriteLine("PASS: WorkspaceStore keeps its slot on a re-path and registers the destination of an unlisted one.");
        // The restart proof: the group has to come back for a directory no session names anymore, which is exactly the
        // state the registry exists for. A second store instance reads the same file a fresh window would.
        var reopened = new WorkspaceStore(root);
        if (reopened.Roots.Count != 3 || !reopened.Contains(Path.Combine(workspaceBase, "ws-moved")))
            throw new Exception($"A reopened registry reads 「{string.Join(" | ", reopened.Roots)}」.");
        // A directory that is not on disk is still a workspace the user named — the repair entry point lives on the
        // group this list produces, so dropping the entry would delete the only way to fix the path.
        var gone = Path.Combine(workspaceBase, "moved-away");
        reopened.Add(gone);
        if (!new WorkspaceStore(root).Contains(gone))
            throw new Exception("A workspace whose directory vanished was not remembered.");
        reopened.Clear();
        if (reopened.Roots.Count > 0 || new WorkspaceStore(root).Roots.Count > 0)
            throw new Exception("Clearing the registry left entries on disk.");
        Console.WriteLine("PASS: WorkspaceStore round-trips through disk and keeps a workspace whose directory is gone.");
        // A damaged registry reads as an empty one rather than taking the chat section down: the sessions still
        // describe their own directories, so the list rebuilds from what is on screen.
        var registryPath = Path.Combine(root, "ai", "workspaces.json");
        File.WriteAllText(registryPath, "{ this is not json");
        if (new WorkspaceStore(root).Roots.Count != 0)
            throw new Exception("A damaged registry did not fall back to empty.");
        File.WriteAllText(registryPath, """{"Roots":["a","","  ","a","b"]}""");
        var handEdited = new WorkspaceStore(root).Roots;
        if (handEdited.Count != 2 || !handEdited.Contains("b"))
            throw new Exception($"A hand-edited registry read as 「{string.Join(" | ", handEdited)}」, expected one 'a' and 'b'.");
        Console.WriteLine("PASS: WorkspaceStore normalizes a damaged or hand-edited file instead of throwing.");
        File.Delete(registryPath);
        // The one-time migration: the directories existing sessions already name become the registry, but only while
        // the file is absent. A backfill that re-ran on every start would resurrect a workspace somebody removed,
        // because an empty draft that left the list still names that directory on disk.
        var seeding = new WorkspaceStore(root);
        if (!seeding.SeedFrom([wsOne, wsTwo + Path.DirectorySeparatorChar, wsOne, null, "  "]) || seeding.Roots.Count != 2)
            throw new Exception("The first migration did not adopt the directories the sessions were already working in.");
        var afterMigration = new WorkspaceStore(root);
        if (afterMigration.SeedFrom([Path.Combine(workspaceBase, "ws-four")]) || afterMigration.Roots.Count != 2)
            throw new Exception("A second seed re-ran the migration over a registry that already exists.");
        Console.WriteLine("PASS: WorkspaceStore migrates once, from the absence of its file.");
        File.Delete(registryPath);
        // ── the plan store: a plan's body is a document, and the session file keeps its name ──
        var planRoot = Path.Combine(root, "plans-check");
        if (Directory.Exists(planRoot))
            Directory.Delete(planRoot, recursive: true);
        Directory.CreateDirectory(planRoot);
        const string planBody = "# 计划：把正文搬出会话文件\n\n" + "```text\nai/plans/<guid>.md\n```\n\n- 第一步：改动 `Conversation`，保留下划线。\n";
        if (PlanStore.DirectoryFor(null)is not null || PlanStore.DirectoryFor("")is not null)
            throw new Exception("The plan store invented a directory with no data root.");
        if (PlanStore.DirectoryFor(planRoot) != Path.Combine(planRoot, "ai", "plans"))
            throw new Exception($"Plans do not live beside the other ai/ state ({PlanStore.DirectoryFor(planRoot)}).");
        if (Directory.Exists(PlanStore.DirectoryFor(planRoot)!))
            throw new Exception("Asking where plans live created the directory; only a plan being stored pays for it.");
        var filed = PlanStore.Store(planRoot, planBody);
        if (!filed.Stored || !filed.Name.EndsWith(".md", StringComparison.Ordinal) || filed.Name.Length != 35 || !filed.Name[..32].All(Uri.IsHexDigit))
            throw new Exception($"A plan document was not filed under a GUID name ({filed.Name}, {filed.Message}).");
        if (PlanStore.Read(planRoot, filed.Name, out var filedState) != planBody || filedState != PlanBodyState.Read)
            throw new Exception("A plan body did not come back byte for byte from the file it was written to.");
        if (PlanStore.Store(planRoot, planBody).Name == filed.Name)
            throw new Exception("Two plans of the same body share one document, so one of them would be rewritten.");
        // The refusal table, and the reason each row exists: this directory is shared by every session, so a name
        // read out of one session file must not be able to ask for another's data or anything outside the store.
        foreach (var rejected in new[]
        {
            "..\\x.md",
            "../x.md",
            "x.txt",
            Path.Combine(planRoot, "x.md"),
            filed.Name[..31] + ".md",
            "index.json",
            "",
            "   "
        }

        )
        {
            if (PlanStore.Read(planRoot, rejected, out var rejectState)is not null || rejectState != PlanBodyState.RefusedPath)
                throw new Exception($"A plan name a session file invented was followed anyway ({rejected}).");
        }

        if (PlanStore.Read(planRoot, null, out var nullState)is not null || nullState != PlanBodyState.RefusedPath)
            throw new Exception("A turn with no name was read as a document rather than as no reference.");
        if (PlanStore.Store(planRoot, "").Stored || PlanStore.Store(null, planBody).Stored || PlanStore.Store("", planBody).Stored)
            throw new Exception("An empty body or a missing data root still wrote a document.");
        if (PlanStore.Store(planRoot, new string ('规', PlanStore.MaxBodyCharacters + 1)).Stored)
            throw new Exception("A plan longer than the store's ceiling was given a file of its own.");
        // The one reading rule, in both of the shapes it has to answer for: a turn naming a document, and a turn that
        // is the document. The second is every session file written before the store existed and every stamp whose
        // write failed — one code path, which is why the migration is "nothing".
        var namedTurn = ChatTurn.Assistant("")with
        {
            PlanApprovalState = PlanApprovalStates.Pending,
            Plan = new PlanReference(filed.Name, "计划", planBody.Length, ContextTrimmer.BodyTokens(planBody)),
        };
        var inlineTurn = ChatTurn.Assistant(planBody)with
        {
            PlanApprovalState = PlanApprovalStates.Pending
        };
        var missingTurn = namedTurn with
        {
            Plan = namedTurn.Plan!with
            {
                File = new string ('a', 32) + ".md"
            }
        };
        if (PlanStore.BodyOf(inlineTurn, planRoot, out var inlineState) != planBody || inlineState != PlanBodyState.NoReference)
            throw new Exception("A plan whose body is its own text was not read as one.");
        if (PlanStore.BodyOf(namedTurn, planRoot, out var namedState) != planBody || namedState != PlanBodyState.Read)
            throw new Exception("A plan's own turn did not resolve to the document it names.");
        if (PlanStore.BodyOf(missingTurn, planRoot, out var missingState)is not null || missingState != PlanBodyState.Gone)
            throw new Exception("A missing plan document read as an empty plan instead of as a fact.");
        if (!PlanStore.MissingNotice(filed.Name).Contains(filed.Name, StringComparison.Ordinal))
            throw new Exception("The notice handed the model did not say which document is gone.");
        // Titles are frozen where the body is filed, so the card never reads a file. Written by a model, so none of
        // the three shapes can be assumed: a heading, a preamble, or a fence first.
        if (PlanStore.TitleOf(planBody) != "计划：把正文搬出会话文件")
            throw new Exception($"A fenced preamble stole the title ({PlanStore.TitleOf(planBody)}).");
        if (PlanStore.TitleOf("- 直接以一个列表项开场\n\n正文") != "直接以一个列表项开场")
            throw new Exception("A plan with no heading gave its card no name.");
        if (PlanStore.TitleOf("```\nint main(){}\n```\n\n# 真标题") != "真标题")
            throw new Exception("Code inside a fence was read as the plan's own first line.");
        if (PlanStore.TitleOf("## 改 file_write 的两处") != "改 file_write 的两处" || PlanStore.TitleOf("## **加粗**的标题") != "加粗的标题")
            throw new Exception("A symbol or an emphasis mark was mishandled while the title was being made.");
        var longTitle = PlanStore.TitleOf(new string ('长', PlanStore.TitleCharacters + 40));
        if (longTitle.Length != PlanStore.TitleCharacters + 1 || !longTitle.EndsWith("…", StringComparison.Ordinal))
            throw new Exception($"A title longer than the card's terms was not cut to them ({longTitle.Length}).");
        if (PlanStore.TitleOf("") != "" || PlanStore.TitleOf("```\ncode only\n```") != "")
            throw new Exception("A plan with nothing to name invented a title instead of leaving it to the view.");
        // What the session file actually holds: the name, and not a character of the body. Read as text on purpose —
        // a hydrated object reads the body back the moment anything resolves the reference.
        var planStore = new ConversationStore(planRoot);
        var planSession = Conversation.Create("orcarouter");
        planSession.Append(ChatTurn.User("先给出计划"));
        planSession.Append(namedTurn);
        planSession.Append(ChatTurn.User("Implement the following plan.")with { Plan = namedTurn.Plan });
        planStore.Save(planSession);
        var planJson = planStore.ReadStoredText(planSession.Id) ?? throw new Exception("The plan session did not write a session file.");
        if (!planJson.Contains(filed.Name, StringComparison.Ordinal) || planJson.Contains("把正文搬出会话文件", StringComparison.Ordinal))
            throw new Exception("A filed plan's body was still written into the session file.");
        var rereadPlan = planStore.Load(planSession.Id) ?? throw new Exception("A plan session did not reload.");
        if (rereadPlan.Messages[1].Plan?.File != filed.Name || rereadPlan.Messages[1].Plan?.Chars != planBody.Length || rereadPlan.Messages[1].Plan?.Tokens != ContextTrimmer.BodyTokens(planBody) || rereadPlan.Messages[2].Plan?.File != filed.Name)
            throw new Exception("A plan reference did not survive the session file it is stored in.");
        if (PlanStore.BodyOf(rereadPlan.Messages[1], planRoot, out _) != planBody)
            throw new Exception("A plan reloaded from disk came back without its body.");
        File.WriteAllText(Path.Combine(planRoot, "ai", "sessions", "legacy-inline-plan.json"), """{"Id":"legacy-inline-plan","Title":"legacy","ProviderId":"orcarouter","Messages":[{"Role":"assistant","Text":"# 旧计划\n\n正文还在轮子上。","PlanApprovalState":"pending"}]}""");
        var legacyPlan = planStore.Load("legacy-inline-plan") ?? throw new Exception("A session file written before the plan store stopped loading.");
        if (legacyPlan.Messages[0].Plan is not null || PlanStore.BodyOf(legacyPlan.Messages[0], planRoot, out var legacyState) != "# 旧计划\n\n正文还在轮子上。" || legacyState != PlanBodyState.NoReference)
            throw new Exception("An inline plan from before the store was read as a document nobody wrote.");
        // Bounded the way the undo store is: enough plans to trip the cap, and the guarantee that matters — the one
        // just stamped is never the one the pass takes, because the write happens before the sweep.
        var last = PlanStore.Store(planRoot, "最新的一份计划").Name;
        for (var index = 0; index < PlanStore.MaxFiles + 10; index++)
            last = PlanStore.Store(planRoot, $"计划 {index:D3}：{(index % 7 == 0 ? "清理旧正文" : "file the body")}").Name;
        if (PlanStore.FileCount(planRoot) > PlanStore.MaxFiles)
            throw new Exception($"The plan store grew past its bound ({PlanStore.FileCount(planRoot)} files).");
        if (PlanStore.Read(planRoot, last, out _) != $"计划 {PlanStore.MaxFiles + 9:D3}：file the body")
            throw new Exception("The eviction pass took the plan that had just been written.");
        Console.WriteLine("PASS: PlanStore files a plan as ai/plans/<guid>.md, refuses every other name, and bounds itself.");
        // Hub's own JSON leaves through one funnel, and the indented formatter wrote the host's newline into every
        // state, settings and conversation file. Folded at the funnel — and only the formatter's: a CRLF inside a
        // stored string is data, and it has to come back exactly as it went in.
        var eolState = Path.Combine(root, "cache/eol-state.json");
        StateStore.WriteJson(eolState, new { text = "a\r\nb" });
        var eolJson = File.ReadAllText(eolState);
        if (eolJson.Contains('\r'))
            throw new Exception($"A state file wrote the host's newline into its JSON ({eolJson.Replace("\r", "\\r")}).");
        if (JsonDocument.Parse(eolJson).RootElement.GetProperty("text").GetString() != "a\r\nb")
            throw new Exception("Folding the JSON newlines changed a newline inside a stored value.");
        Console.WriteLine("PASS: state JSON is LF on every host, and a newline inside a stored value survives the fold.");
        return;
    }
}
