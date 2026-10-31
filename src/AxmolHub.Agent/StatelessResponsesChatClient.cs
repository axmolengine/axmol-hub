using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>
/// Keeps the tool loop stateless before it chooses what to replay. Removing a response id only from the
/// serialized request is too late: the invoking client has already reduced the history to the latest result.
/// </summary>
internal sealed class StatelessResponsesChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        RejectChain(options);
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        response.ConversationId = null;
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RejectChain(options);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            update.ConversationId = null;
            yield return update;
        }
    }

    private static void RejectChain(ChatOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.ConversationId))
            throw new InvalidOperationException("This Responses endpoint requires complete history, not a conversation ID.");
    }
}
