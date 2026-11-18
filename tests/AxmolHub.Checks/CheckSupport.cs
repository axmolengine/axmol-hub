using AxmolHub.Core;

internal static class CheckSupport
{
    internal static int count;
    // Build is delegated to the engine command line, including the checks.
    internal static EngineCommandLine EngineCli(ProcessRunner runner) => new(runner);
    internal static EngineEntry CommandEngine(string root, string channel, string script = "exit 0\n")
    {
        foreach (var directory in new[] { "core", "tools/cmdline", "templates/cpp", "1k" }) Directory.CreateDirectory(Path.Combine(root, directory));
        File.WriteAllText(Path.Combine(root, "core/axmolver.h.in"), "#define AX_VERSION_MAJOR 2\n#define AX_VERSION_MINOR 11\n#define AX_VERSION_PATCH 5\n");
        File.WriteAllText(Path.Combine(root, "templates/cpp/axproj-template.json"), "{}");
        File.WriteAllText(Path.Combine(root, "1k/1kiss.ps1"), "");
        File.WriteAllText(Path.Combine(root, "tools/cmdline/axmol.ps1"), script);
        return StateStore.ValidateEngine(root, channel);
    }

    internal static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAILED: " + name);
        count++;
        Console.WriteLine("PASS: " + name);
    }

    internal static async Task Reject<T>(Func<Task> action, string name)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            Check(true, name);
            return;
        }

        throw new Exception("FAILED: " + name);
    }
}
