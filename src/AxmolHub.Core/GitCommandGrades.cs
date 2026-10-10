using System.Text;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// What a shell command line would do to a git repository, decided without running it.
///
/// Hub gives the model one command tool rather than a git tool, which is what every established client does
/// (Copilot's <c>#execute</c>, Claude Code's <c>Bash</c>, opencode's <c>bash</c>, Cline's
/// <c>execute_command</c>): the shell stays one path, and the repository stays reachable. What that choice
/// commits Hub to is grading the <i>subcommand</i>, because "a command run in the session's sandbox" stops being
/// the whole story the moment the command is <c>git reset --hard</c> — the sandbox is the working tree, and the
/// state git moves lives in <c>.git</c>, which the write guard already treats as protected
/// (<see cref="WorkspacePaths.IsExcludedDirectory"/>). Windsurf's terminal documentation is the cautionary tale
/// for the coarser rule: allow-listing the bare word <c>git</c> let <c>git add -A</c> run unasked, so the
/// granularity has to be the verb, not the program.
///
/// The ladder only ever goes <b>up</b>. A command this classifier does not understand keeps the tier it has today
/// (<see cref="ToolRisk.WorkspaceCommand"/>), and that direction is deliberate: every read in the product
/// (<c>git status</c>, <c>git diff</c>, <c>git log</c>) must keep running under 自动审批, because a model that
/// cannot look at the repository without a click guesses from a file it read three turns ago instead. An
/// <i>unrecognised git verb</i> is the exception and goes up: an extension or alias outside the read set
/// (<c>git lfs install</c>, <c>git flow feature start</c>) can move the tree, and nothing about a verb nobody
/// recognised says it will not.
/// </summary>
public enum GitGrade
{
    /// <summary>No git invocation in it, or nothing in it that can change a repository. Keeps today's tier.</summary>
    NotGit,

    /// <summary>A git command that only reads: <c>status</c>, <c>diff</c>, <c>log</c>, <c>show</c>. Keeps
    /// today's tier, so the model can look at the repository under 自动审批 exactly as it looks at files.</summary>
    GitRead,

    /// <summary>A git command that moves the index, the working tree, a ref, or history — and, unlike
    /// <c>file_write</c>, does it to state Hub cannot hand back.</summary>
    GitWrite,

    /// <summary>Git was called with a verb this build has never heard of. Graded as a write, because the honest
    /// reading of an unrecognised subcommand is that it might touch the tree.</summary>
    GitUnclassifiable,
}

/// <summary>
/// The classifier, as one pure function over a command line. It lives in Core for the standing reason given at
/// <see cref="WorkspaceTools"/>: <c>tests/AxmolHub.Checks</c> may reference Core but not the app, so a rule only
/// the app could see is a rule nobody can assert. It does no I/O and never throws — a command line that confuses
/// the tokenizer answers <see cref="GitGrade.NotGit"/>, which is the tier the call already had.
/// </summary>
public static class GitCommandGrades
{
    /// <summary>Segments past this are not looked at. A line with thirty separators is not one a person can
    /// approve anyway, and the cap keeps a pathological argument bounded.</summary>
    public const int MaxSegments = 32;

    /// <summary>How much of a command line is read. Every segment's head token arrives long before this, so the
    /// cut costs nothing in accuracy; the limit is on parsing an absurd argument.</summary>
    public const int MaxCommandCharacters = 8000;

    /// <summary>Verbs that only read. A verb missing from both sets lands on
    /// <see cref="GitGrade.GitUnclassifiable"/>, which asks, so an omission costs a card rather than opening a
    /// hole — which is why the write set is allowed to be short.</summary>
    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "annotate", "blame", "cat-file", "describe", "diff", "diff-files", "diff-index", "diff-tree",
        "for-each-ref", "fsck", "grep", "help", "log", "ls-files", "ls-remote", "ls-tree", "merge-base",
        "name-rev", "reflog", "rev-list", "rev-parse", "shortlog", "show", "show-ref", "status",
        "stripspace", "var", "verify-commit", "verify-pack", "verify-tag", "version", "whitespace",
    };

    /// <summary>Verbs that move the index, the tree, a ref, or history. <c>stash</c>, <c>worktree</c>,
    /// <c>branch</c>, <c>tag</c>, <c>config</c> and <c>remote</c> are deliberately absent: each is a read or a
    /// write depending on its arguments, so listing them here would decide them before their arguments were read.</summary>
    private static readonly HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "am", "apply", "bisect", "bundle", "checkout", "cherry", "cherry-pick", "clean", "clone",
        "commit", "fast-import", "fetch", "filter-branch", "gc", "init", "merge", "mv", "notes", "prune",
        "pull", "push", "rebase", "replace", "reset", "restore", "revert", "rm", "sparse-checkout", "submodule",
        "switch", "update-index", "update-ref", "update-server-info",
    };

    /// <summary>Git's own options that take a separate argument, so the verb is one token further on than it
    /// looks. Everything else beginning with <c>-</c> is boolean or carries its value with <c>=</c>.</summary>
    private static readonly HashSet<string> GlobalFlagsWithValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "-C", "-c", "--exec-path", "--git-dir", "--namespace", "--super-prefix", "--work-tree",
    };

    /// <summary>Shells whose command payload is itself a command line, and so worth parsing again.</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "cmd", "dash", "ksh", "pwsh", "powershell", "sh", "zsh",
    };

    /// <summary>Programs that only arrange somebody else's execution, so the verb to grade is the next token's.
    /// <c>env</c> and <c>nohup</c> are the ones a build script really uses, and <c>sudo</c> is the one that would
    /// otherwise hide a write behind the most privileged prefix available; without them <c>env git commit</c>
    /// would read as "a program called env ran".</summary>
    private static readonly HashSet<string> PassThroughNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "env", "nohup", "sudo", "time", "timeout",
    };

    /// <summary>Whether a grade has to cost more than the sandbox tier. Reads are not here on purpose.</summary>
    public static bool Elevates(GitGrade grade) => grade is GitGrade.GitWrite or GitGrade.GitUnclassifiable;

    /// <summary>
    /// The grade of the <c>command</c> argument of a tool call. Absent, blank, or unparseable arguments answer
    /// <see cref="GitGrade.NotGit"/> rather than the top tier, and that is load-bearing twice: the tool body
    /// refuses an empty command before it can run anything (<see cref="WorkspaceTools.RunCommand"/>), and a card
    /// about "the command Hub could not read" would be a card with nothing in it to approve.
    /// </summary>
    public static GitGrade CommandOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return GitGrade.NotGit;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("command", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? Grade(value.GetString())
                : GitGrade.NotGit;
        }
        catch (JsonException)
        {
            return GitGrade.NotGit;
        }
    }

    /// <summary>Grade a whole command line: the worst segment wins, since <c>git add -A && git commit -m x</c>
    /// is a write whatever its first half looks like.</summary>
    public static GitGrade Grade(string? command) => Grade(command, 0);

    private static GitGrade Grade(string? command, int depth)
    {
        if (string.IsNullOrWhiteSpace(command)) return GitGrade.NotGit;
        var text = command!.Trim();
        if (text.Length > MaxCommandCharacters) text = text[..MaxCommandCharacters];

        var worst = GitGrade.NotGit;
        foreach (var segment in SplitSegments(text))
        {
            var grade = GradeSegment(segment, depth);
            if (Rank(grade) > Rank(worst)) worst = grade;
            if (worst == GitGrade.GitWrite) break;
        }
        return worst;
    }

    private static int Rank(GitGrade grade) => grade switch
    {
        GitGrade.GitWrite => 3,
        GitGrade.GitUnclassifiable => 2,
        GitGrade.GitRead => 1,
        _ => 0,
    };

    /// <summary>One parsed word of the command line. <see cref="Quoted"/> is kept because <c>"git commit"</c>
    /// is one argument to <c>echo</c> rather than a git call, while <c>"git" commit</c> really is one.</summary>
    private readonly record struct Token(string Text, bool Quoted);

    /// <summary>Split on the shell's sequence separators — <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>;</c>,
    /// <c>&amp;</c>, newline — with quoting respected, so a separator inside a commit message invents no segment.
    /// Redirect characters split too, which leaves heads like <c>2></c> and <c>1</c> that nobody recognises: they
    /// grade as <see cref="GitGrade.NotGit"/>, and the real verb is still read in its own segment.</summary>
    private static List<List<Token>> SplitSegments(string command)
    {
        var segments = new List<List<Token>>();
        var current = new List<Token>();
        var buffer = new StringBuilder();
        var quotedInBuffer = false;

        void FlushToken()
        {
            if (buffer.Length == 0) return;
            current.Add(new Token(buffer.ToString(), quotedInBuffer));
            buffer.Clear();
            quotedInBuffer = false;
        }

        void FlushSegment()
        {
            FlushToken();
            if (current.Count == 0) return;
            segments.Add(current);
            current = new List<Token>();
        }

        for (var i = 0; i < command.Length; i++)
        {
            if (segments.Count >= MaxSegments) break;
            var c = command[i];
            if (c is '"' or '\'')
            {
                quotedInBuffer = true;
                var quote = c;
                i++;
                while (i < command.Length && command[i] != quote)
                {
                    // Only a double-quoted string honours the backslash escape; inside single quotes every
                    // character is literal, which is git's own rule and the one a shell follows.
                    if (quote == '"' && command[i] == '\\' && i + 1 < command.Length && command[i + 1] == quote) i++;
                    buffer.Append(command[i]);
                    i++;
                }
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (c is '\n' or '\r') FlushSegment();
                else FlushToken();
                continue;
            }
            if (c is ';' or '|' or '&' or '<' or '>')
            {
                FlushToken();
                if ((c == '|' || c == '&') && i + 1 < command.Length && command[i + 1] == c) i++;
                FlushSegment();
                continue;
            }
            buffer.Append(c);
        }
        FlushSegment();
        return segments;
    }

    /// <summary>Grade one segment by its head word, after any environment prefix. <paramref name="depth"/>
    /// bounds the recursion through a shell payload or a pass-through program, so a self-nesting argument
    /// cannot walk itself out of the answer.</summary>
    private static GitGrade GradeSegment(List<Token> segment, int depth)
    {
        var index = 0;
        while (index < segment.Count && IsEnvironmentAssignment(segment[index])) index++;
        if (index >= segment.Count) return GitGrade.NotGit;

        var head = segment[index];
        var name = ExecutableName(head.Text);
        if (name.Length == 0) return GitGrade.NotGit;
        if (string.Equals(name, "git", StringComparison.Ordinal)) return GradeGitCall(segment, index + 1);
        if (depth < 2)
        {
            if (ShellNames.Contains(name))
            {
                var payload = CommandPayload(segment, index + 1);
                return payload is null ? GitGrade.NotGit : Grade(payload, depth + 1);
            }
            if (PassThroughNames.Contains(name)) return GradeTail(segment, index + 1, depth);
        }
        return GitGrade.NotGit;
    }

    /// <summary>
    /// Grade what a pass-through program was given, by finding the first token that is itself a program this
    /// classifier knows: <c>timeout 60 git clone x</c> and <c>env -i LC_ALL=C git push</c> both hide their verb
    /// behind the wrapper's own arguments, and reading position 0 as the head would call <c>60</c> a program.
    /// Anything the scan does not recognise keeps today's tier, which is the direction that never stops a build.
    /// </summary>
    private static GitGrade GradeTail(List<Token> segment, int from, int depth)
    {
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (IsEnvironmentAssignment(token)) continue;
            if (token.Text.StartsWith('-') && !token.Quoted) continue;
            var name = ExecutableName(token.Text);
            if (name.Length == 0) continue;
            if (string.Equals(name, "git", StringComparison.Ordinal)) return GradeGitCall(segment, i + 1);
            if (depth >= 2) continue;
            if (ShellNames.Contains(name))
            {
                var payload = CommandPayload(segment, i + 1);
                return payload is null ? GitGrade.NotGit : Grade(payload, depth + 1);
            }
            if (PassThroughNames.Contains(name)) return GradeTail(segment, i + 1, depth + 1);
        }
        return GitGrade.NotGit;
    }

    /// <summary><c>LC_ALL=C git status</c>: an assignment before the program is not the program.</summary>
    private static bool IsEnvironmentAssignment(Token token)
    {
        if (token.Quoted) return false;
        var at = token.Text.IndexOf('=');
        if (at <= 0) return false;
        for (var i = 0; i < at; i++)
        {
            var c = token.Text[i];
            // A leading '-' makes this an option that happens to carry a value (--pretty=format:%h), not a
            // variable, and options are never skipped as a prefix.
            if (char.IsLetterOrDigit(c) || c is '_' or ':' or '$' or '.') continue;
            return false;
        }
        return true;
    }

    /// <summary>The program a segment runs, as a bare lower-cased name: <c>git</c>, <c>git.exe</c>,
    /// <c>/usr/bin/git</c> and <c>C:\Program Files\Git\bin\git.exe</c> name one program, and PowerShell's call
    /// operator is decoration on it.</summary>
    private static string ExecutableName(string text)
    {
        var trimmed = text.Trim().TrimStart('&', '.');
        if (trimmed.Length == 0) return "";
        var name = Path.GetFileName(trimmed.Replace('\\', '/'));
        if (name.Length >= 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    /// <summary>Read the verb after <c>git</c>, skipping its own options.</summary>
    private static GitGrade GradeGitCall(List<Token> segment, int from)
    {
        // A bare `git` prints usage and changes nothing.
        if (from >= segment.Count) return GitGrade.NotGit;
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.Length == 0) continue;
            if (token.Text[0] != '-') return GradeGitVerb(token.Text, segment, i + 1);
            if (token.Text is "--version" or "--help" or "--help-all" or "-h" or "-V") return GitGrade.NotGit;
            if (GlobalFlagsWithValues.Contains(token.Text))
            {
                i++; // its value is not the verb either.
                continue;
            }
        }
        // Options and no verb: something was asked of git and this build cannot say what.
        return GitGrade.GitUnclassifiable;
    }

    private static GitGrade GradeGitVerb(string verb, List<Token> segment, int rest)
    {
        if (ReadVerbs.Contains(verb)) return GitGrade.GitRead;
        if (WriteVerbs.Contains(verb)) return GitGrade.GitWrite;
        return verb.ToLowerInvariant() switch
        {
            // These are reads or writes depending on their arguments, so each is decided by them rather than
            // guessed from the verb alone.
            "branch" => GradeNamedOperation(segment, rest, ListingFlags:
                ["-a", "-r", "-v", "-l", "--list", "--all", "--remotes", "--show-current", "--contains",
                 "--no-merged", "--merged", "--format"]),
            "tag" => GradeNamedOperation(segment, rest, ListingFlags: ["-l", "--list", "-n", "--contains"]),
            "config" => GradeConfig(segment, rest),
            "remote" => GradeRemote(segment, rest),
            "stash" => GradeSubcommandListing(segment, rest, defaultWrite: true),
            // `git stash` with nothing after it pushes; `git worktree` prints its subcommand list.
            "worktree" => GradeSubcommandListing(segment, rest, defaultWrite: false),
            _ => GitGrade.GitUnclassifiable,
        };
    }

    /// <summary>A bare listing versus a named operation: no non-option argument, or one of the listing flags, is
    /// a read; anything else names a thing to create, delete, or move.</summary>
    private static GitGrade GradeNamedOperation(List<Token> segment, int rest, string[] ListingFlags)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                if (Array.IndexOf(ListingFlags, token.Text) >= 0) return GitGrade.GitRead;
                continue;
            }
            return GitGrade.GitWrite;
        }
        return GitGrade.GitRead;
    }

    /// <summary><c>git config x</c> reads, <c>git config x y</c> writes, and the destructive spellings write
    /// whatever their argument count.</summary>
    private static GitGrade GradeConfig(List<Token> segment, int rest)
    {
        var arguments = 0;
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                if (token.Text is "--unset" or "--unset-all" or "--add" or "--replace-all" or "--remove-section"
                               or "--rename-section" or "--edit" or "-e")
                    return GitGrade.GitWrite;
                if (token.Text is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l")
                    return GitGrade.GitRead;
                continue;
            }
            arguments++;
        }
        return arguments >= 2 ? GitGrade.GitWrite : GitGrade.GitRead;
    }

    /// <summary><c>git remote</c> and <c>git remote -v</c> list; <c>show</c>/<c>get-url</c> read; the rest name
    /// a remote to add, rename, or point somewhere else.</summary>
    private static GitGrade GradeRemote(List<Token> segment, int rest)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                if (token.Text is "-v" or "-vn" or "--verbose") return GitGrade.GitRead;
                continue;
            }
            return token.Text.ToLowerInvariant() is "show" or "get-url"
                ? GitGrade.GitRead
                : GitGrade.GitWrite;
        }
        return GitGrade.GitRead;
    }

    /// <summary>For the verbs whose read is a sub-listing (<c>stash list</c>, <c>worktree list</c>).</summary>
    private static GitGrade GradeSubcommandListing(List<Token> segment, int rest, bool defaultWrite)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted) continue;
            return token.Text.ToLowerInvariant() is "list" or "show" or "diff"
                ? GitGrade.GitRead
                : GitGrade.GitWrite;
        }
        return defaultWrite ? GitGrade.GitWrite : GitGrade.GitRead;
    }

    /// <summary>
    /// The command payload of a shell started for one command: <c>pwsh -c "git push"</c> is <c>git push</c> as
    /// far as this classifier is concerned, because treating it as "a shell ran and nothing happened" is exactly
    /// the route a model takes around a rule it dislikes. Everything after the flag is taken, so the unquoted
    /// <c>cmd /c git push</c> form finds its verb too.
    ///
    /// Two blind spots are accepted and named, because closing them would cost a card for every script run: a
    /// payload held in a variable (<c>pwsh -c $cmd</c> after <c>$cmd="git push"</c>) and a script file
    /// (<c>bash deploy.sh</c>). Both sit on the read side of a ladder that only goes up, and the same shapes are
    /// invisible to every client's subcommand rules.
    /// </summary>
    private static string? CommandPayload(List<Token> segment, int rest)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var flag = segment[i].Text.ToLowerInvariant();
            if (flag is not ("-c" or "-lc" or "-command" or "/c" or "/lc")) continue;
            if (i + 1 >= segment.Count) return null;
            var first = segment[i + 1].Text;
            // A variable rather than a command line: nothing here can see inside it.
            if (first.Length == 0 || first[0] is '$' or '%') return null;
            var payload = new StringBuilder();
            for (var j = i + 1; j < segment.Count; j++)
            {
                if (payload.Length > 0) payload.Append(' ');
                payload.Append(segment[j].Text);
            }
            return payload.ToString();
        }
        return null;
    }
}
