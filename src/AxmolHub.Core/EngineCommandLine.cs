namespace AxmolHub.Core;

/// <summary>
/// Hub → 引擎 cmdline 的**唯一入口**。
///
/// 调用规则：<c>axmol &lt;subcmd&gt; args</c>。实际转发经随 exe 分发的
/// <c>Invoke-Axmol.ps1</c>，它只负责设好 <c>AX_ROOT</c> 再把参数交给
/// <c>&lt;engine&gt;/tools/cmdline/axmol.ps1</c>。
///
/// 与改造前的关键差别：**不再自建构建环境**。以前 Hub 在 C# 里手工拼
/// <c>INCLUDE</c>/<c>LIB</c>/<c>PATH</c>（替代 vcvars）并锁定自己 data-root 里的工具；
/// 现在环境继承父进程，工具链由引擎自己的 setup/1kiss 准备 —— 系统 VS、引擎树
/// <c>tools/external</c> 都由引擎去找。Hub 只做参数映射与结果判定。
/// </summary>
public sealed class EngineCommandLine(ProcessRunner runner, string wrapper)
{
    /// <summary>引擎脚本是 PowerShell（Unix 上由引擎自己的 pwshi.sh 保证 pwsh 存在）。</summary>
    private static string Shell => OperatingSystem.IsWindows() ? WindowsShell.PowerShell : "pwsh";

    /// <summary>环境准备专用包装脚本，与 <c>axmol</c> 的包装脚本同目录分发。</summary>
    private string SetupWrapper => Path.Combine(Path.GetDirectoryName(wrapper)!, "Invoke-AxmolSetup.ps1");

    public async Task<ProcessResult> RunAsync(EngineEntry engine, AxmolInvocation invocation, string workingDirectory,
        CancellationToken cancellation = default, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        var arguments = new List<string> { "-NoProfile", "-NonInteractive" };
        if (OperatingSystem.IsWindows()) arguments.AddRange(["-ExecutionPolicy", "Bypass"]);
        arguments.AddRange(["-File", wrapper, "-EngineRoot", engine.Path, invocation.SubCommand]);
        arguments.AddRange(invocation.Arguments);

        runner.Write("axmol: " + invocation);
        // environment: null = 继承父进程环境。这是刻意的：引擎依赖系统 VS / 自己的 tools/external。
        // 传 environment 时它只作为**覆盖**（例如 ANDROID_SERIAL 选择设备），不重建整份环境。
        // timeout 语义 = 连续无输出多久判卡死（默认 10 分钟），不是整条命令的墙钟上限；
        // 编译整棵引擎只要还在吐字就永不超时，故此处不再传 2 小时这种拍脑袋的总时长。
        var result = await runner.RunAsync(Shell, arguments, workingDirectory, environment: null, cancellation, timeout, overrides: environment);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"axmol {invocation.SubCommand} failed (exit {result.ExitCode}).\n{result.Error}\n{result.Output}");
        }

        return result;
    }

    /// <summary>
    /// 官方环境准备入口 <c>setup.ps1</c>。
    ///
    /// **刻意不抛**：调用方要按引擎的输出分类 —— 开发者模式未开时 setup.ps1 会
    /// <c>exit 0</c> 却什么都没装，只看异常是抓不到这种假成功的。
    /// 超时语义 = 连续无输出 10 分钟判卡死（<see cref="ProcessRunner"/> 默认）；setup 在下载 GB 级
    /// 工具链时只要还在吐进度就永不超时，因此不设总时长上限。
    /// </summary>
    public Task<ProcessResult> RunSetupAsync(EngineEntry engine, SetupOptions options, CancellationToken cancellation = default)
    {
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        var arguments = new List<string> { "-NoProfile", "-NonInteractive" };
        if (OperatingSystem.IsWindows()) arguments.AddRange(["-ExecutionPolicy", "Bypass"]);
        arguments.AddRange(["-File", SetupWrapper, "-EngineRoot", engine.Path]);
        if (options.Platform is { Length: > 0 } platform) arguments.AddRange(["-p", platform]);
        if (options.UpdateAdt) arguments.Add("-updateAdt");

        runner.Write($"axmol setup: {string.Join(' ', arguments)}");
        return runner.RunAsync(Shell, arguments, engine.Path, environment: null, cancellation);
    }
}
