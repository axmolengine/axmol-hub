using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>信封里的失败描述。</summary>
public sealed record CliError(string Type, string Message);

/// <summary>
/// CLI <c>--json</c> 的统一信封。契约文档：<c>docs/cli-json-contract.md</c>。
/// </summary>
public sealed class CliEnvelope
{
    /// <summary>破坏性变更时递增；消费方应先校验它再解析 <see cref="Data"/>。</summary>
    public int Schema { get; init; } = CliContract.SchemaVersion;

    public string Command { get; init; } = "";

    /// <summary>这次调用是否正常完成。注意 <c>run/serve/deploy</c> 下它与 ExitCode 可以不同向。</summary>
    public bool Ok { get; init; }

    /// <summary>进程退出码，与不带 <c>--json</c> 时完全一致。</summary>
    public int ExitCode { get; init; }

    /// <summary>动词各自的载荷。<c>ok:false</c> 且为异常失败时不存在。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliError? Error { get; init; }
}

/// <summary>
/// 契约的编码入口。放在 Core 而不是 Cli，是为了让 P1 的 MCP Server 序列化**同一批载荷类型**
/// （见 docs/ai-first-plan.md §1.1"工具只定义一次"）；放 Cli 里迟早会出现两份会漂移的定义。
/// 不引入任何 NuGet 依赖：System.Text.Json 在框架内，Core 的离线冷构建性质必须保持。
/// </summary>
public static class CliContract
{
    /// <summary>
    /// 破坏性变更时递增；消费方应先校验它再解析 <see cref="CliEnvelope.Data"/>。
    /// 2：<c>plan</c> 的载荷从「Hub 自持的 CMake 命令计划」换成引擎 cmdline 调用
    /// （<see cref="AxmolInvocation"/>）—— 构建已委派给 <c>axmol build</c>。
    /// </summary>
    public const int SchemaVersion = 2;

    // camelCase：JSON 的通行写法，也与仓库既有输出一致（.axmol-hub.json 用的就是
    // engine/version/platform 这些小写键）。System.Text.Json 默认是 PascalCase，
    // 不显式设置的话信封会长成 Schema/Command/ExitCode —— 契约文档写的是小写，
    // 这里必须与文档一致，而不是让文档迁就默认值。
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Encode(string command, bool ok, int exitCode, object? data = null, CliError? error = null)
        => JsonSerializer.Serialize(
            new CliEnvelope { Command = command, Ok = ok, ExitCode = exitCode, Data = data, Error = error },
            Json);

    /// <summary>把异常翻译成信封里的 <c>error</c>。只取类型名与消息，不带栈 —— 栈仍然走 stderr。</summary>
    public static CliError Describe(Exception exception) => new(exception.GetType().Name, exception.Message);
}

// ---------------------------------------------------------------------------
// 各动词的载荷。它们是**契约的一部分**，所以是有类型的、集中的；不是就地拼的匿名对象。
// ---------------------------------------------------------------------------

public sealed record TargetDescriptor(
    string Id,
    string Name,
    string Family,
    string Architecture,
    string[] Hosts,
    bool Simulator,
    bool Current);

public sealed record TargetsPayload(IReadOnlyList<TargetDescriptor> Targets);

public sealed record ComponentDescriptor(string Name, string Status, string Details, string? Executable);

/// <summary><c>verify</c> 的载荷。注意它在"有组件缺失"时仍然存在（ok:false + exitCode:2）。</summary>
public sealed record VerifyPayload(string Target, IReadOnlyList<ComponentDescriptor> Components);

public sealed record ProjectPayload(ProjectEntry Project);

/// <summary><c>plan</c> 的载荷：这次构建会执行的引擎命令（<c>axmol &lt;subcmd&gt; args</c>）。</summary>
public sealed record PlanPayload(AxmolInvocation Plan);

public sealed record BuildPayload(ProjectEntry Project, string Status, string? Executable);

/// <summary><c>run</c>/<c>serve</c>/<c>deploy</c> 的载荷：被拉起程序的退出码。</summary>
public sealed record ChildExitPayload(int ExitCode);

public sealed record DeviceDescriptor(string Serial, string State, string Details);

public sealed record DevicesPayload(IReadOnlyList<DeviceDescriptor> Devices);

/// <summary>
/// <c>install-tools</c> 的载荷：跑一次引擎 <c>setup.ps1</c> 的结果。
/// 注意 <c>outcome</c> 只取引擎原始输出里能对上号的结局 —— 开发者模式未开时 setup 会
/// <c>exit 0</c> 却什么都没装，那种假成功会体现为 <c>DeveloperModeBlocked</c>。
/// </summary>
public sealed record SetupPayload(string Engine, string Platform, string Outcome, int ExitCode);

public sealed record CommandsPayload(IReadOnlyList<string> Commands);
