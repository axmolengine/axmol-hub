using System.Runtime.CompilerServices;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

[assembly: InternalsVisibleTo("AxmolHub.Checks")]

namespace AxmolHub.Agent;

/// <summary>One logical model request shares this budget with its HTTP transport retries.</summary>
public sealed class RequestRetryOptions
{
    public int MaxRetries { get; init; } = ProviderRetryDefaults.MaxRetries;
    public Action<int, TimeSpan>? OnWait { get; init; }
    public bool RetryEmpty { get; init; } = true;
    // Only the first request of a rebuilt segment resumes the prior failed request's allowance.
    internal int RetriesUsed { get; init; }
    internal Action<int>? OnRequestEnded { get; init; }
    // Checked again at the transport boundary, including SDK and credential-refresh retries.
    public Func<Task>? ValidateSelection { get; init; }
    // Injected only by offline checks; production always uses cancellable Task.Delay.
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

internal sealed class RequestRetryScope(RequestRetryOptions options, int retriesUsed = 0)
{
    internal static readonly AsyncLocal<RequestRetryScope?> Current = new();
    public int Maximum { get; } = Math.Clamp(options.MaxRetries, 0, ProviderRetryDefaults.MaxRetriesLimit);
    public int Retries { get; private set; } = Math.Clamp(retriesUsed, 0,
        Math.Clamp(options.MaxRetries, 0, ProviderRetryDefaults.MaxRetriesLimit));
    public TimeSpan? TransportDelay { get; set; }
    public ProviderResponseException? StreamFailure { get; set; }
    public TimeSpan Waited { get; private set; }
    public bool CanRetry => Retries < Maximum;
    internal Task ValidateSelectionAsync() => options.ValidateSelection?.Invoke() ?? Task.CompletedTask;

    public TimeSpan Reserve(TimeSpan? specified)
    {
        var delay = specified ?? ProviderFailure.Backoff(Retries);
        Retries++;
        return delay;
    }

    public async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            options.OnWait?.Invoke(Retries + 1, delay);
            await options.Delay(delay, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            Waited += started.Elapsed;
            options.OnWait?.Invoke(0, TimeSpan.Zero);
        }
    }
}

/// <summary>Terminal outcome of bounded recovery, not permission for the goal scheduler to retry it again.</summary>
public sealed class RequestRetryException(Exception cause, int retries, TimeSpan waited, bool partial)
    : Exception(cause.Message, cause)
{
    public int Retries { get; } = retries;
    public TimeSpan Waited { get; } = waited;
    public bool Partial { get; } = partial;
}

/// <summary>Inside tool invocation: retries only the failed model request, never the tools already executed.
/// Tool calls remain provisional until EOF. Visible text, reasoning and hosted-tool activity stay streaming,
/// but once delivered they disable replay so two attempts can never be concatenated into one answer.</summary>
internal sealed class RequestRetryClient(IChatClient inner, RequestRetryOptions options, Action? activity)
    : DelegatingChatClient(inner)
{
    private int _retriesUsed = options.RetriesUsed;

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var scope = new RequestRetryScope(options, Interlocked.Exchange(ref _retriesUsed, 0));
        var previous = RequestRetryScope.Current.Value;
        RequestRetryScope.Current.Value = scope;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await scope.ValidateSelectionAsync().ConfigureAwait(false);
                scope.TransportDelay = null;
                scope.StreamFailure = null;
                var pending = new List<ChatResponseUpdate>();
                var exposed = false;
                var answered = false;
                string? finish = null;
                Exception? failure = null;
                await using (var stream = InnerClient.GetStreamingResponseAsync(history, chatOptions?.Clone(), cancellationToken)
                    .GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        ChatResponseUpdate update;
                        try
                        {
                            if (!await stream.MoveNextAsync().ConfigureAwait(false)) break;
                            update = stream.Current;
                            activity?.Invoke();
                            if (scope.StreamFailure is { } reported) throw reported;
                            if (update.Contents.OfType<ErrorContent>().FirstOrDefault() is { } error)
                                throw ProviderFailure.FromStream(error);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException and not ModelSelectionChangedException)
                        {
                            failure = scope.StreamFailure ?? ex;
                            break;
                        }
                        finish = update.FinishReason?.Value ?? finish;
                        var text = update.Contents.OfType<TextContent>().Any(part => !string.IsNullOrEmpty(part.Text));
                        var call = update.Contents.Any(part => part is FunctionCallContent);
                        var reasoned = update.Contents.Any(part => part is Microsoft.Extensions.AI.TextReasoningContent);
                        answered |= text || call || reasoned;
                        if (call || pending.Count > 0) pending.Add(update);
                        else
                        {
                            exposed |= update.Contents.Any(part => part is not UsageContent
                                && part is not TextContent { Text: null or "" });
                            yield return update;
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                failure ??= scope.StreamFailure;
                // Filtering and output-budget stops are not transient endpoint failures.
                if (failure is null && !answered && !exposed && options.RetryEmpty
                    && ProviderFailure.RetryableMissingAnswer(finish, askedForTool: false, spinStopped: false))
                    failure = new ProviderResponseException("The endpoint returned no answer or tool call.", "empty_response");
                if (failure is null)
                {
                    foreach (var update in pending) yield return update;
                    yield break;
                }
                if (!ProviderFailure.Recoverable(failure))
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                if (exposed || !scope.CanRetry)
                    throw new RequestRetryException(failure, scope.Retries, scope.Waited, exposed);
                await scope.WaitAsync(scope.Reserve(ProviderFailure.WaitFor(failure) ?? scope.TransportDelay), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            RequestRetryScope.Current.Value = previous;
            options.OnRequestEnded?.Invoke(scope.Retries);
        }
    }
}
