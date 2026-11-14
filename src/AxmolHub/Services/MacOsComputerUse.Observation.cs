using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub;

internal static partial class MacOsComputerUse
{
    /// <summary>Ordinary input follows the same AX control, geometry, text and selection, not blinking pixels.</summary>
    private sealed class AccessibilityInputVerification : IComputerInputVerification
    {
        private readonly object _gate = new();
        private nint _element;
        private readonly CapturableWindow _window;
        private readonly ComputerActionRequest _request;
        private readonly ControlState _state;
        private sealed record ControlState(string Metadata, double X, double Y, double Width, double Height,
            string? ValueHash, long? SelectionStart, long? SelectionLength);

        private AccessibilityInputVerification(nint element, CapturableWindow window, ComputerActionRequest request,
            ControlState state)
        {
            _element = CFRetain(element);
            _window = window;
            _request = request;
            _state = state;
        }

        internal static IComputerInputVerification? Create(nint element, CapturableWindow window, ComputerActionRequest request)
        {
            var state = ReadState(element, request.Action != "scroll");
            return state is null ? null : new AccessibilityInputVerification(element, window, request, state);
        }

        public string? Verify()
        {
            lock (_gate)
            {
                if (_element == 0) return "The control observation was released.";
                var app = AXUIElementCreateApplication(_window.ProcessId);
                nint current = 0, end = 0, axWindow = 0;
                try
                {
                    if (app == 0) return "The application's accessibility tree is unavailable.";
                    AXUIElementSetMessagingTimeout(app, 0.25f);
                    if (_request.Action is "click" or "double_click" or "drag" or "scroll")
                    {
                        if (AXUIElementCopyElementAtPosition(app, _window.Left + _request.X, _window.Top + _request.Y, out current) != 0)
                            return "The observed pointer target is unavailable.";
                    }
                    else current = AxAttribute(app, "AXFocusedUIElement");
                    if (current == 0 || !CFEqual(current, _element) || AXUIElementGetPid(current, out var pid) != 0
                        || pid != _window.ProcessId) return "The input target or focused control changed.";
                    if (_request.Action == "drag"
                        && (AXUIElementCopyElementAtPosition(app, _window.Left + _request.EndX, _window.Top + _request.EndY, out end) != 0
                            || end == 0 || !CFEqual(end, _element))) return "The drag endpoint changed control.";
                    axWindow = AxAttribute(current, "AXWindow");
                    if (axWindow == 0 || !MatchesAccessibilityWindow(axWindow, _window)) return "The control's window changed.";
                    return ReadState(current, _request.Action != "scroll") == _state
                        ? null : "The observed control's geometry, metadata, text or selection changed.";
                }
                catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
                { return "The control observation could not be verified."; }
                finally
                {
                    if (axWindow != 0) CFRelease(axWindow);
                    if (end != 0) CFRelease(end);
                    if (current != 0) CFRelease(current);
                    if (app != 0) CFRelease(app);
                }
            }
        }

        private static ControlState? ReadState(nint element, bool text)
        {
            var position = AxAttribute(element, "AXPosition");
            var size = AxAttribute(element, "AXSize");
            var value = nint.Zero;
            var range = nint.Zero;
            try
            {
                if (position == 0 || size == 0 || !AXValueGetValue(position, 1, out var p)
                    || !AXSizeValue(size, 2, out var s)) return null;
                var metadata = string.Join("\u001f", new[] { "AXRole", "AXSubrole", "AXTitle", "AXDescription", "AXHelp", "AXIdentifier" }
                    .Select(name => AxText(element, name)));
                string? hash = null;
                long? start = null, length = null;
                if (text)
                {
                    value = AxAttribute(element, "AXValue");
                    if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) return null;
                    var count = CFStringGetLength(value);
                    // Bound host reads and retain only a digest; unavailable/large controls remain confirmable.
                    if (count < 0 || count > 65536) return null;
                    var characters = new char[(int)count];
                    CFStringGetCharacters(value, new CFRange(0, count), characters);
                    hash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(characters)));
                    range = AxAttribute(element, "AXSelectedTextRange");
                    if (range != 0 && AXRangeValue(range, 4, out var selection))
                    { start = selection.Location; length = selection.Length; }
                    else if (AxText(element, "AXRole") is "AXTextArea" or "AXTextField") return null;
                }
                return new(metadata, p.X, p.Y, s.Width, s.Height, hash, start, length);
            }
            finally
            {
                if (range != 0) CFRelease(range);
                if (value != 0) CFRelease(value);
                if (position != 0) CFRelease(position);
                if (size != 0) CFRelease(size);
            }
        }

        public void Dispose()
        {
            lock (_gate) { if (_element != 0) CFRelease(_element); _element = 0; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CFRange(nint location, nint length)
    { public nint Location = location; public nint Length = length; }
    [DllImport(CoreFoundation)] private static extern nint CFRetain(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(nint value);
    [DllImport(CoreFoundation)] private static extern void CFStringGetCharacters(nint value, CFRange range, [Out] char[] characters);
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXRangeValue(nint value, int type, out CFRange range);
}
