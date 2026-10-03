namespace AxmolHub.Core;

/// <summary>
/// System shell location. Originally a static member of <c>ToolchainDetector</c>; after the
/// toolchain self-detection was retired, only this one job remains: "find a PowerShell executable
/// that can run".
/// </summary>
public static class WindowsShell
{
    /// <summary>The Windows PowerShell that ships with Windows (the entry point for engine scripts on Windows).</summary>
    public static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
}
