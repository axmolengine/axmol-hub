using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace AxmolHub;

internal static class DeepLinkProtocolRegistration
{
    private const string Scheme = "axmolhub";
    // Velopack natively creates the shortcut from --packTitle ('Axmol Hub'), so the desktop and Start menu
    // both carry that name; no post-install rename is needed — renaming would in fact leave Velopack unable
    // to find the shortcut it created when uninstalling.
    private const string BrandedShortcutName = "Axmol Hub.lnk";

    public static void Register(Action<string>? diagnostic = null)
    {
        if (OperatingSystem.IsWindows())
        {
            RegisterWindows(diagnostic);
        }
        else if (OperatingSystem.IsLinux())
        {
            RegisterLinux(diagnostic);
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
        RegisterWindows(null);
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(Action<string>? diagnostic)
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
        TryStampStartMenuShortcut(diagnostic);
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
        // Velopack's stable launcher name follows --packTitle (now 'Axmol Hub.exe'); installs upgraded from
        // ≤0.8.7 still carry the old 'AxmolHub.exe', so try both names and finally fall back to the single
        // non-Update.exe under the install root.
        if (installRoot is not null)
        {
            foreach (var name in new[] { "Axmol Hub.exe", "AxmolHub.exe" })
            {
                var stableLauncher = Path.Combine(installRoot.FullName, name);
                if (File.Exists(stableLauncher)) return stableLauncher;
            }
        }
        if (installRoot is not null && Directory.Exists(installRoot.FullName))
        {
            var launchers = Directory.EnumerateFiles(installRoot.FullName, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (launchers.Length == 1) return launchers[0];
        }

        return Environment.ProcessPath ?? throw new IOException("Could not determine the Hub executable path.");
    }

    [SupportedOSPlatform("windows")]
    private static void TryStampStartMenuShortcut(Action<string>? diagnostic)
    {
        // Velopack writes the shortcut into a *Programs* folder, not the Start Menu root that
        // SpecialFolder.StartMenu resolves to. Looking in the root made File.Exists false on every
        // launch, so the stamp never ran and the shortcut kept Velopack's own AUMID
        // (velopack.dev.axmol.hubapp) — which does not match this app's notifier AUMID, so Windows silently
        // dropped every toast. Search the per-user Programs folder first, then the all-users one.
        var shortcut = FindStartMenuShortcut();
        if (shortcut is null)
        {
            diagnostic?.Invoke("Axmol Hub.lnk was not found in the Start Menu Programs folders; "
                               + "this build has no shortcut to register the Windows toast identity.");
            return;
        }

        object? shellLink = null;
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(
                new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
            shellLink = Activator.CreateInstance(shellLinkType)
                        ?? throw new InvalidOperationException("Could not create the Windows shortcut object.");
            var persistence = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
            persistence.Load(shortcut, 2);
            var store = (IShellPropertyStore)shellLink;
            var key = new ShellPropertyKey(
                new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            var value = new ShellPropertyValue
            {
                Type = 31,
                Pointer = Marshal.StringToCoTaskMemUni(SystemAttentionService.WindowsAppUserModelId),
            };
            try
            {
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
                persistence.Save(shortcut, true);
                diagnostic?.Invoke($"Stamped Windows toast AppUserModelID {SystemAttentionService.WindowsAppUserModelId} on {shortcut}.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }
        }
        catch (Exception ex)
        {
            var message = "Could not register the Windows toast AppUserModelID: " + ex;
            System.Diagnostics.Trace.TraceWarning(message);
            diagnostic?.Invoke(message);
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
                Marshal.FinalReleaseComObject(shellLink);
        }
    }

    /// <summary>Locates the installed <c>Axmol Hub.lnk</c>. Velopack places it under a Start Menu
    /// <em>Programs</em> folder — per-user for a normal install, all-users for a machine-wide one —
    /// never the Start Menu root. Returns the first existing match, or <c>null</c> when the app is
    /// running from a build that was never installed (no shortcut, so nothing to stamp).</summary>
    [SupportedOSPlatform("windows")]
    private static string? FindStartMenuShortcut()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Programs,
                     Environment.SpecialFolder.CommonPrograms,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            var shortcut = Path.Combine(path, BrandedShortcutName);
            if (File.Exists(shortcut)) return shortcut;
        }

        return null;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out ShellPropertyKey key);
        [PreserveSig] int GetValue(ref ShellPropertyKey key, out ShellPropertyValue value);
        [PreserveSig] int SetValue(ref ShellPropertyKey key, ref ShellPropertyValue value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellPropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellPropertyValue
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
    }

    private static void RegisterLinux(Action<string>? diagnostic)
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
        var desktopFile = Path.Combine(applications, LinuxDesktopIdentity.DesktopFile);
        var quotedExecutable = "\"" + EscapeDesktopArgument(executable) + "\"";

        // Icons land on disk first, the desktop entry is written after: when Icon= points at a name not yet
        // planted into the theme, the desktop environment falls back straight to the generic icon — back to
        // "looks unfixed". If planting fails, skip Icon= this round; the deep-link contract itself is intact.
        var hasIcon = LinuxDesktopIntegration.InstallIcons(dataHome, diagnostic);
        var contents = "[Desktop Entry]\n"
                       + "Type=Application\n"
                       + "Name=Axmol Hub\n"
                       + "Comment=Install Axmol engine releases\n"
                       + (hasIcon ? "Icon=" + LinuxDesktopIdentity.Id + "\n" : string.Empty)
                       + "Exec=" + quotedExecutable + " %u\n"
                       + "Terminal=false\n"
                       + "Categories=Development;Utility;\n"
                       + "Keywords=axmol;engine;game;develop;\n"
                       // The window class matching the desktop-entry filename is how GNOME/KDE attribute a
                       // running window to this entry (and the icon comes from that match). The window side is
                       // pinned to the same value by X11PlatformOptions.WmClass in Program.BuildAvaloniaApp —
                       // both sides must be read together.
                       + "StartupWMClass=" + LinuxDesktopIdentity.Id + "\n"
                       + "MimeType=x-scheme-handler/" + Scheme + ";\n";
        LinuxDesktopIntegration.WriteIfChanged(desktopFile, new UTF8Encoding(false).GetBytes(contents));

        // All three refresh tools are optional host dependencies (see LinuxDesktopIntegration); run them
        // before xdg-mime: xdg-mime throws on failure, and by then the caches have already caught up with
        // the freshly written files.
        LinuxDesktopIntegration.RefreshCaches(dataHome, applications, diagnostic);

        using var update = Process.Start(new ProcessStartInfo("xdg-mime", $"default {LinuxDesktopIdentity.DesktopFile} x-scheme-handler/{Scheme}")
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
