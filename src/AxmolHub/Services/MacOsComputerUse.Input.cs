using System.Runtime.InteropServices;
using AxmolHub.Core;

namespace AxmolHub;

internal static partial class MacOsComputerUse
{
    private static string? ActivateWindow(CapturableWindow window, ComputerActionRequest request, ComputerUseTarget target, int fromPid)
    {
        bool Permitted() => !request.Cancellation.IsCancellationRequested
            && request.StillAuthorized?.Invoke() != false && target.Find(ListWindows()).Ok;
        if (!Permitted()) return "Refused: application permission or window identity changed before focus handoff.";
        var app = AXUIElementCreateApplication(window.ProcessId);
        if (app == 0) return "Refused: the target application's accessibility tree is unavailable for focus handoff.";
        try
        {
            AXUIElementSetMessagingTimeout(app, 0.25f);
            var windows = AxAttribute(app, "AXWindows");
            if (windows == 0) return "Refused: macOS did not expose the target application's windows for focus handoff.";
            try
            {
                nint selected = 0;
                for (nint index = 0; index < CFArrayGetCount(windows); index++)
                {
                    var element = CFArrayGetValueAtIndex(windows, index);
                    if (!MatchesAccessibilityWindow(element, window)) continue;
                    if (selected != 0) return "Refused: the target accessibility window is ambiguous; no window was raised.";
                    selected = element;
                }
                if (selected == 0) return "Refused: macOS could not match the authorized window by title and geometry for focus handoff.";
                if (!Permitted()) return "Refused: application permission or window identity changed before focus handoff.";
                if (!Activate(window.ProcessId, fromPid, Permitted))
                    return "Refused: focus or permission changed, or macOS rejected activation of the authorized application.";
                if (!Permitted()) return "Refused: application permission or window identity changed before raising the window.";
                var raise = Key("AXRaise");
                try
                {
                    var error = AXUIElementPerformAction(selected, raise);
                    if (error != 0) return $"Refused: macOS could not raise the authorized window (AX error {error}).";
                }
                finally { CFRelease(raise); }

                // Activation is asynchronous. Wait once for this exact window, not for any same-titled app.
                // Revoke/cancel or a switch to a third application ends the handoff without sending input.
                var deadline = Environment.TickCount64 + 1500;
                do
                {
                    if (!Permitted()) return "Refused: application permission or window identity changed during focus handoff.";
                    if (IsFrontmost(window)) return null;
                    var foreground = FrontmostPid();
                    if (foreground != fromPid && foreground != window.ProcessId)
                        return $"Refused: focus moved to another application (pid {foreground}) during handoff.";
                    if (request.Cancellation.WaitHandle.WaitOne(25)) break;
                } while (Environment.TickCount64 < deadline);
                return $"Refused: focus handoff to window {window.Handle} in pid {window.ProcessId} did not complete within 1500 ms (frontmost pid {FrontmostPid()}). No target input was attempted.";
            }
            finally { CFRelease(windows); }
        }
        finally { CFRelease(app); }
    }

    private static bool OwnsPoint(CapturableWindow window, CGPoint point)
    {
        // Window-list rectangles are not hit-test regions: Dock owns a monitor-sized layer even
        // where it receives no input. Ask macOS for the actual hit, then verify its process and window.
        var desktop = AXUIElementCreateSystemWide();
        nint element = 0;
        if (desktop == 0) return false;
        try
        {
            AXUIElementSetMessagingTimeout(desktop, 0.25f);
            if (AXUIElementCopyElementAtPosition(desktop, (float)point.X, (float)point.Y, out element) != 0
                || element == 0 || AXUIElementGetPid(element, out var pid) != 0 || pid != window.ProcessId)
                return false;
            for (var depth = 0; element != 0 && depth < 16; depth++)
            {
                if (AxText(element, "AXRole") == "AXWindow") return MatchesAccessibilityWindow(element, window);
                var owner = AxAttribute(element, "AXWindow");
                if (owner != 0)
                {
                    try { return MatchesAccessibilityWindow(owner, window); }
                    finally { CFRelease(owner); }
                }
                var parent = AxAttribute(element, "AXParent");
                CFRelease(element);
                element = parent;
            }
            return false;
        }
        finally
        {
            if (element != 0) CFRelease(element);
            CFRelease(desktop);
        }
    }

    private static string? Deliver(CapturableWindow window, ComputerActionRequest r, CGRect bounds, Func<bool> authorized)
    {
        const string interrupted = "Refused: permission, focus, geometry or pointer ownership changed. Further input stopped.";
        CGPoint Point(int x, int y) => new(bounds.Origin.X + x, bounds.Origin.Y + y);
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < bounds.Size.Width && y < bounds.Size.Height;
        bool Wait(int ms) => !r.Cancellation.WaitHandle.WaitOne(ms) && authorized();
        if (r.Action is "click" or "double_click" or "move" or "drag" or "scroll")
        {
            if (!Inside(r.X, r.Y) || r.Action == "drag" && !Inside(r.EndX, r.EndY))
                return "Refused: pointer coordinates must remain within the captured window.";
            var point = Point(r.X, r.Y);
            if (!authorized()) return interrupted;
            if (!OwnsPoint(window, point))
                return "Refused: macOS hit testing did not resolve this point to the selected application window. Nothing was sent.";
            r.InputDeliveryStarted?.Invoke();
            if (!PostMouse(point, 5)) return "Refused: macOS could not create pointer motion.";
            if (r.Action == "move") return null;
            if (!Wait(25) || !OwnsPoint(window, point)) return interrupted;
            if (r.Action == "scroll")
            {
                // Use the fixed-argument API rather than a C variadic call, whose ARM64 ABI differs.
                var value = CGEventCreateScrollWheelEvent2(0, 1, 2, r.Scroll, r.HorizontalScroll, 0);
                if (value == 0) return "Refused: macOS could not create the scroll event.";
                try
                {
                    CGEventSetLocation(value, point);
                    if (!authorized() || !OwnsPoint(window, point)) return interrupted;
                    CGEventPost(0, value);
                    return null;
                }
                finally { CFRelease(value); }
            }
            if (r.Action == "drag")
            {
                var last = point;
                try
                {
                    if (!PostMouse(point, 1)) return "Refused: macOS could not create mouse-down.";
                    var steps = Math.Clamp(r.DurationMs / 20, 5, 150);
                    for (var index = 1; index <= steps; index++)
                    {
                        if (!Wait(r.DurationMs / steps)) return interrupted;
                        var actual = PointerLocation();
                        if (actual is null || Math.Abs(actual.Value.X - last.X) > 2 || Math.Abs(actual.Value.Y - last.Y) > 2)
                            return "Refused: user pointer takeover interrupted dragging.";
                        var next = new CGPoint(point.X + (r.EndX - r.X) * (double)index / steps,
                            point.Y + (r.EndY - r.Y) * (double)index / steps);
                        if (!OwnsPoint(window, next)) return interrupted;
                        if (!PostMouse(next, 6)) return "Refused: macOS could not create drag motion.";
                        last = next;
                    }
                    return null;
                }
                finally { PostMouse(last, 2); }
            }
            var count = r.Action == "double_click" ? 2 : 1;
            for (var click = 1; click <= count; click++)
            {
                if (!authorized() || !OwnsPoint(window, point)) return interrupted;
                try { if (!PostMouse(point, 1, click)) return "Refused: macOS could not create mouse-down."; }
                finally { PostMouse(point, 2, click); }
                if (click < count && !Wait(70)) return interrupted;
            }
            return null;
        }
        if (r.Action == "type")
        {
            var pointer = PointerLocation();
            if (pointer is null) return "Refused: macOS could not read the pointer.";
            foreach (var rune in (r.Text ?? "").EnumerateRunes())
            {
                if (!authorized()) return interrupted;
                var current = PointerLocation();
                if (current is null || current.Value.X != pointer.Value.X || current.Value.Y != pointer.Value.Y)
                    return "Refused: user pointer takeover interrupted typing.";
                r.InputDeliveryStarted?.Invoke();
                if (!PostUnicode(rune.ToString().ToCharArray())) return "Refused: macOS could not create text events.";
            }
            return null;
        }
        if (r.Action != "press_key" || !TryKeyCode(r.Key, out var key)) return "Refused: unsupported key or action.";
        var revision = ClipboardRevision();
        var held = new List<(ushort Key, ulong Flag)>();
        ulong flags = 0;
        try
        {
            foreach (var modifier in r.Modifiers)
            {
                if (!authorized()) return interrupted;
                var (code, flag) = modifier switch
                {
                    "command" => ((ushort)55, 1UL << 20), "shift" => ((ushort)56, 1UL << 17),
                    "alt" => ((ushort)58, 1UL << 19), "control" => ((ushort)59, 1UL << 18),
                    _ => throw new InvalidOperationException("Unsupported modifier."),
                };
                flags |= flag;
                held.Add((code, flag));
                r.InputDeliveryStarted?.Invoke();
                if (!PostKey(code, true, flags)) return "Refused: macOS could not create modifier input.";
            }
            if (!authorized()) return interrupted;
            r.InputDeliveryStarted?.Invoke();
            try { if (!PostKey(key, true, flags)) return "Refused: macOS could not create key input."; }
            finally { PostKey(key, false, flags); }
        }
        finally
        {
            foreach (var modifier in held.AsEnumerable().Reverse())
            {
                flags &= ~modifier.Flag;
                PostKey(modifier.Key, false, flags);
            }
        }
        // Observe copy ownership without reading clipboard bytes; delayed copies simply require confirmation.
        if (r.Key?.Equals("c", StringComparison.OrdinalIgnoreCase) == true && r.Modifiers.Contains("command"))
        {
            if (!Wait(100)) return interrupted;
            RecordCopy(r, revision);
        }
        return null;
    }

    [DllImport(ApplicationServices)] private static extern nint AXUIElementCreateSystemWide();
    [DllImport(ApplicationServices)] private static extern int AXUIElementPerformAction(nint element, nint action);
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount,
        int wheel1, int wheel2, int wheel3);
    [DllImport(CoreGraphics)] private static extern void CGEventSetLocation(nint value, CGPoint point);
}
