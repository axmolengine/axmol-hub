namespace AxmolHub.Core;

/// <summary>
/// Engine root build: compiles the engine into **prebuilt libraries** reusable by projects.
///
/// Structurally parallel to <see cref="EngineSetupService"/> — both just "run the engine's own command on
/// the user's behalf", with the judgment and recording kept in the Hub. The difference is that this step is
/// a **long task** (compiling the whole engine, minutes to tens of minutes, several GB of artifacts), and
/// when the toolchain is missing the engine may trigger its own setup as a side effect (which changes the
/// global environment), so the caller must confirm with the user first.
/// </summary>
public sealed class EngineBuildService(ProcessRunner runner, EngineCommandLine commandLine, EnginePrebuiltState prebuiltState)
{
    public async Task<EngineBuildRecord> BuildAsync(EngineEntry engine, BuildTarget target, string configuration,
        CancellationToken cancellation = default)
    {
        if (!EnginePrebuilt.Supported(target))
        {
            throw new NotSupportedException($"Prebuilt engine libraries are only supported for Windows targets; {target.Id} is a '{target.Family}' target.");
        }

        BuildConfigurations.Validate(configuration);
        runner.Write($"Building Axmol {engine.Version} for {target.Name} ({configuration}); this compiles the whole engine.");

        // No total time cap: compiling the whole engine takes minutes to tens of minutes and several GB of
        // artifacts, so let it run as long as it keeps producing output. The timeout judgment is done by
        // ProcessRunner as "10 minutes with no output" = a stall.
        await commandLine.RunAsync(engine, AxmolCommandMap.BuildEngine(target, configuration), engine.Path,
            cancellation);

        // The build directory is decided by the engine, so **discover** it; if not found, don't record — never write a "built" entry pointing at a nonexistent directory.
        var directory = EnginePrebuilt.Discover(engine, configuration)
            ?? throw new InvalidOperationException(
                $"The engine build finished but produced no usable prebuilt directory under {engine.Path} " +
                $"(expected a build directory with CMakeCache.txt, lib/{configuration}, bin/{configuration} and runtime/axslc).");

        var (platform, architecture) = AxmolCommandMap.Target(target);
        var record = new EngineBuildRecord
        {
            EnginePath = engine.Path,
            EngineVersion = engine.Version,
            Channel = engine.Channel,
            Target = target.Id,
            Platform = platform,
            Architecture = architecture,
            Configuration = configuration,
            BuildDirectory = directory,
            EngineToken = ProjectService.EngineInstallationToken(engine),
            BuiltAt = DateTimeOffset.Now,
        };
        prebuiltState.Save(engine, record);
        runner.Write($"Prebuilt engine libraries ready: {engine.Path}/{directory} ({configuration}).");
        return record;
    }
}
