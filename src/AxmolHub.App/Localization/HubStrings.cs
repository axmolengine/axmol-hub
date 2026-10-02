using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// Avalonia 侧的本地化适配层。**文案本体不在这里**，在 <see cref="HubTexts"/>（Core）。
///
/// 这一层只做一件事：把 <see cref="HubTexts.Keys"/> 灌进 Avalonia 的资源字典，使页面 XAML 里的
/// <c>{DynamicResource Projects}</c> 与 WPF 版逐字一致 —— P5 迁移才可能做到"页面复制过来就能用"。
/// 两侧的差别只剩"写进哪个资源字典"，文案与语言回落规则是同一份实现。
/// </summary>
public static class HubStrings
{
    public static string Language { get; private set; } = HubTexts.DefaultLanguage;

    public static string Get(string key) => HubTexts.Get(key, Language);

    /// <summary>
    /// 必须在**任何窗口构造之前**调用：<c>DynamicResource</c> 是按 key 现查的，
    /// 窗口先建好再灌文案会留下一批解析为 null 的标签（且不会报错，只会显示空白）。
    /// </summary>
    public static void Apply(string language, Application application)
    {
        Language = HubTexts.Normalize(language);
        foreach (var key in HubTexts.Keys)
        {
            application.Resources[key] = Get(key);
        }
    }
}

/// <summary>
/// 对应 WPF 版的 <c>LocalizedValueConverter</c>：把数据里的英文标识
/// （<c>device</c> / <c>Missing</c> / <c>Succeeded</c> …）翻成界面文字。
/// 之所以要有它，是因为这些值来自 Core 的领域模型，不能为了显示而在 Core 里存中文。
/// </summary>
public sealed class LocalizedValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => HubStrings.Get(value?.ToString() ?? "");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
