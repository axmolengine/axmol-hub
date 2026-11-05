using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AxmolHub.Core;

internal sealed record FsManageEntry(string Path, string Relative, bool IsDirectory, byte[]? Bytes,
    FileAttributes Attributes, UnixFileMode? Mode);
internal sealed record FsManagePlan(WorkspacePath Source, WorkspacePath? Destination,
    IReadOnlyList<FsManageEntry> Entries, IReadOnlyList<string> CreatedParents, string Fingerprint, string? Error);

public sealed partial class WorkspaceTools
{
    public static bool ManagementMayBreakCache(WorkspaceToolScope scope, string path)
    {
        var resolved = WorkspacePaths.ResolveManage(scope.Sandbox, path, scope.Guards);
        if (!resolved.IsAllowed) return false;
        if (resolved.Full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(BuildCommandGrades.IsCachePath)) return true;
        if (!Directory.Exists(resolved.Full)) return false;
        try
        {
            var pending = new Stack<string>();
            pending.Push(resolved.Full);
            while (pending.Count > 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (WorkspacePaths.IsProtected(child, scope.Guards)) return false;
                    if (BuildCommandGrades.IsCachePath(child)) return true;
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static FsManagePlan PrepareManagement(WorkspaceToolScope scope, string source, string? destination,
        bool recursive)
    {
        static FsManagePlan Refuse(string error) => new(default, null, [], [], "invalid", error);
        var src = WorkspacePaths.ResolveManage(scope.Sandbox, source, scope.Guards);
        if (!src.IsAllowed) return Refuse(WorkspacePaths.ResultFor(src.Verdict, src.Display));
        var entries = new List<FsManageEntry>();
        try
        {
            if (!File.Exists(src.Full) && !Directory.Exists(src.Full)) return Refuse($"Refused: {src.Display} does not exist.");
            var pending = new Stack<string>();
            pending.Push(src.Full);
            while (pending.Count > 0)
            {
                var full = pending.Pop();
                var resolved = WorkspacePaths.ResolveManage(scope.Sandbox, full, scope.Guards);
                if (!resolved.IsAllowed) return Refuse(WorkspacePaths.ResultFor(resolved.Verdict, full));
                var attributes = File.GetAttributes(full);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    return Refuse($"Refused: {full} is a link or a special file.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var relative = Path.GetRelativePath(src.Full, full);
                var bytes = isDirectory ? null : ReadFileBytes(full);
                var mode = OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(full);
                entries.Add(new(full, relative, isDirectory, bytes, attributes, mode));
                if (!isDirectory) continue;
                foreach (var child in Directory.EnumerateFileSystemEntries(full))
                    pending.Push(child);
            }
            if (destination is null && !recursive && entries.Count > 1)
                return Refuse("Refused: the directory is not empty. Pass recursive=true to delete its contents.");
            WorkspacePath? dst = null;
            var createdParents = new List<string>();
            if (destination is not null)
            {
                var resolved = WorkspacePaths.ResolveManage(scope.Sandbox, destination, scope.Guards);
                if (!resolved.IsAllowed) return Refuse(WorkspacePaths.ResultFor(resolved.Verdict, resolved.Display));
                var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var prefix = src.Full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (resolved.Full.StartsWith(prefix, comparison)) return Refuse("Refused: a directory cannot be moved into itself.");
                if (string.Equals(src.Full, resolved.Full, StringComparison.Ordinal)) return Refuse("No change: source and destination are the same.");
                if (File.Exists(resolved.Full) || Directory.Exists(resolved.Full))
                {
                    // A case-only rename may refer to the same directory entry on a case-insensitive volume.
                    if (!string.Equals(src.Full, resolved.Full, comparison)
                        || ExistsExact(resolved.Full))
                        return Refuse($"Refused: destination {resolved.Display} already exists.");
                }
                var parent = Path.GetDirectoryName(resolved.Full);
                while (parent is { Length: > 0 } && !Directory.Exists(parent))
                {
                    var allowed = WorkspacePaths.ResolveManage(scope.Sandbox, parent, scope.Guards);
                    if (!allowed.IsAllowed) return Refuse(WorkspacePaths.ResultFor(allowed.Verdict, parent));
                    createdParents.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
                createdParents.Reverse();
                dst = resolved;
            }
            var sorted = entries.OrderBy(entry => entry.Relative, StringComparer.Ordinal).ToArray();
            var facts = sorted.Select(entry => new { entry.Relative, entry.IsDirectory,
                Hash = entry.Bytes is null ? null : Convert.ToHexString(SHA256.HashData(entry.Bytes)),
                Attributes = (int)entry.Attributes, Mode = (int?)entry.Mode }).ToArray();
            var identity = JsonSerializer.Serialize(new { Source = src.Full, Destination = dst?.Full, Facts = facts,
                CreatedParents = createdParents });
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return new(src, dst, sorted, createdParents, fingerprint, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Refuse($"Could not inspect {src.Display}: {ex.Message}"); }
    }

    [Description("Delete one file or directory. Nonempty directories require recursive=true.")]
    public string FsDelete([Description("File or directory within a writable workspace.")] string path,
        [Description("Allow deleting a nonempty directory.")] bool recursive = false)
        => Manage(path, null, recursive);

    [Description("Move or rename a file or directory without overwriting the destination.")]
    public string FsMove([Description("Existing file or directory.")] string source,
        [Description("Full destination path; it must not already exist.")] string destination)
        => Manage(source, destination, true);

    private string Manage(string source, string? destination, bool recursive)
    {
        using var gate = context.TakeChangeGate?.Invoke(destination is null ? ToolNames.FsDelete : ToolNames.FsMove);
        if (context.TakeChangeGate is not null && gate is null) return ToolApprovalResults.Busy;
        if (ManagementMayBreakCache(context, source) && !context.ApprovedCacheBreak)
            return "Refused: removing or moving build cache requires explicit approval. Request the operation again.";
        var initial = PrepareManagement(context, source, destination, recursive);
        if (initial.Error is not null) return initial.Error;
        var leases = new List<TextFile.WriteLease>();
        IDisposable? batchLease = null;
        ChatChangeBatch? batch = null;
        var applied = new List<FsManageEntry>();
        var created = new List<string>();
        var moved = false;
        try
        {
            foreach (var entry in initial.Entries.Where(entry => !entry.IsDirectory).OrderBy(entry => entry.Path, StringComparer.Ordinal))
                leases.Add(TextFile.AcquireWrite(entry.Path));
            var plan = PrepareManagement(context, source, destination, recursive);
            if (plan.Error is not null) return plan.Error;
            if (plan.Fingerprint != initial.Fingerprint
                || context.ApprovedWriteFingerprint is { } expected && expected != plan.Fingerprint)
                return "Refused: the file or directory changed since the operation was prepared.";
            if (ManagementMayBreakCache(context, source) && !context.ApprovedCacheBreak)
                return "Refused: build cache appeared after the operation was prepared. Request approval again.";
            var stored = plan.Entries.Select(entry => new FsManagedEntry(entry.Relative, entry.IsDirectory,
                entry.Bytes is null ? null : ChatChangeStore.SaveRawObject(context.DataRoot, entry.Bytes),
                entry.Bytes?.LongLength ?? 0, (int)entry.Attributes, (int?)entry.Mode)).ToArray();
            var operation = new FsManagementRecord(destination is null ? "delete" : "move", plan.Source.Full,
                plan.Destination?.Full, stored, plan.CreatedParents);
            batch = ChatChangeStore.PrepareBatch(context.DataRoot, context.ConversationId, [], out batchLease, operation);
            if (plan.Destination is { } target)
            {
                foreach (var parent in plan.CreatedParents)
                {
                    var allowed = WorkspacePaths.ResolveManage(context.Sandbox, parent, context.Guards);
                    if (!allowed.IsAllowed || File.Exists(parent) || Directory.Exists(parent))
                        throw new IOException($"Destination parent changed: {parent}");
                    CreateDirectoryExclusive(parent);
                    created.Add(parent);
                }
                var check = PrepareManagement(context, source, destination, recursive);
                // Parent creation is the only intended difference in the fingerprint at this point.
                if (check.Error is not null || check.Entries.Count != plan.Entries.Count
                    || !SameTree(plan.Entries, check.Entries)) throw new IOException("The source or destination changed.");
                if (ManagementMayBreakCache(context, source) && !context.ApprovedCacheBreak)
                    throw new IOException("Build cache appeared after the operation was prepared.");
                Rename(plan.Source.Full, target.Full, plan.Entries[0].IsDirectory);
                moved = true;
                batch = ChatChangeStore.RecordBatchProgress(context.DataRoot, batch, ".");
                ChatChangeStore.CompleteBatch(context.DataRoot, batch, ChatChangeStore.Complete);
                return $"Moved {plan.Source.Display} to {target.Display}." + ChatChangeStore.BatchMarker + batch.Id;
            }

            foreach (var entry in plan.Entries.OrderByDescending(entry => entry.Relative == "." ? 0 : 1 + entry.Relative.Count(c => c == Path.DirectorySeparatorChar))
                         .ThenBy(entry => entry.IsDirectory))
            {
                var allowed = WorkspacePaths.ResolveManage(context.Sandbox, entry.Path, context.Guards);
                if (!allowed.IsAllowed) throw new IOException($"The permitted path changed: {entry.Path}");
                if (File.GetAttributes(entry.Path) != entry.Attributes
                    || !OperatingSystem.IsWindows() && entry.Mode != File.GetUnixFileMode(entry.Path))
                    throw new IOException($"The file permissions changed: {entry.Path}");
                if (entry.IsDirectory) Directory.Delete(entry.Path, false);
                else
                {
                    if (!ReadFileBytes(entry.Path).AsSpan().SequenceEqual(entry.Bytes))
                        throw new IOException($"The file changed: {entry.Path}");
                    File.Delete(entry.Path);
                }
                applied.Add(entry);
                batch = ChatChangeStore.RecordBatchProgress(context.DataRoot, batch, entry.Relative);
            }
            ChatChangeStore.CompleteBatch(context.DataRoot, batch, ChatChangeStore.Complete);
            return $"Deleted {plan.Source.Display}." + ChatChangeStore.BatchMarker + batch.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var failures = new List<string>();
            if (ex is IncompleteRename incomplete)
            {
                failures.Add(incomplete.TemporaryPath);
                if (batch?.Management is { } management)
                    batch = batch with { Management = management with { TemporaryPath = incomplete.TemporaryPath } };
            }
            if (moved && initial.Destination is { } target)
            {
                try
                {
                    var rollback = PrepareManagement(context, target.Full, initial.Source.Full, true);
                    if (rollback.Error is not null || !SameTree(initial.Entries, rollback.Entries))
                        throw new IOException("The moved tree changed before rollback.");
                    Rename(target.Full, initial.Source.Full, initial.Entries[0].IsDirectory);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException or NotSupportedException) { failures.Add(target.Full); }
            }
            foreach (var entry in applied.AsEnumerable().Reverse())
            {
                try
                {
                    var allowed = WorkspacePaths.ResolveManage(context.Sandbox, entry.Path, context.Guards);
                    if (!allowed.IsAllowed) throw new IOException("The restore path is no longer permitted.");
                    RestoreManagedEntry(entry, restoreMetadata: !entry.IsDirectory);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { failures.Add(entry.Path); }
            }
            foreach (var entry in applied.Where(entry => entry.IsDirectory).OrderByDescending(entry => entry.Path.Length))
            {
                if (failures.Contains(entry.Path, StringComparer.Ordinal)) continue;
                try
                {
                    if (!WorkspacePaths.ResolveManage(context.Sandbox, entry.Path, context.Guards).IsAllowed)
                        throw new IOException("The restore path is no longer permitted.");
                    if (!OperatingSystem.IsWindows() && entry.Mode is { } mode) File.SetUnixFileMode(entry.Path, mode);
                    File.SetAttributes(entry.Path, entry.Attributes);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { failures.Add(entry.Path); }
            }
            foreach (var parent in created.AsEnumerable().Reverse())
                try
                {
                    if (WorkspacePaths.ResolveManage(context.Sandbox, parent, context.Guards).IsAllowed)
                        Directory.Delete(parent, false);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            if (batch is not null)
            {
                try
                {
                    batch = batch with { AppliedIds = moved && failures.Count > 0 ? ["."]
                        : applied.Where(entry => !File.Exists(entry.Path) && !Directory.Exists(entry.Path))
                            .Select(entry => entry.Relative).ToArray() };
                    ChatChangeStore.CompleteBatch(context.DataRoot, batch, failures.Count == 0 ? "rolled-back" : "partial");
                }
                catch (Exception journal) when (journal is IOException or UnauthorizedAccessException) { failures.Add("change journal"); }
            }
            return failures.Count == 0 ? $"Refused: {ex.Message} Completed work was restored."
                : $"Partial change: {ex.Message} Could not restore {string.Join(", ", failures)}."
                  + (batch is null ? "" : ChatChangeStore.BatchMarker + batch.Id);
        }
        finally
        {
            if (batchLease is not null && context.HoldBatchLease is { } hold && ToolInvocation.CallId is { } callId)
            { hold(callId, batchLease); batchLease = null; }
            batchLease?.Dispose();
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
        }
    }

    private static bool SameTree(IReadOnlyList<FsManageEntry> first, IReadOnlyList<FsManageEntry> second)
        => first.Count == second.Count && first.Zip(second).All(pair => pair.First.Relative == pair.Second.Relative
            && pair.First.IsDirectory == pair.Second.IsDirectory && pair.First.Attributes == pair.Second.Attributes
            && pair.First.Mode == pair.Second.Mode && (pair.First.Bytes is null ? pair.Second.Bytes is null
                : pair.Second.Bytes is not null && pair.First.Bytes.AsSpan().SequenceEqual(pair.Second.Bytes)));

    private static void Rename(string source, string destination, bool directory)
    {
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            && string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source, destination, StringComparison.Ordinal))
        {
            var temporary = source + "." + Guid.NewGuid().ToString("N") + ".tmp";
            RenameNoReplace(source, temporary, directory);
            try { RenameNoReplace(temporary, destination, directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                try { RenameNoReplace(temporary, source, directory); }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException or NotSupportedException)
                { throw new IncompleteRename(temporary, ex); }
                throw;
            }
            return;
        }
        RenameNoReplace(source, destination, directory);
    }

    private static void RenameNoReplace(string source, string destination, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileWindows(source, destination))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(error == 17 ? "Cross-volume moves are not supported."
                    : $"Native rename failed (OS error {error}).");
            }
            return;
        }
        int result;
        try
        {
            result = OperatingSystem.IsMacOS()
                ? RenameExclusive(source, destination, 4)
                : OperatingSystem.IsLinux() ? RenameNoReplaceLinux(-100, source, -100, destination, 1)
                : throw new NotSupportedException("Native no-replace rename is not available on this platform.");
        }
        catch (EntryPointNotFoundException ex)
        { throw new NotSupportedException("Native no-replace rename is not available on this host.", ex); }
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(error == 18 ? "Cross-volume moves are not supported."
                : $"Native rename failed (OS error {error}).");
        }
    }

    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive(string source, string destination, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameNoReplaceLinux(int sourceDirectory, string source, int destinationDirectory,
        string destination, uint flags);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectoryUnix(string path, uint mode);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MakeDirectoryWindows(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileWindows(string source, string destination);

    private static void CreateDirectoryExclusive(string path)
    {
        var created = OperatingSystem.IsWindows() ? MakeDirectoryWindows(path, IntPtr.Zero)
            : MakeDirectoryUnix(path, 0x1ff) == 0;
        if (!created) throw new IOException($"Directory creation failed for {path} (OS error {Marshal.GetLastPInvokeError()}).");
    }

    private sealed class IncompleteRename(string temporaryPath, Exception inner)
        : IOException($"The temporary rename could not be restored: {temporaryPath}", inner)
    {
        public string TemporaryPath { get; } = temporaryPath;
    }

    private static void RestoreManagedEntry(FsManageEntry entry, bool restoreMetadata = true)
    {
        if (File.Exists(entry.Path) || Directory.Exists(entry.Path)) throw new IOException("The restore target exists.");
        if (!Directory.Exists(Path.GetDirectoryName(entry.Path))) throw new IOException("The restore parent is missing.");
        if (entry.IsDirectory) CreateDirectoryExclusive(entry.Path);
        else
        {
            using var output = new FileStream(entry.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(entry.Bytes!);
            output.Flush(true);
        }
        if (restoreMetadata)
        {
            if (!OperatingSystem.IsWindows() && entry.Mode is { } mode) File.SetUnixFileMode(entry.Path, mode);
            File.SetAttributes(entry.Path, entry.Attributes);
        }
    }

    /// <summary>Undo one completed management operation without overwriting a later file.</summary>
    public UndoVerdict UndoManagement(string batchId, out string path)
    {
        path = "";
        if (ChatChangeStore.ReadBatch(context.DataRoot, batchId) is not { Management: { } operation } batch
            || batch.State is not (ChatChangeStore.Complete or "partial"))
            return UndoVerdict.CopyMissing;
        path = operation.Source;
        using var gate = context.TakeChangeGate?.Invoke("undo_fs_management");
        if (context.TakeChangeGate is not null && gate is null) return UndoVerdict.WriteFailed;
        var leases = new List<TextFile.WriteLease>();
        try
        {
            var paths = operation.Entries.Where(entry => !entry.Directory)
                .SelectMany(entry => new[] { operation.Source, operation.Destination ?? operation.Source,
                        operation.TemporaryPath ?? operation.Source }
                    .Select(root => entry.RelativePath == "." ? root : Path.Combine(root, entry.RelativePath)))
                .Distinct(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .OrderBy(full => full, StringComparer.Ordinal).ToArray();
            foreach (var full in paths) leases.Add(TextFile.AcquireWrite(full));
            var source = WorkspacePaths.ResolveManage(context.Sandbox, operation.Source, context.Guards);
            if (!source.IsAllowed) return UndoVerdict.RefusedPath;
            if (operation.Operation == "move" && operation.Destination is { } destination)
            {
                var currentPath = operation.TemporaryPath ?? destination;
                var progress = batch.UndoProgress?.GetValueOrDefault(context.ConversationId) ?? [];
                if (progress.Contains(".", StringComparer.Ordinal) || progress.Contains("move-pending", StringComparer.Ordinal)
                    && ExistsExact(operation.Source) && !ExistsExact(currentPath))
                {
                    var restoredTree = PrepareManagement(context, operation.Source, null, true);
                    if (restoredTree.Error is not null || !MatchesSnapshot(restoredTree.Entries, operation.Entries)
                        || ExistsExact(currentPath)) return UndoVerdict.ChangedSince;
                    if (!progress.Contains(".", StringComparer.Ordinal))
                        ChatChangeStore.RecordBatchUndo(context.DataRoot, batch, context.ConversationId, ".");
                    return UndoVerdict.Reverted;
                }
                var current = PrepareManagement(context, currentPath, operation.Source, true);
                if (current.Error is not null || !MatchesSnapshot(current.Entries, operation.Entries))
                    return UndoVerdict.ChangedSince;
                batch = ChatChangeStore.RecordBatchUndo(context.DataRoot, batch, context.ConversationId, "move-pending");
                Rename(currentPath, operation.Source, operation.Entries[0].Directory);
                batch = ChatChangeStore.RecordBatchUndo(context.DataRoot, batch, context.ConversationId, ".");
                foreach (var parent in operation.CreatedParents.Reverse())
                    try
                    {
                        if (WorkspacePaths.ResolveManage(context.Sandbox, parent, context.Guards).IsAllowed)
                            Directory.Delete(parent, false);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                return UndoVerdict.Reverted;
            }
            if (operation.Operation != "delete") return UndoVerdict.CopyMissing;
            var restored = (batch.UndoProgress?.GetValueOrDefault(context.ConversationId) ?? []).ToHashSet(StringComparer.Ordinal);
            var entries = batch.State == "partial"
                ? operation.Entries.Where(entry => batch.AppliedIds.Contains(entry.RelativePath, StringComparer.Ordinal)).ToArray()
                : operation.Entries.ToArray();
            if (!Directory.Exists(Path.GetDirectoryName(operation.Source))) return UndoVerdict.TargetMissing;
            if (Directory.Exists(operation.Source) && !ExistingTreeMatches(context, operation))
                return UndoVerdict.ChangedSince;
            foreach (var entry in entries)
            {
                var full = entry.RelativePath == "." ? operation.Source : Path.Combine(operation.Source, entry.RelativePath);
                var allowed = WorkspacePaths.ResolveManage(context.Sandbox, full, context.Guards);
                if (!allowed.IsAllowed) return UndoVerdict.RefusedPath;
                if (restored.Contains(entry.RelativePath))
                {
                    if (!MatchesSavedEntry(full, entry)) return UndoVerdict.ChangedSince;
                }
                else if (File.Exists(full) || Directory.Exists(full)) return UndoVerdict.ChangedSince;
            }
            foreach (var entry in entries.OrderBy(entry => entry.RelativePath == "." ? 0 : 1 + entry.RelativePath.Count(c => c == Path.DirectorySeparatorChar))
                         .ThenByDescending(entry => entry.Directory))
            {
                if (restored.Contains(entry.RelativePath)) continue;
                var full = entry.RelativePath == "." ? operation.Source : Path.Combine(operation.Source, entry.RelativePath);
                var allowed = WorkspacePaths.ResolveManage(context.Sandbox, full, context.Guards);
                if (!allowed.IsAllowed) return UndoVerdict.RefusedPath;
                var bytes = entry.ContentHash is null ? null : ChatChangeStore.ReadRawObject(context.DataRoot, entry.ContentHash);
                if (!entry.Directory && bytes is null) return UndoVerdict.CopyMissing;
                RestoreManagedEntry(new FsManageEntry(full, entry.RelativePath, entry.Directory, bytes,
                    (FileAttributes)entry.Attributes, entry.UnixMode is { } mode ? (UnixFileMode)mode : null),
                    restoreMetadata: !entry.Directory);
                batch = ChatChangeStore.RecordBatchUndo(context.DataRoot, batch, context.ConversationId, entry.RelativePath);
            }
            foreach (var entry in entries.Where(entry => entry.Directory).OrderByDescending(entry => entry.RelativePath.Length))
            {
                var full = entry.RelativePath == "." ? operation.Source : Path.Combine(operation.Source, entry.RelativePath);
                if (!OperatingSystem.IsWindows() && entry.UnixMode is { } mode) File.SetUnixFileMode(full, (UnixFileMode)mode);
                File.SetAttributes(full, (FileAttributes)entry.Attributes);
            }
            return UndoVerdict.Reverted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return UndoVerdict.WriteFailed; }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    private static bool MatchesSavedEntry(string path, FsManagedEntry entry)
    {
        if (entry.Directory) return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        if (!File.Exists(path) || entry.ContentHash is null) return false;
        return File.GetAttributes(path) == (FileAttributes)entry.Attributes
            && (OperatingSystem.IsWindows() || entry.UnixMode is null || (int)File.GetUnixFileMode(path) == entry.UnixMode)
            && string.Equals(Convert.ToHexString(SHA256.HashData(ReadFileBytes(path))),
            entry.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ExistsExact(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent)) return false;
        foreach (var item in Directory.EnumerateFileSystemEntries(parent))
        {
            if (string.Equals(Path.GetFileName(item), Path.GetFileName(path), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool ExistingTreeMatches(WorkspaceToolScope scope, FsManagementRecord operation)
    {
        var expected = operation.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(operation.Source);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            if (!WorkspacePaths.ResolveManage(scope.Sandbox, path, scope.Guards).IsAllowed)
                return false;
            var relative = Path.GetRelativePath(operation.Source, path);
            if (!expected.TryGetValue(relative, out var entry) || !MatchesSavedEntry(path, entry)) return false;
            if (entry.Directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    pending.Push(child);
        }
        return true;
    }

    private static bool MatchesSnapshot(IReadOnlyList<FsManageEntry> current, IReadOnlyList<FsManagedEntry> snapshot)
        => current.Count == snapshot.Count && current.Zip(snapshot).All(pair => pair.First.Relative == pair.Second.RelativePath
            && pair.First.IsDirectory == pair.Second.Directory
            && (OperatingSystem.IsWindows() ? (int)pair.First.Attributes == pair.Second.Attributes
                : ((int)pair.First.Attributes & ~(int)FileAttributes.Hidden) == (pair.Second.Attributes & ~(int)FileAttributes.Hidden))
            && (int?)pair.First.Mode == pair.Second.UnixMode && (pair.First.Bytes is null ? pair.Second.ContentHash is null
                : string.Equals(Convert.ToHexString(SHA256.HashData(pair.First.Bytes)), pair.Second.ContentHash, StringComparison.OrdinalIgnoreCase)));
}
