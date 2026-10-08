using System.Text;

namespace AxmolHub;

/// <summary>
/// <c>AxmolHub --check-linux-integration [scratch-dir]</c> — the Linux desktop integration asserted on the
/// host that needs it.
///
/// <para><b>Why it exists.</b> The same gap that hides the secret store from this repository's proofs hides the
/// desktop entry: <c>AxmolHub.Checks</c> is <c>net8.0-windows</c>, <c>--verify-shell</c> needs a display, and CI
/// packages the AppImage without ever running it. "GNOME can find our logo" is therefore a claim about a Linux
/// session that nothing in the build checked — which is exactly how the icon went missing in the first place:
/// the desktop entry was written for months without an <c>Icon=</c> key, and no assertion noticed.</para>
///
/// <para><b>It runs the production entry point</b> (<see cref="DeepLinkProtocolRegistration.Register"/>) rather
/// than a parallel copy of it, so what is asserted is what ships.</para>
///
/// <para><b>It never touches a user's profile.</b> <c>XDG_DATA_HOME</c> is pointed into the scratch directory for
/// the duration of the run, and the scratch directory must be given or derived from the temp path — the check
/// has no mode that writes to the real <c>~/.local/share</c>.</para>
///
/// <para>Output is <c>PASS:</c>/<c>FAIL:</c> lines plus the integration's own diagnostics, in English: this is
/// terminal text, not interface text. Exit code is the number of failed assertions.</para>
/// </summary>
internal static class LinuxIntegrationSelfCheck
{
    public static int Run(string? scratchDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            // The integration is Linux-only by design (Windows uses the registry, macOS the Info.plist), so a
            // non-Linux host has nothing to fail on. Same reasoning as the secrets check's `backend=none` branch.
            Console.WriteLine("PASS: this host is not Linux, and the desktop integration it would test is Linux-only");
            return 0;
        }

        var scratch = string.IsNullOrWhiteSpace(scratchDirectory)
            ? Path.Combine(Path.GetTempPath(), "axmolhub-linux-integration-" + Environment.ProcessId)
            : Path.GetFullPath(scratchDirectory);
        var dataHome = Path.Combine(scratch, "xdg-data");
        Directory.CreateDirectory(dataHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);

        var failures = 0;
        var notes = new List<string>();
        void Check(bool condition, string name)
        {
            if (condition) { Console.WriteLine("PASS: " + name); return; }
            failures++;
            Console.WriteLine("FAIL: " + name);
        }

        // The icon bytes have to be in this process's own payload: the desktop entry points at files this
        // run lays down, and on an installed AppImage there is no source tree to fall back on. Reading them
        // off disk (rather than through avares://) is what makes this assertion possible with no display.
        Check(HubAppIcon.ReadPng(HubAppIcon.WindowIconSize) is { Length: > 0 }, $"the {HubAppIcon.WindowIconSize} px icon ships with the build and is readable");
        Check(HubAppIcon.ReadPng(512) is { Length: > 0 }, "the 512 px icon ships with the build and is readable");

        DeepLinkProtocolRegistration.Register(notes.Add);

        var desktopFile = Path.Combine(dataHome, "applications", LinuxDesktopIdentity.DesktopFile);
        var keys = ReadDesktopEntry(desktopFile);
        Check(keys is not null, $"the desktop entry was written to {desktopFile} and parses as key=value lines");

        // The three names have to be one string, or the desktop environment cannot tie the running window to
        // the entry and serves a generic icon instead: WM_CLASS matches StartupWMClass, and Icon= names the
        // theme entry the PNGs were installed under.
        Check(Get(keys, "StartupWMClass") == LinuxDesktopIdentity.Id, $"StartupWMClass names the window class ({LinuxDesktopIdentity.Id})");
        Check(Get(keys, "Icon") == LinuxDesktopIdentity.Id, $"Icon names the icon theme entry ({LinuxDesktopIdentity.Id})");
        Check(Get(keys, "Name") == "Axmol Hub", "the entry's Name keeps the two-word brand spelling");
        Check(Get(keys, "MimeType")?.Contains("x-scheme-handler/axmolhub", StringComparison.Ordinal) == true, "the URI scheme handler survived the extra keys");
        var exec = Get(keys, "Exec");
        var executable = UnquotedExecutable(exec);
        Check(executable is not null && Path.IsPathRooted(executable), "Exec quotes one rooted executable and keeps the %u argument");

        foreach (var bucket in new[] { 256, 512 })
        {
            var path = LinuxDesktopIntegration.IconPath(dataHome, bucket);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            Check(bytes is not null && IsPngSized(bytes, bucket), $"hicolor {bucket}x{bucket} holds a PNG that really is {bucket} px");
        }

        // The integration runs on every start, so a rewrite every time would touch two binaries per launch.
        var before = File.Exists(desktopFile) ? File.GetLastWriteTimeUtc(desktopFile) : default;
        DeepLinkProtocolRegistration.Register(null);
        Check(File.Exists(desktopFile) && File.GetLastWriteTimeUtc(desktopFile) == before, "a second registration rewrites nothing (bytes already match)");

        foreach (var note in notes) Console.WriteLine("note: " + note);
        return failures;
    }

    private static Dictionary<string, string>? ReadDesktopEntry(string path)
    {
        if (!File.Exists(path)) return null;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0].Trim() != "[Desktop Entry]") return null;
        foreach (var line in lines[1..])
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) return null;
            keys[line[..separator]] = line[(separator + 1)..];
        }

        return keys;
    }

    private static string? Get(Dictionary<string, string>? keys, string name) => keys is not null && keys.TryGetValue(name, out var value) ? value : null;

    // Exec 的形态是 "<exe>" %u：先剥掉尾部的参数，再要求整体被引号包住。
    private static string? UnquotedExecutable(string? value)
    {
        if (value is null) return null;
        const string argument = " %u";
        if (!value.EndsWith(argument, StringComparison.Ordinal)) return null;
        var quoted = value[..^argument.Length];
        if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"') return null;
        return Unquote(quoted[1..^1]);
    }

    private static string Unquote(string value)
    {
        // Desktop-entry values escape backslash and the shell-significant characters; only what the test's
        // own Exec line can contain is undone here.
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length) { builder.Append(value[index]); continue; }
            builder.Append(value[index + 1]);
            index++;
        }

        return builder.ToString();
    }

    private static bool IsPngSized(byte[] bytes, int size)
    {
        // PNG signature, then IHDR: width at 16, height at 20, both big-endian.
        if (bytes.Length < 24) return false;
        if (bytes[0] != 0x89 || bytes[1] != (byte)'P' || bytes[2] != (byte)'N' || bytes[3] != (byte)'G') return false;
        return ReadBigEndian32(bytes, 16) == size && ReadBigEndian32(bytes, 20) == size;
    }

    private static int ReadBigEndian32(byte[] bytes, int offset)
        => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}
