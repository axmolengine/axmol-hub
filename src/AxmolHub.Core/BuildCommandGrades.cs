using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// What a shell command line would do to a build, decided without running it: nothing, run a compile, or remove
/// what a compile already wrote.
///
/// <para>The second and third answers are separated on purpose, because the user ruled them apart. Running a build
/// is the assistant's ordinary work — it reads a compiler error, changes code, builds again, and every one of those
/// rounds asking for permission is how a debug loop dies. Removing the derived state is not: a
/// <c>rm -rf build</c>, <c>git clean -fdx</c> or <c>dotnet clean</c> throws away outputs nobody can regenerate from
/// what is on disk, and it does that whether or not anything was wrong beforehand. Only the third answer costs a
/// card in every mode (<see cref="ToolRisk.CacheBreak"/>).</para>
///
/// <para>The second answer still earns its keep. It is what lets a stalled command be described honestly
/// (<c>this was a build</c> rather than <c>this was a command</c>), and what points the stalled-command advice at the
/// right hazard: two writers in one output directory leave a tree half new, and a cancel does not reach the detached
/// MSBuild nodes or <c>VBCSCompiler</c> that outlive the tree Hub can see
/// (<see cref="ProcessRunner"/>'s kill walks the tree still attached to it).</para>
///
/// <para>The ladder only goes <b>up</b>, exactly as <see cref="GitCommandGrades"/>'s does: a command this does not
/// recognise keeps the tier it had (<see cref="ToolRisk.WorkspaceCommand"/>), so an omission costs a card nobody
/// needed rather than blocking a build nobody asked for. The shapes it cannot see are named rather than pretended
/// about: a wrapper script that is not itself a builder (<c>cmd /c build.bat</c>) and a payload held in a variable
/// (<c>pwsh -c $cmd</c>). Both keep the ordinary command tier; the classifier cannot promise to detect every
/// cache-clearing payload hidden inside a script.</para>
/// </summary>
public enum BuildGrade
{
    /// <summary>Nothing in it builds or destroys a build's output. Keeps the tier the call already had.</summary>
    Nothing,

    /// <summary>Runs a compile — or configures, or restores — into the workspace. Ordinary work: it asks only when
    /// the mode asks for any sandbox command at all.</summary>
    BuildRun,

    /// <summary>Removes what a build already wrote. The one answer this classifier exists for, and the only one
    /// that outranks an approval mode.</summary>
    CacheBreak,
}

/// <summary>The classifier, as pure functions over a command line — the same shape and the same bounds as
/// <see cref="GitCommandGrades"/>, over the same tokenizer (<see cref="ShellCommandLine"/>) so the two can never
/// disagree about where one command ends and the next begins.</summary>
public static class BuildCommandGrades
{
    /// <summary>Programs whose ordinary job in this product is to compile something. <c>dotnet</c> is absent: it is
    /// a host for many verbs, and only some of them build, so it is graded by its subcommand below.</summary>
    private static readonly HashSet<string> BuilderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmake", "msbuild", "ninja", "make", "mingw32-make", "xcodebuild", "ndk-build",
        "gradle", "gradlew", "gradlew.bat", "gradlew.sh", "setup.ps1", "setup.sh",
    };

    /// <summary><c>dotnet</c> verbs that compile, or rewrite what a compile reads. <c>build</c>, <c>msbuild</c> and
    /// <c>publish</c> are the obvious ones; <c>test</c> and <c>run</c> compile first, which is what makes them the
    /// same hazard and not a convenience; <c>restore</c> rewrites <c>obj/</c> under a build's feet.</summary>
    private static readonly HashSet<string> DotnetBuildVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "msbuild", "publish", "test", "run", "restore",
    };

    /// <summary><c>dotnet</c> verbs that throw build output away rather than produce it.</summary>
    private static readonly HashSet<string> DotnetCleanVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "clean",
    };

    /// <summary>Programs that delete. A recursive switch is not required: <c>rm CMakeCache.txt</c> breaks the cache
    /// just as reliably as <c>rm -rf build</c>, and it is the target that makes this a build question.</summary>
    private static readonly HashSet<string> DeleterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "del", "erase", "rd", "rmdir", "remove-item", "unlink",
    };

    /// <summary>The word every builder spells the same way when it means "delete what you made":
    /// <c>make clean</c>, <c>ninja -t clean</c>, <c>gradle clean</c>, <c>cmake --build build --target clean</c>.
    /// Matched as a whole token, so a directory called <c>clean-room</c> is not a cache break.</summary>
    private const string CleanTarget = "clean";

    /// <summary>What a tool call's <c>command</c> argument does to a build. Absent, blank or unparseable arguments
    /// answer <see cref="BuildGrade.Nothing"/>, which keeps the tier the call already had — the same rule
    /// <see cref="GitCommandGrades.CommandOf"/> follows, and for the same reason: an empty argument is not evidence
    /// of anything, and the tool body refuses it before it can run.</summary>
    public static BuildGrade CommandOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return BuildGrade.Nothing;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("command", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? Grade(value.GetString())
                : BuildGrade.Nothing;
        }
        catch (JsonException)
        {
            return BuildGrade.Nothing;
        }
    }

    /// <summary>Grade a whole command line: the most destructive segment wins, because
    /// <c>cmake --build build &amp;&amp; rm -rf build</c> is a cache break whatever its first half looks like, and a
    /// mixed line graded as the milder answer would be read as permission for the milder thing.</summary>
    public static BuildGrade Grade(string? command) => Grade(command, 0);

    /// <summary>Whether the line builds anything at all — the question a stalled command's wording asks ("was this a
    /// build?"), never the question the approval gate asks. A caller that decides a tier uses
    /// <see cref="Grade"/> or <see cref="CommandOf"/>.</summary>
    public static bool IsBuild(string? command) => Grade(command) != BuildGrade.Nothing;

    /// <summary>Whether the line throws away build output. The one answer the tier is built from.</summary>
    public static bool BreaksCache(string? command) => Grade(command) == BuildGrade.CacheBreak;

    private static BuildGrade Grade(string? command, int depth)
    {
        var worst = BuildGrade.Nothing;
        foreach (var segment in ShellCommandLine.SegmentsOf(command))
        {
            var grade = GradeSegment(segment, depth);
            if (grade > worst) worst = grade;
            if (worst == BuildGrade.CacheBreak) break;
        }
        return worst;
    }

    private static BuildGrade GradeSegment(List<ShellCommandLine.Token> segment, int depth)
    {
        var index = ShellCommandLine.HeadIndex(segment);
        if (index >= segment.Count) return BuildGrade.Nothing;

        var name = ShellCommandLine.ExecutableName(segment[index].Text);
        if (name.Length == 0) return BuildGrade.Nothing;
        var head = GradeProgram(name, segment, index + 1);
        if (head != BuildGrade.Nothing) return head;
        if (depth < 2)
        {
            // The route around the rule, read the way the git classifier reads it: a shell started for one command
            // ran that command, and `timeout 300 cmake --build build` is a compile behind a wrapper's own argument.
            if (ShellCommandLine.IsShell(name))
                return Max(
                    ShellCommandLine.CommandPayload(segment, index + 1) is { } payload
                        ? Grade(payload, depth + 1)
                        : BuildGrade.Nothing,
                    // …and a shell pointed at a script file, which is how the engine's own setup is started
                    // (`pwsh -File setup.ps1 -android`): the payload flag is absent, the builder is an argument.
                    GradeTail(segment, index + 1, depth + 1));
            if (ShellCommandLine.IsPassThrough(name)) return GradeTail(segment, index + 1, depth);
        }
        return BuildGrade.Nothing;
    }

    /// <summary>Scan past a pass-through wrapper's own options for the first program this knows.</summary>
    private static BuildGrade GradeTail(List<ShellCommandLine.Token> segment, int from, int depth)
    {
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (ShellCommandLine.IsEnvironmentAssignment(token)) continue;
            if (token.Text.StartsWith('-') && !token.Quoted) continue;
            var name = ShellCommandLine.ExecutableName(token.Text);
            if (name.Length == 0) continue;
            var grade = GradeProgram(name, segment, i + 1);
            if (grade != BuildGrade.Nothing) return grade;
            if (depth >= 2) continue;
            if (ShellCommandLine.IsShell(name))
                return Max(
                    ShellCommandLine.CommandPayload(segment, i + 1) is { } payload
                        ? Grade(payload, depth + 1)
                        : BuildGrade.Nothing,
                    GradeTail(segment, i + 1, depth + 1));
            if (ShellCommandLine.IsPassThrough(name)) return GradeTail(segment, i + 1, depth + 1);
        }
        return BuildGrade.Nothing;
    }

    /// <summary>What this program, with these arguments, does to a build.</summary>
    private static BuildGrade GradeProgram(string name, List<ShellCommandLine.Token> segment, int from)
    {
        // `dotnet` is decided by its verb, and `git` by its subcommand: `git clean` is the cache break the user
        // named by name, while every other git verb belongs to GitCommandGrades and its own ladder.
        if (string.Equals(name, "dotnet", StringComparison.Ordinal))
        {
            var verb = FirstVerb(segment, from);
            if (DotnetCleanVerbs.Contains(verb)) return BuildGrade.CacheBreak;
            return DotnetBuildVerbs.Contains(verb) ? BuildGrade.BuildRun : BuildGrade.Nothing;
        }
        if (string.Equals(name, "git", StringComparison.Ordinal))
            return string.Equals(FirstVerb(segment, from), CleanTarget, StringComparison.OrdinalIgnoreCase)
                ? BuildGrade.CacheBreak
                : BuildGrade.Nothing;
        if (DeleterNames.Contains(name)) return CacheDeletionGrade(segment, from);
        if (!BuilderNames.Contains(name)) return BuildGrade.Nothing;
        // `cmake` is the one builder whose bare form is decoration: `cmake --version` and `cmake --help` read a
        // version, and a plain `cmake` prints usage. Configuring is a build — it writes CMakeCache.txt and the whole
        // CMakeFiles tree — so `-B`, `--fresh` and `--preset` count with `--build`.
        if (string.Equals(name, "cmake", StringComparison.Ordinal)) return CMakeGrade(segment, from);
        // Every other builder runs when it is named; a `clean` target among its arguments makes it the destructive
        // kind.
        return HasCleanTarget(segment, from) ? BuildGrade.CacheBreak : BuildGrade.BuildRun;
    }

    /// <summary>A deletion of build output is a cache break; a deletion of anything else is not this classifier's
    /// business at all (<see cref="WorkspaceTools.FileWrite"/> and the sandbox guards own that).</summary>
    private static BuildGrade CacheDeletionGrade(List<ShellCommandLine.Token> segment, int from)
    {
        for (var i = from; i < segment.Count; i++)
            if (IsCachePath(segment[i].Text)) return BuildGrade.CacheBreak;
        return BuildGrade.Nothing;
    }

    /// <summary><c>cmake</c>: <c>-U</c> deletes cache entries and a <c>clean</c> target removes what the last run
    /// wrote — both destructive. <c>--build</c>, <c>-B</c>, <c>--fresh</c> and <c>--preset</c> generate or compile;
    /// everything else this build knows of only says something about CMake.</summary>
    private static BuildGrade CMakeGrade(List<ShellCommandLine.Token> segment, int from)
    {
        var grade = BuildGrade.Nothing;
        for (var i = from; i < segment.Count; i++)
        {
            var flag = segment[i].Text;
            if (flag.StartsWith("-U", StringComparison.Ordinal)) return BuildGrade.CacheBreak;
            if (flag.Equals(CleanTarget, StringComparison.OrdinalIgnoreCase)) return BuildGrade.CacheBreak;
            if (flag is "--build" or "--fresh" or "-B" or "--preset") grade = BuildGrade.BuildRun;
        }
        return grade;
    }

    private static bool HasCleanTarget(List<ShellCommandLine.Token> segment, int from)
    {
        for (var i = from; i < segment.Count; i++)
            if (segment[i].Text.Equals(CleanTarget, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The first argument that is an operation rather than an option: git's and dotnet's verb, which sits
    /// behind any number of flags (<c>git -c core.x=1 clean -fdx</c>, <c>dotnet --nologo build</c>).</summary>
    private static string FirstVerb(List<ShellCommandLine.Token> segment, int from)
    {
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                // `git -C build clean -fdx`: -C names a repository, so its directory is the next token and the verb
                // is one further on still. -c's value is an assignment, which the scan below already skips.
                if (token.Text is "-C" or "--work-tree" or "--git-dir") i++;
                continue;
            }
            if (ShellCommandLine.IsEnvironmentAssignment(token)) continue;
            return token.Text;
        }
        return "";
    }

    /// <summary>Whether a path argument names the tree a build made: <c>build</c>, <c>build-windows</c>,
    /// <c>out</c>, <c>.cxx</c>, <c>CMakeFiles</c>, <c>CMakeCache.txt</c>. Only the last segment counts, so
    /// <c>rm src/main.cpp</c> stays a file question, and the name is matched rather than substring-scanned, so
    /// <c>rm rebuild-notes.md</c> does not become a build. A <c>-rf</c>/<c>/s</c> style switch on the deleter is
    /// irrelevant: what makes this the cache is which directory it points at.</summary>
    public static bool IsCachePath(string text)
    {
        if (text.Length == 0 || text[0] == '-') return false;
        var name = Path.GetFileName(text.TrimEnd('/', '\\').Replace('\\', '/'));
        if (name.Length == 0) return false;
        name = name.ToLowerInvariant();
        return name is "out" or ".cxx" or "cmakefiles" or "cmakecache.txt"
               || name == "build" || (name.StartsWith("build", StringComparison.Ordinal)
                   && name.Length > 5 && name[5] is '-' or '_' or '.');
    }

    private static BuildGrade Max(BuildGrade left, BuildGrade right) => left > right ? left : right;
}
