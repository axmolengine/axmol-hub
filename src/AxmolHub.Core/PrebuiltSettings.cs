namespace AxmolHub.Core;

/// <summary>
/// 每项目选项：是否链接引擎的**预编译库**（<c>-DAX_PREBUILT_DIR=…</c>）。
///
/// 存放位置刻意跟随 <see cref="AndroidReleaseSettings"/>：写项目目录内的**独立** JSON，
/// 而不是塞进 <c>.axmol-hub.json</c> —— 后者被 <c>StateStore.ReadProject</c>、
/// <c>ProjectService.BuildAsync</c> 与 <c>RunWindowsAsync</c> 逐字段校验，
/// 加字段要同步改四处（改漏一处就是静默忽略），代价不值。
/// </summary>
public sealed class PrebuiltSettings
{
    public bool Enabled { get; set; }

    public static string PathFor(ProjectEntry project) => Path.Combine(project.Path, ".axmol-hub.prebuilt.json");

    public static PrebuiltSettings? Load(ProjectEntry project) => File.Exists(PathFor(project))
        ? System.Text.Json.JsonSerializer.Deserialize<PrebuiltSettings>(File.ReadAllText(PathFor(project)))
        : null;

    public static PrebuiltSettings Require(ProjectEntry project) => Load(project)
        ?? throw new InvalidOperationException("Configure prebuilt engine libraries for this project first.");

    public void Save(ProjectEntry project) => StateStore.WriteJson(PathFor(project), this);
}
