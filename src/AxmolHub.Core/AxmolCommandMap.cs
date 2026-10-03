namespace AxmolHub.Core;

/// <summary>
/// 一次引擎 cmdline 调用的形状。调用规则是 <c>axmol &lt;subcmd&gt; args</c>。
/// </summary>
public sealed record AxmolInvocation(string SubCommand, string[] Arguments)
{
    /// <summary>仅用于日志与 <c>plan</c> 的人读输出。</summary>
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
        "uwp-x64" => ("winuwp", "x64"),
        "android-arm64" => ("android", "arm64"),
        "android-x64" => ("android", "x64"),
        "linux-x64" => ("linux", "x64"),
        "macos-arm64" => ("osx", "arm64"),
        "macos-x64" => ("osx", "x64"),
        "ios-arm64" => ("ios", "arm64"),
        "ios-simulator-arm64" => ("ios", "arm64"),
        "ios-simulator-x64" => ("ios", "x64"),
        "tvos-arm64" => ("tvos", "arm64"),
        "tvos-simulator-arm64" => ("tvos", "arm64"),
        "tvos-simulator-x64" => ("tvos", "x64"),
        "wasm32" => ("wasm", ""),
        _ => throw new InvalidDataException($"No axmol platform is mapped for target: {target.Id}"),
    };

    /// <summary>平台模块 id → 引擎 <c>-p</c> 平台名（模块准备 = <c>setup.ps1 -p &lt;platform&gt;</c>）。</summary>
    public static string? PlatformForModule(string moduleId) => moduleId switch
    {
        "windows" => "win32",
        "android" => "android",
        "web" => "wasm",
        "ios" => "ios",
        "tvos" => "tvos",
        "macos" => "osx",
        "linux" => "linux",
        "uwp" => "winuwp",
        _ => null,
    };

    /// <summary>配置开关。引擎用 <c>-O3</c> 表示优化/Release；默认（不传）即 Debug。</summary>
    private static void AddConfiguration(ICollection<string> arguments, string configuration)
    {
        BuildConfigurations.Validate(configuration);
        if (configuration == "Release") arguments.Add("-O3");
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

    /// <summary><c>-xc</c> 只接一个参数：多个 cmake 选项用逗号连起来。</summary>
    private static void AddCmake(ICollection<string> arguments, IEnumerable<string>? options)
    {
        var values = (options ?? []).Where(option => option.Length > 0).ToArray();
        if (values.Length == 0) return;
        arguments.Add("-xc");
        arguments.Add(string.Join(',', values));
    }
}
