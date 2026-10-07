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
            "file_write" => WritePreview(arguments, scope),
            "run_command" => CommandPreview(arguments, scope),
            "set_workspace" => WorkspacePreview(Text(arguments, "path"), scope, projectPaths),
            "capture_screen" => CapturePreview(arguments, scope),
            "send_to_session" => SendPreview(arguments),
            "memory_write" => MemoryPreview(arguments),
            "memory_read" => $"memory_read · {Text(arguments, "scope")} · {Text(arguments, "name")}",
            "read_file" => $"read_file · {Text(arguments, "path")}",
            "search_text" => $"search_text · {Text(arguments, "pattern")} · "
                             + $"{(Text(arguments, "glob") is { Length: > 0 } glob ? glob : "any text file")} · in "
                             + $"{DirectoryOf(arguments)}",
            "list_directory" => $"list_directory · {DirectoryOf(arguments)} · depth {Number(arguments, "depth")}",
            "find_files" => $"find_files · {Text(arguments, "glob")} · in {DirectoryOf(arguments)}",
            _ => name,
        };
    }

    private static string WritePreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var path = Text(arguments, "path");
        var resolved = WorkspacePaths.ResolveWrite(scope.WorkspaceRoot, path, scope.Guards);
        if (!resolved.IsAllowed)
            return WorkspacePaths.ResultFor(resolved.Verdict, resolved.Relative.Length > 0 ? resolved.Relative : path);

        string? original = null;
        if (File.Exists(resolved.Full))
        {
            try
            {
                original = File.ReadAllText(resolved.Full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Cannot preview {resolved.Relative}: {ex.Message}";
            }
        }

        var edit = FileEdit.Apply(original, Text(arguments, "old_string"), Text(arguments, "new_string"),
            Flag(arguments, "replace_all"));
        return edit.Changed
            ? FileDiff.Unified(original ?? "", edit.Updated, resolved.Relative)
            : FileEdit.ResultFor(edit.Verdict, edit.Matches, resolved.Relative);
    }

    /// <summary>Names the shell and the directory as well as the command: what a person is approving is "this, in
    /// there", and a bare command line hides the part that decides how much damage it can do.</summary>
    private static string CommandPreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var shell = CommandShells.ForCurrent();
        var root = string.IsNullOrWhiteSpace(scope.WorkspaceRoot)
            ? "(no workspace)"
            : Path.GetFullPath(scope.WorkspaceRoot);
        var timeout = arguments.TryGetValue("timeout_seconds", out var seconds) && seconds.TryGetInt32(out var value)
            ? value
            : WorkspaceTools.DefaultCommandTimeoutSeconds;
        return $"{shell.Label} · {root} · idle timeout {timeout}s\n{Text(arguments, "command")}";
    }

    /// <summary>The facts needed to say yes to a new sandbox: does it exist, is it a repository, and is it
    /// something Hub already owns. Choosing the workspace is choosing the guard, so it is shown, not assumed.</summary>
    private static string WorkspacePreview(string path, WorkspaceToolScope scope, IReadOnlyList<string>? projectPaths)
    {
        if (string.IsNullOrWhiteSpace(path)) return "set_workspace · (no path given)";

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var facts = new List<string> { Directory.Exists(path) ? "exists" : "does not exist" };
        if (Directory.Exists(Path.Combine(path, ".git"))) facts.Add("git repository");
        if (projectPaths?.Any(known => string.Equals(known, path, comparison)) == true)
            facts.Add("registered Hub project");
        if (WorkspacePaths.IsProtected(path, scope.Guards)) facts.Add("protected location — will be refused");
        return $"set_workspace · {path}\n{string.Join(" · ", facts)}";
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
    /// "this is what the model gets to see", so the card names the window the target resolves to — and when the
    /// target is ambiguous the card says so, because the tool will refuse it the same way.</summary>
    private static string CapturePreview(IReadOnlyDictionary<string, JsonElement> arguments, WorkspaceToolScope scope)
    {
        var backend = CaptureBackends.ForCurrent();
        if (!backend.Available) return $"capture_screen · {backend.Refusal}";
        var target = Text(arguments, "target");
        if (string.IsNullOrWhiteSpace(target) && Flag(arguments, "fullscreen"))
            return $"capture_screen · fullscreen · {backend.Label}";
        if (scope.Screen is not { } host)
            return $"capture_screen · \"{target}\" · this build has no capture host for {backend.Label}, so the "
                   + "call will be refused";

        IReadOnlyList<CapturableWindow> windows = [];
        try
        {
            windows = host.Windows();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // A card still has to render, and it renders the target the model asked for.
        }
        var match = ScreenCapture.Find(windows, target);
        if (!match.Ok) return $"capture_screen · \"{target}\"\n{match.Refusal}";
        return match.Window is { } found
            ? $"capture_screen · window \"{found.Title}\" (pid {found.ProcessId}) · {found.Width}×{found.Height} · "
              + $"{backend.Label}"
            : $"capture_screen · fullscreen · {backend.Label}";
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
}
