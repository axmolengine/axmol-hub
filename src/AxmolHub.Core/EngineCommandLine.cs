namespace AxmolHub.Core;

/// <summary>
/// The **single entry point** from the Hub to the engine command line.
///
/// Invocation rule: <c>axmol &lt;subcmd&gt; args</c>. Hub invokes the engine's own
/// <c>tools/cmdline/axmol.ps1</c> directly. Ordinary arguments stay separate in the process
/// argument list; colon-bearing dash values use an encoded inline command to survive pwsh's CLI parser.
///
/// The key difference from before the rework: **no longer builds its own build environment**. Previously the
/// Hub hand-assembled <c>INCLUDE</c>/<c>LIB</c>/<c>PATH</c> in C# (standing in for vcvars) and pinned the
/// tools in its own data root; now the environment inherits the parent process, and the toolchain is prepared
/// by the engine's own setup/1kiss — both system VS and the engine tree's <c>tools/external</c> are located by
/// the engine. The Hub only maps arguments and judges results.
/// </summary>
public sealed class EngineCommandLine(ProcessRunner runner)
{
    /// <summary>Use the engine's supported pwsh version on Windows, never fall back to Windows PowerShell 5.1.</summary>
    private async Task<string> ShellAsync(CancellationToken cancellation)
    {
        var status = HostPowerShell.Probe();
        if (OperatingSystem.IsWindows() && status.State == HostShellState.Unknown)
            status = await HostPowerShell.ProbeVersionAsync(runner, status, cancellation).ConfigureAwait(false);
        if (status.Executable is null || (OperatingSystem.IsWindows() && status.State != HostShellState.Ready))
            throw new InvalidOperationException($"PowerShell 7 (pwsh {HostPowerShell.MinimumVersion} or later) is required to run Axmol. {status.Describe()}");
        return status.Executable;
    }

    private static string CommandScript(EngineEntry engine) => Path.Combine(engine.Path, "tools", "cmdline", "axmol.ps1");

    public async Task<ProcessResult> RunAsync(EngineEntry engine, AxmolInvocation invocation, string workingDirectory,
        CancellationToken cancellation = default, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onLine = null)
    {
        await HostEnvironment.EnsureAsync(cancellation).ConfigureAwait(false);
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        var arguments = new List<string> { "-NonInteractive" };
        if (OperatingSystem.IsWindows()) arguments.AddRange(["-ExecutionPolicy", "Bypass"]);
        // pwsh -File (and -CommandWithArgs) splits values such as -DSEARCH_PATH=C:\\SDK
        // at the colon despite ProcessStartInfo.ArgumentList preserving their boundary.
        // Only for that case, send an encoded inline invocation with quoted value tokens;
        // it still calls the engine script directly and introduces no intermediary file.
        var needsEncodedCommand = invocation.Arguments.Any(value => value.StartsWith('-') && value.Contains(':'));
        if (needsEncodedCommand)
        {
            arguments.AddRange(["-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                InlineInvocation(invocation)))]);
        }
        else
        {
            arguments.AddRange(["-File", CommandScript(engine), invocation.SubCommand]);
            arguments.AddRange(invocation.Arguments);
        }
        var overrides = new Dictionary<string, string>();
        if (environment is not null)
            foreach (var entry in environment) overrides[entry.Key] = entry.Value;
        overrides["AX_ROOT"] = engine.Path;
        if (needsEncodedCommand) overrides["AX_HUB_ENGINE_ENTRY"] = CommandScript(engine);
        if (!Directory.Exists(Path.Combine(engine.Path, ".git")))
        {
            // Release ZIPs have no git history. The engine probes git and otherwise leaves its
            // failed exit code in LASTEXITCODE, even after a successful PowerShell-only command.
            // Keep the old wrapper's process-local git masking without touching the host PATH.
            var separator = Path.PathSeparator;
            var path = overrides.TryGetValue("PATH", out var suppliedPath) ? suppliedPath : HostEnvironment.PathValue;
            overrides["PATH"] = string.Join(separator, path.Split(separator, StringSplitOptions.RemoveEmptyEntries)
                .Where(directory => !File.Exists(Path.Combine(directory, "git.exe"))
                                    && !File.Exists(Path.Combine(directory, "git"))));
        }

        runner.Write("axmol: " + invocation);
        // environment: null = inherit the parent environment plus the shared macOS host PATH. The engine depends
        // on system VS / its own tools/external. When environment is passed it acts only as an **override**
        // (e.g. ANDROID_SERIAL to select a device), not as a rebuilt environment.
        // The timeout semantics = how long with no output before judging a stall (default 10 minutes), not a
        // wall-clock cap on the whole command; compiling the whole engine never times out as long as it keeps
        // printing, so don't pass an arbitrary 2-hour total here.
        var result = await runner.RunAsync(await ShellAsync(cancellation).ConfigureAwait(false), arguments, workingDirectory,
            environment: null, cancellation, timeout, overrides: overrides, onLine: onLine).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"axmol {invocation.SubCommand} failed (exit {result.ExitCode}).\n{result.Error}\n{result.Output}");
        }

        return result;
    }

    private static string InlineInvocation(AxmolInvocation invocation)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var tokens = new List<string> { Quote(invocation.SubCommand) };
        var expectsValue = false;
        foreach (var value in invocation.Arguments)
        {
            var isOption = !expectsValue && value.StartsWith('-')
                && value.TrimStart('-') is { Length: > 0 } name && name.All(char.IsLetterOrDigit);
            tokens.Add(isOption ? value : Quote(value));
            expectsValue = isOption && value is "-p" or "-d" or "-l" or "-m" or "-a" or "-cc"
                or "-xc" or "-xb" or "-sdk" or "-t";
        }
        return "& $env:AX_HUB_ENGINE_ENTRY " + string.Join(' ', tokens) + "; exit $LASTEXITCODE";
    }

    /// <summary>
    /// The official environment-preparation entry point <c>setup.ps1</c>.
    ///
    /// **Deliberately does not throw**: the caller must classify by the engine's output — with Developer
    /// Mode off, setup.ps1 does <c>exit 0</c> yet installs nothing; relying on exceptions alone cannot catch
    /// this false success.
    /// Passes <c>-hub</c> so engine setup keeps <c>AX_ROOT</c> and <c>PATH</c> process-local.
    /// Timeout semantics = 10 minutes of no output counts as a stall (the <see cref="ProcessRunner"/> default);
    /// while setup downloads a GB-scale toolchain it never times out as long as it keeps printing progress,
    /// so no total time cap is set.
    /// </summary>
    public async Task<ProcessResult> RunSetupAsync(EngineEntry engine, SetupOptions options, CancellationToken cancellation = default,
        Action<string>? onLine = null)
    {
        await HostEnvironment.EnsureAsync(cancellation).ConfigureAwait(false);
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        var shell = await ShellAsync(cancellation).ConfigureAwait(false);
        var setupScript = Path.Combine(engine.Path, "setup.ps1");
        var prefix = new List<string> { "-NonInteractive" };
        if (OperatingSystem.IsWindows()) prefix.AddRange(["-ExecutionPolicy", "Bypass"]);
        // Parse the script without executing it. A string search can mistake a comment or a nested
        // function parameter for -hub; the PowerShell AST checks the script's actual top-level parameters.
        const string inspect = "$tokens=$null; $errors=$null; "
            + "$ast=[System.Management.Automation.Language.Parser]::ParseFile($env:AX_HUB_SETUP_PATH,[ref]$tokens,[ref]$errors); "
            + "if($errors.Count -gt 0 -or $null -eq $ast.ParamBlock -or 'hub' -notin @($ast.ParamBlock.Parameters.Name.VariablePath.UserPath)) "
            + "{ [Console]::Error.WriteLine('This engine setup.ps1 does not support -hub. Refusing to risk persistent environment changes.'); exit 2 }";
        var environment = new Dictionary<string, string> { ["AX_ROOT"] = engine.Path, ["AX_HUB_SETUP_PATH"] = setupScript };
        var inspection = await runner.RunAsync(shell, prefix.Concat(["-Command", inspect]), engine.Path,
            environment: null, cancellation, overrides: environment).ConfigureAwait(false);
        if (inspection.ExitCode != 0) return inspection;

        environment.Remove("AX_HUB_SETUP_PATH");
        var arguments = prefix.Concat(SetupScriptArguments(setupScript, options)).ToArray();
        runner.Write($"axmol setup: {string.Join(' ', arguments)}");
        return await runner.RunAsync(shell, arguments, engine.Path, environment: null, cancellation,
            overrides: environment, onLine: onLine).ConfigureAwait(false);
    }

    /// <summary>Builds arguments for the engine's setup script, including its no-persistent-AX_ROOT switch.</summary>
    public static IReadOnlyList<string> SetupScriptArguments(string setupScript, SetupOptions options)
    {
        var arguments = new List<string> { "-File", setupScript, "-hub" };
        if (options.Platform is { Length: > 0 } platform) arguments.AddRange(["-p", platform]);
        if (options.UpdateAdt) arguments.Add("-updateAdt");
        return arguments;
    }
}
