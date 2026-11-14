using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        private CFRange? _selection;
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
            if (state is null) return null;
            if (request.Action == "press_key" && state.SelectionLength is not > 0) return null;
            var verification = new AccessibilityInputVerification(element, window, request, state);
            if (request.Action == "drag")
            {
                verification._selection = verification.ReadSelection();
                if (verification._selection is null) { verification.Dispose(); return null; }
            }
            if (verification.Verify() is not null) { verification.Dispose(); return null; }
            return verification;
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
                    current = ResolveInputElement(app, _window, _request);
                    if (current == 0 || !CFEqual(current, _element) || AXUIElementGetPid(current, out var pid) != 0
                        || pid != _window.ProcessId) return "The input target or focused control changed.";
                    if (_request.Action == "drag"
                        && (AXUIElementCopyElementAtPosition(app, _window.Left + _request.EndX, _window.Top + _request.EndY, out end) != 0
                            || end == 0 || !CFEqual(end, _element))) return "The drag endpoint changed control.";
                    if (UnsafeContext(current)) return "The control's sensitivity or actionable context changed.";
                    if (_request.Action == "drag" && ReadSelection() != _selection)
                        return "The observed text endpoints or selection capability changed.";
                    axWindow = AxAttribute(current, "AXWindow");
                    if (axWindow == 0 || !MatchesAccessibilityWindow(axWindow, _window)) return "The control's window changed.";
                    var state = ReadState(current, _request.Action != "scroll");
                    if (state is null) return "The observed control's state is no longer accessible.";
                    if (state.Metadata != _state.Metadata) return "The observed control's role or metadata changed.";
                    if (state.X != _state.X || state.Y != _state.Y || state.Width != _state.Width || state.Height != _state.Height)
                        return "The observed control's geometry changed.";
                    if (state.ValueHash != _state.ValueHash) return "The observed control's text changed.";
                    return state.SelectionStart == _state.SelectionStart && state.SelectionLength == _state.SelectionLength
                        ? null : "The observed text selection changed.";
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

        private CFRange? ReadSelection()
        {
            bool Settable(string name)
            {
                var attribute = Key(name);
                try { return AXUIElementIsAttributeSettable(_element, attribute, out var settable) == 0 && settable; }
                finally { CFRelease(attribute); }
            }
            if (!Settable("AXSelectedTextRange") || !Settable("AXFocused")) return null;
            CFRange? At(int x, int y)
            {
                var point = new CGPoint(_window.Left + x, _window.Top + y);
                var parameter = AXPointValueCreate(1, ref point);
                var name = Key("AXRangeForPosition");
                nint value = 0;
                try
                {
                    if (parameter == 0 || AXUIElementCopyParameterizedAttributeValue(_element, name, parameter, out value) != 0
                        || value == 0 || !AXRangeValue(value, 4, out var range) || range.Location < 0) return null;
                    return range;
                }
                finally
                {
                    if (value != 0) CFRelease(value);
                    CFRelease(name);
                    if (parameter != 0) CFRelease(parameter);
                }
            }
            // Ask AX for character positions and set a text range, never dispatch an ordinary drag that
            // could move existing selected text or start drag-and-drop. Unsupported text stays confirmable.
            var from = At(_request.X, _request.Y);
            var to = At(_request.EndX, _request.EndY);
            if (from is null || to is null) return null;
            var start = Math.Min(from.Value.Location, to.Value.Location);
            var end = Math.Max(from.Value.Location, to.Value.Location);
            var text = AxAttribute(_element, "AXValue");
            try
            {
                if (text == 0 || CFGetTypeID(text) != CFStringGetTypeID() || end > CFStringGetLength(text)) return null;
                return new CFRange((nint)start, (nint)(end - start));
            }
            finally { if (text != 0) CFRelease(text); }
        }

        internal string? SelectText(Func<bool> authorized, Action? started)
        {
            lock (_gate)
            {
                if (_selection is not { } range || Verify() is { } || !authorized())
                    return "Refused: the verified text selection target changed before delivery.";
                var value = AXRangeValueCreate(4, ref range);
                var attribute = Key("AXSelectedTextRange");
                try
                {
                    if (value == 0) return "Refused: macOS could not create the text selection range.";
                    var focus = Key("AXFocused");
                    var foundation = NativeLibrary.Load(CoreFoundation);
                    try
                    {
                        var trueValue = Marshal.ReadIntPtr(NativeLibrary.GetExport(foundation, "kCFBooleanTrue"));
                        started?.Invoke();
                        if (AXUIElementSetAttributeValue(_element, focus, trueValue) != 0)
                            return "Refused: macOS rejected text focus. No click or drag was attempted.";
                    }
                    finally { NativeLibrary.Free(foundation); CFRelease(focus); }
                    if (!authorized()) return "Refused: permission or focus changed before setting the text range.";
                    var app = AXUIElementCreateApplication(_window.ProcessId);
                    var focused = app == 0 ? 0 : AxAttribute(app, "AXFocusedUIElement");
                    try
                    {
                        if (focused == 0 || !CFEqual(focused, _element))
                            return "Refused: macOS did not focus the verified text control. No text range was set.";
                    }
                    finally { if (focused != 0) CFRelease(focused); if (app != 0) CFRelease(app); }
                    if (AXUIElementSetAttributeValue(_element, attribute, value) != 0)
                        return "Refused: macOS rejected text selection. No mouse drag was attempted.";
                    return authorized() ? null : "Refused: permission or focus changed after text selection. Further input stopped.";
                }
                finally { CFRelease(attribute); if (value != 0) CFRelease(value); }
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
                    var characters = new ushort[(int)count];
                    CFStringGetCharacters(value, new CFRange(0, count), characters);
                    hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(characters.AsSpan())));
                    range = AxAttribute(element, "AXSelectedTextRange");
                    if (range != 0 && AXRangeValue(range, 4, out var selection))
                    {
                        if (selection.Location < 0 || selection.Length < 0 || selection.Location > count
                            || selection.Length > count - selection.Location) return null;
                        start = selection.Location; length = selection.Length;
                    }
                    else return null;
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

    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyParameterizedAttributeValue(nint element, nint attribute, nint parameter, out nint value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementIsAttributeSettable(nint element, nint attribute, [MarshalAs(UnmanagedType.I1)] out bool settable);
    [DllImport(ApplicationServices)] private static extern int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);
    [DllImport(ApplicationServices, EntryPoint = "AXValueCreate")] private static extern nint AXPointValueCreate(int type, ref CGPoint point);
    [DllImport(ApplicationServices, EntryPoint = "AXValueCreate")] private static extern nint AXRangeValueCreate(int type, ref CFRange range);

    [StructLayout(LayoutKind.Sequential)] private readonly record struct CFRange(nint Location, nint Length);
    [DllImport(CoreFoundation)] private static extern nint CFRetain(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(nint value);
    [DllImport(CoreFoundation)] private static extern void CFStringGetCharacters(nint value, CFRange range, [Out] ushort[] characters);
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXRangeValue(nint value, int type, out CFRange range);
}
