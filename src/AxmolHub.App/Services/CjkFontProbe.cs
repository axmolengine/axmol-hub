using System.Globalization;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// "这台机器能不能把中文画出来" —— 问的是**渲染器自己**。
///
/// 为什么不去扫 <c>/usr/share/fonts</c>、也不去查有没有装某个包：那些都是"看起来像"的近似判据。
/// Avalonia 在 Linux 上通过 fontconfig 找字体，Skia 画不出来的时候，它在
/// <see cref="FontManager.TryMatchCharacter"/> 这里同样匹配不到 —— 也就是说，
/// 这个查询与"界面上会不会出现方框"是**同一件事**，不是它的替代品。
///
/// 它有一个前提：必须在 Avalonia 起来之后调用（<c>FontManager.Current</c> 要平台实现）。
/// 主窗口构造完之后才用，天然满足。
/// </summary>
internal static class CjkFontProbe
{
    /// <summary>
    /// Hub 中文界面实际用到的两类字符：**汉字**（中）与**全角标点**（，）。
    /// 只查汉字是不够的 —— 界面文案里"（未）选择、引擎目录"这类全角标点占比不低，
    /// 而一个只覆盖汉字的极简字体是可能存在的。
    /// </summary>
    private static readonly int[] Probes =
    [
        0x4E2D, // 中
        0xFF0C, // ，
    ];

    /// <summary>
    /// 探测一次就缓存：字体在进程生命周期内不会凭空出现（真要装字体，提示里写的是"重启 Hub"）。
    /// 每次切语言都去查一遍 fontconfig 是白花时间，而且结果只会一样。
    /// </summary>
    private static readonly Lazy<(CjkFontAvailability Availability, string Family)> Result = new(Inspect);

    public static CjkFontAvailability Availability => Result.Value.Availability;

    /// <summary>匹配到的字体族，只用于自检与日志（"能显示"要能被指认出来源）。</summary>
    public static string MatchedFamily => Result.Value.Family;

    private static (CjkFontAvailability Availability, string Family) Inspect()
    {
        try
        {
            var family = "";
            foreach (var codepoint in Probes)
            {
                if (!FontManager.Current.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal,
                        FontStretch.Normal, null, CultureInfo.GetCultureInfo("zh-CN"), out var typeface))
                {
                    return (CjkFontAvailability.Missing, family);
                }

                family = typeface.FontFamily.Name;
            }

            return (CjkFontAvailability.Available, family);
        }
        catch (Exception)
        {
            // 探测失败**不能**当成"缺字体"：那会变成一条假警报，而假警报会让人连真问题一起忽略。
            // 返回 Unknown，ShouldWarn 对 Unknown 一律不弹窗。
            return (CjkFontAvailability.Unknown, "");
        }
    }
}
