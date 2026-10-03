namespace AxmolHub.Core;

/// <summary>
/// The shape of a single engine cmdline invocation. The invocation rule is <c>axmol &lt;subcmd&gt; args</c>.
/// </summary>
public sealed record AxmolInvocation(string SubCommand, string[] Arguments)
{
    /// <summary>
    /// Used only for logging and <c>plan</c>'s human-readable output.
    ///
    /// Values containing spaces are wrapped in <c>"…"</c>: it both makes boundaries clear to a human reader
    /// and reminds any future code that "reassembles <see cref="Arguments"/> back into a single shell command
    /// string" — the <c>-xc</c> value **must** be quoted, otherwise spaces would split it at the shell layer,
    /// and by the time it reaches the engine <c>build.ps1</c>'s "consume one argument at a time" loop only half
    /// would remain, causing mysterious build failures.
    /// </summary>
    public override string ToString() => "axmol " + SubCommand + " " + string.Join(' ',
        Arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
}

/// <summary>
/// The Hub's build target → engine cmdline arguments.
///
/// The argument conventions do **not come from guessing**, but from the engine's own CI invocations
/// (<c>.github/workflows/build.yml</c>):
/// <code>
///   axmol -p win32 -a x64 -xc '-DAX_ENABLE_VR=ON,-DAX_ENABLE_OPENXR=ON' -O3 -t cpp-tests
///   axmol -d .\HelloCpp -xc '-DAX_PREBUILT_DIR=build' -O3
///   axmol run -p win32 -a arm64 -t unit-tests -O3
///   ./tools/cmdline/axmol -p ios -a arm64 -sdk simulator -t cpp-tests
///   ./tools/cmdline/axmol -p android -a arm64 -t cpp-tests
/// </code>
/// Two easy mistakes: <c>-xc</c> takes **one** comma-separated string (not multiple arguments);
/// Release uses <c>-O3</c> (not <c>-DCMAKE_BUILD_TYPE</c>).
/// </summary>
public static class AxmolCommandMap
{
    /// <summary>Hub target → the <c>-p</c> platform name and <c>-a</c> architecture (an empty string means <c>-a</c> is not passed).</summary>
    public static (string Platform, string Architecture) Target(BuildTarget target) => target.Id switch
    {
        "windows-x64" => ("win32", "x64"),
        "windows-arm64" => ("win32", "arm64"),
        "uwp-x64" => ("winuwp", "x64"),
        "android-arm64" => ("android", "arm64"),
        "android-x64" => ("android", "x64"),
        "linux-x64" => ("linux", "x64"),
        "linux-arm64" => ("linux", "arm64"),
        "macos-arm64" => ("osx", "arm64"),
        "macos-x64" => ("osx", "x64"),
        "ios-arm64" => ("ios", "arm64"),
        "ios-simulator-arm64" => ("ios", "arm64"),
        "ios-simulator-x64" => ("ios", "x64"),
        "tvos-arm64" => ("tvos", "arm64"),
        "tvos-simulator-arm64" => ("tvos", "arm64"),
        "tvos-simulator-x64" => ("tvos", "x64"),
        "wasm32" => ("wasm", ""),
        "wasm64" => ("wasm64", ""),
        _ => throw new InvalidDataException($"No axmol platform is mapped for target: {target.Id}"),
    };

    /// <summary>Platform module id → the engine <c>-p</c> platform name (module preparation = <c>setup.ps1 -p &lt;platform&gt;</c>).</summary>
    /// <summary>
    /// The configuration switch. The engine uses the <c>n</c> in <c>-O&lt;n&gt;</c> as an index to pick the
    /// build type (<c>1k/1kiss.ps1</c>: <c>@('Debug','MinSizeRel','RelWithDebInfo','Release')[$options.O]</c>):
    /// <c>-O0</c>=Debug, <c>-O3</c>=Release. <b>Without <c>-O</c> the engine defaults to <c>RelWithDebInfo</c></b>,
    /// so here <c>Debug</c> must explicitly pass <c>-O0</c>, otherwise the Hub's "Debug" would silently build
    /// as RelWithDebInfo.
    /// </summary>
    private static void AddConfiguration(ICollection<string> arguments, string configuration)
    {
        BuildConfigurations.Validate(configuration);
        arguments.Add(configuration == "Release" ? "-O3" : "-O0");
    }

    private static void AddTarget(ICollection<string> arguments, BuildTarget target)
    {
        var (platform, architecture) = Target(target);
        arguments.Add("-p");
        arguments.Add(platform);
        if (architecture.Length > 0)
        {
            arguments.Add("-a");
            arguments.Add(architecture);
        }

        // The engine won't guess an iOS arm64 as the simulator; it must be declared explicitly.
        if (target.Simulator)
        {
            arguments.Add("-sdk");
            arguments.Add("simulator");
        }
    }

    public static AxmolInvocation Build(BuildTarget target, string projectDirectory, string configuration, bool configureOnly, IEnumerable<string>? additionalCmake = null)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);
        if (configureOnly) arguments.Add("-c");
        AddConfiguration(arguments, configuration);
        AddCmake(arguments, additionalCmake);
        arguments.Add("-d");
        arguments.Add(projectDirectory);
        return new("build", arguments.ToArray());
    }

    /// <summary>
    /// **Engine root build**: compiles the engine into prebuilt libraries reusable by projects (the aggregate
    /// target <c>axmol-sdk</c>).
    ///
    /// Deliberately does **not reuse <see cref="Build"/>**: that one always appends <c>-d &lt;project&gt;</c>,
    /// whereas an engine root build has no project directory (the CI golden path is
    /// `axmol -p win32 -a x64 -xc '…' -O3`, without <c>-d</c>). The working directory is set to the engine
    /// root by the caller.
    /// </summary>
    public static AxmolInvocation BuildEngine(BuildTarget target, string configuration)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);               // -p win32 -a x64
        AddConfiguration(arguments, configuration); // Release → -O3; Debug → not passed
        return new("build", arguments.ToArray());
    }

    public static AxmolInvocation Run(BuildTarget target, string projectDirectory, string configuration)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);
        AddConfiguration(arguments, configuration);
        arguments.Add("-d");
        arguments.Add(projectDirectory);
        return new("run", arguments.ToArray());
    }

    public static AxmolInvocation Deploy(BuildTarget target, string projectDirectory, string configuration)
    {
        var arguments = new List<string>();
        AddTarget(arguments, target);
        AddConfiguration(arguments, configuration);
        arguments.Add("-d");
        arguments.Add(projectDirectory);
        return new("deploy", arguments.ToArray());
    }

    /// <summary>
    /// <c>-xc</c> takes **one** argument: multiple cmake options are joined into a single comma-separated
    /// string.
    ///
    /// **Why it must be a single argument**: the engine <c>plugins/build.ps1</c> argument loop "consumes one
    /// at a time" — the first token after <c>-xc</c> is stored into <c>$options.xc</c>, then split by
    /// <c>Split(',')</c>. The moment the <c>-xc</c> value is split into multiple tokens (e.g. the value
    /// contains spaces, or someone passes it as multiple arguments), only the first token makes it into
    /// <c>$options.xc</c>, and the rest become orphan arguments in <c>$unhandled_args</c>. The final CMake
    /// command ends up missing options, showing up as "mysterious build failures" that are extremely hard to
    /// diagnose. Therefore this must <c>string.Join(',', …)</c> into a single argument without spaces; the
    /// outer shell escaping is also backstopped by the quoting in <see cref="AxmolInvocation.ToString"/> (see
    /// its docs).
    /// </summary>
    private static void AddCmake(ICollection<string> arguments, IEnumerable<string>? options)
    {
        var values = (options ?? []).Where(option => option.Length > 0).ToArray();
        if (values.Length == 0) return;
        arguments.Add("-xc");
        arguments.Add(string.Join(',', values));
    }
}
