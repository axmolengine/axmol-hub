namespace AxmolHub.Core;

/// <summary>
/// Per-project option: whether to link the engine's **prebuilt libraries** (<c>-DAX_PREBUILT_DIR=…</c>).
///
/// The storage location deliberately follows <see cref="AndroidReleaseSettings"/>: a **separate** JSON file
/// in the project directory, rather than being stuffed into <c>.axmol-hub.json</c> — the latter is validated
/// field by field in <c>StateStore.ReadProject</c>, <c>ProjectService.BuildAsync</c> and <c>RunWindowsAsync</c>,
/// so adding a field would require changing four places in lockstep (missing one means silently ignored),
/// a cost that isn't worth it.
/// </summary>
public sealed class PrebuiltSettings
{
    public bool Enabled { get; set; }

    public static string PathFor(ProjectEntry project) => Path.Combine(project.Path, ".axmol-hub.prebuilt.json");

    public static PrebuiltSettings? Load(ProjectEntry project) => StateStore.ReadJson<PrebuiltSettings>(PathFor(project), ignoreErrors: false);

    internal static bool AppliesTo(ProjectEntry project, BuildTarget target)
        => EnginePrebuilt.Supported(target) && Load(project)?.Enabled == true;

    public void Save(ProjectEntry project) => StateStore.WriteJson(PathFor(project), this);
}
