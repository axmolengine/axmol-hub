using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AxmolHub.Core;
using Rect = AxmolHub.WindowsAutomation.Rect;

namespace AxmolHub;

/// <summary>Window-scoped Windows input. Unknown actions retain one-shot approval and live window verification.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsComputerUse
{
    internal static ComputerUseBridge InputBridge() => new(Operate)
    { AssessRisk = AssessRisk, ReportsInputDelivery = true, Preflight = Preflight, WaitForProcessing = WaitForProcessing };

    private static string? Preflight(CapturableWindow window, ComputerActionRequest request)
    {
        if (ComputerInput.Validate(request) is { } invalid) return invalid;
        if (request.Modifiers.Contains("command")) return "Refused [unsupported]: Command is not a Windows modifier.";
        if (request.Action == "press_key" && !TryVirtualKey(request.Key, out _)) return "Refused [unsupported]: unknown Windows key.";
        if (request.Action == "type" && !ValidUnicode(request.Text!)) return "Refused [unsupported]: unpaired UTF-16 surrogate.";
        if (ComputerUseTarget.FromWindow(window) is not { } target || !WindowMatches(window, target))
            return "Refused [target-changed]: window closed, moved, resized or changed owner. Observe again.";
        var foreground = GetForegroundWindow();
        if (foreground != window.Handle && !CanHandOff(foreground, target))
            return "Refused [user-takeover]: another application has the foreground. Select Hub or the target and observe again.";
        if (request.Action is "click" or "double_click" or "move" or "drag" or "scroll")
        {
            bool Inside(int x, int y) => x >= 0 && y >= 0 && x < window.Width && y < window.Height;
            if (!Inside(request.X, request.Y) || request.Action == "drag" && !Inside(request.EndX, request.EndY))
                return "Refused [bounds]: pointer endpoints must be inside the observed window.";
        }
        return null;
    }

    private static string? WaitForProcessing(CapturableWindow window, Func<bool> authorized, CancellationToken cancellation)
    {
        // WaitForInputIdle is not a document-success signal. Bound both it and the subsequent
        // visual settling wait; animations/carets need not stop for a fresh frame to be useful.
        cancellation.ThrowIfCancellationRequested();
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(window.ProcessId);
            process.WaitForInputIdle(150);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        var deadline = Environment.TickCount64 + 600;
        string? previous = null;
        var identical = 0;
        do
        {
            if (cancellation.WaitHandle.WaitOne(75)) cancellation.ThrowIfCancellationRequested();
            if (!authorized()) return "Permission changed after delivery; post-input settling capture stopped.";
            var frame = WindowsScreenCapture.Grab(window);
            if (frame is null || frame.Stats.IsBlank()) return "Post-input observation unavailable; application processing is unverified.";
            var current = frame.ContentFingerprint;
            identical = current is not null && current == previous ? identical + 1 : 0;
            if (identical >= 2) return "Post-input frames settled; this is not proof of application success.";
            previous = current;
        } while (Environment.TickCount64 < deadline);
        return "The application may still be processing or animating; inspect the new frame before continuing. Do not replay input.";
    }

    private const string Interrupted = "Refused [delivery-interrupted]: permission, window identity, focus, geometry or pointer ownership changed. Further input stopped.";
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, KeyUp = 0x0002, Unicode = 0x0004, ExtendedKey = 0x0001;

    private static string? Operate(CapturableWindow window, ComputerActionRequest request)
    {
        request.Cancellation.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return "Refused: Windows input is unavailable on this host.";
        if (Preflight(window, request) is { } preflight) return preflight;
        var target = ComputerUseTarget.FromWindow(window)!;
        // Approval deactivates the app, so verify before handing focus back and changing its chrome.
        if (request.VerifyObservation?.Invoke() != true)
            return "Refused: the input target or window changed or cannot be verified. Capture again before input.";
        if (Activate(window, target, request) is { } refusal) return refusal;

        bool Authorized()
        {
            request.Cancellation.ThrowIfCancellationRequested();
            return request.StillAuthorized?.Invoke() != false && GetForegroundWindow() == window.Handle
                && WindowMatches(window, target);
        }
        if (!Authorized()) return Interrupted;
        if (request.InputVerification is AutomationVerification verification && verification.VerifyFocused(Authorized, request.Cancellation) is { } changed)
            return "Refused: " + changed;
        if (request.GenericInteraction)
        {
            // A newly visible sensitive control must not inherit a generic assessment made while inactive.
            var liveRisk = AssessRisk(window, request with { Capabilities = ComputerCapabilities.Browse | ComputerCapabilities.Interact });
            try
            {
                if (liveRisk.Risk != ComputerRisk.Allow)
                    return "Refused [sensitive-target]: risk changed after activation; observe and request a separate action confirmation.";
            }
            finally { liveRisk.Verification?.Dispose(); }
        }
        if (request.GenericInteraction && request.InputVerification is not PointerVerification)
            return "Refused [authorization]: generic delivery has no live host verification.";
        if (!request.ExplicitlyApproved && !request.GenericInteraction && request.Action != "move"
            && request.InputVerification is not AutomationVerification { Capability: not ComputerCapabilities.None })
            return "Refused: no verified browsing capability or explicit action approval.";
        // SendInput does not reset the keyboard state. Never borrow a person's held shortcut or mouse button.
        if (AnyKeyHeld()) return "Refused: a keyboard key or mouse button is already held. Release it and observe again.";
        return Deliver(window, request, Authorized);
    }

    private static bool ValidUnicode(string text)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(remaining, out _, out var consumed) != System.Buffers.OperationStatus.Done)
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static bool WindowMatches(CapturableWindow window, ComputerUseTarget target)
    {
        if (!IsWindow(window.Handle) || !IsWindowVisible(window.Handle) || IsIconic(window.Handle)
            || !GetWindowRect(window.Handle, out var rect) || rect.Left != window.Left || rect.Top != window.Top
            || rect.Right - rect.Left != window.Width || rect.Bottom - rect.Top != window.Height) return false;
        GetWindowThreadProcessId(window.Handle, out var pid);
        var owner = DesktopApplicationIdentity.Resolve(pid);
        return pid == target.ProcessId && owner?.StartedAtUtcTicks == target.ProcessStartedAtUtcTicks
            && target.Application.IsSameApplication(owner?.Application);
    }

    private static bool CanHandOff(nint foreground, ComputerUseTarget target)
    {
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == Environment.ProcessId
            || target.Application.IsSameApplication(DesktopApplicationIdentity.Resolve(pid)?.Application);
    }

    private static string? Activate(CapturableWindow window, ComputerUseTarget target, ComputerActionRequest request)
    {
        bool Permitted()
        {
            request.Cancellation.ThrowIfCancellationRequested();
            return request.StillAuthorized?.Invoke() != false && WindowMatches(window, target);
        }
        if (!Permitted()) return Interrupted;
        if (GetForegroundWindow() == window.Handle) return null;
        if (!CanHandOff(GetForegroundWindow(), target))
            return "Refused: another application has the foreground. Switch to Hub or the authorized app and observe again.";
        if (!Permitted() || !CanHandOff(GetForegroundWindow(), target)) return Interrupted;
        // No AttachThreadInput, synthetic Alt, or foreground-lock workaround. Windows may deny this request.
        SetForegroundWindow(window.Handle);
        var deadline = Environment.TickCount64 + 1500;
        do
        {
            if (!Permitted()) return Interrupted;
            var foreground = GetForegroundWindow();
            if (foreground == window.Handle) return null;
            if (!CanHandOff(foreground, target))
                return "Refused: a third application took focus during handoff. No target input was attempted.";
            if (request.Cancellation.WaitHandle.WaitOne(25)) request.Cancellation.ThrowIfCancellationRequested();
        } while (Environment.TickCount64 < deadline);
        return "Refused: Windows did not give the exact target window focus within 1500 ms. Select the app and observe again; no target input was attempted.";
    }

    private static string? Deliver(CapturableWindow window, ComputerActionRequest r, Func<bool> authorized)
    {
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < window.Width && y < window.Height;
        Point At(int x, int y) => new() { X = window.Left + x, Y = window.Top + y };
        bool OwnsPoint(Point point)
        {
            var hit = WindowFromPoint(point);
            GetWindowThreadProcessId(hit, out var pid);
            return pid == window.ProcessId && GetAncestor(hit, 2) == window.Handle;
        }
        bool PointerAt(Point point) => GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y;
        bool Wait(int ms)
        {
            if (r.Cancellation.WaitHandle.WaitOne(ms)) r.Cancellation.ThrowIfCancellationRequested();
            return authorized();
        }
        bool Move(Point point)
        {
            if (!authorized() || !OwnsPoint(point)) return false;
            if (!SetCursorPos(point.X, point.Y)) return false;
            r.InputDeliveryStarted?.Invoke();
            return authorized() && PointerAt(point) && OwnsPoint(point);
        }
        bool SendMouse(uint flags, int data = 0)
            => Send([new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = unchecked((uint)data) } } }], r);
        bool SendKey(ushort key, bool up)
            => Send([Keyboard(key, 0, (up ? KeyUp : 0) | (IsExtended(key) ? ExtendedKey : 0))], r);

        if (r.Action is "click" or "double_click" or "move" or "drag" or "scroll")
        {
            if (!Inside(r.X, r.Y) || r.Action == "drag" && !Inside(r.EndX, r.EndY))
                return "Refused: pointer coordinates, including the drag endpoint, must stay inside the observed window.";
            var point = At(r.X, r.Y);
            if (!authorized() || !OwnsPoint(point) || r.Action == "drag" && !OwnsPoint(At(r.EndX, r.EndY)))
                return "Refused: the target point is occluded or the window lost authorization or focus.";
            if (r.InputVerification is AutomationVerification { Capability: ComputerCapabilities.SelectText or ComputerCapabilities.Scroll } browsing)
            {
                if (!GetCursorPos(out var originalPointer)) return "Refused: Windows could not read the pointer.";
                return browsing.Browse(() => authorized() && PointerAt(originalPointer) && OwnsPoint(point)
                    && (r.Action != "drag" || OwnsPoint(At(r.EndX, r.EndY))), r.InputDeliveryStarted);
            }
            if (!Move(point)) return Interrupted;
            if (r.Action == "move") return null;
            bool PointerReady() => authorized() && PointerAt(point) && OwnsPoint(point);
            if (!PointerReady()) return Interrupted;
            if (r.Action == "scroll")
            {
                if (r.Scroll != 0 && !SendMouse(0x0800, r.Scroll * 120)) return InputRefusal();
                if (!PointerReady()) return Interrupted;
                // Windows HWHEEL is positive right; the tool contract is positive left.
                if (r.HorizontalScroll != 0 && !SendMouse(0x1000, -r.HorizontalScroll * 120)) return InputRefusal();
                return null;
            }
            if (r.Action == "drag")
            {
                string? result = null;
                var attempted = false;
                var released = true;
                try
                {
                    if (!PointerReady()) result = Interrupted;
                    else
                    {
                        attempted = true;
                        if (!SendMouse(LeftDown)) result = InputRefusal();
                        else
                        {
                            var steps = Math.Clamp(r.DurationMs / 20, 5, 150);
                            for (var index = 1; index <= steps; index++)
                            {
                                if (!Wait(r.DurationMs / steps) || !PointerReady()) { result = Interrupted; break; }
                                var next = At(r.X + (int)((long)(r.EndX - r.X) * index / steps),
                                    r.Y + (int)((long)(r.EndY - r.Y) * index / steps));
                                if (!Move(next)) { result = Interrupted; break; }
                                point = next;
                            }
                        }
                    }
                }
                finally { if (attempted) released = SendMouse(LeftUp); }
                if (!released) return (result is null ? "" : result + " ") + InputRefusal();
                return result;
            }
            var count = r.Action == "double_click" ? 2 : 1;
            for (var click = 0; click < count; click++)
            {
                if (!PointerReady()) return Interrupted;
                var down = false;
                var up = false;
                try { down = SendMouse(LeftDown); }
                finally { up = SendMouse(LeftUp); }
                if (!down || !up) return InputRefusal();
                if (click + 1 < count && (!Wait((int)Math.Min(50u, Math.Max(1u, GetDoubleClickTime() / 4))) || !PointerReady()))
                    return Interrupted;
            }
            return null;
        }

        if (!GetCursorPos(out var pointer)) return "Refused: Windows could not read the pointer position.";
        bool KeyboardReady() => authorized() && PointerAt(pointer)
            && (r.InputVerification is not AutomationVerification verification || verification.KeyboardFocus());
        if (r.Action == "type")
        {
            AutomationVerification.TextDelivery? tracker = null;
            try
            {
                if (r.InputVerification is AutomationVerification textVerification)
                    tracker = textVerification.BeginTextDelivery();
            }
            catch (Exception ex) when (Unavailable(ex))
            { return "Refused: the exact text/selection acknowledgement baseline is unavailable. No text was posted."; }
            using var textTracker = tracker;
            var previousCr = false;
            foreach (var rune in r.Text!.EnumerateRunes())
            {
                if (!KeyboardReady()) return Interrupted;
                if (textTracker?.ConfirmBeforeInput() is { } changedText) return changedText;
                if (!KeyboardReady()) return Interrupted;
                // Deliver CRLF as one Enter. Tab/Enter use real keys instead of unhandled VK_PACKET controls.
                if (rune.Value == '\n' && previousCr) { previousCr = false; continue; }
                previousCr = rune.Value == '\r';
                if (rune.Value is '\r' or '\n' or '\t')
                {
                    var key = (ushort)(rune.Value == '\t' ? 0x09 : 0x0D);
                    var down = false;
                    var up = false;
                    try { down = SendKey(key, false); }
                    finally { up = SendKey(key, true); }
                    if (!down || !up) return InputRefusal();
                }
                else
                {
                    // One batch per Unicode scalar keeps a surrogate pair together. Never use the clipboard.
                    var units = rune.ToString();
                    var inputs = units.SelectMany(c => new[] { Keyboard(0, c, Unicode), Keyboard(0, c, Unicode | KeyUp) }).ToArray();
                    var delivered = false;
                    var released = true;
                    try { delivered = Send(inputs, r); }
                    finally
                    {
                        if (!delivered)
                            released = Send(units.Select(c => Keyboard(0, c, Unicode | KeyUp)).ToArray(), r);
                    }
                    if (!delivered || !released) return InputRefusal();
                }
                var inserted = rune.Value is '\r' or '\n' ? "\n" : rune.ToString();
                if (textTracker?.Acknowledge(inserted, () => authorized() && PointerAt(pointer), r.Cancellation) is { } mismatch)
                    return mismatch;
            }
            return null;
        }
        if (r.Action != "press_key" || !TryVirtualKey(r.Key, out var virtualKey))
            return "Refused: unsupported Windows input action.";
        var held = new List<ushort>();
        var cleanupOk = true;
        string? keyResult = null;
        try
        {
            foreach (var modifier in r.Modifiers)
            {
                if (!KeyboardReady()) { keyResult = Interrupted; break; }
                var key = (ushort)(modifier switch { "control" => 0xA2, "alt" => 0xA4, "shift" => 0xA0,
                    _ => throw new InvalidOperationException("Unsupported Windows modifier.") });
                held.Add(key);
                if (!SendKey(key, false)) { keyResult = InputRefusal(); break; }
            }
            if (keyResult is null)
            {
                if (!KeyboardReady()) keyResult = Interrupted;
                else
                {
                    held.Add(virtualKey);
                    if (!SendKey(virtualKey, false)) keyResult = InputRefusal();
                }
            }
        }
        finally
        {
            // Cleanup must run even after cancellation/revocation and must never press another key.
            foreach (var key in held.AsEnumerable().Reverse()) cleanupOk &= SendKey(key, true);
        }
        if (!cleanupOk) return (keyResult is null ? "" : keyResult + " ") + InputRefusal();
        if (keyResult is not null) return keyResult;
        // Copy is queued by the app. Give it a bounded processing interval before the next observation,
        // without reading or replacing the clipboard and without replaying the shortcut.
        if (virtualKey == 'C' && r.Modifiers.Contains("control") && !Wait(100)) return Interrupted;
        return null;
    }

    private static bool AnyKeyHeld()
    {
        for (var key = 1; key <= 254; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    private static bool TryVirtualKey(string? name, out ushort key)
    {
        var normalized = (name ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 1 && char.IsAsciiLetterOrDigit(normalized[0])) { key = normalized[0]; return true; }
        if (normalized.StartsWith('F') && int.TryParse(normalized.AsSpan(1), out var function) && function is >= 1 and <= 24)
        { key = (ushort)(0x70 + function - 1); return true; }
        key = normalized switch
        {
            "ENTER" or "RETURN" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08, "DELETE" => 0x2E, "SPACE" => 0x20,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "INSERT" => 0x2D,
            _ => 0,
        };
        return key != 0;
    }

    private static bool IsExtended(ushort key) => key is >= 0x21 and <= 0x28 or 0x2D or 0x2E;
    private static Input Keyboard(ushort key, ushort scan, uint flags)
        => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, ScanCode = scan, Flags = flags } } };
    private static bool Send(Input[] inputs, ComputerActionRequest request)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent > 0) request.InputDeliveryStarted?.Invoke();
        return sent == inputs.Length;
    }
    private static string InputRefusal()
        => "Refused: Windows did not deliver all input or key/button releases. SendInput may be blocked by UIPI for elevated apps. Use the same privilege level and observe before any retry; never replay partially delivered input.";

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int Dx, Dy; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
