using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

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
/// <param name="Screen">The host's capture mechanics. Null on a host this build has not taught to draw a frame,
/// which the tool says out loud rather than returning a black picture.</param>
/// <param name="RecordFrames">Where a captured frame is handed to the app so the result turn can carry it. The
/// turn is written by the app, not by this class, and the call id never reaches a tool body — so the frame goes
/// out through the request's own scope, which is one request and one conversation by construction.</param>
/// <param name="Web">The host's side of an outbound fetch: the setting that allows it and the socket that makes it.
/// Null in a scope with nothing to send with — <c>web_fetch</c> then says so out loud rather than pretending the
/// address was bad, which is the only way a zero-network self-check and a real Hub answer the same question.</param>
public sealed record WorkspaceToolScope(
    string? WorkspaceRoot,
    WorkspaceGuards Guards,
    string? DataRoot,
    string ConversationId,
    IReadOnlyList<string> SensitiveValues,
    HubLog? Log,
    Func<string, Task<string>>? ApplyWorkspaceRoot,
    ConversationStore? Sessions = null,
    CrossSessionBridge? CrossSession = null,
    ScreenCaptureBridge? Screen = null,
    Action<IReadOnlyList<ChatImage>>? RecordFrames = null,
    WebBridge? Web = null)
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

    /// <summary>How many files a search opens before it stops and says so. A walk bounded by files found would
    /// still read all of a tree to report nothing; this is the bound that actually costs anything.</summary>
    public const int MaxScannedFiles = 4000;
    public const int DefaultSearchMatches = 200;
    public const int MaxSearchMatches = 500;

    /// <summary>One minified bundle line can be a megabyte. Kept short so a single hit cannot eat the result.</summary>
    public const int MaxSearchLineCharacters = 200;

    /// <summary>Bigger than this is generated or vendored, not where a source question lives.</summary>
    public const long MaxSearchFileBytes = 1024 * 1024;

    public const int DefaultListDepth = 1;
    public const int MaxListDepth = 4;
    public const int MaxListedEntries = 300;
    public const int MaxFoundFiles = 500;

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
                 + "PowerShell 7 when it is installed and Windows PowerShell otherwise; elsewhere pwsh, or "
                 + "/bin/sh when pwsh is not installed.")]
    public async Task<string> RunCommand(
        [Description("A single command line, for example: cmake --build build --config Debug")] string command,
        [Description("How many seconds of silence end the command, up to 900. It is not a total time limit: a "
                     + "build that keeps printing may run far longer than this.")]
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
        catch (IdleTimeoutException stalled)
        {
            // Going quiet is not the same as having nothing to say. The lines before the silence are the diagnosis
            // — a build that hangs has already printed the error it stopped on — so they come back with the result,
            // head-and-tail truncated like any other output.
            var builder = new StringBuilder($"{header}{Environment.NewLine}The command was killed after {seconds}s "
                                            + "of no output. What it printed before going silent:")
                .Append(Environment.NewLine).Append(Shaped(stalled.Output, stalled.Error));
            if (stalled.SurvivedKill)
                builder.Append(Environment.NewLine)
                    .Append("It was still running after Hub gave up on killing it, so it may still hold files in "
                            + "the workspace.");
            builder.Append(Environment.NewLine)
                .Append("timeout_seconds counts silence, not total runtime, so raise it only if the command really "
                        + "stopped printing. Do not retry the same command unchanged.");
            return builder.ToString();
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

    [Description("Fetch one web page over https and return its text with the scripts, styles and menus taken out. "
                 + "For a documentation page, a changelog, or the link behind an error the user quoted — the parts "
                 + "of the web that answer a question. Text only: a PDF or an image has to be downloaded with "
                 + "run_command, which shows the user the whole command.")]
    public async Task<string> FetchWebPage(
        [Description("The absolute URL to read, https only, for example https://www.lua.org/manual/5.4/readme.html")]
        string url,
        [Description("How many characters of page text to return, up to 20000.")]
        int max_characters = WebFetch.DefaultCharacters,
        CancellationToken cancellationToken = default)
    {
        // No path guard runs here, deliberately: the sandbox says nothing about a URL. What has something to say is
        // the setting (is this Hub allowed out at all), WebFetch's scheme-and-host policy (is this address one to
        // ask), and the approval tier the registration names (does a person have to agree first).
        if (context.Web is not { } bridge)
            return WebFetch.ResultFor(new WebFetchDecision(WebFetchVerdict.RefusedNoHost, null), url ?? "");

        var decision = WebFetch.Decide(bridge, url);
        if (decision.Verdict != WebFetchVerdict.Allowed) return WebFetch.ResultFor(decision, url);

        var target = decision.Target!;
        context.Log?.Write($"Assistant fetching {SecretRedaction.Redact(target.ToString(), context.SensitiveValues)}");
        return await WebFetch.FetchAsync(bridge, target, max_characters, context.SensitiveValues, cancellationToken)
            .ConfigureAwait(false);
    }

    [Description("Capture one window, or the whole display, as an image and put it in this conversation so you can "
                 + "see what is on the screen: an error in another app, the state of a running game window, what "
                 + "the user is looking at. The picture arrives attached to this result — read it from there. "
                 + "Nothing is written to the workspace.")]
    public string CaptureScreen(
        [Description("Part of the window's title, matched case-insensitively. Leave it empty only when fullscreen "
                     + "is true; a target matching several windows is refused rather than guessed.")]
        string target = "",
        [Description("Capture the entire display instead of one window.")] bool fullscreen = false)
    {
        // No path guard runs here, deliberately: this call reads the screen and stores into Hub's own session
        // directory, so the sandbox has nothing to say about it. What does have something to say is the approval
        // gate — capture_screen sits on the SystemCommand tier and asks in every mode but full.
        var backend = CaptureBackends.ForCurrent();
        if (!backend.Available) return backend.Refusal!;
        if (context.Screen is not { } host)
            return $"Refused: this build has no capture host for {backend.Label}. Ask the user for a screenshot "
                   + "instead; do not retry the capture.";

        // The whole display is the one call with no target; anything else has to name exactly one window first.
        var wanted = (target ?? "").Trim();
        CapturableWindow? window = null;
        if (!fullscreen || wanted.Length > 0)
        {
            var match = ScreenCapture.Find(TryWindows(host), wanted);
            if (!match.Ok) return match.Refusal!;
            window = match.Window;
        }

        // A grabber swallows its own failure and answers null, so what the model reads is a sentence about the
        // screen rather than a stack trace from the host.
        if (host.Grab(window) is not { } frame)
            return $"Refused: the {backend.Label} capture of {Described(window, fullscreen)} returned no frame. The "
                   + "window may have closed, or the display may be locked; ask the user before capturing anything "
                   + "else.";

        // The blank check has to come before the store: a black frame is a valid PNG with a plausible byte count,
        // so every later step — admission, the file on disk, the wire — reads it as a capture that worked, and the
        // model then describes a screenshot that showed nothing. This is the one place that knows.
        var what = Described(window, fullscreen);
        if (frame.Stats.IsBlank())
            return $"Refused: the {backend.Label} capture of {what} came back a blank frame — {frame.Width}×"
                   + $"{frame.Height}, {frame.Stats.DistinctColors} distinct colour(s), variance "
                   + $"{frame.Stats.LuminanceVariance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}. "
                   + "Nothing was stored and no picture is attached. That window paints through a path this capture "
                   + "cannot read (a hardware-accelerated surface, or a protected desktop); ask the user for a "
                   + "screenshot instead, and do not retry this target.";

        var verdict = ChatImageFormat.Admit(frame.Png, 0);
        if (verdict != ChatImageVerdict.Accepted) return ChatImageFormat.ResultFor(verdict, frame.Png.LongLength);
        if (context.Sessions is not { } store)
            return "Refused: this session has no attachment storage to write the frame into. Ask the user for a "
                   + "screenshot; do not retry the capture.";

        var image = store.SaveImage(context.ConversationId, frame.Png);
        context.RecordFrames?.Invoke([image]);
        return $"Captured {what} with {backend.Label} — {frame.Width}×{frame.Height}, {image.Bytes} bytes, stored as "
               + $"{image.File}. The picture is attached to this result as an image: say what is on it, and do not "
               + "infer it from the title.";
    }

    /// <summary>The window list, or an empty one. A host that cannot enumerate windows has not captured anything,
    /// and "no visible window matches" with nothing listed is the truthful answer rather than a throw.</summary>
    private static IReadOnlyList<CapturableWindow> TryWindows(ScreenCaptureBridge host)
    {
        try
        {
            return host.Windows();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Described(CapturableWindow? window, bool fullscreen)
        => window is null
            ? (fullscreen ? "the whole display" : "no target")
            : $"window \"{window.Title}\" (pid {window.ProcessId}, {window.Width}×{window.Height})";

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
    ///
    /// <para>Archived sessions are left out: the user put them away, and a peer the model can write to but the
    /// person cannot see is a message that goes nowhere they can notice. Their transcripts are still readable by
    /// id — <see cref="ReadSession"/> — because reading what somebody concluded is not the same as talking back.</para>
    /// </summary>
    [Description("List this Hub's chat sessions with their ids and titles, for send_to_session and read_session.")]
    public string ListSessions()
    {
        if (context.Sessions is not { } store) return NoPeers();

        var reachable = store.List().Where(summary => !summary.Archived).ToList();
        if (reachable.Count == 0) return "There are no chat sessions in this Hub yet, so nobody to write to.";

        var summaries = reachable.Take(MaxListedSessions).ToList();
        var builder = new StringBuilder($"Sessions, newest first ({summaries.Count} shown of "
                                        + $"{reachable.Count}, ids are what send_to_session takes):");
        foreach (var summary in summaries)
        {
            var title = summary.Title.Length > 0 ? summary.Title : "(untitled)";
            var notes = new List<string> { $"{summary.MessageCount} messages" };
            if (summary.PendingApprovals > 0) notes.Add($"{summary.PendingApprovals} awaiting approval");
            // A session the person did not start has to say so, or the list reads like a pile of conversations
            // with unknown origins — and one of them is a helper this very model asked for.
            if (summary.SpawnedBy is { Length: > 0 } parent) notes.Add($"spawned from {parent}");
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
        // Refused before the cross-session rules run, and for both halves of the tool: a note left in a session
        // the user put away is as unread as a wake that lights a dot on a row no list shows. What the model has to
        // hear is that the session is not gone — the user stopped looking at it — and that read_session still works.
        if (resolved.Value.Archived)
            return $"Refused: session {resolved.Value.Id} is archived, so the user is not looking at it. Do not "
                   + "write there; read_session can still show what it concluded, and your own reply is where to "
                   + "say what you found.";

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

    [Description("Start a fresh Hub session to do one bounded piece of work in a context of its own, and have it "
                 + "report back to this session later. Worth it when the job means reading a lot (a big file, a "
                 + "whole directory, a long build log) but only a short conclusion is wanted here, or when it can "
                 + "run independently of what this conversation already knows. Not worth it for something you can "
                 + "answer directly. The child answers in its own session: never wait for it in this turn, and "
                 + "never spawn the same one twice.")]
    public async Task<string> SpawnSession(
        [Description("What the child is to do, written self-contained — it cannot see this conversation. Name the "
                     + "files or the question, say what conclusion to bring back, and include this session's id "
                     + "as where to send it.")] string task,
        [Description("The mode the child runs in: \"agent\" to read and edit, \"plan\" to look and propose without "
                     + "touching anything.")] string mode = "agent",
        [Description("Give the child this session's workspace so it can read the same repository. Leave it false "
                     + "only for a child that needs no files at all.")] bool inherit_workspace = true)
    {
        if (context.Sessions is not { } store || context.CrossSession is not { } bridge) return NoPeers();
        if (bridge.Spawn is not { } start)
            return "Refused: this Hub does not let a tool call start a session. Do the work here instead; do not "
                   + "retry the spawn.";
        if (string.IsNullOrWhiteSpace(task))
            return "Refused: the task is empty, and a session started with nothing to do answers nothing. Say what "
                   + "the child should conclude; do not retry it empty.";
        if (task.Length > MaxMessageCharacters)
            return $"Refused: the task is {task.Length} characters, over the {MaxMessageCharacters} limit. The "
                   + "child cannot see this conversation, so write what it needs to do instead of pasting what you "
                   + "already know; do not resend the same text.";

        var childMode = string.Equals(mode.Trim(), ChatModes.Plan, StringComparison.OrdinalIgnoreCase)
            ? ChatModes.Plan
            : ChatModes.Agent;

        // The target half of the live state is nobody's business here: a spawn starts a session that does not
        // exist yet, so only the source-side counts and the fleet decide anything.
        var live = await bridge.RunState(context.ConversationId, "").ConfigureAwait(false);
        var decision = SpawnRules.Decide(new SpawnFacts(live.SpawningAllowed, live.SourceIsSpawned,
            live.SpawnsUsed, live.ActiveSpawnedSessions, live.FleetHasRoom, live.QueueHasRoom));
        if (decision.Verdict is not (SpawnVerdict.Started or SpawnVerdict.Queued))
            return SpawnRules.ResultFor(decision, "", childMode);

        var childId = await start(new SpawnRequest(context.ConversationId, task.Trim(), childMode,
            inherit_workspace, decision.Verdict == SpawnVerdict.Started)).ConfigureAwait(false);
        return childId is { Length: > 0 } id
            ? SpawnRules.ResultFor(decision, id, childMode)
            : "Refused: the child session could not be created — the fleet or the model list turned it down. Do "
              + "this part yourself; do not retry the spawn.";
    }

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
    private static (string Id, string Title, bool AwaitingApproval, bool Archived)? ResolveTarget(string wanted, ConversationStore store)
    {
        var name = wanted.Trim();
        var byId = store.List().FirstOrDefault(summary => string.Equals(summary.Id, name, StringComparison.Ordinal));
        if (byId is not null) return (byId.Id, byId.Title, byId.PendingApprovals > 0, byId.Archived);

        var byTitle = store.List()
            .Where(summary => summary.Title.Length > 0
                              && string.Equals(summary.Title, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byTitle.Count == 1
            ? (byTitle[0].Id, byTitle[0].Title, byTitle[0].PendingApprovals > 0, byTitle[0].Archived)
            : null;
    }

    [Description("Find text inside the session workspace with a regular expression and get back "
                 + "path:line: content, ready to open with read_file at that offset. This is how to look around a "
                 + "project without running a shell command.")]
    public string SearchText(
        [Description("Regular expression matched per line. Characters that are not operators must be escaped.")]
        string pattern,
        [Description("Only search files this glob names: \"*.cpp\" matches that name at any depth, \"include/*.h\" "
                     + "matches one directory deep. Patterns are matched inside 'path', not from the workspace root. "
                     + "Empty searches every text file.")]
        string glob = "",
        [Description("Directory to search, relative to the workspace. Empty or \".\" means the whole workspace.")]
        string path = "",
        [Description("Match letters of either case.")] bool ignore_case = false,
        [Description("Stop after this many matches, up to 500.")] int max_matches = DefaultSearchMatches)
    {
        var directory = WorkspacePaths.ResolveDirectory(context.WorkspaceRoot, path, context.Guards);
        if (!directory.IsAllowed) return Refusal(directory.Verdict, directory.Relative, path);
        if (string.IsNullOrWhiteSpace(pattern))
            return "Refused: search_text needs a pattern. Say what to look for, or call list_directory to see the "
                   + "shape of the project. Do not retry it empty.";
        if (GlobMatcher(glob) is not { } wants)
            return $"Refused: '{glob}' is not a usable glob. Use a name pattern like \"*.cpp\", a path one like "
                   + "\"src/*.h\", or leave it empty to search every text file. Do not retry another spelling.";

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant
                                       | (ignore_case ? RegexOptions.IgnoreCase : RegexOptions.None));
        }
        catch (ArgumentException ex)
        {
            return $"Refused: '{pattern}' does not compile as a regular expression ({ex.Message}). Search for a "
                   + "literal word instead, or simplify it. Do not retry another spelling of the same pattern.";
        }

        var cap = Math.Clamp(max_matches, 1, MaxSearchMatches);
        var hits = new List<string>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var searched = 0;
        var walked = 0;
        var unreadable = 0;

        foreach (var file in WalkFiles(directory.Full, MaxScannedFiles))
        {
            walked++;
            // The glob filters inside the directory that was asked for; the path printed is the one read_file
            // takes, which is the workspace-relative one. Those differ as soon as path is not the root.
            var within = RelativeOf(directory.Full, file);
            if (!wants(within) || !WorkspacePaths.IsTextFile(Path.GetFileName(file))) continue;
            searched++;
            if (new FileInfo(file).Length > MaxSearchFileBytes || ReadAll(file) is not { } text)
            {
                unreadable++;
                continue;
            }

            var shown = WorkspaceRelativeOf(directory, file);
            var lines = SplitLines(text);
            for (var line = 0; line < lines.Count; line++)
            {
                if (!regex.IsMatch(lines[line])) continue;
                hits.Add($"{shown}:{line + 1}: {Clipped(lines[line])}");
                matched.Add(shown);
                if (hits.Count >= cap) break;
            }

            if (hits.Count >= cap) break;
        }

        var scope = $"{(string.IsNullOrWhiteSpace(glob) ? "any text file" : $"files matching {glob}")}"
                    + $" under {LabelOf(directory)}";
        if (hits.Count == 0)
            return $"search_text · {pattern} · {scope}\nNo match in the {searched} file(s) searched."
                   + (unreadable > 0 ? $" {unreadable} file(s) were too large or not UTF-8 text." : "")
                   + (walked >= MaxScannedFiles ? $" The walk stopped at {MaxScannedFiles} files." : "")
                   + " Try a shorter pattern, or widen path. Do not retry this one unchanged.";

        var builder = new StringBuilder($"search_text · {pattern} · {scope}\n")
            .AppendJoin('\n', hits)
            .Append($"\n{hits.Count} match(es) in {matched.Count} file(s) of the {searched} searched.");
        if (unreadable > 0) builder.Append($" {unreadable} file(s) were skipped as too large or not UTF-8 text.");
        if (hits.Count >= cap)
            builder.Append($" The answer was cut at {cap} matches — narrow with glob or path, do not ask again the same way.");
        else if (walked >= MaxScannedFiles)
            builder.Append($" The walk hit its {MaxScannedFiles}-file limit, so a deeper directory may hold more.");
        return builder.ToString();
    }

    [Description("List a directory in the session workspace — subdirectories first, then files with their size — "
                 + "so the shape of a project is visible without running a shell command.")]
    public string ListDirectory(
        [Description("Directory to list, relative to the workspace. Empty or \".\" means the workspace root.")]
        string path = "",
        [Description("How many levels to include, 1 to 4. Depth costs output, so ask for one and repeat.")]
        int depth = DefaultListDepth)
    {
        var directory = WorkspacePaths.ResolveDirectory(context.WorkspaceRoot, path, context.Guards);
        if (!directory.IsAllowed) return Refusal(directory.Verdict, directory.Relative, path);

        var levels = Math.Clamp(depth, 1, MaxListDepth);
        var builder = new StringBuilder();
        var shown = 0;
        var dirs = 0;
        var files = 0;
        var skipped = 0;
        var truncated = false;
        // Flat workspace-relative paths, not an indented tree: the point of listing is to name something the next
        // call can open, and "  note.md" under a header is a path no tool takes.
        var fromRoot = directory.Relative.Length == 0
            ? ""
            : directory.Relative.Replace(Path.DirectorySeparatorChar, '/') + "/";

        void Walk(string folder, string prefix, int level)
        {
            foreach (var child in DirectoriesOf(folder))
            {
                if (IsExcluded(child)) { skipped++; continue; }
                if (shown >= MaxListedEntries) { truncated = true; return; }
                var name = prefix + Path.GetFileName(child);
                dirs++;
                shown++;
                builder.Append('\n').Append(name).Append('/');
                if (level < levels) Walk(child, name + "/", level + 1);
                if (truncated) return;
            }

            foreach (var child in FilesOf(folder))
            {
                if (shown >= MaxListedEntries) { truncated = true; return; }
                files++;
                shown++;
                builder.Append('\n').Append(prefix).Append(Path.GetFileName(child))
                    .Append("  ").Append(SizeOf(child));
            }
        }

        Walk(directory.Full, fromRoot, 1);
        var head = $"list_directory · {LabelOf(directory)} · depth {levels} · {dirs} dir(s), {files} file(s)";
        if (truncated) head += $" (cut at {MaxListedEntries} entries)";
        if (skipped > 0) head += $" · {skipped} build/vendored dir(s) not entered";
        return builder.Length == 0 ? head + "\n(that directory is empty)" : head + builder;
    }

    [Description("Find files by glob and return their paths without reading them. Use this when the name is known "
                 + "but the directory is not.")]
    public string FindFiles(
        [Description("Glob to match: \"*.cpp\", \"CMakeLists.txt\", \"settings*.json\". A glob with no '/' matches "
                     + "the file name at any depth; one with '/' matches inside 'path'.")] string glob,
        [Description("Directory to search from, relative to the workspace. Empty or \".\" means the whole workspace.")]
        string path = "")
    {
        var directory = WorkspacePaths.ResolveDirectory(context.WorkspaceRoot, path, context.Guards);
        if (!directory.IsAllowed) return Refusal(directory.Verdict, directory.Relative, path);
        if (GlobMatcher(glob) is not { } wants)
            return $"Refused: '{glob}' is not a usable glob. Use a name pattern like \"*.cpp\", or a path one like "
                   + "\"src/*.h\". Do not retry another spelling.";

        var found = new List<string>();
        var walked = 0;
        foreach (var file in WalkFiles(directory.Full, MaxScannedFiles))
        {
            walked++;
            var within = RelativeOf(directory.Full, file);
            if (wants(within)) found.Add(WorkspaceRelativeOf(directory, file));
            if (found.Count >= MaxFoundFiles) break;
        }

        if (found.Count == 0)
            return $"find_files · {glob} · under {LabelOf(directory)}\nNo file matched in the {walked} file(s) "
                   + "walked. Try the name without its extension, or call list_directory to see what is there.";

        var builder = new StringBuilder($"find_files · {glob} · under {LabelOf(directory)}\n")
            .AppendJoin('\n', found)
            .Append($"\n{found.Count} file(s).");
        if (found.Count >= MaxFoundFiles) builder.Append(" Cut at the limit — narrow with path.");
        return builder.ToString();
    }

    /// <summary>Builds a matcher for one glob, or null when the glob is empty (no filter) or cannot be used.
    /// A glob with no '/' matches the file name at any depth, because that is what a model means by
    /// <c>"*.cpp"</c>; one containing '/' matches the workspace-relative path, where <c>**</c> crosses directories
    /// and <c>*</c> does not. Translated to one regex rather than handed to a framework matcher so the rule fits
    /// in this method and is assertable from the command line.</summary>
    private static Func<string, bool>? GlobMatcher(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) return _ => true;
        var wanted = glob.Replace('\\', '/');
        var byName = !wanted.Contains('/', StringComparison.Ordinal);
        var builder = new StringBuilder("^");
        for (var index = 0; index < wanted.Length; index++)
        {
            var c = wanted[index];
            if (c == '*')
            {
                var deep = index + 1 < wanted.Length && wanted[index + 1] == '*';
                if (!deep) { builder.Append("[^/]*"); continue; }
                index++;
                // "**/" must also match nothing, or "**/*.h" would never find a file at the top of the tree.
                if (index + 1 < wanted.Length && wanted[index + 1] == '/')
                {
                    builder.Append("(?:.*/)?");
                    index++;
                }
                else builder.Append(".*");
                continue;
            }

            if (c == '?') { builder.Append("[^/]"); continue; }
            if (".+^$()[]{}|=&#~@".Contains(c, StringComparison.Ordinal)) builder.Append('\\');
            builder.Append(c);
        }

        builder.Append('$');
        try
        {
            var regex = new Regex(builder.ToString(), RegexOptions.CultureInvariant);
            return byName
                ? relative => regex.IsMatch(Path.GetFileName(relative))
                : relative => regex.IsMatch(relative);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Every file under a directory, up to <paramref name="limit"/>, sorted so one query always returns
    /// the same order, shallowest first — a question about a project is usually about its own files, not about
    /// whatever it pulled in. Build and vendored directories are not entered and a link is not followed: an engine
    /// tree is full of both, and walking them would spend the limit on other people's code.</summary>
    private static IEnumerable<string> WalkFiles(string root, int limit)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var yielded = 0;
        while (pending.Count > 0 && yielded < limit)
        {
            var folder = pending.Dequeue();
            foreach (var child in DirectoriesOf(folder))
            {
                if (IsExcluded(child)) continue;
                pending.Enqueue(child);
            }

            foreach (var file in FilesOf(folder))
            {
                if (++yielded > limit) yield break;
                yield return file;
            }
        }
    }

    private static List<string> DirectoriesOf(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder)
                .Where(child => !IsLink(child))
                .OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> FilesOf(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(child => !IsLink(child))
                .OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A link is not followed and not listed: the read guard resolves ancestors, so a contained walk
    /// that stepped through one would be reporting files outside the sandbox the user chose. One unreadable
    /// attribute is that entry's problem, not a reason to drop the whole directory.</summary>
    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsExcluded(string folder)
        => WorkspacePaths.IsExcludedDirectory(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)));

    private static string SizeOf(string file)
    {
        try
        {
            var bytes = new FileInfo(file).Length;
            return bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KiB";
        }
        catch (IOException)
        {
            return "?";
        }
    }

    private static string RelativeOf(string root, string full)
        => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>A found file as the next call needs it. read_file resolves against the workspace root, so a scoped
    /// search still has to print <c>probe/app.cpp</c>: the bare <c>app.cpp</c> is true inside the directory that
    /// was searched and a dead end everywhere else.</summary>
    private static string WorkspaceRelativeOf(WorkspacePath directory, string file)
    {
        var inside = RelativeOf(directory.Full, file);
        return directory.Relative.Length == 0
            ? inside
            : directory.Relative.Replace(Path.DirectorySeparatorChar, '/') + "/" + inside;
    }

    private static string LabelOf(WorkspacePath directory)
        => directory.Relative.Length > 0 ? $"'{directory.Relative}/'" : "the workspace root";

    private static string Clipped(string line)
    {
        var text = line.Trim();
        return text.Length <= MaxSearchLineCharacters ? text
            : text[..MaxSearchLineCharacters] + "…";
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
            // The log lives in Hub's data directory, which is a protected root: no tool of this one can read it
            // back. Naming it is still worth the line, because the person reading the transcript can open it.
            text += $"\n(the whole output is in Hub's run log at {logPath}, which the user can open and the "
                    + "assistant cannot read)";
        return text;
    }
}
