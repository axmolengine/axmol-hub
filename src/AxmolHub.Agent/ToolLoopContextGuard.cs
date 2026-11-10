using System.Text.Json;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>
/// Keeps the tool loop inside the model's window.
///
/// <see cref="ContextTrimmer"/> runs once per request, before the first iteration; a loop that reads six files
/// grows the message list afterwards and nothing else is watching. Elision rewrites the *text* of older tool
/// results and drops the bytes of older pictures, but never removes a message, which is the whole point: a
/// provider rejects an assistant message whose call has no result, so shrinking the window by dropping messages
/// would trade a slow request for a failed one. A result that says "elided, call the tool again if you need it"
/// is truthful and cheap.
/// </summary>
public sealed class ToolLoopContextGuard(IChatClient innerClient, int budgetTokens, int yieldBudgetTokens = 0)
    : DelegatingChatClient(innerClient)
{
    /// <summary>The budget the handoff decision measures against.
    ///
    /// <para><b>Why it is not the trimming budget.</b> The whole tool loop runs inside one stream segment, so this
    /// guard is the only thing that ever asks the run owner for a chance to compact: measuring that question
    /// against the model's window means a 500 000-token model loops happily at 100 000–400 000 tokens a round and
    /// never hands back, which is how one measured run sent 4 520 397 input tokens in eighteen minutes. The host
    /// therefore passes its cost ceiling here while trimming keeps the real budget — what fits is a fact about the
    /// model, how much a round is worth re-sending is a policy.</para></summary>
    private int YieldBudget => yieldBudgetTokens > 0 ? yieldBudgetTokens : budgetTokens;

    /// <summary>Lets a run owner compact and resume at a complete tool boundary instead of repeatedly rewriting
    /// the cached prefix. Standalone callers retain the existing elision fallback unless they opt in.</summary>
    public bool YieldForCompaction { get; init; }
    public Func<Task<bool>>? CanYieldForCompaction { get; init; }
    public Func<Task>? ValidateSelection { get; init; }
    private int _requests;

    // Rebuild carriers at every live boundary, not only when a saved conversation is replayed.
    public Func<string, ChatMessage?>? ToolFrame { get; init; }

    /// <summary>The newest results are the ones the model is reasoning about right now.</summary>
    public const int KeepRecentResults = 4;

    /// <summary>Below this a result is left alone: replacing 200 characters with a 90-character placeholder
    /// buys nothing and loses the answer.</summary>
    private const int SmallestWorthEliding = 512;

    /// <summary>What an elided result starts with. <see cref="ToolSpinGuard"/> reads it too: a repeat after an
    /// elision is Hub having asked for the re-call, and must not count as a spin.</summary>
    public const string ElidedResultPrefix = "[elided:";

    /// <summary>
    /// Asked once before every request the loop makes, whether the user has added something to the conversation.
    ///
    /// <para>This is where a steer lands. The guard sits inside the invoking client, so it is reached once per
    /// iteration with the message list as the loop has grown it — which is also the first moment a new
    /// instruction can be handed over without throwing away an answer in progress, and the only such moment: the
    /// list at this point ends in the tool results the model is waiting on, so a user message after them is a
    /// shape the bridge accepts. Cancelling the stream to get the point across sooner costs the reasoning behind
    /// an answer nobody asked to lose.</para>
    /// </summary>
    public Func<Task<ChatMessage?>>? PendingInterjection { get; init; }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = await WithInterjectionAsync(messages).ConfigureAwait(false);
        LastEstimatedInputTokens = EstimateTokens(list);
        return await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = await WithInterjectionAsync(messages).ConfigureAwait(false);
        LastEstimatedInputTokens = EstimateTokens(list);
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken)
                           .ConfigureAwait(false))
            yield return update;
    }

    /// <summary>The list that goes out: whatever the user added, then the older results shrunk to fit. In that
    /// order, because the budget has to be spent with the new instruction already counted against it.</summary>
    private async Task<List<ChatMessage>> WithInterjectionAsync(IEnumerable<ChatMessage> messages)
    {
        if (ValidateSelection is { } validate) await validate().ConfigureAwait(false);
        var list = messages.ToList();
        var atToolBoundary = AtToolBoundary(list);
        if (ToolFrame is { } frameFor)
        {
            var carried = list.Where(message => message.AdditionalProperties?.ContainsKey(ToolSpinGuard.CapturedFrameKey) == true)
                .Select(message => message.AdditionalProperties![ToolSpinGuard.CapturedFrameKey] as string)
                .ToHashSet(StringComparer.Ordinal);
            for (var index = list.Count - 1; index >= 0; index--)
            {
                var frames = list[index].Contents.OfType<FunctionResultContent>()
                    .Where(result => !carried.Contains(result.CallId))
                    .Select(result => frameFor(result.CallId)).OfType<ChatMessage>().ToList();
                list.InsertRange(index + 1, frames);
            }
        }
        if (PendingInterjection is { } ask && atToolBoundary)
        {
            // FunctionInvokingChatClient owns this mutable history and appends later responses to it.
            // A steer must enter that history before the next response, not only this request's copy.
            // Validate before consuming the callback: an unsupported caller must not silently lose a message.
            if (messages is not IList<ChatMessage> { IsReadOnly: false } history)
                throw new InvalidOperationException("Mid-run messages require mutable tool-loop history.");
            if (await ask().ConfigureAwait(false) is { } interjection)
            {
                history.Add(interjection);
                list.Add(interjection);
            }
        }
        // Never yield the first request: an owner whose compaction cannot shrink the prompt must still have
        // a finite fallback. Later boundaries contain every result in the preceding batch, so no tool repeats.
        if (YieldForCompaction && _requests > 0 && atToolBoundary
            && ContextCompaction.ShouldCompact(EstimateTokens(list), YieldBudget)
            && (CanYieldForCompaction is null || await CanYieldForCompaction().ConfigureAwait(false)))
            throw new ToolLoopContextBoundaryException();
        _requests++;
        var elided = Elide(list, budgetTokens);
        NoteElided(elided);
        return elided;
    }

    /// <summary>
    /// The call ids whose results this run has elided off the wire. The invoking client's own message list
    /// keeps the original bytes — elision rewrites only what goes out — so <see cref="ToolSpinGuard"/> cannot
    /// see an elision from that list alone, and without this a re-call Hub literally asked for in the
    /// placeholder would count as a spin. Grows over one run; call ids are unique per call, so a re-run's fresh
    /// id is never confused with the elided one it replaced.
    /// </summary>
    public IReadOnlySet<string> ElidedCallIds => _elidedCallIds;

    private readonly HashSet<string> _elidedCallIds = new(StringComparer.Ordinal);

    private void NoteElided(List<ChatMessage> elided)
    {
        foreach (var message in elided)
        foreach (var content in message.Contents)
            if (content is FunctionResultContent result && TextOf(result) is { Length: > 0 } text
                && text.StartsWith(ElidedResultPrefix, StringComparison.Ordinal))
                _elidedCallIds.Add(result.CallId);
    }

    /// <summary>Whether this list ends where a person may legitimately be answered: on the results of the calls
    /// the model asked for. Anything else — the first request of a turn, or a list still open on a call with no
    /// result — is a place where adding a user message either stacks two of them or splits a call from its
    /// answer, and both are shapes a bridge that wants alternating roles refuses.</summary>
    private static bool AtToolBoundary(List<ChatMessage> list)
        => list.Count > 0 && list[^1].Contents.Any(content => content is FunctionResultContent);

    /// <summary>
    /// What the list that went out last cost, in Hub's own currency. It exists so a reported token count has
    /// something to be compared <i>against</i>: the drift between this number and the model's answer is the only
    /// way the estimate ever gets better, and pairing a measurement with the size of some other request would
    /// calibrate the wrong thing. What makes "last" mean "this one" is that the loop sends one request at a time —
    /// <c>AllowConcurrentInvocation = false</c> on the invoking client, and the batch starter runs <i>tools</i>
    /// side by side without ever putting a second list in flight.
    /// </summary>
    public int LastEstimatedInputTokens { get; private set; }

    public static List<ChatMessage> Elide(IEnumerable<ChatMessage> messages, int budgetTokens)
    {
        var list = messages.ToList();
        var resultIndices = new List<int>();
        for (var index = 0; index < list.Count; index++)
            // A turn carrying a picture is a candidate too: the bytes are the most expensive part of the
            // request, and the newest image is the one the model is looking at.
            if (list[index].Contents.Any(content => content is FunctionResultContent or DataContent)) resultIndices.Add(index);

        var oldest = 0;
        while (EstimateTokens(list) > budgetTokens && oldest < resultIndices.Count - KeepRecentResults)
        {
            var messageIndex = resultIndices[oldest++];
            if (ElideMessage(list[messageIndex]) is { } elided) list[messageIndex] = elided;
        }
        return list;
    }

    private static ChatMessage? ElideMessage(ChatMessage message)
    {
        var changed = false;
        var contents = new List<AIContent>();
        foreach (var content in message.Contents)
        {
            if (content is FunctionResultContent result && TextOf(result) is { Length: > SmallestWorthEliding } text)
            {
                // FunctionResultContent carries the call id but not the tool name in this version of
                // Microsoft.Extensions.AI, so the placeholder describes the size rather than the tool.
                contents.Add(new FunctionResultContent(result.CallId,
                    $"{ElidedResultPrefix} {text.Length} characters of tool output — call the tool again if you need this]"));
                changed = true;
            }
            else if (content is DataContent picture)
            {
                // The picture goes and the message stays. Dropping the turn would orphan an assistant call from
                // its result, which is the failure this guard exists to avoid, and it would also erase the
                // question the picture belonged to — a turn that was about a screenshot keeps its text and loses
                // the screenshot, and the placeholder says so in the same message rather than silently.
                contents.Add(new TextContent($"[elided: the {picture.MediaType ?? "binary"} attachment is no longer in this "
                                             + "request — ask the user to attach it again, or capture it again, if you still need it]"));
                changed = true;
            }
            else
            {
                contents.Add(content);
            }
        }

        return changed ? new ChatMessage(message.Role, contents) { AuthorName = message.AuthorName } : null;
    }

    private static string? TextOf(FunctionResultContent result) => result.Result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        null => null,
        _ => result.Result.ToString(),
    };

    /// <summary>
    /// Prices the outgoing list in the same currency as <see cref="ContextTrimmer"/>. Core cannot see
    /// <see cref="ChatMessage"/>, so the walk over content types has to live here — but the <i>weighting</i> does
    /// not, and that is the point: this used to be a second char/3 policy, which meant the two estimators could
    /// and did disagree about the same bytes. One message pays its framing once, however many content parts it
    /// carries, and a picture is priced in tokens rather than converted to imaginary characters so it survives a
    /// division.
    /// </summary>
    internal static int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        var tokens = 0;
        foreach (var message in messages)
        {
            var units = 0L;
            foreach (var content in message.Contents)
                units += content switch
                {
                    // TextReasoningContent is a sibling of TextContent, not a subclass — Microsoft.Extensions.AI
                    // states so in its own docs — so it needs its own arm. Before it had one, a 30k-character
                    // chain of thought was free inside the loop that grows it: the guard would decide it had
                    // room while the request was already past the window.
                    TextReasoningContent thought => TokenWeighing.Units(thought.Text)
                        + TokenWeighing.Units(thought.ProtectedData),
                    TextContent text => TokenWeighing.Units(text.Text),
                    FunctionResultContent result => TokenWeighing.Units(TextOf(result)),
                    FunctionCallContent call => call.Arguments is null
                        ? 0L
                        : TokenWeighing.Units(JsonSerializer.Serialize(call.Arguments)),
                    // A picture contributes no characters to a character count, and left unpriced it would bill
                    // an 8 MiB screenshot as free and let the loop grow the request past the window on exactly the
                    // turn where ContextTrimmer had already charged for it.
                    DataContent => (long)ContextTrimmer.ImageTokenCost * ContextTrimmer.CharactersPerToken,
                    _ => 0L,
                };
            tokens += ContextTrimmer.ToTokens(units) + ContextTrimmer.MessageOverheadTokens;
        }

        return tokens;
    }
}

/// <summary>A completed tool batch needs compaction before the next model request. No request was sent.</summary>
public sealed class ToolLoopContextBoundaryException() : Exception("Tool-loop context reached the compaction boundary.");
