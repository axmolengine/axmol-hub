using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AxmolHub.App;

/// <summary>
/// 运行期核对主题与可视化树的小工具集。
///
/// 它存在的前提是一条 Avalonia 的性质：**样式写错不会报错，只会静默地用默认外观渲染**。
/// 所以"构建通过"对样式层毫无证据价值，只能靠在运行期读真实可视化树。
/// P3 的控件画廊与 P4 的基础件自检共用这里的实现，避免两处各写一份、然后各自漂移。
/// </summary>
internal static class ThemeProbe
{
    /// <summary>按 key 取当前变体下的语义色。</summary>
    public static Color? TokenColor(string key)
    {
        if (Application.Current is not { } app)
        {
            return null;
        }

        // 走 IResourceHost 接口而不用 Window 上的扩展方法：Application.Resources 里的
        // ThemeDictionaries 必须带变体查，否则在 Default 变体下拿不到 Dark/Light 的值
        // —— 那样每个 token 都会解析成 null，断言会集体假失败（第一次跑就踩到了）。
        var variant = app.ActualThemeVariant;
        if (((IResourceHost)app).TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        if (((IResourceHost)app).TryGetResource(key, null, out value) && value is ISolidColorBrush fallback)
        {
            return fallback.Color;
        }

        return null;
    }

    public static bool IsToken(IBrush? brush, string token)
        => TokenColor(token) is { } expected && ColorOf(brush) == expected;

    /// <summary>
    /// 资源字典里有没有这个 key。比 <see cref="TokenColor"/> 宽：色值 token 之外也适用
    /// —— 文案是字符串、图标是几何，它们同样会因为少一个 key 而**静默显示空白**。
    /// </summary>
    public static bool Resolves(string key)
    {
        if (Application.Current is not { } app)
        {
            return false;
        }

        var variant = app.ActualThemeVariant;
        if (((IResourceHost)app).TryGetResource(key, variant, out var value) && value is not null)
        {
            return true;
        }

        return ((IResourceHost)app).TryGetResource(key, null, out value) && value is not null;
    }

    public static Color? ColorOf(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : null;

    public static string Describe(IBrush? brush)
        => ColorOf(brush)?.ToString() ?? brush?.ToString() ?? "null";

    public static T? Descendant<T>(Visual root)
        where T : Visual
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    public static T? NamedDescendant<T>(Visual root, string name)
        where T : Visual
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault(v => v.Name == name);
}
