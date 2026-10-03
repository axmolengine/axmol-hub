using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 把 <see cref="CjkFontProbe"/> 的结论变成一次提示。
///
/// 触发点只有两个，都在 <see cref="MainWindow"/> 里：**窗口出现之后**与**切到中文之后**。
/// 这两个点都等价于"用户马上要看到方框了"，而不是"每次启动都拦一下"。
/// </summary>
internal static class CjkFontNotifier
{
    /// <summary>一个进程只提示一次。用户已经被告知过，再拦就是骚扰。</summary>
    private static bool _shown;

    /// <summary>
    /// 该提示就提示。返回是否真的弹了 —— 自检靠它区分"没弹"是**判断的结果**还是**代码没走到**。
    /// </summary>
    public static bool NotifyIfNeeded(Window? owner)
    {
        if (_shown || App.Options.IsAutomation)
        {
            return false;
        }

        if (!CjkFontNotice.ShouldWarn(CjkFontProbe.Availability, HubStrings.Language))
        {
            return false;
        }

        _shown = true;

        // 不等用户点确认：这是一条告知，调用方（启动流程 / 语言切换）没有"确认之后才继续"的后半段。
        // 等待会让设置页的语言切换卡在这里 —— 而那一步早就完成了。
        _ = HubDialog.ShowAsync(owner, CjkFontNotice.Title, CjkFontNotice.Message(CjkFontNotice.RunningOnLinux));
        return true;
    }
}
