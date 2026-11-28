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
        // A user-entered commit title answers to its own project's convention: no prefix rule,
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
                var selection = await GitRepository.ReadAsync(repo, runner, includeDiffs: false);
                var file = selection.Entries.Single();
                async Task ChangeFile(bool unstage)
                {
                    var current = await GitRepository.ReadAsync(repo, runner, includeDiffs: false);
                    var changed = await GitRepository.ChangeIndexAsync(repo, current.Root!, "main",
                        current.Entries.Single(e => e.RelativePath == file.RelativePath), unstage, runner);
                    if (!changed.Success) throw new Exception("File staging failed: " + changed.Detail);
                }
                await ChangeFile(false);
                await ChangeFile(true);
                if ((await runner.RunAsync(git, ["diff", "--cached", "--name-only"], repo)).Output.Trim().Length != 0
                    || !File.ReadAllText(Path.Combine(repo, "staged.txt")).Contains("staged change"))
                    throw new Exception("Unstaging changed the working file or left it staged.");
                await ChangeFile(false);
                await File.WriteAllTextAsync(Path.Combine(repo, "unstaged.txt"), "unstaged change\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new file\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "ignored.txt"), "keep this ignored\n");
                File.Delete(Path.Combine(repo, "deleted.txt"));
                var repository = await GitRepository.ReadAsync(repo, runner);
                if (!repository.IsReadable || repository.Root is not { } repositoryRoot)
                    throw new Exception("Git fixture repository could not be read.");
                await File.AppendAllTextAsync(Path.Combine(repo, "staged.txt"), "keep this unstaged\n");
                var outcome = await GitRepository.CommitStagedAsync(repo, repositoryRoot,
                    "feat: commit non-ignored changes", CommitSummary.DefaultPrefixes, runner);
                if (!outcome.Success) throw new Exception("Staged commit failed: " + outcome.FailureKey + " " + outcome.Detail);
                var committed = await runner.RunAsync(git, ["show", "--format=", "--name-only", "HEAD"], repo);
                var status = await GitRepository.ReadAsync(repo, runner);
                if (committed.Output.Trim() != "staged.txt" || status.Entries.Count != 4
                    || (await runner.RunAsync(git, ["show", "HEAD:staged.txt"], repo)).Output.Trim() != "staged change")
                    throw new Exception("Commit included unstaged content or changed unrelated files.");
                var noneStaged = await GitRepository.CommitStagedAsync(repo, repositoryRoot,
                    "", ["feat:", "fix:"], runner);
                if (noneStaged.Success || noneStaged.FailureKey != "nothing-staged")
                    throw new Exception("Commit accepted an empty index.");

                // A typed title outside the configured prefixes still commits when the caller says so.
                await File.WriteAllTextAsync(Path.Combine(repo, "freeform.txt"), "typed title\n");
                var staged = await GitRepository.ChangeIndexAsync(repo, repositoryRoot, "main", null, false, runner);
                if (!staged.Success) throw new Exception("Explicit stage all failed: " + staged.Detail);
                var freeform = await GitRepository.CommitStagedAsync(repo, repositoryRoot, "整理发布流程", null, runner);
                var freeformTitle = await runner.RunAsync(git, ["log", "-1", "--format=%s"], repo);
                if (!freeform.Success || freeformTitle.Output.TrimEnd('\r', '\n') != "整理发布流程")
                    throw new Exception("A typed title outside the configured prefixes was refused: "
                        + freeform.FailureKey + " " + freeform.Detail);
                await File.WriteAllTextAsync(Path.Combine(repo, "freeform.txt"), "second typed title\n");
                await RunGit("add", "freeform.txt");
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
                    async Task<GitCommitResult> Branch(string from, string to, bool create = false) =>
                        await GitRepository.ChangeBranchAsync(repo, repositoryRoot, from, to, create, runner);
                    if (!(await Branch("main", "topic", create: true)).Success
                        || (await GitRepository.ReadAsync(repo, runner, includeDiffs: false)).Branch != "topic")
                        throw new Exception("Creating a local branch did not switch to it.");
                    var first = (await GitRepository.ReadAsync(repo, runner, includeDiffs: false, includePushTarget: true)).PushTarget
                        ?? throw new Exception("Git did not resolve the first push destination.");
                    await RunGit("config", "branch.topic.pushRemote", "missing");
                    if ((await GitRepository.PushAsync(repo, repositoryRoot, runner, expectedTarget: first)).FailureKey != "push-target-changed")
                        throw new Exception("Invalidated first-push target silently fell back to origin.");
                    await RunGit("config", "--unset", "branch.topic.pushRemote");

                    // A URL rewrite can change the destination without changing the remote or branch name.
                    await RunGit("config", "remote.origin.url", "hub-check:owner/repo.git");
                    await RunGit("config", "url.https://github.com/.pushInsteadOf", "hub-check:");
                    var rewritten = (await GitRepository.ReadAsync(repo, runner, includeDiffs: false, includePushTarget: true)).PushTarget;
                    await RunGit("config", "--add", "remote.origin.pushurl", "https://github.com/owner/repo.git");
                    await RunGit("config", "--add", "remote.origin.pushurl", "ssh://example.invalid/repo.git");
                    var multiple = (await GitRepository.ReadAsync(repo, runner, includeDiffs: false, includePushTarget: true)).PushTarget;
                    if (rewritten is null || rewritten.GitHubUrl != "https://github.com/owner/repo.git"
                        || multiple is null || multiple.GitHubUrl is not null)
                        throw new Exception("Git did not resolve the rewritten push URL, or allowed browser sign-in for multiple destinations.");
                    if (OperatingSystem.IsLinux())
                        await CheckGitHubCredentialCacheAsync(git, repo, rewritten.GitHubUrl);
                    await RunGit("config", "--unset-all", "remote.origin.pushurl");
                    await RunGit("config", "--add", "remote.origin.pushurl", Path.Combine(remote, "other.git"));
                    if ((await GitRepository.PushAsync(repo, repositoryRoot, runner, expectedTarget: rewritten, gitHubToken: "offline_fixture_token")).FailureKey != "push-target-changed")
                        throw new Exception("Retry did not refuse a changed push URL under the same remote name.");
                    await RunGit("config", "--unset-all", "remote.origin.pushurl");
                    await RunGit("config", "remote.origin.url", remote);

                    var approvedOid = await ReadHead(repo, "HEAD");
                    await RunGit("commit", "--allow-empty", "-m", "advance during sign-in");
                    var staleRetry = await GitRepository.PushAsync(repo, repositoryRoot, runner, expectedBranch: "topic",
                        expectedTarget: first, expectedSourceOid: approvedOid);
                    if (staleRetry.FailureKey != "push-source-changed"
                        || (await runner.RunAsync(git, ["show-ref", "--verify", "--quiet", "refs/heads/topic"], remote)).ExitCode == 0)
                        throw new Exception("A changed branch tip was published by an authorization retry.");
                    if (!(await GitRepository.PushAsync(repo, repositoryRoot, runner, expectedTarget: first,
                            expectedSourceOid: await ReadHead(repo, "HEAD"))).Success
                        || await ReadHead(remote, "refs/heads/topic") != await ReadHead(repo, "HEAD")
                        || (await runner.RunAsync(git, ["rev-parse", "--abbrev-ref", "@{upstream}"], repo)).Output.Trim() != first.Display)
                        throw new Exception("First push did not publish and track exactly the displayed branch.");
                    if (!(await GitRepository.PullAsync(repo, repositoryRoot, "topic", runner)).Success)
                        throw new Exception("Pull failed on an up-to-date tracked branch.");
                    if (!(await Branch("topic", "main")).Success) throw new Exception("Could not switch back to main.");
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
                    await RunGit("add", "push.txt");
                    // Push the existing commit, without following any other branch or tag.

                    await RunGit("commit", "-m", "feat: prepare pending local commit");
                    await RunGit("branch", "-f", "other", "HEAD");
                    var pending = await ReadHead(repo, "HEAD");
                    var pushed = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (!pushed.Success || await ReadHead(repo, "HEAD") != pending
                        || await ReadHead(remote, "refs/heads/published") != pending)
                        throw new Exception("Clean commit-and-push did not publish the existing commit without creating another.");
                    var remoteRefs = await runner.RunAsync(git, ["for-each-ref", "--format=%(refname)"], remote);
                    if (await ReadHead(remote, "refs/heads/other") != otherHead
                        || remoteRefs.Output.Contains("refs/tags/", StringComparison.Ordinal)
                        || remoteRefs.Output.Contains("refs/heads/main", StringComparison.Ordinal))
                        throw new Exception("Push changed unrelated refs due to matching, mirror, follow-tags or remote push configuration.");

                    // An invalid push URL proves no-op paths never launch a push or contact a remote.
                    await RunGit("config", "remote.origin.pushurl", Path.Combine(remote, "missing.git"));
                    var noOp = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (noOp.FailureKey != "nothing-to-push") throw new Exception("An up-to-date branch attempted a push.");
                    await RunGit("config", "--unset", "remote.origin.pushurl");
                    await RunGit("commit", "--allow-empty", "-m", "local divergence");
                    var partialHead = await ReadHead(repo, "HEAD");

                    // A divergent remote must reject, never force-overwrite the remote commit.
                    await RunGit("checkout", "-b", "divergent", pending);
                    await File.WriteAllTextAsync(Path.Combine(repo, "divergent.txt"), "remote-only commit\n");
                    await RunGit("add", "divergent.txt");
                    await RunGit("commit", "-m", "fix: create remote divergence");
                    var divergent = await ReadHead(repo, "HEAD");
                    await RunGit("-c", "remote.origin.mirror=false", "push", "--force", "--no-follow-tags", "origin", "divergent:published");
                    await RunGit("checkout", "main");
                    var rejected = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (rejected.Success || rejected.FailureKey != "push-non-fast-forward"
                        || await ReadHead(remote, "refs/heads/published") != divergent
                        || await ReadHead(repo, "HEAD") != partialHead)
                        throw new Exception("A divergent upstream was not rejected without changing either branch.");
                    await RunGit("config", "pull.rebase", "true");
                    await RunGit("config", "rebase.autoStash", "true");
                    var pull = await GitRepository.PullAsync(repo, repositoryRoot, "main", runner);
                    if (pull.FailureKey != "pull-failed" || await ReadHead(repo, "HEAD") != partialHead)
                        throw new Exception("Pull did not refuse divergent history.");
                    var collision = Path.Combine(repo, "divergent.txt");
                    await File.WriteAllTextAsync(collision, "keep untracked content");
                    if ((await Branch("main", "divergent")).Success || await ReadHead(repo, "HEAD") != partialHead
                        || await File.ReadAllTextAsync(collision) != "keep untracked content")
                        throw new Exception("Branch switch overwrote a conflicting worktree file.");
                    await RunGit("checkout", "--detach");
                    var detached = await GitRepository.PushAsync(repo, repositoryRoot, runner, expectedTarget: first);
                    if (detached.Success || detached.FailureKey != "branch-unavailable")
                        throw new Exception("Push accepted a detached HEAD.");
                }
                finally
                {
                    try { AiWorkspaceChecks.WipeRepository(remote); } catch (IOException) { }
                }
            }
            finally
            {
                try { AiWorkspaceChecks.WipeRepository(repo); } catch (IOException) { }
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
        Console.WriteLine("PASS: commit-summary parsing and legacy defaults; commit preserves unstaged content; first push sets upstream, tracked push is scoped and non-forced, pull rejects divergence, switch preserves worktree content.");
        return;
    }

    // Use Git's actual credential protocol with a fake token and no network.
    private static async Task CheckGitHubCredentialCacheAsync(string git, string repo, string url)
    {
        const string token = "offline_fixture_token";
        var runner = new ProcessRunner(_ => { });
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = HostEnvironment.PathValue, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_ASKPASS"] = "",
        };
        var cache = new GitPushCredentialCache(git);
        var options = cache.GitOptions(url);
        Task<ProcessResult> Read(string requested) => runner.RunAsync(git, [.. options, "credential", "fill"], repo,
            environment: environment, timeout: TimeSpan.FromSeconds(10), standardInput: $"url={requested}\n\n");
        await using (cache)
        {
            if (await cache.StoreAsync(url, token + "\npassword=invalid", default) || !await cache.StoreAsync(url, token, default))
                throw new Exception("Could not seed the private Git credential cache safely.");
            var matching = await Read(url + "/");
            if (matching.ExitCode != 0 || !matching.Output.Split('\n').Contains("password=" + token))
                throw new Exception("Git did not retrieve the temporary credential for the matching repository.");
            await RequireUnavailable("https://github.com/owner/different.git");
            await RequireUnavailable("https://example.invalid/owner/repo.git");
        }
        await RequireUnavailable(url);
        Console.WriteLine("PASS: Linux Git credentials are scoped to the matching HTTPS repository and removed after the operation.");

        async Task RequireUnavailable(string requested)
        {
            var refused = await Read(requested);
            if (refused.ExitCode == 0 || refused.Output.Contains(token, StringComparison.Ordinal))
                throw new Exception("Git credential remained accessible outside its repository or after cache disposal.");
        }
    }
}
