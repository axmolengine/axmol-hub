using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// One selectable mirror of an engine tree.
/// <paramref name="Storage"/> is the **technical** answer to "what does switching actually write" —
/// it is a path, not copy, so it is deliberately not localized (same reasoning as the executable
/// paths shown on the settings page).
/// </summary>
public sealed record MirrorOption(string Id, string TextKey, string Storage);

// ─────────────────────────────────────────────────────────────────────────────
// Engine mirror switching: where an already-installed engine fetches its dependencies and tools.
//
// This is **not** a Hub preference and it is **not** stored in the Hub data root: the choice lives
// inside the engine tree, and the engine's own scripts are the only consumer. Two layouts, both
// taken verbatim from the engine:
//
//   v2 (`core/` layout) — 1k/fetch.ps1:116 and 1k/resolv-url.ps1:43
//       $mirror = if (!(Test-Path '1k/.gitee' -PathType Leaf)) { 'github' } else { 'gitee' }
//     i.e. the mirror is a **sentinel file**: an empty `1k/.gitee` selects gitee, its absence
//     selects github. There are exactly two choices and no third value.
//
//   v3 (`axmol/` layout) — 1k/.env holds `active_mirror=<name>`, and 1k/fetch.ps1:137 /
//     resolv-url.ps1:56 / 1kiss.ps1:504 look the name up in `1k/sources.json`:
//       $url = $sources_conf.dependencies.$name.sources.$active_mirror
//     So **the valid values are data, not constants**: they are the keys declared in sources.json,
//     and they change when the engine changes. Hub reads them instead of hard-coding a list.
//     When a package doesn't declare that mirror, 1kiss.ps1:508 warns and falls back to 'origin' —
//     that fallback is the engine's, not ours, and the UI says so.
//
// The v2/v3 judgment has exactly one source: <see cref="StateStore.FindEngineCoreDirectory"/>, the
// same point import validation and the acceptance checks already use.
// ─────────────────────────────────────────────────────────────────────────────
public static class EngineMirror
{
    /// <summary>v2 default: no sentinel file, the engine reads <c>github</c>.</summary>
    public const string GithubId = "github";

    /// <summary>v3 default: the <c>origin</c> entry of every package in <c>1k/sources.json</c>.</summary>
    public const string OriginId = "origin";

    public const string AtomGitId = "atomgit";
    public const string GiteeId = "gitee";

    private const string EnvFile = ".env";
    private const string GiteeMarker = ".gitee";
    private const string MirrorKey = "active_mirror";

    /// <summary>The relative paths Hub touches. Shown in the UI so the switch is never a black box.</summary>
    public const string EnvRelativePath = "1k/" + EnvFile;
    public const string MarkerRelativePath = "1k/" + GiteeMarker;

    /// <summary>v3 = <c>axmol/axmolver.h.in</c>; the version is not guessed from the version number.</summary>
    public static bool IsV3(EngineEntry engine) => StateStore.FindEngineCoreDirectory(engine.Path) == "axmol";

    /// <summary>v2 = <c>core/axmolver.h.in</c>.</summary>
    public static bool IsV2(EngineEntry engine) => StateStore.FindEngineCoreDirectory(engine.Path) == "core";

    /// <summary>Whether Hub knows how to switch this tree. An unrecognized tree gets no button, rather than a button that fails.</summary>
    public static bool IsSupported(EngineEntry engine) => IsV3(engine) || IsV2(engine);

    /// <summary>The mirrors this engine offers. Empty when the tree is neither v2 nor v3.</summary>
    public static IReadOnlyList<MirrorOption> Options(EngineEntry engine)
    {
        if (IsV2(engine))
        {
            return
            [
                new(GithubId, "MirrorGithub", MarkerRelativePath + " (absent)"),
                new(GiteeId, "MirrorGitee", MarkerRelativePath + " (empty file)"),
            ];
        }

        if (!IsV3(engine))
        {
            return [];
        }

        return DeclaredMirrors(engine)
            .Select(id => new MirrorOption(id, TextKey(id), EnvRelativePath + " · " + MirrorKey + "=" + id))
            .ToArray();
    }

    /// <summary>
    /// The mirror currently in effect; <c>""</c> when the tree is not recognized.
    /// A missing <c>1k/.env</c> means <c>origin</c> — that is the engine's own default
    /// (1kiss.ps1:488, fetch.ps1:127), so Hub must not invent a different answer.
    /// </summary>
    public static string Current(EngineEntry engine)
    {
        if (IsV2(engine)) return File.Exists(MarkerPath(engine)) ? GiteeId : GithubId;
        if (IsV3(engine)) return ReadEnvValue(engine, MirrorKey) ?? OriginId;
        return "";
    }

    /// <summary>
    /// Writes the choice into the engine tree. Throws when the tree is unrecognized or the mirror
    /// isn't one this engine declares — the UI only offers valid choices, so reaching either throw
    /// means the two sides disagree, which must be loud.
    /// </summary>
    public static void Apply(EngineEntry engine, string mirror)
    {
        if (IsV2(engine))
        {
            switch (mirror)
            {
                case GiteeId:
                    // The file's **existence** is the whole signal; an empty file is the documented form.
                    File.WriteAllText(MarkerPath(engine), "");
                    return;
                case GithubId:
                    if (File.Exists(MarkerPath(engine))) File.Delete(MarkerPath(engine));
                    return;
                default: throw new InvalidOperationException("This engine does not support the mirror '" + mirror + "'.");
            }
        }

        if (IsV3(engine))
        {
            if (!DeclaredMirrors(engine).Contains(mirror))
            {
                throw new InvalidOperationException("This engine does not declare the mirror '" + mirror + "' in 1k/sources.json.");
            }

            WriteEnvValue(engine, MirrorKey, mirror);
            return;
        }

        throw new InvalidOperationException("This engine's mirror mechanism is not recognized (1k/.env or 1k/.gitee required).");
    }

    /// <summary>
    /// Copy key for a mirror id, or <c>""</c> for one that has no entry (a name the engine declares
    /// in <c>1k/sources.json</c> but Hub has no copy for). The UI then prints the raw id: printing a
    /// *wrong* localized name would be worse than printing the identifier the engine itself uses.
    /// </summary>
    public static string TextKey(string id) => id switch
    {
        AtomGitId => "MirrorAtomGit",
        GiteeId => "MirrorGitee",
        GithubId => "MirrorGithub",
        OriginId => "MirrorOrigin",
        _ => "",
    };

    /// <summary>
    /// The mirror names declared in <c>1k/sources.json</c>: the union of every <c>sources</c> object's
    /// keys, wherever it appears (dependencies and devtools both honour <c>active_mirror</c>).
    ///
    /// When the file is missing or unreadable, a **fixed fallback list** is returned instead of an
    /// empty one: a v3 tree always understands these three (they are what the engine ships today),
    /// and an empty list would leave the user with no way to escape a slow origin.
    /// </summary>
    private static List<string> DeclaredMirrors(EngineEntry engine)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(engine.Path, "1k", "sources.json");
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                CollectMirrors(document.RootElement, found);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Unreadable data falls through to the fallback list; a corrupt sources.json is
                // already going to break the engine's own fetch, and Hub is not the one to fix it.
            }
        }

        if (found.Count == 0) return [OriginId, AtomGitId, GiteeId];

        var ordered = new List<string>();
        if (found.Contains(OriginId)) ordered.Add(OriginId);
        ordered.AddRange(found.Where(id => id != OriginId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    private static void CollectMirrors(JsonElement element, HashSet<string> found)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("sources") && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var mirror in property.Value.EnumerateObject())
                {
                    if (mirror.Name.Length > 0) found.Add(mirror.Name);
                }

                continue;
            }

            CollectMirrors(property.Value, found);
        }
    }

    private static string MarkerPath(EngineEntry engine) => Path.Combine(engine.Path, "1k", GiteeMarker);

    private static string EnvPath(EngineEntry engine) => Path.Combine(engine.Path, "1k", EnvFile);

    private static string? ReadEnvValue(EngineEntry engine, string key)
    {
        var path = EnvPath(engine);
        if (!File.Exists(path)) return null;
        var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var match = pattern.Match(line);
            if (match.Success && match.Groups[1].Value is { Length: > 0 } value) return value;
        }

        return null;
    }

    /// <summary>
    /// Rewrites one <c>key=value</c> line of <c>1k/.env</c>, keeping every other line (in particular
    /// <c>android_sdk_root</c>) untouched. The file is a properties file the engine owns; replacing it
    /// wholesale would drop settings the engine's setup wrote earlier.
    /// </summary>
    private static void WriteEnvValue(EngineEntry engine, string key, string value)
    {
        var path = EnvPath(engine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        // A trailing newline yields a final empty element; keeping it would append one blank line per
        // switch, so switching back and forth would slowly grow the file.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=", RegexOptions.IgnoreCase);
        var replaced = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!pattern.IsMatch(lines[index])) continue;
            lines[index] = key + "=" + value;
            replaced = true;
            break;
        }

        if (!replaced) lines.Add(key + "=" + value);
        File.WriteAllText(path, string.Join(newLine, lines) + newLine);
    }
}
