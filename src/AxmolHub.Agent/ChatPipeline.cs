using System.Runtime.CompilerServices;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>
/// Drives one assistant turn: Hub's persisted <see cref="ChatTurn"/> history in, a stream of assistant text
/// chunks out.
///
/// The pipeline is where the two message shapes meet. Storage keeps a flat, version-stable record
/// (<see cref="ChatTurn"/>); the model call needs M.E.AI's <c>ChatMessage</c>. Conversion happens here at the
/// edge — <see cref="ToChatMessages"/> on the way in, plain strings on the way out — so a future upgrade of
/// M.E.AI cannot rewrite conversations on disk.
///
/// Trimming happens inside the pipeline rather than at the call site so every caller gets a request that fits
/// the model's window; the caller still owns the full history.
/// </summary>
public sealed class ChatPipeline(IChatClient client)
{
    /// <summary defaultTokenBudget="0">Conservative fallback when a provider declares no budget; most chat
    /// models accept far more, but the estimate only costs a few dropped old turns.</summary>
    private const int FallbackContextTokens = 8192;

    /// <summary>
    /// Streams the assistant reply to <paramref name="history"/>. Semantics mirror the underlying client:
    /// text arrives incrementally and cancellation surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async IAsyncEnumerable<string> SendAsync(
        ModelProvider provider,
        IReadOnlyList<ChatTurn> history,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var budget = provider.MaxContextTokens ?? FallbackContextTokens;
        var trimmed = ContextTrimmer.Trim(history, budget, systemPrompt);
        var messages = ToChatMessages(trimmed);

        var options = BuildOptions(provider);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            // M.E.AI streams updates that may carry only non-text contents (usage, tool calls); emit the text
            // and let the UI decide what to show. Joining happens in the caller so partial chunks stay partial.
            foreach (var content in update.Contents)
                if (content is TextContent text && text.Text.Length > 0)
                    yield return text.Text;
        }
    }

    /// <summary>Converts persisted turns into the wire shape, in order.</summary>
    public static List<ChatMessage> ToChatMessages(IReadOnlyList<ChatTurn> turns) =>
        [.. turns.Select(ToChatMessage)];

    public static ChatMessage ToChatMessage(ChatTurn turn) => new(ToRole(turn.Role), turn.Text);

    private static ChatRole ToRole(string role) => role switch
    {
        ChatRoles.System => ChatRole.System,
        ChatRoles.User => ChatRole.User,
        ChatRoles.Assistant => ChatRole.Assistant,
        ChatRoles.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };

    /// <summary>Maps the provider's pass-through options onto the request; unknown keys are ignored rather
    /// than rejected, so a manifest entry can carry an option this build does not know yet.</summary>
    private static ChatOptions? BuildOptions(ModelProvider provider)
    {
        var options = new ChatOptions();
        var any = false;
        foreach (var (key, value) in provider.ExtraOptions)
        {
            switch (key)
            {
                case "temperature" when double.TryParse(value, out var temperature):
                    options.Temperature = (float)temperature;
                    any = true;
                    break;
                case "max_tokens" when int.TryParse(value, out var maxTokens):
                    options.MaxOutputTokens = maxTokens;
                    any = true;
                    break;
            }
        }

        return any ? options : null;
    }
}
