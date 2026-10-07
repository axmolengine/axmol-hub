namespace AxmolHub.Core;

/// <summary>The shell a <c>run_command</c> call is handed to, plus the label shown to the user so an approval
/// card says which shell in which directory is about to run what.</summary>
public sealed record CommandShell(string Executable, IReadOnlyList<string> PrefixArguments, string Label,
                                  bool IsPowerShell = false)
{
    /// <summary>PowerShell writes the machine's OEM codepage unless it is told otherwise, and Hub decodes the
    /// pipe as UTF-8 (<see cref="ProcessRunner"/>), so on a Chinese Windows every CJK line of a tool result
    /// arrives as replacement characters. The setter is wrapped because Hub is a windowed process with no
    /// console, and setting the output encoding of a console that does not exist throws.</summary>
    public const string Utf8Preamble =
        "try{[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)}catch{}";

    public IReadOnlyList<string> ArgumentsFor(string command) =>
        [.. PrefixArguments, IsPowerShell ? Utf8Preamble + ";" + command : command];
}

/// <summary>
/// Shell selection for the assistant's command tool. PowerShell 7 is used on any host where it is actually
/// installed — it is the one that reads and writes UTF-8 by default, and a session that reads a source file
/// through the shell should not need to know that. Windows still falls back to the PowerShell that ships with
/// it, because that is the only one a developer machine is guaranteed to have. Engine scripts are a separate
/// question: <see cref="EngineCommandLine"/> keeps invoking the shell each script declares for itself.
/// </summary>
public static class CommandShells
{
    public static CommandShell For(string host, bool pwshAvailable)
        => string.Equals(host, "windows", StringComparison.OrdinalIgnoreCase)
            ? pwshAvailable
                ? new CommandShell("pwsh",
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"],
                    "PowerShell 7", IsPowerShell: true)
                : new CommandShell(WindowsShell.PowerShell,
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"],
                    "Windows PowerShell", IsPowerShell: true)
            : pwshAvailable
                ? new CommandShell("pwsh", ["-NoProfile", "-NonInteractive", "-Command"], "pwsh", IsPowerShell: true)
                : new CommandShell("/bin/sh", ["-c"], "/bin/sh");

    public static CommandShell ForCurrent() => For(BuildTargets.Host, PwshAvailable());

    public static bool PwshAvailable() => Resolve("pwsh") is not null;

    /// <summary>Finds an executable on PATH without spawning a process: a probe the model can trigger must not
    /// cost a child process, and this keeps the function testable on any host. On Windows a bare name also
    /// matches its <c>.exe</c>, because that is how <c>pwsh</c> is actually installed.</summary>
    public static string? Resolve(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate)) return candidate;
                if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return candidate + ".exe";
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is the environment's problem, not a reason to refuse the command.
            }
        }
        return null;
    }
}
