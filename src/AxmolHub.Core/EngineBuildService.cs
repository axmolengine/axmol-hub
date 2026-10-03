namespace AxmolHub.Core;

/// <summary>
/// 引擎根构建：把引擎编译成项目可复用的**预编译库**。
///
/// 与 <see cref="EngineSetupService"/> 同构 —— 都只是「替用户跑引擎自己的命令」，判定与记录留在 Hub。
/// 区别是这一步是**长任务**（编译整棵引擎，数分钟到数十分钟，产物数 GB），
/// 而且缺工具链时引擎可能顺手触发自己的 setup（会改全局环境），所以调用方必须先向用户确认。
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

        // 超时给 6 小时：这一步真的在编引擎（并可能在缺工具链时触发引擎自己的 setup）。
        await commandLine.RunAsync(engine, AxmolCommandMap.BuildEngine(target, configuration), engine.Path,
            cancellation, TimeSpan.FromHours(6));

        // 构建目录由引擎决定，所以**发现**它；找不到就不记 —— 绝不写一条指向不存在目录的「已构建」。
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
