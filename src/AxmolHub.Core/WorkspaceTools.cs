using System.ComponentModel;
using System.Security.Cryptography;
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
/// <param name="ApplyGrantedRoot">Persists one directory the person just approved on a <c>fs_request_access</c>
/// card and answers the model. The seam exists because the tool body is in Core and cannot reach a session
/// registry, and a check that only exercises the guard passes null.</param>
/// <param name="ApplyGrantedFile">The same seam for a <c>fs_request_access</c> card: one file, persisted on the session
/// as one exact path, answered with what the model may do with that file and nothing beside it.</param>
/// <param name="Sessions">Where the other sessions live, for the cross-session tools. Null in a scope that has no
/// session store to read — those tools then say so instead of inventing a peer.</param>
/// <param name="CrossSession">Live run state and the write path for another session, both of which belong to the
/// app. Null when nothing can answer for a peer.</param>
/// <param name="Screen">The host's capture mechanics. Null on a host this build has not taught to draw a frame,
/// which the tool says out loud rather than returning a black picture.</param>
/// <param name="RecordFrames">Where a captured frame is handed to the app so the result turn can carry it. The
/// turn is written by the app, not by this class, and the call id never reaches a tool body — so the frame goes
/// out through the request's own scope, which is one request and one conversation by construction.</param>
/// <param name="Projects">The directories Hub also knows as registered Axmol game projects, so a workspace map can
/// state that attribute. Null in a build with no snapshot, which the map reads as "nothing extra is known" rather
/// than as "this is not a project" — the two are different claims and only one of them is this build's to make.</param>
/// <param name="Web">The host's side of an outbound fetch: the setting that allows it and the socket that makes it.
/// Null in a scope with nothing to send with — <c>web_fetch</c> then says so out loud rather than pretending the
/// address was bad, which is the only way a zero-network self-check and a real Hub answer the same question.</param>
/// <param name="TakeChangeGate">The host's one-change-at-a-time gate, asked for the duration of a command that may
/// change state. Null in a scope with no queue to share — a self-check, or a tool run outside a window. The host
/// answers null when something is already changing (a build, an engine install) or when another assistant command
/// holds it, and a command that could not be given the gate is refused rather than run anyway: what is protected is
/// the disk, not the schedule.</param>
/// <param name="RecordChangeLines">Where a landed write hands over how many lines it moved, so the app can stamp
/// that on the call's own turn. The same reason <paramref name="RecordFrames"/> exists: a tool body never learns its
/// call id, and the turn is written by the app. Reported only after the bytes are on disk, which is what makes the
/// number a fact about a change that happened rather than about an edit that was attempted.</param>
/// <param name="AskAboutStalledProcess">The question the host puts to the user when a command goes silent while it
/// is still running, given the live process and the command line the card has to name. Hub does not end a process on
/// its own initiative — keeping it, leaving it alone, or stopping it and running again is the person's call, and this
/// is where that call is asked for and answered. Null in a scope with nobody to ask (a self-check, a tool run outside
/// a window), and a stall on that road still ends the process, because "nobody is watching" cannot also mean
/// "nothing is stopping it".</param>
/// <param name="RecordCommandOutput">One line of the command now printing, handed over as it arrives so the
/// transcript card can show a long build's progress instead of a command that has said nothing for ten minutes.
/// Display-only and never replayed: the model still reads one whole result where the call ends. It is the same
/// redacted text the log got, and the app decides where it lands — a tool body has no call id to name.</param>
/// <param name="RecordCommandExit">The exit code the command ended with, reported where it is known. Reported
/// before the result text is composed and outside it, because a caller that paints a row needs the number as a
/// fact rather than as the tail of a sentence it would have to parse back out.</param>
public sealed record WorkspaceToolScope(
    string? WorkspaceRoot,
    WorkspaceGuards Guards,
    string? DataRoot,
    string ConversationId,
    IReadOnlyList<string> SensitiveValues,
    HubLog? Log,
    ConversationStore? Sessions = null,
    CrossSessionBridge? CrossSession = null,
    ScreenCaptureBridge? Screen = null,
    Action<IReadOnlyList<ChatImage>>? RecordFrames = null,
    WebBridge? Web = null,
    IReadOnlyList<WorkspaceProject>? Projects = null,
    Func<string, IDisposable?>? TakeChangeGate = null,
    Action<ChangeLineCounts>? RecordChangeLines = null,
    Func<StalledProcess, string, Task<StalledProcessFate>>? AskAboutStalledProcess = null,
    // The command's lines as they arrive, and the code it ended with. Both are for the card the transcript grows
    // while a command runs — the model still reads one whole result where the call ends — and both leave through
    // the scope rather than the result for the same reason the frames do: a tool body never learns its own call
    // id, so the app attributes what arrives here to the call it recorded as running.
    Action<string>? RecordCommandOutput = null,
    Action<int>? RecordCommandExit = null,
    // Last for a mechanical reason worth stating: this record is built positionally by a few dozen assertions, and a
    // parameter in the middle would silently shift every one of them. Only the app builds it by hand.
    Func<string, Task<string>>? ApplyGrantedRoot = null,
    Func<int, string, string>? UpdatePlanTask = null,
    DeepSeekWebApiBridge? DeepSeekWeb = null,
    // The second door onto a grant, and the narrower one: one file rather than one place. Its own seam because a
    // session keeps the two apart — a file grant has no folder behind it — and because the answer the model reads
    // has to say exactly that.
    Func<string, Task<string>>? ApplyGrantedFile = null)
{
    internal ComputerUseBridge? ComputerUse { get; init; }
    /// <summary>Returns this call's frozen target only while its application grant remains in force.</summary>
    public Func<ComputerUseTarget?>? ApprovedComputerTarget { get; init; }
    /// <summary>Fingerprint of the file shown on a parked fs_write approval card; null on an automatic call.</summary>
    public string? ApprovedWriteFingerprint { get; init; }
    /// <summary>Keep a batch journal locked until the host has persisted its tool result.</summary>
    public Action<string, IDisposable>? HoldBatchLease { get; init; }
    public string? ApprovedAccessKind { get; init; }
    public bool ApprovedCacheBreak { get; init; }
    /// <summary>A scope with nothing in it. Every file and command tool answers
    /// <see cref="WorkspacePathVerdict.NoWorkspace"/> rather than guessing a directory.</summary>
    public static WorkspaceToolScope Empty { get; } = new(null, new WorkspaceGuards(null, []), null, "", [], null);

    /// <summary>
    /// Every directory this request may touch: the session's workspace plus the directories a person granted it.
    /// Filled in by the app from the session's own record — so an approval granted before a restart applies to the
    /// same directories afterwards — and defaulted from <see cref="WorkspaceRoot"/> so a scope an assertion builds
    /// by naming one directory means exactly what it says. Every tool body reads it instead of the root, and so do
    /// the previews: a card and the call it describes are graded against one list.
    /// </summary>
    public WorkspaceSandbox Sandbox { get; init; } = WorkspaceSandbox.For(WorkspaceRoot);
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
public sealed partial class WorkspaceTools(WorkspaceToolScope context)
{
    public const int MaxReadFileBytes = 256 * 1024;
    public const int DefaultReadLimit = 400;
    public const int MaxReadLimit = 2000;
    public const int DefaultCommandTimeoutSeconds = 300;
    public const int MaxCommandTimeoutSeconds = 900;

    /// <summary>The one tool whose work is a command line, and therefore the one whose call grows a card while it
    /// prints and reports an exit code when it ends. Named here rather than quoted at each reader: the tier table,
    /// the card and the exit-code channel all have to be talking about the same name.</summary>
    public const string RunCommandTool = ToolNames.RunCommand;

    /// <summary>Sessions listed before the answer is cut, newest first — enough to pick a peer, short enough not
    /// to be the majority of a context window.</summary>
    public const int MaxListedSessions = 20;
    public const int DefaultReadTurns = 20;
    public const int MaxReadTurns = 60;
    public const int MaxTurnCharacters = 600;

    /// <summary>The newline of every tool result. It is the newline <c>fs_replace</c> puts in the files those
    /// results describe, so a transcript replayed from another host reads the same.</summary>
    private const string Lf = "\n";

    /// <summary>A peer message goes into another session's context and stays there. A limit that generous is what
    /// lets one assistant bury another.</summary>
    public const int MaxMessageCharacters = 4000;

    [Description("Update the status of a step in the approved plan shown above the composer. Use before starting and after completing each step.")]
    public string UpdatePlanTask(
        [Description("One-based task number from the approved plan.")] int task_number,
        [Description("New status: pending, in_progress, or completed.")] string status)
        => context.UpdatePlanTask?.Invoke(task_number, status)
           ?? "There is no active approved plan task list in this session.";

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

    /// <summary>One file a search will open. Bigger than this is a vendored bundle rather than a source file or a
    /// log — but a log is allowed to be big, which is why this is megabytes and not a hundred kilobytes.</summary>
    public const long MaxSearchFileBytes = 8 * 1024 * 1024;

    /// <summary>How many bytes one call may open in total. The file count bounds the walking, not the reading: eight
    /// logs of eight megabytes are a minute of scanning, and one call has no business spending it. A budget says
    /// "this answer was cut" rather than leaving a short result to be read as "there is nothing there".</summary>
    public const long MaxSearchBytes = 64 * 1024 * 1024;

    public const int DefaultListDepth = 1;
    public const int MaxListDepth = 4;
    public const int MaxListedEntries = 300;
    public const int MaxFoundFiles = 500;

    [Description("Read a text file from the session workspace, or from a directory the user granted this session, "
                 + "or one single file the user granted with fs_request_access. "
                 + "Lines come back without numbering so they can be copied verbatim into fs_replace.")]
    public string ReadFile(
        [Description("Path relative to the session workspace, for example src/main.cpp, or an absolute path inside "
                     + "a directory or to a single file the user granted this session. A relative path must not "
                     + "contain '..', a drive letter or a leading separator.")]
        string path,
        [Description("First line to return, counting from 1.")] int offset = 1,
        [Description("How many lines to return at most, up to 2000.")] int limit = DefaultReadLimit)
    {
        var resolved = WorkspacePaths.ResolveRead(context.Sandbox, path, context.Guards);
        if (!resolved.IsAllowed) return Refusal(resolved.Verdict, resolved.Display, path);

        long size;
        try
        {
            size = new FileInfo(resolved.Full).Length;
        }
        catch (IOException ex)
        {
            return $"Could not read {resolved.Display}: {ex.Message}";
        }

        // The cap is about what one result may cost, not about which files are readable: a 356 KiB source file used
        // to be refused outright, with advice — "read a window of it with offset and limit" — that this very check
        // made impossible to follow. A file too big to decode whole is streamed instead, and the cap bounds the
        // window that comes back.
        if (size > MaxReadFileBytes) return WindowOf(resolved.Display, resolved.Full, offset, limit);

        if (ReadAll(resolved.Full) is not { } text) return NotText(resolved.Display);

        var lines = SplitLines(text);
        var from = Math.Clamp(offset, 1, Math.Max(1, lines.Count));
        var count = Math.Clamp(limit, 1, MaxReadLimit);
        var window = lines.Skip(from - 1).Take(count).ToList();
        var last = from - 1 + window.Count;
        var header = $"{resolved.Display} — lines {from}-{last} of {lines.Count} (UTF-8)";
        if (window.Count == 0) return $"{header}{Lf}(that window is empty)";

        var builder = new StringBuilder(header);
        foreach (var line in window) builder.Append('\n').Append(line);
        if (last < lines.Count)
            builder.Append($"\n… ({lines.Count - last} more lines; read again with offset={last + 1})");
        return builder.ToString();
    }

    [Description("Edit or create one text file in the session workspace, in a directory the user granted this "
                 + "session, or in a single file granted with fs_request_access — by replacing an exact string. The file "
                 + "is left untouched unless old_string matches, so a wrong anchor is a refusal rather than damage.")]
    public string FileWrite(
        [Description("Path relative to the session workspace, or an absolute path inside a directory the user "
                     + "granted this session, or to a single file granted with fs_request_access (such a file is "
                     + "read-only when it sits in a protected location or is not a text file type the assistant may "
                     + "write, and this call then refuses it).")]
        string path,
        [Description("The exact text to replace, copied verbatim from fs_read including indentation and line "
                     + "breaks. Leave empty only to create a file that does not exist yet.")]
        string old_string,
        [Description("The text that takes old_string's place.")] string new_string,
        [Description("Replace every occurrence instead of refusing when old_string matches more than one place.")]
        bool replace_all = false)
        => WriteText(path, original => FileEdit.Apply(original, old_string, new_string, replace_all));

    [Description("Create a text file or replace its entire contents. Set overwrite to true only when the existing "
                 + "file should be replaced. For localized changes use fs_replace.")]
    public string FsWrite(
        [Description("Path in the session workspace or a granted writable directory.")] string path,
        [Description("The complete new text of the file.")] string content,
        [Description("Explicitly allow replacing an existing file.")] bool overwrite = false)
    {
        if (Encoding.UTF8.GetByteCount(content ?? "") > 256L * 1024 * 1024)
            return "Refused: the replacement exceeds the 256 MiB limit.";
        using var gate = context.TakeChangeGate?.Invoke(ToolNames.FsWrite);
        if (context.TakeChangeGate is not null && gate is null) return ToolApprovalResults.Busy;
        return WriteText(path, original => FileEdit.Overwrite(original, content, overwrite),
            context.ApprovedWriteFingerprint, wholeFile: true);
    }

    private string WriteText(string path, Func<string?, FileEditResult> makeEdit, string? approvedFingerprint = null,
        bool wholeFile = false)
    {
        var resolved = WorkspacePaths.ResolveWrite(context.Sandbox, path, context.Guards);
        if (!resolved.IsAllowed) return Refusal(resolved.Verdict, resolved.Display, path);

        using var write = TextFile.AcquireWrite(resolved.Full);
        // Resolve again after waiting: another Hub writer or a permission change may have intervened.
        var permitted = WorkspacePaths.ResolveWrite(context.Sandbox, path, context.Guards);
        if (!permitted.IsAllowed || permitted.Full != resolved.Full)
            return $"Refused: the permitted path changed before writing {resolved.Display}. Read it again.";

        string? original = null;
        byte[]? originalBytes = null;
        if (File.Exists(resolved.Full))
        {
            try
            {
                if (new FileInfo(resolved.Full).Length > 256L * 1024 * 1024)
                    return "Refused: the file exceeds the 256 MiB backup limit.";
                originalBytes = ReadBoundedBytes(resolved.Full, 256L * 1024 * 1024);
                using var memory = new MemoryStream(originalBytes);
                using var reader = new StreamReader(memory, new UTF8Encoding(false, true), true);
                original = reader.ReadToEnd();
            }
            catch (DecoderFallbackException) { return NotText(resolved.Display); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Could not read {resolved.Display}: {ex.Message}";
            }
        }

        if (approvedFingerprint is not null
            && !string.Equals(approvedFingerprint, Fingerprint(resolved.Full, originalBytes), StringComparison.Ordinal))
            return $"Refused: {resolved.Display} changed since approval. Review the operation again.";

        var edit = makeEdit(original);
        if (!edit.Changed)
            return wholeFile && edit.Verdict == FileEditVerdict.Unchanged
                ? $"No change: {resolved.Display} already has that content."
                : wholeFile && edit.Verdict == FileEditVerdict.AlreadyExists
                ? $"Refused: {resolved.Display} already exists. Pass overwrite=true to replace it."
                : FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Display);

        ChatFileRevision revision;
        try
        {
            revision = ChatChangeStore.Prepare(context.DataRoot, context.ConversationId, resolved.Full,
                resolved.Root, original, edit.Updated, originalBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Refused: could not save complete before/after snapshots for {resolved.Display}: {ex.Message}";
        }

        var replaced = write.Replace(() => WorkspacePaths.ResolveWrite(context.Sandbox, path, context.Guards),
            originalBytes, edit.Updated);
        if (replaced.Verdict != TextReplaceVerdict.Written)
        {
            ChatChangeStore.Abandon(context.DataRoot, revision);
            return replaced.Verdict switch
            {
                TextReplaceVerdict.RefusedPath => $"Refused: the permitted path changed before writing {resolved.Display}. Read it again.",
                TextReplaceVerdict.ChangedSince => $"No change: {resolved.Display} changed after it was read. Read it again before editing.",
                _ => $"Could not write {resolved.Display}: {replaced.Detail}"
            };
        }

        string journalWarning = "";
        try { ChatChangeStore.CompleteRevision(context.DataRoot, revision); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The pending journal and both objects were durable before the source rename. Keep its ID so the
            // interrupted completion can be recovered without inventing a pre-image from the current workspace.
            journalWarning = $" (change journal pending: {ex.Message})";
        }

        // After the bytes are on disk and nowhere earlier: a refused anchor, a sandbox refusal and a failed write
        // all changed nothing, and a tally reported for one of them would be a number for a change that never
        // happened. Reuse the revision's complete measurement, including its uncertainty; counting again would
        // duplicate the comparison, and reading the capped preview would lose changes.
        context.RecordChangeLines?.Invoke(revision.Lines!);

        context.Log?.Write($"Assistant wrote {resolved.Full} ({edit.Verdict}, {edit.Matches} place(s))");
        return FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Display)
               + ChatChangeStore.Marker + revision.Id + journalWarning;
    }

    /// <summary>Capture the file identity for a parked full-write call. Invalid paths fail closed.</summary>
    public static string WriteFingerprint(WorkspaceToolScope scope, string path)
    {
        var resolved = WorkspacePaths.ResolveWrite(scope.Sandbox, path, scope.Guards);
        if (!resolved.IsAllowed) return "invalid";
        try
        {
            if (File.Exists(resolved.Full) && new FileInfo(resolved.Full).Length > 256L * 1024 * 1024)
                return "invalid";
            var bytes = File.Exists(resolved.Full) ? ReadBoundedBytes(resolved.Full, 256L * 1024 * 1024) : null;
            return Fingerprint(resolved.Full, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return "invalid"; }
    }

    private static string Fingerprint(string path, byte[]? bytes)
    {
        var identity = path + "\n" + (bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes)));
        if (bytes is not null)
        {
            identity += "\n" + (int)File.GetAttributes(path);
            if (!OperatingSystem.IsWindows()) identity += "\n" + (int)File.GetUnixFileMode(path);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static byte[] ReadBoundedBytes(string path, long limit)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > limit) throw new IOException("The file exceeds the backup limit.");
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - output.Length + 1))) > 0)
        {
            if (output.Length + count > limit) throw new IOException("The file grew beyond the backup limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    [Description("Run one shell command in the session workspace, or in a directory the user granted this session, "
                 + "and return its output. On Windows the shell is PowerShell 7 when it is installed and Windows "
                 + "PowerShell otherwise; elsewhere pwsh, or /bin/sh when pwsh is not installed.")]
    public async Task<string> RunCommand(
        [Description("A single command line, for example: cmake --build build --config Debug")] string command,
        [Description("How many seconds of silence make Hub stop waiting and ask the user what to do with the "
                     + "command — up to 900. It is not a total time limit and it does not end the process: a build "
                     + "that keeps printing may run far longer than this, and a quiet one is left running until the "
                     + "user says otherwise.")]
        int timeout_seconds = DefaultCommandTimeoutSeconds,
        [Description("Directory to run in: relative to the session workspace, an absolute path inside a directory "
                     + "the user granted this session, or empty for the workspace itself.")]
        string cwd = "",
        CancellationToken cancellationToken = default)
    {
        var directory = WorkspacePaths.ResolveDirectory(context.Sandbox, cwd, context.Guards);
        if (!directory.IsAllowed) return Refusal(directory.Verdict, directory.Display, cwd);
        if (string.IsNullOrWhiteSpace(command)) return "Refused: the command is empty. Do not retry it.";

        // Anything but a git read holds Hub's one-change-at-a-time gate for its duration. What is being kept apart
        // is not a slow command but two of them writing one output directory: a build interrupted or doubled leaves
        // a tree half new, and no later card can undo that. A git *read* is exempt for two reasons — it cannot
        // touch a build tree, and the exemption is what keeps the gate cheap, since the model reads the repository
        // constantly and every `git status` would otherwise put a few hundred milliseconds between the person and
        // their own build button. Note this is the gate, not the concurrency class: a git read is excluded from
        // both, for different reasons (see <see cref="ToolConcurrency"/>).
        IDisposable? gate = null;
        if (context.TakeChangeGate is { } take && GitCommandGrades.Grade(command) != GitGrade.GitRead)
        {
            gate = take("run_command");
            if (gate is null) return ToolApprovalResults.Busy;
        }

        try
        {
            return await RunInShellAsync(command, directory.Full, timeout_seconds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Released on every road out, the cancellation included: a run the person stopped must not leave the
            // gate held, or Hub would report itself busy with nothing running.
            gate?.Dispose();
        }
    }

    /// <summary>One shell run in <paramref name="cwd"/>, which the guard has already resolved — the workspace by
    /// default, or a directory the person granted when the call named one. The directory is a parameter rather
    /// than a field read here for one reason: the retry road of a stalled command has to land in the same place.</summary>
    private async Task<string> RunInShellAsync(string command, string cwd, int timeout_seconds,
        CancellationToken cancellationToken)
    {
        var shell = CommandShells.ForCurrent();
        var seconds = Math.Clamp(timeout_seconds, 1, MaxCommandTimeoutSeconds);
        var header = $"shell: {shell.Label} · cwd: {cwd}";
        // A host that can ask is the only host that gets to leave a process alive: the runner's stall road forks on
        // this, so a scope with no seam behaves exactly as it always did and no command is ever left running with
        // nobody able to decide about it.
        var runner = new ProcessRunner(line => context.Log?.Write(line))
        {
            AdoptOnIdle = context.AskAboutStalledProcess is not null,
        };

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(
                shell.Executable, shell.ArgumentsFor(command), cwd,
                timeout: TimeSpan.FromSeconds(seconds),
                sensitiveValues: context.SensitiveValues,
                // The same redacted lines the log gets, handed out as they land: the card in the transcript is the
                // only surface a long build has while it prints, and reading them back off the finished result
                // would be the one thing that arrives after the wait it was supposed to fill.
                onLine: line => context.RecordCommandOutput?.Invoke(line),
                cancellation: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user stopped the run. Surfacing it as a tool result would answer a cancellation with more text.
            throw;
        }
        catch (StalledProcessException alive)
        {
            return await DecideStalledAsync(command, cwd, header, alive, cancellationToken).ConfigureAwait(false);
        }
        catch (IdleTimeoutException stalled)
        {
            // Going quiet is not the same as having nothing to say. The lines before the silence are the diagnosis
            // — a build that hangs has already printed the error it stopped on — so they come back with the result,
            // head-and-tail truncated like any other output.
            var builder = new StringBuilder($"{header}{Lf}The command was killed after {seconds}s "
                                            + "of no output. What it printed before going silent:")
                .Append(Lf).Append(Shaped(stalled.Output, stalled.Error));
            if (stalled.SurvivedKill)
                builder.Append(Lf)
                    .Append("It was still running after Hub gave up on killing it, so it may still hold files in "
                            + "the workspace.");
            if (BuildCommandGrades.IsBuild(command))
                // The one place the old sentence was a lie. "Do not retry the same command unchanged" told a model
                // that a changed flag had earned it another run, and a second compile beside the one Hub just
                // stopped is how a build tree ends half-written: killing the tree does not reach the detached
                // MSBuild nodes, VBCSCompiler or the dotnet build server, which outlive this process by design.
                // What the model owes here is not a cleverer command line, it is a question.
                builder.Append(Lf)
                    .Append("This was a build, and build services detach from the process tree Hub killed, so "
                            + "compilation may still be going under the hood. Do not retry it with different flags "
                            + "and do not clean anything: tell the user what it printed and ask how to proceed.");
            else
                builder.Append(Lf)
                    .Append("timeout_seconds counts silence, not total runtime, so raise it only if the command "
                            + "really stopped printing. Do not retry the same command unchanged.");
            return builder.ToString();
        }
        catch (Exception ex)
        {
            return $"{header}{Lf}The command could not start: {ex.Message}";
        }

        // Reported before the body is composed, and only on this road: every other ending above is a decision about
        // a process rather than the process's own exit, and a number invented for one of them would be a claim
        // about how a command ended that nobody observed.
        context.RecordCommandExit?.Invoke(result.ExitCode);
        var output = Shaped(result.Output, result.Error);
        var body = $"{header} · exit: {result.ExitCode}{Lf}{output}";
        if (result.ExitCode != 0 && BuildCommandGrades.IsBuild(command))
            // Only for a build: a failing `ls` does not need a note, and a note attached to everything gets skimmed
            // for the one case where it is true. Reading the compiler and building again is the work — the sentence
            // exists for the one way out of a failed build that destroys something a later approval cannot put back,
            // which is deleting what is already there.
            return body + Lf
                   + "The build failed, which can leave the tree half new: outputs from before this run and outputs "
                   + "from it are now mixed. Fix what the compiler reported and build again as usual; do not clear "
                   + "or delete the build directory to get around it — that is the part the user has to approve.";
        return body;
    }

    /// <summary>
    /// What happens to a command that stopped producing output while it is still running, now that Hub does not end
    /// it on its own initiative. The question went out through the host; this acts on the answer, and every branch
    /// here is a decision somebody made — which is the point of the whole road.
    ///
    /// <para>The handle stays alive on purpose, so the exit code is still readable when the process finishes: a
    /// command that ends while the question is open has an answer, not a guess. Dropping it (<c>Release</c>) never
    /// touches the process — that is what "leave it running" means.</para>
    /// </summary>
    private async Task<string> DecideStalledAsync(string command, string cwd, string header,
        StalledProcessException error, CancellationToken cancellationToken)
    {
        var stalled = new StalledProcess(error);
        var seconds = (int)stalled.SilentFor.TotalSeconds;

        // A question nobody needs must not be asked. The gap between the stall and this line is where a build that
        // was merely slow finishes, and a card about a finished command is a card with nothing left to decide.
        if (stalled.HasExited)
            return await FinishedQuietlyAsync(stalled, header, asked: false).ConfigureAwait(false);

        var builds = BuildCommandGrades.IsBuild(command);
        StalledProcessFate fate;
        try
        {
            fate = await context.AskAboutStalledProcess!(stalled, command).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The run itself was stopped. That is the user's word about this process too — "no more of this" — and
            // leaving it running behind a cancelled run would be the invisible half of exactly what they stopped.
            stalled.Stop();
            stalled.Release();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nobody could be reached. A live process with no card, no watcher and no owner is the collision this
            // rule exists to prevent, so the fallback ends it — and says plainly that Hub could not ask, rather than
            // pretending an answer arrived.
            var survived = !stalled.Stop();
            var printed = stalled.Snapshot();
            stalled.Release();
            return $"{header}{Lf}The command went silent for {seconds}s and Hub could not reach "
                   + $"the user to ask about it ({ex.Message}), so it stopped the process."
                   + (survived ? " It was still running after the kill and may still hold files in the workspace." : "")
                   + Lf + Shaped(printed.Output, printed.Error)
                   + Lf + "Do not run it again or clear anything first; tell the user what happened.";
        }

        switch (fate)
        {
            case StalledProcessFate.Exited:
                // The host saw it finish and withdrew the question. Nothing was decided about a process that had
                // already gone, and the result says so — a person who reads 「用户选择」 here is being told a lie.
                return await FinishedQuietlyAsync(stalled, header, asked: true).ConfigureAwait(false);

            case StalledProcessFate.KeepWaiting:
                // No ceiling. The person said wait, so deciding when waiting has gone on long enough stops being
                // Hub's call to make; the run's own cancellation is the only thing that ends it early, and that is
                // another decision they will have made out loud.
                try
                {
                    await stalled.Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stalled.Stop();
                    stalled.Release();
                    throw;
                }

                var waited = await stalled.Drain(ProcessRunner.DrainGrace).ConfigureAwait(false);
                var waitText = FinishedResult(stalled, header, waited);
                return waitText + Lf
                       + $"The command went quiet for {seconds}s and the user chose to keep waiting rather than stop "
                       + "it. That wait is over; the result above is the whole run.";

            case StalledProcessFate.LeaveItRunning:
                {
                    // Let go of the handle, not the process. Hub also stops holding the change gate for it, which is
                    // what makes this answer dangerous and what makes the sentence below necessary rather than polite:
                    // the next build — the user's own or the model's — can collide with something Hub just walked away
                    // from, and nobody should discover that from a compiler error.
                    var left = stalled.Snapshot();
                    stalled.Release();
                    return $"{header}{Lf}The user chose not to wait for this command and not to stop "
                           + "it. It is still running, Hub is no longer watching it and no longer holds the change "
                           + "gate for it, so it may still be writing to the workspace. What it printed so far:"
                           + Lf + Shaped(left.Output, left.Error)
                           + (builds
                               ? Lf + "Before running another build or clearing anything, tell the "
                                 + "user this one may still be going and ask what they want."
                               : Lf + "Do not assume this command failed; it was left running on "
                                 + "purpose. Ask the user before repeating it.");
                }

            default:
                {
                    // Kill, then run the same command once more. The kill is the runner's own (tree, bounded wait,
                    // honest survival report); the retry is a fresh shell exactly as if the model had asked again,
                    // because that is what the user approved. A retry that goes quiet again asks again rather than
                    // looping here — every extra round costs a human click, which is what keeps it bounded.
                    var survived = !stalled.Stop();
                    stalled.Release();
                    var again = await RunInShellAsync(command, cwd, seconds, cancellationToken).ConfigureAwait(false);
                    return (survived
                        ? $"{header}{Lf}The user asked Hub to stop the silent command and run it "
                          + "again. It was still running after the kill, so the new run may be going alongside it; "
                          + "read the output below with that in mind."
                        : $"{header}{Lf}The user asked Hub to stop the silent command and run it "
                          + "again. The earlier run was ended first.")
                        + Lf + again;
                }
        }
    }

    /// <summary>The command finished without anybody deciding anything — either in the breath between the stall and
    /// the question, or while the question was open. Both roads take the second: the exit code and the full output
    /// are what the model was owed, and there is no choice to report.</summary>
    private async Task<string> FinishedQuietlyAsync(StalledProcess stalled, string header, bool asked)
    {
        var final = await stalled.Drain(ProcessRunner.DrainGrace).ConfigureAwait(false);
        var result = FinishedResult(stalled, header, final);
        return result + Lf
               + (asked
                    ? "The command finished on its own while Hub was asking what to do about the silence. Nothing was "
                      + "stopped, and no choice was made about it."
                    : "The command went quiet, then finished on its own before Hub had anything to ask about. Nothing "
                      + "was stopped.");
    }

    /// <summary>The ordinary result shape — shell, directory, exit code, head-and-tail output — reused for a stalled
    /// command that turned out to have finished, because the model is owed the same facts as for any other run. An
    /// exit code that cannot be read is reported as unknown rather than guessed at zero.</summary>
    private string FinishedResult(StalledProcess stalled, string header,
        (string Output, string Error) final)
        => $"{header} · exit: {(stalled.ExitCode.HasValue ? stalled.ExitCode.ToString() : "unknown (Hub could not read it)")}"
           + $"{Lf}{Shaped(final.Output, final.Error)}";

    public static string AccessKind(string? path, string? kind)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return "invalid";
        kind = string.IsNullOrWhiteSpace(kind) ? "auto" : kind.Trim().ToLowerInvariant();
        if (kind is not ("auto" or "file" or "directory")) return "invalid";
        if (Directory.Exists(path)) return kind is "auto" or "directory" ? "directory" : "invalid";
        if (File.Exists(path)) return kind is "auto" or "file" ? "file" : "invalid";
        return kind == "auto" ? "invalid" : kind;
    }

    [Description("Request access to one user-named absolute file or directory. Calling this tool is the request; "
                 + "the approval card decides it. A file grant never opens its parent directory. Protected "
                 + "locations and unsupported file types remain read-only. Never ask for permission in prose first: "
                 + "call this tool when the user named the path. Never guess a path.")]
    public Task<string> FsRequestAccess(
        [Description("Absolute path of the file or directory named by the user.")] string path,
        [Description("auto detects an existing path; use file or directory to identify a missing target.")] string kind = "auto")
    {
        var actual = AccessKind(path, kind);
        if (actual == "invalid")
            return Task.FromResult("Refused: provide an absolute existing path or specify kind=file or kind=directory for a missing target; the kind must match the target.");
        if (context.ApprovedAccessKind is { } expected && actual != expected)
            return Task.FromResult("Refused: the target type changed since approval. Request access again.");
        return actual == "directory" ? RequestDirectory(path) : RequestFile(path);
    }

    [Description("Ask the user to grant this session read and write access to one directory outside the "
                 + "workspace: another project, a reference checkout, a folder the task or the user's own words "
                 + "named. It shows one approval card, and approving it adds that directory for the rest of this "
                 + "session — alongside the workspace, not instead of it — after which fs_read, fs_replace, "
                 + "fs_list, fs_grep, fs_find, workspace_map and run_command's cwd all take its "
                 + "absolute paths. Never ask the user for permission in prose first: calling this tool *is* the "
                 + "request, and the card it shows *is* the approval, so a sentence that waits for the user to "
                 + "authorize you in words parks the task forever. If the user's own words already named the "
                 + "directory, call this tool now, once, and let the card decide; only a directory nobody has named "
                 + "yet is worth asking about first, and never one you guessed at. A protected location — Hub's own "
                 + "data directory, an engine installation, a .git directory — is granted for reading only, because "
                 + "no mode opens writing there; the card says so, and the directory has to be named itself, not "
                 + "covered by a grant on the folder above it.")]
    public async Task<string> RequestDirectory(
        [Description("Absolute path of the directory to grant, for example D:\\dev\\other-project")]
        string path)
    {
        var (verdict, full, detail) = WorkspacePaths.ClassifyGrant(context.Sandbox, context.Guards, path);
        switch (verdict)
        {
            case GrantVerdict.Granted or GrantVerdict.GrantedReadOnly:
                // One record, whichever door it came through: the app writes the session file and answers with the
                // sentence, and a build with nobody to persist to answers with that same sentence.
                return context.ApplyGrantedRoot is { } apply
                    ? await apply(full).ConfigureAwait(false)
                    : WorkspacePaths.GrantedResultFor(full, verdict == GrantVerdict.GrantedReadOnly);
            case GrantVerdict.NoPath:
                return "Refused: fs_request_access needs the absolute path of a directory. Do not retry it empty.";
            case GrantVerdict.NotAbsolute:
                return $"Refused: '{path}' is not an absolute path. Ask the user for the full directory. Do not retry "
                       + "a relative one.";
            case GrantVerdict.Unusable:
                return $"Refused: '{path}' is not a usable path ({detail}). Do not retry it.";
            case GrantVerdict.Missing:
                return $"Refused: '{full}' does not exist or is not a directory. Ask the user to confirm the path; do "
                       + "not guess another one.";
            case GrantVerdict.SameAsWorkspace:
                return $"'{full}' already is this session's workspace, so it needs no grant. Work in it with "
                       + "relative paths and do not retry this call.";
            case GrantVerdict.InsideWorkspace:
                return $"'{full}' is inside the session workspace, so a relative path already reaches it. Use one "
                       + "like src/main.cpp; do not retry this call.";
            case GrantVerdict.ContainsWorkspace:
                return $"Refused: '{full}' contains the session workspace '{detail}', so a grant would open more "
                       + "than the folder you asked about. Ask for the directory you actually need; do not retry "
                       + "this one.";
            case GrantVerdict.AlreadyGranted:
                return $"'{full}' is already granted to this session. Name it by absolute path in fs_read, "
                       + "fs_replace, fs_list or run_command's cwd; do not ask again.";
            case GrantVerdict.InsideGrant:
                return $"'{full}' is already inside '{detail}', which this session holds. Work there with absolute "
                       + "paths; do not ask again.";
            case GrantVerdict.ContainsGrant:
                return $"Refused: '{full}' contains '{detail}', which this session was granted on its own. Ask for "
                       + "the directory you actually need; a grant covers one folder and what is under it.";
            default:
                // SessionGone, which only the workspace menu's own row can see — a card is always answered inside a
                // session that exists. The sentence is the one ApplyGrantedRoot already answers when the record
                // fails, so the two doors describe a vanished session the same way.
                return "Refused: the session is gone, so nothing could be granted. Do not retry this call.";
        }
    }

    [Description("Ask the user to grant this session access to one single file outside the workspace: a log a "
                 + "diagnosis needs, a config in another checkout — the file the task or the user's own words "
                 + "named. One approval card opens that one file for the rest of this session, by absolute path in "
                 + "fs_read and fs_replace; its folder is not part of it, so listing, searching and a command's "
                 + "cwd still cannot reach it. A file inside a protected location (Hub's data directory, an engine "
                 + "installation, a .git) or outside the write allowlist is granted for reading only, and the card "
                 + "says so. Never ask the user for permission in prose first: calling this tool *is* the request, "
                 + "and the card it shows *is* the approval, so a sentence that waits for the user to authorize you "
                 + "in words parks the task forever. If the user's own words already named the file, call this tool "
                 + "now, once, and let the card decide, and never guess a path. When the task needs more than one "
                 + "file in a folder, ask for the folder with fs_request_access instead.")]
    public async Task<string> RequestFile(
        [Description("Absolute path of the single file to grant, for example D:\\dev\\other-project\\notes.md")]
        string path)
    {
        var (verdict, full, detail) = WorkspacePaths.ClassifyFileGrant(context.Sandbox, context.Guards, path);
        switch (verdict)
        {
            case GrantVerdict.Granted or GrantVerdict.GrantedReadOnly:
                return context.ApplyGrantedFile is { } apply
                    ? await apply(full).ConfigureAwait(false)
                    : WorkspacePaths.FileGrantedResultFor(full, verdict == GrantVerdict.GrantedReadOnly);
            case GrantVerdict.NoPath:
                return "Refused: fs_request_access needs the absolute path of one file. Do not retry it empty.";
            case GrantVerdict.NotAbsolute:
                return $"Refused: '{path}' is not an absolute path. Ask the user for the file's full path. Do not "
                       + "retry a relative one.";
            case GrantVerdict.Unusable:
                return $"Refused: '{path}' is not a usable path ({detail}). Do not retry it.";
            case GrantVerdict.IsDirectory:
                return $"Refused: '{full}' is a directory, and fs_request_access opens one file rather than the folder "
                       + "around it. If the task needs more than that one file, ask for the directory with "
                       + "fs_request_access; do not retry this path with fs_request_access.";
            case GrantVerdict.Missing:
                return $"Refused: '{full}' does not exist and neither does its folder. Ask the user to confirm the "
                       + "path; do not guess another one.";
            case GrantVerdict.NotWritableText:
                return $"Refused: '{full}' does not exist and the assistant could never write it — the name is "
                       + "outside the text allowlist, or the place is protected — so this card would open nothing. "
                       + "Ask the user to create the file, then ask again with the same path; do not retry another "
                       + "name.";
            case GrantVerdict.InsideWorkspace:
                return $"'{full}' is inside the session workspace, so a relative path already reaches it. Use one "
                       + "like src/main.cpp; do not retry this call.";
            case GrantVerdict.AlreadyGranted:
                return $"'{full}' is already granted to this session. Name it by absolute path in fs_read or "
                       + "fs_replace; do not ask again.";
            case GrantVerdict.InsideGrant:
                return $"'{full}' is already inside '{detail}', which this session holds. Work there with absolute "
                       + "paths; do not ask again.";
            default:
                // SessionGone, which only the person's own door can see.
                return "Refused: the session is gone, so nothing could be granted. Do not retry this call.";
        }
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
        if (context.DeepSeekWeb is not null)
            return await FetchDeepSeekWebPage(url, max_characters, cancellationToken).ConfigureAwait(false);

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

    [Description("Fetch one public HTTPS web page through the configured DeepSeek Fetch API and return its title and Markdown content.")]
    public async Task<string> FetchDeepSeekWebPage(
        string url,
        int max_characters = WebFetch.DefaultCharacters,
        CancellationToken cancellationToken = default)
    {
        var bridge = context.DeepSeekWeb;
        if (bridge is null) return "Refused: DeepSeek Fetch API is not configured for this provider. Do not retry.";
        if (!bridge.Allowed()) return "Refused: DeepSeek Search/Fetch API is disabled in Hub settings. Do not retry.";
        if (context.Web is not { } web)
            return WebFetch.ResultFor(new WebFetchDecision(WebFetchVerdict.RefusedNoHost, null), url ?? "");
        // The DeepSeek master switch governs this backend; local Hub fetching has its own independent setting.
        // Reuse only the shared URL policy here and do not inherit that local-fetch toggle.
        var decision = WebFetch.Decide(new WebBridge(() => true, web.Get), url);
        if (decision.Verdict != WebFetchVerdict.Allowed) return WebFetch.ResultFor(decision, url ?? "");

        try
        {
            var page = await bridge.Fetch(decision.Target!, cancellationToken).ConfigureAwait(false);
            var title = string.IsNullOrWhiteSpace(page.Title) ? "(untitled page)" : page.Title.Trim();
            var content = page.Content ?? "";
            var limit = Math.Clamp(max_characters, 1, WebFetch.MaxCharacters);
            if (content.Length > limit) content = content[..limit] + "\n[Content truncated by Hub]";
            return $"Title: {title}\nURL: {decision.Target}\n\n{content}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "DeepSeek Fetch API call was canceled with the conversation. No result was returned.";
        }
        catch (OperationCanceledException)
        {
            return "DeepSeek Fetch API call timed out. No result was returned; retry only if still needed.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or
                                   System.Text.Json.JsonException)
        {
            return $"DeepSeek Fetch API failed ({ex.GetType().Name}: {ex.Message}). Check the endpoint and API key; do not retry immediately.";
        }
    }

    [Description("Search the live web using DeepSeek's Search API. Returns ranked titles, URLs, and summaries; use web_fetch to read a selected page.")]
    public async Task<string> SearchWeb(
        [Description("A concise web search query, from 1 to 128 characters.")] string query,
        [Description("The user's original question that led to this search, when available.")] string? original_question = null,
        CancellationToken cancellationToken = default)
    {
        var bridge = context.DeepSeekWeb;
        if (bridge is null) return "Refused: DeepSeek Search API is not configured for this provider. Do not retry.";
        if (!bridge.Allowed()) return "Refused: DeepSeek Search/Fetch API is disabled in Hub settings. Do not retry.";
        var normalized = (query ?? "").Trim();
        if (normalized.Length is < 1 or > 128)
            return "Refused: query must contain 1 to 128 characters. Do not retry with the same query.";
        try
        {
            var response = await bridge.Search(new DeepSeekWebSearchRequest(normalized,
                OriginalQuestion: string.IsNullOrWhiteSpace(original_question)
                    ? null : original_question.Trim()[..Math.Min(original_question.Trim().Length, 128)]),
                cancellationToken).ConfigureAwait(false);
            if (response.Results.Count == 0) return "No web search results were found.";
            var output = new System.Text.StringBuilder();
            foreach (var result in response.Results)
            {
                output.Append("- ").Append(result.Title).Append("\n  URL: ").Append(result.Url);
                if (!string.IsNullOrWhiteSpace(result.SiteName)) output.Append("\n  Site: ").Append(result.SiteName);
                if (!string.IsNullOrWhiteSpace(result.Date)) output.Append("\n  Date: ").Append(result.Date);
                if (!string.IsNullOrWhiteSpace(result.Summary)) output.Append("\n  Summary: ").Append(result.Summary);
                else if (!string.IsNullOrWhiteSpace(result.Snippet)) output.Append("\n  Summary: ").Append(result.Snippet);
                output.Append("\n  Authority: ").Append(result.Authority).Append("; relevance: ")
                    .Append(result.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("\n");
            }
            return output.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "DeepSeek Search API call was canceled with the conversation. No result was returned.";
        }
        catch (OperationCanceledException)
        {
            return "DeepSeek Search API call timed out. No result was returned; retry only if still needed.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or
                                   System.Text.Json.JsonException)
        {
            return $"DeepSeek Search API failed ({ex.GetType().Name}: {ex.Message}). Check the endpoint and API key; do not retry immediately.";
        }
    }

    [Description("Capture one specifically named application window as an image and attach it to this conversation. "
                 + "Hub asks for that app's Computer Use grant before capturing. Whole-display capture is unavailable. "
                 + "Nothing is written to the workspace.")]
    public string CaptureScreen(
        [Description("Part of the named application window's title, matched case-insensitively. Required so Hub can "
                     + "request a grant for the specific app; a target matching several windows is refused rather "
                     + "than guessed.")]
        string target = "",
        [Description("Whole-display capture is disabled. Leave false and use computer_use for a named app.")] bool fullscreen = false,
        [Description("The process that owns the window, when the title alone does not pick exactly one — two Hub "
                     + "windows can carry the same title, and the refusal lists each one's pid. Narrowing only: a "
                     + "pid owning several windows is still refused.")]
        int process_id = 0)
    {
        if (fullscreen)
            return "Refused: Computer Use only captures a named application window. Name the app and use computer_use with action=screenshot; do not capture the whole display.";
        if (string.IsNullOrWhiteSpace(target))
            return "Refused: capture_screen needs the named application window so Hub can request its Computer Use grant. Use computer_use with application and action=screenshot.";
        // No path guard runs here: the dedicated app-grant gate decides whether this named window can be read.
        var backend = CaptureBackends.ForCurrent();
        if (!backend.Available) return backend.Refusal!;
        if (context.Screen is not { } host)
            return $"Refused: this build has no capture host for {backend.Label}. Ask the user for a screenshot "
                   + "instead; do not retry the capture.";

        // A named title is required so the approval card and the captured window refer to the same app.
        var wanted = (target ?? "").Trim();
        IReadOnlyList<CapturableWindow> windows;
        try { windows = host.Windows(); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return $"Refused: {ex.Message} Check the operating system's screen or accessibility permission; do not retry until the required capability is available.";
        }
        var match = ResolveApprovedComputerWindow(windows, wanted, process_id);
        if (!match.Ok) return match.Refusal!;
        var window = match.Window;

        // A grabber swallows its own failure and answers null, so what the model reads is a sentence about the
        // screen rather than a stack trace from the host.
        CapturedFrame? frame;
        try { frame = host.Grab(window); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return "Refused: the operating system denied screen capture for this app. Ask the user to check screen recording access before retrying.";
        }
        if (frame is null)
            return $"Refused: the {backend.Label} capture of {Described(window, false)} returned no frame. The "
                   + "window may have closed, or the display may be locked; ask the user before capturing anything "
                   + "else.";

        // The blank check has to come before the store: a black frame is a valid PNG with a plausible byte count,
        // so every later step — admission, the file on disk, the wire — reads it as a capture that worked, and the
        // model then describes a screenshot that showed nothing. This is the one place that knows.
        var what = Described(window, false);
        if (frame.Stats.IsBlank())
            return $"Refused: the {backend.Label} capture of {what} came back a blank frame — {frame.Width}×"
                   + $"{frame.Height}, {frame.Stats.DistinctColors} distinct colour(s), variance "
                   + $"{frame.Stats.LuminanceVariance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}. "
                   + "Nothing was stored and no picture is attached. That window paints through a path this capture "
                   + "cannot read (a hardware-accelerated surface, or a protected desktop); ask the user for a "
                   + "screenshot instead, and do not retry this target.";

        try
        {
            if (context.ApprovedComputerTarget?.Invoke() is not { } stillApproved
                || !stillApproved.Matches(window!) || !stillApproved.Find(host.Windows()).Ok)
                return "Refused: the application's Computer Use grant or window owner changed during capture. No image was attached.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return "Refused: the application identity could not be rechecked after capture. No image was attached.";
        }
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

    [Description("Use one named application window. Supported actions are screenshot, click, type, press_key, and scroll. "
                 + "Coordinates are relative to the window's top-left. The application must be named on every call.")]
    internal string ComputerUse(
        [Description("The visible application/window title to locate. Authorization follows its owning application, not the title.")] string application,
        [Description("One action: screenshot, click, type, press_key, or scroll.")] string action,
        [Description("Horizontal window-relative coordinate for click; otherwise 0.")] int x = 0,
        [Description("Vertical window-relative coordinate for click; otherwise 0.")] int y = 0,
        [Description("Text to type, up to 1000 characters.")] string? text = null,
        [Description("Key name for press_key, such as Enter, Tab, Escape, or an A-Z/0-9 key.")] string? key = null,
        [Description("Vertical scroll amount; positive scrolls up, negative down, from -10 to 10.")] int scroll = 0,
        [Description("Narrow the target to this process id when titles are ambiguous.")] int process_id = 0)
    {
        var app = ComputerUseGrants.NormalizeTitle(application);
        if (app.Length is 0 or > 256)
            return "Refused: Computer Use needs one application window title, no more than 256 characters. Do not retry without a title.";
        var normalizedAction = (action ?? "").Trim().ToLowerInvariant();
        if (normalizedAction is not ("screenshot" or "click" or "type" or "press_key" or "scroll"))
            return "Refused: Computer Use action must be screenshot, click, type, press_key, or scroll. Do not retry with another action name.";
        if (normalizedAction == "type" && string.IsNullOrEmpty(text) || (text?.Length ?? 0) > 1000)
            return "Refused: type needs 1 to 1000 characters. Do not split and retry automatically.";
        if (normalizedAction == "press_key" && string.IsNullOrWhiteSpace(key))
            return "Refused: press_key needs one named key. Do not retry without a key.";
        if (normalizedAction == "scroll" && scroll is 0 or < -10 or > 10)
            return "Refused: scroll must be between -10 and 10, excluding zero. Do not retry automatically.";

        if (normalizedAction == "screenshot") return CaptureScreen(app, process_id: process_id);
        var backend = CaptureBackends.ForCurrent();
        if (context.Screen is not { } host)
            return $"Refused: Computer Use cannot enumerate or capture application windows on this {backend.Label} build. Ask the user to operate the app manually.";

        CapturableWindow target;
        try
        {
            var match = ResolveApprovedComputerWindow(host.Windows(), app, process_id);
            if (!match.Ok || match.Window is null) return match.Refusal!;
            target = match.Window;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return $"Refused: {ex.Message} Check the operating system's screen or accessibility permission, then ask the user before trying again.";
        }

        if (context.ComputerUse is not { } operations)
            return $"Refused: this {backend.Label} build can capture windows but has no input-control backend. Ask the user to perform this action.";
        var refusal = operations.Operate(target, new ComputerActionRequest(normalizedAction, x, y, text, key, scroll));
        return refusal ?? $"Completed {normalizedAction} in application window \"{target.Title}\".";
    }

    private ScreenCapture.Match ResolveApprovedComputerWindow(IReadOnlyList<CapturableWindow> windows, string title, int pid)
    {
        if (context.ApprovedComputerTarget?.Invoke() is { } approved) return approved.Find(windows);
        var match = ScreenCapture.Find(windows, title, pid);
        if (!match.Ok) return match;
        return new ScreenCapture.Match(null, ComputerUseTarget.FromWindow(match.Window) is null
            ? "Refused: Hub could not verify the executable identity of this application window. No screenshot or input is allowed; ask the user to operate it manually."
            : "Refused: this call has no current Computer Use application grant. Request approval for the named application before capturing or sending input.");
    }

    private static string Described(CapturableWindow? window, bool fullscreen)
        => window is null
            ? (fullscreen ? "the whole display" : "no target")
            : $"window \"{window.Title}\" (pid {window.ProcessId}, {window.Width}×{window.Height})";

    [Description("Read one assistant memory file: a topic, or the index that lists the topics.")]
    public string MemoryRead(
        [Description(MemoryScopes.Description)] string scope,
        [Description("File name, for example build-conventions.md, or the index name shown in the memory section "
                     + "of the system prompt.")]
        string name)
    {
        if (MemoryScopes.Of(scope) is not { } memoryScope) return BadScope(scope);
        var root = MemoryStore.RootFor(memoryScope, context.WorkspaceRoot, context.DataRoot);
        if (root is null) return MemoryStore.NoRootMessage(memoryScope);

        return MemoryStore.ReadText(root, memoryScope, name)
               ?? $"There is no memory file '{name}' in {memoryScope.ToString().ToLowerInvariant()} memory. The "
                  + "index lists what exists; do not guess another name.";
    }

    [Description("Save something worth remembering in a later session. Write the fact, not a summary of the "
                 + "conversation.")]
    public string MemoryWrite(
        [Description(MemoryScopes.Description)] string scope,
        [Description("Topic file name in kebab-case with a .md suffix, for example build-conventions.md.")]
        string name,
        [Description("What should be remembered.")] string content,
        [Description("Short title for the index.")] string title = "",
        [Description("One line saying when this memory matters.")] string description = "",
        [Description("user, project, feedback, decision, reference or note.")] string type = "note",
        [Description("replace (the default) or append to what is already there.")] string mode = "replace")
    {
        if (MemoryScopes.Of(scope) is not { } memoryScope) return BadScope(scope);
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
        return fresh && memoryScope == MemoryScope.Workspace
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
            // context on bytes nobody asked for. The call itself is shown: "it is mid-way through fs_replace"
            // is a fact about the peer that a reader came here for.
            if (turn.Role == ChatRoles.Tool) continue;
            builder.Append('\n').Append(Describe(turn, context.DataRoot));
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

    [Description("Use a fresh Hub session as a context overflow valve, not a parallel implementation mechanism. "
                 + "Proactively spawn only when substantial reading (a huge file, directory or build log) would "
                 + "crowd this context and only a short conclusion is needed back. Do not delegate ordinary "
                 + "approved plan tasks: editing, UI changes, fixes, builds and tests stay in this session. "
                 + "An independent task, a multi-step plan or possible parallelism is not a reason to spawn. "
                 + "Use the default read-only plan mode for reading; choose agent only when the user explicitly "
                 + "asks to delegate implementation, work in parallel, or execute in a spawned session. Enabling "
                 + "spawning is permission, not a request to split the plan. Hub returns the child's conclusion "
                 + "automatically: never wait for it in this turn, and never spawn the same one twice.")]
    public async Task<string> SpawnSession(
        [Description("What the child is to do, written self-contained — it cannot see this conversation. Name the "
                     + "files or the question, and say what conclusion to bring back. Hub returns the child's final "
                     + "answer to this session automatically.")] string task,
        [Description("The child defaults to \"plan\": read-only investigation and a concise conclusion, without "
                     + "editing files or running commands. Choose \"agent\" to read and edit only when the user "
                     + "explicitly asks to delegate implementation, work in parallel, or execute in a spawned "
                     + "session; an approved plan or the spawning setting alone is not that request.")] string mode = "plan",
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
    /// raw command output, and forwarding them would spend this context on bytes nobody asked for.
    ///
    /// <para>A plan's text is read out of the document its turn names, because that is where it is stored. The
    /// peer has no other way to it: <c>data</c> is a protected root for the file tools, so this is the one door
    /// across sessions, and a document that is gone is said so rather than answered with an empty line.</para></summary>
    private static string Describe(ChatTurn turn, string? dataRoot)
    {
        var speaker = turn.Role switch
        {
            ChatRoles.User when turn.InjectedFrom is { Length: > 0 } from => $"user (written by session {from})",
            ChatRoles.User => "user",
            _ => "assistant",
        };
        var stored = PlanStore.BodyOf(turn, dataRoot, out var state);
        var text = state switch
        {
            PlanBodyState.Read or PlanBodyState.NoReference => stored ?? "",
            _ when turn.Plan is { Title.Length: > 0, File: { } gone } =>
                $"[the plan 「{turn.Plan.Title}」 is stored as ai/plans/{gone}, and that file is no longer on disk]",
            _ => "[a plan this session wrote is no longer on disk]",
        };
        var body = text.Length <= MaxTurnCharacters
            ? text
            : text[..MaxTurnCharacters] + "…";
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

    [Description("Find text inside the session workspace, or inside a directory the user granted this session, "
                 + "with a regular expression and get back path:line: content, ready to open with fs_read at that "
                 + "offset. This is how to look around a project without running a shell command, and it reads a "
                 + "single file too when the question is about one log.")]
    public string SearchText(
        [Description("Regular expression matched per line. Characters that are not operators must be escaped.")]
        string pattern,
        [Description("Only search files this glob names: \"*.cpp\" matches that name at any depth, \"include/*.h\" "
                     + "matches one directory deep. Patterns are matched inside 'path', not from the workspace root. "
                     + "Empty searches every text file, and it is ignored when 'path' names one file.")]
        string glob = "",
        [Description("Directory to search: relative to the workspace, or an absolute path inside a directory the "
                     + "user granted this session. One file instead of a directory searches just that file. Empty or "
                     + "\".\" means the whole workspace.")]
        string path = "",
        [Description("Match letters of either case.")] bool ignore_case = false,
        [Description("Stop after this many matches, up to 500.")] int max_matches = DefaultSearchMatches)
    {
        var directory = WorkspacePaths.ResolveDirectory(context.Sandbox, path, context.Guards);
        // A file where a directory was expected is how one log is searched: the model was pointed at that file, and
        // answering "not a directory" would send it back to reading windows by hand. One file is resolved by the
        // read verb, so the sandbox decides it either way — and only that verdict may open the branch, because a
        // defaulted WorkspacePath is Allowed and would read the workspace as if it were a file.
        var single = directory.IsAllowed
            ? null
            : WorkspacePaths.ResolveRead(context.Sandbox, path, context.Guards) is { IsAllowed: true } read
                ? read
                : (WorkspacePath?)null;
        if (!directory.IsAllowed && single is null) return Refusal(directory.Verdict, directory.Display, path);
        var oneFile = single is not null;
        var named = single ?? default;
        if (string.IsNullOrWhiteSpace(pattern))
            return "Refused: fs_grep needs a pattern. Say what to look for, or call fs_list to see the "
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
        var tooLarge = 0;
        var notText = 0;
        long scanned = 0;
        var budgetHit = false;

        IEnumerable<string> candidates = oneFile ? [named.Full] : WalkFiles(directory.Full, MaxScannedFiles);
        foreach (var file in candidates)
        {
            walked++;
            // The glob filters inside the directory that was asked for; the path printed is the one fs_read
            // takes, which is the workspace-relative one. Those differ as soon as path is not the root. Naming one
            // file is already the filter, so a glob beside it is ignored rather than answering "no match" about the
            // file the caller named.
            var within = oneFile ? Path.GetFileName(file) : RelativeOf(directory.Full, file);
            if (!oneFile && !wants(within)) continue;
            // A log is text whatever it is called — that list is the search's own, and not the write allowlist.
            if (!WorkspacePaths.IsSearchableText(Path.GetFileName(file))) continue;
            searched++;
            var length = new FileInfo(file).Length;
            if (length > MaxSearchFileBytes)
            {
                tooLarge++;
                continue;
            }

            if (scanned + length > MaxSearchBytes)
            {
                budgetHit = true;
                break;
            }

            scanned += length;
            if (ReadAll(file) is not { } text)
            {
                notText++;
                continue;
            }

            var shown = oneFile ? named.Display : WorkspaceRelativeOf(directory, file);
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

        // What was left out, said once: a search that silently skipped a sixteen-megabyte log is a search whose
        // "no match" the model has to distrust.
        var notes = new StringBuilder();
        if (tooLarge > 0)
            notes.Append($" {tooLarge} file(s) were bigger than {MaxSearchFileBytes / 1024 / 1024} MiB and were skipped.");
        if (notText > 0) notes.Append($" {notText} file(s) were not UTF-8 text.");
        if (budgetHit)
            notes.Append($" The scan stopped at its {MaxSearchBytes / 1024 / 1024} MiB budget, so a match further on "
                         + "was not seen — narrow path or glob.");
        else if (walked >= MaxScannedFiles)
            notes.Append($" The walk hit its {MaxScannedFiles}-file limit, so a deeper directory may hold more.");

        var scope = $"{(string.IsNullOrWhiteSpace(glob) || oneFile ? "any text file" : $"files matching {glob}")}"
                    + (oneFile ? $" in {named.Display}" : $" under {LabelOf(directory)}");
        if (hits.Count == 0)
            return $"fs_grep · {pattern} · {scope}\nNo match in the {searched} file(s) searched."
                   + notes + " Try a shorter pattern, or widen path. Do not retry this one unchanged.";

        var builder = new StringBuilder($"fs_grep · {pattern} · {scope}\n")
            .AppendJoin('\n', hits)
            .Append($"\n{hits.Count} match(es) in {matched.Count} file(s) of the {searched} searched.");
        if (hits.Count >= cap)
            builder.Append($" The answer was cut at {cap} matches — narrow with glob or path, do not ask again the same way.");
        builder.Append(notes);
        return builder.ToString();
    }

    [Description("List a directory in the session workspace, or in a directory the user granted this session — "
                 + "subdirectories first, then files with their size — so the shape of a project is visible without "
                 + "running a shell command.")]
    public string ListDirectory(
        [Description("Directory to list: relative to the workspace, or an absolute path inside a directory the user "
                     + "granted this session. Empty or \".\" means the workspace root.")]
        string path = "",
        [Description("How many levels to include, 1 to 4. Depth costs output, so ask for one and repeat.")]
        int depth = DefaultListDepth)
    {
        var directory = WorkspacePaths.ResolveDirectory(context.Sandbox, path, context.Guards);
        if (!directory.IsAllowed) return Refusal(directory.Verdict, directory.Display, path);

        var levels = Math.Clamp(depth, 1, MaxListDepth);
        var builder = new StringBuilder();
        var shown = 0;
        var dirs = 0;
        var files = 0;
        var skipped = 0;
        var truncated = false;
        // Flat paths, not an indented tree: the point of listing is to name something the next call can open, and
        // "  note.md" under a header is a path no tool takes. Under the workspace they are workspace-relative; under
        // a granted directory they carry the directory, because a bare "src/main.cpp" means the workspace and would
        // open a different file.
        var fromRoot = directory.Granted
            ? directory.Full.Replace(Path.DirectorySeparatorChar, '/') + "/"
            : directory.Relative.Length == 0
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
        var head = $"fs_list · {LabelOf(directory)} · depth {levels} · {dirs} dir(s), {files} file(s)";
        if (truncated) head += $" (cut at {MaxListedEntries} entries)";
        if (skipped > 0) head += $" · {skipped} build/vendored dir(s) not entered";
        return builder.Length == 0 ? head + "\n(that directory is empty)" : head + builder;
    }

    [Description("Find files by glob and return their paths without reading them. Use this when the name is known "
                 + "but the directory is not.")]
    public string FindFiles(
        [Description("Glob to match: \"*.cpp\", \"CMakeLists.txt\", \"settings*.json\". A glob with no '/' matches "
                     + "the file name at any depth; one with '/' matches inside 'path'.")] string glob,
        [Description("Directory to search from: relative to the workspace, or an absolute path inside a directory "
                     + "the user granted this session. Empty or \".\" means the whole workspace.")]
        string path = "")
    {
        var directory = WorkspacePaths.ResolveDirectory(context.Sandbox, path, context.Guards);
        if (!directory.IsAllowed) return Refusal(directory.Verdict, directory.Display, path);
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
            return $"fs_find · {glob} · under {LabelOf(directory)}\nNo file matched in the {walked} file(s) "
                   + "walked. Try the name without its extension, or call fs_list to see what is there.";

        var builder = new StringBuilder($"fs_find · {glob} · under {LabelOf(directory)}\n")
            .AppendJoin('\n', found)
            .Append($"\n{found.Count} file(s).");
        if (found.Count >= MaxFoundFiles) builder.Append(" Cut at the limit — narrow with path.");
        return builder.ToString();
    }

    [Description("Ask what a directory IS before reading what is in it: whether this workspace is also a registered "
                 + "Hub project, whether it is a git repository and on what branch, which build units it holds and "
                 + "how they reference each other, what each top-level directory contains, and which parts the scan "
                 + "left out. Reads listings and manifest files only — no symbols, no index — and changes nothing. "
                 + "Make this the first call in a tree you have not seen; use fs_grep and fs_read for "
                 + "questions about contents.")]
    public async Task<string> MapWorkspace(
        [Description("Directory to map: relative to the workspace, or an absolute path inside a directory the user "
                     + "granted this session. Empty or \".\" means the workspace itself.")]
        string path = "",
        CancellationToken cancellationToken = default)
    {
        // Inside the same guard as the listing tools: a map walks directories the session may already list, so it
        // reaches nowhere new in any approval mode, and it is registered as ReadOnly for the reason fs_grep
        // is — exploration that costs a card gets replaced by a grep inside a command that costs nothing.
        var directory = WorkspacePaths.ResolveDirectory(context.Sandbox, path, context.Guards);
        if (!directory.IsAllowed) return Refusal(directory.Verdict, directory.Display, path);
        return await WorkspaceMap.ReadAsync(context, directory.Full, cancellationToken).ConfigureAwait(false);
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

    /// <summary>A found file as the next call needs it. fs_read resolves a relative path against the workspace
    /// root, so a scoped search still has to print <c>probe/app.cpp</c>: the bare <c>app.cpp</c> is true inside the
    /// directory that was searched and a dead end everywhere else — and under a granted directory the path has to
    /// carry the directory itself, or it would name a file in the workspace instead.</summary>
    private static string WorkspaceRelativeOf(WorkspacePath directory, string file)
    {
        if (directory.Granted) return file.Replace(Path.DirectorySeparatorChar, '/');
        var inside = RelativeOf(directory.Full, file);
        return directory.Relative.Length == 0
            ? inside
            : directory.Relative.Replace(Path.DirectorySeparatorChar, '/') + "/" + inside;
    }

    private static string LabelOf(WorkspacePath directory)
        => directory.Granted
            ? $"'{directory.Full}'"
            : directory.Relative.Length > 0 ? $"'{directory.Relative}/'" : "the workspace root";

    private static string Clipped(string line)
    {
        var text = line.Trim();
        return text.Length <= MaxSearchLineCharacters ? text
            : text[..MaxSearchLineCharacters] + "…";
    }

    private static string BadScope(string? value)
        => $"Refused: scope '{value}' is neither workspace nor global (a project is one kind of workspace, so the "
           + "word project also names it). Do not retry another spelling.";

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

    /// <summary>
    /// One window of a file too large to decode whole. Lines are streamed rather than buffered, the total is counted
    /// as they go so the header still says "of N lines", and the cap bounds the bytes actually returned — a window
    /// that would blow it stops growing and says so. CR, LF and CRLF all end a line here, which is the same shape
    /// <see cref="SplitLines"/> gives the ordinary read.
    /// </summary>
    private static string WindowOf(string display, string full, int offset, int limit)
    {
        var count = Math.Clamp(limit, 1, MaxReadLimit);
        var wanted = Math.Max(1, offset);
        var window = new List<string>();
        long total = 0;
        long bytes = 0;
        var cut = false;
        try
        {
            using var stream = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            while (reader.ReadLine() is { } line)
            {
                total++;
                if (total < wanted || cut || window.Count >= count) continue;
                // Counted with its line ending, because that is what the result spends on the way out.
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
                if (bytes > MaxReadFileBytes)
                {
                    cut = true;
                    continue;
                }

                window.Add(line);
            }
        }
        catch (DecoderFallbackException)
        {
            return NotText(display);
        }
        catch (IOException)
        {
            return NotText(display);
        }

        var from = Math.Clamp(wanted, 1, Math.Max(1, total));
        var last = from - 1 + window.Count;
        var header = $"{display} — lines {from}-{last} of {total} (UTF-8)";
        if (window.Count == 0)
            return cut
                ? $"{header}{Lf}(the first line of this window is over the {MaxReadFileBytes / 1024} KiB result "
                  + "limit on its own; read this one with run_command)"
                : $"{header}{Lf}(that window is empty)";

        var builder = new StringBuilder(header);
        foreach (var line in window) builder.Append('\n').Append(line);
        if (cut)
            builder.Append($"{Lf}… (this window was cut at {MaxReadFileBytes / 1024} KiB of result; ask for fewer "
                           + "lines)");
        else if (last < total)
            builder.Append($"{Lf}… ({total - last} more lines; read again with offset={last + 1})");
        return builder.ToString();
    }

    private static List<string> SplitLines(string text)
    {
        var lines = TextEol.ToLf(text).Split('\n').ToList();
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
            if (builder.Length > 0) builder.Append(Lf).Append("--- stderr ---").Append('\n');
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
