using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>Locate evidence for a skill, not an engine to install or build. Registration describes a
/// candidate; only the request's sandbox can make its directory and version header readable.</summary>
internal static class AxmolSkillEngine
{
    public static string Describe(WorkspaceToolScope scope)
    {
        if (string.IsNullOrEmpty(scope.WorkspaceRoot)) return "No project workspace. No engine was inspected.";

        WorkspacePath DirectoryAt(string path) => WorkspacePaths.ResolveDirectory(scope.Sandbox, path, scope.Guards);
        WorkspacePath FileAt(string path)
        {
            var file = WorkspacePaths.ResolveRead(scope.Sandbox, path, scope.Guards);
            if (!file.IsAllowed && file.Verdict != WorkspacePathVerdict.NotAFile)
                throw new UnauthorizedAccessException();
            return file;
        }
        bool Exists(string path) => FileAt(path).IsAllowed;
        string ReadText(string path)
        {
            var file = FileAt(path);
            if (!file.IsAllowed) throw new FileNotFoundException();
            var snapshot = TextFile.Read(file.Full, 64 * 1024);
            return snapshot.State == TextFileState.Text ? snapshot.Text : throw new InvalidDataException();
        }

        const string denied = "Not authorized: the project or its engine source is outside the readable scope or linked. "
            + "Use the session's directory grant for the intended tree. Registration is not read permission.";
        try
        {
            var root = DirectoryAt(scope.WorkspaceRoot);
            if (!root.IsAllowed) return root.Verdict is WorkspacePathVerdict.NotADirectory or WorkspacePathVerdict.MissingWorkspace
                ? "Missing project workspace." : denied;
            var project = StateStore.ReadProject(root.Full, Exists, ReadText);
            var locked = Exists(StateStore.MetadataPath(root.Full));
            var source = DirectoryAt(Path.Combine(root.Full, "axmol"));
            var embedded = source.IsAllowed;
            if (!embedded)
            {
                // The engine template uses the embedded directory whenever present, even if it is incomplete.
                // Never substitute an installation merely because access to that directory was refused.
                if (source.Verdict != WorkspacePathVerdict.NotADirectory) return denied;
                if (!locked || string.IsNullOrWhiteSpace(project.Channel))
                    return $"Missing channel lock: .axproj identifies {project.Version} but not an installation. Confirm the build source.";
                var matches = scope.Engines.Where(e => e.Version == project.Version && e.Channel == project.Channel)
                    .DistinctBy(e => WorkspacePaths.CanonicalRoot(e.Path)).Take(2).ToArray();
                if (matches.Length == 0) return $"Missing: no registered engine matches {project.Version} ({project.Channel}).";
                if (matches.Length > 1) return $"Ambiguous: multiple installations match {project.Version} ({project.Channel}). "
                    + "Identify the actual build source before citing any of them.";
                source = DirectoryAt(matches[0].Path);
                if (!source.IsAllowed) return source.Verdict == WorkspacePathVerdict.NotADirectory
                    ? "Missing: registered engine directory is absent." : denied;
            }

            var core = StateStore.FindEngineCoreDirectory(source.Full, Exists);
            if (core is null) return "Missing: the selected source has no recognized version header. Do not substitute another tree.";
            var version = StateStore.VersionFromHeader(ReadText(Path.Combine(source.Full, core, "axmolver.h.in")));
            if (!embedded && version != project.Version)
                return $"Mismatch: the registered installation contains {version}, not the project lock {project.Version}.";
            var warning = embedded && version != project.Version ? $" Project lock says {project.Version}; cite the embedded version." : "";
            var target = locked ? $", {project.Platform}/{project.Configuration}" : "";
            return $"Matched {(embedded ? "embedded" : "registered")} candidate: {source.Full}\n"
                + $"Axmol {version}; core: {core}/; project: {project.ProjectType}{target}.{warning}\n"
                + "This locates a source by project layout or lock, not by evaluating CMake. Confirm the game's engine setup selects it before citing APIs.";
        }
        catch (UnauthorizedAccessException) { return denied; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            return "Missing or invalid project/engine metadata. Select an Axmol game project with a readable lock or .axproj and version header.";
        }
    }
}
