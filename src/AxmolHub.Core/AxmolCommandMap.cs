namespace AxmolHub.Core;

/// <summary>
/// 一次引擎 cmdline 调用的形状。调用规则是 <c>axmol &lt;subcmd&gt; args</c>。
/// </summary>
public sealed record AxmolInvocation(string SubCommand, string[] Arguments)
{
    /// <summary>
    /// 仅用于日志与 <c>plan</c> 的人读输出。
    ///
    /// 含空格的值用 <c>"…"</c> 包起来：这既是给人读时看得清边界，也提醒后续任何「把
    /// <see cref="Arguments"/> 重新拼回一条 shell 命令字符串」的代码——<c>-xc</c> 的值
    /// **必须**用引号包裹，否则空格会在 shell 层把它拆碎，落到引擎 <c>build.ps1</c>
    /// 的「一次只吃一个参数」上就只剩半截，导致莫名其妙的构建失败。
    /// </summary>
    public override string ToString() => "axmol " + SubCommand + " " + string.Join(' ',
        Arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
}

/// <summary>
/// Hub 的构建目标 → 引擎 cmdline 参数。
///
/// 参数口径**不来自猜测**，来自引擎自身的 CI 调用（<c>.github/workflows/build.yml</c>）：
/// <code>
///   axmol -p win32 -a x64 -xc '-DAX_ENABLE_VR=ON,-DAX_ENABLE_OPENXR=ON' -O3 -t cpp-tests
///   axmol -d .\HelloCpp -xc '-DAX_PREBUILT_DIR=build' -O3
///   axmol run -p win32 -a arm64 -t unit-tests -O3
///   ./tools/cmdline/axmol -p ios -a arm64 -sdk simulator -t cpp-tests
///   ./tools/cmdline/axmol -p android -a arm64 -t cpp-tests
/// </code>
/// 两条易错点：<c>-xc</c> 收的是**一个**逗号分隔的字符串（不是多个参数）；
/// Release 用 <c>-O3</c>（不是 <c>-DCMAKE_BUILD_TYPE</c>）。
/// </summary>
public static class AxmolCommandMap
{
    /// <summary>Hub 目标 → <c>-p</c> 平台名、<c>-a</c> 架构（空串表示不传 <c>-a</c>）。</summary>
    public static (string Platform, string Architecture) Target(BuildTarget target) => target.Id switch
    {
        "windows-x64" => ("win32", "x64"),
        "windows-arm64" => ("win32", "arm64"),
        "uwp-x64" => ("winuwp", "x64"),
        "android-arm64" => ("android", "arm64"),
        "android-x64" => ("android", "x64"),
        "linux-x64" => ("linux", "x64"),
        "linux-arm64" => ("linux", "arm64"),
        "macos-arm64" => ("osx", "arm64"),
        "macos-x64" => ("osx", "x64"),
        "ios-arm64" => ("ios", "arm64"),
        "ios-simulator-arm64" => ("ios", "arm64"),
        "ios-simulator-x64" => ("ios", "x64"),
        "tvos-arm64" => ("tvos", "arm64"),
        "tvos-simulator-arm64" => ("tvos", "arm64"),
        "tvos-simulator-x64" => ("tvos", "x64"),
        "wasm32" => ("wasm", ""),
        "wasm64" => ("wasm64", ""),
        _ => throw new InvalidDataException($"No axmol platform is mapped for target: {target.Id}"),
    };

    /// <summary>平台模块 id → 引擎 <c>-p</c> 平台名（模块准备 = <c>setup.ps1 -p &lt;platform&gt;</c>）。</summary>
    /// <summary>
    /// 配置开关。引擎用 <c>-O&lt;n&gt;</c> 的 <c>n</c> 作索引选构建类型
    /// （<c>1k/1kiss.ps1</c>：<c>@('Debug','MinSizeRel','RelWithDebInfo','Release')[$options.O]</c>）：
    /// <c>-O0</c>=Debug、<c>-O3</c>=Release。<b>不传 <c>-O</c> 时引擎默认 <c>RelWithDebInfo</c></b>，
    /// 所以这里 <c>Debug</c> 必须显式传 <c>-O0</c>，否则 Hub 的「Debug」会静默构建成 RelWithDebInfo。
    /// </summary>
    private static void AddConfiguration(ICollection<string> arguments, string configuration)
    {
        BuildConfigurations.Validate(configuration);
        arguments.Add(configuration == "Release" ? "-O3" : "-O0");
    }

    private static void AddTarget(ICollection<string> arguments, BuildTarget target)
    {
        var (platform, architecture) = Target(target);
        arguments.Add("-p");
        arguments.Add(platform);
        if (architecture.Length > 0)
        {
            arguments.Add("-a");
            arguments.Add(architecture);
        }

        // 引擎不会把 iOS arm64 猜成模拟器，必须显式声明。
        if (target.Simulator)
        {
            arguments.Add("-sdk");
            arguments.Add("simulator");
        }
    }

    public static AxmolInvocation Build(BuildTarget target, string projectDirectory, string configuration, bool configureOnly, IEnumerable<string>? additionalCmake = null)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);
        if (configureOnly) arguments.Add("-c");
        AddConfiguration(arguments, configuration);
        AddCmake(arguments, additionalCmake);
        arguments.Add("-d");
        arguments.Add(projectDirectory);
        return new("build", arguments.ToArray());
    }

    /// <summary>
    /// **引擎根构建**：把引擎编译成可被项目复用的预编译库（聚合目标 <c>axmol-sdk</c>）。
    ///
    /// 刻意**不复用 <see cref="Build"/>**：那个总是追加 <c>-d &lt;project&gt;</c>，而引擎根构建没有工程目录
    /// （CI 黄金路径就是 `axmol -p win32 -a x64 -xc '…' -O3`，不带 <c>-d</c>）。
    /// 工作目录由调用方设为引擎根。
    /// </summary>
    public static AxmolInvocation BuildEngine(BuildTarget target, string configuration)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);               // -p win32 -a x64
        AddConfiguration(arguments, configuration); // Release → -O3；Debug → 不传
        return new("build", arguments.ToArray());
    }

    public static AxmolInvocation Run(BuildTarget target, string projectDirectory, string configuration)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);
        AddConfiguration(arguments, configuration);
        arguments.Add("-d");
        arguments.Add(projectDirectory);
        return new("run", arguments.ToArray());
    }

    public static AxmolInvocation Deploy(BuildTarget target, string projectDirectory, string configuration)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);
        AddConfiguration(arguments, configuration);
        arguments.Add("-d");
        arguments.Add(projectDirectory);
        return new("deploy", arguments.ToArray());
    }

    /// <summary>
    /// <c>-xc</c> 只接**一个**参数：多个 cmake 选项用逗号连成一个字符串。
    ///
    /// **为什么必须是单参数**：引擎 <c>plugins/build.ps1</c> 的参数循环是「一次只吃一个」——
    /// <c>-xc</c> 后面的第一个 token 被存进 <c>$options.xc</c>，再 <c>Split(',')</c> 拆开。
    /// 一旦 <c>-xc</c> 的值被拆成多个 token（例如值里含空格、或被人当多个参数传入），
    /// 只有第一个 token 进得了 <c>$options.xc</c>，其余变成 <c>$unhandled_args</c> 里的游离参数，
    /// 最终拼出的 CMake 命令缺选项，表现为「莫名其妙的构建失败」且极难排查。
    /// 因此这里务必 <c>string.Join(',', …)</c> 成一个不含空格的单参数；外层 shell 转义也靠
    /// <see cref="AxmolInvocation.ToString"/> 的引号包裹来兜底（见其文档）。
    /// </summary>
    private static void AddCmake(ICollection<string> arguments, IEnumerable<string>? options)
    {
        var values = (options ?? []).Where(option => option.Length > 0).ToArray();
        if (values.Length == 0) return;
        arguments.Add("-xc");
        arguments.Add(string.Join(',', values));
    }
}
