using System.Diagnostics;

namespace AxmolHub.Core;

/// <summary>Reveals a file without opening it in its associated editor. Arguments never pass through a shell.</summary>
public static class FileManagerReveal
{
    public static string LabelKey => OperatingSystem.IsMacOS() ? "RevealInFinder"
        : OperatingSystem.IsWindows() ? "RevealInExplorer" : "RevealInFileManager";

    public static string[] CommandFor(string platform, string path)
    {
        var full = Path.GetFullPath(path);
        return platform switch
        {
            "macOS" => ["/usr/bin/open", "-R", full],
            "Windows" => ["explorer.exe", "/select,", full],
            _ => ["dbus-send", "--session", "--print-reply", "--reply-timeout=3000",
                "--dest=org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                "org.freedesktop.FileManager1.ShowItems", "array:string:" + new Uri(full).AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal), "string:"],
        };
    }

    public static async Task<bool> TryRevealAsync(string path)
    {
        if (!File.Exists(path)) return false;
        var platform = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux";
        if (await LaunchAsync(CommandFor(platform, path), wait: platform != "Windows")) return true;
        // Desktops without FileManager1 still get the containing folder, never the file's associated app.
        return platform == "Linux" && await LaunchAsync(["xdg-open", Path.GetDirectoryName(Path.GetFullPath(path))!], wait: true);
    }

    private static async Task<bool> LaunchAsync(string[] command, bool wait)
    {
        try
        {
            var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = wait, RedirectStandardError = wait };
            foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!wait) return true;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(); return false; }
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
