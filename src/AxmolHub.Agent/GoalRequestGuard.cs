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
        var hasContent = false;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
            {
                hasContent |= update.Contents.Any(content => content is not UsageContent
                    && content is not TextContent { Text: null or "" });
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
            // An unsuccessful request with no response content releases its reservation. A completed or
            // partially streamed response without usage keeps an estimate; reported usage always wins.
            if (afterRequest is not null)
                await afterRequest(estimate, hasUsage ? reported : !completed && !hasContent ? 0 : -1, completed)
                    .ConfigureAwait(false);
        }
    }
}

public sealed class GoalGuardException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
