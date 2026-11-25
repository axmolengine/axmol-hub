using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public enum AxmolEngineStatus
{
    NoWorkspace,
    NotAxmolWorkspace,
    MatchedEmbedded,
    MatchedRegistered,
    MatchedEngineCheckout,
    NotAuthorized,
    Missing,
    Ambiguous,
    Mismatch,
    InvalidMetadata,
}

public sealed record AxmolEngineResolution(
    AxmolEngineStatus Status,
    string Message,
    string? EngineRoot = null,
    string? CoreDirectory = null,
    string? Version = null,
    int MajorVersion = 0,
    string? Revision = null,
    string? ProjectType = null,
    string? Target = null,
    string? Warning = null)
{
    public bool IsMatched => Status is AxmolEngineStatus.MatchedEmbedded
        or AxmolEngineStatus.MatchedRegistered
        or AxmolEngineStatus.MatchedEngineCheckout;
}

/// <summary>Locate evidence for Axmol skills, workspace digests, and local API indexing. Registration describes a
/// candidate; only the request's sandbox can make its directory and version header readable.</summary>
public static class AxmolSkillEngine
{
    private const string DeniedMessage = "Not authorized: the project or its engine source is outside the readable scope or linked. "
        + "Use the session's directory grant for the intended tree. Registration is not read permission.";

    public static string Describe(WorkspaceToolScope scope) => Resolve(scope).Message;

    public static AxmolEngineResolution Resolve(WorkspaceToolScope scope)
    {
        if (string.IsNullOrEmpty(scope.WorkspaceRoot))
            return new(AxmolEngineStatus.NoWorkspace, "No project workspace. No engine was inspected.");

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
        string? TryReadSafeText(string path)
        {
            var file = WorkspacePaths.ResolveRead(scope.Sandbox, path, scope.Guards);
            if (!file.IsAllowed) return null;
            var snapshot = TextFile.Read(file.Full, 16 * 1024);
            return snapshot.State == TextFileState.Text ? snapshot.Text : null;
        }

        try
        {
            var root = DirectoryAt(scope.WorkspaceRoot);
            if (!root.IsAllowed)
                return root.Verdict is WorkspacePathVerdict.NotADirectory or WorkspacePathVerdict.MissingWorkspace
                    ? new(AxmolEngineStatus.Missing, "Missing project workspace.")
                    : new(AxmolEngineStatus.NotAuthorized, DeniedMessage);

            // Case 1: The workspace itself is an Axmol engine checkout (e.g. local v2/v3 source repository).
            var directCore = StateStore.FindEngineCoreDirectory(root.Full, Exists);
            if (directCore is not null)
            {
                var version = StateStore.VersionFromHeader(ReadText(Path.Combine(root.Full, directCore, "axmolver.h.in")));
                var revision = TryReadGitRevision(root.Full, TryReadSafeText);
                var revSuffix = revision is not null ? $" (rev {revision})" : "";
                var summary = $"Matched engine checkout: {root.Full}\n"
                    + $"Axmol {version}{revSuffix}; core: {directCore}/; project: engine.\n"
                    + "Version comes from axmolver.h.in; cite exact relative file:line from this tree.";
                return new(AxmolEngineStatus.MatchedEngineCheckout, summary, root.Full, directCore, version,
                    ParseMajor(version), revision, "engine");
            }

            var hasLock = Exists(StateStore.MetadataPath(root.Full));
            var hasAxproj = Exists(Path.Combine(root.Full, ".axproj"));
            var embeddedDir = DirectoryAt(Path.Combine(root.Full, "axmol"));
            var hasEmbedded = embeddedDir.IsAllowed;

            // The engine template uses the embedded directory whenever present, even if it is incomplete.
            // Never substitute an installation merely because access to that directory was refused.
            if (!hasEmbedded && embeddedDir.Verdict != WorkspacePathVerdict.NotADirectory)
                return new(AxmolEngineStatus.NotAuthorized, DeniedMessage);

            if (!hasEmbedded && !hasLock && !hasAxproj)
                return new(AxmolEngineStatus.NotAxmolWorkspace,
                    "No Axmol project metadata (.axproj, .axmol-hub.json, or embedded axmol/) was found in this workspace.");

            ProjectEntry? project = null;
            if (hasLock || hasAxproj)
                project = StateStore.ReadProject(root.Full, Exists, ReadText);

            WorkspacePath source;
            if (hasEmbedded)
            {
                source = embeddedDir;
            }
            else
            {
                if (!hasLock || project is null || string.IsNullOrWhiteSpace(project.Channel))
                    return new(AxmolEngineStatus.Missing,
                        $"Missing channel lock: .axproj identifies {project?.Version ?? "unknown"} but not an installation. Confirm the build source.");
                var matches = scope.Engines.Where(e => e.Version == project.Version && e.Channel == project.Channel)
                    .DistinctBy(e => WorkspacePaths.CanonicalRoot(e.Path)).Take(2).ToArray();
                if (matches.Length == 0)
                    return new(AxmolEngineStatus.Missing,
                        $"Missing: no registered engine matches {project.Version} ({project.Channel}).");
                if (matches.Length > 1)
                    return new(AxmolEngineStatus.Ambiguous,
                        $"Ambiguous: multiple installations match {project.Version} ({project.Channel}). "
                        + "Identify the actual build source before citing any of them.");
                source = DirectoryAt(matches[0].Path);
                if (!source.IsAllowed)
                    return source.Verdict == WorkspacePathVerdict.NotADirectory
                        ? new(AxmolEngineStatus.Missing, "Missing: registered engine directory is absent.")
                        : new(AxmolEngineStatus.NotAuthorized, DeniedMessage);
            }

            var core = StateStore.FindEngineCoreDirectory(source.Full, Exists);
            if (core is null)
                return new(AxmolEngineStatus.Missing,
                    "Missing: the selected source has no recognized version header. Do not substitute another tree.");
            var headerVersion = StateStore.VersionFromHeader(ReadText(Path.Combine(source.Full, core, "axmolver.h.in")));
            if (!hasEmbedded && project is not null && headerVersion != project.Version)
                return new(AxmolEngineStatus.Mismatch,
                    $"Mismatch: the registered installation contains {headerVersion}, not the project lock {project.Version}.");

            var revisionId = TryReadGitRevision(source.Full, TryReadSafeText)
                ?? (hasAxproj ? TryReadAxprojRevision(TryReadSafeText(Path.Combine(root.Full, ".axproj"))) : null);
            var warning = hasEmbedded && project is not null && headerVersion != project.Version
                ? $" Project lock says {project.Version}; cite the embedded version." : "";
            var target = hasLock && project is not null ? $"{project.Platform}/{project.Configuration}" : null;
            var targetSuffix = target is not null ? $", {target}" : "";
            var projectType = project?.ProjectType ?? "cpp";
            var revText = revisionId is not null ? $" (rev {revisionId})" : "";
            var text = $"Matched {(hasEmbedded ? "embedded" : "registered")} candidate: {source.Full}\n"
                + $"Axmol {headerVersion}{revText}; core: {core}/; project: {projectType}{targetSuffix}.{warning}\n"
                + "This locates a source by project layout or lock, not by evaluating CMake. Confirm the game's engine setup selects it before citing APIs.";
            return new(
                hasEmbedded ? AxmolEngineStatus.MatchedEmbedded : AxmolEngineStatus.MatchedRegistered,
                text,
                source.Full,
                core,
                headerVersion,
                ParseMajor(headerVersion),
                revisionId,
                projectType,
                target,
                string.IsNullOrEmpty(warning) ? null : warning.Trim());
        }
        catch (UnauthorizedAccessException) { return new(AxmolEngineStatus.NotAuthorized, DeniedMessage); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            return new(AxmolEngineStatus.InvalidMetadata,
                "Missing or invalid project/engine metadata. Select an Axmol game project with a readable lock or .axproj and version header.");
        }
    }

    private static int ParseMajor(string version)
    {
        var dot = version.IndexOf('.');
        return dot > 0 && int.TryParse(version[..dot], out var major) ? major : 0;
    }

    private static string? TryReadAxprojRevision(string? axprojText)
    {
        if (string.IsNullOrWhiteSpace(axprojText)) return null;
        var match = Regex.Match(axprojText, @"(?m)^engine_version\s*=\s*\d+\.\d+\.\d+-([0-9a-fA-F]{7,40})\b");
        return match.Success ? match.Groups[1].Value[..Math.Min(12, match.Groups[1].Value.Length)] : null;
    }

    private static string? TryReadGitRevision(string root, Func<string, string?> tryReadText)
    {
        var head = tryReadText(Path.Combine(root, ".git", "HEAD"))?.Trim();
        if (string.IsNullOrEmpty(head)) return null;
        string? commit = null;
        if (head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
        {
            var refPath = head[4..].Trim().Replace('/', Path.DirectorySeparatorChar);
            commit = tryReadText(Path.Combine(root, ".git", refPath))?.Trim();
        }
        else
        {
            commit = head;
        }
        if (string.IsNullOrEmpty(commit) || commit.Length < 7 || !commit.All(Uri.IsHexDigit)) return null;
        return commit[..Math.Min(12, commit.Length)];
    }
}

/// <summary>Builds a compact, deterministic Axmol workspace digest injected into Plan/Agent system prompts only when
/// the active workspace is a recognized Axmol game project or engine checkout.</summary>
public static class AxmolProjectDigest
{
    public static string Build(WorkspaceToolScope scope)
    {
        var resolution = AxmolSkillEngine.Resolve(scope);
        if (resolution.Status is AxmolEngineStatus.NoWorkspace
            or AxmolEngineStatus.NotAxmolWorkspace
            or AxmolEngineStatus.InvalidMetadata)
            return "";

        if (!resolution.IsMatched)
        {
            return "\n\n# Axmol workspace context\n"
                + resolution.Message + "\n"
                + $"Built-in Axmol workflows (call axmol_skill_read with exact ID): {AxmolSkillCatalog.DigestList()}.";
        }

        var series = resolution.MajorVersion switch
        {
            2 => "v2 LTS (core/, C++17/20, GLSL shaders, ax::backend, tolua++ Lua)",
            3 => "v3 (axmol/, C++23, HLSL shaders, ax::rhi, InputSystem/PointerEventListener, sol2 Lua manifests)",
            _ => $"v{resolution.MajorVersion} ({resolution.CoreDirectory}/)",
        };
        var kindLabel = resolution.Status switch
        {
            AxmolEngineStatus.MatchedEmbedded => "embedded engine",
            AxmolEngineStatus.MatchedRegistered => "registered engine",
            _ => "engine checkout",
        };
        var rev = resolution.Revision is not null ? $", rev {resolution.Revision}" : "";
        var target = resolution.Target is not null ? $", target {resolution.Target}" : "";
        var warning = resolution.Warning is not null ? $"\n- Note: {resolution.Warning}" : "";

        return "\n\n# Axmol workspace context\n"
            + $"- Source ({kindLabel}): {resolution.EngineRoot}\n"
            + $"- Engine: Axmol {resolution.Version}{rev} — {series}; project: {resolution.ProjectType}{target}.{warning}\n"
            + "- Version rule: always cite the version from <core>/axmolver.h.in above, never the top heading of CHANGELOG.md. For game projects, still confirm CMakeLists.txt / AXGameEngineSetup.cmake selects this tree.\n"
            + $"- Workflows: call axmol_skill_read with one of [{AxmolSkillCatalog.DigestList()}] for task-specific v2/v3 rules.\n"
            + "- Fast lookup: call axmol_api_query BEFORE broad fs_grep/fs_find sweeps to locate C++ declarations and Lua bindings with exact file:line, then verify only the short needed windows with fs_read.";
    }
}
