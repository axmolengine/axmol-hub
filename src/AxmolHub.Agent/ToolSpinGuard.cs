using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>
/// Stops a tool loop that repeats itself instead of working.
///
/// <para>The loop carries no round cap — the model ends it by answering — so what stands between a long task and
/// an unbounded paid loop is only cancellation, the consecutive-error counter (which counts failures, not
/// success) and the context budget. A model that has decided the answer is "read the same file again" trips
/// none of those: every call succeeds, the window compacts, the loop runs forever. This guard catches that one
/// shape by walking the transcript the loop itself accumulated: the same tool with the same arguments, run
/// <see cref="RepeatLimit"/> times successfully inside a sliding window with nothing state-changing in between,
/// is refused as a result string the model reads instead of an exception that kills the turn. Failed calls
/// have a separate <see cref="FailureLimit"/> allowance: a missing window is not a successful capture.</para>
///
/// <para>Two exemptions are what keep the refusal off correct work, and both are read from the transcript
/// rather than from counters, so a restart, a steer or a replayed session sees the same history:</para>
/// <list type="bullet">
/// <item>A confirmed write or a granted path ends the walk. Re-reading a file you just edited is
/// verification, not spinning.</item>
/// <item>A result that <see cref="ToolLoopContextGuard.Elide"/> rewrote tells the model in so many words to
/// call again if it still needs it — so a repeat after an elision is Hub asking for it, not a loop.</item>
/// </list>
///
/// <para>Deliberately exact-match only: similarity thresholds and cycle detection would be guesswork with no
/// local data behind them, and a false accusation refuses real work. A model that ignores the refusal keeps
/// getting the same refusal — cheap, one string per round, no tool executed.</para>
/// </summary>
public static class ToolSpinGuard
{
    /// <summary>Identical calls within the window that make the next one a spin. Three: one call, its verify,
    /// and one more is already more than a debugging loop needs.</summary>
    public const int RepeatLimit = 3;

    /// <summary>Failed executions of one signature allowed before refusing another attempt.
    /// A refusal still gives the model a chance to choose another tool instead of ending the turn immediately.</summary>
    public const int FailureLimit = 3;

    // A captured-frame carrier uses the user role for transport, but is not a new user instruction.
    internal const string CapturedFrameKey = "AxmolHub.CapturedFrame";

    /// <summary>How far back repetitions count. Beyond this the calls were part of working through the problem;
    /// inside it, with no state change between, they are the problem.</summary>
    public const int WindowSize = 12;

    /// <summary>The sentence a refused repeat hands back to the model. Matched on itself by the checks.</summary>
    public const string RefusalMarker = "has already run";

    /// <summary>The second refusal of one signature does not go back to the model — the turn is over. This
    /// starts the result the transcript keeps and <see cref="ChatWorkspace"/> reads to say so out loud.</summary>
    public const string TerminationMarker = "This turn is stopping:";

    /// <summary>Key order, whitespace and number spelling are not what the model meant — a spin that shuffled
    /// them would be a different signature by byte and the same call by intent. Objects are sorted, arrays keep
    /// their order (an argument list is ordered), and everything else round-trips through the JSON tree.</summary>
    public static string NormalizeArguments(string argumentsJson)
    {
        return Core.GoalProgress.NormalizeArguments(argumentsJson);
    }

    /// <summary>
    /// The refusal string if asking for this call now would be the <see cref="RepeatLimit"/>-th identical run
    /// inside the window, otherwise null and the call may proceed. <paramref name="history"/> is the invoking
    /// client's own accumulated list, which keeps original bytes — so elisions are reported through
    /// <paramref name="elidedCallIds"/> (see <see cref="ToolLoopContextGuard.ElidedCallIds"/>), and a result
    /// carrying the elide prefix directly is accepted as a second, list-independent spelling of the same fact.
    /// <paramref name="failedCallIds"/> carries live and replayed execution failures; older textual refusals
    /// use the shared result vocabulary rather than a second tool-specific list.
    ///
    /// <para>The walk runs backwards and stops after <see cref="WindowSize"/> paired results: only the newest
    /// window can refuse, so pairing and normalizing the whole transcript would price every check at the size
    /// of the run — and an argument blob (a file written into a call's arguments) would be re-canonicalized on
    /// every tool call for the rest of the turn. Names are compared before arguments for the same reason: a
    /// window of twelve calls normally costs one normalization, the wanted one.</para>
    /// </summary>
    public static string? RefusalFor(string toolName, string argumentsJson, IList<ChatMessage> history,
        IReadOnlySet<string>? elidedCallIds = null, IReadOnlySet<string>? failedCallIds = null)
    {
        var wantedArguments = NormalizeArguments(argumentsJson);
        var observingWindow = IsWindowObservation(toolName, argumentsJson);
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var identical = 0;
        var failed = 0;
        var paired = 0;
        for (var messageIndex = history.Count - 1; messageIndex >= 0 && paired < WindowSize; messageIndex--)
        {
            // A new user instruction authorizes a fresh attempt; earlier turns are not this invocation's loop.
            if (history[messageIndex].Role == ChatRole.User
                && history[messageIndex].AdditionalProperties?.ContainsKey(CapturedFrameKey) != true) break;
            var contents = history[messageIndex].Contents;
            for (var contentIndex = contents.Count - 1; contentIndex >= 0; contentIndex--)
            {
                var content = contents[contentIndex];
                // Walking backwards, the result arrives before the call that made it; the dict is the seam.
                if (content is FunctionResultContent result)
                {
                    results[result.CallId] = TextOf(result);
                    continue;
                }

                if (content is not FunctionCallContent call || !results.Remove(call.CallId, out var text))
                    continue;
                paired++;
                var failure = Core.GoalProgress.Failed(call.Name, text, failedCallIds?.Contains(call.CallId) == true);
                // Input consumes its frame even when delivery is refused. The requested fresh observation
                // must remain possible; this does not forgive repeated input failures or screenshot-only spins.
                if (observingWindow && call.Name == Core.ToolNames.ComputerUse
                    && text.EndsWith(Core.GoalProgress.ComputerObservationRequired, StringComparison.Ordinal)) return null;
                // The world moved: anything older than this is a different problem, not a repetition of this one.
                if (!failure && Core.GoalProgress.StateChanged(call.Name, text))
                    return null;
                if (text.StartsWith(ToolLoopContextGuard.ElidedResultPrefix, StringComparison.Ordinal)
                    || (elidedCallIds is { Count: > 0 } elided && elided.Contains(call.CallId))) continue;
                if (!string.Equals(call.Name, toolName, StringComparison.Ordinal)) continue;
                if (NormalizeArguments(JsonSerializer.Serialize(call.Arguments)) != wantedArguments) continue;
                // Keep failure retries apart from successful repetitions. A closed capture target is not
                // evidence the model can answer from, but retrying an unchanged target forever is not useful either.
                if (failure)
                {
                    if (++failed < FailureLimit) continue;
                    return $"This exact call ({toolName} with the same arguments) has already run and failed {failed} times. "
                           + "Do not retry these unchanged arguments: choose another approach or explain what the user "
                           + "needs to restore. No successful result is available for this call.";
                }
                if (++identical >= RepeatLimit - 1)
                    return $"This exact call ({toolName} with the same arguments) has already run successfully {identical} times "
                           + "and the results are in the conversation. Stop repeating it: answer from "
                           + "what you have already found, or do something different.";
            }
        }

        return null;
    }

    private static bool IsWindowObservation(string name, string json)
    {
        if (name == Core.ToolNames.CaptureScreen) return true;
        if (name != Core.ToolNames.ComputerUse) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String
                && string.Equals(action.GetString()?.Trim(), "screenshot", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private static string TextOf(FunctionResultContent result) => result.Result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        null => "",
        _ => result.Result.ToString() ?? "",
    };
}
