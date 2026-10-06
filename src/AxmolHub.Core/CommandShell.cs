namespace AxmolHub.Core;

/// <summary>The shell a <c>run_command</c> call is handed to, plus the label shown to the user so an approval
/// card says which shell in which directory is about to run what.</summary>
public sealed record CommandShell(string Executable, IReadOnlyList<string> PrefixArguments, string Label)
{
    public IReadOnlyList<string> ArgumentsFor(string command) => [.. PrefixArguments, command];
}

/// <summary>
/// Shell selection for the assistant's command tool. Windows uses the PowerShell that ships with Windows — the
/// same entry point <see cref="EngineCommandLine"/> uses, so there is one notion of "the shell" in the Hub.
/// Unix prefers <c>pwsh</c> when it is actually installed: the engine's own <c>pwshi.sh</c> guarantees it only
/// for engine work, which a general command tool cannot assume.
/// </summary>
public static class CommandShells
{
    public static CommandShell For(string host, bool pwshAvailable)
        => string.Equals(host, "windows", StringComparison.OrdinalIgnoreCase)
            ? new CommandShell(WindowsShell.PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"], "Windows PowerShell")
            : pwshAvailable
                ? new CommandShell("pwsh", ["-NoProfile", "-NonInteractive", "-Command"], "pwsh")
                : new CommandShell("/bin/sh", ["-c"], "/bin/sh");

    public static CommandShell ForCurrent() => For(BuildTargets.Host, PwshAvailable());

    public static bool PwshAvailable() => !OperatingSystem.IsWindows() && Resolve("pwsh") is not null;

    /// <summary>Finds an executable on PATH without spawning a process: a probe the model can trigger must not
    /// cost a child process, and this keeps the function testable on any host.</summary>
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
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is the environment's problem, not a reason to refuse the command.
            }
        }
        return null;
    }
}
