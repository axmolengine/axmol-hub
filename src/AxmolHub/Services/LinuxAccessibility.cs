using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Bounded AT-SPI evidence. Provider failures never establish browsing permission.</summary>
internal static class LinuxAccessibility
{
    private const string Lib = "libatspi.so.0", Glib = "libglib-2.0.so.0", Object = "libgobject-2.0.so.0";
    private static readonly object Gate = new();
    private static bool _initialized;
    private sealed class Sensitive : InvalidOperationException;
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(int X, int Y, int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Range(int Start, int End);
    internal sealed record Snapshot(string Role, string Name, Rect Bounds, string? Hash, Range Selection, int Caret);
    private static readonly string[] SensitiveWords = ["send", "submit", "pay", "purchase", "password", "credential", "upload", "delete", "remove", "发送", "提交", "付款", "支付", "密码", "上传", "删除"];

    internal static ComputerRiskDecision Assess(CapturableWindow window, ComputerActionRequest request)
    {
        lock (Gate)
        {
            nint selected = 0;
            try
            {
                if (!_initialized)
                {
                    if (atspi_init() != 0) throw new InvalidOperationException();
                    atspi_set_timeout(300, 300);
                    _initialized = true;
                }
                selected = Resolve(window, request);
                var snapshot = Read(selected, window);
                var capability = request.Action switch
                {
                    "scroll" when snapshot.Role == "scroll pane" => ComputerCapabilities.Scroll,
                    "drag" when snapshot.Hash is not null && snapshot.Role != "scroll pane" => ComputerCapabilities.SelectText,
                    "press_key" when request.Modifiers.SequenceEqual(new[] { "control" })
                        && string.Equals(request.Key, "c", StringComparison.OrdinalIgnoreCase)
                        && snapshot.Hash is not null && snapshot.Selection.End > snapshot.Selection.Start => ComputerCapabilities.CopyText,
                    _ => ComputerCapabilities.None,
                };
                var verification = new Verification(selected, window, request, snapshot, capability);
                if (capability == ComputerCapabilities.SelectText && !verification.CanSelect())
                { verification.Dispose(); selected = 0; return ComputerRiskDecision.Unknown; }
                selected = 0;
                if (capability == ComputerCapabilities.None && (request.Capabilities & ComputerCapabilities.Interact) != 0)
                    capability = ComputerCapabilities.Interact;
                return new(capability == ComputerCapabilities.None ? ComputerRisk.RequireConfirmation : ComputerRisk.Allow,
                    "AT-SPI target identity, geometry, metadata and bounded text/selection verification.")
                    { Capability = capability, Verification = verification };
            }
            catch (Sensitive) { return new(ComputerRisk.RequireConfirmation, "Recognized sensitive, actionable or modal AT-SPI target; confirm this action separately."); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            { return ComputerRiskDecision.Unknown; }
            finally { Unref(selected); }
        }
    }

    private static nint Resolve(CapturableWindow window, ComputerActionRequest request)
    {
        var desktop = atspi_get_desktop(0);
        if (desktop == 0) throw new InvalidOperationException();
        nint root = 0;
        try
        {
            var count = Count(desktop);
            for (var i = 0; i < count; i++)
            {
                var app = Child(desktop, i);
                try
                {
                    if (Pid(app) != window.ProcessId) continue;
                    var windows = Count(app);
                    for (var j = 0; j < windows; j++)
                    {
                        var candidate = Child(app, j);
                        if (Name(candidate) == window.Title && Bounds(candidate) == new Rect(window.Left, window.Top, window.Width, window.Height))
                        {
                            if (root != 0) { Unref(candidate); throw new InvalidOperationException(); }
                            root = candidate;
                        }
                        else Unref(candidate);
                    }
                }
                finally { Unref(app); }
            }
            if (root == 0) throw new InvalidOperationException();
            var pointer = request.Action is "click" or "double_click" or "drag" or "scroll";
            if (pointer)
            {
                var hit = Hit(root, window.Left + request.X, window.Top + request.Y);
                try
                {
                    Context(hit, window);
                    if (request.Action == "scroll")
                    {
                        for (var depth = 0; depth < 20; depth++)
                        {
                            if (Role(hit) == "scroll pane") { var answer = hit; hit = 0; return answer; }
                            if (hit == root) break;
                            var parent = Parent(hit); Unref(hit); hit = parent;
                            if (hit == 0) break;
                        }
                        throw new InvalidOperationException();
                    }
                    var result = hit; hit = 0; return result;
                }
                finally { Unref(hit); }
            }
            var queue = new Queue<nint>();
            queue.Enqueue(g_object_ref(root));
            nint focused = 0;
            try
            {
                var visited = 0;
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    try
                    {
                        if (++visited > 128) throw new InvalidOperationException();
                        atspi_accessible_clear_cache(item);
                        var states = atspi_accessible_get_state_set(item);
                        try
                        {
                            if (states != 0 && atspi_state_set_contains(states, 12))
                            {
                                if (focused != 0) throw new InvalidOperationException();
                                focused = g_object_ref(item);
                            }
                        }
                        finally { Unref(states); }
                        var children = Count(item);
                        if (children + queue.Count + visited > 128) throw new InvalidOperationException();
                        for (var i = 0; i < children; i++) queue.Enqueue(Child(item, i));
                    }
                    finally { Unref(item); }
                }
                if (focused == 0) throw new InvalidOperationException();
                var answer = focused; focused = 0; return answer;
            }
            finally { Unref(focused); foreach (var item in queue) Unref(item); }
        }
        finally { Unref(root); Unref(desktop); }
    }
    private static void Context(nint element, CapturableWindow window)
    {
        var current = g_object_ref(element);
        try
        {
            for (var depth = 0; current != 0 && depth < 24; depth++)
            {
                atspi_accessible_clear_cache(current);
                if (Pid(current) != window.ProcessId) throw new InvalidOperationException();
                var role = Role(current);
                var label = Name(current);
                if (role is "password text" or "push button" or "push button menu" or "menu item" or "link" or "dialog" or "alert"
                    || SensitiveWords.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase))) throw new Sensitive();
                var states = atspi_accessible_get_state_set(current);
                try { if (states == 0 || atspi_state_set_contains(states, 6)) throw new InvalidOperationException();
                    if (atspi_state_set_contains(states, 16)) throw new Sensitive(); }
                finally { Unref(states); }
                if (role is "frame" or "window")
                {
                    if (label != window.Title || Bounds(current) != new Rect(window.Left, window.Top, window.Width, window.Height))
                        throw new InvalidOperationException();
                    return;
                }
                var parent = Parent(current); Unref(current); current = parent;
            }
            throw new InvalidOperationException();
        }
        finally { Unref(current); }
    }
    private static Snapshot Read(nint element, CapturableWindow window)
    {
        Context(element, window);
        var role = Role(element);
        string? hash = null; var range = new Range(-1, -1); var caret = -1;
        if (role is "text" or "entry" or "paragraph" or "document text" or "terminal")
        {
            var text = atspi_accessible_get_text_iface(element);
            try
            {
                if (text != 0)
                {
                    var count = atspi_text_get_character_count(text, out var error); Check(error);
                    if (count is < 0 or > 65536) throw new InvalidOperationException();
                    var content = atspi_text_get_text(text, 0, count, out error); Check(error);
                    var value = String(content);
                    if (value.Length > 131072) throw new InvalidOperationException();
                    hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
                    caret = atspi_text_get_caret_offset(text, out error); Check(error);
                    var selections = atspi_text_get_n_selections(text, out error); Check(error);
                    if (selections > 1) throw new InvalidOperationException();
                    if (selections == 1)
                    {
                        var ptr = atspi_text_get_selection(text, 0, out error); Check(error);
                        if (ptr == 0) throw new InvalidOperationException();
                        try { range = Marshal.PtrToStructure<Range>(ptr); } finally { g_free(ptr); }
                        if (range.Start < 0 || range.End < range.Start || range.End > count) throw new InvalidOperationException();
                    }
                }
            }
            finally { Unref(text); }
        }
        if (role == "scroll pane")
        {
            var state = new StringBuilder();
            var count = Count(element);
            if (count > 32) throw new InvalidOperationException();
            for (var i = 0; i < count; i++)
            {
                var child = Child(element, i);
                try
                {
                    atspi_accessible_clear_cache(child);
                    state.Append(Role(child)).Append('|').Append(Bounds(child)).Append('|');
                    var value = atspi_accessible_get_value_iface(child);
                    if (value != 0)
                    {
                        try { state.Append(atspi_value_get_current_value(value, out var error).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); Check(error); }
                        finally { Unref(value); }
                    }
                }
                finally { Unref(child); }
            }
            hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
        }
        return new(role, Name(element), Bounds(element), hash, range, caret);
    }

    internal sealed class Verification(nint element, CapturableWindow window, ComputerActionRequest request,
        Snapshot snapshot, ComputerCapabilities capability) : IComputerInputVerification
    {
        private nint _element = element;
        private Range _selection;
        internal ComputerCapabilities Capability => capability;
        public string? Verify()
        {
            lock (Gate)
            {
                try { return _element != 0 && Read(_element, window) == snapshot ? null : "AT-SPI target, metadata, geometry, text or selection changed."; }
                catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
                { return "AT-SPI target verification is unavailable. Observe again."; }
            }
        }
        internal bool KeyboardFocused()
        {
            lock (Gate)
            {
                atspi_accessible_clear_cache(_element);
                var states = atspi_accessible_get_state_set(_element);
                try { return states != 0 && atspi_state_set_contains(states, 12); }
                finally { Unref(states); }
            }
        }
        internal bool CanSelect()
        {
            var first = Hit(_element, window.Left + request.X, window.Top + request.Y);
            var last = Hit(_element, window.Left + request.EndX, window.Top + request.EndY);
            var text = atspi_accessible_get_text_iface(_element);
            try
            {
                if (first != _element || last != _element || text == 0) return false;
                var from = atspi_text_get_offset_at_point(text, window.Left + request.X, window.Top + request.Y, 0, out var error); Check(error);
                var to = atspi_text_get_offset_at_point(text, window.Left + request.EndX, window.Top + request.EndY, 0, out error); Check(error);
                _selection = new(Math.Min(from, to), Math.Max(from, to));
                return from >= 0 && to >= 0 && from != to;
            }
            finally { Unref(first); Unref(last); Unref(text); }
        }
        internal string? Select(Func<bool> authorized, Action? started)
        {
            lock (Gate)
            {
                if (!authorized() || Verify() is not null) return "Refused: AT-SPI selection target changed.";
                var text = atspi_accessible_get_text_iface(_element);
                try
                {
                    if (text == 0) return "Refused: AT-SPI text selection unavailable.";
                    if (!authorized()) return "Refused: selection permission changed.";
                    started?.Invoke();
                    var ok = snapshot.Selection.Start >= 0
                        ? atspi_text_set_selection(text, 0, _selection.Start, _selection.End, out var error)
                        : atspi_text_add_selection(text, _selection.Start, _selection.End, out error);
                    Check(error);
                    return ok ? null : "Refused: AT-SPI text selection was not acknowledged.";
                }
                catch (InvalidOperationException) { return "Refused: AT-SPI selection delivery failed. Observe again."; }
                finally { Unref(text); }
            }
        }
        public void Dispose() { lock (Gate) { Unref(_element); _element = 0; } }
    }
    private static nint Hit(nint element, int x, int y)
    {
        var current = g_object_ref(element);
        try
        {
            for (var depth = 0; depth < 24; depth++)
            {
                var component = atspi_accessible_get_component_iface(current);
                if (component == 0) throw new InvalidOperationException();
                nint next;
                try { next = atspi_component_get_accessible_at_point(component, x, y, 0, out var error); Check(error); }
                finally { Unref(component); }
                if (next == 0 || next == current)
                { Unref(next); var answer = current; current = 0; return answer; }
                Unref(current); current = next;
            }
            throw new InvalidOperationException();
        }
        finally { Unref(current); }
    }
    private static Rect Bounds(nint obj)
    {
        var component = atspi_accessible_get_component_iface(obj);
        if (component == 0) throw new InvalidOperationException();
        try
        {
            var ptr = atspi_component_get_extents(component, 0, out var error); Check(error);
            if (ptr == 0) throw new InvalidOperationException();
            try { return Marshal.PtrToStructure<Rect>(ptr); } finally { g_free(ptr); }
        }
        finally { Unref(component); }
    }
    private static int Count(nint obj) { var n = atspi_accessible_get_child_count(obj, out var e); Check(e); return n is >= 0 and <= 128 ? n : throw new InvalidOperationException(); }
    private static nint Child(nint obj, int i) { var n = atspi_accessible_get_child_at_index(obj, i, out var e); Check(e); return n != 0 ? n : throw new InvalidOperationException(); }
    private static nint Parent(nint obj) { var n = atspi_accessible_get_parent(obj, out var e); Check(e); return n; }
    private static uint Pid(nint obj) { var n = atspi_accessible_get_process_id(obj, out var e); Check(e); return n; }
    private static string Name(nint obj) { var n = atspi_accessible_get_name(obj, out var e); Check(e); return String(n); }
    private static string Role(nint obj) { var n = atspi_accessible_get_role_name(obj, out var e); Check(e); return String(n); }
    private static string String(nint value) { try { return Marshal.PtrToStringUTF8(value) ?? ""; } finally { g_free(value); } }
    private static void Check(nint error) { if (error == 0) return; g_error_free(error); throw new InvalidOperationException(); }
    private static void Unref(nint obj) { if (obj != 0) g_object_unref(obj); }
    [DllImport(Lib)] private static extern int atspi_init();
    [DllImport(Lib)] private static extern void atspi_set_timeout(int timeout, int startup);
    [DllImport(Lib)] private static extern nint atspi_get_desktop(int index);
    [DllImport(Lib)] private static extern void atspi_accessible_clear_cache(nint obj);
    [DllImport(Lib)] private static extern int atspi_accessible_get_child_count(nint obj, out nint error);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_child_at_index(nint obj, int index, out nint error);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_parent(nint obj, out nint error);
    [DllImport(Lib)] private static extern uint atspi_accessible_get_process_id(nint obj, out nint error);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_name(nint obj, out nint error);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_role_name(nint obj, out nint error);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_state_set(nint obj);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool atspi_state_set_contains(nint states, int state);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_component_iface(nint obj);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_text_iface(nint obj);
    [DllImport(Lib)] private static extern nint atspi_accessible_get_value_iface(nint obj);
    [DllImport(Lib)] private static extern double atspi_value_get_current_value(nint value, out nint error);
    [DllImport(Lib)] private static extern nint atspi_component_get_extents(nint component, int coords, out nint error);
    [DllImport(Lib)] private static extern nint atspi_component_get_accessible_at_point(nint component, int x, int y, int coords, out nint error);
    [DllImport(Lib)] private static extern int atspi_text_get_character_count(nint text, out nint error);
    [DllImport(Lib)] private static extern nint atspi_text_get_text(nint text, int start, int end, out nint error);
    [DllImport(Lib)] private static extern int atspi_text_get_caret_offset(nint text, out nint error);
    [DllImport(Lib)] private static extern int atspi_text_get_n_selections(nint text, out nint error);
    [DllImport(Lib)] private static extern nint atspi_text_get_selection(nint text, int selection, out nint error);
    [DllImport(Lib)] private static extern int atspi_text_get_offset_at_point(nint text, int x, int y, int coords, out nint error);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool atspi_text_set_selection(nint text, int selection, int start, int end, out nint error);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool atspi_text_add_selection(nint text, int start, int end, out nint error);
    [DllImport(Glib)] private static extern void g_free(nint value);
    [DllImport(Glib)] private static extern void g_error_free(nint error);
    [DllImport(Object)] private static extern nint g_object_ref(nint obj);
    [DllImport(Object)] private static extern void g_object_unref(nint obj);
}
