using System.Runtime.CompilerServices;
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
        var thinking = new ClaudeThinkingAccumulator();
        await foreach (var update in InnerClient.GetStreamingResponseAsync(Replay(messages), options, cancellationToken))
        {
            // MEAI merges one trailing signature with its preceding text, but multiple signature deltas
            // become separate blocks. Emit the completed signature once, at the original block boundary.
            if (update.RawRepresentation is RawMessageStreamEvent { Value: RawContentBlockDeltaEvent { Delta.Value: SignatureDelta } })
                foreach (var part in update.Contents.OfType<TextReasoningContent>()) part.ProtectedData = null;
            if (thinking.Push(update.RawRepresentation) is { } block)
            {
                if (!block.Redacted)
                    update.Contents.Add(new TextReasoningContent(null) { ProtectedData = block.ProtectedData });
                update.Contents.Add(new ClaudeThinkingContent(block));
            }
            // The SDK does not turn an error SSE frame into ErrorContent. The transport observer records
            // its provider code in the shared retry scope without changing the bytes.
            if (RequestRetryScope.Current.Value?.StreamFailure is { } failure) throw failure;
            yield return update;
        }
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
        return result;
    }
}
