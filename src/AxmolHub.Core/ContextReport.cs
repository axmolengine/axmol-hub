namespace AxmolHub.Core;

/// <summary>
/// What the model reported having spent, kept beside what Hub estimated it would spend.
///
/// <para>The estimate alone is a guess with a decimal point on it; the reported number is the only measurement
/// in the system. Keeping the paired estimate alongside <see cref="InputTokens"/> lets compaction account for
/// any positive unestimated gap (fixed server instructions or a denser tokenizer) additively without shrinking
/// the model's declared context window.</para>
///
/// <para><b>Input takes the largest, output takes the sum</b>, and the asymmetry is the point. One user turn in
/// agent mode is up to seventeen HTTP requests — the tool loop re-sends the growing transcript each time — and
/// only the last of them carries the full window. Summing the inputs would report a seventeen-fold window and
/// push the meter in the wrong direction, while outputs genuinely are separate money and must add up.</para>
///
/// <para>The window is only half of what the turn cost, though, and <see cref="SummedInputTokens"/> is the other
/// half: those seventeen requests each <i>read</i> the transcript, so the input the endpoint was paid for is the
/// sum, not the largest one. The two numbers answer different questions — "how much of the window is this
/// conversation" against "how much did this answer burn" — and both are kept, so no surface has to borrow the
/// other's meaning and get it wrong.</para>
///
/// <para>Written on a thread-pool thread (the stream's), read on the UI thread. It is merged into one value per
/// request and handed over at the end of the send, so the object never lives long enough for the two threads to
/// race on it.</para>
/// </summary>
public sealed class ContextReport
{
    /// <summary>The largest <b>input</b> any request of this send reported — the one that held the whole window.</summary>
    public int InputTokens { get; private set; }

    /// <summary>Output tokens summed over the requests: each one is money the reply actually cost.</summary>
    public int OutputTokens { get; private set; }

    /// <summary>Reasoning tokens summed over the requests. Reported separately because a thinking model's chain
    /// of thought is billed inside the output and is otherwise invisible in the meter.</summary>
    public int ReasoningTokens { get; private set; }

    /// <summary>Cached input of the largest request, when the endpoint distinguishes it.</summary>
    public int CachedInputTokens { get; private set; }

    /// <summary>The <b>sum</b> of every request's input — what the turn actually read, and therefore what it was
    /// billed for. Deliberately not <see cref="InputTokens"/>, which is the largest single request and describes
    /// the window rather than the spend; a tool loop of seventeen requests over a 40 000-token transcript burns
    /// roughly 680 000 input tokens and only ever shows 40 000 of them as window.</summary>
    public long SummedInputTokens { get; private set; }

    /// <summary>Cached input summed over the requests, so a discount the endpoint reported is not read as a
    /// window-sized number. Zero on a gateway that reports no cache split at all.</summary>
    public long SummedCachedInputTokens { get; private set; }

    /// <summary>Hub's own estimate of the request that produced <see cref="InputTokens"/> — the pair the drift
    /// is computed from. Zero when the report arrived without the pipeline having sized that request.</summary>
    public int EstimatedInputTokens { get; private set; }

    /// <summary>How many HTTP requests this send made. Under one, the loop never ran; over one, the window
    /// number came from a later request than the transcript the meter is showing.</summary>
    public int Requests { get; private set; }

    /// <summary>Whether the model said anything at all. False for a gateway that never sends a usage chunk,
    /// which is a reason to keep estimating, not a reason to show zero.</summary>
    public bool IsMeasured => InputTokens > 0;

    /// <summary>Folds one request's report in. <paramref name="estimatedInputTokens"/> must be the estimate of
    /// <b>this</b> request, because it is kept only when this request is the largest one so far.</summary>
    public void Merge(int inputTokens, int outputTokens, int reasoningTokens, int cachedInputTokens,
        int estimatedInputTokens)
    {
        Requests++;
        if (outputTokens > 0) OutputTokens += outputTokens;
        if (reasoningTokens > 0) ReasoningTokens += reasoningTokens;
        // Both sums happen above the early return below, because that return is the window's rule and the spend
        // does not obey it: a request smaller than the largest one still cost what it read.
        if (inputTokens > 0) SummedInputTokens += inputTokens;
        if (cachedInputTokens > 0) SummedCachedInputTokens += cachedInputTokens;
        if (inputTokens <= InputTokens) return;
        InputTokens = inputTokens;
        CachedInputTokens = cachedInputTokens;
        EstimatedInputTokens = estimatedInputTokens;
    }
}
