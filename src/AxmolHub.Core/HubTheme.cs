namespace AxmolHub.Core;

/// <summary>
/// 界面主题三种取值的**单一定义**，与 <see cref="HubTexts"/> 里的语言常量是同一套做法：
/// 值来自用户的设置文件，而"哪些值受支持"若散落到各处（下拉项、存盘校验、界面适配层），
/// 加一个取值或改个拼写就会静默分叉 —— 用户选了"深色"，下一次启动却因为某处不认这个值
/// 而回到跟随系统，看起来像设置丢了。
///
/// 这里同样**只有数据与查表**，不含任何 UI 框架类型：主题字符串与界面框架无关，
/// "system / light / dark" 怎么落到各自的 ThemeVariant 是各客户端适配层的事
/// （Avalonia 侧见 <c>App/Services/ThemeService.cs</c>）。因此 Core 的零依赖、
/// 可离线冷构建性质不受影响。
///
/// 取值的含义：
/// <list type="bullet">
///   <item><see cref="System"/>：跟随操作系统（默认）。系统切到浅色时 Hub 也切，这是"不表态"。</item>
///   <item><see cref="Light"/> / <see cref="Dark"/>：用户明确指定，不再随系统变。</item>
/// </list>
/// </summary>
public static class HubTheme
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>没有设置、或设置里是一个不认识的值时使用的主题：跟随系统。</summary>
    public const string DefaultTheme = System;

    /// <summary>全部取值，顺序即界面上的排列顺序（跟随系统在最前）。断言靠它确认下拉项没漏、也没多。</summary>
    public static string[] All => [System, Light, Dark];

    public static bool IsSupported(string? theme) => theme is System or Light or Dark;

    /// <summary>未知主题一律回落到 <see cref="DefaultTheme"/>，而不是抛异常：这个值来自设置文件，属于用户数据。</summary>
    public static string Normalize(string? theme) => theme switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };
}
