using System.Runtime.InteropServices;
using AxmolHub.Core;

namespace AxmolHub;

internal static partial class MacOsComputerUse
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private sealed class PointerVerification : IComputerInputVerification
    {
        public string? Verify() => null; // Delivery checks live geometry, focus, identity and pointer ownership.
        public void Dispose() { }
    }

    private static ComputerRiskDecision AssessRisk(CapturableWindow window, ComputerActionRequest request)
    {
        if (ComputerInput.Validate(request) is { } invalid) return new(ComputerRisk.Deny, invalid);
        if (request.Action == "move") return new(ComputerRisk.Allow, "Pointer motion within the selected application window.")
            { Verification = new PointerVerification(), Capability = ComputerCapabilities.MovePointer };
        if (!AXIsProcessTrusted()) return ComputerRiskDecision.Unknown;
        var app = AXUIElementCreateApplication(window.ProcessId);
        if (app == 0) return ComputerRiskDecision.Unknown;
        nint element = 0;
        try
        {
            AXUIElementSetMessagingTimeout(app, 0.25f);
            element = ResolveInputElement(app, window, request);
            if (element == 0 || AXUIElementGetPid(element, out var pid) != 0 || pid != window.ProcessId)
                return ComputerRiskDecision.Unknown;
            if (UnsafeContext(element))
                return new(ComputerRisk.RequireConfirmation, "Sensitive, actionable or modal control context. Review before delivery.");
            var axWindow = AxAttribute(element, "AXWindow");
            if (axWindow == 0) return ComputerRiskDecision.Unknown;
            try { if (!MatchesAccessibilityWindow(axWindow, window)) return ComputerRiskDecision.Unknown; }
            finally { CFRelease(axWindow); }

            var role = AxText(element, "AXRole");
            var capability = request.Action switch
            {
                "scroll" when role == "AXScrollArea" => ComputerCapabilities.Scroll,
                "drag" when IsTextRole(role) => ComputerCapabilities.SelectText,
                "press_key" when IsTextRole(role) && request.Modifiers.SequenceEqual(new[] { "command" })
                    && string.Equals(request.Key, "c", StringComparison.OrdinalIgnoreCase) => ComputerCapabilities.CopyText,
                _ => ComputerCapabilities.None,
            };
            if (capability == ComputerCapabilities.None)
                return new(ComputerRisk.RequireConfirmation, "Browsing permission does not cover clicks, typing, pasting or unclassified actions.");
            var verification = AccessibilityInputVerification.Create(element, window, request);
            if (verification is null) return ComputerRiskDecision.Unknown;
            return new(ComputerRisk.Allow, $"Verified accessibility browsing capability: {capability}.")
                { Capability = capability, Verification = verification };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException) { return ComputerRiskDecision.Unknown; }
        finally { if (element != 0) CFRelease(element); CFRelease(app); }
    }

    private static bool IsTextRole(string role) => role is "AXTextArea" or "AXTextField" or "AXStaticText";

    // Scroll hit tests often return a text child, not its scroll container. Use the same resolved target
    // for assessment and delivery verification, without expanding a text selection to a different element.
    private static nint ResolveInputElement(nint app, CapturableWindow window, ComputerActionRequest request)
    {
        nint element;
        if (request.Action is "click" or "double_click" or "drag" or "scroll")
        {
            if (AXUIElementCopyElementAtPosition(app, window.Left + request.X, window.Top + request.Y, out element) != 0)
                return 0;
        }
        else element = AxAttribute(app, "AXFocusedUIElement");
        if (request.Action != "scroll") return element;
        for (var depth = 0; element != 0 && depth < 16; depth++)
        {
            if (AxText(element, "AXRole") == "AXScrollArea") return element;
            if (AxText(element, "AXRole") == "AXWindow") break;
            var parent = AxAttribute(element, "AXParent");
            CFRelease(element);
            element = parent;
        }
        if (element != 0) CFRelease(element);
        return 0;
    }

    private static bool UnsafeContext(nint element)
    {
        var current = CFRetain(element);
        try
        {
            for (var depth = 0; current != 0 && depth < 20; depth++)
            {
                var role = AxText(current, "AXRole");
                var subrole = AxText(current, "AXSubrole");
                var label = string.Join(" ", new[] { "AXTitle", "AXDescription", "AXHelp", "AXIdentifier" }
                    .Select(key => AxText(current, key)));
                if (subrole is "AXSecureTextField" or "AXDialog" or "AXSystemDialog"
                    || role is "AXSheet" or "AXDialog" or "AXButton" or "AXLink" or "AXMenuItem"
                    || SensitiveLabel(label)) return true;
                if (role == "AXWindow") return false;
                var parent = AxAttribute(current, "AXParent");
                CFRelease(current);
                current = parent;
            }
            return true; // Missing or unbounded ownership chains never establish safety.
        }
        finally { if (current != 0) CFRelease(current); }
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
