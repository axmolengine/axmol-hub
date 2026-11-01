using System.Text;
using System.Text.Json;
using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

internal static class AiToolPolicyChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // The permission model is one pure function over two small enums, so every cell is asserted rather than
        // sampled: a transposed table is the difference between "auto lets a build run" and "auto stops at a
        // build", and neither reads as an error at compile time.
        var table = new (string Mode, ToolRisk Risk, bool Expected)[]
        {
            (ToolApprovalModes.Ask, ToolRisk.ReadOnly, false),
            (ToolApprovalModes.Ask, ToolRisk.WorkspaceWrite, true),
            (ToolApprovalModes.Ask, ToolRisk.WorkspaceCommand, true),
            (ToolApprovalModes.Ask, ToolRisk.SystemCommand, true),
            (ToolApprovalModes.Auto, ToolRisk.ReadOnly, false),
            (ToolApprovalModes.Auto, ToolRisk.WorkspaceWrite, false),
            // The line the whole complaint was about: a command inside the session's own sandbox is what "自动审批"
            // promises to run without a click, while the tier above it — the screen, another session, a directory
            // nobody chose — still asks.
            (ToolApprovalModes.Auto, ToolRisk.WorkspaceCommand, false),
            // …and the other half of that same complaint, ruled later and answered the same way: a git write that stays
            // in the repository — add, commit — is ordinary work too, so 自动审批 runs it unannounced while 询问审批 still
            // asks. The strict-tier row is the one that keeps it honest: drop it and "the mode lets a commit through"
            // would be indistinguishable from a gate that stopped asking about git at all.
            (ToolApprovalModes.Ask, ToolRisk.RepositoryWrite, true),
            (ToolApprovalModes.Auto, ToolRisk.RepositoryWrite, false),
            (ToolApprovalModes.Full, ToolRisk.RepositoryWrite, false),
            (ToolApprovalModes.Auto, ToolRisk.SystemCommand, true),
            (ToolApprovalModes.Full, ToolRisk.ReadOnly, false),
            (ToolApprovalModes.Full, ToolRisk.WorkspaceWrite, false),
            (ToolApprovalModes.Full, ToolRisk.WorkspaceCommand, false),
            (ToolApprovalModes.Full, ToolRisk.SystemCommand, false),
            // The assistant's own notes never ask, in any mode: a note that costs a card is a note never written.
            (ToolApprovalModes.Ask, ToolRisk.AssistantNote, false),
            (ToolApprovalModes.Auto, ToolRisk.AssistantNote, false),
            (ToolApprovalModes.Full, ToolRisk.AssistantNote, false),
            // And the mirror image: a question stops the run in every mode, including the one whose whole promise is
            // that nothing asks. These three are the only thing standing between "full access means answer for me"
            // and the tier being read before the mode switch — put the arm below the switch and the table still
            // compiles, every other cell still passes, and questions simply stop existing.
            (ToolApprovalModes.Ask, ToolRisk.AskUser, true),
            (ToolApprovalModes.Auto, ToolRisk.AskUser, true),
            (ToolApprovalModes.Full, ToolRisk.AskUser, true),
            // The cache-break rows are the same shape as the question rows for a different reason, and they are the
            // whole point of this tier: removing what a build already wrote asks in every mode. Moved the arm inside the
            // switch and only the full-access cell goes red — which is exactly the bug the rule exists for (a client
            // clearing a build directory on its own, mid-loop, because nothing was allowed to stop it), so this cell is
            // the one that has to stay.
            (ToolApprovalModes.Ask, ToolRisk.CacheBreak, true),
            (ToolApprovalModes.Auto, ToolRisk.CacheBreak, true),
            (ToolApprovalModes.Full, ToolRisk.CacheBreak, true),
        };
        foreach (var(mode, risk, expected)in table)
        {
            if (ToolApprovalPolicy.RequiresApproval(mode, risk) != expected)
                throw new Exception($"Approval decision for {mode} × {risk} was wrong.");
        }

        Console.WriteLine("PASS: the approval decision table asks exactly where it should.");
        // Which tiers a 「总是允许」 grant cannot open, as its own table: the gate and the card's button read this one
        // predicate now, so the failure this guards against — a button that parks a call the gate would keep parking —
        // cannot come back by editing one side. Three tiers, three reasons, and a new tier defaults to grantable, so
        // forgetting to name it here is a visible omission rather than a silent one.
        var grantable = new (ToolRisk Risk, bool Expected)[]
        {
            (ToolRisk.ReadOnly, true),
            (ToolRisk.WorkspaceWrite, true),
            // Building sits here, not above: a grant that stops asking about `run_command` was always going to cover a
            // compile, and that is the ruling rather than an oversight — the loop is the work.
            (ToolRisk.WorkspaceCommand, true),
            // The git write that stays in the repository is here for the same reason, one rung over: a person who wants a
            // commit to stop asking in 询问审批 as well gets that from a grant, not from the mode table.
            (ToolRisk.RepositoryWrite, true),
            (ToolRisk.AssistantNote, true),
            (ToolRisk.SystemCommand, false),
            (ToolRisk.AskUser, false),
            (ToolRisk.CacheBreak, false),
        };
        foreach (var(risk, expected)in grantable)
        {
            if (ToolApprovalPolicy.IsGrantable(risk) != expected)
                throw new Exception($"A grant {(expected ? "should" : "must never")} open {risk}, and it does not agree.");
        }

        Console.WriteLine("PASS: no grant opens the reach tier, the question tier or the cache-break tier, and every " + "other tier — building included — stays grantable.");
        // What a command line has to look like before Hub asks the person awake about it. Asserted as a table because
        // the shape of the failure is a miss in both directions. The negatives are rows too, and so is every pair that
        // separates the two answers: a compile into the directory (`BuildRun`) and a removal of what is already in it
        // (`CacheBreak`) are graded apart on purpose, because only the second one outranks an approval mode.
        var buildCases = new (string Command, BuildGrade Expected)[]
        {
            // The engine's own spellings of "compile it".
            ("cmake --build build", BuildGrade.BuildRun),
            ("cmake --build build --config Debug", BuildGrade.BuildRun),
            ("cmake -B build", BuildGrade.BuildRun),
            ("cmake -S samples/cpp -B build", BuildGrade.BuildRun),
            ("cmake --fresh -B build", BuildGrade.BuildRun),
            ("cmake --build --preset dev", BuildGrade.BuildRun),
            ("msbuild app.vcxproj", BuildGrade.BuildRun),
            ("ninja -C build", BuildGrade.BuildRun),
            ("make -j8", BuildGrade.BuildRun),
            ("xcodebuild -project App.xcodeproj", BuildGrade.BuildRun),
            ("ndk-build APP_ABI=arm64-v8a", BuildGrade.BuildRun),
            ("gradle assembleRelease", BuildGrade.BuildRun),
            ("dotnet build src/AxmolHub/AxmolHub.csproj", BuildGrade.BuildRun),
            ("dotnet msbuild app.csproj", BuildGrade.BuildRun),
            ("dotnet publish -c Release", BuildGrade.BuildRun),
            ("dotnet test", BuildGrade.BuildRun),
            ("dotnet run --project src/AxmolHub", BuildGrade.BuildRun),
            ("dotnet restore", BuildGrade.BuildRun),
            ("dotnet --nologo build app.csproj", BuildGrade.BuildRun),
            // The engine's setup script, started directly and started through a shell.
            ("./setup.ps1 -android", BuildGrade.BuildRun),
            ("setup.sh android", BuildGrade.BuildRun),
            ("pwsh -File setup.ps1 -android", BuildGrade.BuildRun),
            // The destructive half: what a person means by "it rebuilt from scratch", in every shell's phrasing. These
            // are the rows the tier is built from, so a `clean` target has to be read as the removal it is even when the
            // same line also compiles.
            ("cmake --build build --target clean", BuildGrade.CacheBreak),
            ("cmake -UCACHE_option", BuildGrade.CacheBreak),
            ("mingw32-make clean", BuildGrade.CacheBreak),
            ("./gradlew.bat clean assembleRelease", BuildGrade.CacheBreak),
            ("dotnet clean", BuildGrade.CacheBreak),
            ("git clean -fdx", BuildGrade.CacheBreak),
            ("git -C repo clean -fdx", BuildGrade.CacheBreak),
            ("rm -rf build", BuildGrade.CacheBreak),
            ("rm -rf ./build/", BuildGrade.CacheBreak),
            ("rm -rf build-windows out .cxx", BuildGrade.CacheBreak),
            ("rm CMakeCache.txt", BuildGrade.CacheBreak),
            ("Remove-Item -Recurse -Force build", BuildGrade.CacheBreak),
            ("rd /s /q build", BuildGrade.CacheBreak),
            ("del build\\CMakeCache.txt", BuildGrade.CacheBreak),
            // Wrapped, mixed and prefixed: the most destructive segment decides the line, and a wrapper's own arguments
            // do not hide it. This is the same route the git ladder closes, and one shared tokenizer is what lets both
            // see it — two tokenizers would let `cmake --build build && git status` be one classifier's business only.
            ("pwsh -c \"cmake --build build\"", BuildGrade.BuildRun),
            ("cmd /c \"dotnet test\"", BuildGrade.BuildRun),
            ("sh -c \"make && git status\"", BuildGrade.BuildRun),
            ("git status && cmake --build build", BuildGrade.BuildRun),
            ("cmake --build build && git status", BuildGrade.BuildRun),
            ("timeout 300 cmake --build build", BuildGrade.BuildRun),
            ("env -i LC_ALL=C ninja", BuildGrade.BuildRun),
            ("$env:X=1; cmake --build build", BuildGrade.BuildRun),
            // The mixed line graded by its worst half — the reason a compile beside a deletion cannot be waved through
            // as "just a build".
            ("cmake --build build && rm -rf build", BuildGrade.CacheBreak),
            // Not build work at all. A read that names the build directory, a file that merely sits under it, git's own
            // reads and writes, and a dotnet verb that touches nothing.
            ("git status", BuildGrade.Nothing),
            ("git diff HEAD", BuildGrade.Nothing),
            ("git commit -m x", BuildGrade.Nothing),
            ("ls build", BuildGrade.Nothing),
            ("ls -la build/", BuildGrade.Nothing),
            ("cat build/x.log", BuildGrade.Nothing),
            ("Get-Content build/output.log", BuildGrade.Nothing),
            ("cmake --version", BuildGrade.Nothing),
            ("cmake --help", BuildGrade.Nothing),
            ("cmake", BuildGrade.Nothing),
            ("dotnet --info", BuildGrade.Nothing),
            ("dotnet ef database update", BuildGrade.Nothing),
            ("rm src/main.cpp", BuildGrade.Nothing),
            ("rm notes.md", BuildGrade.Nothing),
            ("mkdir build", BuildGrade.Nothing),
            ("echo cmake --build build", BuildGrade.Nothing),
            // A word that only looks like a cache directory: matched by name, not by substring.
            ("rm rebuild-notes.md", BuildGrade.Nothing),
            ("rm -rf ~/Documents", BuildGrade.Nothing),
            // Nothing to grade.
            ("", BuildGrade.Nothing),
            ("   ", BuildGrade.Nothing),
        };
        foreach (var(command, expected)in buildCases)
        {
            var actual = BuildCommandGrades.Grade(command);
            if (actual != expected)
                throw new Exception($"「{command}」 graded as {actual}, expected {expected}.");
        }

        // The argument wrapper the gate actually calls, including the shapes that must keep the tier the call had.
        if (BuildCommandGrades.CommandOf(null) != BuildGrade.Nothing || BuildCommandGrades.CommandOf("{}") != BuildGrade.Nothing || BuildCommandGrades.CommandOf("not json") != BuildGrade.Nothing || BuildCommandGrades.CommandOf("{\"other\":1}") != BuildGrade.Nothing || BuildCommandGrades.CommandOf("{\"command\":\"\"}") != BuildGrade.Nothing)
            throw new Exception("An unreadable or unrelated argument read as build work.");
        if (BuildCommandGrades.CommandOf("{\"command\":\"cmake --build build\",\"timeout_seconds\":60}") != BuildGrade.BuildRun || BuildCommandGrades.CommandOf("{\"command\":\"rm -rf build\"}") != BuildGrade.CacheBreak || BuildCommandGrades.CommandOf("{\"command\":\"git status\"}") != BuildGrade.Nothing)
            throw new Exception("The command-argument reader lost the distinction the tier is built from.");
        Console.WriteLine($"PASS: all {buildCases.Length} command lines answer both questions this classifier is asked —" + " does it compile, and is that different from throwing away what already compiled.");
        // Fail closed on anything unrecognized, including null: the settings file is user-editable, and a newer
        // Hub may write a mode this build has never heard of.
        foreach (var unknown in new[]
        {
            null,
            "",
            "yolo",
            "ASK",
            "auto-approve"
        }

        )
        {
            if (ToolApprovalModes.Normalize(unknown) != ToolApprovalModes.Ask)
                throw new Exception($"Unrecognized approval mode 「{unknown}」 did not fall back to ask.");
        }

        if (!ToolApprovalPolicy.RequiresApproval(null, ToolRisk.WorkspaceWrite))
            throw new Exception("A null approval mode let a workspace write through.");
        Console.WriteLine("PASS: an unrecognized approval mode falls back to asking.");
        // The app-wide trust list is a field in a file the user can open with a text editor, so it is asserted as one:
        // what a round trip keeps, what the reader folds away, and what a hand-edited value cannot do. Two different
        // comparisons live in it and both are deliberate — duplicates fold case-insensitively on the way in, while a
        // grant matches a call by the wire spelling exactly, because a model emits `file_write` and not `FILE_WRITE`.
        var trustPath = Path.Combine(root, "hub-settings-trust.json");
        var trustStore = new PreferencesStore(trustPath);
        var trusting = new HubPreferences
        {
            ToolApprovalMode = ToolApprovalModes.Auto
        };
        trusting.TrustedTools.AddRange(["run_command", " run_command ", "", "FS_WRITE", "run_command"]);
        trustStore.Save(trusting);
        var trusted = trustStore.Load();
        // Five names written, two left: the padded and repeated spellings of `run_command` fold into one, the blank
        // goes, and `FS_WRITE` survives as its own entry — because the dedupe is forgiving while the match is not.
        if (trusted.TrustedTools.Count != 2 || !ToolTrust.Contains(trusted.TrustedTools, "run_command") || !ToolTrust.Contains(trusted.TrustedTools, "FS_WRITE") || ToolTrust.Contains(trusted.TrustedTools, "fs_write") || ToolTrust.Contains(trusted.TrustedTools, " "))
            throw new Exception($"The trust list did not round-trip as written: [{string.Join(",", trusted.TrustedTools)}]");
        // A name this build has never heard is kept rather than dropped: the app's table decides what matches a call,
        // and a reader that "cleaned" the list would silently revoke a grant a newer Hub wrote down.
        if (!ToolTrust.Add(trusted.TrustedTools, "tool_from_a_newer_hub"))
            throw new Exception("A grant for a name this build does not know was refused.");
        trustStore.Save(trusted);
        if (!ToolTrust.Contains(trustStore.Load().TrustedTools, "tool_from_a_newer_hub"))
            throw new Exception("The unknown grant never reached the file.");
        var flooded = new HubPreferences();
        for (var index = 0; index < ToolTrust.Limit * 3; index++)
            ToolTrust.Add(flooded.TrustedTools, $"tool_{index}");
        flooded.TrustedTools.Add("");
        trustStore.Save(flooded);
        if (trustStore.Load().TrustedTools.Count > ToolTrust.Limit)
            throw new Exception($"The trust list grew past its bound: {trustStore.Load().TrustedTools.Count}.");
        if (ToolTrust.Add(new List<string>(["file_write"]), "file_write") || ToolTrust.Add([], ""))
            throw new Exception("A grant was recorded twice, or an empty name was accepted as one.");
        File.Delete(trustPath);
        Console.WriteLine("PASS: the app-wide trust list round-trips, folds blanks and duplicates, and is bounded.");
        // The per-session override rides on the session file, and a file written before approval existed must
        // still load — with the pending count reading as zero rather than throwing.
        var policyStore = new ConversationStore(root);
        // The simulated workspace must not be inside the protected data root the store owns.
        var workspaceRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "tool-policy-workspace");
        Directory.CreateDirectory(workspaceRoot);
        var policyConversation = Conversation.Create("orcarouter");
        policyConversation.Append(ChatTurn.User("写一个文件"));
        policyConversation.ApprovalMode = ToolApprovalModes.Auto;
        policyConversation.WorkspaceRoot = workspaceRoot;
        policyConversation.AutoApprovedTools.Add("fs_replace");
        policyConversation.Messages.Add(ChatTurn.FunctionCall("c1", "fs_replace", """{"edits":[]}""")with { ApprovalState = ChatApprovalStates.Pending, ApprovalPreview = "--- a/x.cpp\n+++ b/x.cpp\n+hi", });
        policyStore.Save(policyConversation);
        var policyReloaded = policyStore.Load(policyConversation.Id) ?? throw new Exception("Session with approval settings did not reload.");
        if (policyReloaded.ApprovalMode != ToolApprovalModes.Auto || policyReloaded.WorkspaceRoot != workspaceRoot || policyReloaded.AutoApprovedTools.Count != 1 || policyReloaded.AutoApprovedTools[0] != "fs_replace")
            throw new Exception("The session's approval settings did not round-trip.");
        var pendingCall = policyReloaded.Messages[^1];
        if (pendingCall.ApprovalState != ChatApprovalStates.Pending || pendingCall.ApprovalPreview is not { Length: > 0 } || pendingCall.ToolName != "fs_replace")
            throw new Exception("A pending tool call lost its state, its preview, or its name.");
        var pendingSummary = policyStore.List().First(summary => summary.Id == policyConversation.Id);
        if (pendingSummary.PendingApprovals != 1)
            throw new Exception($"The index did not report the pending approval (reported {pendingSummary.PendingApprovals}).");
        File.WriteAllText(Path.Combine(root, "ai", "sessions", "legacy-no-approval.json"), """{"Id":"legacy-no-approval","Title":"legacy","ProviderId":"orcarouter","Messages":[{"Role":"assistant","Text":"","ToolCallId":"c1","ToolName":"workspace_map"}]}""");
        if (policyStore.Load("legacy-no-approval")is not { ApprovalMode: null, WorkspaceRoot: null, AutoApprovedTools: { Count: 0 } } legacy)
            throw new Exception("A session file predating tool approval no longer loads.");
        if (legacy.Messages[0].ApprovalState is not null || legacy.Messages[0].ApprovalPreview is not null)
            throw new Exception("A tool call from before approval gained a state.");
        // The file was written by hand, so it is not in the index — what matters is that summarizing it, which is
        // what the index is built from, reports no pending decision rather than throwing.
        if (ConversationSummary.From(legacy).PendingApprovals != 0)
            throw new Exception("A legacy session advertised pending approvals it cannot have.");
        // A resolved call stops counting as pending, which is what keeps the badge from outliving the decision.
        var resolved = policyStore.Load(policyConversation.Id)!;
        resolved.Messages[^1] = resolved.Messages[^1] with
        {
            ApprovalState = ChatApprovalStates.Approved
        };
        policyStore.Save(resolved);
        if (policyStore.List().First(summary => summary.Id == policyConversation.Id).PendingApprovals != 0)
            throw new Exception("An approved call was still counted as pending in the index.");
        // Which tier a call belonged to is not on disk anywhere, and `run_command` is exactly the tool whose tier moved
        // in this build: a command someone approved under the old table has to read back as approved. A build that
        // re-derived a decision from a tool name would silently re-open every one of those the day a tier changed,
        // and ask a person to answer a question they already answered.
        resolved.Messages.Add(ChatTurn.FunctionCall("c9", "run_command", """{"command":"cmake --build build"}""")with { ApprovalState = ChatApprovalStates.Approved, });
        resolved.Messages.Add(ChatTurn.FunctionResult("c9", "exit: 0"));
        policyStore.Save(resolved);
        var retiered = policyStore.Load(policyConversation.Id) ?? throw new Exception("A session with an approved command did not reload.");
        if (retiered.Messages[^2].ToolName != "run_command" || retiered.Messages[^2].ApprovalState != ChatApprovalStates.Approved)
            throw new Exception("An approved run_command lost its recorded decision when its tier moved.");
        if (policyStore.List().First(summary => summary.Id == policyConversation.Id).PendingApprovals != 0)
            throw new Exception("A re-tiered call came back as a decision still owed.");
        Console.WriteLine("PASS: a call's recorded decision outlives the tier that decided it, because the tier is not stored.");
        policyStore.Delete("legacy-no-approval");
        policyStore.Delete(policyConversation.Id);
        Console.WriteLine("PASS: approval fields round-trip, pending counts derive, and pre-approval sessions still load.");
        // The three answers a gate can give are asserted here, at the pipeline, rather than through a screen: this
        // is the layer where "the call did not run" is decided, and a UI built later can only be as correct as it is.
        var gateProvider = new ModelProvider
        {
            Id = "orcarouter",
            Name = "OrcaRouter",
            BaseUrl = "https://api.orcarouter.ai/v1"
        };
        // A function that objects to being run: every gate answer other than Allow has to keep it from executing.
        var gatedFunction = AIFunctionFactory.Create((Func<string>)(() => throw new Exception("A gated tool ran without being allowed.")), new AIFunctionFactoryOptions { Name = "project_list", Description = "Return registered projects.", });
        // Deny: the refusal has to reach the model, or the model asks again.
        var order = new List<string>();
        var denyCompleted = 0;
        var denyResult = "";
        var denyFailed = false;
        var denyClient = new ToolLoopChatClient(callName: "project_list");
        var denyAnswer = new StringBuilder();
        await foreach (var chunk in new ChatPipeline(denyClient).SendAsync(gateProvider, [ChatTurn.User("write a file")], tools: [gatedFunction], gate: (info, _, _) =>
        {
            order.Add("gate:" + info.Name);
            return Task.FromResult(ChatPipeline.ToolGateOutcome.Deny);
        }, onToolStarted: info =>
        {
            order.Add("started:" + info.Name);
            return Task.CompletedTask;
        }, onToolCompleted: (_, result, failed) =>
        {
            denyCompleted++;
            denyResult = result;
            denyFailed = failed;
            return Task.CompletedTask;
        }))
            denyAnswer.Append(chunk);
        // The call is recorded before the gate is asked, which is what lets a pending call be marked after the fact
        // instead of needing a side table of its own.
        if (string.Join(",", order) != "started:project_list,gate:project_list")
            throw new Exception($"The pipeline asked the gate in the wrong order: {string.Join(",", order)}.");
        if (denyCompleted != 1 || denyFailed != true || denyResult != ToolApprovalResults.Denied)
            throw new Exception($"A refused call was not reported to the model as a failure: 「{denyResult}」 (failed={denyFailed}).");
        if (denyClient.CallCount != 2 || denyAnswer.ToString() != "Projects loaded.")
            throw new Exception("A refusal stopped the reply loop instead of letting the model answer about it.");
        if (!denyClient.SecondRequest.Any(message => message.Contents.Any(content => content is FunctionResultContent result && result.Result as string == ToolApprovalResults.Denied)))
            throw new Exception("The refusal the model was sent is not the refusal it was given.");
        Console.WriteLine("PASS: a denied call never runs, and the model is told it was refused.");
        // A response that asks for a tool this request never declared. The invoking client refuses to run a name it
        // was not given and ends the loop there, so nothing is gated and nothing reaches the transcript — and until
        // the iterator started reporting the calls it saw, the only trace was a reply carrying reasoning and no text.
        // That is the ending a person met and a log could not explain: reasoning, then 「模型只返回了思考过程」.
        var undeclaredClient = new ToolLoopChatClient(callName: "web_search");
        var undeclaredSeen = new List<string>();
        var undeclaredGated = 0;
        var undeclaredAnswer = new StringBuilder();
        await foreach (var chunk in new ChatPipeline(undeclaredClient).SendAsync(gateProvider, [ChatTurn.User("search the web for it")], tools: [gatedFunction], gate: (_, _, _) =>
        {
            undeclaredGated++;
            return Task.FromResult(ChatPipeline.ToolGateOutcome.Allow);
        }, onCallContent: name => undeclaredSeen.Add(name)))
            undeclaredAnswer.Append(chunk);
        if (string.Join(",", undeclaredSeen) != "web_search")
            throw new Exception($"A call the request never declared did not reach the caller: [{string.Join(",", undeclaredSeen)}].");
        if (undeclaredGated != 0)
            throw new Exception($"A call the request never declared reached the gate ({undeclaredGated} time(s)).");
        if (undeclaredAnswer.Length != 0)
            throw new Exception($"An undeclared call was answered instead of ending the turn: 「{undeclaredAnswer}」.");
        Console.WriteLine("PASS: a call the request never declared is reported to the caller and never reaches the gate.");
        // And whether the same report fires for a call the client *does* run decides how the log line is read: a name
        // there means "the response asked", and only the gate count beside it says whether anything acted on it. The
        // answer is a property of the shipped Microsoft.Extensions.AI assembly, so it is measured here rather than
        // assumed — the assertion allows both, and the printed line records which one this lock file gives.
        var declaredClient = new ToolLoopChatClient(callName: "project_list");
        var declaredSeen = new List<string>();
        var declaredGated = 0;
        var declaredAnswer = new StringBuilder();
        await foreach (var chunk in new ChatPipeline(declaredClient).SendAsync(gateProvider, [ChatTurn.User("list the projects")], tools: [gatedFunction], gate: (_, _, _) =>
        {
            declaredGated++;
            return Task.FromResult(ChatPipeline.ToolGateOutcome.Allow);
        }, onCallContent: name => declaredSeen.Add(name)))
            declaredAnswer.Append(chunk);
        if (declaredGated != 1 || declaredAnswer.ToString() != "Projects loaded.")
            throw new Exception($"A declared call stopped running: gated={declaredGated}, answer=「{declaredAnswer}」.");
        if (declaredSeen.Count > 1)
            throw new Exception($"One call of one response was reported {declaredSeen.Count} times: " + $"[{string.Join(",", declaredSeen)}].");
        Console.WriteLine("PASS: a call the client runs is reported " + $"{(declaredSeen.Count == 0 ? "not at all — this report means \"nothing ran it\"" : "once, so the gate count beside it is what says whether it acted")}.");
        // An endpoint that reports a failure inside the stream, before it has produced anything. The turn then has nothing
        // to lose and everything to gain from the provider's own sentence reaching the person: that is the difference
        // between "the server is overloaded, retry" and "the model returned nothing, check your configuration" — the
        // second of which is what an overloaded gpt-6-sol told a person on 2026-10-21 while the wire said the first.
        var providerSaid = "Our servers are currently overloaded. Please try again later.";
        string? reachedCaller = null;
        try
        {
            await foreach (var _ in new ChatPipeline(new ErrorStreamChatClient(providerSaid)).SendAsync(gateProvider, [ChatTurn.User("hello")], tools: [gatedFunction]))
            {
            }
        }
        catch (InvalidOperationException ex)
        {
            reachedCaller = ex.Message;
        }

        if (reachedCaller != providerSaid)
            throw new Exception("An in-stream provider error did not reach the caller as its own sentence: " + $"「{reachedCaller ?? "nothing was thrown"}」");
        if (!ProviderFailure.ProviderRefused(new ProviderResponseException(providerSaid))
            || ProviderFailure.ProviderRefused(new InvalidOperationException(providerSaid))
            || ProviderFailure.ProviderRefused(new ProviderResponseException("Account unavailable.", "insufficient_quota"))
            || !ProviderFailure.AuthenticationFailed(new ProviderResponseException("Account unavailable.", "invalid_api_key")))
            throw new Exception("Provider stream failures lost their origin/code or local failures were retried.");
        Console.WriteLine("PASS: an in-stream provider error reaches the caller as the provider's own sentence.");
        // Pending: the stream ends, nothing is written as the call's result, and the placeholder stays inside.
        using var parkCancellation = new CancellationTokenSource();
        var parkCompleted = 0;
        var parkClient = new ToolLoopChatClient(callName: "project_list", honorCancellation: true);
        var parkAnswer = new StringBuilder();
        var parkThrew = false;
        try
        {
            await foreach (var chunk in new ChatPipeline(parkClient).SendAsync(gateProvider, [ChatTurn.User("write a file")], tools: [gatedFunction], gate: (_, _, _) =>
            {
                parkCancellation.Cancel();
                return Task.FromResult(ChatPipeline.ToolGateOutcome.Pending);
            }, onToolCompleted: (_, _, _) =>
            {
                parkCompleted++;
                return Task.CompletedTask;
            }, cancellationToken: parkCancellation.Token))
                parkAnswer.Append(chunk);
        }
        catch (OperationCanceledException)
        {
            parkThrew = true;
        }

        // Whether the turn ends by cancellation or by the stream simply stopping is the pipeline's choice; either is
        // how the caller learns to wait, and ChatWorkspace reads the run's phase rather than the exception for that.
        if (parkAnswer.Length > 0)
            throw new Exception($"Parking let the reply keep going (threw={parkThrew}): 「{parkAnswer}」.");
        if (parkCompleted != 0)
            throw new Exception("A parked call was written a tool result, which is a decision nobody made.");
        if (parkClient.SecondRequest.Count > 0)
            throw new Exception("The follow-up request went out with the approval placeholder: " + parkClient.SecondRequest.Count + " messages.");
        Console.WriteLine("PASS: a parked call ends the turn with no result, and its placeholder never reaches the model.");
        // Which calls of one response may be in flight together is decided by the tier — and every named row here is
        // why this is a table instead of a sample: the tier answers for a whole class, and a tool whose reach does not
        // follow from its verb has to be refused by name or the class quietly covers it.
        var concurrentTable = new (string Name, ToolRisk Risk, bool Expected)[]
        {
            ("read_file", ToolRisk.ReadOnly, true),
            ("search_text", ToolRisk.ReadOnly, true),
            ("find_files", ToolRisk.ReadOnly, true),
            ("list_directory", ToolRisk.ReadOnly, true),
            ("workspace_map", ToolRisk.ReadOnly, true),
            ("memory_read", ToolRisk.ReadOnly, true),
            ("project_list", ToolRisk.ReadOnly, true),
            ("engine_list", ToolRisk.ReadOnly, true),
            ("toolchain_status", ToolRisk.ReadOnly, true),
            ("list_sessions", ToolRisk.ReadOnly, true),
            // A grant grows the list of directories the session holds, so a read started beside it would be graded
            // against a sandbox that stops being the one it runs in. Not a read-only tier at all — the card is the
            // mechanism — and listed here so the concurrency question is answered rather than left to the default.
            ("request_directory", ToolRisk.SystemCommand, false),
            ("file_write", ToolRisk.WorkspaceWrite, false),
            ("send_to_session", ToolRisk.WorkspaceWrite, false),
            ("memory_write", ToolRisk.AssistantNote, false),
            // Out on purpose, not out by omission: a model-typed `git status` is the read this row keeps refusing, and
            // it stays out because Hub's own git reads have to pass --no-optional-locks to avoid taking index.lock.
            // The shell suite asserts the tier and the membership as one line, next to the classifier.
            ("run_command", ToolRisk.WorkspaceCommand, false),
            ("web_fetch", ToolRisk.WorkspaceCommand, false),
            ("capture_screen", ToolRisk.SystemCommand, false),
            ("spawn_session", ToolRisk.SystemCommand, false),
            ("ask_user", ToolRisk.AskUser, false),
        };
        foreach (var cell in concurrentTable.Where(cell => ToolConcurrency.IsSafe(cell.Name, cell.Risk) != cell.Expected))
            throw new Exception($"The concurrency class disagreed with the tier over 「{cell.Name}」 ({cell.Risk}).");
        // The count is part of the assertion: a table where every row flipped to true would still satisfy the rows
        // above, and what it would mean is that writes started running beside reads.
        if (concurrentTable.Count(cell => cell.Expected) != 10)
            throw new Exception($"The concurrency class holds {concurrentTable.Count(cell => cell.Expected)} tools, " + "which is not the ten reads it was drawn to cover.");
        Console.WriteLine("PASS: the read-only tier may start beside its siblings, and nothing above that tier does.");
        // The proof that "may start beside" is not only a decision on paper. Two safe calls are put in one response and
        // each refuses to finish until <b>both</b> are inside: a serial executor deadlocks on that barrier and the cell
        // fails on its own. The unsafe call reports whether anything was in flight at its own start, because a batch
        // that ran the write concurrently would still pass a test that only measures the reads — and results are read
        // back off the follow-up request, so the order the model was shown is asserted rather than assumed.
        var inside = 0;
        var bothInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeSawCompany = false;
        AIFunction ReadFunction(string name) => AIFunctionFactory.Create(async () =>
        {
            if (System.Threading.Interlocked.Increment(ref inside) >= 2)
                bothInside.TrySetResult();
            var finished = await Task.WhenAny(bothInside.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            System.Threading.Interlocked.Decrement(ref inside);
            if (finished != bothInside.Task)
                throw new Exception("two safe calls of one response were never in flight together");
            return name + " read";
        }, new AIFunctionFactoryOptions { Name = name });
        var gatedReadA = ReadFunction("read_one");
        var gatedReadB = ReadFunction("read_two");
        var gatedWrite = AIFunctionFactory.Create((Func<string>)(() =>
        {
            writeSawCompany = System.Threading.Volatile.Read(ref inside) > 0;
            return "written";
        }), new AIFunctionFactoryOptions { Name = "file_write" });
        var overlapClient = new MultiCallChatClient("read_one", "read_two", "file_write");
        var overlapAnswer = new StringBuilder();
        await foreach (var chunk in new ChatPipeline(overlapClient).SendAsync(gateProvider, [ChatTurn.User("读两遍再写一个")], tools: [gatedReadA, gatedReadB, gatedWrite], concurrencySafe: info => info.Name != "file_write"))
            overlapAnswer.Append(chunk);
        if (writeSawCompany)
            throw new Exception("The call outside the concurrency class started while a sibling was still in flight.");
        if (overlapAnswer.ToString() != "都读完了。")
            throw new Exception($"The batch did not end with the model answering once: 「{overlapAnswer}」.");
        var delivered = overlapClient.SecondRequest.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId + "=" + (result.Result as string)).ToList();
        if (string.Join(" / ", delivered) != "m0=read_one read / m1=read_two read / m2=written")
            throw new Exception("The answers the model was shown are not the calls in the order it asked: " + string.Join(" / ", delivered) + ".");
        Console.WriteLine("PASS: two safe calls of one response really do overlap, the third starts alone, and the " + "answers reach the model in the order the calls were asked.");
        // Git is not a tool, so the rule that decides whether `run_command` may touch a repository is a pure function
        // over the command line — and it is asserted as a table rather than sampled. The ladder only goes up: every
        // case that is not a named git write has to keep the tier it had, because a classifier that stops a build loop
        // at `git status` is the failure mode this feature would cause, and that half of the rule is invisible in a
        // sample of the interesting cases.
        var gitCases = new (string Command, GitGrade Expected)[]
        {
            // Reads, in the spellings the assistant actually emits: global options before the verb, a value-taking
            // `-c`, the no-pager prefix, a redirect after the arguments.
            ("git status", GitGrade.GitRead),
            ("git status --porcelain=v1 -z --untracked-files=all", GitGrade.GitRead),
            ("git diff", GitGrade.GitRead),
            ("git diff HEAD~3 -- src/main.cpp", GitGrade.GitRead),
            ("git --no-pager diff --stat", GitGrade.GitRead),
            ("git -c core.quotepath=false log -1", GitGrade.GitRead),
            ("git --git-dir=.git rev-parse HEAD", GitGrade.GitRead),
            ("git -C sub status", GitGrade.GitRead),
            ("git log --oneline -20", GitGrade.GitRead),
            ("git show HEAD:README.md", GitGrade.GitRead),
            ("git blame src/main.cpp", GitGrade.GitRead),
            ("git grep TODO", GitGrade.GitRead),
            ("git ls-files", GitGrade.GitRead),
            ("git reflog", GitGrade.GitRead),
            // The five verbs that are reads or writes depending on their arguments.
            ("git branch", GitGrade.GitRead),
            ("git branch -a", GitGrade.GitRead),
            ("git branch --show-current", GitGrade.GitRead),
            ("git tag", GitGrade.GitRead),
            ("git tag -l 'v1*'", GitGrade.GitRead),
            ("git remote -v", GitGrade.GitRead),
            ("git remote show origin", GitGrade.GitRead),
            ("git config user.name", GitGrade.GitRead),
            ("git config --list", GitGrade.GitRead),
            ("git stash list", GitGrade.GitRead),
            ("git worktree list", GitGrade.GitRead),
            // Writes: the index, the tree, a ref, or history.
            ("git add -A", GitGrade.GitWrite),
            ("git commit -m \"fix: the title\"", GitGrade.GitWrite),
            ("git commit --amend --no-edit", GitGrade.GitWrite),
            ("git push origin main", GitGrade.GitWrite),
            ("git pull", GitGrade.GitWrite),
            ("git fetch origin", GitGrade.GitWrite),
            ("git reset --hard HEAD~1", GitGrade.GitWrite),
            ("git checkout main", GitGrade.GitWrite),
            ("git switch -c feature", GitGrade.GitWrite),
            ("git restore src/main.cpp", GitGrade.GitWrite),
            ("git clean -fd", GitGrade.GitWrite),
            ("git merge feature", GitGrade.GitWrite),
            ("git rebase -i HEAD~3", GitGrade.GitWrite),
            ("git cherry-pick abc123", GitGrade.GitWrite),
            ("git revert HEAD", GitGrade.GitWrite),
            ("git rm -r build", GitGrade.GitWrite),
            ("git mv a.cpp b.cpp", GitGrade.GitWrite),
            ("git stash", GitGrade.GitWrite),
            ("git stash pop", GitGrade.GitWrite),
            ("git tag v1.0.0", GitGrade.GitWrite),
            ("git tag -d v0.9.0", GitGrade.GitWrite),
            ("git branch new-topic", GitGrade.GitWrite),
            ("git branch -d old-topic", GitGrade.GitWrite),
            ("git config user.email hi@example.com", GitGrade.GitWrite),
            ("git config --unset http.proxy", GitGrade.GitWrite),
            ("git remote set-url origin https://example.com/x.git", GitGrade.GitWrite),
            ("git worktree add ../side main", GitGrade.GitWrite),
            ("git submodule update --init", GitGrade.GitWrite),
            ("git init", GitGrade.GitWrite),
            ("git clone https://example.com/x.git", GitGrade.GitWrite),
            ("git gc --prune=now", GitGrade.GitWrite),
            // Compound lines, pipes and redirects: the worst segment wins, and a separator inside a quoted message
            // must not invent a segment of its own.
            ("git add -A && git commit -m x", GitGrade.GitWrite),
            ("cmake --build build && git commit -m x", GitGrade.GitWrite),
            ("git status; git log -1", GitGrade.GitRead),
            ("git log --oneline | head -5", GitGrade.GitRead),
            ("git diff HEAD > tmp/diff.txt", GitGrade.GitRead),
            ("git status 2>&1", GitGrade.GitRead),
            ("git commit -m \"a; b\" && git push", GitGrade.GitWrite),
            // A shell started for one command is that command: leaving `pwsh -c "git push"` on the read tier would be
            // the route around this whole rule, and it is the route a model finds.
            ("pwsh -c \"git push\"", GitGrade.GitWrite),
            ("powershell -Command \"git commit -m x\"", GitGrade.GitWrite),
            ("cmd /c git push", GitGrade.GitWrite),
            ("sh -c \"git reset --hard\"", GitGrade.GitWrite),
            ("env LC_ALL=C git commit -m x", GitGrade.GitWrite),
            ("sudo git commit -m x", GitGrade.GitWrite),
            ("timeout 60 git clone https://example.com/x.git", GitGrade.GitWrite),
            ("& git status", GitGrade.GitRead),
            // What is deliberately NOT graded upward, and said out loud because it is a blind spot rather than an
            // oversight: a payload in a variable and a script file are both unknowable without running something, and
            // every client's subcommand rules are blind the same way. `echo "git commit"` is the one that must stay
            // quiet, since the quoted pair is one argument to echo rather than a call.
            ("pwsh -c $cmd", GitGrade.NotGit),
            ("bash deploy.sh", GitGrade.NotGit),
            ("echo \"git commit\"", GitGrade.NotGit),
            // Ordinary work must not start costing cards because the word git appears near it.
            ("npm test", GitGrade.NotGit),
            ("cmake --build build --config Debug", GitGrade.NotGit),
            ("gitk", GitGrade.NotGit),
            ("github cli status", GitGrade.NotGit),
            ("git", GitGrade.NotGit),
            ("git --version", GitGrade.NotGit),
            // A verb this build has never heard of is not assumed harmless, and an option that swallowed its own
            // value is not assumed to be a read.
            ("git my-tool thing", GitGrade.GitUnclassifiable),
            ("git -C", GitGrade.GitUnclassifiable),
        };
        foreach (var(command, expected)in gitCases)
        {
            var actual = GitCommandGrades.Grade(command);
            if (actual != expected)
                throw new Exception($"Graded 「{command}」 as {actual}, expected {expected}.");
        }

        Console.WriteLine($"PASS: all {gitCases.Length} git command lines grade to the tier the ladder intends.");
        // The grade answers what a line does, and the table below answers what that costs — two questions, and this is
        // the assertion that keeps them apart. The predicate that used to sit here (`Elevates()`, "more than the sandbox
        // tier") answered both with one bool, and that is the collapse the local-write table exists to undo: a commit is
        // a git write that costs no card, and a table that could not say so had to send every write to the top tier.
        // Asserted on the pair that has to disagree, because agreeing is what a future edit would restore.
        if (GitCommandGrades.Grade("git commit -m x") != GitGrade.GitWrite || GitCommandGrades.Grade("git push origin main") != GitGrade.GitWrite || !GitCommandGrades.LocalWritesOnly(CommandArgs("git commit -m x")) || GitCommandGrades.LocalWritesOnly(CommandArgs("git push origin main")))
            throw new Exception("The grade stopped describing what a git line does, or the local-write table stopped " + "deciding what it costs.");
        // The gate hands the raw arguments in, so the wrapper is asserted with the rest: absent, blank, unparseable
        // and unrelated JSON all keep today's tier, which is what the approval-card fixtures and the audit lines in
        // the shell suite depend on. A card about a command Hub could not read would have nothing in it to approve,
        // and the tool body refuses an empty command before it can run anything.
        if (GitCommandGrades.CommandOf(null) != GitGrade.NotGit || GitCommandGrades.CommandOf("{}") != GitGrade.NotGit || GitCommandGrades.CommandOf("{\"command\":\"\"}") != GitGrade.NotGit || GitCommandGrades.CommandOf("not json at all") != GitGrade.NotGit || GitCommandGrades.CommandOf("{\"other\":1}") != GitGrade.NotGit || GitCommandGrades.CommandOf("{\"command\":\"git push\"}") != GitGrade.GitWrite || GitCommandGrades.CommandOf("{\"command\":\"git status\",\"timeout_seconds\":5}") != GitGrade.GitRead)
            throw new Exception("The command-argument reader graded a call it should have left on its old tier.");
        Console.WriteLine("PASS: a run_command with nothing readable in its arguments keeps the tier it had.");
        // The git write exemption: which git writes never cost a card, and under whose word. Two pure questions, so
        // they are asserted here rather than in the window — the tier they feed is a pure function of them too.
        static string CommandArgs(string command) => $"{{\"command\":{JsonSerializer.Serialize(command)}}}";
        if (!GitCommandGrades.LocalWriteVerbs.SequenceEqual(["add", "commit"]))
            throw new Exception("The local-write table changed. It is the whole list of git writes that run unattended " + "under 自动审批 — the one place a verb is judged to stay inside the repository and be put back from it — " + "so a change here is a ruling, not a detail. `add` and `commit` also have to stay in the same table: " + "split them and `git add -A && git commit -m x` straddles both and keeps asking.");
        // What a card may offer to stop asking about: the verbs a line spells itself, lowercase, in order, deduplicated.
        // Three lines report nothing on purpose — a verb this build cannot name, a read, and a write that only exists
        // behind a wrapper — because a card must never offer a key nothing honours. `pwsh -c "git push"` is graded as a
        // write and still is not the call the person read on a card.
        foreach (var(command, expected)in new (string, string[])[]
        {
            ("git add -A", ["add"]),
            ("git add -A && git add -p", ["add"]),
            ("git commit -m \"fix: the title\"", ["commit"]),
            ("git push origin main", ["push"]),
            ("git add -A && git commit -m x", ["add", "commit"]),
            ("git status && git log -1", []),
            ("git lfs install", []),
            ("git -C", []),
            ("pwsh -c \"git push\"", []),
            ("cmd /c git push", []),
            ("sudo git commit -m x", []),
            ("env LC_ALL=C git commit -m x", []),
            ("timeout 60 git clone https://example.com/x.git", []),
            ("npm test", []),
            ("not json at all", []),
        }

        )
        {
            var said = GitCommandGrades.WriteVerbsOf(CommandArgs(command)).OrderBy(verb => verb, StringComparer.Ordinal).ToArray();
            if (!said.SequenceEqual(expected.OrderBy(verb => verb, StringComparer.Ordinal)))
                throw new Exception($"WriteVerbsOf(「{command}」) said [{string.Join(", ", said)}], expected " + $"[{string.Join(", ", expected)}].");
        }

        Console.WriteLine("PASS: a card can only ever offer to trust a verb the line spells itself.");
        // What this build calls ordinary work, one row per cell the ruling named. The line everyone actually types
        // (`git add -A && git commit -m x`, two verbs from one table) is the cell the whole shape exists for, and the
        // verb reached through a shell is the one that must not sneak in beside it. The rest pin the edges: a read, a
        // non-git line and a verb this build cannot name are not local writes, and "at least one write" is what keeps a
        // bare build out of this answer and on the sandbox tier it already had.
        foreach (var(label, command, expected)in new (string, string, bool)[]
        {
            ("可撤销的索引操作是本地写", "git add -A", true),
            ("提交是本地写：仓库里已记录、能撤回来、不出这台机器", "git commit -m \"fix: the title\"", true),
            ("最常走的那条两动词的行来自同一张表", "git add -A && git commit -m x", true),
            ("同一张表里的多个写动词照旧放行", "git add -A && git add -p", true),
            ("未授权的写不是本地写", "git push", false),
            ("fetch 出这台机器，不在本地表里", "git fetch origin", false),
            ("重写历史不是本地写", "git rebase main", false),
            ("销毁未提交改动的写不是本地写", "git reset --hard", false),
            ("删除已构建的东西不是本地写", "git clean -fdx", false),
            ("认不出的子命令不是本地写", "git my-tool thing", false),
            ("读命令不是本地写", "git status", false),
            ("没有 git 的行不受影响", "npm test", false),
            ("穿透段里的 commit 不是直接写出来的动词", "pwsh -c \"git commit -m x\"", false),
            ("sudo 包着的 commit 也一样", "sudo git commit -m x", false),
        }

        )
        {
            var local = GitCommandGrades.LocalWritesOnly(CommandArgs(command));
            if (local != expected)
                throw new Exception($"{label}: LocalWritesOnly(「{command}」) said {local}, expected {expected}.");
        }

        // The same question with nothing readable in the arguments: a call the tool body will refuse before it runs
        // anything must not be classed as work this build trusts.
        if (GitCommandGrades.LocalWritesOnly(null) || GitCommandGrades.LocalWritesOnly("{}") || GitCommandGrades.LocalWritesOnly("not json at all"))
            throw new Exception("A run_command whose arguments name no command was read as a local git write.");
        Console.WriteLine("PASS: the local-write table is exactly the git writes that run unattended.");
        // The grant table, on the cells where it is the thing that decides: a grant names one verb and has to cover every
        // write in the line, which is what stops it from quietly widening a table it does not own. The straddle cell is
        // the one that matters — `git add && git push` with only `push` granted is covered by neither table, and the
        // union of the two is never taken.
        foreach (var(label, command, granted, expected)in new (string, string, string[], bool)[]
        {
            ("授权 push 后它落在沙箱档", "git push origin main", ["push"], true),
            ("两表并集也救不了跨表的行", "git add && git push", ["push"], false),
            ("本地的那半也授权了才算覆盖整行", "git add && git push", ["add", "push"], true),
            ("穿透段要自己命中授权", "pwsh -c \"git push\"", ["push"], false),
            ("sudo 包着的写也一样", "sudo git push", ["push"], false),
            ("认不出的子命令永不可授权", "git lfs install", ["lfs"], false),
            ("两个都授权的动词来自同一张表时放行", "git fetch origin && git push", ["fetch", "push"], true),
            ("读命令不需要任何表", "git status", [], true),
            ("没有 git 的行不受影响", "npm test", [], true),
            ("认不出的行也照旧要问", "git my-tool thing", ["my-tool"], false),
        }

        )
        {
            var covered = GitCommandGrades.AllWritesAllowed(CommandArgs(command), granted);
            if (covered != expected)
                throw new Exception($"{label}: AllWritesAllowed(「{command}」, [{string.Join(", ", granted)}]) said " + $"{covered}, expected {expected}.");
        }

        Console.WriteLine("PASS: a grant covers a line's git writes, and never the union with the local table.");
        // The stored shape of a grant, which is what the settings file holds and what the card's verb button writes: the
        // two words the person read, matched Ordinal, and an entry this build cannot read matching nothing rather than
        // everything. A hand-edited settings line has to fail toward asking.
        if (CommandTrust.KeyFor("PUSH") != "git push" || CommandTrust.KeyFor("  push ") != "git push" || CommandTrust.KeyFor("") != "" || CommandTrust.KeyFor(null) != "" || CommandTrust.VerbOf("git push") != "push" || CommandTrust.VerbOf("GIT PUSH") != "push" || CommandTrust.VerbOf("push") != "" || CommandTrust.VerbOf("rm -rf") != "" || CommandTrust.VerbOf("git ") != "" || CommandTrust.VerbOf("git push --force") != "" || !CommandTrust.Contains(["git push"], "git push") || CommandTrust.Contains(["git push"], "git Push") || CommandTrust.Contains(["rm -rf /"], "git push"))
            throw new Exception("A command-verb key no longer round-trips through the shape the list stores.");
        var messyKeys = CommandTrust.Normalize(["  git push ", "git PUSH", "rm -rf /", ""]);
        if (messyKeys.Count != 2 || !CommandTrust.VerbOf(messyKeys[0]).Equals("push"))
            throw new Exception($"Normalizing the verb list kept {messyKeys.Count} entries, expected 2: the same grant " + "written twice is one grant, and one this build cannot read is kept but names nothing.");
        if (!CommandTrust.VerbsOf(messyKeys).SequenceEqual(["push"]))
            throw new Exception("An unreadable entry contributed a verb, so a stale settings line could widen a grant.");
        var verbList = new List<string>();
        if (!CommandTrust.Add(verbList, "git commit") || CommandTrust.Add(verbList, "git commit") || CommandTrust.Remove(verbList, "git push") || !CommandTrust.Remove(verbList, "git commit") || verbList.Count != 0)
            throw new Exception("Adding, re-adding and removing a verb grant no longer reports what changed.");
        for (var i = 0; i < CommandTrust.Limit + 5; i++)
            CommandTrust.Add(verbList, $"git v{i}");
        if (verbList.Count != CommandTrust.Limit || CommandTrust.Add(verbList, "git one-more"))
            throw new Exception($"The verb list is not bounded at {CommandTrust.Limit} entries.");
        Console.WriteLine("PASS: a command-verb grant has one shape, one key, and a bound.");
        // A question the card cannot be built from must not park: the run would sit waiting on a card that shows
        // nothing. So the shape rules are the gate's guard, and they are asserted here rather than trusted to the
        // schema — a gateway is free to answer a tool call with anything its own model wanted.
        var good = """{"question":"Where should the retry live?","options":[{"label":"In the caller"},{"label":"In the transport","detail":"Covers every caller."}]}""";
        if (!AskUserQuestion.TryParse(good, out var asked, out var choices, out var refusal) || asked != "Where should the retry live?" || choices.Count != 2 || choices[0].Letter != "A" || choices[1].Letter != "B" || choices[0].Detail.Length != 0 || choices[1].Detail != "Covers every caller.")
            throw new Exception($"A well-formed question was refused ({refusal}) or came back reshaped.");
        if (!AskUserQuestion.TryParse("""{"question":"q","options":[{"label":"a"},{"label":"b"},{"label":"c"},{"label":"d"}]}""", out _, out var four, out _) || four.Count != 4 || four[3].Letter != "D")
            throw new Exception("The fourth option did not arrive, or was not lettered D.");
        foreach (var(label, json)in new (string, string)[]
        {
            ("one option", """{"question":"q","options":[{"label":"only"}]}"""),
            ("five options", """{"question":"q","options":[{"label":"a"},{"label":"b"},{"label":"c"},{"label":"d"},{"label":"e"}]}"""),
            ("no question", """{"options":[{"label":"a"},{"label":"b"}]}"""),
            ("blank label", """{"question":"q","options":[{"label":"a"},{"label":"   "}]}"""),
            ("duplicate labels", """{"question":"q","options":[{"label":"Same"},{"label":"same"}]}"""),
            ("options not an array", """{"question":"q","options":"a or b"}"""),
            ("not JSON at all", "question: q, options: a, b"),
        }

        )
        {
            if (AskUserQuestion.TryParse(json, out _, out _, out var why) || why.Length == 0)
                throw new Exception($"A question with {label} was accepted, or refused with nothing to say.");
        }

        // Long text is kept whole and drawn short. The card's rows have a fixed height, so what does not fit is a
        // drawing problem — and a parse that threw the rest away would leave the tip with nothing to show while the
        // answer sent back described an option, in a row ending in an ellipsis, that the model never wrote.
        var longLabel = new string ('x', AskUserQuestion.LabelLimit + 40);
        var longQuestion = new string ('q', AskUserQuestion.QuestionLimit + 60);
        if (!AskUserQuestion.TryParse($$"""{"question":"{{longQuestion}}","options":[{"label":"{{longLabel}}"},{"label":"b"}]}""", out var askedLong, out var clipped, out _) || clipped[0].Label != longLabel || clipped[0].LabelShown.Length != AskUserQuestion.LabelLimit || !clipped[0].LabelShown.EndsWith('…') || askedLong != longQuestion || AskUserQuestion.QuestionShown(askedLong).Length != AskUserQuestion.QuestionLimit || !AskUserQuestion.QuestionShown(askedLong).EndsWith('…'))
            throw new Exception("A long label or question was discarded instead of kept whole and drawn short.");
        if (!AskUserQuestion.Answered("q", clipped[0]).Contains(longLabel, StringComparison.Ordinal))
            throw new Exception("The answer sent back carried the drawn cut rather than the label the model wrote.");
        // What the person's answer becomes on the way back to the model. Pinned here because the card is the only
        // other reader of these, and a view that composed transcript text would drift from it silently.
        if (AskUserQuestion.Answered("Where?", choices[1]) != "The user answered \"B: In the transport\" to \"Where?\"." || AskUserQuestion.AnsweredInOwnWords("Where?", "put it in the retry helper") != "The user answered \"Where?\" in their own words: \"put it in the retry helper\".")
            throw new Exception("The answer text no longer names the letter, the label and the question together.");
        // Skipping says "decide yourself", and refusing says "ask what to do instead" — the second one handed to a
        // skipped question is a loop, because it ends by telling the model to go back to the thing just skipped.
        if (ToolApprovalResults.QuestionSkipped.Contains("ask the user what to do", StringComparison.OrdinalIgnoreCase) || !ToolApprovalResults.QuestionSkipped.Contains("Do not ask it again", StringComparison.Ordinal))
            throw new Exception("A skipped question now tells the model to ask again, which is a re-ask loop.");
        Console.WriteLine("PASS: a question parses into lettered options, refuses what no card can show, and answers in the model's terms.");
        // An unanswered question and an unrun action leave different facts behind. The caller picks the sentence per
        // call, so this is the one place the two can be told apart without a window.
        var mixed = Conversation.Create("orcarouter");
        mixed.Append(ChatTurn.User("do it"));
        mixed.Messages.Add(ChatTurn.FunctionCall("q1", "ask_user", good));
        mixed.Messages.Add(ChatTurn.FunctionCall("w1", "file_write", """{"path":"x.cpp"}"""));
        mixed.CloseUnansweredToolCalls(turn => turn.ToolName == "ask_user" ? ToolApprovalResults.QuestionUnanswered : ToolApprovalResults.Superseded);
        var questionResult = mixed.Messages.First(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId == "q1");
        var writeResult = mixed.Messages.First(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId == "w1");
        if (questionResult.Text != ToolApprovalResults.QuestionUnanswered || writeResult.Text != ToolApprovalResults.Superseded || !questionResult.Text.Contains("Do not ask it again", StringComparison.Ordinal))
            throw new Exception("An unanswered question was closed with the sentence meant for an unrun action.");
        if (mixed.Messages[2] != questionResult || mixed.Messages[4] != writeResult)
            throw new Exception("A synthetic result was written where the provider cannot replay it.");
        Console.WriteLine("PASS: a question nobody answered is closed as a question, beside its own call.");
        var askPreferencesPath = Path.Combine(root, "ask-preferences.json");
        var askPreferences = new HubPreferences
        {
            AllowAssistantQuestions = false
        };
        new PreferencesStore(askPreferencesPath).Save(askPreferences);
        if (new PreferencesStore(askPreferencesPath).Load().AllowAssistantQuestions)
            throw new Exception("The app-wide question preference did not round-trip as disabled.");
        if (!new HubPreferences().AllowAssistantQuestions)
            throw new Exception("The app-wide question preference must default to enabled.");
        Console.WriteLine("PASS: the app-wide choice-question preference round-trips and defaults to enabled.");
        return;
    }
}
