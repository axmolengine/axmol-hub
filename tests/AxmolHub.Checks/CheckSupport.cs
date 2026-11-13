using AxmolHub.Core;

internal static class CheckSupport
{
    internal static int count;
    // Build is delegated to the engine command line, including the checks.
    internal static EngineCommandLine EngineCli(ProcessRunner runner) => new(runner);
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
