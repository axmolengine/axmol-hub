using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed partial class ChatExecutionCoordinator
{
    private readonly ConcurrentDictionary<string, ComputerControlSession> _computerControls = new();
    private sealed record InputPermit(ComputerControlSession Session, long Version, string Arguments,
        ComputerRiskDecision Decision, bool Approved);
    private readonly ConcurrentDictionary<(string Conversation, string Call), InputPermit> _computerPermits = new();

    internal ComputerControlSession? ComputerControlFor(string id)
        => _computerControls.TryGetValue(id, out var value) ? value : null;

    internal void PauseComputerControl(string id, bool paused)
    {
        if (ComputerControlFor(id) is not { } control) return;
        control.Pause(paused);
        foreach (var key in _computerPermits.Keys.Where(key => key.Conversation == id)) ReleaseComputerPermit(key);
        if (_computerObservations.TryGetValue(id, out var observation)) observation.Invalidate();
        Audit(id, paused ? "Computer Use paused; observations invalidated." : "Computer Use resumed; fresh observation required.");
        Changed?.Invoke();
    }

    internal void RevokeComputerControl(string id)
    {
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

    private void GrantComputerControl(string id, ComputerUseTarget target)
    {
        RevokeComputerControl(id);
        var conversation = Sessions.Registry.Peek(id);
        var task = conversation?.Messages.LastOrDefault(t => t.Role == ChatRoles.User)?.Text ?? "GUI task";
        _computerControls[id] = new(target, task);
        Audit(id, $"Computer Use application session permission granted · initial window {target.WindowHandle} · pid {target.ProcessId} · {target.Application.ExecutablePath}");
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
            var request = ComputerInput.Parse(json);
            if (request is null) return new(ComputerRisk.Deny, "Invalid action JSON or parameter types.");
            if (ComputerInput.Validate(request) is { } invalid) return new(ComputerRisk.Deny, invalid);
            using var doc = JsonDocument.Parse(json!);
            var frame = doc.RootElement.TryGetProperty("frame_id", out var value) ? value.GetString() : null;
            if (!_computerObservations.TryGetValue(id, out var observation))
                return new(ComputerRisk.Deny, "No live observation is registered. Capture this window again.");
            var mapped = observation.Map(frame, window, request, out var refusal);
            if (mapped is null) return new(ComputerRisk.Deny, refusal!);
            var decision = bridge.AssessRisk?.Invoke(window, mapped with { PermissionVersion = ComputerControlFor(id)?.Version ?? 0 })
                ?? ComputerRiskDecision.Unknown;
            var capabilities = ComputerControlFor(id)?.Capabilities ?? ComputerCapabilities.None;
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
