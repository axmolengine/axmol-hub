namespace AxmolHub.Core;

/// <summary>
/// 系统 shell 定位。原先是 <c>ToolchainDetector</c> 的静态成员；工具链自持退役后，
/// 这里只留下「找到一个能跑 PowerShell 的可执行文件」这一件事。
/// </summary>
public static class WindowsShell
{
    /// <summary>Windows 自带的 Windows PowerShell（引擎脚本在 Windows 上的入口）。</summary>
    public static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
}
