namespace AxmolHub.Core;

/// <summary>
/// The <c>-xc</c> options passed to the engine during project builds — the **single assembly point**.
///
/// Why it must be single: <c>build</c> and <c>plan</c> once each assembled their own set, and as a result
/// <c>plan --json</c> omitted options (`PlatformBuildService.Plan` didn't pass <c>additionalCmake</c>).
/// plan's job is to **faithfully preview what build will execute**, so the two must share one implementation.
/// </summary>
public static class ProjectBuildOptions
{
    /// <summary>Plan and execution use this exact invocation, including pending engine reconfiguration.</summary>
    public static AxmolInvocation Invocation(ProjectEntry project, EngineEntry engine, bool configureOnly, EnginePrebuiltState prebuiltState)
    {
        var extra = ProjectBuildArguments.Parse(project.AdvancedBuildEnabled, project.AdditionalBuildParameters);
        var target = BuildTargets.Get(project.Platform);
        return AxmolCommandMap.Build(target, project.Path, project.Configuration, configureOnly,
            CmakeOptions(project, engine, target, prebuiltState), force: ProjectService.NeedsReconfigure(project), additionalBuild: extra);
    }

    private static string[] CmakeOptions(ProjectEntry project, EngineEntry engine, BuildTarget target, EnginePrebuiltState prebuiltState)
    {
        var options = new List<string>();

        if (PrebuiltSettings.Load(project) is { Enabled: true })
        {
            // The engine **silently falls back to a source build** when the directory is unusable
            // (AXGameEngineSetup.cmake:22), so this must judge precisely on its own: only hand the option
            // over when Ready; otherwise fail explicitly and point at the Engines page.
            var availability = EnginePrebuilt.Inspect(engine, target, project.Configuration, prebuiltState);
            if (!availability.Usable) throw new PrebuiltUnavailableException(target.Name, project.Configuration, availability);
            options.Add("-DAX_PREBUILT_DIR=" + availability.RelativeDirectory);
        }

        return options.ToArray();
    }
}
