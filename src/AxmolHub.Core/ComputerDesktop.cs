namespace AxmolHub.Core;

/// <summary>One shared desktop, regardless of how many conversations are running.</summary>
internal static class ComputerDesktop
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _generation;
    internal static long Generation => Interlocked.Read(ref _generation);
    internal static void Invalidate() => Interlocked.Increment(ref _generation);

    internal static IDisposable? TryEnter()
        => Gate.Wait(0) ? new Lease() : null;

    private sealed class Lease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Gate.Release();
        }
    }
}
