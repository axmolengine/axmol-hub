using System.ClientModel.Primitives;

namespace AxmolHub.Agent;

/// <summary>
/// The retry policy's wait, reported to whoever owns the run.
///
/// <para><see cref="ClientRetryPolicy"/> does its sleeping inside the client pipeline, below the streaming
/// enumerator, so from Hub's side a provider that answers 429 and a provider that has stopped answering look
/// identical: silence. The app's own inactivity deadline could not tell them apart either, and it usually fired
/// first — a run then reported "the stream stopped" for what was really the provider's <c>Retry-After</c> being
/// obeyed, and the user was sent to check a network that was fine.</para>
///
/// <para><see cref="Wait"/> and <see cref="WaitAsync"/> are the only documented hooks that span the delay, so they
/// are where the notice has to come from. Decisions reject exhausted quota and permanent failures before
/// the SDK retries; explicit server delays win over exponential jitter, and a long delay parks goal automation
/// instead of holding a run slot asleep.</para>
/// </summary>
public sealed class IdleAwareRetryPolicy(int maxRetries, Action<int, TimeSpan>? onWait,
    TimeSpan? maxInlineWait = null) : ClientRetryPolicy(Math.Clamp(maxRetries, 0, 10))
{
    /// <summary>How many times the policy has tried to send the request, taken from
    /// <see cref="ClientRetryPolicy.GetNextDelay"/> so the wait can name the attempt that follows it. The base
    /// class passes the delay and the count to separate methods, and only the count arrives first.</summary>
    private readonly AsyncLocal<int> _tried = new();

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        if (message.CancellationToken.IsCancellationRequested || exception is OperationCanceledException) return false;
        if (message.Response is { Status: >= 400 } response)
        {
            response.BufferContent(message.CancellationToken);
            if (ProviderFailure.QuotaExhausted(response.Content.ToString())) return false;
            if (response.Status is not (408 or 429 or 500 or 502 or 503 or 504)) return false;
        }
        return base.ShouldRetry(message, exception);
    }

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception)
    {
        if (message.Response is { Status: >= 400 } response)
            await response.BufferContentAsync(message.CancellationToken).ConfigureAwait(false);
        return ShouldRetry(message, exception);
    }

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount)
    {
        _tried.Value = tryCount;
        TimeSpan? specified = null;
        if (message.Response is { } response)
        {
            foreach (var name in new[] { "retry-after-ms", "x-ms-retry-after-ms", "Retry-After" })
                if (response.Headers.TryGetValue(name, out var value)
                    && ProviderFailure.RetryAfter(value, DateTimeOffset.UtcNow, name.EndsWith("-ms")) is { } delay)
                { specified = delay; break; }
        }
        // The installed SDK supplies exponential backoff but no jitter. Honor an explicit server delay exactly.
        var next = specified ?? TimeSpan.FromMilliseconds(
            Math.Min(30_000, base.GetNextDelay(message, tryCount).TotalMilliseconds)
            * (0.75 + Random.Shared.NextDouble() * 0.5));
        if (maxInlineWait is { } ceiling && next > ceiling) throw new ProviderCooldownException(next);
        return next;
    }

    protected override void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        Notice(delay);
        try
        {
            base.Wait(delay, cancellationToken);
        }
        finally
        {
            Release();
        }
    }

    protected override async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Notice(delay);
        try
        {
            await base.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private void Notice(TimeSpan delay) => onWait?.Invoke(_tried.Value + 1, delay);

    private void Release() => onWait?.Invoke(0, TimeSpan.Zero);
}
