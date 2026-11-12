using System.ComponentModel;
using System.Text.Json;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub;

/// <summary>
/// The tools the assistant may call, and what each one is allowed to touch.
///
/// Risk lives beside the tool because <c>AIFunctionFactoryOptions</c> has nowhere to put it, and because a
/// permission mode that had to know tools by name would stop being a promise a user could hold the app to.
/// Keeping the list in one place is also what lets the same tools be projected for the CLI or an MCP server
/// later without deciding any of this twice.
///
/// The bodies are in <see cref="WorkspaceTools"/> (Core), so every one of them can be asserted without a window.
/// This file only binds them to names and to a risk.
/// </summary>
internal static class ChatTools
{
    /// <summary>The tools for one request. <c>ask</c> gets only explicitly enabled DeepSeek web tools, which
    /// read external information without changing the workspace. <c>plan</c> gets the ordinary read-only tools
    /// and those same optional web tools, plus the question tool, which is not read-only and never was a
    /// permission question either way: it reads
    /// nothing and changes nothing, it stops the run and waits for a person.</summary>
    public static IReadOnlyList<AITool> CreateFor(string mode, ChatToolScope scope) => mode switch
    {
        ChatModes.Ask => DeepSeekWebBodies(scope) ? [.. WebTools(scope)] : [],
        ChatModes.Plan => [.. ReadOnlyTools(scope), .. WebTools(scope), .. AskingTools(scope)],
        _ => [.. ReadOnlyTools(scope), .. WebTools(scope), .. ActingTools(scope),
              .. AskingTools(scope), .. PlanTaskTools(scope)],
    };

    /// <summary>Whether the DeepSeek bodies are the ones on offer for the web pair: the switch is on <i>and</i> this
    /// workspace holds a bridge to run them. The declaration and fetch-body selection share this condition.</summary>
    private static bool DeepSeekWebBodies(ChatToolScope scope)
        => scope.DeepSeekWebApiEnabled && scope.Workspace.DeepSeekWeb is not null;

    /// <summary>Prefer the DeepSeek pair when its enabled bridge is available; otherwise use local bodies.
    /// Both names remain callable when switched off, so their bodies return a precise refusal instead of
    /// leaving an old transcript's tool call undeclared.</summary>
    private static IEnumerable<AITool> WebTools(ChatToolScope scope)
    {
        var tools = new WorkspaceTools(scope.Workspace);
        return
        [
            AIFunctionFactory.Create(tools.SearchWeb, Options(ToolNames.WebSearch)),
            DeepSeekWebBodies(scope)
                ? AIFunctionFactory.Create(tools.FetchDeepSeekWebPage, Options(ToolNames.WebFetch))
                : AIFunctionFactory.Create(tools.FetchWebPage, Options(ToolNames.WebFetch)),
        ];
    }

    /// <summary>The question tool, or nothing. Switched off means the model is never told the capability exists,
    /// which is the only shape of "don't ask me" that cannot leave a run parked on a question nobody will read —
    /// a declared-but-ignored tool still gets called.</summary>
    private static IEnumerable<AITool> AskingTools(ChatToolScope scope)
        => scope.AskUserEnabled ? [new AskUserTool()] : [];

    private static IEnumerable<AITool> PlanTaskTools(ChatToolScope scope)
        => scope.PlanTasksEnabled
            ? [AIFunctionFactory.Create(new WorkspaceTools(scope.Workspace).UpdatePlanTask,
                Options(ToolNames.UpdatePlanTask))]
            : [];

    /// <summary>
    /// Risk by name, arguments and the sandbox the call runs in. The arguments decide two of them: a memory note
    /// inside the workspace is the assistant's own and never asks, while the same write to global memory lands in
    /// Hub's data directory, outside every sandbox, and does; and asking for a directory is the one call that opens
    /// a place this session did not hold. A command's tier comes from the root the request was built
    /// with — see <see cref="ChatWorkspace"/>'s gate, which passes that same scope to the tool body.
    ///
    /// An unknown name lands on the worst tier, and so does a call whose arguments cannot be parsed: a call this
    /// build cannot classify is exactly the one that should not run unasked. The scope is a required argument
    /// rather than an optional one because an omitted scope is a silent second table — the card and the gate would
    /// have to agree by habit instead of by construction.
    /// </summary>
    public static ToolRisk RiskOf(string name, string? argumentsJson, WorkspaceToolScope scope,
        IReadOnlyList<string>? trustedCommands = null) => name switch
    {
        ToolNames.ProjectList or ToolNames.EngineList or ToolNames.ToolchainStatus or ToolNames.FsRead or ToolNames.MemoryRead
            or ToolNames.ListSessions or ToolNames.ReadSession
            or ToolNames.FsGrep or ToolNames.FsList or ToolNames.FsFind or ToolNames.WorkspaceMap
            => ToolRisk.ReadOnly,
        ToolNames.FsWrite or ToolNames.FsReplace or ToolNames.SendToSession => ToolRisk.WorkspaceWrite,
        ToolNames.FsDelete or ToolNames.FsMove => ManagementRisk(name, argumentsJson, scope),
        ToolNames.UpdatePlanTask => ToolRisk.AssistantNote,
        // Decided by one method rather than an inline ternary, because there are now two things that can make a
        // command cost more than the sandbox tier, and both have to be read from the same place the gate reads.
        ToolNames.RunCommand => RunCommandRisk(scope, argumentsJson, trustedCommands),
        // Local search and fetching share the sandbox-command tier. DeepSeek search/fetch are read-only
        // provider API calls; the outbound switch is still checked by every body.
        ToolNames.WebSearch or ToolNames.WebFetch when scope.DeepSeekWeb is not null => ToolRisk.ReadOnly,
        ToolNames.WebSearch or ToolNames.WebFetch => ToolRisk.WorkspaceCommand,
        // Asking for a directory is asking for a place nobody has agreed to yet, so it sits on the tier that always
        // asks. It stays there whatever the path is: the card *is* the mechanism, and a
        // call that fell through silently under 自动审批 would be a grant the person never saw. 「总是允许」 is not
        // offered — the same tier rule as the screen and another session — and here it would mean "open every
        // directory I ever name". A single file shares the tier for the same reason, and 「总是允许」 is refused
        // harder still there: it would read as "open every file I ever name", which is not something one click
        // could have meant.
        ToolNames.FsRequestAccess => ToolRisk.SystemCommand,
        // Reading the screen writes nothing, but it shows the desktop to a model that decided when to look, and
        // the frame leaves the machine in the next request. That is the SystemCommand tier's job: it asks in
        // every mode but full. Spelled out rather than left to the fallback arm, because "unknown tool" and "this
        // tool was thought about" must not read the same in this table.
        // "spawn_session" is spelled out for the same reason: a new session spends money on nobody's click.
        ToolNames.CaptureScreen => ToolRisk.SystemCommand,
        ToolNames.ComputerUse => ToolRisk.SystemCommand,
        ToolNames.SpawnSession => ToolRisk.SystemCommand,
        // The one tier no mode ever asks for, which is exactly why the reading of the scope has to fail closed:
        // a scope this cannot name is not a workspace note, it is a write whose destination is unknown, and an
        // unknown destination asks a person. Comparing against Workspace rather than Global is what keeps that
        // true for a call whose arguments do not parse at all.
        ToolNames.MemoryWrite => ScopeOf(argumentsJson) == MemoryScope.Workspace
            ? ToolRisk.AssistantNote
            : ToolRisk.WorkspaceWrite,
        // Spelled out for the standing reason, and it bites harder here than anywhere: the unknown-name arm below
        // still parks, so a missing line like this reads as working while drawing the question with the
        // full-access glyph, logging it as a reach past the sandbox, and telling the card its 「总是允许」 is
        // unavailable. Three claims about asking somebody a question, all of them false.
        "ask_user" => ToolRisk.AskUser,
        _ => ToolRisk.SystemCommand,
    };

    /// <summary>
    /// A command's tier: the directory it will run in, what the command does to a repository or to a build, and —
    /// where those two do not already decide it — which write verbs a person has said to stop asking about.
    ///
    /// <para>The root comes from the same check the tool body runs before it spawns the shell
    /// (<see cref="WorkspacePaths.VerifyCommandRoot"/>, asked of the <c>cwd</c> the call names — empty means the
    /// workspace itself), so "this call is exempt" and "this call stays inside a directory the person allowed" are
    /// one expression rather than two that can drift apart. A directory outside every root — no workspace, a
    /// protected one, a link-ridden one, or a path the arguments do not spell — is not a workspace command, and a
    /// command that runs nowhere this session holds asks in every mode. <c>run_command</c>'s own body refuses it
    /// too; the tier is about whether it may run unasked, not about whether it may run.</para>
    ///
    /// <para>The verb is the second half, and it is the reason the arm is a method: two different things can make a
    /// command cost more than the sandbox, and the line between them is a ruling rather than a ranking.
    /// <c>git reset --hard</c> and <c>git push</c> do run in the directory the person chose and still belong on the
    /// same tier as the screen and another session — the one tier a 「总是允许」 grant cannot open (see the gate in
    /// <c>ChatWorkspace</c>) and the one that keeps asking under 自动审批. The writes that stay inside the
    /// repository and are put back from it (<see cref="GitCommandGrades.LocalWriteVerbs"/>, deliberately two words
    /// long) are ordinary work instead, exactly like a build: <see cref="ToolRisk.RepositoryWrite"/> is a card in
    /// 询问审批 and nothing at all in 自动审批. Git <i>reads</i> stay on the sandbox tier where every established
    /// client leaves them: a model that has to ask permission to see <c>git status</c> answers from a file it read
    /// three turns ago instead, which is a worse guard, not a stricter one.</para>
    ///
    /// <para><b>A cache break is asked about before a git verb is</b>, and that order is the rule rather than a
    /// detail: <c>git commit -m x &amp;&amp; rm -rf build</c> under 完全访问 must still produce a card, and if the git read
    /// decided the tier the line would land on <see cref="ToolRisk.SystemCommand"/> — which full access answers
    /// silently, with the deletion attached to it. A line that only <em>builds</em> is different: it stays on the
    /// sandbox tier whatever else it does, because the debug loop is ordinary work. The two ladders also disagree on
    /// purpose about <c>git clean</c>: git grades it as a write (it moves the working tree) and this grades it as a
    /// cache break (it removes what the tree was built from), and the destructive reading is the one that gets the
    /// card in every mode.</para>
    /// </summary>
    /// <summary>
    /// The command ladder, in the one order that matters. The sandbox test comes first, then the build classifier's
    /// cache-break verdict, then the local-write table, and the person's granted verbs last — so <c>git clean -fdx</c>
    /// is a cache break even with <c>git clean</c> granted, and no grant can walk a command past the first two rungs.
    /// The two git questions are asked one at a time rather than as one union: <see cref="GitCommandGrades.LocalWritesOnly"/>
    /// is what this build calls ordinary work, and <see cref="GitCommandGrades.AllWritesAllowed"/> is what a person has
    /// answered for. A line whose writes straddle the two (<c>git add &amp;&amp; git push</c> with only <c>push</c>
    /// granted) is covered by neither, because a grant names a verb and a mode does not get to widen it.
    /// </summary>
    private static ToolRisk RunCommandRisk(WorkspaceToolScope scope, string? argumentsJson,
        IReadOnlyList<string>? trustedCommands)
    {
        if (CwdOf(argumentsJson) is not { } cwd) return ToolRisk.SystemCommand;
        if (WorkspacePaths.VerifyCommandRoot(scope.Sandbox, cwd, scope.Guards) != WorkspacePathVerdict.Allowed)
            return ToolRisk.SystemCommand;
        if (BuildCommandGrades.CommandOf(argumentsJson) == BuildGrade.CacheBreak) return ToolRisk.CacheBreak;
        if (GitCommandGrades.LocalWritesOnly(argumentsJson)) return ToolRisk.RepositoryWrite;
        return GitCommandGrades.AllWritesAllowed(argumentsJson, CommandTrust.VerbsOf(trustedCommands))
            ? ToolRisk.WorkspaceCommand
            : ToolRisk.SystemCommand;
    }

    private static ToolRisk ManagementRisk(string name, string? argumentsJson, WorkspaceToolScope scope)
    {
        var path = ArgumentOf(argumentsJson, name == ToolNames.FsMove ? "source" : "path");
        if (path is null) return ToolRisk.SystemCommand;
        return WorkspaceTools.ManagementMayBreakCache(scope, path) ? ToolRisk.CacheBreak : ToolRisk.WorkspaceWrite;
    }

    /// <summary>
    /// The single git write verb a card may offer to trust, or null when no one verb would change this call's
    /// verdict. The card's third button and the gate read this one predicate, so a button can never be drawn for a
    /// grant the gate would ignore: a compound line writing two verbs has no single key, a write reached through a
    /// shell has no spelled key (<c>pwsh -c "git push"</c>), a verb this build cannot name has none either, and a
    /// call that asks for another reason — no sandbox root, a cache break — is not helped by trusting a verb.
    /// </summary>
    public static string? TrustCandidateVerb(string name, string? argumentsJson, WorkspaceToolScope scope,
        IReadOnlyList<string>? trustedCommands = null)
    {
        if (!string.Equals(name, ToolNames.RunCommand, StringComparison.Ordinal)) return null;
        var verbs = GitCommandGrades.WriteVerbsOf(argumentsJson);
        if (verbs.Count != 1) return null;
        var key = CommandTrust.KeyFor(verbs.First());
        if (key.Length == 0) return null;
        // Ask the same ladder twice: what the call is now, and what it would be with this one verb granted.
        if (RiskOf(name, argumentsJson, scope, trustedCommands) != ToolRisk.SystemCommand) return null;
        var granted = new List<string>(trustedCommands ?? []);
        if (!CommandTrust.Add(granted, key)) return null;
        return RiskOf(name, argumentsJson, scope, granted) == ToolRisk.WorkspaceCommand ? key : null;
    }

    /// <summary>Whether a granted command verb is the reason this call runs: the person's list, and not the built-in
    /// policy, covers every write in the line. The gate reads it for one thing the tier cannot express — a grant
    /// silences the call in every mode, the way a tool grant does, while the built-in policy only makes it a sandbox
    /// command that 询问档 still asks about.</summary>
    public static bool GrantedCommandVerb(string name, string? argumentsJson, IReadOnlyList<string>? trustedCommands)
    {
        // The line has to write something, or every command that is not git would count as granted: "no git write in
        // it" is exactly the state AllWritesAllowed allows vacuously, and the gate reads a true here as "a person
        // answered for this".
        if (!string.Equals(name, ToolNames.RunCommand, StringComparison.Ordinal)) return false;
        if (GitCommandGrades.WriteVerbsOf(argumentsJson).Count == 0) return false;
        return GitCommandGrades.AllWritesAllowed(argumentsJson, CommandTrust.VerbsOf(trustedCommands));
    }

    /// <summary>Finds a registered tool by name. The path that executes an <i>approved</i> call needs this,
    /// because it invokes the function itself instead of handing the call back to the model loop.</summary>
    public static AIFunction? Find(string name, string mode, ChatToolScope scope)
        => CreateFor(mode, scope).OfType<AIFunction>()
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    /// <summary>The directory a <c>run_command</c> call would run in, exactly as the model wrote it: empty when the
    /// call names none, which is the workspace itself. <c>null</c> means the arguments cannot be read at all, and a
    /// call this build cannot read is not one it can vouch for — the caller grades it as a reach past the sandbox
    /// rather than as a command in the workspace.</summary>
    private static string? CwdOf(string? argumentsJson) => ArgumentOf(argumentsJson, "cwd");

    /// <summary>
    /// Whether this call would open a protected location — Hub's own data directory, an engine installation, a
    /// <c>.git</c> — which is the one grant no approval mode answers for. Asked of
    /// <see cref="WorkspacePaths.ClassifyGrant"/> and <see cref="WorkspacePaths.ClassifyFileGrant"/>, so the gate
    /// and the card cannot disagree about what counts: <see cref="GrantVerdict.GrantedReadOnly"/> is exactly "a card
    /// would land a read-only grant here", and the same value is what the gate turns into a card in 完全访问 as well
    /// as in the two modes that ask anyway.
    ///
    /// <para>A file grant that is read-only <i>only</i> because of its extension is not this: a log outside any
    /// protected location is an ordinary grant, and 完全访问 is allowed to answer it silently. What the extra
    /// <see cref="WorkspacePaths.IsProtected"/> asks is the place, which is the part no mode can wave through.</para>
    /// </summary>
    public static bool OpensProtectedLocation(string name, string? argumentsJson, WorkspaceToolScope scope)
        => ArgumentOf(argumentsJson, "path") is { Length: > 0 } path
           && (string.Equals(name, ToolNames.FsRequestAccess, StringComparison.Ordinal)
               && WorkspacePaths.IsProtected(path, scope.Guards)
               && (ToolPreviews.AccessKind(argumentsJson) == "directory"
                   && WorkspacePaths.ClassifyGrant(scope.Sandbox, scope.Guards, path).Verdict == GrantVerdict.GrantedReadOnly
                   || ToolPreviews.AccessKind(argumentsJson) == "file"
                   && WorkspacePaths.ClassifyFileGrant(scope.Sandbox, scope.Guards, path).Verdict == GrantVerdict.GrantedReadOnly));

    /// <summary>One string argument, exactly as the model wrote it: empty when the call names none, null when the
    /// arguments cannot be read at all. One reader for every argument the tier table looks at, because two of them
    /// disagreeing is a call tiered against one directory and executed into another.</summary>
    private static string? ArgumentOf(string? argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return "";
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The scope a stored call names, read through Core's one spelling list — the same reading the tool
    /// body uses, so a call cannot be tiered against one directory and then executed into another. An unreadable
    /// scope is null, which the table turns into the tier that asks a person rather than a silent write.</summary>
    private static MemoryScope? ScopeOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("scope", out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            return MemoryScopes.Of(value.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AIFunctionFactoryOptions Options(string name, string? description = null) => new()
    {
        Name = name,
        Description = description,
    };

    private static IReadOnlyList<AITool> ReadOnlyTools(ChatToolScope scope)
    {
        var snapshot = scope.Snapshot
                       ?? throw new InvalidOperationException("The Hub read-only tool snapshot is unavailable.");
        var tools = new WorkspaceTools(scope.Workspace);

        return
        [
            AIFunctionFactory.Create(
                (Func<string>)(() => JsonSerializer.Serialize(snapshot.Projects.Select(project => new
                {
                    project.Name,
                    project.Path,
                    project.EngineVersion,
                    project.Platform,
                    project.Configuration,
                    project.BuildStatus,
                }))),
                Options(ToolNames.ProjectList,
                    "List the Axmol game projects registered in Hub — each one is a workspace with a locked engine "
                    + "version and a build target. A workspace is any directory, so being a project only adds those "
                    + "attributes.")),
            AIFunctionFactory.Create(
                (Func<string>)(() => JsonSerializer.Serialize(snapshot.Engines)),
                Options(ToolNames.EngineList, "List installed Axmol engine versions and channels.")),
            AIFunctionFactory.Create(
                (Func<string>)(() => JsonSerializer.Serialize(snapshot.Toolchains)),
                Options(ToolNames.ToolchainStatus,
                    "Read the latest already-known toolchain detection status; does not run probes or change anything.")),
            AIFunctionFactory.Create(tools.ReadFile, Options(ToolNames.FsRead)),
            AIFunctionFactory.Create(tools.SearchText, Options(ToolNames.FsGrep)),
            AIFunctionFactory.Create(tools.ListDirectory, Options(ToolNames.FsList)),
            AIFunctionFactory.Create(tools.FindFiles, Options(ToolNames.FsFind)),
            // The method is MapWorkspace because WorkspaceMap is the Core class it delegates to; the name the
            // model sees is the tool's own, and AIFunctionFactory would have taken the method name otherwise.
            AIFunctionFactory.Create(tools.MapWorkspace, Options(ToolNames.WorkspaceMap)),
            AIFunctionFactory.Create(tools.MemoryRead, Options(ToolNames.MemoryRead)),
            AIFunctionFactory.Create(tools.ListSessions, Options(ToolNames.ListSessions)),
            AIFunctionFactory.Create(tools.ReadSession, Options(ToolNames.ReadSession)),
        ];
    }

    private static IReadOnlyList<AITool> ActingTools(ChatToolScope scope)
    {
        var tools = new WorkspaceTools(scope.Workspace);
        var result = new List<AITool>
        {
            AIFunctionFactory.Create(tools.FsWrite, Options(ToolNames.FsWrite)),
            AIFunctionFactory.Create(tools.FsReplace, Options(ToolNames.FsReplace)),
            AIFunctionFactory.Create(tools.FsDelete, Options(ToolNames.FsDelete)),
            AIFunctionFactory.Create(tools.FsMove, Options(ToolNames.FsMove)),
            AIFunctionFactory.Create(tools.RunCommand, Options(ToolNames.RunCommand)),
            // The name is the pair's, not this call's: web_fetch reads a page from here, and the phase-two search
            // arrives as web_search — which is not ours to name anyway, since a hosted tool is advertised by the
            // provider's own kind string. Two nouns in front of "fetch" and "search" so the model sees one capability
            // family even though only this half has a Hub body. The method behind it is FetchWebPage because
            // WebFetch is the rules class in Core's namespace, and a method of that name would shadow it here.
            scope.Workspace.PortalComputer is not null
                ? AIFunctionFactory.Create(tools.CaptureScreenAsync, Options(ToolNames.CaptureScreen))
                : AIFunctionFactory.Create(tools.CaptureScreen, Options(ToolNames.CaptureScreen)),
            scope.Workspace.PortalComputer is not null
                ? AIFunctionFactory.Create(tools.ComputerUseAsync, Options(ToolNames.ComputerUse))
                : AIFunctionFactory.Create(tools.ComputerUse, Options(ToolNames.ComputerUse)),
            AIFunctionFactory.Create(tools.FsRequestAccess, Options(ToolNames.FsRequestAccess)),
            AIFunctionFactory.Create(tools.MemoryWrite, Options(ToolNames.MemoryWrite)),
            AIFunctionFactory.Create(tools.SendToSession, Options(ToolNames.SendToSession)),
            AIFunctionFactory.Create(tools.SpawnSession, Options(ToolNames.SpawnSession)),
        };
        return result;
    }
}
