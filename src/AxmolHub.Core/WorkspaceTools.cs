using System.ComponentModel;
using System.Text;

namespace AxmolHub.Core;

/// <summary>
/// Everything a tool body needs that the model must never see: the sandbox, the guards, the session, and the
/// secrets whose appearance in command output has to be redacted.
///
/// One instance per request, captured when the request is built. That is what makes an approval granted after a
/// restart apply to the same directory the card was shown for: the value is read from the persisted session, not
/// re-derived from whatever is on screen now.
/// </summary>
/// <param name="ApplyWorkspaceRoot">Persists a new sandbox root and answers the model. Async because the caller
/// owns a UI thread and a session registry; null when nothing can persist one, which is the case in a self-check
/// that only exercises the guard.</param>
/// <param name="Sessions">Where the other sessions live, for the cross-session tools. Null in a scope that has no
/// session store to read — those tools then say so instead of inventing a peer.</param>
/// <param name="CrossSession">Live run state and the write path for another session, both of which belong to the
/// app. Null when nothing can answer for a peer.</param>
public sealed record WorkspaceToolScope(
    string? WorkspaceRoot,
    WorkspaceGuards Guards,
    string? DataRoot,
    string ConversationId,
    IReadOnlyList<string> SensitiveValues,
    HubLog? Log,
    Func<string, Task<string>>? ApplyWorkspaceRoot,
    ConversationStore? Sessions = null,
    CrossSessionBridge? CrossSession = null)
{
    /// <summary>A scope with nothing in it. Every file and command tool answers
    /// <see cref="WorkspacePathVerdict.NoWorkspace"/> rather than guessing a directory.</summary>
    public static WorkspaceToolScope Empty { get; } = new(null, new WorkspaceGuards(null, []), null, "", [], null, null);
}

/// <summary>
/// The assistant's hands: read, edit, run, choose the sandbox, and remember.
///
/// Bodies live in Core rather than beside their registration for one reason — <c>tests/AxmolHub.Checks</c> may
/// reference Core but not the app, so a tool that can only be exercised through a window cannot be asserted at
/// all. Registration (<c>ChatTools</c>) is the only part that needs <c>Microsoft.Extensions.AI</c>.
///
/// Each body answers in sentences, never by throwing: the text is what the model reads next, so a refusal that
/// does not say what to do instead becomes a retry loop. The workspace guard runs inside every body and in every
/// approval mode, including <see cref="ToolApprovalModes.Full"/> — approving a call is not the same as trusting
/// the path in it.
///
/// Multi-word parameters are named in snake_case, which is not how C# wants them named and is deliberate: the
/// parameter name <i>is</i> the name on the wire, because <c>AIFunctionFactory</c> exports parameters by name and
/// applies no naming policy to them. A serializer-level policy would rename the schema without renaming what the
/// invoker looks for, and the mismatch surfaces as "missing required parameter" — which reads like a model bug.
/// </summary>
public sealed class WorkspaceTools(WorkspaceToolScope context)
{
    public const int MaxReadFileBytes = 256 * 1024;
    public const int DefaultReadLimit = 400;
    public const int MaxReadLimit = 2000;
    public const int DefaultCommandTimeoutSeconds = 300;
    public const int MaxCommandTimeoutSeconds = 900;

    /// <summary>Sessions listed before the answer is cut, newest first — enough to pick a peer, short enough not
    /// to be the majority of a context window.</summary>
    public const int MaxListedSessions = 20;
    public const int DefaultReadTurns = 20;
    public const int MaxReadTurns = 60;
    public const int MaxTurnCharacters = 600;

    /// <summary>A peer message goes into another session's context and stays there. A limit that generous is what
    /// lets one assistant bury another.</summary>
    public const int MaxMessageCharacters = 4000;

    /// <summary>Compiler errors are at the end and the command line is at the beginning, so both ends are kept
    /// and the middle is dropped. The full output is in Hub's log either way, and the result says so.</summary>
    public const int CommandHeadCharacters = 2048;
    public const int CommandTailCharacters = 6144;

    [Description("Read a text file from the session workspace. Lines come back without numbering so they can be "
                 + "copied verbatim into file_write.")]
    public string ReadFile(
        [Description("Path relative to the session workspace, for example src/main.cpp. Must not contain '..', "
                     + "a drive letter or a leading separator.")]
        string path,
        [Description("First line to return, counting from 1.")] int offset = 1,
        [Description("How many lines to return at most, up to 2000.")] int limit = DefaultReadLimit)
    {
        var resolved = WorkspacePaths.ResolveRead(context.WorkspaceRoot, path, context.Guards);
        if (!resolved.IsAllowed) return Refusal(resolved.Verdict, resolved.Relative, path);

        long size;
        try
        {
            size = new FileInfo(resolved.Full).Length;
        }
        catch (IOException ex)
        {
            return $"Could not read {resolved.Relative}: {ex.Message}";
        }

        if (size > MaxReadFileBytes)
            return $"Refused: {resolved.Relative} is {size / 1024} KiB, over the {MaxReadFileBytes / 1024} KiB read "
                   + "limit. Read a window of it with offset and limit, or search it with run_command. Do not "
                   + "retry without a window.";

        if (ReadAll(resolved.Full) is not { } text) return NotText(resolved.Relative);

        var lines = SplitLines(text);
        var from = Math.Clamp(offset, 1, Math.Max(1, lines.Count));
        var count = Math.Clamp(limit, 1, MaxReadLimit);
        var window = lines.Skip(from - 1).Take(count).ToList();
        var last = from - 1 + window.Count;
        var header = $"{resolved.Relative} — lines {from}-{last} of {lines.Count} (UTF-8)";
        if (window.Count == 0) return $"{header}{Environment.NewLine}(that window is empty)";

        var builder = new StringBuilder(header);
        foreach (var line in window) builder.Append('\n').Append(line);
        if (last < lines.Count)
            builder.Append($"\n… ({lines.Count - last} more lines; read again with offset={last + 1})");
        return builder.ToString();
    }

    [Description("Edit or create one text file in the session workspace by replacing an exact string. The file is "
                 + "left untouched unless old_string matches, so a wrong anchor is a refusal rather than damage.")]
    public string FileWrite(
        [Description("Path relative to the session workspace.")] string path,
        [Description("The exact text to replace, copied verbatim from read_file including indentation and line "
                     + "breaks. Leave empty only to create a file that does not exist yet.")]
        string old_string,
        [Description("The text that takes old_string's place.")] string new_string,
        [Description("Replace every occurrence instead of refusing when old_string matches more than one place.")]
        bool replace_all = false)
    {
        var resolved = WorkspacePaths.ResolveWrite(context.WorkspaceRoot, path, context.Guards);
        if (!resolved.IsAllowed) return Refusal(resolved.Verdict, resolved.Relative, path);

        string? original = null;
        if (File.Exists(resolved.Full))
        {
            if (ReadAll(resolved.Full) is not { } text) return NotText(resolved.Relative);
            original = text;
        }

        var edit = FileEdit.Apply(original, old_string, new_string, replace_all);
        if (!edit.Changed) return FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Relative);

        // Pre-image before the write, never after: this is the only copy of what was there.
        var undo = original is null
            ? ChatUndoEntry.None
            : ChatUndoStore.Store(context.DataRoot, context.ConversationId, resolved.Relative, original);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.Full)!);
            File.WriteAllText(resolved.Full, edit.Updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not write {resolved.Relative}: {ex.Message}";
        }

        context.Log?.Write($"Assistant wrote {resolved.Full} ({edit.Verdict}, {edit.Matches} place(s))");
        return FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Relative) + ChatUndoStore.NoteFor(undo);
    }

    [Description("Run one shell command in the session workspace and return its output. On Windows the shell is "
                 + "Windows PowerShell; elsewhere pwsh, or /bin/sh when pwsh is not installed.")]
    public async Task<string> RunCommand(
        [Description("A single command line, for example: cmake --build build --config Debug")] string command,
        [Description("Kill the command after this many seconds without any output, up to 900.")]
        int timeout_seconds = DefaultCommandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var verdict = WorkspacePaths.VerifyCommandRoot(context.WorkspaceRoot, context.Guards);
        if (verdict != WorkspacePathVerdict.Allowed)
            return WorkspacePaths.ResultFor(verdict, context.WorkspaceRoot ?? "");
        if (string.IsNullOrWhiteSpace(command)) return "Refused: the command is empty. Do not retry it.";

        var shell = CommandShells.ForCurrent();
        var root = Path.GetFullPath(context.WorkspaceRoot!);
        var seconds = Math.Clamp(timeout_seconds, 1, MaxCommandTimeoutSeconds);
        var header = $"shell: {shell.Label} · cwd: {root}";
        var runner = new ProcessRunner(line => context.Log?.Write(line));

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(
                shell.Executable, shell.ArgumentsFor(command), root,
                timeout: TimeSpan.FromSeconds(seconds),
                sensitiveValues: context.SensitiveValues,
                cancellation: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user stopped the run. Surfacing it as a tool result would answer a cancellation with more text.
            throw;
        }
        catch (TimeoutException)
        {
            return $"{header}{Environment.NewLine}The command was killed after {seconds}s without producing any "
                   + "output. Raise timeout_seconds if it is a long build, or break it into smaller steps. "
                   + "Do not retry the same command unchanged.";
        }
        catch (Exception ex)
        {
            return $"{header}{Environment.NewLine}The command could not start: {ex.Message}";
        }

        var output = Shaped(result.Output, result.Error);
        return $"{header} · exit: {result.ExitCode}{Environment.NewLine}{output}";
    }

    [Description("Set the directory this session's file and command tools are confined to. Nothing outside it can "
                 + "be read, written or run in.")]
    public async Task<string> SetWorkspace(
        [Description("Absolute path of the directory to work in, for example D:\\dev\\my-game")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Refused: set_workspace needs the absolute path of a directory. Do not retry it empty.";
        if (!Path.IsPathRooted(path))
            return $"Refused: '{path}' is not an absolute path. Ask the user for the full directory. Do not retry "
                   + "a relative one.";

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            return $"Refused: '{path}' is not a usable path ({ex.Message}). Do not retry it.";
        }

        if (!Directory.Exists(full))
            return $"Refused: '{full}' does not exist or is not a directory. Ask the user to confirm the path; do "
                   + "not guess another one.";
        if (WorkspacePaths.IsProtected(full, context.Guards))
            return WorkspacePaths.ResultFor(WorkspacePathVerdict.ProtectedRoot, full);

        return context.ApplyWorkspaceRoot is { } apply
            ? await apply(full).ConfigureAwait(false)
            : $"Workspace set to {full}.";
    }

    [Description("Read one assistant memory file: a topic, or the index that lists the topics.")]
    public string MemoryRead(
        [Description("project (this workspace) or global (the user, across projects).")] string scope,
        [Description("File name, for example build-conventions.md, or the index name shown in the memory section "
                     + "of the system prompt.")]
        string name)
    {
        if (!TryParseScope(scope, out var memoryScope)) return BadScope(scope);
        var root = MemoryStore.RootFor(memoryScope, context.WorkspaceRoot, context.DataRoot);
        if (root is null) return MemoryStore.NoRootMessage(memoryScope);

        return MemoryStore.ReadText(root, memoryScope, name)
               ?? $"There is no memory file '{name}' in {memoryScope.ToString().ToLowerInvariant()} memory. The "
                  + "index lists what exists; do not guess another name.";
    }

    [Description("Save something worth remembering in a later session. Write the fact, not a summary of the "
                 + "conversation.")]
    public string MemoryWrite(
        [Description("project (this workspace) or global (the user, across projects).")] string scope,
        [Description("Topic file name in kebab-case with a .md suffix, for example build-conventions.md.")]
        string name,
        [Description("What should be remembered.")] string content,
        [Description("Short title for the index.")] string title = "",
        [Description("One line saying when this memory matters.")] string description = "",
        [Description("user, project, feedback, decision, reference or note.")] string type = "note",
        [Description("replace (the default) or append to what is already there.")] string mode = "replace")
    {
        if (!TryParseScope(scope, out var memoryScope)) return BadScope(scope);
        if (!string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase))
            return $"Refused: mode '{mode}' is neither replace nor append. Do not retry another spelling.";

        var root = MemoryStore.RootFor(memoryScope, context.WorkspaceRoot, context.DataRoot);
        if (root is null) return MemoryStore.NoRootMessage(memoryScope);

        // The directory is created inside the user's repository, so whether it is committed is their call. Said
        // once, when it first appears, and never edited into their .gitignore on their behalf.
        var fresh = !Directory.Exists(root);
        var result = MemoryStore.Write(root, memoryScope, name, title, description, type, content,
            string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase));
        if (!result.Written) return result.Message;

        context.Log?.Write($"Assistant memory ({memoryScope}): {result.Path}");
        return fresh && memoryScope == MemoryScope.Project
            ? result.Message + $" Created {WorkspacePaths.MemoryDirectory}/ in the workspace; tell the user once "
                               + "that committing it is their choice."
            : result.Message;
    }

    /// <summary>
    /// The other sessions in this Hub. A list that left the sending session out would be one the model cannot
    /// check itself against — "who else is there" and "which one am I" are the same question.
    /// </summary>
    [Description("List this Hub's chat sessions with their ids and titles, for send_to_session and read_session.")]
    public string ListSessions()
    {
        if (context.Sessions is not { } store) return NoPeers();

        var summaries = store.List().Take(MaxListedSessions).ToList();
        if (summaries.Count == 0) return "There are no chat sessions in this Hub yet, so nobody to write to.";

        var builder = new StringBuilder($"Sessions, newest first ({summaries.Count} shown of "
                                        + $"{store.List().Count}, ids are what send_to_session takes):");
        foreach (var summary in summaries)
        {
            var title = summary.Title.Length > 0 ? summary.Title : "(untitled)";
            var notes = new List<string> { $"{summary.MessageCount} messages" };
            if (summary.PendingApprovals > 0) notes.Add($"{summary.PendingApprovals} awaiting approval");
            if (string.Equals(summary.Id, context.ConversationId, StringComparison.Ordinal)) notes.Add("this session");
            builder.Append($"\n- {summary.Id} · {title} · {string.Join(" · ", notes)} · "
                           + $"updated {summary.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        }

        return builder.ToString();
    }

    [Description("Read the last turns of another session, to see what it already knows or decided.")]
    public string ReadSession(
        [Description("Another session's id or exact title, as list_sessions returns them.")] string target,
        [Description("How many of its most recent turns to read, up to 60.")] int limit = DefaultReadTurns)
    {
        if (context.Sessions is not { } store) return NoPeers();
        if (string.IsNullOrWhiteSpace(target)) return MissingTarget();

        var resolved = ResolveTarget(target, store);
        if (resolved is null) return AmbiguousOrMissing(target);
        if (resolved.Value.Id == context.ConversationId)
            return "Refused: that is the session you are in — its history is already in front of you.";
        if (store.Load(resolved.Value.Id) is not { } peer)
            return $"Refused: session {resolved.Value.Id} disappeared while it was being read.";

        var count = Math.Clamp(limit, 1, MaxReadTurns);
        var shown = peer.Messages.TakeLast(count).ToList();
        if (shown.Count == 0) return $"{resolved.Value.Title} has no turns yet.";

        var builder = new StringBuilder($"{resolved.Value.Title} — last {shown.Count} of "
                                        + $"{peer.Messages.Count} turns, oldest first:");
        foreach (var turn in shown)
        {
            // A tool result is the peer's raw command output, and forwarding it would spend this session's
            // context on bytes nobody asked for. The call itself is shown: "it is mid-way through file_write"
            // is a fact about the peer that a reader came here for.
            if (turn.Role == ChatRoles.Tool) continue;
            builder.Append('\n').Append(Describe(turn));
        }

        if (peer.Messages.Count > shown.Count)
            builder.Append($"\n… ({peer.Messages.Count - shown.Count} earlier turns not shown; raise limit)");
        return builder.ToString();
    }

    [Description("Write a message into another Hub session's history. Set wake only when that session has to act on "
                 + "it now; otherwise it reads the message when it next answers.")]
    public async Task<string> SendToSession(
        [Description("The other session's id or exact title, as list_sessions returns them.")] string target,
        [Description("What to tell that session. Short: it goes into the other session's context for good.")]
        string text,
        [Description("Start the other session answering now. Leave false for a note it can read later.")]
        bool wake = false)
    {
        if (context.Sessions is not { } store || context.CrossSession is not { } bridge) return NoPeers();
        if (string.IsNullOrWhiteSpace(target)) return MissingTarget();
        if (string.IsNullOrWhiteSpace(text))
            return "Refused: the message is empty. Say nothing instead of sending an empty turn.";
        if (text.Length > MaxMessageCharacters)
            return $"Refused: the message is {text.Length} characters, over the {MaxMessageCharacters} limit. "
                   + "Summarize what the other session needs; do not resend it as is.";

        var resolved = ResolveTarget(target, store);
        if (resolved is null) return AmbiguousOrMissing(target);

        // R2 needs one fact only the source's own transcript has: is this run answering the session it is about
        // to write back to. Read before deciding, because that is what the rule is about.
        var echo = store.Load(context.ConversationId)?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)
            ?.InjectedFrom is string origin && string.Equals(origin, resolved.Value.Id, StringComparison.Ordinal);

        var live = await bridge.RunState(context.ConversationId, resolved.Value.Id).ConfigureAwait(false);
        var decision = CrossSessionRules.Decide(new CrossSessionFacts(
            context.ConversationId, resolved.Value.Id, true, wake, echo, live.WakesUsed,
            live.TargetRunning, resolved.Value.AwaitingApproval, live.FleetHasRoom, live.QueueHasRoom));

        var title = resolved.Value.Title.Length > 0 ? resolved.Value.Title : resolved.Value.Id;
        if (decision.Verdict is CrossSessionVerdict.RefusedSelf or CrossSessionVerdict.RefusedTarget)
            return CrossSessionRules.ResultFor(decision, title);

        if (!await bridge.Deliver(context.ConversationId, resolved.Value.Id, text, decision).ConfigureAwait(false))
            return $"Refused: session {resolved.Value.Id} was deleted while the message was being delivered.";

        return CrossSessionRules.ResultFor(decision, title);
    }

    private static string NoPeers()
        => "Refused: this session cannot reach the other sessions of this Hub. Answer in your own reply instead.";

    private static string MissingTarget()
        => "Refused: no session was named. Call list_sessions first and use one of the ids it gives.";

    /// <summary>A title two sessions share is not a guess waiting to be right: refuse and hand back the ids.</summary>
    private static string AmbiguousOrMissing(string target)
        => $"Refused: 「{target}」 is not one session's exact title or id. Call list_sessions and use an id.";

    /// <summary>One turn as the peer's model reads it. Tool results are left out — they are the other session's
    /// raw command output, and forwarding them would spend this context on bytes nobody asked for.</summary>
    private static string Describe(ChatTurn turn)
    {
        var speaker = turn.Role switch
        {
            ChatRoles.User when turn.InjectedFrom is { Length: > 0 } from => $"user (written by session {from})",
            ChatRoles.User => "user",
            _ => "assistant",
        };
        var body = turn.Text.Length <= MaxTurnCharacters
            ? turn.Text
            : turn.Text[..MaxTurnCharacters] + "…";
        if (turn.ToolCallId is { Length: > 0 })
            body += (body.Length > 0 ? " " : "") + $"[calls {turn.ToolName}"
                    + (turn.ApprovalState == ChatApprovalStates.Pending ? ", awaiting approval" : "") + "]";
        return $"{speaker}: {body}";
    }

    /// <summary>Id first, then an exact title match — and only when exactly one session has it.</summary>
    private static (string Id, string Title, bool AwaitingApproval)? ResolveTarget(string wanted, ConversationStore store)
    {
        var name = wanted.Trim();
        var byId = store.List().FirstOrDefault(summary => string.Equals(summary.Id, name, StringComparison.Ordinal));
        if (byId is not null) return (byId.Id, byId.Title, byId.PendingApprovals > 0);

        var byTitle = store.List()
            .Where(summary => summary.Title.Length > 0
                              && string.Equals(summary.Title, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byTitle.Count == 1
            ? (byTitle[0].Id, byTitle[0].Title, byTitle[0].PendingApprovals > 0)
            : null;
    }

    private static bool TryParseScope(string? value, out MemoryScope scope)
    {
        if (string.Equals(value, "project", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Project;
            return true;
        }

        if (string.Equals(value, "global", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Global;
            return true;
        }

        scope = MemoryScope.Project;
        return false;
    }

    private static string BadScope(string? value)
        => $"Refused: scope '{value}' is neither project nor global. Do not retry another spelling.";

    private static string Refusal(WorkspacePathVerdict verdict, string relative, string asked)
        => WorkspacePaths.ResultFor(verdict, relative.Length > 0 ? relative : asked);

    private static string NotText(string relative)
        => $"Refused: {relative} is not UTF-8 text. The assistant reads and writes text files only; ask the user "
           + "to handle this one, or use run_command with a tool that understands the format. Do not retry.";

    /// <summary>Decodes strictly: a byte sequence that is not UTF-8 is a binary file, and replacing its
    /// undecodable bytes would let a read/write round-trip corrupt it silently. Same rule
    /// <c>ChatContextReader</c> already applies to attached files. Public because <see cref="ChatUndoStore"/>
    /// compares a file against what a write left there, and that comparison is only honest in one encoding.</summary>
    public static string? ReadAll(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        // A trailing newline ends the last line rather than starting an empty one; counting it would make every
        // "of N lines" header one too many.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Keeps both ends of the output. The middle is the part a compiler or a test run repeats.</summary>
    private string Shaped(string output, string error)
    {
        var builder = new StringBuilder();
        if (output.Length > 0) builder.Append(output.TrimEnd());
        if (error.Trim().Length > 0)
        {
            if (builder.Length > 0) builder.Append(Environment.NewLine).Append("--- stderr ---").Append('\n');
            builder.Append(error.TrimEnd());
        }

        var text = builder.Length == 0 ? "(no output)" : builder.ToString();
        var dropped = 0;
        if (text.Length > CommandHeadCharacters + CommandTailCharacters)
        {
            dropped = text.Length - CommandHeadCharacters - CommandTailCharacters;
            text = text[..CommandHeadCharacters] + $"\n…[{dropped} characters of output omitted]…\n"
                                                                  + text[^CommandTailCharacters..];
        }

        if (context.Log?.FilePath is { } logPath)
            text += $"\n(full output in {logPath})";
        return text;
    }
}
