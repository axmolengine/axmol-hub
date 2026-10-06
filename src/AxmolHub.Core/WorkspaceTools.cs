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
public sealed record WorkspaceToolScope(
    string? WorkspaceRoot,
    WorkspaceGuards Guards,
    string? DataRoot,
    string ConversationId,
    IReadOnlyList<string> SensitiveValues,
    HubLog? Log,
    Func<string, Task<string>>? ApplyWorkspaceRoot)
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

        if (ReadText(resolved.Full) is not { } text) return NotText(resolved.Relative);

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
            if (ReadText(resolved.Full) is not { } text) return NotText(resolved.Relative);
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
        return FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Relative)
               + (undo.Stored ? $" Undo copy: {undo.Path}" : undo.Message.Length > 0 ? $" ({undo.Message})" : "");
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
    /// <c>ChatContextReader</c> already applies to attached files.</summary>
    private static string? ReadText(string path)
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
