namespace AxmolHub.Core;

/// <summary>
/// What the model reported having spent, kept beside what Hub estimated it would spend.
///
/// <para>The estimate alone is a guess with a decimal point on it; the reported number is the only measurement
/// in the system. Keeping the two together is what makes the guess improve: <see cref="DriftPermille"/> is the
/// ratio between them, and every later request in the session is measured against it instead of against the
/// flat character rule.</para>
///
/// <para><b>Input takes the largest, output takes the sum</b>, and the asymmetry is the point. One user turn in
/// agent mode is up to seventeen HTTP requests — the tool loop re-sends the growing transcript each time — and
/// only the last of them carries the full window. Summing the inputs would report a seventeen-fold window and
/// push the meter in the wrong direction, while outputs genuinely are separate money and must add up.</para>
///
/// <para>Written on a thread-pool thread (the stream's), read on the UI thread. It is merged into one value per
/// request and handed over at the end of the send, so the object never lives long enough for the two threads to
/// race on it.</para>
/// </summary>
public sealed class ContextReport
{
    /// <summary>A ratio of 1000 means the estimate and the measurement agreed, so nothing is calibrated.</summary>
    public const int UncalibratedPermille = 1_000;

    /// <summary>How far a single reading is allowed to move the estimate. A gateway that bills the tool schema
    /// and a gateway that bills only the visible text differ by more than 4×, and letting one such reading set
    /// the scale would make the meter read the shape of one vendor rather than the size of this transcript.</summary>
    public const int MaximumDriftPermille = 4_000;

    /// <summary>The largest <b>input</b> any request of this send reported — the one that held the whole window.</summary>
    public int InputTokens { get; private set; }

    /// <summary>Output tokens summed over the requests: each one is money the reply actually cost.</summary>
    public int OutputTokens { get; private set; }

    /// <summary>Reasoning tokens summed over the requests. Reported separately because a thinking model's chain
    /// of thought is billed inside the output and is otherwise invisible in the meter.</summary>
    public int ReasoningTokens { get; private set; }

    /// <summary>Cached input of the largest request, when the endpoint distinguishes it.</summary>
    public int CachedInputTokens { get; private set; }

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
        if (inputTokens <= InputTokens) return;
        InputTokens = inputTokens;
        CachedInputTokens = cachedInputTokens;
        EstimatedInputTokens = estimatedInputTokens;
    }

    /// <summary>Per mille: 1000 × what the model said ÷ what the estimate said. A number above 1000 means the
    /// estimator is reading the transcript too small, which is the direction that overflows, so the floor is
    /// 1000 — an estimate that turns out to be generous is left alone rather than rewarded.</summary>
    public int DriftPermille
    {
        get
        {
            if (InputTokens <= 0 || EstimatedInputTokens <= 0) return UncalibratedPermille;
            return (int)Math.Clamp(1_000L * InputTokens / EstimatedInputTokens,
                UncalibratedPermille, MaximumDriftPermille);
        }
    }
}
