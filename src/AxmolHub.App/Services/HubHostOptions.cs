using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 命令行与宿主目录。对应 WPF 版 App.xaml.cs 里那段参数解析，默认值逐条对齐。
/// </summary>
internal sealed record HubHostOptions(
    string? DataRootArgument,
    string PreferencesPath,
    string? SmokeImagePath,
    string? SmokePagesDirectory,
    string? VerifyThemeReport,
    string? VerifyFoundationReport,
    string? VerifyShellReport,
    string? VerifyOpsReport,
    string[] VerifyOpsEngines,
    bool Gallery)
{
    /// <summary>
    /// 设置与数据根放每用户目录，**不能**放 AppContext.BaseDirectory 旁边：
    /// Velopack 更新时整体替换安装目录下的 current\，卸载时删除整个安装目录。
    /// 引擎与工具链是 GB 级，所以用 LocalApplicationData 而不是漫游 AppData。
    /// </summary>
    public static string UserDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "AxmolHub");

    public static string DefaultPreferencesPath => System.IO.Path.Combine(UserDirectory, "hub-settings.json");

    public static string DefaultDataRoot => System.IO.Path.Combine(UserDirectory, "data");

    /// <summary>
    /// 数据根的三级优先：命令行 &gt; 设置文件 &gt; 默认目录（与 WPF 版一致）。
    /// 因此这里**不做**兜底解析 —— 一旦在解析阶段就填上默认值，
    /// 就没法区分"用户显式传了 --data-root"和"没传"，设置文件里的值会永远被默认值压住。
    /// </summary>
    public string ResolveDataRoot(HubPreferences preferences)
        => System.IO.Path.GetFullPath(DataRootArgument ?? preferences.DataRoot ?? DefaultDataRoot);

    public static HubHostOptions Parse(string[] args)
    {
        return new HubHostOptions(
            Value(args, "--data-root"),
            Value(args, "--preferences") ?? DefaultPreferencesPath,
            Value(args, "--smoke"),
            Value(args, "--smoke-pages"),
            Value(args, "--verify-theme"),
            Value(args, "--verify-foundation"),
            Value(args, "--verify-shell"),
            Value(args, "--verify-ops"),
            Trailing(args, "--verify-ops"),
            args.Any(a => string.Equals(a, "--gallery", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// <c>--verify-ops &lt;报告&gt; &lt;引擎目录&gt;...</c>：报告路径之后的、不以 <c>--</c>
    /// 开头的参数都算引擎目录。用位置参数而不是重复的 <c>--engine</c>，
    /// 是因为这条命令天生要一次给好几个引擎（一个完整的、一个不完整的）。
    /// </summary>
    private static string[] Trailing(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return [];
        }

        var values = new List<string>();
        for (var i = index + 2; i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            values.Add(args[i]);
        }

        return [.. values];
    }

    /// <summary>
    /// 取 `--flag value` 里的值。刻意不把"标志后面跟着另一个标志"当值：
    /// 传成 `--smoke --gallery` 时应视为缺参，而不是把 `--gallery` 当文件名用。
    /// </summary>
    private static string? Value(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var value = args[index + 1];
        return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }
}
