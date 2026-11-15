using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;
using static AxmolHub.WindowsAutomation;

namespace AxmolHub;

internal static partial class WindowsComputerUse
{
    private const int TextPattern = 10014, ScrollPattern = 10004, EditControl = 50004, DocumentControl = 50030;
    private const int TextLimit = 65536;
    private sealed class PointerVerification : IComputerInputVerification
    {
        public string? Verify() => null; // Native delivery still checks identity, geometry, focus and hit ownership.
        public void Dispose() { }
    }

    private static ComputerRiskDecision AssessRisk(CapturableWindow window, ComputerActionRequest request)
    {
        if (ComputerInput.Validate(request) is { } invalid) return new(ComputerRisk.Deny, invalid);
        if (request.Modifiers.Contains("command")) return new(ComputerRisk.Deny, "Command is not a Windows modifier.");
        if (request.Action == "press_key" && (request.Modifiers.Contains("alt")
            || request.Modifiers.Contains("control") && string.Equals(request.Key, "Enter", StringComparison.OrdinalIgnoreCase)))
            return new(ComputerRisk.RequireConfirmation, "Potential application/menu/submit shortcut requires separate confirmation and strict target verification.");
        if (request.Action == "move") return new(ComputerRisk.Allow, "Pointer motion within the authorized window.")
            { Capability = ComputerCapabilities.MovePointer, Verification = new PointerVerification() };
        try
        {
            return Run(client =>
            {
                using var element = Resolve(client, window, request);
                var snapshot = ReadSnapshot(client, element, window, request);
                var capability = request.Action switch
                {
                    "scroll" => ComputerCapabilities.Scroll,
                    "drag" when snapshot.TextHash is not null => ComputerCapabilities.SelectText,
                    "press_key" when IsCopy(request) && snapshot.NonEmptySelection => ComputerCapabilities.CopyText,
                    _ => ComputerCapabilities.None,
                };
                var interaction = (request.Capabilities & ComputerCapabilities.Interact) != 0;
                if (capability == ComputerCapabilities.None && interaction) capability = ComputerCapabilities.Interact;
                var verification = new AutomationVerification(window, request, snapshot, capability);
                return new ComputerRiskDecision(capability == ComputerCapabilities.None ? ComputerRisk.RequireConfirmation : ComputerRisk.Allow,
                    capability == ComputerCapabilities.None
                        ? "This edit, click or shortcut requires confirmation; UI Automation will verify the same control, text and selection."
                        : capability == ComputerCapabilities.Interact
                            ? "Explicit application interaction permission with UI Automation target verification."
                            : $"Verified UI Automation browsing capability: {capability}.")
                    { Capability = capability, Verification = verification };
            });
        }
        catch (SensitiveTarget)
        {
            return new(ComputerRisk.RequireConfirmation, "Recognized sensitive or actionable target; review this exact action. Strict target verification remains required.");
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            if ((request.Capabilities & ComputerCapabilities.Interact) == 0) return ComputerRiskDecision.Unknown;
            // Missing TextPattern does not hide recognized sensitive window/hit metadata. This pass
            // reads only metadata, never asks another control to stand in for the missing text target.
            try
            {
                Run(client =>
                {
                    using var root = client.FromHandle(window.Handle);
                    _ = Context(client, root, window);
                    if (request.Action is "click" or "double_click" or "drag" or "scroll")
                    {
                        using var hit = client.FromPoint(window.Left + request.X, window.Top + request.Y);
                        _ = Context(client, hit, window);
                    }
                    else if (GetForegroundWindow() == window.Handle)
                    {
                        using var focus = client.Focused();
                        _ = Context(client, focus, window);
                    }
                    return true;
                });
            }
            catch (SensitiveTarget)
            { return new(ComputerRisk.RequireConfirmation, "Recognized sensitive/actionable target metadata requires separate confirmation and strict verification."); }
            catch (Exception metadataError) when (Unavailable(metadataError)) { }
            return new(ComputerRisk.Allow, "Explicit temporary application interaction permission; generic delivery without semantic verification.")
            {
                Capability = ComputerCapabilities.Interact, GenericInteraction = true,
                Verification = new PointerVerification(),
            };
        }
    }

    private sealed class SensitiveTarget : InvalidOperationException;

    private static bool Unavailable(Exception ex) => ex is ExternalException or InvalidOperationException or ArgumentException or NotSupportedException;

    // Only fixed stage names and HRESULTs reach the tool result; provider messages can contain document text.
    private sealed class VerificationFailure(string stage, Exception inner) : InvalidOperationException(stage, inner)
    {
        internal string Stage { get; } = stage;
    }
    private static T AtStage<T>(string stage, Func<T> read)
    {
        try { return read(); }
        catch (SensitiveTarget) { throw; }
        catch (VerificationFailure) { throw; }
        catch (Exception ex) when (Unavailable(ex)) { throw new VerificationFailure(stage, ex); }
    }
    private static string VerificationRefusal(Exception ex, bool focused)
    {
        var stage = ex is VerificationFailure failure ? failure.Stage : "client-connection";
        var hr = ex is VerificationFailure { InnerException: { } inner } ? inner.HResult : ex.HResult;
        return $"UI Automation {(focused ? "post-activation" : "pre-activation")} verification unavailable at {stage} (HRESULT 0x{hr:X8}). Observe again.";
    }
    private static bool IsCopy(ComputerActionRequest r) => r.Modifiers.SequenceEqual(new[] { "control" })
        && string.Equals(r.Key, "c", StringComparison.OrdinalIgnoreCase);
    private static bool TextRole(Com element) => element.Int(30003) is EditControl or DocumentControl;
    private static string NormalizeText(string text) => TextEol.ToLf(text);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Identity(Com element) => string.Join(",", element.RuntimeId());

    private static Com Resolve(Client client, CapturableWindow window, ComputerActionRequest r, string? expectedIdentity = null)
    {
        Com element;
        var pointer = r.Action is "click" or "double_click" or "drag" or "scroll";
        var foreground = GetForegroundWindow() == window.Handle;
        // Once assessed, resolve that exact runtime identity independently of global UIA focus.
        // A retained HWND may be a XAML pane; global UIA focus can also lag HWND activation.
        // Delivery separately requires actual focus on this same assessed control, never a substitute.
        if (!pointer && expectedIdentity is not null)
            element = UniqueTextTarget(client, window, expectedIdentity);
        else if (pointer)
            element = client.FromPoint(window.Left + r.X, window.Top + r.Y);
        else
        {
            // A Hub approval owns global focus. Use the selected thread's focused HWND when available,
            // otherwise a bounded unique/previously assessed text target; never the first text control.
            var thread = GetWindowThreadProcessId(window.Handle, out _);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(thread, ref info))
                throw new InvalidOperationException("The selected window's focus state is unavailable.");
            if (GetForegroundWindow() != window.Handle && info.Focus == 0)
                element = UniqueTextTarget(client, window, expectedIdentity);
            else
            {
                if (info.Focus == 0 || GetAncestor(info.Focus, 2) != window.Handle)
                    throw new InvalidOperationException("The selected window has no verified focused child.");
                element = client.FromHandle(info.Focus);
            }
            // Virtual controls may share one HWND. Only use global UIA focus when it is inside this window.
            if (foreground)
            {
                element.Dispose();
                element = client.Focused();
            }
        }
        try
        {
            _ = Context(client, element, window); // Do not lose an actionable hit when normalizing to its text/scroll ancestor.
            for (var depth = 0; depth < 16; depth++)
            {
                if (element.Int(30002) != window.ProcessId || element.Bool(30019))
                    throw new SensitiveTarget();
                var supported = r.Action == "scroll" ? Supports(element, ScrollPattern) : TextRole(element) && Supports(element, TextPattern);
                if (supported) return element;
                using var root = client.FromHandle(window.Handle);
                if (client.Same(element, root)) break;
                var parent = client.Parent(element);
                element.Dispose(); element = parent;
            }
            throw new InvalidOperationException("No supported text or scroll target.");
        }
        catch { element.Dispose(); throw; }
    }

    // Inactive threads can expose no focused HWND. A unique visible text-pattern control is a
    // candidate, never permission to type into the first edit: exact UIA focus must match after
    // cooperative activation, or delivery refuses. Multiple candidates remain unclassified.
    private static Com UniqueTextTarget(Client client, CapturableWindow window, string? expectedIdentity)
    {
        using var root = client.FromHandle(window.Handle);
        using var candidates = client.TextDescendants(root);
        var count = candidates.Number(3);
        if (count is < 1 or > 32) throw new InvalidOperationException("Text target is missing or unbounded.");
        Com? selected = null;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var candidate = candidates.Item(i);
                var keep = false;
                try
                {
                    if (candidate.Bool(30022, required: false) || !candidate.Bool(30010)
                        || expectedIdentity is not null && Identity(candidate) != expectedIdentity || !Supports(candidate, TextPattern)) continue;
                    if (selected is not null) throw new InvalidOperationException("Multiple visible text targets.");
                    selected = candidate; keep = true;
                }
                finally { if (!keep) candidate.Dispose(); }
            }
            return selected ?? throw new InvalidOperationException("No visible text-pattern target.");
        }
        catch { selected?.Dispose(); throw; }
    }

    private static bool Supports(Com element, int pattern)
    {
        try { using var value = element.Pattern(pattern); return true; }
        catch (Exception ex) when (Unavailable(ex)) { return false; }
    }

    private sealed record Snapshot(string Identity, Rect Bounds, string Context, string? TextHash,
        string Selection, bool NonEmptySelection, string Scroll, string Endpoints);

    private static Snapshot ReadSnapshot(Client client, Com element, CapturableWindow window, ComputerActionRequest r)
    {
        var context = AtStage("control-context", () => Context(client, element, window));
        var bounds = AtStage("control-bounds", element.Bounds);
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top
            || bounds.Left < window.Left || bounds.Top < window.Top
            || bounds.Right > window.Left + window.Width || bounds.Bottom > window.Top + window.Height)
            throw new InvalidOperationException("Control bounds outside the authorized window.");
        if (r.Action == "scroll")
        {
            using var scroll = element.Pattern(ScrollPattern);
            if (r.Scroll != 0 && scroll.Number(10) == 0 || r.HorizontalScroll != 0 && scroll.Number(9) == 0)
                throw new InvalidOperationException("The target does not support the requested scroll axis.");
            var state = string.Join("|", Enumerable.Range(5, 4).Select(slot => scroll.Real(slot).ToString("R", CultureInfo.InvariantCulture)));
            return new(Identity(element), bounds, context, null, "", false, state, "");
        }
        if (!TextRole(element)) throw new InvalidOperationException("Not a text control.");
        using var pattern = AtStage("text-pattern", () => element.Pattern(TextPattern));
        if (AtStage("selection-support", () => pattern.Number(8)) == 0)
            throw new VerificationFailure("selection-support", new InvalidOperationException());
        using var document = AtStage("document-range", () => pattern.Object(7));
        var textHash = AtStage("document-text", () => Hash(NormalizeText(document.RangeText(TextLimit + 1))));
        using var selections = AtStage("selection-ranges", () => pattern.Object(5));
        if (AtStage("selection-count", () => selections.Number(3)) != 1)
            throw new VerificationFailure("selection-count", new InvalidOperationException());
        using var selection = AtStage("selected-range", () => selections.Item(0));
        var start = AtStage("selection-start", () => Offset(document, selection, 0));
        var end = AtStage("selection-end", () => Offset(document, selection, 1));
        if (start < 0 || end < start) throw new InvalidOperationException("Invalid text range.");
        var endpoints = "";
        if (r.Action == "drag")
        {
            using var other = Resolve(client, window, r with { X = r.EndX, Y = r.EndY });
            if (!client.Same(element, other)) throw new InvalidOperationException("Selection endpoints are in different controls.");
            using var range = SelectionRange(client, element, pattern, window, r);
            endpoints = $"{Offset(document, range, 0)}:{Offset(document, range, 1)}";
            if (range.CompareEndpoint(0, range, 1) == 0) throw new InvalidOperationException("Empty selection gesture.");
        }
        return new(Identity(element), bounds, context, textHash, $"{start}:{end}", end > start, "", endpoints);
    }

    private static int Offset(Com document, Com range, int endpoint)
    {
        if (range.CompareEndpoint(endpoint, document, 0) < 0 || range.CompareEndpoint(endpoint, document, 1) > 0)
            throw new InvalidOperationException("Text range is outside the document.");
        using var prefix = document.Object(3); // Clone
        prefix.Endpoint(1, range, endpoint);
        return NormalizeText(prefix.RangeText(TextLimit + 1)).Length;
    }

    private static Com SelectionRange(Client client, Com element, Com pattern, CapturableWindow window, ComputerActionRequest r)
    {
        var first = pattern.RangeAt(window.Left + r.X, window.Top + r.Y);
        try
        {
            using var last = pattern.RangeAt(window.Left + r.EndX, window.Top + r.EndY);
            using var firstOwner = first.Object(11);
            using var lastOwner = last.Object(11);
            // Text range ownership can be an inner text node; it must still be in the same bounded text context.
            if (Context(client, firstOwner, window) != Context(client, lastOwner, window))
                throw new InvalidOperationException("Text range owners differ.");
            if (first.CompareEndpoint(0, last, 0) <= 0) first.Endpoint(1, last, 0);
            else { first.Endpoint(1, first, 0); first.Endpoint(0, last, 0); }
            return first;
        }
        catch { first.Dispose(); throw; }
    }

    private static string Context(Client client, Com element, CapturableWindow window)
    {
        using var root = client.FromHandle(window.Handle);
        var chain = new StringBuilder();
        // Borrow the initial element; own and release only the subsequent parent references.
        var current = element;
        var ownsCurrent = false;
        try
        {
            for (var depth = 0; depth < 24; depth++)
            {
                if (current.Int(30002) != window.ProcessId || !current.Bool(30010) || current.Bool(30019)
                    || current.Bool(30022, required: false)) throw new SensitiveTarget();
                var role = current.Int(30003);
                var rootReached = client.Same(current, root);
                // Positive container/text roles, not an absence-of-sensitive-words allowlist. In particular,
                // combo/check/radio/slider/menu/button ancestors may turn gestures into state changes.
                if (role is not (50004 or 50008 or 50020 or 50023 or 50026 or 50028 or 50030 or 50032 or 50033 or 50036)
                    || current.Property(30077, required: false) is true)
                    throw new SensitiveTarget();
                var name = current.Text(30005);
                var label = string.Join(" ", name, current.Text(30011), current.Text(30012), current.Text(30013));
                if (Sensitive(label)) throw new SensitiveTarget();
                // A document title/chrome can change asynchronously after editing; its native identity and
                // geometry are checked separately. Do not include the window's Name in semantic equality.
                chain.Append(Identity(current)).Append('|').Append(role).Append('|');
                if (!rootReached) chain.Append(Hash(label)).Append('|').Append(current.Bounds()).Append('|');
                if (rootReached) return Hash(chain.ToString());
                var parent = client.Parent(current);
                if (ownsCurrent) current.Dispose();
                current = parent; ownsCurrent = true;
            }
            throw new InvalidOperationException("Unbounded or missing authorized window ancestry.");
        }
        finally { if (ownsCurrent) current.Dispose(); }
    }

    private static bool Sensitive(string label) => new[]
        { "send", "submit", "pay", "purchase", "password", "credential", "upload", "delete", "remove", "发送", "提交", "付款", "支付", "密码", "上传", "删除" }
        .Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase));

    private sealed class AutomationVerification(CapturableWindow window, ComputerActionRequest request,
        Snapshot snapshot, ComputerCapabilities capability) : IComputerInputVerification
    {
        private int disposed;
        private Snapshot Assessed => snapshot;
        private CapturableWindow Window => window;
        private ComputerActionRequest Request => request;
        internal ComputerCapabilities Capability => capability;
        public string? Verify() => Check(false);
        private const string FocusPending = "The assessed text control has not acquired UI Automation keyboard focus.";
        internal string? VerifyFocused(Func<bool> authorized, CancellationToken cancellation)
        {
            var deadline = Environment.TickCount64 + 750;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!authorized()) return "Permission, window identity, geometry or native focus changed while verifying UI Automation focus.";
                var result = Check(true);
                // Only wait for focus propagation. Never retry changed text, metadata, selection, or a failed provider read.
                if (result != FocusPending || Environment.TickCount64 >= deadline) return result;
                if (cancellation.WaitHandle.WaitOne(25)) cancellation.ThrowIfCancellationRequested();
            }
        }
        internal bool KeyboardFocus()
        {
            if (Volatile.Read(ref disposed) != 0) return false;
            try
            {
                return Run(client =>
                {
                    using var focused = client.Focused();
                    return Identity(focused) == snapshot.Identity && focused.Int(30002) == window.ProcessId
                        && focused.Bool(30010) && !focused.Bool(30019) && focused.Bounds() == snapshot.Bounds;
                });
            }
            catch (Exception ex) when (Unavailable(ex)) { return false; }
        }
        internal TextDelivery BeginTextDelivery()
        {
            return Run(client =>
            {
                if (Volatile.Read(ref disposed) != 0) throw new InvalidOperationException("Assessment ended.");
                using var element = Resolve(client, window, request, snapshot.Identity);
                using var focused = client.Focused();
                if (!client.Same(element, focused) || ReadSnapshot(client, element, window, request) != snapshot)
                    throw new InvalidOperationException("Text target changed before delivery.");
                using var pattern = element.Pattern(TextPattern);
                using var document = pattern.Object(7);
                var text = NormalizeText(document.RangeText(TextLimit + 1));
                if (Hash(text) != snapshot.TextHash) throw new InvalidOperationException("Text changed before delivery.");
                var offsets = snapshot.Selection.Split(':').Select(int.Parse).ToArray();
                if (offsets.Length != 2 || offsets[0] < 0 || offsets[1] > text.Length)
                    throw new InvalidOperationException("Invalid selection before delivery.");
                if (text.Length - (offsets[1] - offsets[0]) + NormalizeText(request.Text ?? "").Length > TextLimit)
                    throw new InvalidOperationException("Requested text exceeds the verification bound.");
                return new TextDelivery(this, text, offsets[0], offsets[1]);
            });
        }

        // One posted scalar must be observed before another can reuse VK_PACKET. Queue acceptance
        // is not document acceptance. Text stays only in this bounded call-local tracker, never logs.
        internal sealed class TextDelivery(AutomationVerification owner, string initialText, int start, int end) : IDisposable
        {
            private string expectedText = initialText;
            private Snapshot previous = owner.Assessed;
            private int selectionStart = start, selectionEnd = end;
            internal string? ConfirmBeforeInput()
            {
                try { return Read() == previous ? null : "Refused: text or selection changed between input characters. Further input stopped."; }
                catch (Exception ex) when (Unavailable(ex)) { return "Refused: text delivery verifier became unavailable before the next character."; }
            }
            private Snapshot Read() => Run(client =>
            {
                if (Volatile.Read(ref owner.disposed) != 0) throw new InvalidOperationException("Assessment ended.");
                using var element = Resolve(client, owner.Window, owner.Request, owner.Assessed.Identity);
                using var focused = client.Focused();
                if (!client.Same(element, focused)) throw new InvalidOperationException("Text focus changed.");
                return ReadSnapshot(client, element, owner.Window, owner.Request);
            });
            internal string? Acknowledge(string inserted, Func<bool> authorized, CancellationToken cancellation)
            {
                var nextText = expectedText[..selectionStart] + inserted + expectedText[selectionEnd..];
                if (nextText.Length > TextLimit) return "Refused: text delivery exceeds the bounded verification size.";
                var caret = selectionStart + inserted.Length;
                var next = previous with { TextHash = Hash(nextText), Selection = $"{caret}:{caret}", NonEmptySelection = false };
                var deadline = Environment.TickCount64 + 1500;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!authorized()) return Interrupted;
                    Snapshot current;
                    try { current = Read(); }
                    catch (Exception ex) when (Unavailable(ex))
                    { return "Refused: text acknowledgement became unavailable after input. Do not replay; observe the partial document."; }
                    if (current == next)
                    {
                        expectedText = nextText; previous = next; selectionStart = selectionEnd = caret;
                        return null;
                    }
                    // UIA text and caret updates can arrive separately. Only the old and exact expected
                    // text/selection are pending states; a substituted/duplicated character is never accepted.
                    var sameControl = current.Identity == previous.Identity && current.Bounds == previous.Bounds
                        && current.Context == previous.Context;
                    var pendingText = current.TextHash == previous.TextHash || current.TextHash == next.TextHash;
                    var pendingSelection = current.Selection == previous.Selection || current.Selection == next.Selection;
                    if (!sameControl || !pendingText || !pendingSelection)
                        return "Refused: the editor's text or caret differs from the exact expected input. Further input stopped; do not replay.";
                    if (Environment.TickCount64 >= deadline)
                        return "Refused: the editor did not acknowledge the last character within 1500 ms. Further input stopped; do not replay.";
                    if (cancellation.WaitHandle.WaitOne(15)) cancellation.ThrowIfCancellationRequested();
                }
            }
            public void Dispose() { expectedText = ""; }
        }

        private string? Check(bool focused)
        {
            if (Volatile.Read(ref disposed) != 0) return "UI Automation assessment is no longer live.";
            try
            {
                return Run<string?>(client =>
                {
                    using var element = AtStage("target-resolution", () => Resolve(client, window, request, snapshot.Identity));
                    var currentSnapshot = AtStage("target-snapshot", () => ReadSnapshot(client, element, window, request));
                    if (currentSnapshot != snapshot)
                    {
                        var field = currentSnapshot.Identity != snapshot.Identity ? "control identity"
                            : currentSnapshot.Bounds != snapshot.Bounds ? "control geometry"
                            : currentSnapshot.Context != snapshot.Context ? "control metadata/ancestry"
                            : currentSnapshot.TextHash != snapshot.TextHash ? "document text"
                            : currentSnapshot.Selection != snapshot.Selection ? "text selection"
                            : currentSnapshot.Scroll != snapshot.Scroll ? "scroll state" : "selection endpoints";
                        return $"UI Automation {field} changed. Observe again.";
                    }
                    if (focused && request.Action is "type" or "press_key")
                    {
                        using var current = AtStage("keyboard-focus", client.Focused);
                        if (!client.Same(element, current)) return FocusPending;
                    }
                    return null;
                });
            }
            catch (Exception ex) when (Unavailable(ex)) { return VerificationRefusal(ex, focused); }
        }
        internal string? Browse(Func<bool> authorized, Action? started)
        {
            try
            {
                return Run<string?>(client =>
                {
                    if (Volatile.Read(ref disposed) != 0 || !authorized()) return Interrupted;
                    using var element = Resolve(client, window, request, snapshot.Identity);
                    if (ReadSnapshot(client, element, window, request) != snapshot) return "Refused: assessed browsing target changed.";
                    if (capability == ComputerCapabilities.SelectText)
                    {
                        using var pattern = element.Pattern(TextPattern);
                        using var range = SelectionRange(client, element, pattern, window, request);
                        if (!authorized()) return Interrupted;
                        started?.Invoke(); // Focusing is itself a delivered operation, even if Select later fails.
                        element.Focus();
                        if (!authorized()) return Interrupted;
                        using var focused = client.Focused();
                        if (!client.Same(element, focused) || ReadSnapshot(client, element, window, request) != snapshot)
                            return "Refused: focus or text selection changed before selection delivery.";
                        if (!authorized()) return Interrupted;
                        range.Select(); // Never drag existing selected text or start drag-and-drop.
                        return null;
                    }
                    if (capability != ComputerCapabilities.Scroll) return "Refused: no verified direct browsing operation.";
                    using var scroll = element.Pattern(ScrollPattern);
                    var count = Math.Max(Math.Abs(request.Scroll), Math.Abs(request.HorizontalScroll));
                    for (var i = 0; i < count; i++)
                    {
                        if (!authorized() || Context(client, element, window) != snapshot.Context) return Interrupted;
                        var horizontal = i < Math.Abs(request.HorizontalScroll) ? request.HorizontalScroll > 0 ? 0 : 3 : 2;
                        var vertical = i < Math.Abs(request.Scroll) ? request.Scroll > 0 ? 0 : 3 : 2;
                        started?.Invoke();
                        scroll.Scroll(horizontal, vertical); // SmallDecrement=0, NoAmount=2, SmallIncrement=3
                    }
                    return null;
                });
            }
            catch (Exception ex) when (Unavailable(ex)) { return "Refused: UI Automation browsing delivery failed; observe before any retry."; }
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }

    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        internal int Size; internal uint Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretBounds;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
