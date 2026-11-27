using System.Text;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent.Acp;

/// <summary>Observed workspace write performed via ACP <c>fs/write_text_file</c>.</summary>
public sealed record AcpFileWriteObservation(
    string Path,
    string Result,
    ChangeLineCounts? Lines,
    bool Failed);

/// <summary>
/// Handles ACP client filesystem callbacks (<c>fs/read_text_file</c>, <c>fs/write_text_file</c>)
/// directly on top of Hub's <see cref="WorkspacePaths"/> sandbox, <see cref="WorkspaceTools.FsWrite"/>,
/// <see cref="ChatChangeStore"/>, and <see cref="ChatUndoStore"/> — guaranteeing LF line endings,
/// protected-location enforcement, and full Changes / Diff / Undo tracking with zero duplicate I/O code.
/// </summary>
public sealed class AcpWorkspaceFileBridge
{
    private readonly Func<WorkspaceToolScope?> _workspaceProvider;
    private readonly Action<AcpFileWriteObservation>? _onFileWritten;

    public AcpWorkspaceFileBridge(
        WorkspaceToolScope workspace,
        Action<AcpFileWriteObservation>? onFileWritten = null)
        : this(() => workspace, onFileWritten)
    {
    }

    public AcpWorkspaceFileBridge(
        Func<WorkspaceToolScope?> workspaceProvider,
        Action<AcpFileWriteObservation>? onFileWritten = null)
    {
        _workspaceProvider = workspaceProvider;
        _onFileWritten = onFileWritten;
    }

    private WorkspaceToolScope RequireWorkspace()
        => _workspaceProvider() ?? throw new AcpRpcException(-32601, "No workspace is open for ACP filesystem access.");

    public object HandleReadTextFile(JsonElement @params)
    {
        var workspace = RequireWorkspace();
        var path = AcpParsers.TryString(@params, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new AcpRpcException(-32602, "fs/read_text_file requires a non-empty 'path'.");

        var resolved = WorkspacePaths.ResolveRead(workspace.Sandbox, path, workspace.Guards);
        if (!resolved.IsAllowed)
            throw new AcpRpcException(-32000, $"Read refused ({resolved.Verdict}): {resolved.Display}");

        if (!File.Exists(resolved.Full))
            throw new AcpRpcException(-32000, $"File not found: {resolved.Display}");

        var info = new FileInfo(resolved.Full);
        if (info.Length > WorkspaceTools.MaxReadFileBytes * 4L)
            throw new AcpRpcException(-32000, $"File exceeds ACP read size cap: {resolved.Display}");

        var bytes = File.ReadAllBytes(resolved.Full);
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            throw new AcpRpcException(-32000, $"Binary file cannot be read as UTF-8 text: {resolved.Display}");

        var text = TextEol.ToLf(Encoding.UTF8.GetString(bytes));
        var line = AcpParsers.TryInt(@params, "line");
        var limit = AcpParsers.TryInt(@params, "limit");

        if (line is > 0 || limit is > 0)
        {
            var lines = text.Split('\n');
            var start = Math.Clamp((line ?? 1) - 1, 0, lines.Length);
            var take = limit is > 0 ? Math.Min(limit.Value, lines.Length - start) : lines.Length - start;
            text = string.Join('\n', lines.Skip(start).Take(take));
        }

        return new { content = text };
    }

    public object HandleWriteTextFile(JsonElement @params)
    {
        var workspace = RequireWorkspace();
        var path = AcpParsers.TryString(@params, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new AcpRpcException(-32602, "fs/write_text_file requires a non-empty 'path'.");

        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("content", out var contentProp)
            || contentProp.ValueKind != JsonValueKind.String)
            throw new AcpRpcException(-32602, "fs/write_text_file requires a string 'content'.");

        var content = contentProp.GetString()!;
        ChangeLineCounts? capturedLines = null;
        var isolatedContext = workspace with
        {
            RecordChangeLines = lines => capturedLines = lines,
        };

        var tools = new WorkspaceTools(isolatedContext);
        var result = tools.FsWrite(path, content, overwrite: true);
        var failed = IsWriteRefusal(result);

        _onFileWritten?.Invoke(new AcpFileWriteObservation(path, result, capturedLines, failed));

        if (failed)
            throw new AcpRpcException(-32000, result);

        return new { };
    }

    private static bool IsWriteRefusal(string result)
        => result.StartsWith("Refused", StringComparison.OrdinalIgnoreCase)
           || result.StartsWith("Could not ", StringComparison.OrdinalIgnoreCase)
           || result.StartsWith("Cannot ", StringComparison.OrdinalIgnoreCase);
}
