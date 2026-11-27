using System.Runtime.CompilerServices;
using System.Text;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>Wraps the official <c>Google.GenAI</c> <see cref="IChatClient"/> adapter to own client disposal,
/// synthesize non-empty tool-call IDs when Gemini omits them, capture and replay <c>thoughtSignature</c> across
/// persisted sessions without leaking foreign reasoning blocks, rejoin persisted parallel tool batches, and fold
/// <c>thoughtsTokenCount</c> into <c>OutputTokenCount</c> to match Hub's usage contract.</summary>
internal sealed class GoogleGenAiChatClient(
    Google.GenAI.Client client,
    IChatClient inner,
    string model,
    ReasoningTable? reasoning = null) : DelegatingChatClient(inner)
{
    private static readonly byte[] SkipThoughtSignatureBytes =
        Encoding.UTF8.GetBytes(ReasoningReplayPolicy.MissingThoughtSignature);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await InnerClient.GetResponseAsync(Replay(messages), PrepareOptions(options), cancellationToken)
            .ConfigureAwait(false);
        var callCounter = 0;
        foreach (var message in response.Messages)
            NormalizeContents(message.Contents, ref callCounter);
        if (response.Usage is { } usage)
            NormalizeUsage(usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var callCounter = 0;
        await foreach (var update in InnerClient.GetStreamingResponseAsync(
                               Replay(messages), PrepareOptions(options), cancellationToken)
                           .ConfigureAwait(false))
        {
            if (RequestRetryScope.Current.Value?.StreamFailure is { } failure) throw failure;
            NormalizeContents(update.Contents, ref callCounter);
            foreach (var usage in update.Contents.OfType<UsageContent>())
                NormalizeUsage(usage.Details);
            yield return update;
        }
        if (RequestRetryScope.Current.Value?.StreamFailure is { } trailingFailure) throw trailingFailure;
    }

    private ChatOptions? PrepareOptions(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools) return options;
        var hasBuiltIn = false;
        var hasFunction = false;
        foreach (var tool in tools)
        {
            if (tool is HostedWebSearchTool or HostedCodeInterpreterTool or HostedFileSearchTool)
                hasBuiltIn = true;
            else if (tool is AIFunctionDeclaration)
                hasFunction = true;
        }
        if (!hasBuiltIn || !hasFunction) return options;

        var clone = options.Clone();
        // Combining built-in tools (such as googleSearch) with functionDeclarations is only supported on
        // Gemini 3+ models; Gemini 1.x / 2.x reject the mixed tools array with HTTP 400.
        if (IsLegacyPreGemini3Model(options.ModelId ?? model))
        {
            clone.Tools = clone.Tools!
                .Where(t => t is not (HostedWebSearchTool or HostedCodeInterpreterTool or HostedFileSearchTool))
                .ToList();
            return clone;
        }

        // Gemini 3+ requires toolConfig.includeServerSideToolInvocations=true when combining built-in tools
        // with functionDeclarations, and rejects FunctionCallingConfigMode.Auto in that mode. Clear ToolMode
        // on the clone so upstream GoogleGenAIChatClient does not overwrite ToolConfig.
        var originalFactory = clone.RawRepresentationFactory;
        var originalToolMode = clone.ToolMode;
        clone.ToolMode = null;
        clone.RawRepresentationFactory = rawClient =>
        {
            var config = originalFactory?.Invoke(rawClient) as GenerateContentConfig ?? new GenerateContentConfig();
            config.ToolConfig ??= new ToolConfig();
            config.ToolConfig.IncludeServerSideToolInvocations = true;
            if (originalToolMode is not null && config.ToolConfig.FunctionCallingConfig is null)
            {
                switch (originalToolMode)
                {
                    case NoneChatToolMode:
                        config.ToolConfig.FunctionCallingConfig = new FunctionCallingConfig
                        {
                            Mode = FunctionCallingConfigMode.None,
                        };
                        break;
                    case RequiredChatToolMode required:
                        config.ToolConfig.FunctionCallingConfig = new FunctionCallingConfig
                        {
                            Mode = FunctionCallingConfigMode.Any,
                            AllowedFunctionNames = required.RequiredFunctionName is { } name ? [name] : null,
                        };
                        break;
                }
            }
            return config;
        };
        return clone;
    }

    private static bool IsLegacyPreGemini3Model(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var leaf = modelName[(modelName.LastIndexOf('/') + 1)..].Trim();
        return leaf.StartsWith("gemini-1.", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("gemini-2.", StringComparison.OrdinalIgnoreCase);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) client.Dispose();
    }

    private void NormalizeContents(IList<AIContent> contents, ref int callCounter)
    {
        for (var i = 0; i < contents.Count; i++)
        {
            var item = contents[i];
            if (item is FunctionCallContent call)
            {
                var target = call;
                if (string.IsNullOrEmpty(call.CallId))
                {
                    var syntheticId = $"gemini_call_{++callCounter}_{Guid.NewGuid().ToString("N")[..8]}";
                    target = new FunctionCallContent(syntheticId, call.Name, call.Arguments)
                    {
                        RawRepresentation = call.RawRepresentation,
                        AdditionalProperties = call.AdditionalProperties,
                    };
                    if (target.RawRepresentation is Part { FunctionCall: { } rawCall } && string.IsNullOrEmpty(rawCall.Id))
                        rawCall.Id = syntheticId;
                    contents[i] = target;
                }

                string? signature = null;
                if (target.RawRepresentation is Part { ThoughtSignature: { Length: > 0 } rawSig })
                    signature = Convert.ToBase64String(rawSig);

                if (i + 1 < contents.Count
                    && contents[i + 1] is TextReasoningContent trailing
                    && string.IsNullOrEmpty(trailing.Text)
                    && trailing.ProtectedData is { Length: > 0 } protectedSig)
                {
                    signature ??= protectedSig;
                    // The signature already travels on Part.RawRepresentation and AdditionalProperties;
                    // removing the synthetic empty TextReasoningContent prevents retry/UI layers from
                    // treating an opaque signature carrier as visible reasoning.
                    if (target.RawRepresentation is Part { ThoughtSignature: { Length: > 0 } })
                        contents.RemoveAt(i + 1);
                }

                if (signature is { Length: > 0 })
                {
                    reasoning?.RecordThoughtSignature(target.CallId, signature);
                    (target.AdditionalProperties ??= new())[ReasoningTable.ThoughtSignatureKey] = signature;
                }
            }
            else if (item is TextReasoningContent emptyThought
                     && string.IsNullOrEmpty(emptyThought.Text)
                     && emptyThought.ProtectedData is { Length: > 0 }
                     && i > 0
                     && contents[i - 1].RawRepresentation is Part { ThoughtSignature: { Length: > 0 } })
            {
                // Same deduplication for signed non-tool parts (e.g. TextContent carrying Part.ThoughtSignature).
                contents.RemoveAt(i);
                i--;
            }
        }
    }

    private static void NormalizeUsage(UsageDetails details)
    {
        // Gemini reports candidatesTokenCount and thoughtsTokenCount separately, whereas Hub's ContextReport and
        // burn display treat Reasoning as a billed subset inside Output.
        if (details.ReasoningTokenCount is > 0 and var thoughts)
            details.OutputTokenCount = (details.OutputTokenCount ?? 0) + thoughts;
    }

    private List<ChatMessage> Replay(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            // Keep genuine live Gemini Part reasoning blocks intact; strip unsigned display reasoning or
            // foreign-provider signatures (such as Claude ProtectedData after a mid-session model switch)
            // so GoogleGenAIChatClient never sends invalid unsigned thought parts or fails base64 decoding.
            var contents = message.Contents
                .Where(part => part is not TextReasoningContent || part.RawRepresentation is Part)
                .ToList();
            if (contents.Count == 0) continue;

            var copy = message.Clone();
            copy.Contents = contents;

            // Hub persists parallel calls as separate call/result turns for approvals. Gemini requires all
            // parallel functionCall parts in one model turn followed by all matching functionResponse parts
            // in one user turn.
            var previousAssistant = result.LastOrDefault()?.Role == ChatRole.Tool
                ? result.ElementAtOrDefault(result.Count - 2)
                : result.LastOrDefault();
            if (copy.Role == ChatRole.Assistant && contents.Any(part => part is FunctionCallContent)
                && previousAssistant?.Role == ChatRole.Assistant
                && previousAssistant.Contents.Any(part => part is FunctionCallContent)
                && copy.AdditionalProperties?.TryGetValue("hub:tool-batch", out var batch) == true
                && previousAssistant.AdditionalProperties?.TryGetValue("hub:tool-batch", out var previousBatch) == true
                && Equals(batch, previousBatch))
            {
                foreach (var part in contents)
                    previousAssistant.Contents.Add(part);
            }
            else if (copy.Role == ChatRole.Tool && result.LastOrDefault()?.Role == ChatRole.Tool)
            {
                foreach (var part in contents)
                    result[^1].Contents.Add(part);
            }
            else
            {
                result.Add(copy);
            }
        }

        // Rebuild Part.RawRepresentation for persisted FunctionCallContent items so exact thoughtSignatures
        // survive round-trips and only the first call of an unsigned legacy turn receives Gemini's sentinel.
        foreach (var message in result.Where(message => message.Role == ChatRole.Assistant))
        {
            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0 || calls.Any(call => call.RawRepresentation is Part)) continue;

            var signatures = new byte[]?[calls.Count];
            var anyRealSignature = false;
            for (var i = 0; i < calls.Count; i++)
            {
                var sig = ReadThoughtSignature(calls[i]);
                if (TryDecodeSignature(sig, out var decoded))
                {
                    signatures[i] = decoded;
                    anyRealSignature = true;
                }
            }

            var callIndex = 0;
            for (var contentIndex = 0; contentIndex < message.Contents.Count; contentIndex++)
            {
                if (message.Contents[contentIndex] is not FunctionCallContent call) continue;
                var sigBytes = signatures[callIndex] ?? (!anyRealSignature && callIndex == 0 ? SkipThoughtSignatureBytes : null);
                callIndex++;
                message.Contents[contentIndex] = new FunctionCallContent(call.CallId, call.Name, call.Arguments)
                {
                    AdditionalProperties = call.AdditionalProperties,
                    RawRepresentation = new Part
                    {
                        FunctionCall = new FunctionCall
                        {
                            Id = call.CallId,
                            Name = call.Name,
                            Args = ToArgs(call.Arguments),
                        },
                        ThoughtSignature = sigBytes,
                    },
                };
            }
        }

        return result;
    }

    private string? ReadThoughtSignature(FunctionCallContent call)
    {
        if (call.AdditionalProperties?.TryGetValue(ReasoningTable.ThoughtSignatureKey, out var raw) == true
            && raw is string stored && stored.Length > 0)
            return stored;
        return reasoning?.GetThoughtSignature(call.CallId);
    }

    private static bool TryDecodeSignature(string? value, out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (string.Equals(value, ReasoningReplayPolicy.MissingThoughtSignature, StringComparison.Ordinal))
        {
            bytes = SkipThoughtSignatureBytes;
            return true;
        }
        try
        {
            bytes = Convert.FromBase64String(value);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Dictionary<string, object>? ToArgs(IDictionary<string, object?>? arguments)
    {
        if (arguments is null) return null;
        var dict = new Dictionary<string, object>(arguments.Count, StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
            dict[key] = value!;
        return dict;
    }
}
