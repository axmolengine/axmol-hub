namespace AxmolHub.Core;

/// <summary>
/// 系统字体能不能把中文画出来。**三态而不是 bool**：<see cref="Unknown"/> 表示探测本身没得出结论
/// （字体管理器还没起来、查询抛异常……）。它必须与"确实没有字体"分开 ——
/// 并成一档就等于让"探测失败"变成一条"你缺字体"的假警报，而假警报比不报警更糟。
/// </summary>
public enum CjkFontAvailability
{
    /// <summary>匹配得到覆盖汉字字形的字体。</summary>
    Available,

    /// <summary>系统里没有任何字体覆盖汉字字形 —— 中文界面会显示成方框或空白。</summary>
    Missing,

    /// <summary>没探测出结论（不据此打扰用户）。</summary>
    Unknown,
}

/// <summary>
/// "这台机器显示不了中文"的提示。
///
/// 三条刻意的选择，都不是顺手写的：
///
/// **① 判据是"能不能渲染"，不是"装没装某个包"。** 装了文泉驿、思源黑体一样能显示中文；
/// 反过来，`fonts-noto-cjk` 装上了但字体缓存没刷新，照样显示不出来。所以
/// <c>fonts-noto-cjk</c> 只出现在**提示的操作建议**里，不参与判断。
///
/// **② 文案固定英文，不进 <see cref="HubTexts"/>。** 这条提示出现的前提恰恰是"中文渲染不出来"，
/// 用中文写它，最需要看清它的人看到的是一片方框 —— 提示本身成了它的反例。
/// 这里有一条断言守着（<c>--verify-shell</c>：标题与正文不得含 CJK 字符）。
///
/// **③ 只在界面语言是中文时才打扰用户。** 英文界面下用户根本没受影响，
/// 每次启动都拦一下只会让人烦；而"切到中文"这个动作本身就是那一刻的判据。
/// </summary>
public static class CjkFontNotice
{
    /// <summary>
    /// Debian / Ubuntu 上最省事的那条命令。写在这里而不是拼在文案里，是为了让断言能直接引用它 ——
    /// "提示用户安装"的分量全在这行可粘贴的命令上，它要是被改坏了，提示就只剩一句空话。
    /// </summary>
    public const string DebianInstallCommand = "sudo apt install fonts-noto-cjk";

    public const string Title = "Chinese font not found";

    /// <summary>
    /// 写进日志那一行。**英文界面的用户不该被弹窗拦**，但"中文为什么是方框"必须留下可查的痕迹 ——
    /// 一句日志正好：不打扰，且故障报告里能看见。内容不区分平台（命令在弹窗里按平台给）。
    /// </summary>
    public const string LogLine = "No CJK font detected; Chinese text will not render on this system.";

    /// <summary>
    /// Hub 进程是否跑在 Linux 上。抽成属性是为了让 <see cref="Message"/> 的两种取值都能被断言到 ——
    /// 直接在里面调 <c>OperatingSystem.IsLinux()</c> 的话，非 Linux 那半边文案永远没人读过。
    /// </summary>
    public static bool RunningOnLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// 什么时候该打扰用户：**确实探测到没有中文字体**，且**当前界面语言就是中文**。
    /// <see cref="CjkFontAvailability.Unknown"/> 一律不提示。
    /// </summary>
    public static bool ShouldWarn(CjkFontAvailability availability, string? language)
        => availability == CjkFontAvailability.Missing
           && HubTexts.Normalize(language) == HubTexts.ChineseLanguage;

    /// <summary>完整的提示正文。<paramref name="linux"/> 只影响"装什么"那几行。</summary>
    public static string Message(bool linux)
    {
        var lines = new List<string>
        {
            "This system has no font that can display Chinese (CJK) characters, so the Chinese",
            "interface would be shown as empty boxes.",
            "",
        };

        if (linux)
        {
            lines.Add("Install one with:");
            lines.Add("");
            lines.Add("    " + DebianInstallCommand);
            lines.Add("");
            lines.Add("Any CJK font works too (Source Han Sans, WenQuanYi, ...).");
        }
        else
        {
            lines.Add("Install a font that covers Chinese (CJK) characters from your system's");
            lines.Add("font settings.");
        }

        lines.Add("");
        lines.Add("Restart Axmol Hub afterwards. Until then you can keep using the English");
        lines.Add("interface (Settings, Interface language).");
        return string.Join("\n", lines);
    }
}
