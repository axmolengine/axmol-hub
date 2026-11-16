using System.Runtime.InteropServices;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>One X11 connection per input, with scalar delivery and bounded cooperative checks.</summary>
internal sealed class LinuxX11Input : IDisposable
{
    private const string Xdo = "libxdo.so.3", X11 = "libX11.so.6";
    private readonly nint _xdo;
    private readonly nint _display;
    internal LinuxX11Input()
    {
        _xdo = xdo_new(null);
        if (_xdo == 0) throw new InvalidOperationException("Cannot open the X11 input connection.");
        _display = Marshal.ReadIntPtr(_xdo);
    }
    internal nint Active => xdo_get_active_window(_xdo, out var window) == 0 ? window : 0;
    internal bool Activate(nint window) => xdo_activate_window(_xdo, window) == 0;
    internal (int X, int Y) Pointer()
        => xdo_get_mouse_location(_xdo, out var x, out var y, out _) == 0 ? (x, y)
            : throw new InvalidOperationException("Cannot read the X11 pointer.");
    internal bool Held()
    {
        var keys = new byte[32];
        XQueryKeymap(_display, keys);
        return keys.Any(value => value != 0) || !XQueryPointer(_display, XDefaultRootWindow(_display),
            out _, out _, out _, out _, out _, out _, out var mask) || (mask & 0x1f00) != 0;
    }
    internal bool Owns(nint expected, int x, int y)
    {
        var window = XDefaultRootWindow(_display);
        for (var depth = 0; depth < 64; depth++)
        {
            if (window == expected) return true;
            if (!XTranslateCoordinates(_display, XDefaultRootWindow(_display), window, x, y, out var wx, out var wy, out _)
                || !XTranslateCoordinates(_display, window, window, wx, wy, out _, out _, out var child) || child == 0) return false;
            window = child;
        }
        return false;
    }
    internal string? Deliver(CapturableWindow window, ComputerActionRequest r, Func<bool> authorized)
    {
        var pointer = Pointer();
        bool Ready() => authorized() && Pointer() == pointer
            && (r.Action is not ("type" or "press_key") || r.InputVerification is not LinuxAccessibility.Verification semantic || semantic.KeyboardFocused());
        bool Wait(int ms)
        {
            if (r.Cancellation.WaitHandle.WaitOne(ms)) r.Cancellation.ThrowIfCancellationRequested();
            return Ready();
        }
        bool Move(int x, int y)
        {
            var next = (X: window.Left + x, Y: window.Top + y);
            if (!Ready() || !Owns(window.Handle, next.X, next.Y)) return false;
            r.InputDeliveryStarted?.Invoke();
            if (xdo_move_mouse_relative_to_window(_xdo, window.Handle, x, y) != 0) return false;
            XSync(_display, false);
            pointer = next;
            return Ready() && Owns(window.Handle, next.X, next.Y);
        }
        bool Button(int button)
        {
            if (!Ready() || !Owns(window.Handle, pointer.X, pointer.Y)) return false;
            r.InputDeliveryStarted?.Invoke();
            var down = false;
            var up = false;
            try { down = xdo_mouse_down(_xdo, 0, button) == 0; }
            finally { up = xdo_mouse_up(_xdo, 0, button) == 0; XSync(_display, false); }
            return down && up;
        }
        const string stopped = "Refused [delivery-interrupted]: identity, focus, geometry, permission or pointer changed; further input stopped.";
        if (r.Action is "move" or "click" or "double_click" or "drag" or "scroll")
        {
            if (!Move(r.X, r.Y)) return stopped;
            if (r.Action == "move") return null;
            if (r.Action == "drag")
            {
                var released = false;
                string? refusal = null;
                try
                {
                    if (!Ready()) return stopped;
                    r.InputDeliveryStarted?.Invoke();
                    if (xdo_mouse_down(_xdo, 0, 1) != 0) refusal = stopped;
                    else
                    {
                        var steps = Math.Clamp(r.DurationMs / 20, 5, 150);
                        for (var i = 1; i <= steps; i++)
                            if (!Wait(r.DurationMs / steps) || !Move(r.X + (r.EndX - r.X) * i / steps, r.Y + (r.EndY - r.Y) * i / steps))
                            { refusal = stopped; break; }
                    }
                }
                finally { released = xdo_mouse_up(_xdo, 0, 1) == 0; XSync(_display, false); }
                return released ? refusal : "Refused: X11 mouse release failed; observe before continuing.";
            }
            if (r.Action == "scroll")
            {
                foreach (var (amount, positive, negative) in new[] { (r.Scroll, 4, 5), (r.HorizontalScroll, 6, 7) })
                    for (var i = 0; i < Math.Abs(amount); i++)
                        if (!Button(amount > 0 ? positive : negative) || !Wait(15)) return stopped;
                return null;
            }
            if (!Button(1)) return stopped;
            return r.Action == "double_click" && (!Wait(50) || !Button(1)) ? stopped : null;
        }
        if (r.Action == "type")
        {
            foreach (var rune in TextEol.ToLf(r.Text!).EnumerateRunes())
            {
                if (!Ready()) return stopped;
                r.InputDeliveryStarted?.Invoke();
                var result = rune.Value is 10 or 9
                    ? Key(rune.Value == 10 ? "Return" : "Tab", Ready, r)
                    : xdo_enter_text_window(_xdo, 0, rune.ToString(), 12000) == 0;
                XSync(_display, false);
                if (!result || !Wait(5)) return stopped;
            }
            return null;
        }
        if (r.Action == "press_key")
        {
            var names = r.Modifiers.Select(m => m switch { "control" => "Control_L", "alt" => "Alt_L", _ => "Shift_L" })
                .Append(KeyName(r.Key!)).ToArray();
            var held = new List<string>();
            var releaseOk = true;
            string? refusal = null;
            try
            {
                foreach (var name in names)
                {
                    if (!Ready()) { refusal = stopped; break; }
                    held.Add(name);
                    r.InputDeliveryStarted?.Invoke();
                    if (xdo_send_keysequence_window_down(_xdo, 0, name, 12000) != 0) { refusal = stopped; break; }
                }
            }
            finally
            {
                foreach (var name in held.AsEnumerable().Reverse())
                    releaseOk &= xdo_send_keysequence_window_up(_xdo, 0, name, 12000) == 0;
                XSync(_display, false);
            }
            return releaseOk ? refusal : "Refused: X11 key release failed; observe before continuing.";
        }
        return "Refused [unsupported]: unknown X11 input action.";
    }
    private bool Key(string name, Func<bool> ready, ComputerActionRequest r)
    {
        if (!ready()) return false;
        r.InputDeliveryStarted?.Invoke();
        var down = false; var up = false;
        try { down = xdo_send_keysequence_window_down(_xdo, 0, name, 12000) == 0; }
        finally { up = xdo_send_keysequence_window_up(_xdo, 0, name, 12000) == 0; }
        return down && up;
    }
    internal static string KeyName(string key) => key.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => "Return", "escape" or "esc" => "Escape", "tab" => "Tab",
        "left" => "Left", "right" => "Right", "up" => "Up", "down" => "Down", "backspace" => "BackSpace",
        "delete" => "Delete", "space" => "space", "home" => "Home", "end" => "End", "pageup" => "Prior",
        "pagedown" => "Next", var value when value.Length == 1 && char.IsAsciiLetterOrDigit(value[0]) => value, _ => "",
    };
    public void Dispose() => xdo_free(_xdo);
    [DllImport(Xdo)] private static extern nint xdo_new(string? display);
    [DllImport(Xdo)] private static extern void xdo_free(nint xdo);
    [DllImport(Xdo)] private static extern int xdo_get_active_window(nint xdo, out nint window);
    [DllImport(Xdo)] private static extern int xdo_activate_window(nint xdo, nint window);
    [DllImport(Xdo)] private static extern int xdo_get_mouse_location(nint xdo, out int x, out int y, out int screen);
    [DllImport(Xdo)] private static extern int xdo_move_mouse_relative_to_window(nint xdo, nint window, int x, int y);
    [DllImport(Xdo)] private static extern int xdo_mouse_down(nint xdo, nint window, int button);
    [DllImport(Xdo)] private static extern int xdo_mouse_up(nint xdo, nint window, int button);
    [DllImport(Xdo)] private static extern int xdo_enter_text_window(nint xdo, nint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint delay);
    [DllImport(Xdo)] private static extern int xdo_send_keysequence_window_down(nint xdo, nint window, string key, uint delay);
    [DllImport(Xdo)] private static extern int xdo_send_keysequence_window_up(nint xdo, nint window, string key, uint delay);
    [DllImport(X11)] private static extern int XQueryKeymap(nint display, [Out] byte[] keys);
    [DllImport(X11)] private static extern nint XDefaultRootWindow(nint display);
    [DllImport(X11)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool XQueryPointer(nint display, nint window,
        out nint root, out nint child, out int rootX, out int rootY, out int x, out int y, out uint mask);
    [DllImport(X11)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool XTranslateCoordinates(nint display, nint source, nint destination,
        int sourceX, int sourceY, out int x, out int y, out nint child);
    [DllImport(X11)] private static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
