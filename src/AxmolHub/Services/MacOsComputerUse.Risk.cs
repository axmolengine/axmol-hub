using System.Runtime.InteropServices;
using AxmolHub.Core;

namespace AxmolHub;

internal static partial class MacOsComputerUse
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private static readonly object ClipboardGate = new();
    private static long _copiedPermission;
    private static int _copiedRevision = -1;

    // Only a verified local editor adapter can authorize edits from role information alone. Generic text
    // fields may be message composers, terminal commands or credentials; unknown controls stay confirmable.
    private static bool IsLocalEditor(CapturableWindow window)
        => window.Owner?.Application.ExecutablePath == "/System/Applications/TextEdit.app/Contents/MacOS/TextEdit";

    private static ComputerRiskDecision AssessRisk(CapturableWindow window, ComputerActionRequest request)
    {
        if (ComputerInput.Validate(request) is { } invalid) return new(ComputerRisk.Deny, invalid);
        if (request.Action == "move") return new(ComputerRisk.Allow, "Pointer motion within the selected application window.");
        if (!AXIsProcessTrusted()) return ComputerRiskDecision.Unknown;
        var app = AXUIElementCreateApplication(window.ProcessId);
        if (app == 0) return ComputerRiskDecision.Unknown;
        nint element = 0;
        try
        {
            AXUIElementSetMessagingTimeout(app, 0.25f);
            if (request.Action is "click" or "double_click" or "drag" or "scroll")
            {
                if (AXUIElementCopyElementAtPosition(app, window.Left + request.X, window.Top + request.Y, out element) != 0)
                    return ComputerRiskDecision.Unknown;
            }
            else element = AxAttribute(app, "AXFocusedUIElement");
            if (element == 0 || AXUIElementGetPid(element, out var pid) != 0 || pid != window.ProcessId)
                return ComputerRiskDecision.Unknown;
            var role = AxText(element, "AXRole");
            var subrole = AxText(element, "AXSubrole");
            var label = string.Join(" ", new[] { "AXTitle", "AXDescription", "AXHelp", "AXIdentifier" }.Select(k => AxText(element, k)));
            if (subrole == "AXSecureTextField" || SensitiveLabel(label))
                return new(ComputerRisk.RequireConfirmation, "Sensitive control or consequential action detected by the host. Review before delivery.");
            // Role inspection must not silently expand permission to the app's other window.
            var axWindow = AxAttribute(element, "AXWindow");
            if (axWindow == 0) return ComputerRiskDecision.Unknown;
            try
            {
                if (!MatchesAccessibilityWindow(axWindow, window)) return ComputerRiskDecision.Unknown;
            }
            finally { CFRelease(axWindow); }
            if (request.Action == "scroll" && HasAxAncestor(element, "AXScrollArea"))
                return new(ComputerRisk.Allow, "Scrolling a verified accessibility scroll area.");
            if (request.Action == "press_key" && request.Modifiers.SequenceEqual(new[] { "command" })
                && string.Equals(request.Key, "c", StringComparison.OrdinalIgnoreCase)
                && role is "AXTextArea" or "AXTextField" or "AXStaticText")
                return new(ComputerRisk.Allow, "Copy from the authorized application's text control; clipboard contents are not read.");
            if (!IsLocalEditor(window) || role != "AXTextArea" || HasAxAncestor(element, "AXSheet")
                || HasAxAncestor(element, "AXDialog"))
                return new(ComputerRisk.RequireConfirmation, $"Unclassified {role} control. Application permission is not consent to submit, delete, pay or disclose data.");
            if (request.Action == "drag")
            {
                nint end = 0;
                try
                {
                    if (AXUIElementCopyElementAtPosition(app, window.Left + request.EndX, window.Top + request.EndY, out end) != 0
                        || end == 0 || !CFEqual(element, end)) return ComputerRiskDecision.Unknown;
                }
                finally { if (end != 0) CFRelease(end); }
            }
            if (request.Action == "type" && request.Text!.Any(c => char.IsControl(c) && c != '\n' && c != '\t'))
                return ComputerRiskDecision.Unknown;
            if (request.Action == "press_key")
            {
                var key = (request.Key ?? "").Trim().ToUpperInvariant();
                if (request.Modifiers.Length > 0)
                {
                    if (!request.Modifiers.SequenceEqual(new[] { "command" }) || key is not ("A" or "C" or "V"))
                        return ComputerRiskDecision.Unknown;
                    if (key == "V")
                    {
                        lock (ClipboardGate)
                            if (request.PermissionVersion == 0 || _copiedPermission != request.PermissionVersion
                                || _copiedRevision != ClipboardRevision())
                                return new(ComputerRisk.RequireConfirmation, "Clipboard provenance is unknown or changed. Explicitly approve this paste; no clipboard content was read.");
                    }
                }
                else if (!TryKeyCode(key, out _)) return ComputerRiskDecision.Unknown;
            }
            return new(ComputerRisk.Allow, "Verified local TextEdit document text interaction; no dialogs, submit or application command authorized.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException) { return ComputerRiskDecision.Unknown; }
        finally { if (element != 0) CFRelease(element); CFRelease(app); }
    }

    private static bool MatchesAccessibilityWindow(nint element, CapturableWindow window)
    {
        var position = AxAttribute(element, "AXPosition");
        var size = AxAttribute(element, "AXSize");
        try
        {
            return position != 0 && size != 0 && AxText(element, "AXTitle") == window.Title
                && AXValueGetValue(position, 1, out var p) && AXSizeValue(size, 2, out var s)
                && Math.Abs(p.X - window.Left) < 1 && Math.Abs(p.Y - window.Top) < 1
                && Math.Abs(s.Width - window.Width) < 1 && Math.Abs(s.Height - window.Height) < 1;
        }
        finally { if (position != 0) CFRelease(position); if (size != 0) CFRelease(size); }
    }

    private static bool SensitiveLabel(string label)
        => new[] { "send", "submit", "pay", "purchase", "password", "credential", "upload", "delete", "remove", "发送", "提交", "付款", "支付", "密码", "上传", "删除" }
            .Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool HasAxAncestor(nint element, string role)
    {
        var current = AxAttribute(element, "AXParent");
        for (var depth = 0; current != 0 && depth < 12; depth++)
        {
            var next = nint.Zero;
            try
            {
                if (AxText(current, "AXRole") == role) return true;
                next = AxAttribute(current, "AXParent");
            }
            finally { CFRelease(current); }
            current = next;
        }
        if (current != 0) CFRelease(current);
        return false;
    }

    private static nint AxAttribute(nint element, string name)
    {
        var key = Key(name);
        try { return AXUIElementCopyAttributeValue(element, key, out var value) == 0 ? value : 0; }
        finally { CFRelease(key); }
    }

    private static string AxText(nint element, string name)
    {
        var value = AxAttribute(element, name);
        if (value == 0) return "";
        try
        {
            if (CFGetTypeID(value) != CFStringGetTypeID()) return "";
            var buffer = new byte[2048];
            if (!CFStringGetCString(value, buffer, buffer.Length, Utf8)) return "";
            var end = Array.IndexOf(buffer, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }
        finally { CFRelease(value); }
    }

    private static int ClipboardRevision()
    {
        var board = ObjectMessage(objc_getClass("NSPasteboard"), sel_registerName("generalPasteboard"));
        return board == 0 ? -1 : IntMessage(board, sel_registerName("changeCount"));
    }

    internal static void ForgetClipboard(long permissionVersion)
    {
        lock (ClipboardGate)
            if (_copiedPermission == permissionVersion) { _copiedPermission = 0; _copiedRevision = -1; }
    }

    private static void RecordCopy(ComputerActionRequest request, int before)
    {
        if (request.Action != "press_key" || !request.Modifiers.SequenceEqual(new[] { "command" })
            || !string.Equals(request.Key, "c", StringComparison.OrdinalIgnoreCase)) return;
        // A delivered shortcut alone does not prove that the clipboard changed. Never infer ownership from it.
        var after = ClipboardRevision();
        lock (ClipboardGate)
        {
            _copiedPermission = after != before && after >= 0 ? request.PermissionVersion : 0;
            _copiedRevision = after;
        }
    }

    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] private static extern nint AXUIElementCreateApplication(int pid);
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyElementAtPosition(nint element, float x, float y, out nint result);
    [DllImport(ApplicationServices)] private static extern int AXUIElementSetMessagingTimeout(nint element, float seconds);
    [DllImport(ApplicationServices)] private static extern int AXUIElementGetPid(nint element, out int pid);
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetValue(nint value, int type, out CGPoint point);
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXSizeValue(nint value, int type, out CGSize size);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFEqual(nint a, nint b);
}
