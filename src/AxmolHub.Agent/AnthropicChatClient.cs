using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>Leaves streaming and signed thinking aggregation to the official SDK. Unsigned reasoning from
/// another protocol is display history, not a Claude thinking block; it cannot acquire a fabricated signature.</summary>
internal sealed class AnthropicChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => InnerClient.GetResponseAsync(Replay(messages), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aggregator = new MessageContentAggregator();
        var trailing = new List<ChatResponseUpdate>();
        ChatResponseUpdate? current = null;
        async IAsyncEnumerable<RawMessageStreamEvent> Events()
        {
            await foreach (var update in InnerClient.GetStreamingResponseAsync(Replay(messages), options, cancellationToken))
            {
                current = update;
                if (update.RawRepresentation is RawMessageStreamEvent raw) yield return raw;
                else trailing.Add(update);
            }
        }
        await foreach (var raw in aggregator.CollectAsync(Events()))
        {
            var update = current!;
            if (raw.Value is RawMessageStopEvent)
                update.Contents.Add(new ClaudeResponseContent(aggregator.Message().Content
                    .Select(block => block.Json.Clone()).ToList()));
            // The SDK does not turn an error SSE frame into ErrorContent. The transport observer records
            // its provider code in the shared retry scope without changing the bytes.
            if (RequestRetryScope.Current.Value?.StreamFailure is { } failure) throw failure;
            yield return update;
        }
        foreach (var update in trailing) yield return update;
    }

    private static IEnumerable<ChatMessage> Replay(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            var contents = message.Contents.Where(part => part is not TextReasoningContent
                || part is TextReasoningContent { ProtectedData: { Length: > 0 } }).ToList();
            if (contents.Count == 0) continue;
            var copy = message.Clone();
            copy.Contents = contents;
            // Hub stores a batch as call/result pairs for approvals. Messages requires all calls in the
            // assistant response before its user-role results. Rejoin only adjacent pairs from that batch.
            var previousAssistant = result.LastOrDefault()?.Role == ChatRole.Tool
                ? result.ElementAtOrDefault(result.Count - 2) : result.LastOrDefault();
            if (copy.Role == ChatRole.Assistant && contents.Any(part => part is FunctionCallContent)
                && previousAssistant?.Role == ChatRole.Assistant
                && previousAssistant.Contents.Any(part => part is FunctionCallContent)
                && copy.AdditionalProperties?.TryGetValue("hub:tool-batch", out var batch) == true
                && previousAssistant.AdditionalProperties?.TryGetValue("hub:tool-batch", out var previousBatch) == true
                && Equals(batch, previousBatch))
            {
                foreach (var part in contents.Where(part => part is not TextReasoningContent))
                    previousAssistant.Contents.Add(part);
            }
            else if (copy.Role == ChatRole.Tool && result.LastOrDefault()?.Role == ChatRole.Tool)
            {
                foreach (var part in contents) result[^1].Contents.Add(part);
            }
            else result.Add(copy);
        }
        foreach (var message in result.Where(message => message.Role == ChatRole.Assistant))
        {
            var blocks = message.Contents.OfType<ClaudeResponseContent>().LastOrDefault()?.Blocks;
            if (blocks is null && message.AdditionalProperties?.TryGetValue(ClaudeResponseContent.HistoryKey, out var stored) == true)
                blocks = stored as List<JsonElement>;
            if (blocks is { Count: > 0 })
                message.Contents = blocks.Select(block => (AIContent)new ClaudeWireContent
                {
                    RawRepresentation = JsonSerializer.Deserialize<ContentBlockParam>(block)
                        ?? throw new InvalidOperationException("Stored Messages block is invalid."),
                }).ToList();
        }
        return result;
    }
}
