namespace AxmolHub.Core;

/// <summary>A portal selection is an ephemeral user-selected stream, never an executable/application grant.</summary>
internal sealed record PortalComputerResult(CapturedFrame? Frame, string Detail);

internal sealed record PortalComputerRequest(string Application, string Action, string? FrameId,
    int X, int Y, string? Text, string? Key, int Scroll)
{
    internal Func<bool>? StillApproved { get; init; }
}

/// <summary>The host owns portal consent, stream lifetime and observations. Core owns call approval and storage.</summary>
internal sealed record PortalComputerBridge(
    Func<PortalComputerRequest, CancellationToken, Task<PortalComputerResult>> ExecuteAsync);
