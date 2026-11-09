using System.Text;
using Anthropic.Models.Messages;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>Captures completed thinking blocks from SDK events for durable storage, without buffering the
/// displayed stream. Only a block stop commits a signature; cancellation never turns a partial thought into history.</summary>
internal sealed class ClaudeThinkingContent(ClaudeThinkingBlock block) : AIContent
{
    internal ClaudeThinkingBlock Block { get; } = block;
}

internal sealed class ClaudeThinkingAccumulator
{
    private sealed record Pending(bool Redacted, StringBuilder Text, StringBuilder Signature);
    private readonly Dictionary<long, Pending> pending = [];

    internal ClaudeThinkingBlock? Push(object? raw)
    {
        if (raw is RawMessageStreamEvent streamEvent) raw = streamEvent.Value;
        switch (raw)
        {
            case RawMessageStartEvent:
                pending.Clear();
                break;
            case RawContentBlockStartEvent start when start.ContentBlock.Value is ThinkingBlock thought:
                pending[start.Index] = new(false, new(thought.Thinking), new(thought.Signature));
                break;
            case RawContentBlockStartEvent start when start.ContentBlock.Value is RedactedThinkingBlock redacted:
                pending[start.Index] = new(true, new(), new(redacted.Data));
                break;
            case RawContentBlockDeltaEvent delta when pending.TryGetValue(delta.Index, out var block):
                if (delta.Delta.Value is ThinkingDelta text) block.Text.Append(text.Thinking);
                if (delta.Delta.Value is SignatureDelta signature) block.Signature.Append(signature.Signature);
                break;
            case RawContentBlockStopEvent stop when pending.Remove(stop.Index, out var completed):
                return completed.Signature.Length > 0
                    ? new(completed.Redacted ? null : completed.Text.ToString(), completed.Signature.ToString(), completed.Redacted)
                    : null;
        }
        return null;
    }
}
