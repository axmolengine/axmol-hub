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
        => Prepare(project, engine, configureOnly, prebuiltState, null).Invocation;

    internal static (AxmolInvocation Invocation, bool UsesPrebuilt) Prepare(ProjectEntry project, EngineEntry engine,
        bool configureOnly, EnginePrebuiltState prebuiltState, BuildCleanup? cleanup)
    {
        var extra = ProjectBuildArguments.Parse(project.AdvancedBuildEnabled, project.AdditionalBuildParameters);
        var target = BuildTargets.Get(project.Platform);
        var usesPrebuilt = PrebuiltSettings.AppliesTo(project, target);
        var options = new List<string>();
        var force = ProjectService.NeedsReconfigure(project);
        if (EnginePrebuilt.Supported(target))
        {
            string? prebuiltDirectory = null;
            if (usesPrebuilt)
            {
                // An unusable library must not silently turn an enabled build into a source build.
                var availability = EnginePrebuilt.Inspect(engine, target, project.Configuration, prebuiltState);
                if (!availability.Usable) throw new PrebuiltUnavailableException(target.Name, project.Configuration, availability);
                prebuiltDirectory = availability.RelativeDirectory;
            }
            // Older templates test DEFINED, then check the path relative to AX_ROOT. An empty value
            // could therefore select the engine root; remove the entry entirely instead.
            options.Add(prebuiltDirectory is null ? "-UAX_PREBUILT_DIR" : "-DAX_PREBUILT_DIR=" + prebuiltDirectory);
            force |= PrebuiltCacheDiffers(project, target, prebuiltDirectory);
        }
        if (cleanup?.CmakeOption is { } output) options.Add(output);
        return (AxmolCommandMap.Build(target, project.Path, project.Configuration, configureOnly,
            options, force: force, additionalBuild: extra), usesPrebuilt);
    }

    private static bool PrebuiltCacheDiffers(ProjectEntry project, BuildTarget target, string? expected)
    {
        var cache = Path.Combine(project.Path, BuildCleanup.NativeDirectory(target), "CMakeCache.txt");
        if (!File.Exists(cache)) return false;
        const string entry = "AX_PREBUILT_DIR:";
        var line = File.ReadLines(cache).FirstOrDefault(line => line.StartsWith(entry, StringComparison.Ordinal));
        var equals = line?.IndexOf('=') ?? -1;
        var cached = equals >= 0 ? line![(equals + 1)..].Replace('\\', '/') : null;
        return !string.Equals(cached, expected, StringComparison.Ordinal);
    }
}
