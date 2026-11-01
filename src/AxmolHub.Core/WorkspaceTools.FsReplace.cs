using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace AxmolHub.Core;

/// <summary>One exact replacement in an fs_replace request.</summary>
public sealed record FsReplacement(
    [property: Description("Writable text file path.")] string path,
    [property: Description("Exact existing text, including indentation and line breaks.")] string old_string,
    [property: Description("Replacement text.")] string new_string,
    [property: Description("Replace all matches; otherwise multiple matches are refused.")] bool replace_all = false);

internal sealed record FsTextChange(WorkspacePath Path, byte[] BeforeBytes, string Before, string After);
internal sealed record FsReplacePlan(IReadOnlyList<FsTextChange> Changes, string? Error, string Fingerprint);

public sealed partial class WorkspaceTools
{
    private const int MaxFsEntries = 1000;
    private const long MaxFsBackupBytes = 256L * 1024 * 1024;

    /// <summary>Preflight every replacement before a file is modified.</summary>
    internal static FsReplacePlan PrepareReplacements(WorkspaceToolScope scope, IReadOnlyList<FsReplacement>? edits)
    {
        static FsReplacePlan Refuse(string message) => new([], message, "invalid");
        if (edits is null || edits.Count == 0) return Refuse("Refused: fs_replace needs at least one edit.");
        if (edits.Count > MaxFsEntries) return Refuse($"Refused: {edits.Count} edits exceed the {MaxFsEntries} entry limit.");
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var changes = new Dictionary<string, FsTextChange>(comparer);
        long bytesRead = 0;
        long outputBudget = 0;
        for (var index = 0; index < edits.Count; index++)
        {
            var item = edits[index];
            if (item is null || string.IsNullOrEmpty(item.old_string))
                return Refuse("Refused: every fs_replace edit requires a nonempty old_string.");
            // Named by position because a batch can hold several edits: an edit with no file is the model's own
            // argument to fix, and "edit 3 of 5" is the only handle on it when there is no path to quote.
            if (string.IsNullOrWhiteSpace(item.path))
                return Refuse($"Refused: edit {index + 1} of {edits.Count} names no file — every edit needs its own "
                              + "'path', the file that edit belongs to. Do not retry with an empty path.");
            var path = WorkspacePaths.ResolveWrite(scope.Sandbox, item.path, scope.Guards);
            if (!path.IsAllowed) return Refuse(WorkspacePaths.ResultFor(path.Verdict, path.Display));
            if (!changes.TryGetValue(path.Full, out var current))
            {
                try
                {
                    var info = new FileInfo(path.Full);
                    if (!info.Exists) return Refuse($"Refused: {path.Display} does not exist; use fs_write to create it.");
                    if (info.Length > MaxFsBackupBytes - bytesRead)
                        return Refuse($"Refused: the original files contain at least {bytesRead + info.Length} bytes, exceeding the {MaxFsBackupBytes} byte backup limit.");
                    var bytes = ReadBoundedBytes(path.Full, MaxFsBackupBytes - bytesRead);
                    bytesRead += bytes.Length;
                    if (bytesRead > MaxFsBackupBytes) return Refuse("Refused: the files grew beyond the backup limit.");
                    using var stream = new MemoryStream(bytes);
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                    var text = reader.ReadToEnd();
                    current = new FsTextChange(path, bytes, text, text);
                    outputBudget += Encoding.UTF8.GetByteCount(TextEol.ToLf(text));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                { return Refuse($"Could not read {path.Display}: {ex.Message}"); }
            }
            var baseline = TextEol.ToLf(current.After);
            var anchor = TextEol.ToLf(item.old_string);
            var replacement = TextEol.ToLf(item.new_string ?? "");
            var matches = FileEdit.CountMatches(baseline, anchor);
            var replacements = item.replace_all ? matches : Math.Min(matches, 1);
            var currentBytes = Encoding.UTF8.GetByteCount(baseline);
            var outputBytes = (long)currentBytes
                              + ((long)Encoding.UTF8.GetByteCount(replacement) - Encoding.UTF8.GetByteCount(anchor)) * replacements;
            if (outputBudget - currentBytes + outputBytes > MaxFsBackupBytes)
                return Refuse("Refused: the combined replacement output exceeds the 256 MiB limit.");
            var edit = FileEdit.Apply(current.After, item.old_string, item.new_string ?? "", item.replace_all);
            if (edit.Verdict is not (FileEditVerdict.Applied or FileEditVerdict.Unchanged))
                return Refuse(FileEdit.ResultFor(edit.Verdict, edit.Matches, path.Display));
            outputBudget += outputBytes - currentBytes;
            changes[path.Full] = current with { After = edit.Updated };
        }

        var ordered = changes.Values.OrderBy(change => change.Path.Full, comparer).ToArray();
        try
        {
            var identity = string.Join("\n", ordered.Select(change => Fingerprint(change.Path.Full, change.BeforeBytes)));
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return new(ordered.Where(change => change.Before != change.After).ToArray(), null, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Refuse($"Could not inspect files: {ex.Message}"); }
    }

    [Description("Apply exact replacements to one or more existing text files. Every edit is checked before writing.")]
    public string FsReplace([Description("Exact replacements in their application order.")] IReadOnlyList<FsReplacement> edits)
    {
        using var gate = context.TakeChangeGate?.Invoke(ToolNames.FsReplace);
        if (context.TakeChangeGate is not null && gate is null) return ToolApprovalResults.Busy;
        var initial = PrepareReplacements(context, edits);
        if (initial.Error is not null) return initial.Error;
        var leases = new List<(FsTextChange Change, TextFile.WriteLease Lease)>();
        var revisions = new List<ChatFileRevision>();
        var landed = new List<int>();
        ChatChangeBatch? batch = null;
        IDisposable? batchLease = null;
        FsReplacePlan? plan = null;
        string Fail(string message)
        {
            var failedRollback = new List<int>();
            if (plan is not null)
            {
                foreach (var j in landed.AsEnumerable().Reverse())
                {
                    var previous = plan.Changes[j];
                    var lease = leases.First(pair => pair.Change.Path.Full == previous.Path.Full).Lease;
                    var afterBytes = new UTF8Encoding(false, true).GetBytes(TextEol.ToLf(previous.After));
                    var undo = lease.ReplaceRaw(
                        () => WorkspacePaths.ResolveWrite(context.Sandbox, previous.Path.Full, context.Guards),
                        afterBytes, previous.BeforeBytes);
                    if (undo.Verdict != TextReplaceVerdict.Written) failedRollback.Add(j);
                }
            }
            for (var j = 0; j < revisions.Count; j++)
                if (!failedRollback.Contains(j)) ChatChangeStore.Abandon(context.DataRoot, revisions[j]);
            if (batch is not null)
            {
                try
                {
                    batch = batch with { AppliedIds = failedRollback.Select(j => revisions[j].Id).ToArray() };
                    ChatChangeStore.CompleteBatch(context.DataRoot, batch, failedRollback.Count == 0 ? "rolled-back" : "partial");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { message += $" The batch journal could not be finalized: {ex.Message}."; }
            }
            if (failedRollback.Count == 0) return message + " Completed writes were restored.";
            return message + " Rollback failed for "
                   + string.Join(", ", failedRollback.Select(j => plan!.Changes[j].Path.Display))
                   + "; inspect the change journal."
                   + (batch is null ? "" : ChatChangeStore.BatchMarker + batch.Id);
        }
        try
        {
            foreach (var change in initial.Changes)
                leases.Add((change, TextFile.AcquireWrite(change.Path.Full)));
            plan = PrepareReplacements(context, edits);
            if (plan.Error is not null) return plan.Error;
            if (plan.Fingerprint != initial.Fingerprint
                || context.ApprovedWriteFingerprint is { } expected && expected != plan.Fingerprint)
                return "Refused: one or more files changed since the operation was prepared. Read them again.";
            if (plan.Changes.Count == 0) return "No change: all replacements leave the files unchanged.";
            foreach (var change in plan.Changes)
                revisions.Add(ChatChangeStore.Prepare(context.DataRoot, context.ConversationId, change.Path.Full,
                    change.Path.Root, change.Before, change.After, change.BeforeBytes));
            batch = ChatChangeStore.PrepareBatch(context.DataRoot, context.ConversationId, revisions, out batchLease);

            for (var i = 0; i < plan.Changes.Count; i++)
            {
                var change = plan.Changes[i];
                var lease = leases.First(pair => pair.Change.Path.Full == change.Path.Full).Lease;
                var result = lease.Replace(() => WorkspacePaths.ResolveWrite(context.Sandbox, change.Path.Full, context.Guards),
                    change.BeforeBytes, change.After);
                if (result.Verdict != TextReplaceVerdict.Written)
                    return Fail($"Refused: the batch failed for {change.Path.Display} ({result.Verdict}).");
                landed.Add(i);
                batch = ChatChangeStore.RecordBatchProgress(context.DataRoot, batch, revisions[i].Id);
                ChatChangeStore.CompleteRevision(context.DataRoot, revisions[i]);
            }
            ChatChangeStore.CompleteBatch(context.DataRoot, batch, ChatChangeStore.Complete);
            context.RecordChangeLines?.Invoke(ChangeLineCounts.Sum(revisions.Select(revision => revision.Lines)));
            return $"Edited {plan.Changes.Count} file(s)." + ChatChangeStore.BatchMarker + batch.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail($"Batch failed: {ex.Message}.");
        }
        finally
        {
            if (batchLease is not null && context.HoldBatchLease is { } hold && ToolInvocation.CallId is { } callId)
            {
                hold(callId, batchLease);
                batchLease = null;
            }
            batchLease?.Dispose();
            foreach (var (_, lease) in leases.AsEnumerable().Reverse()) lease.Dispose();
        }
    }
}
