using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Unsaved manual buffers belong to a conversation, not to disposable inspector rows.</summary>
internal sealed class ChangesEditSession
{
    private static readonly HashSet<ChangesEditSession> Active = [];
    internal readonly Dictionary<string, ChangesEditBuffer> Buffers = new(StringComparer.Ordinal);
    internal bool Editing { get; private set; }
    internal bool IsDirty => Buffers.Values.Any(buffer => buffer.IsDirty);
    internal static int DirtyCount => Active.Count(session => session.IsDirty);

    internal void Begin()
    {
        Editing = true;
        Active.Add(this);
    }

    internal void Cancel()
    {
        Buffers.Clear();
        Editing = false;
        Active.Remove(this);
    }

    internal static string? SaveAll()
    {
        foreach (var session in Active.ToArray())
            if (session.IsDirty && session.Save() is { } error) return error;
        return null;
    }

    internal static void DiscardAll()
    {
        foreach (var session in Active.ToArray()) session.Cancel();
    }

    internal string? Save()
    {
        foreach (var (path, buffer) in Buffers.ToArray())
        {
            if (!buffer.IsDirty) continue;
            var result = buffer.Save();
            if (result.Verdict is not (TextReplaceVerdict.Written or TextReplaceVerdict.Unchanged))
                return path + ": " + (result.Detail ?? result.Verdict.ToString());
        }
        Cancel();
        return null;
    }
}

/// <summary>The disk identity and operation-time permission check used by an Edit diff buffer.</summary>
internal sealed class ChangesEditBuffer
{
    private readonly Func<WorkspacePath> _resolveWrite;
    private TextFileSnapshot _baseline;
    internal string Path { get; }
    internal string Text { get; set; }
    internal bool IsDirty => Text != TextEol.ToLf(_baseline.Text);

    private ChangesEditBuffer(string path, TextFileSnapshot baseline, Func<WorkspacePath> resolveWrite)
    {
        Path = path;
        Text = TextEol.ToLf(baseline.Text);
        _baseline = baseline;
        _resolveWrite = resolveWrite;
    }

    internal static ChangesEditBuffer? Open(string path, Func<WorkspaceToolScope> scopeFor)
    {
        var scope = scopeFor();
        var read = WorkspacePaths.ResolveRead(scope.Sandbox, path, scope.Guards);
        var write = WorkspacePaths.ResolveWrite(scope.Sandbox, path, scope.Guards);
        if (!read.IsAllowed || !write.IsAllowed || read.Full != write.Full
            || !WorkspacePaths.IsWritableText(System.IO.Path.GetFileName(read.Full))) return null;
        var baseline = TextFile.Read(read.Full, TextFile.MaxDiffBytes);
        if (baseline.State != TextFileState.Text) return null;
        return new ChangesEditBuffer(read.Full, baseline, () =>
        {
            var current = scopeFor();
            return WorkspacePaths.ResolveWrite(current.Sandbox, path, current.Guards);
        });
    }

    internal TextReplaceResult Save()
    {
        using var lease = TextFile.AcquireWrite(Path);
        var result = lease.Save(_resolveWrite, _baseline, IsDirty ? Text : _baseline.Text);
        if (result.Verdict is TextReplaceVerdict.Written or TextReplaceVerdict.Unchanged)
        {
            var saved = TextFile.Read(Path, TextFile.MaxDiffBytes);
            if (saved.State != TextFileState.Text || TextEol.ToLf(saved.Text) != Text)
                return new TextReplaceResult(TextReplaceVerdict.ChangedSince,
                    "The file changed again after saving; the manual buffer has been retained.");
            // A successful file in a partially failed batch is a new save point, never written twice.
            _baseline = saved;
        }
        return result;
    }
}
