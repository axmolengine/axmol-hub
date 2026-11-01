namespace AxmolHub.Core;

/// <summary>Shared provider retry defaults for preferences, the transport, run admission and checks.</summary>
public static class ProviderRetryDefaults
{
    public const int MaxRetries = 5;
    public const int MaxRetriesLimit = 10;
    public const double JitterFraction = 0.25;

    public static readonly TimeSpan RateLimitBaseDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BackoffCeiling = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan GoalMaxInlineWait = BackoffCeiling;
    public static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(120);
}
