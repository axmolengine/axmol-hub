using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace AxmolHub.App;

internal static class DeepLinkProtocolRegistration
{
    private const string Scheme = "axmolhub";

    public static void Register()
    {
        if (OperatingSystem.IsWindows())
        {
            RegisterWindows();
        }
        else if (OperatingSystem.IsLinux())
        {
            RegisterLinux();
        }
    }

    public static void UnregisterWindowsOnUninstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        UnregisterWindows();
    }

    public static void RegisterWindowsAfterInstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        RegisterWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows()
    {
        var executable = WindowsLaunchPath();
        var command = $"\"{executable}\" \"%1\"";
        using var protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme)
            ?? throw new IOException("Could not create the current-user URI protocol registration.");
        protocol.SetValue("", "URL:Axmol Hub Protocol", RegistryValueKind.String);
        protocol.SetValue("URL Protocol", "", RegistryValueKind.String);
        using var commandKey = protocol.CreateSubKey(@"shell\open\command")
            ?? throw new IOException("Could not create the URI protocol launch command.");
        commandKey.SetValue("", command, RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows()
    {
        var expectedCommand = $"\"{WindowsLaunchPath()}\" \"%1\"";
        using var commandKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + Scheme + @"\shell\open\command");
        if (!string.Equals(commandKey?.GetValue("") as string, expectedCommand, StringComparison.OrdinalIgnoreCase)) return;
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Scheme, throwOnMissingSubKey: false);
    }

    private static string WindowsLaunchPath()
    {
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installRoot = Directory.GetParent(appDirectory);
        var stableLauncher = installRoot is null ? null : Path.Combine(installRoot.FullName, "Axmol Hub.exe");
        if (stableLauncher is not null && File.Exists(stableLauncher)) return stableLauncher;
        if (installRoot is not null && Directory.Exists(installRoot.FullName))
        {
            var launchers = Directory.EnumerateFiles(installRoot.FullName, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (launchers.Length == 1) return launchers[0];
        }

        return Environment.ProcessPath ?? throw new IOException("Could not determine the Hub executable path.");
    }

    private static void RegisterLinux()
    {
        var executable = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable))
        {
            executable = Environment.ProcessPath;
        }

        if (string.IsNullOrWhiteSpace(executable)) throw new IOException("Could not determine the Hub executable path.");
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome)) dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        if (!Path.IsPathRooted(dataHome)) throw new IOException("XDG_DATA_HOME must be an absolute path.");

        var applications = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(applications);
        var desktopFile = Path.Combine(applications, "axmol-hub.desktop");
        var quotedExecutable = "\"" + EscapeDesktopArgument(executable) + "\"";
        var contents = "[Desktop Entry]\n"
                       + "Type=Application\n"
                       + "Name=Axmol Hub\n"
                       + "Comment=Install Axmol engine releases\n"
                       + "Exec=" + quotedExecutable + " %u\n"
                       + "Terminal=false\n"
                       + "Categories=Development;\n"
                       + "MimeType=x-scheme-handler/" + Scheme + ";\n";
        var temporary = desktopFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, desktopFile, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        using var update = Process.Start(new ProcessStartInfo("xdg-mime", $"default axmol-hub.desktop x-scheme-handler/{Scheme}")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new IOException("Could not start xdg-mime to register the Axmol Hub URI scheme.");
        var standardError = update.StandardError.ReadToEndAsync();
        _ = update.StandardOutput.ReadToEndAsync();
        if (!update.WaitForExit(5000))
        {
            update.Kill();
            throw new IOException("xdg-mime timed out while registering the Axmol Hub URI scheme.");
        }

        if (update.ExitCode != 0)
        {
            var error = standardError.GetAwaiter().GetResult();
            throw new IOException("xdg-mime could not register the Axmol Hub URI scheme: " + error);
        }
    }

    private static string EscapeDesktopArgument(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
}
