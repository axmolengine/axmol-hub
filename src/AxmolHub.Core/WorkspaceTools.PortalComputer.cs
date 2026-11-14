using System.ComponentModel;

namespace AxmolHub.Core;

public sealed partial class WorkspaceTools
{
    [Description("Capture a named application window. On Wayland the user selects one window in the system portal; the supplied name is a request, not verified application identity. No whole-display capture or permanent portal grant. Capture waits 3 seconds after selection/approval so the user can switch back to the intended window for a consistent input baseline.")]
    public Task<string> CaptureScreenAsync(
        [Description("Requested window title. On Wayland the user must select the intended window in the system picker.")] string target = "",
        [Description("Whole-display capture is disabled.")] bool fullscreen = false,
        [Description("Optional process filter for native window backends. Not available for Wayland portals.")] int process_id = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.PortalComputer is null) return Task.FromResult(CaptureScreen(target, fullscreen, process_id, cancellationToken));
        if (fullscreen) return Task.FromResult("Refused: whole-display capture is disabled. Select one window in the portal.");
        return ExecutePortalComputerAsync(new(target, "screenshot", null, 0, 0, null, null, 0), process_id, cancellationToken);
    }

    [Description("Observe or control a window. Native backends reuse application-session permission; sensitive or unclassified inputs require confirmation. On Wayland every call still requires individual approval and uses a temporary portal session, not an executable grant. After Wayland approval the user has a 3-second focus handoff. Other application focus cannot be verified. Inspect returned frames and never replay input automatically.")]
    internal Task<string> ComputerUseAsync(
        [Description("Requested window. Wayland uses the user-selected portal stream and cannot verify its title or process.")] string application,
        [Description("screenshot, click, double_click, move, drag, type, press_key, or scroll. Extended actions are macOS-only.")] string action,
        [Description("Pointer x in screenshot pixels; -1 with y=-1 uses center for scrolling.")] int x = -1,
        [Description("Pointer y in screenshot pixels.")] int y = -1,
        [Description("Text to type, at most 1000 characters.")] string? text = null,
        [Description("One named key, letter or digit.")] string? key = null,
        [Description("Vertical scroll steps, -10 to 10 excluding zero.")] int scroll = 0,
        [Description("Native process filter; unavailable for Wayland portal sessions.")] int process_id = 0,
        [Description("Latest frame_id for one input only.")] string? frame_id = null,
        [Description("Drag end x in screenshot pixels.")] int end_x = -1,
        [Description("Drag end y in screenshot pixels.")] int end_y = -1,
        [Description("Drag duration, 100 to 3000 milliseconds.")] int duration_ms = 400,
        [Description("press_key modifiers: command, control, alt, shift.")] string[]? modifiers = null,
        [Description("Horizontal scroll steps, -10 to 10.")] int horizontal_scroll = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.PortalComputer is null)
            return Task.FromResult(ComputerUse(application, action, x, y, text, key, scroll, process_id, frame_id,
                end_x, end_y, duration_ms, modifiers, horizontal_scroll, cancellationToken));
        if (end_x != -1 || end_y != -1 || duration_ms != 400 || modifiers is { Length: > 0 } || horizontal_scroll != 0
            || action == "scroll" && (x != -1 || y != -1))
            return Task.FromResult("Refused: extended pointer, modifier and positioned-scroll actions are unavailable on this Wayland backend.");
        return ExecutePortalComputerAsync(new(application, (action ?? "").Trim().ToLowerInvariant(), frame_id, x, y, text, key, scroll),
            process_id, cancellationToken);
    }

    private async Task<string> ExecutePortalComputerAsync(PortalComputerRequest request, int processId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Application) || request.Application.Length > 256)
            return "Refused: name the intended window, up to 256 characters. The user selects it in the portal.";
        if (processId != 0) return "Refused: Wayland portals cannot verify process_id. Do not infer a process from the shared stream.";
        if (request.Action is not ("screenshot" or "click" or "type" or "press_key" or "scroll"))
            return "Refused: unsupported Computer Use action.";
        if (request.Action == "type" && (string.IsNullOrEmpty(request.Text) || request.Text.Length > 1000))
            return "Refused: type needs 1 to 1000 characters. Do not split and retry automatically.";
        if (request.Action == "press_key" && string.IsNullOrWhiteSpace(request.Key))
            return "Refused: press_key requires one supported named key.";
        if (request.Action == "scroll" && (request.Scroll == 0 || Math.Abs((long)request.Scroll) > 10))
            return "Refused: scroll needs -10 to 10 steps excluding zero.";
        if (context.PortalComputerApproved?.Invoke() != true)
            return "Refused: this portal call has no individual confirmation. No permanent application grant applies.";
        using var lease = ComputerDesktop.TryEnter();
        if (lease is null) return "Refused: another conversation is using the desktop. Observe again after it finishes.";
        try
        {
            var result = await context.PortalComputer!.ExecuteAsync(request with { StillApproved = context.PortalComputerApproved }, cancellationToken).ConfigureAwait(false);
            if (result.Frame is not { } frame) return result.Detail;
            if (context.PortalComputerApproved?.Invoke() != true)
                return "Refused: portal confirmation was revoked before attaching the frame.";
            if (frame.Stats.IsBlank()) return "Refused: portal returned a blank frame. No picture was attached; ask the user before retrying.";
            var verdict = ChatImageFormat.Admit(frame.Png, 0);
            if (verdict != ChatImageVerdict.Accepted) return ChatImageFormat.ResultFor(verdict, frame.Png.LongLength);
            if (context.Sessions is not { } store) return "Refused: attachment storage is unavailable.";
            var image = store.SaveImage(context.ConversationId, frame.Png);
            context.RecordFrames?.Invoke([image]);
            return result.Detail + $"\nimage={frame.Width}×{frame.Height}; attached={image.File}. Portal-selected window; application identity and keyboard focus are unverified. Inspect the picture before claiming success.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return "Refused: " + ex.Message + (request.Action == "screenshot" ? " Do not retry automatically."
                : " Input may have been partially delivered; do not replay automatically.");
        }
    }
}
