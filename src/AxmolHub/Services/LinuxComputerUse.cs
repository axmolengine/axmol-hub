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
    public static ComputerUseBridge InputBridge() => new(Operate)
    {
        Preflight = Preflight, ReportsInputDelivery = true, WaitForProcessing = WaitForProcessing,
        AssessRisk = AssessRisk,
    };

    private static IReadOnlyList<CapturableWindow> ListWindows() => ListWindows(default);
    private static IReadOnlyList<CapturableWindow> ListWindows(CancellationToken cancellation)
    {
        var backend = CaptureBackends.ForCurrent();
        if (!backend.Available) throw new InvalidOperationException(backend.Refusal);
        var result = Run(CommandShells.Resolve("wmctrl")!, ["-lpG"], cancellation);
        if (result.Code != 0) throw new InvalidOperationException("wmctrl could not list the X11 windows.");
        var windows = new List<CapturableWindow>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9 || !fields[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !nint.TryParse(fields[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(fields[2], out var pid) || !int.TryParse(fields[3], out var left)
                || !int.TryParse(fields[4], out var top) || !int.TryParse(fields[5], out var width)
                || !int.TryParse(fields[6], out var height) || width < 32 || height < 32) continue;
            windows.Add(new(id, fields[8], pid, width, height)
            { Owner = DesktopApplicationIdentity.Resolve(pid), Left = left, Top = top });
        }
        return windows;
    }

    private static CapturedFrame? CaptureWindow(CapturableWindow? window)
    {
        if (window is null) return null;
        var backend = CaptureBackends.ForCurrent();
        if (!backend.Available) throw new InvalidOperationException(backend.Refusal);
        var imageTool = CommandShells.Resolve("maim") ?? CommandShells.Resolve("import")!;
        var path = Path.Combine(Path.GetTempPath(), $"axmolhub-computer-use-{Guid.NewGuid():N}.png");
        try
        {
            var args = Path.GetFileName(imageTool).Equals("maim", StringComparison.Ordinal)
                ? new[] { "-i", $"0x{window.Handle:x}", path } : new[] { "-window", $"0x{window.Handle:x}", path };
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
            return new(png, locked.Size.Width, locked.Size.Height,
                FrameAnalysis.Analyze(bytes, locked.Size.Width, locked.Size.Height, locked.RowBytes))
                { ContentFingerprint = FrameAnalysis.Fingerprint(bytes) };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ExternalException) { return null; }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static string? Preflight(CapturableWindow window, ComputerActionRequest request)
    {
        if (ComputerInput.Validate(request) is { } invalid) return invalid;
        var backend = CaptureBackends.ForCurrent();
        if (backend.Refusal is { } unavailable) return unavailable;
        if (backend.InputRefusal is { } missing) return missing;
        if (request.Modifiers.Contains("command")) return "Refused [unsupported]: Command is not an X11 modifier.";
        if (request.Action == "press_key" && LinuxX11Input.KeyName(request.Key!).Length == 0)
            return "Refused [unsupported]: unknown X11 key.";
        if (request.Action == "type" && !ValidUnicode(request.Text!)) return "Refused [unsupported]: invalid Unicode text.";
        if (ComputerUseTarget.FromWindow(window) is not { } target || !Matches(window, target, request.Cancellation))
            return "Refused [target-changed]: the X11 window closed, moved, resized or changed owner. Observe again.";
        try
        {
            using var input = new LinuxX11Input();
            return input.Active == window.Handle || CanHandOff(input.Active, target, request.Cancellation)
                ? null : "Refused [user-takeover]: another application has focus. Select Hub or the target and observe again.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { return "Refused [unsupported]: X11 input requires a usable libxdo.so.3 connection (provided with xdotool)."; }
    }
    private static bool ValidUnicode(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != System.Buffers.OperationStatus.Done
                || rune.Value == 0) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
    private static bool Matches(CapturableWindow window, ComputerUseTarget target, CancellationToken ct)
        => target.Find(ListWindows(ct)).Window is { } current && current.Left == window.Left && current.Top == window.Top
            && current.Width == window.Width && current.Height == window.Height;
    private static bool CanHandOff(nint active, ComputerUseTarget target, CancellationToken ct)
        => ListWindows(ct).FirstOrDefault(w => w.Handle == active) is { } current
            && (current.ProcessId == Environment.ProcessId || target.Application.IsSameApplication(current.Owner?.Application));

    private static string? Operate(CapturableWindow window, ComputerActionRequest request)
    {
        if (Preflight(window, request) is { } refusal) return refusal;
        var target = ComputerUseTarget.FromWindow(window)!;
        if (request.VerifyObservation?.Invoke() != true)
            return "Refused [target-verification]: the observed target changed. Observe again.";
        using var input = new LinuxX11Input();
        bool Permitted()
        {
            request.Cancellation.ThrowIfCancellationRequested();
            return request.StillAuthorized?.Invoke() != false && Matches(window, target, request.Cancellation);
        }
        if (input.Active != window.Handle)
        {
            if (!Permitted() || !CanHandOff(input.Active, target, request.Cancellation) || !input.Activate(window.Handle))
                return "Refused: X11 focus handoff failed. No target input was sent.";
            var deadline = Environment.TickCount64 + 1500;
            while (input.Active != window.Handle)
            {
                if (!Permitted() || !CanHandOff(input.Active, target, request.Cancellation) || Environment.TickCount64 >= deadline)
                    return "Refused: X11 focus handoff interrupted or timed out. No target input was sent.";
                if (request.Cancellation.WaitHandle.WaitOne(25)) request.Cancellation.ThrowIfCancellationRequested();
            }
        }
        bool Authorized() => Permitted() && input.Active == window.Handle;
        if (!Authorized() || input.Held()) return "Refused: authorization changed or a key/button is already held. Observe again.";
        if (request.GenericInteraction)
        {
            var risk = AssessRisk(window, request with { Capabilities = ComputerCapabilities.Browse | ComputerCapabilities.Interact });
            try { if (risk.Risk != ComputerRisk.Allow) return "Refused: sensitive target appeared after activation; confirm separately."; }
            finally { risk.Verification?.Dispose(); }
        }
        if (request.InputVerification is LinuxAccessibility.Verification verification)
        {
            if (verification.Verify() is { } changed) return "Refused: " + changed;
            if (request.Action is "type" or "press_key" && !verification.KeyboardFocused())
                return "Refused: the assessed AT-SPI text control does not own keyboard focus.";
            if (verification.Capability == ComputerCapabilities.SelectText)
            {
                var pointer = input.Pointer();
                return verification.Select(() => Authorized() && input.Pointer() == pointer
                    && input.Owns(window.Handle, window.Left + request.X, window.Top + request.Y)
                    && input.Owns(window.Handle, window.Left + request.EndX, window.Top + request.EndY), request.InputDeliveryStarted);
            }
        }
        return input.Deliver(window, request, Authorized);
    }

    private sealed class PointerVerification : IComputerInputVerification
    {
        public string? Verify() => null;
        public void Dispose() { }
    }
    private static ComputerRiskDecision AssessRisk(CapturableWindow window, ComputerActionRequest request)
    {
        if (request.Action == "press_key" && (request.Modifiers.Contains("alt")
            || request.Modifiers.Contains("control") && string.Equals(request.Key, "Enter", StringComparison.OrdinalIgnoreCase)))
            return new(ComputerRisk.RequireConfirmation, "Potential menu or submission shortcut requires separate confirmation.");
        if (request.Action == "move") return new(ComputerRisk.Allow, "Verified window-scoped pointer movement.")
            { Capability = ComputerCapabilities.MovePointer, Verification = new PointerVerification() };
        var decision = LinuxAccessibility.Assess(window, request);
        if (decision.Verification is null && decision.Risk == ComputerRisk.RequireConfirmation
            && decision.Reason == ComputerRiskDecision.Unknown.Reason && (request.Capabilities & ComputerCapabilities.Interact) != 0)
            return new(ComputerRisk.Allow, "Explicit temporary X11 interaction consent; semantic target is unavailable.")
                { Capability = ComputerCapabilities.Interact, GenericInteraction = true, Verification = new PointerVerification() };
        return decision;
    }

    private static string? WaitForProcessing(CapturableWindow window, Func<bool> authorized, CancellationToken cancellation)
    {
        string? previous = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
            if (!authorized()) return "Permission changed after delivery; observation stopped.";
            var frame = CaptureWindow(window);
            if (frame is null || frame.Stats.IsBlank()) return "Post-input frame unavailable; outcome unverified.";
            if (previous is not null && previous == frame.ContentFingerprint) return "Post-input frames settled; application outcome remains unverified.";
            previous = frame.ContentFingerprint;
        }
        return "The application may still be rendering; inspect the returned frame. Do not replay input.";
    }

    private static (int Code, string Output) Run(string fileName, IReadOnlyList<string> arguments,
        CancellationToken cancellation = default, int timeoutMs = 5000)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var start = new ProcessStartInfo(fileName)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return (-1, "");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeoutMs);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
                error.GetAwaiter().GetResult();
                return (process.ExitCode, output.GetAwaiter().GetResult());
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return (-1, ""); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return (-1, ""); }
    }
}
