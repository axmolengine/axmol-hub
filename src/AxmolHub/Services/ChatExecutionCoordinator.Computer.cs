using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed partial class ChatExecutionCoordinator
{
    private readonly ConcurrentDictionary<string, ComputerControlSession> _computerControls = new();
    private readonly ConcurrentDictionary<string, PortalControlSession> _portalControls = new();
    private readonly LinuxWaylandRemoteDesktop _portalDesktop = new();
    private readonly ConcurrentDictionary<(string Conversation, string Call), (PortalControlSession Control, long Version)> _portalPermits = new();
    private bool PortalPermitCurrent(string id, string call)
        => _portalPermits.TryGetValue((id, call), out var permit)
            && ReferenceEquals(permit.Control, PortalControlFor(id))
            && permit.Control.Covers(permit.Control.Application, permit.Version);
    internal PortalControlSession? PortalControlFor(string id)
        => _portalControls.TryGetValue(id, out var control) ? control : null;

    private static string PortalApplication(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "{}");
            var root = document.RootElement;
            return (root.TryGetProperty("application", out var app) ? app.GetString()
                : root.TryGetProperty("target", out var target) ? target.GetString() : null)?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return ""; }
    }

    private PortalComputerBridge PortalBridge(string id) => new(async (request, cancellation) =>
    {
        var control = PortalControlFor(id);
        var version = control?.Version ?? 0;
        if (control is null || !control.Covers(request.Application, version))
            return new(null, "Refused: sharing permission changed or is paused. Resume and observe again.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, control.Cancellation);
        bool Approved() => ReferenceEquals(control, PortalControlFor(id)) && control.Covers(request.Application, version)
            && request.StillApproved?.Invoke() == true;
        return await _portalDesktop.Bridge(id).ExecuteAsync(request with
        {
            StillApproved = Approved,
            ContinuousInteraction = control.Interaction,
        }, linked.Token);
    });

    private void GrantPortalControl(string id, string application, bool interaction)
    {
        RevokeComputerControl(id);
        // One desktop stream is active per host. A new selection never inherits the old stream's consent.
        foreach (var other in _portalControls.Keys.ToArray()) RevokeComputerControl(other);
        _portalControls[id] = new(application, interaction);
        _portalDesktop.SessionEnded = closed =>
        {
            if (_portalControls.TryRemove(closed, out var old)) old.Revoke();
            foreach (var key in _portalPermits.Keys.Where(key => key.Conversation == closed)) _portalPermits.TryRemove(key, out _);
            RaiseOnUi(() => Changed?.Invoke());
        };
    }
    private sealed record InputPermit(ComputerControlSession Session, long Version, string Arguments,
        ComputerRiskDecision Decision, bool Approved);
    private readonly ConcurrentDictionary<(string Conversation, string Call), InputPermit> _computerPermits = new();
    private sealed record FailedInput(ChatTurn? UserTurn, string Signature);
    private readonly ConcurrentDictionary<string, FailedInput> _failedComputerInputs = new();

    private static string InputSignature(string? json)
    {
        var request = ComputerInput.Parse(json);
        // Excludes the replaceable observation ID, but never stores typed text in plaintext.
        return request is null ? "invalid" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                request.Action, request.X, request.Y, request.Text, request.Key, request.Scroll,
                request.EndX, request.EndY, request.DurationMs, request.Modifiers, request.HorizontalScroll,
            }))));
    }

    private void RecordComputerFailure(string id, string? callId)
    {
        var conversation = Sessions.Registry.Peek(id);
        var call = conversation?.Messages.LastOrDefault(t => t.Role == ChatRoles.Assistant && t.ToolCallId == callId);
        if (call is null) return;
        _failedComputerInputs[id] = new(conversation!.Messages.LastOrDefault(t => t.Role == ChatRoles.User), InputSignature(call.ToolArguments));
    }

    internal ComputerControlSession? ComputerControlFor(string id)
        => _computerControls.TryGetValue(id, out var value) ? value : null;

    internal void PauseComputerControl(string id, bool paused)
    {
        if (PortalControlFor(id) is { } portal)
        {
            portal.Pause(paused);
            foreach (var key in _portalPermits.Keys.Where(key => key.Conversation == id)) _portalPermits.TryRemove(key, out _);
            _portalDesktop.CloseFor(id, notify: false);
            Changed?.Invoke();
            return;
        }
        if (ComputerControlFor(id) is not { } control) return;
        control.Pause(paused);
        foreach (var key in _computerPermits.Keys.Where(key => key.Conversation == id)) ReleaseComputerPermit(key);
        if (_computerObservations.TryGetValue(id, out var observation)) observation.Invalidate();
        Audit(id, paused ? "Computer Use paused; observations invalidated." : "Computer Use resumed; fresh observation required.");
        Changed?.Invoke();
    }

    internal void RevokeComputerControl(string id)
    {
        if (_portalControls.TryRemove(id, out var portal)) portal.Revoke();
        foreach (var key in _portalPermits.Keys.Where(key => key.Conversation == id)) _portalPermits.TryRemove(key, out _);
        _portalDesktop.CloseFor(id, notify: false);
        _failedComputerInputs.TryRemove(id, out _);
        if (_computerControls.TryRemove(id, out var control))
        {
            control.Revoke();
            Audit(id, "Computer Use application session permission revoked.");
        }
        if (_computerObservations.TryRemove(id, out var observation)) observation.Invalidate();
        foreach (var key in _computerPermits.Keys.Where(key => key.Conversation == id)) ReleaseComputerPermit(key);
        Changed?.Invoke();
    }

    private void ReleaseComputerPermit((string Conversation, string Call) key)
    {
        if (_computerPermits.TryRemove(key, out var permit)) permit.Decision.Verification?.Dispose();
    }

    private void RecordComputerPermit(string id, string call, ComputerControlSession control, string arguments,
        ComputerRiskDecision decision)
    {
        ReleaseComputerPermit((id, call));
        _computerPermits[(id, call)] = new(control, control.Version, arguments, decision, false);
    }

    private void GrantComputerControl(string id, ComputerUseTarget target, bool interaction = false)
    {
        RevokeComputerControl(id);
        var conversation = Sessions.Registry.Peek(id);
        var task = conversation?.Messages.LastOrDefault(t => t.Role == ChatRoles.User)?.Text ?? "GUI task";
        _computerControls[id] = new(target, task, interaction);
        Audit(id, $"Computer Use {(interaction ? "interaction" : "browsing")} session permission granted · initial window {target.WindowHandle} · pid {target.ProcessId} · {target.Application.ExecutablePath}");
    }

    private ComputerRiskDecision AssessComputerInput(string id, ComputerUseTarget? target, string? json, ScreenCaptureBridge? screen, ComputerUseBridge? bridge)
    {
        if (target is null || screen is null || bridge is null)
            return new(ComputerRisk.Deny, "Input backend or verified target unavailable.");
        try
        {
            var match = target.Find(screen.Windows());
            if (match.Window is not { } window)
            {
                return new(ComputerRisk.Deny, "This window closed or changed owner. Observe another window of the authorized application.");
            }
            var userTurn = Sessions.Registry.Peek(id)?.Messages.LastOrDefault(t => t.Role == ChatRoles.User);
            if (_failedComputerInputs.TryGetValue(id, out var failed) && ReferenceEquals(failed.UserTurn, userTurn)
                && failed.Signature == InputSignature(json))
                return new(ComputerRisk.Deny, "[repeat-stopped] This input already failed in the current user turn. Do not ask for the same approval again; explain the failure and wait for user direction.");
            var request = ComputerInput.Parse(json);
            if (request is null) return new(ComputerRisk.Deny, "Invalid action JSON or parameter types.");
            if (ComputerInput.Validate(request) is { } invalid) return new(ComputerRisk.Deny, invalid);
            using var doc = JsonDocument.Parse(json!);
            var frame = doc.RootElement.TryGetProperty("frame_id", out var value) ? value.GetString() : null;
            if (!_computerObservations.TryGetValue(id, out var observation))
                return new(ComputerRisk.Deny, "[observation-missing] No live observation is registered. Capture this window again.");
            var mapped = observation.Map(frame, window, request, out var refusal);
            if (mapped is null) return new(ComputerRisk.Deny, "[observation-invalid] " + refusal);
            if (bridge.Preflight?.Invoke(window, mapped) is { } preflight)
                return new(ComputerRisk.Deny, preflight);
            var control = ComputerControlFor(id);
            var capabilities = control?.Capabilities ?? ComputerCapabilities.None;
            var decision = bridge.AssessRisk?.Invoke(window, mapped with
                { PermissionVersion = control?.Version ?? 0, Capabilities = capabilities })
                ?? ComputerRiskDecision.Unknown;
            return decision.Within(capabilities);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or UnauthorizedAccessException)
        { return ComputerRiskDecision.Unknown; }
    }

    private bool HasExplicitComputerPermit(string id, string? callId)
    {
        bool Read()
        {
            var control = ComputerControlFor(id);
            var call = Sessions.Registry.Peek(id)?.Messages.LastOrDefault(t => t.Role == ChatRoles.Assistant && t.ToolCallId == callId);
            return control?.Covers(call?.ApprovalComputerTarget) == true && callId is not null
                && _computerPermits.TryGetValue((id, callId), out var permit) && permit.Approved
                && ReferenceEquals(permit.Session, control) && permit.Version == control.Version
                && permit.Arguments == call!.ToolArguments;
        }
        return Dispatcher.UIThread.CheckAccess() ? Read()
            : Dispatcher.UIThread.InvokeAsync(Read).GetAwaiter().GetResult();
    }

    private ComputerUseObservation ComputerObservationFor(string id)
        => _computerObservations.GetOrAdd(id, _ => new ComputerUseObservation());

    private ComputerRiskDecision ComputerInputDecision(string id, string? callId)
    {
        ComputerRiskDecision Read()
        {
            if (callId is null) return new(ComputerRisk.Deny, "The current tool invocation has no call identity.");
            var call = Sessions.Registry.Peek(id)?.Messages.LastOrDefault(t => t.Role == ChatRoles.Assistant && t.ToolCallId == callId);
            if (call is null) return new(ComputerRisk.Deny, "The current tool call is absent from this conversation.");
            var control = ComputerControlFor(id);
            if (control is null) return new(ComputerRisk.Deny, "No live application-session permission. Request the application's session permission first.");
            if (control.Paused) return new(ComputerRisk.Deny, "Application control is paused. Resume it and observe again.");
            if (!control.Covers(call.ApprovalComputerTarget))
                return new(ComputerRisk.Deny, "This window's application is not covered by the live session permission.");
            // A saved transcript approval is not a live permit. Pause/revoke invalidates even approved actions.
            if (!_computerPermits.TryGetValue((id, callId), out var permit)
                || !ReferenceEquals(permit.Session, control) || permit.Version != control.Version
                || permit.Arguments != call.ToolArguments)
                return new(ComputerRisk.Deny, "No current host assessment for this action. Observe again and request a new action.");
            return permit.Approved ? permit.Decision with { Risk = ComputerRisk.Allow } : permit.Decision;
        }
        return Dispatcher.UIThread.CheckAccess() ? Read()
            : Dispatcher.UIThread.InvokeAsync(Read).GetAwaiter().GetResult();
    }
}
