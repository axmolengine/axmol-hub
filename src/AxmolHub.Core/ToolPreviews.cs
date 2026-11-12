using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// What an approval card says a call would do, computed once when the call parks and stored on the turn.
///
/// Frozen rather than recomputed at decision time: the decision can arrive after a restart, and the card then has
/// to show what the gate actually saw. For a write that means a real diff of the file as it stood — which is also
/// what makes the re-check at approval time meaningful, since <see cref="FileEdit"/> will refuse to land an anchor
/// that no longer matches exactly one place.
///
/// In Core, beside the guards it reads, so every shape of card can be asserted without a window.
/// </summary>
public static class ToolPreviews
{
    public static string PreviewFor(
        string name, string? argumentsJson, WorkspaceToolScope scope, IReadOnlyList<string>? projectPaths = null)
    {
        var arguments = Parse(argumentsJson);
        return name switch
        {
            LegacyFileToolNames.FileWrite => WritePreview(arguments, scope),
            ToolNames.FsWrite => FullWritePreview(arguments, scope),
            ToolNames.FsReplace => ReplacePreview(arguments, scope),
            ToolNames.FsDelete or ToolNames.FsMove => ManagementPreview(name, arguments, scope),
            ToolNames.RunCommand => CommandPreview(arguments, scope),
            LegacyFileToolNames.RequestDirectory => GrantPreview(Text(arguments, "path"), scope, projectPaths),
            LegacyFileToolNames.RequestFile => FileGrantPreview(Text(arguments, "path"), scope),
            ToolNames.FsRequestAccess => AccessPreview(arguments, scope, projectPaths),
            ToolNames.CaptureScreen => CapturePreview(arguments, scope),
            ToolNames.WebFetch => FetchPreview(arguments, scope),
            ToolNames.SendToSession => SendPreview(arguments),
            ToolNames.SpawnSession => SpawnPreview(arguments, scope),
            ToolNames.MemoryWrite => MemoryPreview(arguments),
            ToolNames.MemoryRead => $"memory_read · {Text(arguments, "scope")} · {Text(arguments, "name")}",
            // Not a preview of an action: there is nothing this call would do, so the line carries the question
            // itself. The card reads the stored arguments directly rather than this string — a question's text is
            // immutable model output and needs no freezing, unlike a diff of a file that can change under it.
            ToolNames.AskUser => AskUserQuestion.Preview(argumentsJson),
            LegacyFileToolNames.ReadFile or ToolNames.FsRead => $"{name} · {Text(arguments, "path")}",
            LegacyFileToolNames.SearchText or ToolNames.FsGrep => $"{name} · {Text(arguments, "pattern")} · "
                             + $"{(Text(arguments, "glob") is { Length: > 0 } glob ? glob : "any text file")} · in "
                             + $"{DirectoryOf(arguments)}",
            LegacyFileToolNames.ListDirectory or ToolNames.FsList => $"{name} · {DirectoryOf(arguments)} · depth {Number(arguments, "depth")}",
            LegacyFileToolNames.FindFiles or ToolNames.FsFind => $"{name} · {Text(arguments, "glob")} · in {DirectoryOf(arguments)}",
            _ => name,
        };
    }

    public static string ManagementFingerprint(string name, string? argumentsJson, WorkspaceToolScope scope)
    {
        var arguments = Parse(argumentsJson);
        var isMove = name == ToolNames.FsMove;
        return WorkspaceTools.PrepareManagement(scope, Text(arguments, isMove ? "source" : "path"),
            isMove ? Text(arguments, "destination") : null, isMove || Flag(arguments, "recursive")).Fingerprint;
    }

    private static string ManagementPreview(string name, IReadOnlyDictionary<string, JsonElement> arguments,
        WorkspaceToolScope scope)
    {
        var isMove = name == ToolNames.FsMove;
        var plan = WorkspaceTools.PrepareManagement(scope, Text(arguments, isMove ? "source" : "path"),
            isMove ? Text(arguments, "destination") : null, isMove || Flag(arguments, "recursive"));
        if (plan.Error is not null) return plan.Error;
        var size = plan.Entries.Sum(entry => entry.Bytes?.LongLength ?? 0);
        var action = isMove ? $"Move {plan.Source.Display} → {plan.Destination?.Display}" : $"Delete {plan.Source.Display}";
        return $"{action}\n{plan.Entries.Count} entries · {size} bytes\n"
               + string.Join("\n", plan.Entries.Select(entry => entry.Relative + (entry.IsDirectory ? "/" : "")));
    }

    public static string AccessKind(string? argumentsJson)
    {
        var arguments = Parse(argumentsJson);
        return WorkspaceTools.AccessKind(Text(arguments, "path"), Text(arguments, "kind"));
    }

    private static string AccessPreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope,
        IReadOnlyList<string>? projectPaths)
    {
        var path = Text(arguments, "path");
        var kind = WorkspaceTools.AccessKind(path, Text(arguments, "kind"));
        return kind switch
        {
            "directory" => GrantPreview(path, scope, projectPaths).Replace("request_directory ·", "fs_request_access · directory ·", StringComparison.Ordinal),
            "file" => FileGrantPreview(path, scope).Replace("request_file ·", "fs_request_access · file ·", StringComparison.Ordinal),
            _ => "fs_request_access · invalid or ambiguous path; specify an existing path or its kind.",
        };
    }

    private static string FullWritePreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var path = Text(arguments, "path");
        var resolved = WorkspacePaths.ResolveWrite(scope.Sandbox, path, scope.Guards, allowUnlistedText: true);
        if (!resolved.IsAllowed) return WorkspacePaths.ResultFor(resolved.Verdict, path);
        try
        {
            var unlisted = !WorkspacePaths.IsWritableText(Path.GetFileName(resolved.Full));
            if (unlisted && (File.Exists(resolved.Full)
                    ? !TextFile.HasTextFilePrefix(resolved.Full)
                    : !TextFile.HasTextContentPrefix(Text(arguments, "content"))))
                return $"Refused: {resolved.Display} does not begin like text.";
            var original = File.Exists(resolved.Full) ? WorkspaceTools.ReadAll(resolved.Full) : null;
            if (File.Exists(resolved.Full) && original is null)
                return $"Refused: {resolved.Display} is not UTF-8 text.";
            var edit = FileEdit.Overwrite(original, Text(arguments, "content"), Flag(arguments, "overwrite"));
            return edit.Changed ? FileDiff.Unified(original ?? "", edit.Updated, resolved.Display)
                : edit.Verdict == FileEditVerdict.Unchanged ? $"No change: {resolved.Display} already has that content."
                : edit.Verdict == FileEditVerdict.AlreadyExists ? $"Refused: {resolved.Display} already exists. Pass overwrite=true to replace it."
                : FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Display);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"Cannot preview {resolved.Display}: {ex.Message}"; }
    }

    public static string WriteFingerprint(string? argumentsJson, WorkspaceToolScope scope)
        => WorkspaceTools.WriteFingerprint(scope, Text(Parse(argumentsJson), "path"));

    private static IReadOnlyList<FsReplacement>? ReplacementEdits(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (!arguments.TryGetValue("edits", out var value)) return null;
        try { return value.Deserialize<FsReplacement[]>(StateStore.ReadOptions); }
        catch (JsonException) { return null; }
    }

    public static string ReplaceFingerprint(string? argumentsJson, WorkspaceToolScope scope)
        => WorkspaceTools.PrepareReplacements(scope, ReplacementEdits(Parse(argumentsJson))).Fingerprint;

    private static string ReplacePreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var plan = WorkspaceTools.PrepareReplacements(scope, ReplacementEdits(arguments));
        if (plan.Error is not null) return plan.Error;
        var preview = new System.Text.StringBuilder($"Edit {plan.Changes.Count} file(s)");
        foreach (var change in plan.Changes)
        {
            preview.Append('\n').Append(FileDiff.Unified(change.Before, change.After, change.Path.Display));
            if (preview.Length > 60_000)
            {
                preview.Append("\n… (preview truncated)");
                break;
            }
        }
        return preview.ToString();
    }

    private static string WritePreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var path = Text(arguments, "path");
        var resolved = WorkspacePaths.ResolveWrite(scope.Sandbox, path, scope.Guards);
        if (!resolved.IsAllowed)
            return WorkspacePaths.ResultFor(resolved.Verdict,
                resolved.Display.Length > 0 ? resolved.Display : path);

        string? original = null;
        if (File.Exists(resolved.Full))
        {
            try
            {
                original = File.ReadAllText(resolved.Full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Cannot preview {resolved.Display}: {ex.Message}";
            }
        }

        var edit = FileEdit.Apply(original, Text(arguments, "old_string"), Text(arguments, "new_string"),
            Flag(arguments, "replace_all"));
        return edit.Changed
            ? FileDiff.Unified(original ?? "", edit.Updated, resolved.Display)
            : FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Display);
    }

    /// <summary>
    /// Names the shell and the directory as well as the command: what a person is approving is "this, in there",
    /// and a bare command line hides the part that decides how much damage it can do.
    ///
    /// The git grade rides on the same line because the tier is no longer decided by the directory alone, and a
    /// card that parks a command under 自动审批 without saying why reads as a bug in the mode. The sentence names
    /// what the command can reach — the index, the tree, history — rather than repeating that it asks: the card
    /// being on screen already said that. An unrecognised subcommand is said differently on purpose, because
    /// "this move the index" would be a claim about a verb this build has never seen.
    /// </summary>
    private static string CommandPreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var shell = CommandShells.ForCurrent();
        // The card names the directory the command would actually run in, resolved through the same guard the tool
        // body runs before it spawns a shell. Showing the workspace for a command the model pointed at a granted
        // directory would describe a different run, and promising one the guard refuses would be a card that lies.
        var asked = Text(arguments, "cwd");
        var directory = WorkspacePaths.ResolveDirectory(scope.Sandbox, asked, scope.Guards);
        if (!directory.IsAllowed)
            return $"{shell.Label} · {WorkspacePaths.ResultFor(directory.Verdict,
                directory.Display.Length > 0 ? directory.Display : asked)}";

        var root = directory.Full;
        var timeout = arguments.TryGetValue("timeout_seconds", out var seconds) && seconds.TryGetInt32(out var value)
            ? value
            : WorkspaceTools.DefaultCommandTimeoutSeconds;
        var head = $"{shell.Label} · {root} · Hub asks after {timeout}s of silence";
        var grade = GitCommandGrades.Grade(Text(arguments, "command"));
        var build = BuildCommandGrades.Grade(Text(arguments, "command"));
        // One line, named for the half that makes this card exist. A line that both compiles and deletes is spelled
        // as the deletion because that is the loss with no undo, and a card that listed everything the command does
        // would be a paragraph nobody reads. The git reading wins over a bare compile for the same reason the tier
        // does: under 自动审批 it is the reach that makes the command park at all, and the rebuild line alone would
        // read as an apology.
        var flag = build == BuildGrade.CacheBreak
            ? "\ncache break · removes what a build already wrote"
            : grade switch
            {
                GitGrade.GitWrite => "\ngit write · moves the index, the working tree, or history",
                GitGrade.GitUnclassifiable => "\ngit subcommand Hub does not recognise · treated as a write",
                _ when build == BuildGrade.BuildRun => "\nrebuild · compiles into this directory",
                _ => "",
            };
        return $"{head}{flag}\n{Text(arguments, "command")}";
    }

    /// <summary>
    /// What a grant card is asking for, in the terms the answer is given in: the directory, whether it is there,
    /// whether Hub already knows it — and, on the same line, what a yes would allow. "read and write · this session
    /// only" is part of the card rather than a footnote, because the scope of the yes is the thing being decided: a
    /// grant that quietly outlived the session, or quietly covered writing, would be an approval nobody gave. A
    /// protected location is the one case where the yes is narrower — reading — and the card says which of the
    /// three places it is, because "Hub's own data" and "one repository's .git" are not the same thing to agree to.
    /// </summary>
    private static string GrantPreview(string path, WorkspaceToolScope scope, IReadOnlyList<string>? projectPaths)
    {
        if (string.IsNullOrWhiteSpace(path)) return "request_directory · (no path given)";

        // Read through the classifier and the guards the gate reads, so a card can never describe a directory
        // differently than the answer will grade it.
        var protectedLocation = Path.IsPathRooted(path)
            ? WorkspacePaths.ProtectedLocationOf(path, scope.Guards)
            : null;
        var facts = DirectoryFacts(path, scope, projectPaths, protectedLocation);
        if (Path.IsPathRooted(path)
            && WorkspacePaths.ClassifyWorkspaceTarget(path, scope.WorkspaceRoot, projectPaths, scope.Guards)
            is WorkspaceTarget.SameAsSandbox or WorkspaceTarget.InsideSandbox)
            facts.Add("already inside the session workspace");
        facts.Add(protectedLocation is null ? "read and write" : "read only");
        facts.Add("this session only");
        return $"request_directory · {path}\n{string.Join(" · ", facts)}";
    }

    /// <summary>The directory facts the grant card shows. Read through the classifier the tier table reads, so a
    /// card can never describe a directory differently than the gate graded it.</summary>
    private static List<string> DirectoryFacts(string path, WorkspaceToolScope scope,
        IReadOnlyList<string>? projectPaths, (ProtectedKind Kind, string Anchor)? protectedLocation)
    {
        var facts = new List<string> { Directory.Exists(path) ? "exists" : "does not exist" };
        if (Directory.Exists(Path.Combine(path, ".git"))) facts.Add("git repository");
        if (WorkspacePaths.ClassifyWorkspaceTarget(path, scope.WorkspaceRoot, projectPaths, scope.Guards)
            == WorkspaceTarget.RegisteredProject) facts.Add("registered Hub project");
        // Named rather than flagged: the person deciding is agreeing to one of three very different places, and
        // what is in the data directory is the part they cannot see from the path.
        if (protectedLocation is { } location) facts.Add(ProtectedFact(location));
        return facts;
    }

    /// <summary>
    /// The one sentence for "which of the three places this is", shared by both grant cards. A directory and a file
    /// inside the data directory are the same fact about the place, and two spellings of it would eventually stop
    /// agreeing about what a person is agreeing to.
    /// </summary>
    private static string ProtectedFact((ProtectedKind Kind, string Anchor) location) => location.Kind switch
    {
        ProtectedKind.DataRoot => "protected location — Hub's own data: every session's transcript, the settings "
                                  + "and the logs",
        ProtectedKind.Engine => "protected location — an engine installation",
        _ => "protected location — a .git directory: its hooks and history",
    };

    /// <summary>
    /// What a card on one <b>file</b> is asking for. Narrower than a directory card in the one way that has to be
    /// visible before the yes: the file is what is being opened, and the folder around it is not. So the facts say
    /// whether it is there, which of the three protected places it sits in when it sits in one, and the tier — and
    /// the tier is read from <see cref="WorkspacePaths.FileGrantIsReadOnly"/>, the same function every resolution
    /// asks, never from a second opinion held here.
    /// </summary>
    private static string FileGrantPreview(string path, WorkspaceToolScope scope)
    {
        if (string.IsNullOrWhiteSpace(path)) return "request_file · (no path given)";

        string? full = null;
        if (Path.IsPathRooted(path))
        {
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or System.Security.SecurityException)
            {
                full = null;
            }
        }

        if (full is null) return $"request_file · {path}\nnot an absolute path — a card needs the file's full path";

        var facts = new List<string> { File.Exists(full) ? "exists" : "does not exist yet" };
        if (WorkspacePaths.ProtectedLocationOf(full, scope.Guards) is { } location) facts.Add(ProtectedFact(location));
        else if (!WorkspacePaths.IsWritableText(Path.GetFileName(full)))
            facts.Add(File.Exists(full) && TextFile.HasTextFilePrefix(full)
                ? "its extension is outside the write allowlist; the file prefix looks like text"
                : "its extension is outside the write allowlist and the file is read only");
        if (WorkspacePaths.ClassifyFileGrant(scope.Sandbox, scope.Guards, full).Verdict
            is GrantVerdict.AlreadyGranted or GrantVerdict.InsideGrant)
            facts.Add("already reachable by this session");
        facts.Add(WorkspacePaths.FileGrantIsReadOnly(full, scope.Guards) ? "read only" : "read and write");
        facts.Add("this session only · the folder around it is not opened");
        return $"request_file · {path}\n{string.Join(" · ", facts)}";
    }

    /// <summary>Who is being written to, and whether that session is being started up to answer: a person
    /// approving this is approving noise in another session's conversation, and the two differ.</summary>
    private static string SendPreview(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var text = Text(arguments, "text");
        var head = text.Length <= 400 ? text : text[..400] + "…";
        return $"send_to_session → {Text(arguments, "target")} · wake: {(Flag(arguments, "wake") ? "yes" : "no")}\n{head}";
    }

    /// <summary>Which window is about to be drawn, or the whole display. A person approving this is approving
    /// "this is what the model gets to see", so the card names the window the address resolves to — and when the
    /// address is ambiguous the card says so, because the tool will refuse it the same way. A bare process id
    /// never reaches the card alone: whoever is deciding has to see the window they are agreeing to show.</summary>
    private static string CapturePreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        if (scope.PortalComputer is not null)
            return "capture_screen · temporary Wayland portal session · user selects one window; title and process identity are unverified · no permanent grant";
        var backend = scope.Screen?.Backend ?? CaptureBackends.ForCurrent();
        if (!backend.Available) return $"capture_screen · {backend.Refusal}";
        var target = Text(arguments, "target");
        var processId = Integer(arguments, "process_id");
        if (Flag(arguments, "fullscreen"))
            return "capture_screen · Refused: whole-display capture is disabled; name an application window.";
        if (scope.Screen is not { } host)
            return $"capture_screen · {Addressed(target, processId)} · this build has no capture host for "
                   + $"{backend.Label}, so the call will be refused";

        IReadOnlyList<CapturableWindow> windows = [];
        try
        {
            windows = host.Windows();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // A card still has to render, and it renders the address the model asked for.
        }
        var match = ScreenCapture.Find(windows, target, processId);
        if (!match.Ok) return $"capture_screen · {Addressed(target, processId)}\n{match.Refusal}";
        return match.Window is { } found
            ? $"capture_screen · window \"{found.Title}\" (pid {found.ProcessId}) · {found.Width}×{found.Height} · "
              + $"{backend.Label}"
            : $"capture_screen · fullscreen · {backend.Label}";
    }

    /// <summary>How the call addressed the window, in the words a person reading the card already knows: the title
    /// is the thing they can judge, the pid is only there because two windows can share a title.</summary>
    private static string Addressed(string target, int processId)
        => string.IsNullOrWhiteSpace(target)
            ? $"pid {processId}"
            : $"\"{target}\"" + (processId > 0 ? $" · pid {processId}" : "");

    /// <summary>Where this machine is about to go. The host is the thing a person is deciding about, so it leads the
    /// card with the path and query after it — the query comes because a query is what gets sent, and it is the part
    /// of a URL that carries a token, so it goes through the same redaction the command preview's output does.
    /// A call that the address policy or Hub's own switch will refuse says so here rather than promising an action
    /// the tool then declines: a card that lies is a card the user learns to ignore.</summary>
    private static string FetchPreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var url = Text(arguments, "url");
        var asked = SecretRedaction.Redact(
            url.Length > 0 ? url : "(no url given)", scope.SensitiveValues);
        var decision = WebFetch.Decide(scope.Web, url);
        if (decision.Verdict != WebFetchVerdict.Allowed) return $"{asked}\n{WebFetch.ResultFor(decision, url)}";
        var target = decision.Target!;
        // The query is shown because it is what gets sent — and redacted in the same breath, because a URL is one
        // of the places an API key hides in plain sight.
        var where = SecretRedaction.Redact($"{target.Host}{target.PathAndQuery}", scope.SensitiveValues);
        return $"web_fetch · {where}\n"
               + $"scheme: https · max_characters: {Number(arguments, "max_characters")} · "
               + $"reads at most {WebFetch.MaxBytes / 1024 / 1024} MiB, {WebFetch.Timeout.TotalSeconds:0}s";
    }

    /// <summary>What a new session is going to be pointed at, and which sandbox it inherits. A person approving
    /// this is approving a model call nobody typed and a second window over their own files, so the card shows the
    /// task head and says plainly whether the workspace comes with it.</summary>
    private static string SpawnPreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var task = Text(arguments, "task");
        var head = task.Length <= 400 ? task : task[..400] + "…";
        var mode = Text(arguments, "mode");
        var workspace = !Flag(arguments, "inherit_workspace")
            ? "no workspace"
            : scope.WorkspaceRoot is { Length: > 0 } root
                ? $"inherits workspace {root}"
                : "this session has no workspace to hand over";
        return $"spawn_session · {(mode.Length > 0 ? mode : "plan")} · {workspace}\n"
               + (head.Length > 0 ? head : "(no task given)");
    }

    private static string MemoryPreview(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var content = Text(arguments, "content");
        var head = content.Length <= 400 ? content : content[..400] + "…";
        return $"memory_{Text(arguments, "mode")} · {Text(arguments, "scope")} · {Text(arguments, "name")}\n{head}";
    }

    private static IReadOnlyDictionary<string, JsonElement> Parse(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new Dictionary<string, JsonElement>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson)
                   ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            // A card for arguments that do not parse still has to render: the user is being asked to refuse it.
            return new Dictionary<string, JsonElement>();
        }
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> arguments, string name)
        => arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Flag(IReadOnlyDictionary<string, JsonElement> arguments, string name)
        => arguments.TryGetValue(name, out var value)
           && (value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    /// <summary>The directory a read-only tool was pointed at. An empty path means the whole workspace, and
    /// naming that fact is what keeps the line from reading as a blank.</summary>
    private static string DirectoryOf(IReadOnlyDictionary<string, JsonElement> arguments)
        => Text(arguments, "path") is { Length: > 0 } path ? path : "(the whole workspace)";

    private static string Number(IReadOnlyDictionary<string, JsonElement> arguments, string name)
        => arguments.TryGetValue(name, out var value) && value.TryGetInt32(out var number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "(default)";

    /// <summary>An integer argument as the tool sees it: absent or unreadable is the same 0 the tool's own default
    /// parameter carries, so the card resolves the address exactly the way the body will.</summary>
    private static int Integer(IReadOnlyDictionary<string, JsonElement> arguments, string name)
        => arguments.TryGetValue(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
