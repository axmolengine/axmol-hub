using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>Runs once per model request, inside tool invocation and outside transport retry.</summary>
internal sealed class GoalRequestGuard(IChatClient inner, int schemaTokens, int fallbackOutput,
    Func<long, Task> beforeRequest, Func<long, long, bool, Task>? afterRequest) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        options = options?.Clone() ?? new ChatOptions();
        options.MaxOutputTokens ??= fallbackOutput;
        var estimate = (long)ToolLoopContextGuard.EstimateTokens(list) + schemaTokens
            + (options?.MaxOutputTokens ?? fallbackOutput);
        cancellationToken.ThrowIfCancellationRequested();
        await beforeRequest(estimate).ConfigureAwait(false);
        long reported = 0;
        var completed = false;
        var hasUsage = false;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
            {
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    hasUsage = true;
                    reported = Math.Max(reported, Math.Max(0, usage.Details.InputTokenCount ?? 0)
                        + Math.Max(0, usage.Details.OutputTokenCount ?? 0));
                }
                yield return update;
            }
            completed = true;
        }
        finally
        {
            // No usage keeps the reservation as an explicit estimate, including interrupted responses.
            if (afterRequest is not null)
                await afterRequest(estimate, hasUsage ? reported : -1, completed).ConfigureAwait(false);
        }
    }
}

public sealed class GoalGuardException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
