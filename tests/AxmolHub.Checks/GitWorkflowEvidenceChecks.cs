using AxmolHub.Core;

internal static class GitWorkflowEvidenceChecks
{
    internal static void Run()
    {
        var all = GitActions.Commit | GitActions.Push | GitActions.CommitAndPush;
        var clean = new GitRepositoryState(GitReadOutcome.Ok, Path.GetFullPath("."), "main", 0, 0,
            [], new Dictionary<string, string>(), false, null);
        var fixtures = new (string Name, GitRepositoryState State, GitActions Expected)[]
        {
            ("clean", clean, GitActions.None),
            ("pending push", clean with { Ahead = 1 }, GitActions.Push),
            ("unstaged only", clean with { Entries = [new("file.txt", " ", "M", false)] }, all),
            ("staged only", clean with { Entries = [new("file.txt", "M", " ", false)] }, all),
            ("untracked only", clean with { Entries = [new("file.txt", "?", "?", true)] }, all),
            ("dirty with ahead", clean with { Ahead = 1, Entries = [new("file.txt", " ", "M", false)] }, all),
            ("branch safety is checked at execution", clean with { Branch = null, Entries = [new("file.txt", " ", "M", false)] }, all),
            ("unreadable", GitRepositoryState.Stopped(GitReadOutcome.Failed), GitActions.None),
        };
        foreach (var fixture in fixtures)
        {
            var actual = GitWorkflow.ActionsForRepository(fixture.State);
            if (actual != fixture.Expected)
                throw new Exception(fixture.Name + ": expected " + fixture.Expected + ", actual " + actual);
        }
        Console.WriteLine("PASS: Git action visibility depends only on repository changes and pending commits, with no reply-summary or tool-history input.");
        foreach (var (text, expected) in new (string, string?)[]
                 { ("@hub commit", "commit"), (" @HUB PUSH ", "push"), ("@hub commit+push", "commit-push"),
                     ("commit", null), ("push", null), ("commit+push", null), ("提交", null), ("推送", null), ("提交并推送", null),
                     ("do not commit", null), ("explain git commit", null), ("commit && push", null), ("git commit -m x", null),
                     ("@hub commit && push", null), ("explain @hub commit", null), ("@hub commit -m x", null) })
            if (GitWorkflow.ParseRequest(text) != expected) throw new Exception("Ambiguous Git request recognized: " + text);
        foreach (var (text, expectedTitle, expectedSyntax) in new (string, string?, bool)[]
        {
            ("@hub commit \"fix: supplied title\"", "fix: supplied title", true),
            ("@hub commit \"ver: 0.19.0\"", "ver: 0.19.0", true),
            ("@hub commit 'fix: single quoted title'", "fix: single quoted title", true),
            ("@hub commit 'fix: title' && push", null, false),
            (" @HUB COMMIT \"docs: 中文标题\" ", "docs: 中文标题", true),
            ("@hub commit \"fix: escaped \\\"quote\\\"\"", "fix: escaped \"quote\"", true),
            ("@hub commit \"\"", "", true),
            ("@hub commit \"chore: unconfigured\"", "chore: unconfigured", true),
            ("@hub commit \"fix: first\\nsecond\"", "fix: first\nsecond", true),
            ("@hub commit \"fix: broken", null, false),
            ("@hub commit \"fix: title\" && push", null, false),
            ("@hub commit fix: unquoted", null, false),
        })
        {
            var action = GitWorkflow.ParseRequest(text, out var title, out var validSyntax);
            if (action != "commit" || title != expectedTitle || validSyntax != expectedSyntax)
                throw new Exception("Explicit commit title parsing failed: " + text);
            if (validSyntax && !(string.IsNullOrEmpty(title) || title.Contains('\n'))
                && !CommitSummary.IsWellFormedTitle(title)) throw new Exception("A well-formed explicit title was rejected: " + text);
        }
        if (!CommitSummary.IsValid("chore: configured title", ["chore:"]))
            throw new Exception("Configured prefixes were not honored for explicit titles.");
        Console.WriteLine("PASS: exact @hub actions accept optional quoted commit titles as data; malformed titles cannot execute and typed titles escape the prefix rule.");
    }
}
