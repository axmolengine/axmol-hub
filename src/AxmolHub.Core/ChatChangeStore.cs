using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>A complete, immutable file revision. Null BeforeHash means the file did not exist; the hash of an
/// empty file is a real value and must not be confused with absence.</summary>
public sealed record ChatFileRevision(
    string Id,
    string ConversationId,
    string Path,
    string Root,
    DateTimeOffset At,
    string? BeforeHash,
    string? AfterHash,
    string State,
    ChangeLineCounts? Lines = null,
    string? BeforeRawHash = null);

public sealed record ChatFileSnapshot(string Before, string After);

/// <summary>One file a write call produced. The counts ride along because one call can touch several files:
/// the turn's own total would be the sum, which belongs to no single row.</summary>
public sealed record ChatChangeReference(string Path, string RevisionId, ChangeLineCounts? Lines = null);

public sealed record FsManagedEntry(string RelativePath, bool Directory, string? ContentHash, long Length,
    int Attributes, int? UnixMode);

public sealed record FsManagementRecord(string Operation, string Source, string? Destination,
    IReadOnlyList<FsManagedEntry> Entries, IReadOnlyList<string> CreatedParents, string? TemporaryPath = null);

/// <summary>A durable list of the file revisions prepared for one multi-file tool call.</summary>
public sealed record ChatChangeBatch(string Id, string ConversationId, IReadOnlyList<string> RevisionIds,
    IReadOnlyList<string> AppliedIds, string State, string? ToolCallId = null, FsManagementRecord? Management = null,
    IReadOnlyDictionary<string, string[]>? UndoProgress = null);

/// <summary>Durable content and a write journal for each successful assistant file edit. Pending records are
/// written before touching source; a crash between the source rename and journal completion can be recovered by
/// comparing the source with the recorded before and after hashes.</summary>
public static class ChatChangeStore
{
    public const string Marker = " Change ID: ";
    public const string BatchMarker = " Change batch: ";
    public const string Pending = "pending";
    public const string Complete = "complete";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string? DirectoryFor(string? dataRoot) => string.IsNullOrWhiteSpace(dataRoot)
        ? null : System.IO.Path.Combine(dataRoot, "ai", "changes");

    /// <summary>The path a row shows: relative to the workspace the write happened in. A revision keeps the
    /// absolute path because that is what a restore resolves, but a list that spelled it out would print the whole
    /// checkout in every row and in the undo notice.</summary>
    public static string RelativeToRoot(string path, string root)
        => !System.IO.Path.IsPathRooted(path) || string.IsNullOrEmpty(root)
            ? path : System.IO.Path.GetRelativePath(root, path);

    public static string? IdFromResult(string? result)
    {
        if (result?.IndexOf(Marker, StringComparison.Ordinal) is not { } at || at < 0) return null;
        var value = result[(at + Marker.Length)..].Split([' ', '\r', '\n'], 2)[0];
        return Guid.TryParseExact(value, "N", out _) ? value : null;
    }

    public static string? BatchIdFromResult(string? result)
    {
        if (result?.IndexOf(BatchMarker, StringComparison.Ordinal) is not { } at || at < 0) return null;
        var value = result[(at + BatchMarker.Length)..].Split([' ', '\r', '\n'], 2)[0];
        return Guid.TryParseExact(value, "N", out _) ? value : null;
    }

    public static ChatChangeBatch PrepareBatch(string? dataRoot, string conversationId,
        IReadOnlyList<ChatFileRevision> revisions, out IDisposable lease, FsManagementRecord? management = null)
    {
        var batch = new ChatChangeBatch(Guid.NewGuid().ToString("N"), conversationId,
            revisions.Select(revision => revision.Id).ToArray(), [], Pending, ToolInvocation.CallId, management);
        var root = DirectoryFor(dataRoot) ?? throw new IOException("Hub data directory is unavailable.");
        var lockPath = BatchLockPath(root, batch.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(lockPath)!);
        var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            WriteBatch(dataRoot, batch);
            lease = stream;
            return batch;
        }
        catch { stream.Dispose(); throw; }
    }

    public static bool BatchIsActive(string? dataRoot, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || DirectoryFor(dataRoot) is not { } root) return true;
        try
        {
            using var stream = new FileStream(BatchLockPath(root, id), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    public static ChatChangeBatch RecordBatchProgress(string? dataRoot, ChatChangeBatch batch, string revisionId)
    {
        var next = batch with { AppliedIds = [.. batch.AppliedIds, revisionId] };
        WriteBatch(dataRoot, next);
        return next;
    }

    public static void CompleteBatch(string? dataRoot, ChatChangeBatch batch, string state)
        => WriteBatch(dataRoot, batch with { State = state });

    public static ChatChangeBatch RecordBatchUndo(string? dataRoot, ChatChangeBatch batch, string conversationId, string relativePath)
    {
        var progress = batch.UndoProgress?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
                       ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        progress[conversationId] = [.. progress.GetValueOrDefault(conversationId) ?? [], relativePath];
        var next = batch with { UndoProgress = progress };
        WriteBatch(dataRoot, next);
        return next;
    }

    public static ChatChangeBatch? ReadBatch(string? dataRoot, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || DirectoryFor(dataRoot) is not { } root) return null;
        try
        {
            var batch = JsonSerializer.Deserialize<ChatChangeBatch>(File.ReadAllBytes(BatchPath(root, id)), StateStore.ReadOptions);
            return batch?.Id == id && batch.RevisionIds is not null && batch.AppliedIds is not null
                   && (batch.Management is null || batch.Management.Entries is not null && batch.Management.CreatedParents is not null)
                ? batch : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static IReadOnlyList<ChatChangeBatch> BatchesForConversation(string? dataRoot, string conversationId)
        => Batches(dataRoot).Where(batch => batch.ConversationId == conversationId).ToArray();

    public static IReadOnlyList<ChatChangeBatch> Batches(string? dataRoot)
    {
        if (DirectoryFor(dataRoot) is not { } root) return [];
        var directory = System.IO.Path.Combine(root, "batches");
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateFiles(directory, "*.json")
                .Select(file => ReadBatch(dataRoot, System.IO.Path.GetFileNameWithoutExtension(file)))
                .Where(batch => batch is not null).Cast<ChatChangeBatch>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static string SaveRawObject(string? dataRoot, byte[] bytes)
        => DirectoryFor(dataRoot) is { } root ? SaveBytes(root, bytes)
            : throw new IOException("Hub data directory is unavailable.");

    public static byte[]? ReadRawObject(string? dataRoot, string hash) => ReadBytes(dataRoot, hash);

    /// <summary>Attach interrupted batch journals to their unanswered calls when a host starts.</summary>
    public static int RecoverBatchCalls(string? dataRoot, Conversation conversation, IReadOnlyList<ChatChangeBatch> batches,
        WorkspaceSandbox sandbox, WorkspaceGuards guards)
    {
        var answered = conversation.Messages.Where(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 })
            .Select(turn => turn.ToolCallId!).ToHashSet(StringComparer.Ordinal);
        var recovered = 0;
        foreach (var group in batches.Where(batch => batch.ConversationId == conversation.Id && batch.ToolCallId is { Length: > 0 }
                                                    && !BatchIsActive(dataRoot, batch.Id))
                     .GroupBy(batch => batch.ToolCallId!, StringComparer.Ordinal))
        {
            if (answered.Contains(group.Key)) continue;
            var index = conversation.IndexOfToolCall(group.Key);
            if (index < 0) continue;
            var call = conversation.Messages[index];
            if (ChatChanges.IsManagement(call.ToolName))
            {
                var managementBatch = group.FirstOrDefault(batch => batch.Management is not null);
                if (managementBatch?.Management is not { } operation) continue;
                // A crash can leave a durable progress record without a terminal state. Expose
                // only recorded effects for guarded undo; the result still reports uncertainty.
                if (managementBatch.State == "pending" && managementBatch.AppliedIds.Count > 0)
                {
                    try
                    {
                        CompleteBatch(dataRoot, managementBatch, "partial");
                        managementBatch = managementBatch with { State = "partial" };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                var knownChange = managementBatch.State == Complete || managementBatch.State == "partial";
                if (knownChange)
                {
                    var entries = managementBatch.State == Complete || operation.Operation == "move"
                        ? operation.Entries : operation.Entries.Where(entry => managementBatch.AppliedIds.Contains(entry.RelativePath,
                            StringComparer.Ordinal)).ToArray();
                    conversation.Messages[index] = call with
                    {
                        ManagementBatchId = managementBatch.Id, ManagementSource = operation.Source,
                        ManagementDestination = operation.Destination, ManagedEntries = entries,
                    };
                }
                var complete = group.Count() == 1 && managementBatch.State == Complete;
                conversation.AppendFunctionResult(ChatTurn.FunctionResult(group.Key,
                    complete ? "The filesystem operation completed; its journal was recovered after interruption."
                        : managementBatch.State == "rolled-back" ? "The interrupted filesystem operation was rolled back."
                        : $"The filesystem operation was interrupted. Inspect {operation.Source} and its journal {managementBatch.Id} before retrying.",
                    failed: !complete));
                answered.Add(group.Key);
                recovered++;
                continue;
            }
            if (call.ToolName != ToolNames.FsReplace) continue;
            var records = group.SelectMany(batch => batch.RevisionIds).Distinct(StringComparer.Ordinal)
                .Select(id => ReadMetadata(dataRoot, id))
                .Where(record => record is not null && record.ConversationId == conversation.Id
                    && WorkspacePaths.ResolveWrite(sandbox, record.Path, guards) is { IsAllowed: true } resolved
                    && resolved.Full == record.Path)
                .Select(record => Read(dataRoot, record!.Id))
                .Where(record => record is { State: Complete }).Cast<ChatFileRevision>().ToArray();
            var references = records.Select(record => new ChatChangeReference(
                RelativeToRoot(record.Path, record.Root), record.Id, record.Lines)).ToArray();
            var turn = conversation.Messages[index];
            conversation.Messages[index] = turn with
            {
                ChangeId = references.FirstOrDefault()?.RevisionId,
                ChangeRefs = references,
                ChangeLines = references.Length == 0 ? null : ChangeLineCounts.Sum(records.Select(record => record.Lines)),
            };
            foreach (var reference in references)
                if (!conversation.ChangeHistoryIds.Contains(reference.RevisionId, StringComparer.Ordinal))
                    conversation.ChangeHistoryIds.Add(reference.RevisionId);
            var completed = group.Count() == 1 && group.First().State == Complete
                            && references.Length == group.First().RevisionIds.Count;
            var result = completed
                ? $"Edited {references.Length} file(s); recovered the completed change journal after interruption."
                : $"The batch was interrupted. {references.Length} recorded file change(s) may need review; inspect the change journal before retrying.";
            conversation.AppendFunctionResult(ChatTurn.FunctionResult(group.Key, result, failed: !completed));
            answered.Add(group.Key);
            recovered++;
        }
        return recovered;
    }

    private static void WriteBatch(string? dataRoot, ChatChangeBatch batch)
    {
        var root = DirectoryFor(dataRoot) ?? throw new IOException("Hub data directory is unavailable.");
        var path = BatchPath(root, batch.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(TextEol.ToLf(JsonSerializer.Serialize(batch, Json)) + "\n");
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string BatchPath(string root, string id) => System.IO.Path.Combine(root, "batches", id + ".json");
    private static string BatchLockPath(string root, string id) => System.IO.Path.Combine(root, "batches", id + ".lock");

    public static ChatFileRevision? Read(string? dataRoot, string id)
    {
        var record = ReadMetadata(dataRoot, id);
        return record is { State: Pending } && RecoverState(dataRoot, record) == Complete
            ? record with { State = Complete }
            : record;
    }

    private static ChatFileRevision? ReadMetadata(string? dataRoot, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || DirectoryFor(dataRoot) is not { } root) return null;
        try
        {
            var record = JsonSerializer.Deserialize<ChatFileRevision>(File.ReadAllText(RecordPath(root, id)), StateStore.ReadOptions);
            if (record is null || !string.Equals(record.Id, id, StringComparison.Ordinal)) return null;
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<ChatFileRevision> ForConversation(string? dataRoot, string conversationId)
    {
        if (DirectoryFor(dataRoot) is not { } root || string.IsNullOrWhiteSpace(conversationId)) return [];
        var records = new List<ChatFileRevision>();
        try
        {
            var directory = System.IO.Path.Combine(root, "records");
            if (!Directory.Exists(directory)) return records;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(file);
                if (Read(dataRoot, id) is { } record && record.ConversationId == conversationId)
                    records.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return records.OrderBy(record => record.At).ToArray();
    }

    public static string? ReadContent(string? dataRoot, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || DirectoryFor(dataRoot) is not { } root)
            return null;
        try
        {
            var bytes = File.ReadAllBytes(ObjectPath(root, hash));
            if (!string.Equals(Hash(bytes), hash, StringComparison.OrdinalIgnoreCase)) return null;
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    public static ChatFileSnapshot? ReadSnapshot(string? dataRoot, string id, out UndoCopyState state)
    {
        state = UndoCopyState.MissingSnapshot;
        var revision = Read(dataRoot, id);
        if (revision is null) return null;
        if (revision.State != Complete)
        {
            state = UndoCopyState.PendingSnapshot;
            return null;
        }
        var before = revision.BeforeHash is null ? "" : ReadContent(dataRoot, revision.BeforeHash);
        var after = revision.AfterHash is null ? "" : ReadContent(dataRoot, revision.AfterHash);
        if ((revision.BeforeHash is not null && before is null)
            || (revision.AfterHash is not null && after is null))
        {
            state = UndoCopyState.Unreadable;
            return null;
        }
        state = UndoCopyState.Read;
        return new ChatFileSnapshot(before!, after!);
    }

    /// <summary>
    /// The net snapshot of one file's chain of writes: the pre-image that opened it and the post-image that closed
    /// it. Two writes to one file in one turn are one before and one after, not two diffs added together — a line
    /// the turn added and then removed again is not part of what the turn did.
    ///
    /// <para>State comes from the closing revision, because that is the one whose content has to be readable for
    /// the pair to mean anything. A chain whose ends belong to different files is not a chain: it answers null
    /// rather than drawing a diff across two paths.</para>
    /// </summary>
    public static ChatFileSnapshot? ReadChainSnapshot(string? dataRoot, string firstId, string lastId,
        out UndoCopyState state)
    {
        state = UndoCopyState.MissingSnapshot;
        if (Read(dataRoot, firstId) is not { } first || Read(dataRoot, lastId) is not { } last) return null;
        if (!string.Equals(first.Path, last.Path, StringComparison.Ordinal)) return null;
        if (first.State != Complete || last.State != Complete)
        {
            state = UndoCopyState.PendingSnapshot;
            return null;
        }
        var before = first.BeforeHash is null ? "" : ReadContent(dataRoot, first.BeforeHash);
        var after = last.AfterHash is null ? "" : ReadContent(dataRoot, last.AfterHash);
        if ((first.BeforeHash is not null && before is null) || (last.AfterHash is not null && after is null))
        {
            state = UndoCopyState.Unreadable;
            return null;
        }
        state = UndoCopyState.Read;
        return new ChatFileSnapshot(before!, after!);
    }

    /// <summary>Prepare both full LF snapshots and the pending record. Throws when any durable prerequisite fails;
    /// callers must leave the source untouched on that road.</summary>
    public static ChatFileRevision Prepare(string? dataRoot, string conversationId, string path, string rootPath,
        string? before, string? after, byte[]? beforeBytes = null)
    {
        if (DirectoryFor(dataRoot) is not { } root || string.IsNullOrWhiteSpace(conversationId))
            throw new IOException("Hub has no data directory or conversation for a durable change snapshot.");

        var beforeHash = before is null ? null : SaveContent(root, TextEol.ToLf(before));
        var afterHash = after is null ? null : SaveContent(root, TextEol.ToLf(after));
        var record = new ChatFileRevision(Guid.NewGuid().ToString("N"), conversationId,
            System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(rootPath), DateTimeOffset.UtcNow,
            beforeHash, afterHash, Pending, FileDiff.CountChangedLines(before, after),
            beforeBytes is null ? null : SaveBytes(root, beforeBytes));
        WriteRecord(root, record);
        return record;
    }

    public static void CompleteRevision(string? dataRoot, ChatFileRevision record)
    {
        var root = DirectoryFor(dataRoot) ?? throw new IOException("Hub data directory is unavailable.");
        WriteRecord(root, record with { State = Complete });
    }

    /// <summary>A failed or conflicted source write has no successful revision. Deletion is best effort; a
    /// surviving pending record still cannot masquerade as a completed edit.</summary>
    public static void Abandon(string? dataRoot, ChatFileRevision record)
    {
        if (DirectoryFor(dataRoot) is not { } root) return;
        try { File.Delete(RecordPath(root, record.Id)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Resolve an interrupted journal without guessing. A source matching the after snapshot proves
    /// that the replacement landed. A source matching the before snapshot may also be a later user reversal, so
    /// any other state remains pending for inspection rather than being declared a failed write.</summary>
    public static string RecoverState(string? dataRoot, ChatFileRevision record)
    {
        if (record.State == Complete) return Complete;
        try
        {
            var current = File.Exists(record.Path) ? WorkspaceTools.ReadAll(record.Path) : null;
            if (current is null && File.Exists(record.Path)) return Pending;
            var hash = current is null ? null : Hash(Encoding.UTF8.GetBytes(TextEol.ToLf(current)));
            if (hash == record.AfterHash)
            {
                CompleteRevision(dataRoot, record);
                return Complete;
            }
            return Pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Pending;
        }
    }

    public static UndoVerdict Restore(string? dataRoot, string conversationId, string id,
        WorkspaceSandbox sandbox, WorkspaceGuards guards, out string path, out string? inverseId)
    {
        path = "";
        inverseId = null;
        if (Read(dataRoot, id) is not { } revision)
            return UndoVerdict.CopyMissing;
        return RestoreRange(dataRoot, conversationId, revision, revision, sandbox, guards, out path, out inverseId);
    }

    /// <summary>
    /// Undo one file's whole chain of writes inside one turn: <paramref name="firstId"/> opened the chain and
    /// <paramref name="lastId"/> closed it. The guard is still the last write's after-image — the file has to hold
    /// exactly what the turn left there — while what goes back is the first write's pre-image, the only state the
    /// turn as a whole may claim to undo.
    ///
    /// <para>Undoing the last revision instead would revert one edit of a sequence and leave the file in a state
    /// nobody produced, which is why the pair is the unit. A chain that spans two turns is two calls of this, in
    /// reverse order, and each one's guard is what makes that safe.</para>
    /// </summary>
    public static UndoVerdict RestoreChain(string? dataRoot, string conversationId, string firstId, string lastId,
        WorkspaceSandbox sandbox, WorkspaceGuards guards, out string path, out string? inverseId)
    {
        path = "";
        inverseId = null;
        if (Read(dataRoot, firstId) is not { } first) return UndoVerdict.CopyMissing;
        if (Read(dataRoot, lastId) is not { } last) return UndoVerdict.CopyMissing;
        if (!string.Equals(first.Path, last.Path, StringComparison.Ordinal)) return UndoVerdict.CopyMissing;
        return RestoreRange(dataRoot, conversationId, first, last, sandbox, guards, out path, out inverseId);
    }

    private static UndoVerdict RestoreRange(string? dataRoot, string conversationId,
        ChatFileRevision first, ChatFileRevision last, WorkspaceSandbox sandbox, WorkspaceGuards guards,
        out string path, out string? inverseId)
    {
        path = "";
        inverseId = null;
        if (last.AfterHash is null) return UndoVerdict.CopyMissing;

        var resolved = WorkspacePaths.ResolveWrite(sandbox, last.Path, guards);
        if (!resolved.IsAllowed) return UndoVerdict.RefusedPath;
        path = resolved.Display;
        using var write = TextFile.AcquireWrite(resolved.Full);
        var permitted = WorkspacePaths.ResolveWrite(sandbox, last.Path, guards);
        if (!permitted.IsAllowed || permitted.Full != resolved.Full) return UndoVerdict.RefusedPath;
        var baseline = TextFile.Read(resolved.Full, TextFile.MaxWriteBytes);
        if (ChatUndoStore.TextRefusal(baseline.State) is { } targetError) return targetError;
        var current = baseline.Text;
        if (Hash(Encoding.UTF8.GetBytes(TextEol.ToLf(current))) != last.AfterHash)
            return UndoVerdict.ChangedSince;

        // Restore exactly the validated object. Older revisions without raw bytes kept UTF-8/LF instead.
        TextFileSnapshot? preImage = null;
        if ((first.BeforeRawHash ?? first.BeforeHash) is { } hash)
        {
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || DirectoryFor(dataRoot) is not { } root)
                return UndoVerdict.CopyMissing;
            preImage = TextFile.Read(ObjectPath(root, hash), TextFile.MaxWriteBytes);
            if (preImage.State == TextFileState.Missing) return UndoVerdict.CopyMissing;
            if (ChatUndoStore.TextRefusal(preImage.State) is { } copyError) return copyError;
            if (!string.Equals(hash, preImage.Identity, StringComparison.OrdinalIgnoreCase)
                || first.BeforeRawHash is not null && Hash(Encoding.UTF8.GetBytes(TextEol.ToLf(preImage.Text))) != first.BeforeHash)
                return UndoVerdict.CopyMissing;
        }
        ChatFileRevision inverse;
        try { inverse = Prepare(dataRoot, conversationId, resolved.Full, resolved.Root, current, preImage?.Text, baseline.Bytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return UndoVerdict.WriteFailed;
        }

        var replaced = preImage is null
            ? write.Replace(() => WorkspacePaths.ResolveWrite(sandbox, last.Path, guards), baseline.Bytes, null)
            : write.ReplaceRaw(() => WorkspacePaths.ResolveWrite(sandbox, last.Path, guards), baseline.Bytes, preImage.Bytes!);
        if (replaced.Verdict != TextReplaceVerdict.Written)
        {
            Abandon(dataRoot, inverse);
            return replaced.Verdict switch
            {
                TextReplaceVerdict.RefusedPath => UndoVerdict.RefusedPath,
                TextReplaceVerdict.ChangedSince => UndoVerdict.ChangedSince,
                _ => UndoVerdict.WriteFailed
            };
        }

        // A landed inverse must retain its pending journal even if completing the record fails. Read() can
        // recover it from the durable after-image; abandoning it here would lose a successful source change.
        try { CompleteRevision(dataRoot, inverse); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        inverseId = inverse.Id;
        return UndoVerdict.Reverted;
    }

    /// <summary>Delete a session's unreferenced records, then content no remaining record uses. Any uncertainty
    /// keeps data on disk; cleanup must never turn a readable history into a missing snapshot.</summary>
    public static void PruneSession(string? dataRoot, IReadOnlySet<string> liveConversationIds,
        IReadOnlySet<string> referencedIds)
    {
        if (DirectoryFor(dataRoot) is not { } root) return;
        try
        {
            var recordsDirectory = System.IO.Path.Combine(root, "records");
            Directory.CreateDirectory(recordsDirectory);
            var activeRevisions = Batches(dataRoot).Where(batch => BatchIsActive(dataRoot, batch.Id))
                .SelectMany(batch => batch.RevisionIds).ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(recordsDirectory, "*.json"))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(file);
                var record = Read(dataRoot, id);
                if (record is not null && !liveConversationIds.Contains(record.ConversationId)
                    && !referencedIds.Contains(id) && !activeRevisions.Contains(id)) File.Delete(file);
            }

            var batchesDirectory = System.IO.Path.Combine(root, "batches");
            if (Directory.Exists(batchesDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(batchesDirectory, "*.json"))
                {
                    var id = System.IO.Path.GetFileNameWithoutExtension(file);
                    var batch = ReadBatch(dataRoot, id);
                    if (batch is not null && !liveConversationIds.Contains(batch.ConversationId)
                        && !referencedIds.Contains(id) && !batch.RevisionIds.Any(referencedIds.Contains)
                        && !BatchIsActive(dataRoot, id))
                    {
                        File.Delete(file);
                        var lockPath = BatchLockPath(root, id);
                        if (File.Exists(lockPath)) File.Delete(lockPath);
                    }
                }
            }

            var liveHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(recordsDirectory, "*.json"))
            {
                var record = JsonSerializer.Deserialize<ChatFileRevision>(File.ReadAllText(file), StateStore.ReadOptions);
                if (record?.BeforeHash is { } before) liveHashes.Add(before);
                if (record?.AfterHash is { } after) liveHashes.Add(after);
                if (record?.BeforeRawHash is { } raw) liveHashes.Add(raw);
            }
            foreach (var batch in Batches(dataRoot))
                if (batch.Management is { } management)
                    foreach (var entry in management.Entries)
                        if (entry.ContentHash is { } raw) liveHashes.Add(raw);
            var objects = System.IO.Path.Combine(root, "objects");
            if (!Directory.Exists(objects)) return;
            foreach (var file in Directory.EnumerateFiles(objects, "*.txt", SearchOption.AllDirectories))
                if (!liveHashes.Contains(System.IO.Path.GetFileNameWithoutExtension(file))) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The session remains deleted; a partial prune only leaks snapshots, it cannot lose a referenced one.
        }
    }

    private static string SaveContent(string root, string text) => SaveBytes(root, new UTF8Encoding(false).GetBytes(text));

    private static byte[]? ReadBytes(string? dataRoot, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || DirectoryFor(dataRoot) is not { } root) return null;
        try
        {
            var bytes = File.ReadAllBytes(ObjectPath(root, hash));
            return string.Equals(Hash(bytes), hash, StringComparison.OrdinalIgnoreCase) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string SaveBytes(string root, byte[] bytes)
    {
        var hash = Hash(bytes);
        var path = ObjectPath(root, hash);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return hash;
            throw new IOException("A stored change snapshot did not match its content hash.");
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return hash;
    }

    private static void WriteRecord(string root, ChatFileRevision record)
    {
        var path = RecordPath(root, record.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(TextEol.ToLf(JsonSerializer.Serialize(record, Json)) + "\n");
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string RecordPath(string root, string id) => System.IO.Path.Combine(root, "records", id + ".json");
    private static string ObjectPath(string root, string hash) => System.IO.Path.Combine(root, "objects", hash[..2], hash + ".txt");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
