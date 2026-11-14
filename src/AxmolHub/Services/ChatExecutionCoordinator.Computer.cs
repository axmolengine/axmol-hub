using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed partial class ChatExecutionCoordinator
{
    private readonly ConcurrentDictionary<string, ComputerControlSession> _computerControls = new();
    private sealed record InputPermit(ComputerControlSession Session, long Version, string Arguments, bool Approved);
    private readonly ConcurrentDictionary<(string Conversation, string Call), InputPermit> _computerPermits = new();

    internal ComputerControlSession? ComputerControlFor(string id)
        => _computerControls.TryGetValue(id, out var value) ? value : null;

    internal void PauseComputerControl(string id, bool paused)
    {
        if (ComputerControlFor(id) is not { } control) return;
        control.Pause(paused);
        if (_computerObservations.TryGetValue(id, out var observation)) observation.Invalidate();
        Audit(id, paused ? "Computer Use paused; observations invalidated." : "Computer Use resumed; fresh observation required.");
        Changed?.Invoke();
    }

    internal void RevokeComputerControl(string id)
    {
        if (_computerControls.TryRemove(id, out var control))
        {
            var version = control.Version;
            control.Revoke();
            Audit(id, "Computer Use application session permission revoked.");
            if (OperatingSystem.IsMacOS()) MacOsComputerUse.ForgetClipboard(version);
        }
        if (_computerObservations.TryRemove(id, out var observation)) observation.Invalidate();
        foreach (var key in _computerPermits.Keys.Where(key => key.Conversation == id)) _computerPermits.TryRemove(key, out _);
        Changed?.Invoke();
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
            if (request is null || ComputerInput.Validate(request) is not null)
                return new(ComputerRisk.Deny, "Invalid action arguments.");
            using var doc = JsonDocument.Parse(json!);
            var frame = doc.RootElement.TryGetProperty("frame_id", out var value) ? value.GetString() : null;
            var mapped = _computerObservations.TryGetValue(id, out var observation) ? observation.Map(frame, window, request) : null;
            if (mapped is null) return new(ComputerRisk.Deny, "A fresh frame and valid coordinates are required.");
            return bridge.AssessRisk?.Invoke(window, mapped with { PermissionVersion = ComputerControlFor(id)?.Version ?? 0 })
                ?? ComputerRiskDecision.Unknown;
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

    private bool ComputerInputAllowed(string id, string? callId, ScreenCaptureBridge? screen, ComputerUseBridge? bridge)
    {
        bool Read()
        {
            if (callId is null) return false;
            var call = Sessions.Registry.Peek(id)?.Messages.LastOrDefault(t => t.Role == ChatRoles.Assistant && t.ToolCallId == callId);
            var control = ComputerControlFor(id);
            if (control?.Covers(call?.ApprovalComputerTarget) != true) return false;
            // A saved transcript approval is not a live permit. Pause/revoke invalidates even approved actions.
            if (HasExplicitComputerPermit(id, callId)) return true;
            return AssessComputerInput(id, call?.ApprovalComputerTarget, call?.ToolArguments, screen, bridge).Risk == ComputerRisk.Allow;
        }
        return Dispatcher.UIThread.CheckAccess() ? Read()
            : Dispatcher.UIThread.InvokeAsync(Read).GetAwaiter().GetResult();
    }
}
