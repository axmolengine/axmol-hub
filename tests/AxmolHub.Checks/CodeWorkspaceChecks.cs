using System.Text;
using AxmolHub.Core;

internal static class CodeWorkspaceChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var folder = Path.Combine(root, "code-workspace", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(folder, "ws");
        Directory.CreateDirectory(workspace);
        var guards = new WorkspaceGuards(Path.Combine(folder, "data"), []);
        var sandbox = WorkspaceSandbox.For(workspace);
        var passed = 0;
        var total = 0;
        // Each claim has its own result: a format failure must not conceal a later authorization failure.
        async Task Claim(string name, Func<Task> body)
        {
            total++;
            try { await body(); passed++; Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { Console.WriteLine("FAIL: " + name + " — " + ex); }
        }
        void Require(bool condition, string observation)
        {
            if (!condition) throw new InvalidOperationException(observation);
        }
        TextReplaceResult Save(string path, TextFileSnapshot baseline, string text, Func<WorkspacePath>? resolve = null)
        {
            using var lease = TextFile.AcquireWrite(path);
            return lease.Save(resolve ?? (() => WorkspacePaths.ResolveWrite(sandbox, path, guards)), baseline, text);
        }

        // Byte identity and formatting are not covered by file_write's UTF-8-only output contract.
        await Claim("supported BOM/encoding survives changed saves; LF and EOF intent are explicit", () =>
        {
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
                         new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
                         new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
            {
                var path = Path.Combine(workspace, "format.txt");
                var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("中😀\r\nlast")).ToArray();
                File.WriteAllBytes(path, bytes);
                var baseline = TextFile.Read(path);
                Require(baseline.State == TextFileState.Text && baseline.LineEndings == TextLineEndings.CrLf
                    && !baseline.EndsInNewline, $"{encoding.WebName}: {baseline.State}/{baseline.LineEndings}");
                var result = Save(path, baseline, baseline.Text + "!\r\n");
                var expected = encoding.GetPreamble().Concat(encoding.GetBytes("中😀\nlast!\n")).ToArray();
                Require(result.Verdict == TextReplaceVerdict.Written && File.ReadAllBytes(path).SequenceEqual(expected),
                    $"{encoding.WebName} BOM={baseline.HasBom}: {result}");
            }
            return Task.CompletedTask;
        });
        await Claim("unchanged save preserves original bytes, BOM and timestamp", () =>
        {
            var path = Path.Combine(workspace, "noop.txt");
            File.WriteAllBytes(path, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("a\r\nb\r")).ToArray());
            File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            var bytes = File.ReadAllBytes(path);
            var stamp = File.GetLastWriteTimeUtc(path);
            var baseline = TextFile.Read(path);
            var result = Save(path, baseline, baseline.Text);
            Require(result.Verdict == TextReplaceVerdict.Unchanged && File.GetLastWriteTimeUtc(path) == stamp
                && File.ReadAllBytes(path).SequenceEqual(bytes), result.ToString());
            return Task.CompletedTask;
        });
        await Claim("unsupported bytes, binary, empty, missing and input cap remain distinct", () =>
        {
            var path = Path.Combine(workspace, "bounds.txt");
            Require(TextFile.Read(path).State == TextFileState.Missing, "missing became empty");
            File.WriteAllBytes(path, []);
            Require(TextFile.Read(path).State == TextFileState.Text, "empty became missing");
            File.WriteAllBytes(path, [0xff, 0x80]);
            Require(TextFile.Read(path).State == TextFileState.UnsupportedEncoding, "invalid UTF-8 guessed a code page");
            File.WriteAllBytes(path, [0, 1, 2]);
            Require(TextFile.Read(path).State == TextFileState.Binary, "binary became editable");
            File.WriteAllBytes(path, new byte[17]);
            Require(TextFile.Read(path, 16).State == TextFileState.TooLarge, "cap read the whole file");
            return Task.CompletedTask;
        });
        await Claim("changed/deleted/created-on-disk conflict never overwrites the source", () =>
        {
            var path = Path.Combine(workspace, "conflict.txt");
            foreach (var disk in new string?[] { "same\n", "external", null })
            {
                File.WriteAllText(path, "same\r\n");
                var baseline = TextFile.Read(path);
                if (disk is null) File.Delete(path); else File.WriteAllText(path, disk);
                var result = Save(path, baseline, "buffer");
                Require(result.Verdict == TextReplaceVerdict.ChangedSince &&
                    (disk is null ? !File.Exists(path) : File.ReadAllText(path) == disk), $"{disk ?? "deleted"}: {result}");
            }
            var absent = TextFile.Read(path);
            File.WriteAllText(path, "appeared");
            Require(Save(path, absent, "buffer").Verdict == TextReplaceVerdict.ChangedSince
                && File.ReadAllText(path) == "appeared", "new file overwrite");
            return Task.CompletedTask;
        });
        await Claim("authorization and raw identity are rechecked after staging, with temp cleanup", () =>
        {
            var path = Path.Combine(workspace, "staging.txt");
            foreach (var revoke in new[] { true, false })
            {
                File.WriteAllText(path, "original");
                var baseline = TextFile.Read(path);
                var calls = 0;
                var result = Save(path, baseline, "buffer", () =>
                {
                    if (++calls == 2)
                    {
                        if (revoke) return WorkspacePath.Refuse(WorkspacePathVerdict.ReadOnlyRoot);
                        File.WriteAllText(path, "racing external");
                    }
                    return WorkspacePaths.ResolveWrite(sandbox, path, guards);
                });
                Require(result.Verdict == (revoke ? TextReplaceVerdict.RefusedPath : TextReplaceVerdict.ChangedSince)
                    && File.ReadAllText(path) == (revoke ? "original" : "racing external")
                    && !Directory.EnumerateFiles(workspace, "staging.txt.*.tmp").Any(), $"revoke={revoke}: {result}; calls={calls}");
            }
            return Task.CompletedTask;
        });
        await Claim("readonly/exact-file/protected/link boundaries apply to manual saves", () =>
        {
            var path = Path.Combine(workspace, "permission.txt");
            File.WriteAllText(path, "original");
            var baseline = TextFile.Read(path);
            var outside = WorkspaceSandbox.Empty with { Files = [new(path, FileGrantAccess.TextEdit)] };
            var readonlySandbox = new WorkspaceSandbox(null, [], [workspace]);
            Require(Save(path, baseline, "denied", () => WorkspacePaths.ResolveWrite(readonlySandbox, path, guards)).Verdict
                == TextReplaceVerdict.RefusedPath, "readonly grant wrote");
            Require(Save(path, baseline, "allowed", () => WorkspacePaths.ResolveWrite(outside, path, guards)).Verdict
                == TextReplaceVerdict.Written, "exact writable file refused");
            var neighbor = Path.Combine(workspace, "neighbor.txt");
            Require(Save(neighbor, TextFile.Read(neighbor), "denied", () => WorkspacePaths.ResolveWrite(outside, neighbor, guards)).Verdict
                == TextReplaceVerdict.RefusedPath, "file grant opened its neighbor");
            var protectedGuards = new WorkspaceGuards(workspace, []);
            Require(Save(path, TextFile.Read(path), "denied", () => WorkspacePaths.ResolveWrite(sandbox, path, protectedGuards)).Verdict
                == TextReplaceVerdict.RefusedPath, "protected root wrote");
            if (!OperatingSystem.IsWindows())
            {
                // A file link is not a writable text target, even when the link itself is inside the workspace.
                var outsideFolder = Path.Combine(folder, "outside");
                Directory.CreateDirectory(outsideFolder);
                var target = Path.Combine(outsideFolder, "target.txt");
                File.WriteAllText(target, "outside-content");
                var link = Path.Combine(workspace, "link.txt");
                File.CreateSymbolicLink(link, target);
                var linked = Save(link, TextFile.Read(link), "hub-text");
                Require(linked.Verdict == TextReplaceVerdict.RefusedPath && File.ReadAllText(target) == "outside-content",
                    $"a save through a file link was not refused: {linked}");
            }
            else Console.WriteLine("SKIP: symlink creation requires a Windows host privilege not assumed by this fixture.");
            return Task.CompletedTask;
        });
        await Claim("Hub writers serialize; stale editor baseline loses to completed AI write without a new revision", async () =>
        {
            var path = Path.Combine(workspace, "concurrent.txt");
            File.WriteAllText(path, "original");
            var baseline = TextFile.Read(path);
            var tools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, guards.DataRoot, "code-check", [], null, null));
            var holding = TextFile.AcquireWrite(path);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(() => { started.SetResult(); return tools.FileWrite("concurrent.txt", "original", "AI"); });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!task.IsCompleted && File.ReadAllText(path) == "original", "writer passed the held gate");
            }
            finally { holding.Dispose(); }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            var id = ChatChangeStore.IdFromResult(result);
            Require(id is not null, result);
            Require(Save(path, baseline, "manual").Verdict == TextReplaceVerdict.ChangedSince
                && File.ReadAllText(path) == "AI", "stale editor overwrote AI");
            var fresh = TextFile.Read(path);
            Require(Save(path, fresh, "manual").Verdict == TextReplaceVerdict.Written, "fresh manual save failed");
            Require(ChatChangeStore.ForConversation(guards.DataRoot, "code-check").Count == 1, "manual save entered AI ledger");
            var undo = ChatChangeStore.Restore(guards.DataRoot, "code-check", id!, sandbox, guards, out _, out _);
            Require(undo == UndoVerdict.ChangedSince && File.ReadAllText(path) == "manual", "AI undo overwrote manual text: " + undo);
        });
        await Claim("Unix mode survives replacement; failed replacement leaves original and no temporary file", () =>
        {
            var path = Path.Combine(workspace, "mode.txt");
            File.WriteAllText(path, "original");
            if (!OperatingSystem.IsWindows())
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
                File.SetUnixFileMode(path, mode);
                Require(Save(path, TextFile.Read(path), "saved").Verdict == TextReplaceVerdict.Written
                    && File.GetUnixFileMode(path) == mode, "Unix executable/read bits lost");
            }
            else Console.WriteLine("SKIP: Unix mode is not a Windows filesystem contract.");
            var baseline = TextFile.Read(path);
            var failingEncoding = new UTF8Encoding(false, true);
            using var lease = TextFile.AcquireWrite(path);
            var result = lease.Replace(() => WorkspacePaths.ResolveWrite(sandbox, path, guards), File.ReadAllBytes(path), "\ud800", failingEncoding);
            Require(result.Verdict == TextReplaceVerdict.Failed && File.ReadAllText(path) == baseline.Text
                && !Directory.EnumerateFiles(workspace, "mode.txt.*.tmp").Any(), result.ToString());
            return Task.CompletedTask;
        });
        if (CommandShells.Resolve("git") is not { } git)
        {
            total++;
            Console.WriteLine("FAIL: Git two-side reads — git is not installed on this host");
        }
        else
        {
            var log = new StringBuilder();
            var runner = new ProcessRunner(line => log.Append('\n').Append(line));
            var repo = Path.Combine(folder, "repo");
            Directory.CreateDirectory(repo);
            async Task<ProcessResult> RunGit(params string[] arguments) =>
                await runner.RunAsync(git, arguments, repo);
            async Task AssertGit(params string[] arguments)
            {
                var result = await RunGit(arguments);
                Require(result.ExitCode == 0, $"git {string.Join(' ', arguments)}: {result.Error}");
            }
            await AssertGit("init", "-b", "main");
            await AssertGit("config", "user.name", "Axmol Hub Check");
            await AssertGit("config", "user.email", "hub-check@example.invalid");
            const string Head = "HEAD-MARKER\n";
            const string Index = "INDEX-MARKER\n";
            const string Worktree = "WORKTREE-MARKER\n";
            File.WriteAllText(Path.Combine(repo, "three-way.txt"), Head);
            File.WriteAllText(Path.Combine(repo, "crlf.txt"), "one\r\ntwo");
            File.WriteAllText(Path.Combine(repo, "gone.txt"), "gone soon\n");
            File.WriteAllText(Path.Combine(repo, "with space:100%*x.txt"), "special\n");
            File.WriteAllBytes(Path.Combine(repo, "binary.bin"), [0, 1, 2, 3]);
            File.WriteAllText(Path.Combine(repo, "big.txt"), new string('x', 4096));
            File.WriteAllText(Path.Combine(repo, "from.txt"), "rename body\n");
            const string HunkHead = "one\ntwo\nthree\nfour\nfive\nsix\nseven\n";
            File.WriteAllText(Path.Combine(repo, "hunks.txt"), HunkHead);
            File.WriteAllText(Path.Combine(repo, "hunks.txt"), "one\ntwo\nthree\nfour\nfive\n");
            await AssertGit("add", ".");
            await AssertGit("commit", "-m", "chore: create fixture baseline");
            File.WriteAllText(Path.Combine(repo, "three-way.txt"), Index);
            await AssertGit("add", "three-way.txt");
            File.WriteAllText(Path.Combine(repo, "three-way.txt"), Worktree);
            File.WriteAllText(Path.Combine(repo, "hunks.txt"), "ONE\ntwo\nthree\nfour\nFIVE\n");
            File.WriteAllText(Path.Combine(repo, "untracked.txt"), "new\n");
            File.Delete(Path.Combine(repo, "gone.txt"));
            await AssertGit("mv", "from.txt", "to.txt");
            var repoRoot = repo;

            await Claim("file counts follow each status range, including renames and untracked text", async () =>
            {
                var entries = new[] { new GitStatusEntry("untracked.txt", "?", "?", true) };
                var staged = await GitRepository.ReadStatusStatsAsync(repoRoot, GitReviewScope.Staged, runner, entries);
                var unstaged = await GitRepository.ReadStatusStatsAsync(repoRoot, GitReviewScope.Unstaged, runner, entries);
                var uncommitted = await GitRepository.ReadStatusStatsAsync(repoRoot, GitReviewScope.Uncommitted, runner, entries);
                Require(staged.Files.GetValueOrDefault("three-way.txt") == new ChangeLineCounts(1, 1)
                    && staged.Files.GetValueOrDefault("to.txt") == new ChangeLineCounts(0, 0)
                    && !staged.Files.ContainsKey("untracked.txt")
                    && unstaged.Files.GetValueOrDefault("hunks.txt") == new ChangeLineCounts(2, 2)
                    && unstaged.Files.GetValueOrDefault("untracked.txt") == new ChangeLineCounts(1, 0)
                    && uncommitted.Files.GetValueOrDefault("three-way.txt") == new ChangeLineCounts(1, 1)
                    && uncommitted.Files.GetValueOrDefault("untracked.txt") == new ChangeLineCounts(1, 0),
                    "status range counts differ from the versions or omit a renamed/untracked file");
            });

            await Claim("commit and branch rows carry counts from their own comparisons", async () =>
            {
                var commit = await GitRepository.ReadCommitPathsAsync(repoRoot, "HEAD", runner);
                var branch = await GitRepository.ReadBranchRangePathsAsync(repoRoot, "HEAD", runner);
                Require(commit.IsReadable && commit.Paths.Any(path => path.RelativePath == "three-way.txt"
                            && path.Lines == new ChangeLineCounts(1, 0))
                    && branch.IsReadable && branch.Paths.Any(path => path.RelativePath == "hunks.txt"
                            && path.Lines == new ChangeLineCounts(2, 2)),
                    "commit or branch file counts do not match the selected comparison");
            });

            // HEAD=A, index=B, worktree=C: each range has to read its own pair, or staging semantics leak.
            await Claim("Work Tree reads index→disk and Index reads HEAD→index for one path", async () =>
            {
                var worktree = await GitRepository.ReadFileAsync(repoRoot, "three-way.txt", GitFileRange.WorkingTree, runner);
                Require(worktree.IsReadable && worktree.Old.Snapshot.Text == Index
                    && worktree.New.Snapshot.Text == Worktree && worktree.Old.Oid is { Length: 40 },
                    $"working tree: {worktree.Detail ?? $"{worktree.Old.Snapshot.Text}|{worktree.New.Snapshot.Text}"}");
                var index = await GitRepository.ReadFileAsync(repoRoot, "three-way.txt", GitFileRange.Index, runner);
                Require(index.IsReadable && index.Old.Snapshot.Text == Head && index.New.Snapshot.Text == Index,
                    $"index: {index.Detail ?? $"{index.Old.Snapshot.Text}|{index.New.Snapshot.Text}"}");
            });
            await Claim("Git hunk operations stage, unstage and revert one fixed hunk without touching its sibling", async () =>
            {
                var entry = new GitStatusEntry("hunks.txt", " ", "M", false);
                var working = await GitRepository.ReadFileAsync(repoRoot, entry.RelativePath, GitFileRange.WorkingTree, runner);
                var staged = await GitHunkOperations.ApplyAsync(repoRoot, entry, working, 0, DiffHunkAction.Stage, runner);
                Require(staged.Applied, "stage first hunk: " + staged.Detail);
                var stale = await GitHunkOperations.ApplyAsync(repoRoot, entry, working, 1, DiffHunkAction.Stage, runner);
                Require(!stale.Applied && stale.Detail.Contains("changed since", StringComparison.Ordinal),
                    "stale hunk comparison was not refused: " + stale.Detail);
                var afterStage = await GitRepository.ReadFileAsync(repoRoot, entry.RelativePath, GitFileRange.WorkingTree, runner);
                Require(afterStage.Old.Snapshot.Text == "ONE\ntwo\nthree\nfour\nfive\n"
                    && afterStage.New.Snapshot.Text == "ONE\ntwo\nthree\nfour\nFIVE\n",
                    "stage changed more than the selected hunk");
                var index = await GitRepository.ReadFileAsync(repoRoot, entry.RelativePath, GitFileRange.Index, runner);
                var unstaged = await GitHunkOperations.ApplyAsync(repoRoot, entry, index, 0, DiffHunkAction.Unstage, runner);
                Require(unstaged.Applied, "unstage first hunk: " + unstaged.Detail);
                var worktree = await GitRepository.ReadFileAsync(repoRoot, entry.RelativePath, GitFileRange.WorkingTree, runner);
                var reverted = await GitHunkOperations.ApplyAsync(repoRoot, entry, worktree, 1, DiffHunkAction.Revert, runner);
                Require(reverted.Applied, "revert second hunk: " + reverted.Detail);
                Require(File.ReadAllText(Path.Combine(repoRoot, "hunks.txt")) == "ONE\ntwo\nthree\nfour\nfive\n",
                    "revert changed a neighboring hunk or the index");
                Require(!Directory.EnumerateFiles(Path.GetTempPath(), "axmol-hub-hunk-*.patch").Any(),
                    "temporary native patch was not removed");
            });
            await Claim("Git hunk actions refuse untracked and binary inputs before mutation", async () =>
            {
                var untrackedEntry = new GitStatusEntry("untracked.txt", "?", "?", true);
                var untracked = await GitRepository.ReadFileAsync(repoRoot, untrackedEntry.RelativePath, GitFileRange.WorkingTree, runner);
                var untrackedBefore = File.ReadAllText(Path.Combine(repoRoot, "untracked.txt"));
                var refusedUntracked = await GitHunkOperations.ApplyAsync(repoRoot, untrackedEntry, untracked, 0,
                    DiffHunkAction.Stage, runner);
                Require(!refusedUntracked.Applied && File.ReadAllText(Path.Combine(repoRoot, "untracked.txt")) == untrackedBefore,
                    "untracked hunk was staged or changed: " + refusedUntracked.Detail);
                var binaryEntry = new GitStatusEntry("binary.bin", " ", "M", false);
                var binary = await GitRepository.ReadFileAsync(repoRoot, binaryEntry.RelativePath, GitFileRange.WorkingTree, runner);
                var refusedBinary = await GitHunkOperations.ApplyAsync(repoRoot, binaryEntry, binary, 0,
                    DiffHunkAction.Stage, runner);
                Require(!refusedBinary.Applied, "binary hunk was exposed to git apply");
            });
            await Claim("raw blob bytes keep EOL and a missing final newline", async () =>
            {
                var comparison = await GitRepository.ReadFileAsync(repoRoot, "crlf.txt", GitFileRange.Index, runner);
                var side = comparison.New.Snapshot;
                Require(comparison.IsReadable && side.Text == "one\r\ntwo" && side.LineEndings == TextLineEndings.CrLf
                    && !side.EndsInNewline && comparison.Old.Snapshot.Text == "one\r\ntwo",
                    $"crlf: {comparison.Detail ?? side.Text.Replace("\r", "\\r").Replace("\n", "\\n")}");
            });
            await Claim("existence is separate: untracked, deleted and empty are not the same answer", async () =>
            {
                var untracked = await GitRepository.ReadFileAsync(repoRoot, "untracked.txt", GitFileRange.WorkingTree, runner);
                Require(untracked.IsReadable && !untracked.Old.Exists && untracked.New.Exists
                    && untracked.New.Snapshot.Text == "new\n", $"untracked: {untracked.Detail}");
                var gone = await GitRepository.ReadFileAsync(repoRoot, "gone.txt", GitFileRange.WorkingTree, runner);
                Require(gone.IsReadable && gone.Old.Exists && gone.Old.Snapshot.Text == "gone soon\n"
                    && !gone.New.Exists && gone.New.Snapshot.State == TextFileState.Missing, $"deleted: {gone.Detail}");
            });
            await Claim("a literal pathspec reaches a name with spaces, a colon and a glob character", async () =>
            {
                var comparison = await GitRepository.ReadFileAsync(repoRoot, "with space:100%*x.txt", GitFileRange.Index, runner);
                Require(comparison.IsReadable && comparison.New.Snapshot.Text == "special\n",
                    $"special name: {comparison.Detail}");
            });
            await Claim("a rename reads both names instead of claiming a new file", async () =>
            {
                var comparison = await GitRepository.ReadFileAsync(repoRoot, "to.txt", GitFileRange.Index, runner, "from.txt");
                Require(comparison.IsReadable && comparison.OldPath == "from.txt"
                    && comparison.Old.Snapshot.Text == "rename body\n" && comparison.New.Snapshot.Text == "rename body\n",
                    $"rename: {comparison.Detail ?? $"{comparison.Old.Exists}/{comparison.New.Exists}"}");
            });
            await Claim("binary and oversized versions are states, not replacement-character text", async () =>
            {
                var binary = await GitRepository.ReadFileAsync(repoRoot, "binary.bin", GitFileRange.WorkingTree, runner);
                Require(binary.IsReadable && binary.New.Snapshot.State == TextFileState.Binary,
                    $"binary: {binary.Detail ?? binary.New.Snapshot.State.ToString()}");
                var big = await GitRepository.ReadFileAsync(repoRoot, "big.txt", GitFileRange.Index, runner, maxBytes: 64);
                Require(big.IsReadable && big.New.Snapshot.State == TextFileState.TooLarge
                    && big.New.Snapshot.Detail!.Contains("4096", StringComparison.Ordinal),
                    $"oversized: {big.Detail ?? big.New.Snapshot.Detail}");
            });
            var headCommit = (await runner.RunAsync(git, ["rev-parse", "HEAD"], repoRoot)).Output.Trim();
            await Claim("a selected commit compares against its first parent using bounded raw blobs", async () =>
            {
                var commitComparison = await GitRepository.ReadCommitFileAsync(repoRoot, "three-way.txt", headCommit, runner);
                Require(commitComparison.IsReadable && commitComparison.Range == GitFileRange.Commit
                    && !commitComparison.Old.Exists && commitComparison.New.Snapshot.Text == Head,
                    $"initial commit comparison: {commitComparison.Detail ?? $"{commitComparison.Old.Exists}|{commitComparison.New.Snapshot.Text}"}");
                var ordinary = await GitRepository.ReadCommitFileAsync(repoRoot, "crlf.txt", headCommit, runner);
                Require(ordinary.IsReadable && !ordinary.Old.Exists && ordinary.New.Exists,
                    $"initial commit parent should be empty: {ordinary.Detail}");
            });

            // The one thing the raw channel exists for: source text must not arrive in the command log.
            await Claim("blob text never reaches the command log, which still records the argv and exit code", () =>
            {
                var text = log.ToString();
                Require(!text.Contains("MARKER", StringComparison.Ordinal)
                    && text.Contains("cat-file", StringComparison.Ordinal)
                    && text.Contains("Exit code: 0", StringComparison.Ordinal),
                    "the command log carried file content or lost its own diagnostics");
                return Task.CompletedTask;
            });

            await Claim("a committed rename keeps the destination count and compares against its old path", async () =>
            {
                await AssertGit("commit", "-m", "chore: commit fixture rename");
                var changed = await GitRepository.ReadCommitPathsAsync(repoRoot, "HEAD", runner);
                var rename = changed.Paths.FirstOrDefault(path => path.RelativePath == "to.txt");
                var comparison = await GitRepository.ReadCommitFileAsync(repoRoot, "to.txt", "HEAD", runner,
                    oldPath: rename?.OriginalPath);
                Require(changed.IsReadable && rename is { Status: "R", OriginalPath: "from.txt" }
                    && rename.Lines == new ChangeLineCounts(0, 0)
                    && comparison.IsReadable && comparison.Old.Snapshot.Text == "rename body\n"
                    && comparison.New.Snapshot.Text == "rename body\n", "the renamed file lost its old side or count");
            });

            // A repository with no commit: the left side is genuinely empty, which is not a failure.
            var fresh = Path.Combine(folder, "fresh");
            Directory.CreateDirectory(fresh);
            var freshRunner = new ProcessRunner(_ => { });
            await freshRunner.RunAsync(git, ["init", "-b", "main"], fresh);
            File.WriteAllText(Path.Combine(fresh, "first.txt"), "first\n");
            await freshRunner.RunAsync(git, ["add", "first.txt"], fresh);
            await Claim("an unborn HEAD gives an absent left side, not a failed repository", async () =>
            {
                var comparison = await GitRepository.ReadFileAsync(fresh, "first.txt", GitFileRange.Index, freshRunner);
                Require(comparison.IsReadable && !comparison.Old.Exists && comparison.New.Exists
                    && comparison.New.Snapshot.Text == "first\n", $"unborn HEAD: {comparison.Detail}");
            });

            // An unmerged index must not offer stage 1/2/3 as if one of them were stage 0.
            var conflicted = Path.Combine(folder, "conflict");
            Directory.CreateDirectory(conflicted);
            var conflictRunner = new ProcessRunner(_ => { });
            async Task InConflict(params string[] arguments) =>
                await conflictRunner.RunAsync(git, arguments, conflicted);
            await InConflict("init", "-b", "main");
            await InConflict("config", "user.name", "Axmol Hub Check");
            await InConflict("config", "user.email", "hub-check@example.invalid");
            File.WriteAllText(Path.Combine(conflicted, "clash.txt"), "base\n");
            await InConflict("add", "clash.txt");
            await InConflict("commit", "-m", "chore: baseline");
            await InConflict("checkout", "-b", "side");
            File.WriteAllText(Path.Combine(conflicted, "clash.txt"), "side\n");
            await InConflict("commit", "-am", "chore: side");
            await InConflict("checkout", "main");
            File.WriteAllText(Path.Combine(conflicted, "clash.txt"), "main\n");
            await InConflict("commit", "-am", "chore: main");
            await InConflict("merge", "side");
            await Claim("an unmerged index is reported as a conflict, not as whichever stage sorted first", async () =>
            {
                var comparison = await GitRepository.ReadFileAsync(conflicted, "clash.txt", GitFileRange.Index, conflictRunner);
                Require(!comparison.IsReadable && comparison.Detail!.Contains("unmerged", StringComparison.Ordinal),
                    $"conflict: {comparison.Detail ?? comparison.New.Snapshot.Text}");
            });
        }
        await Claim("AI hunk actions append a durable inverse revision without rewriting the source revision", () =>
        {
            var aiRoot = Path.Combine(folder, "ai-revisions");
            var file = Path.Combine(folder, "ai-source.txt");
            const string before = "one\ntwo\nthree\nfour\nfive\n";
            const string after = "ONE\ntwo\nthree\nfour\nFIVE\n";
            var original = ChatChangeStore.Prepare(aiRoot, "ai-conversation", file, folder, before, after);
            ChatChangeStore.CompleteRevision(aiRoot, original);
            var snapshot = ChatChangeStore.ReadSnapshot(aiRoot, original.Id, out var state);
            Require(state == UndoCopyState.Read && snapshot is { Before: before, After: after }, "original revision did not persist");
            Require(AiHunkOperations.TryApply(snapshot!.Before, snapshot.After, after, 1, AiHunkAction.Reject,
                    out var rejected, out var detail), "AI reject hunk: " + detail);
            var inverse = ChatChangeStore.Prepare(aiRoot, "ai-conversation", file, folder, after, rejected);
            ChatChangeStore.CompleteRevision(aiRoot, inverse);
            var history = ChatChangeStore.ForConversation(aiRoot, "ai-conversation");
            var originalAgain = ChatChangeStore.ReadSnapshot(aiRoot, original.Id, out var originalState);
            Require(history.Count == 2 && history[0].Id == original.Id && history[1].Id == inverse.Id
                && originalState == UndoCopyState.Read && originalAgain is { Before: before, After: after },
                "hunk action overwrote the source revision or did not append its inverse");
            return Task.CompletedTask;
        });
        await Claim("AI accept/reject acts on one exact revision hunk and refuses later edits", () =>
        {
            const string before = "one\ntwo\nthree\nfour\nfive\n";
            const string after = "ONE\ntwo\nthree\nfour\nFIVE\n";
            Require(AiHunkOperations.TryApply(before, after, before, 0, AiHunkAction.Accept,
                    out var accepted, out var acceptDetail)
                && accepted == "ONE\ntwo\nthree\nfour\nfive\n", "accept hunk zero: " + acceptDetail);
            Require(AiHunkOperations.TryApply(before, after, after, 1, AiHunkAction.Reject,
                    out var rejected, out var rejectDetail)
                && rejected == "ONE\ntwo\nthree\nfour\nfive\n", "reject hunk one: " + rejectDetail);
            Require(!AiHunkOperations.TryApply(before, after, "one\nuser edit\nthree\nfour\nFIVE\n", 0,
                    AiHunkAction.Accept, out _, out var stale)
                && stale.Contains("context no longer matches", StringComparison.Ordinal), "stale AI revision was not refused");
            const string splitBefore = "a\nold one\nb\nold two\nc\n";
            const string splitAfter = "a\nnew one\nb\nnew two\nc\n";
            var afterFirst = "";
            var afterBoth = "";
            var firstDetail = "";
            var secondDetail = "";
            Require(AiHunkOperations.TryApply(splitBefore, splitAfter, splitAfter, 0, AiHunkAction.Reject,
                    out afterFirst, out firstDetail)
                && AiHunkOperations.TryApply(splitBefore, splitAfter, afterFirst, 1, AiHunkAction.Reject,
                    out afterBoth, out secondDetail)
                && afterBoth == splitBefore, "sequential AI hunk rejection: " + firstDetail + " / " + secondDetail);
            Require(!AiHunkOperations.TryApply("before", "after\n", "after\n", 0, AiHunkAction.Reject,
                    out _, out var eofDetail)
                && eofDetail.Contains("end-of-file newline", StringComparison.Ordinal), "EOF-only revision was not refused explicitly");
            return Task.CompletedTask;
        });
        await Claim("aligned rows carry each side's own line numbers and reconstruct both sides", () =>
        {
            const string before = "a\nb\nc\n";
            const string after = "a\nc\nd\n";
            var script = FileDiff.Rows(before, after);
            Require(script.Exact && script.Rows.Count == 4, $"shape: exact={script.Exact} rows={script.Rows.Count}");
            var expected = new (DiffRowKind Kind, int? Original, int? Updated)[]
            {
                (DiffRowKind.Equal, 1, 1), (DiffRowKind.Delete, 2, null), (DiffRowKind.Equal, 3, 2), (DiffRowKind.Insert, null, 3)
            };
            for (var i = 0; i < expected.Length; i++)
            {
                var row = script.Rows[i];
                Require((row.Kind, row.OriginalLine, row.UpdatedLine) == expected[i],
                    $"row {i}: {row.Kind} {row.OriginalLine}/{row.UpdatedLine}");
            }
            var left = string.Join('\n', script.Rows.Where(row => row.OriginalLine is not null).Select(row => row.Text));
            var right = string.Join('\n', script.Rows.Where(row => row.UpdatedLine is not null).Select(row => row.Text));
            Require(left == "a\nb\nc" && right == "a\nc\nd", $"reconstruction: {left.Replace("\n", "|")} / {right.Replace("\n", "|")}");
            // CRLF against LF is a formatting difference, not a whole-file change: the rows say so.
            Require(FileDiff.Rows("a\r\nb\r\n", "a\nb\n").Rows.All(row => row.Kind == DiffRowKind.Equal),
                "line-ending style became a change");
            return Task.CompletedTask;
        });
        await Claim("identical and insertion-only diffs remain fully aligned", () =>
        {
            var identical = FileDiff.Rows("same\n", "same\n");
            Require(identical.Exact && identical.Rows.Count == 1
                && identical.Rows[0] is { Kind: DiffRowKind.Equal, OriginalLine: 1, UpdatedLine: 1 }, "identical diff lost its line");
            var inserted = FileDiff.Rows("base\n", "base\nnew\n");
            Require(inserted.Exact && inserted.Rows.Any(r => r.Kind == DiffRowKind.Insert
                && r.OriginalLine is null && r.UpdatedLine == 2), "inserted line numbering was not independent");
            return Task.CompletedTask;
        });
        await Claim("the budget degrades to a marked-changed script and says the alignment is not exact", () =>
        {
            var large = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "old " + i));
            var other = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "new " + i));
            var script = FileDiff.Rows(large, other);
            Require(!script.Exact && script.Rows.Count == 10000
                && script.Rows.All(row => row.Kind != DiffRowKind.Equal),
                $"past the size bound: exact={script.Exact} rows={script.Rows.Count}");
            // The tally and the view share the bound, so neither can call a file too large to align "unchanged".
            Require(FileDiff.CountChangedLines(large, other) == new ChangeLineCounts(5000, 5000, false),
                "tally lost the degraded script's uncertainty");
            // A failed comparison must stay visibly unknown, not become a confident count in a header.
            foreach (var limits in new[]
            {
                new DiffLimits(MaxTotalCharacters: 1), new DiffLimits(MaxLinesPerSide: 1),
                new DiffLimits(MaxEditScript: 1), new DiffLimits(MaxTraceCells: 1),
                new DiffLimits(MaxComparisonSteps: 1),
            })
            {
                const string left = "a\nb\nc\n";
                const string right = "x\nb\ny\n";
                var budgeted = FileDiff.Rows(left, right, limits);
                var counts = FileDiff.CountChangedLines(left, right, limits);
                var preview = FileDiff.Unified(left, right, "budget.txt", limits);
                Require(!budgeted.Exact && !counts.Exact
                    && counts.Added == budgeted.Rows.Count(row => row.Kind == DiffRowKind.Insert)
                    && counts.Removed == budgeted.Rows.Count(row => row.Kind == DiffRowKind.Delete)
                    && preview.Contains("counts unavailable", StringComparison.Ordinal),
                    $"budget {limits}: rows exact={budgeted.Exact}, counts={counts}, preview={preview}");
            }
            return Task.CompletedTask;
        });
        // The old single edit always trimmed to a one-line middle. It could not catch the false rewrite of
        // thousands of untouched lines between two edits, which silently inflated the turn's summary.
        await Claim("sparse changes in large files count edits, not the distance between them", () =>
        {
            foreach (var (size, positions) in new[]
            {
                (20000, new[] { 19999 }), (5000, new[] { 99, 3099 }),
                (20000, new[] { 99, 19099 }),
            })
            {
                var lines = Enumerable.Range(0, size).Select(i => "line " + i).ToArray();
                var before = string.Join('\n', lines);
                foreach (var position in positions) lines[position] += " changed";
                var after = string.Join('\n', lines);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var script = FileDiff.Rows(before, after);
                var counts = FileDiff.CountChangedLines(before, after);
                var depthBounded = FileDiff.CountChangedLines(before, after,
                    new DiffLimits(MaxEditScript: 2 * positions.Length));
                var tooShallow = FileDiff.CountChangedLines(before, after,
                    new DiffLimits(MaxEditScript: 2 * positions.Length - 1));
                var previewCounts = ChatChanges.CountChanges(FileDiff.Unified(before, after, "sparse.txt"));
                watch.Stop();
                Console.WriteLine($"  sparse: {size} lines, {positions.Length} replacements, "
                    + $"{watch.Elapsed.TotalMilliseconds:F1} ms, {counts}");
                Require(script.Exact && counts == new ChangeLineCounts(positions.Length, positions.Length)
                    && depthBounded == counts && !tooShallow.Exact
                    && previewCounts == (counts.Added, counts.Removed)
                    && script.Rows.Count(row => row.Kind != DiffRowKind.Equal) == 2 * positions.Length,
                    $"sparse edit: exact={script.Exact}, counts={counts}, depthBounded={depthBounded}, "
                    + $"tooShallow={tooShallow}, preview={previewCounts}");
            }
            return Task.CompletedTask;
        });
        // Per-call tests cannot detect charging a turn for a transient edit it subsequently reverted, or
        // losing uncertainty when snapshot metadata is persisted and re-read by the review cache.
        await Claim("turn snapshot chains measure net changes and retain budget uncertainty after reload", () =>
        {
            var original = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "line " + i));
            var first = original.Replace("line 99\n", "first change\n");
            var second = first.Replace("line 3099\n", "second change\n");
            var transient = second.Replace("line 199\n", "temporary change\n");
            var rewritten = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "new " + i));
            foreach (var (versions, expected) in new[]
            {
                (new[] { original, first, second, transient, second }, new ChangeLineCounts(2, 2)),
                (new[] { original, first, original }, new ChangeLineCounts(0, 0)),
                (new[] { original, first, rewritten }, new ChangeLineCounts(5000, 5000, false)),
            })
            {
                var data = Path.Combine(folder, "chain-data");
                var conversation = new Conversation { Id = Guid.NewGuid().ToString("N"), WorkspaceRoot = workspace };
                conversation.Append(ChatTurn.User("edit the file"));
                for (var i = 1; i < versions.Length; i++)
                {
                    var revision = ChatChangeStore.Prepare(data, conversation.Id, Path.Combine(workspace, "chain.txt"),
                        workspace, versions[i - 1], versions[i]);
                    ChatChangeStore.CompleteRevision(data, revision);
                    var loaded = ChatChangeStore.Read(data, revision.Id)!;
                    Require(loaded.Lines == revision.Lines, $"persisted counts: {revision.Lines} -> {loaded.Lines}");
                    conversation.Append(ChatTurn.FunctionCall("chain-" + i, ToolNames.FsReplace, "{}") with
                    {
                        ChangeId = loaded.Id,
                        ChangeRefs = [new ChatChangeReference("chain.txt", loaded.Id, loaded.Lines)],
                        ChangeLines = ChangeLineCounts.Sum([loaded.Lines]),
                    });
                    conversation.Append(ChatTurn.FunctionResult("chain-" + i, "Edited 1 file(s)."));
                }
                conversation.Append(ChatTurn.Assistant("done"));
                for (var read = 0; read < 2; read++)
                {
                    var review = TurnReview.Of(conversation, conversation.Messages.Count - 1, data);
                    Console.WriteLine($"  chain read {read}: {review.Files.Count} file(s), {review.Lines}");
                    Require(review.Files.Count == 1 && review.Lines == expected
                        && review.CountsExact == expected.Exact && review.Rows[0].Lines == expected,
                        $"net review: expected={expected}, actual={review.Lines}");
                }
            }
            return Task.CompletedTask;
        });
        await Claim("browser history refuses credentials, persists visits, and ranks tab switches before history", () =>
        {
            var data = Path.Combine(folder, "browser-history-data");
            var store = new BrowserHistoryStore(data);
            var t0 = new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero);
            Require(!store.RecordVisit(new Uri("https://user:secret@axmol.dev/"), "Secret"), "credentialed URL refused");
            Require(!store.RecordVisit(new Uri("about:blank"), "Blank"), "about:blank refused");
            Require(store.RecordVisit(new Uri("https://axmol.dev/"), "Axmol 3", "https://axmol.dev/favicon.ico", t0), "axmol recorded");
            Require(store.RecordVisit(new Uri("http://127.0.0.1:8786/lua-tests.html?navmesh-smoke"), null, null, t0.AddMinutes(1)), "loopback recorded");
            Require(store.RecordVisit(new Uri("https://github.com/axmolengine/axmol"), "GitHub - axmolengine/axmol", null, t0.AddMinutes(2)), "github recorded");
            Require(store.UpdateMetadata(new Uri("https://github.com/axmolengine/axmol"), "GitHub - axmolengine/axmol", "https://github.com/favicon.ico"), "metadata updated");

            var reloaded = new BrowserHistoryStore(data);
            Require(reloaded.Entries.Count == 3 && reloaded.Entries[0].Url == "https://github.com/axmolengine/axmol"
                && reloaded.Entries[0].FaviconUrl == "https://github.com/favicon.ico", "history reloaded in recency order");

            var empty = BrowserAddressSuggestions.Build("", [], reloaded.Entries);
            Require(empty.Count == 3 && empty[1].DisplayTitle == "127.0.0.1"
                && empty[1].DisplayUrl == "127.0.0.1:8786/lua-tests.html?navmesh-smoke"
                && empty[2].DisplayUrl == "axmol.dev", "display title and compact URL formatted");

            var tabs = new[] { new BrowserOpenTabInfo("tab-1", "https://axmol.dev/", "Axmol Hub") };
            var filtered = BrowserAddressSuggestions.Build("axmol", tabs, reloaded.Entries);
            Require(filtered.Count == 2 && filtered[0].Kind == BrowserSuggestionKind.SwitchToTab && filtered[0].TabId == "tab-1"
                && filtered[1].Kind == BrowserSuggestionKind.History && filtered[1].Url == "https://github.com/axmolengine/axmol",
                "matching open tab ranked first and deduplicated from history");

            var spans = BrowserAddressSuggestions.HighlightSpans("GitHub - axmolengine/axmol", ["axmol", "mol"]);
            Require(spans.Count == 2 && spans[0] == new BrowserHighlightSpan(9, 5) && spans[1] == new BrowserHighlightSpan(21, 5),
                "overlapping highlight spans merged");
            Require(BrowserAddressSuggestions.LooksLikeDirectAddress("axmol.dev")
                && BrowserAddressSuggestions.LooksLikeDirectAddress("127.0.0.1:8786")
                && !BrowserAddressSuggestions.LooksLikeDirectAddress("axmol"), "direct address heuristic");

            Require(reloaded.Remove("http://127.0.0.1:8786/lua-tests.html?navmesh-smoke"), "entry removed");
            Require(new BrowserHistoryStore(data).Entries.Count == 2, "removal persisted");
            return Task.CompletedTask;
        });
        Console.WriteLine($"RESULT: {passed}/{total} code workspace checks passed");
        if (passed != total) Environment.ExitCode = 1;
    }
}
