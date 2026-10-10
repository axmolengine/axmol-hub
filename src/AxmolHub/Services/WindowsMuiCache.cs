using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AxmolHub;

internal static class WindowsMuiCache
{
    private const string RegistryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    /// <summary>
    /// **All** names the main executable has ever used under <c>current\</c>. MuiCache keys are absolute
    /// paths, and entries left by a pre-rename install can only be deleted under the old name, so this is
    /// a historical-name set rather than "the current name": the old names (AxmolHub.App.exe →
    /// AxmolHub.exe) must stay, and removing them would silently disable the cleanup.
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
            .Prepend(Path.Combine(installRoot, "AxmolHub.exe"))
            .Select(path => Path.GetFullPath(path) + ".")
            .ToArray();

        foreach (var valueName in cache.GetValueNames())
        {
            if (executables.Any(executable => valueName.StartsWith(executable, StringComparison.OrdinalIgnoreCase)))
                cache.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
