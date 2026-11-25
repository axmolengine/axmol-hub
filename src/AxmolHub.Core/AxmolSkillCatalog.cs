namespace AxmolHub.Core;

public sealed record AxmolSkillEntry(string Id, string Title, string Summary);

/// <summary>Single catalog of built-in Axmol v2/v3 workflow skills embedded in Hub Core.</summary>
public static class AxmolSkillCatalog
{
    public static IReadOnlyList<AxmolSkillEntry> All { get; } =
    [
        new("axmol-v3-migration",
            "v2 LTS to v3 migration and architecture differences",
            "Directory layout (core/ vs axmol/), RHI (ax::backend vs ax::rhi), InputSystem/PointerEventListener, JobSystem, C++23, and Lua sol2 breaking changes."),
        new("axmol-sprite-shader",
            "Sprite and custom shaders (v2 GLSL vs v3 HLSL)",
            "Sprite program-state APIs, ProgramManager, v2 GLSL (.fsh/.vsh) vs v3 HLSL (_fs.hlsl/_vs.hlsl), AXBuildHelpers.cmake discovery, -forceConfig, and packaging."),
        new("axmol-lua-input",
            "Lua bindings and input/events (v2 tolua++ vs v3 sol2)",
            "v2 auto/manual EventListenerTouchOneByOne vs v3 generated manifests/adapters PointerEventListener, Node hit-test, and Lua value-table migration."),
        new("axmol-build-cmake",
            "Build, CMake, and cross-platform Axmol CLI",
            "PowerShell axmol CLI (new/build/run/genbindings), -forceConfig, AXGameEngineSetup/AXGameSourceSetup, CMakeOptions, RHI backend flags, and packaging pitfalls."),
    ];

    public static bool TryRead(string id, out string content)
    {
        content = "";
        if (!All.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))) return false;
        using var resource = typeof(AxmolSkillCatalog).Assembly.GetManifestResourceStream(id);
        if (resource is null) return false;
        using var reader = new StreamReader(resource);
        content = TextEol.ToLf(reader.ReadToEnd());
        return true;
    }

    public static string FormatList()
        => "Available Axmol skills (pass exact ID to axmol_skill_read):\n"
           + string.Join('\n', All.Select(entry => $"- {entry.Id}: {entry.Summary}"));

    public static string DigestList()
        => string.Join(", ", All.Select(entry => entry.Id));
}
