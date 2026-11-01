using AxmolHub.Core;

internal static class AiCommitSummaryChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        foreach (var title in new[] { "feat: add offline commit shortcut", "ver: 0.19.0" })
            if (CommitSummary.Parse("Done.\n\nCommit summary: " + title) != title)
                throw new Exception("The explicit commit-summary marker did not yield its valid title: " + title);
        if (CommitSummary.Parse("Commit summary: chore: update dependencies") is not null)
            throw new Exception("A title with an unconfigured prefix was accepted.");
        if (CommitSummary.Parse("Commit summary: feat: first\nCommit summary: fix: second") is not null)
            throw new Exception("Ambiguous multiple summary markers were accepted.");
        if (CommitSummary.Parse("Ordinary prose mentions feat: something") is not null)
            throw new Exception("Ordinary prose was guessed as a commit title.");
        var configured = CommitSummary.NormalizePrefixes(["feature", "repair:", " "]);
        if (CommitSummary.Parse("Commit summary: feature: save staged files", configured)
            != "feature: save staged files"
            || CommitSummary.IsValid("feature: " + new string('x', 121), configured)
            || CommitSummary.IsValid("ver: 0.19.0", configured))
            throw new Exception("Custom prefixes or the title length bound were not enforced.");
        // A title typed into @hub commit "..." answers to its own project's convention: no prefix rule,
        // only what a commit object cannot carry.
        if (!CommitSummary.IsWellFormedTitle("整理发布流程")
            || CommitSummary.IsWellFormedTitle("")
            || CommitSummary.IsWellFormedTitle("chore: two\nlines")
            || CommitSummary.IsWellFormedTitle(" padded ")
            || CommitSummary.IsWellFormedTitle(new string('x', 121)))
            throw new Exception("A typed commit title was not judged by the commit-object limits alone.");
        var preferencesPath = Path.Combine(root, "commit-summary-preferences.json");
        foreach (var (json, expected) in new (string, string[])[]
        {
            ("{}", CommitSummary.DefaultPrefixes),
            ("{\"CommitSummaryPrefixes\":[\"feat:\",\"fix:\",\"docs:\",\"misc:\"]}", CommitSummary.DefaultPrefixes),
            ("{\"CommitSummaryPrefixes\":[\"feature\",\"repair:\"]}", ["feature:", "repair:"]),
        })
        {
            await File.WriteAllTextAsync(preferencesPath, json);
            var preferences = new PreferencesStore(preferencesPath).Load();
            if (!preferences.CommitSummaryPrefixes.SequenceEqual(expected, StringComparer.Ordinal))
                throw new Exception("Commit prefixes did not upgrade former defaults or preserve a custom list: " + json);
        }

        var git = CommandShells.Resolve("git");
        if (git is not null)
        {
            var repo = Path.Combine(Path.GetTempPath(), "axmol-hub-commit-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repo);
            var runner = new ProcessRunner(_ => { });
            try
            {
                await RunGit("init", "-b", "main");
                await RunGit("config", "user.name", "Axmol Hub Check");
                await RunGit("config", "user.email", "hub-check@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "before staged\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "unstaged.txt"), "before worktree\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "deleted.txt"), "delete this\n");
                await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "ignored.txt\n");
                await RunGit("add", "staged.txt", "unstaged.txt", "deleted.txt", ".gitignore");
                await RunGit("commit", "-m", "chore: create fixture baseline");
                await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged change\n");
                await RunGit("add", "staged.txt");
                await File.WriteAllTextAsync(Path.Combine(repo, "unstaged.txt"), "unstaged change\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new file\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "ignored.txt"), "keep this ignored\n");
                File.Delete(Path.Combine(repo, "deleted.txt"));
                var repository = await GitRepository.ReadAsync(repo, runner);
                if (!repository.IsReadable || repository.Root is not { } repositoryRoot)
                    throw new Exception("Git fixture repository could not be read.");
                var outcome = await GitRepository.CommitStagedAsync(repo, repositoryRoot,
                    "feat: commit non-ignored changes", CommitSummary.DefaultPrefixes, runner);
                if (!outcome.Success) throw new Exception("Staged commit failed: " + outcome.FailureKey + " " + outcome.Detail);
                var committed = await runner.RunAsync(git, ["show", "--format=", "--name-only", "HEAD"], repo);
                var status = await GitRepository.ReadAsync(repo, runner);
                if (!committed.Output.Contains("staged.txt", StringComparison.Ordinal)
                    || !committed.Output.Contains("unstaged.txt", StringComparison.Ordinal)
                    || !committed.Output.Contains("untracked.txt", StringComparison.Ordinal)
                    || !committed.Output.Contains("deleted.txt", StringComparison.Ordinal)
                    || committed.Output.Contains("ignored.txt", StringComparison.Ordinal)
                    || status.Entries.Count != 0)
                    throw new Exception("Hub's deterministic commit did not commit staged, unstaged, deleted and untracked changes while preserving ignored files.");
                var noneStaged = await GitRepository.CommitStagedAsync(repo, repositoryRoot,
                    "", ["feat:", "fix:"], runner);
                if (noneStaged.Success || noneStaged.FailureKey != "nothing-staged")
                    throw new Exception("Commit accepted an empty index.");

                // A typed title outside the configured prefixes still commits when the caller says so.
                await File.WriteAllTextAsync(Path.Combine(repo, "freeform.txt"), "typed title\n");
                var freeform = await GitRepository.CommitStagedAsync(repo, repositoryRoot, "整理发布流程", null, runner);
                var freeformTitle = await runner.RunAsync(git, ["log", "-1", "--format=%s"], repo);
                if (!freeform.Success || freeformTitle.Output.TrimEnd('\r', '\n') != "整理发布流程")
                    throw new Exception("A typed title outside the configured prefixes was refused: "
                        + freeform.FailureKey + " " + freeform.Detail);
                await File.WriteAllTextAsync(Path.Combine(repo, "freeform.txt"), "second typed title\n");
                var unfreeform = await GitRepository.CommitStagedAsync(repo, repositoryRoot, "整理发布流程", ["feat:"], runner);
                if (unfreeform.Success || unfreeform.FailureKey != "invalid-summary")
                    throw new Exception("The prefix rule stopped guarding summaries Hub proposes.");

                var remote = Path.Combine(Path.GetTempPath(), "axmol-hub-commit-remote-" + Guid.NewGuid().ToString("N") + ".git");
                try
                {
                    var remoteInit = await runner.RunAsync(git, ["init", "--bare", remote], repo);
                    if (remoteInit.ExitCode != 0) throw new Exception("Git remote fixture failed: " + remoteInit.Error);
                    var noUpstream = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (noUpstream.Success || noUpstream.FailureKey != "upstream-unavailable")
                        throw new Exception("Push did not stop when the branch had no configured upstream.");
                    await RunGit("remote", "add", "origin", remote);
                    await RunGit("push", "-u", "origin", "main:published");
                    await RunGit("branch", "other");
                    await RunGit("push", "origin", "other");
                    var otherHead = await ReadHead(remote, "refs/heads/other");
                    // Conflicting user configuration must not widen the operation to another branch or tag.
                    await RunGit("config", "push.default", "matching");
                    await RunGit("config", "remote.origin.push", "refs/heads/other:refs/heads/other");
                    await RunGit("config", "remote.origin.mirror", "true");
                    await RunGit("config", "push.followTags", "true");
                    await RunGit("tag", "-a", "should-stay-local", "-m", "Keep this tag local");
                    await File.WriteAllTextAsync(Path.Combine(repo, "push.txt"), "push this\n");
                    var combined = await GitRepository.CommitAndPushStagedAsync(repo, repositoryRoot,
                        "feat: commit and push changes", ["feat:", "fix:"], runner);
                    if (!combined.Success) throw new Exception("Commit-and-push failed: " + combined.FailureKey + " " + combined.Detail);
                    if (await ReadHead(remote, "refs/heads/published") != await ReadHead(repo, "HEAD"))
                        throw new Exception("Commit-and-push did not update the differently named configured upstream.");

                    // A clean worktree can still have commits to publish; it must not create an empty commit.
                    await File.WriteAllTextAsync(Path.Combine(repo, "pending.txt"), "already committed\n");
                    await RunGit("add", "pending.txt");
                    await RunGit("commit", "-m", "feat: prepare pending local commit");
                    await RunGit("branch", "-f", "other", "HEAD");
                    var pending = await ReadHead(repo, "HEAD");
                    var cleanCombined = await GitRepository.CommitAndPushStagedAsync(repo, repositoryRoot,
                        "", ["feat:", "fix:"], runner);
                    if (!cleanCombined.Success || await ReadHead(repo, "HEAD") != pending
                        || await ReadHead(remote, "refs/heads/published") != pending)
                        throw new Exception("Clean commit-and-push did not publish the existing commit without creating another.");
                    var remoteRefs = await runner.RunAsync(git, ["for-each-ref", "--format=%(refname)"], remote);
                    if (await ReadHead(remote, "refs/heads/other") != otherHead
                        || remoteRefs.Output.Contains("refs/tags/", StringComparison.Ordinal)
                        || remoteRefs.Output.Contains("refs/heads/main", StringComparison.Ordinal))
                        throw new Exception("Push changed unrelated refs due to matching, mirror, follow-tags or remote push configuration.");

                    // An invalid push URL proves no-op paths never launch a push or contact a remote.
                    await RunGit("config", "remote.origin.pushurl", Path.Combine(remote, "missing.git"));
                    foreach (var noOp in new[]
                    {
                        await GitRepository.PushAsync(repo, repositoryRoot, runner),
                        await GitRepository.CommitAndPushStagedAsync(repo, repositoryRoot,
                            "fix: nothing changed", ["feat:", "fix:"], runner),
                    })
                    {
                        if (noOp.Success || noOp.FailureKey != "nothing-to-push")
                            throw new Exception("An up-to-date branch attempted a push instead of a terminal no-op: " + noOp.FailureKey);
                    }
                    await File.WriteAllTextAsync(Path.Combine(repo, "partial.txt"), "keep this local commit\n");
                    var partial = await GitRepository.CommitAndPushStagedAsync(repo, repositoryRoot,
                        "fix: preserve local commit on push failure", ["feat:", "fix:"], runner);
                    var partialHead = await ReadHead(repo, "HEAD");
                    if (partial.Success || partial.FailureKey != "commit-succeeded-push-failed"
                        || partialHead == pending || await ReadHead(remote, "refs/heads/published") != pending
                        || (await GitRepository.ReadAsync(repo, runner)).Entries.Count != 0)
                        throw new Exception("A failed push did not preserve and distinguish the successful local commit.");
                    var cleanFailure = await GitRepository.CommitAndPushStagedAsync(repo, repositoryRoot,
                        "fix: retry pending push", ["feat:", "fix:"], runner);
                    if (cleanFailure.Success || cleanFailure.FailureKey != "push-failed" || await ReadHead(repo, "HEAD") != partialHead)
                        throw new Exception("A clean push retry falsely claimed to have created a commit.");
                    await RunGit("config", "--unset", "remote.origin.pushurl");
                    var pushOnly = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (!pushOnly.Success || await ReadHead(remote, "refs/heads/published") != partialHead)
                        throw new Exception("Push-only did not publish the retained local commit: " + pushOnly.FailureKey);

                    // A divergent remote must reject, never force-overwrite the remote commit.
                    await RunGit("checkout", "-b", "divergent", pending);
                    await File.WriteAllTextAsync(Path.Combine(repo, "divergent.txt"), "remote-only commit\n");
                    await RunGit("add", "divergent.txt");
                    await RunGit("commit", "-m", "fix: create remote divergence");
                    var divergent = await ReadHead(repo, "HEAD");
                    await RunGit("-c", "remote.origin.mirror=false", "push", "--force", "--no-follow-tags", "origin", "divergent:published");
                    await RunGit("checkout", "main");
                    var rejected = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (rejected.Success || rejected.FailureKey != "push-failed"
                        || await ReadHead(remote, "refs/heads/published") != divergent
                        || await ReadHead(repo, "HEAD") != partialHead)
                        throw new Exception("A divergent upstream was not rejected without changing either branch.");
                    await RunGit("checkout", "--detach");
                    var detached = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (detached.Success || detached.FailureKey != "branch-unavailable")
                        throw new Exception("Push accepted a detached HEAD.");
                }
                finally
                {
                    try { Directory.Delete(remote, recursive: true); } catch (IOException) { }
                }
            }
            finally
            {
                try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
            }

            async Task RunGit(params string[] arguments)
            {
                var result = await runner.RunAsync(git, arguments, repo);
                if (result.ExitCode != 0) throw new Exception("Git fixture failed: " + result.Error + result.Output);
            }

            async Task<string> ReadHead(string directory, string reference)
            {
                var result = await runner.RunAsync(git, ["rev-parse", "--verify", reference], directory);
                if (result.ExitCode != 0) throw new Exception("Git fixture ref could not be read: " + result.Error);
                return result.Output.Trim();
            }
        }
        else
        {
            Console.WriteLine("SKIP: git executable unavailable; repository commit fixture not run.");
        }
        Console.WriteLine("PASS: commit-summary parsing and legacy defaults; commits include staged, unstaged, deleted and untracked changes but not ignored files; clean commit-and-push publishes existing commits; push is upstream-only, non-forced and a terminal no-op when current; push failures preserve successful local commits.");
        return;
    }
}
