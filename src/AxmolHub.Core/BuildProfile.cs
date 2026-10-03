using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// A read-only view of the engine's built-in tool-version manifest — <c>&lt;engine&gt;/1k/build.profiles</c>.
///
/// This is the **single source of truth for tool versions**: each engine version ships its own copy
/// (v2 and v3 differ substantially: NDK r23d/r27d, gradle 9.2.1/9.8.0, buildtools 35.0.0/36.0.0,
/// vs 17.0+/17.9+, axslcc 1.14.0/3.99.2). The Hub no longer holds any tool-version constants or
/// download URLs — versions follow the engine tree, so switching engines switches versions.
///
/// Parsing semantics **faithfully replicate** <c>ConvertFrom-Props</c> in <c>1k/manifest.ps1</c>:
/// skip <c>#</c> comment lines, split on the **first** <c>=</c> and trim both sides.
/// </summary>
public sealed class BuildProfile
{
    // Equivalent to ConvertFrom-Props' `^(.+?)\s*=\s*(.*)$`. Deliberately omit RegexOptions.Singleline:
    // PowerShell's -match also only matches within a single line.
    private static readonly Regex Entry = new(@"^(.+?)\s*=\s*(.*)$", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _values;

    private BuildProfile(Dictionary<string, string> values) => _values = values;

    /// <summary>Path of the manifest inside the engine tree.</summary>
    public static string FileFor(string engineRoot) => Path.Combine(engineRoot, "1k", "build.profiles");

    /// <summary>Failure to read is not an error (older engine trees may lack it); the caller decides whether to fail closed.</summary>
    public static bool TryLoad(string engineRoot, out BuildProfile? profile)
    {
        profile = null;
        var path = FileFor(engineRoot);
        if (!File.Exists(path)) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var match = Entry.Match(line);
            if (!match.Success) continue;
            values[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
        }

        profile = new BuildProfile(values);
        return true;
    }

    /// <summary>Throws when the manifest is missing — never guess default versions when the source of truth is unreadable.</summary>
    public static BuildProfile Load(string engineRoot) => TryLoad(engineRoot, out var profile)
        ? profile!
        : throw new FileNotFoundException($"Engine build profile not found: {FileFor(engineRoot)}");

    /// <summary>All key/value pairs (mainly for display and debugging).</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The raw value; returns <c>null</c> when missing (deliberately no default, to avoid drifting from the engine).</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    // ── Strongly-typed accessors for common keys ──
    public string? Axslcc => Get("axslcc");
    public string? Cmake => Get("cmake");
    public string? Ninja => Get("ninja");
    public string? Nuget => Get("nuget");
    public string? Vs => Get("vs");
    public string? Llvm => Get("llvm");
    public string? Jdk => Get("jdk");
    public string? CmdlineTools => Get("cmdlinetools");
    public string? Ndk => Get("ndk");
    public string? TargetSdk => Get("target_sdk");
    public string? MinSdk => Get("min_sdk");
    public string? Gradle => Get("gradle");
    public string? Agp => Get("agp");
    public string? BuildTools => Get("buildtools");
    public string? Emsdk => Get("emsdk");
}
