namespace AxmolHub.Core;

/// <summary>
/// 项目构建时传给引擎的 <c>-xc</c> 选项 —— **唯一组装点**。
///
/// 为什么必须唯一：<c>build</c> 与 <c>plan</c> 曾经各拼一套，结果 <c>plan --json</c> 漏报了
/// 选项（`PlatformBuildService.Plan` 不传 <c>additionalCmake</c>）。
/// plan 的职责是**忠实预告 build 会执行什么**，所以两者只能有一份实现。
/// </summary>
public static class ProjectBuildOptions
{
    /// <param name="prepareFiles">
    /// <c>true</c>（build）：允许写盘。<c>false</c>（plan）：只算路径、不产生副作用，但内容与 build 逐字一致。
    /// </param>
    public static string[] CmakeOptions(ProjectEntry project, EngineEntry engine, BuildTarget target, bool prepareFiles, EnginePrebuiltState prebuiltState)
    {
        var options = new List<string>();

        if (PrebuiltSettings.Load(project) is { Enabled: true })
        {
            // 引擎在目录不可用时会**静默退回源码构建**（AXGameEngineSetup.cmake:22），
            // 所以这里必须自己判准：只有 Ready 才把选项交出去，否则明确失败并指向引擎页。
            var availability = EnginePrebuilt.Inspect(engine, target, project.Configuration, prebuiltState);
            if (!availability.Usable) throw new PrebuiltUnavailableException(target.Name, project.Configuration, availability);
            options.Add("-DAX_PREBUILT_DIR=" + availability.RelativeDirectory);
        }

        return options.ToArray();
    }
}
