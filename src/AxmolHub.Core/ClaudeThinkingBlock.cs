namespace AxmolHub.Core;

/// <summary>An opaque signed thinking block from Anthropic Messages. Redacted data is never rendered as text.</summary>
public sealed record ClaudeThinkingBlock(string? Text, string ProtectedData, bool Redacted);
