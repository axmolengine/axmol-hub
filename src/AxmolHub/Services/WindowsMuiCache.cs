using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AxmolHub;

internal static class WindowsMuiCache
{
    private const string RegistryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    /// <summary>
    /// 主程序在 <c>current\</c> 里用过的**全部**名字。MuiCache 的键是绝对路径，改名前的安装留下的
    /// 条目只有按旧名才删得掉，所以这里不是"当前名"而是"历史名集合"：改名（AxmolHub.App.exe →
    /// AxmolHub.exe）时旧名必须留着，删掉就等于让清理静默失效。
    /// </summary>
    private static readonly string[] MainExecutableNames =
    [
        "AxmolHub.exe",
        "AxmolHub.App.exe",
        "Axmol Hub.exe",
        "axmol-hub.exe",
    ];

    public static void ClearForCurrentInstall()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var (installRoot, currentDirectory) = GetInstallDirectories();
            ClearForInstall(installRoot, currentDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clear Axmol Hub MuiCache entries: " + ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static (string InstallRoot, string CurrentDirectory) GetInstallDirectories()
    {
        var currentDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(currentDirectory).Equals("current", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Hub process is not running from a Velopack current directory.");

        var installRoot = Directory.GetParent(currentDirectory)
            ?? throw new IOException("Could not determine the Axmol Hub install directory.");
        if (!File.Exists(Path.Combine(installRoot.FullName, "Update.exe"))
            || !MainExecutableNames.Any(name => File.Exists(Path.Combine(currentDirectory, name))))
        {
            throw new IOException("The Axmol Hub Velopack installation layout is incomplete.");
        }

        return (installRoot.FullName, currentDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static void ClearForInstall(string installRoot, string currentDirectory)
    {
        using var cache = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        if (cache is null) return;

        var executables = MainExecutableNames
            .Select(name => Path.Combine(currentDirectory, name))
            .Prepend(Path.Combine(installRoot, "Axmol Hub.exe"))
            .Select(path => Path.GetFullPath(path) + ".")
            .ToArray();

        foreach (var valueName in cache.GetValueNames())
        {
            if (executables.Any(executable => valueName.StartsWith(executable, StringComparison.OrdinalIgnoreCase)))
                cache.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
