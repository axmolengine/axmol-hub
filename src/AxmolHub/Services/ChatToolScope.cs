using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// Everything one request's tools are drawn from, assembled on the UI thread when the request is built.
///
/// Assembled rather than read on demand because a tool call can be answered long after the request that offered
/// it — after a window closed, even. The values here are the ones the approval card was shown with, so approving
/// it later acts on the same directory, the same guards and the same secrets list.
/// </summary>
internal sealed record ChatToolScope(
    ChatWorkspace.HubReadOnlySnapshot? Snapshot,
    WorkspaceToolScope Workspace)
{
    public static ChatToolScope Empty { get; } = new(null, WorkspaceToolScope.Empty);
}
