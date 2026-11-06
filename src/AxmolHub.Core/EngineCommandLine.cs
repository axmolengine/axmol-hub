namespace AxmolHub.Core;

/// <summary>
/// The **single entry point** from the Hub to the engine command line.
///
/// Invocation rule: <c>axmol &lt;subcmd&gt; args</c>. Actual forwarding goes through
/// <c>Invoke-Axmol.ps1</c>, distributed alongside the exe; it only sets <c>AX_ROOT</c> and then hands the
/// arguments to <c>&lt;engine&gt;/tools/cmdline/axmol.ps1</c>.
///
/// The key difference from before the rework: **no longer builds its own build environment**. Previously the
/// Hub hand-assembled <c>INCLUDE</c>/<c>LIB</c>/<c>PATH</c> in C# (standing in for vcvars) and pinned the
/// tools in its own data root; now the environment inherits the parent process, and the toolchain is prepared
/// by the engine's own setup/1kiss — both system VS and the engine tree's <c>tools/external</c> are located by
/// the engine. The Hub only maps arguments and judges results.
/// </summary>
public sealed class EngineCommandLine(ProcessRunner runner, string wrapper)
{
    /// <summary>Uses the host PowerShell executable discovered by the shared host-shell probe.</summary>
    private static string Shell
    {
        get
        {
            if (OperatingSystem.IsWindows()) return WindowsShell.PowerShell;
            var status = HostPowerShell.Probe();
            return status.Executable
                ?? throw new InvalidOperationException("PowerShell 7 (pwsh) was not found. Install PowerShell 7 or add its executable to PATH, then retry.");
        }
    }

    /// <summary>The environment-preparation wrapper script, distributed in the same directory as the <c>axmol</c> wrapper.</summary>
    private string SetupWrapper => Path.Combine(Path.GetDirectoryName(wrapper)!, "Invoke-AxmolSetup.ps1");

    public async Task<ProcessResult> RunAsync(EngineEntry engine, AxmolInvocation invocation, string workingDirectory,
        CancellationToken cancellation = default, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onLine = null)
    {
        await HostEnvironment.EnsureAsync(cancellation).ConfigureAwait(false);
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        var arguments = new List<string> { "-NoProfile", "-NonInteractive" };
        if (OperatingSystem.IsWindows()) arguments.AddRange(["-ExecutionPolicy", "Bypass"]);
        // PowerShell -File can split a dash-prefixed value at ':' even when ArgumentList preserves
        // its boundary (e.g. -DSEARCH_PATH=C:\\SDK). Encode the argv array, not shell source.
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new[] { invocation.SubCommand }.Concat(invocation.Arguments))));
        arguments.AddRange(["-File", wrapper, "-EngineRoot", engine.Path, "-InvocationBase64", payload]);

        runner.Write("axmol: " + invocation);
        // environment: null = inherit the parent environment plus the shared macOS host PATH. The engine depends
        // on system VS / its own tools/external. When environment is passed it acts only as an **override**
        // (e.g. ANDROID_SERIAL to select a device), not as a rebuilt environment.
        // The timeout semantics = how long with no output before judging a stall (default 10 minutes), not a
        // wall-clock cap on the whole command; compiling the whole engine never times out as long as it keeps
        // printing, so don't pass an arbitrary 2-hour total here.
        var result = await runner.RunAsync(Shell, arguments, workingDirectory, environment: null, cancellation, timeout, overrides: environment, onLine: onLine);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"axmol {invocation.SubCommand} failed (exit {result.ExitCode}).\n{result.Error}\n{result.Output}");
        }

        return result;
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
        var arguments = new List<string> { "-NoProfile", "-NonInteractive" };
        if (OperatingSystem.IsWindows()) arguments.AddRange(["-ExecutionPolicy", "Bypass"]);
        arguments.AddRange(SetupScriptArguments(SetupWrapper, engine.Path, options));

        runner.Write($"axmol setup: {string.Join(' ', arguments)}");
        return await runner.RunAsync(Shell, arguments, engine.Path, environment: null, cancellation, onLine: onLine).ConfigureAwait(false);
    }

    /// <summary>Builds the engine setup-wrapper arguments, including the Hub-specific no-persistent-AX_ROOT switch.</summary>
    public static IReadOnlyList<string> SetupScriptArguments(string setupWrapper, string engineRoot, SetupOptions options)
    {
        var arguments = new List<string> { "-File", setupWrapper, "-EngineRoot", engineRoot, "-hub" };
        if (options.Platform is { Length: > 0 } platform) arguments.AddRange(["-p", platform]);
        if (options.UpdateAdt) arguments.Add("-updateAdt");
        return arguments;
    }
}
