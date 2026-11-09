using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AxmolHub.Core;

namespace AxmolHub;

internal static class LinuxComputerUse
{
    public static ScreenCaptureBridge ScreenBridge() => new(ListWindows, CaptureWindow);
    public static ComputerUseBridge InputBridge() => new(Operate);

    private static IReadOnlyList<CapturableWindow> ListWindows()
    {
        EnsureX11();
        var wmctrl = CommandShells.Resolve("wmctrl")
                     ?? throw new InvalidOperationException("X11 Computer Use needs wmctrl to list application windows.");
        var result = Run(wmctrl, ["-lpG"]);
        if (result.Code != 0) throw new InvalidOperationException("wmctrl could not list the X11 windows.");
        var windows = new List<CapturableWindow>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9 || !nint.TryParse(fields[0].AsSpan(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(fields[2], out var pid)
                || !int.TryParse(fields[3], out var left)
                || !int.TryParse(fields[4], out var top)
                || !int.TryParse(fields[5], out var width)
                || !int.TryParse(fields[6], out var height)) continue;
            if (width < 32 || height < 32) continue;
            windows.Add(new CapturableWindow(id, fields[8], pid, width, height)
            { Owner = DesktopApplicationIdentity.Resolve(pid), Left = left, Top = top });
        }
        return windows;
    }

    private static CapturedFrame? CaptureWindow(CapturableWindow? window)
    {
        if (window is null) return null;
        EnsureX11();
        var imageTool = CommandShells.Resolve("maim") ?? CommandShells.Resolve("import");
        if (imageTool is null)
            throw new InvalidOperationException("X11 window capture needs maim or ImageMagick import.");
        var path = Path.Combine(Path.GetTempPath(), $"axmolhub-computer-use-{Guid.NewGuid():N}.png");
        try
        {
            var args = Path.GetFileName(imageTool).Equals("maim", StringComparison.Ordinal)
                ? new[] { "-i", $"0x{window.Handle:x}", path }
                : new[] { "-window", $"0x{window.Handle:x}", path };
            var result = Run(imageTool, args, timeoutMs: 8000);
            if (result.Code != 0 || !File.Exists(path)) return null;
            var png = File.ReadAllBytes(path);
            using var stream = new MemoryStream(png);
            using var bitmap = new Bitmap(stream);
            using var pixels = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var locked = pixels.Lock();
            bitmap.CopyPixels(locked);
            var bytes = new byte[locked.RowBytes * locked.Size.Height];
            Marshal.Copy(locked.Address, bytes, 0, bytes.Length);
            return new CapturedFrame(png, locked.Size.Width, locked.Size.Height,
                FrameAnalysis.Analyze(bytes, locked.Size.Width, locked.Size.Height, locked.RowBytes))
                { ContentFingerprint = FrameAnalysis.Fingerprint(bytes) };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ExternalException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static string? Operate(CapturableWindow window, ComputerActionRequest request)
    {
        request.Cancellation.ThrowIfCancellationRequested();
        try { EnsureX11(); }
        catch (InvalidOperationException ex) { return $"Refused: {ex.Message}"; }
        if (ComputerUseTarget.FromWindow(window) is not { } target || !target.Find(ListWindows()).Ok)
            return "Refused: the selected application's identity changed before input.";
        var xdotool = CommandShells.Resolve("xdotool");
        if (xdotool is null)
            return "Refused: X11 Computer Use needs xdotool. Install it, then ask before retrying.";
        if (request.VerifyObservation?.Invoke() != true)
            return "Refused: the application image changed or cannot be verified. Capture again before input.";
        var id = $"0x{window.Handle:x}";
        if (!Focused(xdotool, window.Handle))
        {
            var active = Run(xdotool, ["getactivewindow", "getwindowpid"]);
            if (active.Code != 0 || !int.TryParse(active.Output.Trim(), out var activePid) || activePid != Environment.ProcessId)
                return "Refused: another app has focus. User takeover pauses input; select the target and capture again.";
        }
        var activate = Run(xdotool, ["windowactivate", "--sync", id]);
        if (activate.Code != 0) return "Refused: X11 did not activate the selected app window.";
        var focused = Run(xdotool, ["getactivewindow"]);
        var foregroundText = focused.Output.Trim();
        var hasHexPrefix = foregroundText.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!nint.TryParse(hasHexPrefix ? foregroundText.AsSpan(2) : foregroundText.AsSpan(),
                hasHexPrefix ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var foreground) || foreground != window.Handle)
            return "Refused: the selected app did not receive X11 focus. Do not retry automatically.";

        if (target.Find(ListWindows()).Window is not { } current || current.Left != window.Left
            || current.Top != window.Top || current.Width != window.Width || current.Height != window.Height)
            return "Refused: the selected application's identity or geometry changed after activation. Capture again.";
        request.Cancellation.ThrowIfCancellationRequested();
        string[] args;
        switch (request.Action)
        {
            case "click":
                if (request.X < 0 || request.Y < 0 || request.X >= window.Width || request.Y >= window.Height)
                    return "Refused: click coordinates must be inside the selected application window.";
                args = ["mousemove", "--window", id, request.X.ToString(CultureInfo.InvariantCulture),
                    request.Y.ToString(CultureInfo.InvariantCulture), "click", "1"];
                break;
            case "type":
                var pointer = Run(xdotool, ["getmouselocation", "--shell"]);
                if (pointer.Code != 0) return "Refused: X11 could not read the pointer position.";
                foreach (var rune in (request.Text ?? "").EnumerateRunes())
                {
                    request.Cancellation.ThrowIfCancellationRequested();
                    var currentPointer = Run(xdotool, ["getmouselocation", "--shell"]);
                    if (currentPointer.Code != 0 || currentPointer.Output != pointer.Output)
                        return "Refused: pointer activity detected; user takeover paused typing.";
                    if (!target.Find(ListWindows()).Ok || !Focused(xdotool, window.Handle))
                        return "Refused: the selected window lost its identity or focus during input.";
                    if (Run(xdotool, ["type", "--clearmodifiers", "--delay", "1", "--window", id, "--", rune.ToString()]).Code != 0)
                        return "Refused: X11 text delivery failed. Nothing further was sent.";
                }
                return null;
            case "press_key":
                if (!AllowedKey(request.Key))
                    return "Refused: unsupported key name. Use a letter, digit, Enter, Tab, Escape, arrows, Backspace, Delete, or Space.";
                args = ["key", "--clearmodifiers", "--window", id, NormalizeKey(request.Key!)];
                break;
            case "scroll":
                var button = request.Scroll > 0 ? "4" : "5";
                for (var index = 0; index < Math.Abs(request.Scroll); index++)
                {
                    request.Cancellation.ThrowIfCancellationRequested();
                    if (!Focused(xdotool, window.Handle) || !target.Find(ListWindows()).Ok)
                        return "Refused: the selected window lost identity or focus during scrolling.";
                    if (Run(xdotool, ["mousemove", "--window", id, (window.Width / 2).ToString(CultureInfo.InvariantCulture),
                        (window.Height / 2).ToString(CultureInfo.InvariantCulture), "click", button]).Code != 0)
                        return "Refused: X11 scroll delivery failed.";
                }
                return null;
            default:
                return "Refused: unsupported Computer Use action.";
        }
        request.Cancellation.ThrowIfCancellationRequested();
        if (!Focused(xdotool, window.Handle)) return "Refused: the selected window lost focus before input.";
        var sent = Run(xdotool, args);
        if (sent.Code != 0)
            return "Refused: X11 input was not delivered to the selected app. Check desktop permissions and ask before retrying.";
        return null;
    }

    private static bool Focused(string xdotool, nint expected)
    {
        var focus = Run(xdotool, ["getactivewindow"]);
        return focus.Code == 0 && long.TryParse(focus.Output.Trim(), out var value) && value == (long)expected;
    }

    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => "Return", "escape" or "esc" => "Escape",
        "tab" => "Tab", "left" => "Left", "right" => "Right", "up" => "Up", "down" => "Down",
        "backspace" => "BackSpace", "delete" => "Delete", "space" => "space", "home" => "Home",
        "end" => "End", "pageup" => "Prior", "pagedown" => "Next", _ => key.Trim().ToLowerInvariant(),
    };

    private static bool AllowedKey(string? key)
    {
        var value = (key ?? "").Trim();
        return value.Length == 1 && char.IsAsciiLetterOrDigit(value[0])
               || value.ToLowerInvariant() is "enter" or "return" or "tab" or "escape" or "esc"
                   or "left" or "right" or "up" or "down" or "backspace" or "delete" or "space"
                   or "home" or "end" or "pageup" or "pagedown";
    }

    private static void EnsureX11()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Wayland Computer Use requires an XDG RemoteDesktop portal session; this Hub build cannot create one. Ask the user to operate the app manually instead of falling back to global input injection.");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("No X11 display is available for Computer Use.");
    }

    private static (int Code, string Output) Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs = 5000)
    {
        try
        {
            var start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return (-1, "");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return (-1, "");
            }
            errorTask.GetAwaiter().GetResult();
            return (process.ExitCode, outputTask.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }
}
