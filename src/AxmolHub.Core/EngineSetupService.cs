namespace AxmolHub.Core;

/// <summary>传给官方 <c>setup.ps1</c> 的选项。</summary>
public sealed record SetupOptions(string? Platform = null, bool UpdateAdt = false);

/// <summary>
/// 环境准备的结局。
///
/// 刻意**不**在 Hub 侧发明判据：只有能对到引擎脚本原文的两种结局才单独分类，
/// 其余一律归 <see cref="Failed"/> 并把引擎原文留给用户看。
/// </summary>
public enum SetupOutcome
{
    Success,

    /// <summary>Windows 10+ 未开开发者模式。setup.ps1 打开设置页后 **<c>exit 0</c>** —— 退出码是假成功。</summary>
    DeveloperModeBlocked,

    /// <summary>需要提权（改执行策略 / 装工具），UAC 对话框无人点。</summary>
    NeedsElevation,

    /// <summary>引擎自己的 bash 入口引导（Unix 无 pwsh 时）。</summary>
    NeedsPowerShell,

    Cancelled,
    Failed,
}

public sealed record SetupResult(SetupOutcome Outcome, int ExitCode, string Output, string Error)
{
    public bool Succeeded => Outcome == SetupOutcome.Success;

    public string Describe() => Outcome switch
    {
        SetupOutcome.Success => "Engine setup completed.",
        SetupOutcome.DeveloperModeBlocked => "Windows Developer Mode is off. setup.ps1 opened the settings page and exited 0 without preparing tools. Enable it and run setup again.",
        SetupOutcome.NeedsElevation => "setup.ps1 needs elevation (execution policy or tool install) and the UAC prompt was not accepted.",
        SetupOutcome.NeedsPowerShell => "setup.ps1 needs PowerShell 7 (pwsh) on this host before it can prepare tools.",
        SetupOutcome.Cancelled => "Engine setup was cancelled.",
        _ => $"Engine setup failed (exit {ExitCode}).",
    };
}

/// <summary>
/// 环境准备 —— 唯一实现就是**跑引擎自己的 <c>setup.ps1</c>**。
///
/// Hub 不再下载或安装任何工具链：安装是 <c>1k/1kiss.ps1</c> 的职责，
/// 落点 <c>&lt;engine&gt;/tools/external</c>，版本真源 <see cref="BuildProfile"/>。
/// 这里做的只有三件事：把命令跑起来、**把结局分类对**、把原文留给用户。
/// </summary>
public sealed class EngineSetupService(EngineCommandLine commandLine)
{
    // 下面两条标记都取自 setup.ps1 原文，不是猜的：
    //   setup.ps1:128  'axmol: Developer Mode is currently disabled on this Windows 10+ device.'
    //   setup.ps1:201  "Setting system installed powershell execution policy ... please click 'YES' on UAC dialog"
    private const string DeveloperModeMarker = "Developer Mode is currently disabled";
    private const string ElevationMarker = "please click 'YES' on UAC dialog";
    private const string PowerShellMarker = "pwshi.sh";

    /// <summary>这棵树是否已经跑过 setup（<c>tools/external</c> 下有东西）。</summary>
    public static bool IsPrepared(EngineEntry engine)
    {
        var root = EngineToolchain.ToolRoot(engine);
        return Directory.Exists(root) && Directory.EnumerateDirectories(root).Any();
    }

    public async Task<SetupResult> RunAsync(EngineEntry engine, SetupOptions options, CancellationToken cancellation = default)
    {
        if (!File.Exists(Path.Combine(engine.Path, "setup.ps1")))
        {
            return new(SetupOutcome.Failed, 0, "", $"Engine tree has no setup.ps1: {engine.Path}");
        }

        var result = await commandLine.RunSetupAsync(engine, options, cancellation);
        var text = result.Output + "\n" + result.Error;
        return new(Classify(result.ExitCode, text), result.ExitCode, result.Output, result.Error);
    }

    /// <summary>
    /// 分类只看**引擎自己打印的原文**。特别地：开发者模式未开时 setup.ps1 会 <c>exit 0</c>，
    /// 只看退出码就会把它当成准备成功 —— 这是必须识破的假成功。
    /// </summary>
    private static SetupOutcome Classify(int exitCode, string text)
    {
        if (text.Contains(DeveloperModeMarker, StringComparison.Ordinal)) return SetupOutcome.DeveloperModeBlocked;
        if (text.Contains(ElevationMarker, StringComparison.Ordinal)) return SetupOutcome.NeedsElevation;
        if (text.Contains(PowerShellMarker, StringComparison.Ordinal) && exitCode != 0) return SetupOutcome.NeedsPowerShell;
        return exitCode == 0 ? SetupOutcome.Success : SetupOutcome.Failed;
    }
}
