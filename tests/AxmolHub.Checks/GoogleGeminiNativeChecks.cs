using System.Net;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent;
using AxmolHub.Core;
using Google.GenAI;
using Microsoft.Extensions.AI;

internal static class GoogleGeminiNativeChecks
{
    internal static async Task RunAsync()
    {
        await CheckStreamingToolsReplayAndUsageAsync();
        await CheckThinkingModesAndKeylessRelayAsync();
        CheckErrorClassification();
        Console.WriteLine("PASS: Google Gemini native wire covers auth, thinking config, parallel tool replay, thoughtSignature, grounding, usage, and error classification.");
    }

    // One offline round-trip covers the consequential failures: synthetic CallId when omitted,
    // thoughtSignature capture/replay across JSON persistence, parallel batch merging, grounding metadata,
    // reasoning-token usage folding, foreign reasoning stripping, and caller-owned transport lifetime.
    private static async Task CheckStreamingToolsReplayAndUsageAsync()
    {
        using var spy = new Spy(expectedPath: "/v1beta/models/gemini-4-neon:streamGenerateContent",
            expectedApiKey: "fixture-key", expectedCustomHeader: ("X-Hub-Test", "yes"));
        var provider = AiProviderManifest.CreateBuiltIn("google-gemini")
            ?? throw new Exception("The official Google Gemini preset is missing.");
        provider.Model = "gemini-4-neon";
        provider.Credential = new ProviderCredential { Id = "offline", ProviderId = provider.Id, Secret = "fixture-key" };
        provider.CustomHeaders["X-Hub-Test"] = "yes";

        var reasoning = new ReasoningTable();
        var client = ChatClientFactory.Create(provider, provider.Model, reasoning, transport: spy, maxRetries: 0);
        var pipeline = new ChatPipeline(client, reasoning);
        var function = AIFunctionFactory.Create(() => "ok", "inspect");

        var turns = new List<ChatTurn> { ChatTurn.User("check") };
        var answer = new StringBuilder();
        var thoughts = new StringBuilder();
        ContextReport? lastUsage = null;
        var searches = new List<ServerSearchNotice>();

        await foreach (var text in pipeline.SendAsync(
            provider,
            turns.ToArray(),
            tools: [function, new HostedWebSearchTool()],
            reasoningEffort: "xhigh",
            maxOutputTokens: 200,
            onReasoning: delta => thoughts.Append(delta),
            onUsage: usage => lastUsage = usage,
            onServerSearch: notice => searches.Add(notice),
            onToolStarted: call =>
            {
                turns.Add(ChatTurn.FunctionCall(call.CallId, call.Name, call.ArgumentsJson,
                    reasoning: call.Reasoning, batchId: call.BatchId, thoughtSignature: call.ThoughtSignature));
                return Task.CompletedTask;
            },
            onToolCompleted: (call, result, failed) =>
            {
                turns.Add(ChatTurn.FunctionResult(call.CallId, result, failed));
                return Task.CompletedTask;
            }))
        {
            answer.Append(text);
        }

        turns.Add(ChatTurn.Assistant(answer.ToString()) with
        {
            Reasoning = "unsigned display reasoning",
            ClaudeThinking = [new ClaudeThinkingBlock("foreign claude thought", "not-base64!!!", Redacted: false)],
        });
        // Append an unsigned legacy tool round to confirm the bypass sentinel lands only on unsigned turns.
        turns.Add(ChatTurn.FunctionCall("legacy_call", "inspect", "{}"));
        turns.Add(ChatTurn.FunctionResult("legacy_call", "legacy-ok"));
        turns.Add(ChatTurn.Assistant("legacy done"));

        var restored = JsonSerializer.Deserialize<List<ChatTurn>>(JsonSerializer.Serialize(turns))!;
        restored.Add(ChatTurn.User("continue"));

        // Re-create a fresh ReasoningTable so replay relies solely on persisted ChatTurn.ThoughtSignature.
        using (var replayClient = ChatClientFactory.Create(provider, provider.Model, new ReasoningTable(), transport: spy, maxRetries: 0))
        {
            var replayPipeline = new ChatPipeline(replayClient);
            await foreach (var _ in replayPipeline.SendAsync(provider, restored, tools: [function],
                reasoningEffort: "low", maxOutputTokens: 150)) { }
        }

        // Disposing ChatClient must not dispose caller-owned spy transport.
        client.Dispose();
        if (spy.Disposed) throw new Exception("Disposing Google Gemini client disposed caller-owned transport.");

        if (spy.Requests.Count != 3)
            throw new Exception($"Gemini native round-trip expected 3 requests, saw {spy.Requests.Count}.");
        if (thoughts.ToString() != "plan" || answer.ToString() != "done")
            throw new Exception($"Unexpected streamed reasoning/answer: '{thoughts}' / '{answer}'.");
        if (lastUsage is not { InputTokens: 120, OutputTokens: 80, ReasoningTokens: 50 })
            throw new Exception($"Expected OutputTokens=80 (30+50) and ReasoningTokens=50, saw input={lastUsage?.InputTokens} out={lastUsage?.OutputTokens} reason={lastUsage?.ReasoningTokens}.");
        if (searches.Count == 0
            || !searches.SelectMany(s => s.Queries).Contains("axmol gemini", StringComparer.Ordinal)
            || !searches.SelectMany(s => s.Sources).Contains("https://axmol.dev/docs", StringComparer.Ordinal))
            throw new Exception("Grounding metadata was not surfaced through onServerSearch.");

        // Verify round 1 request: thinkingLevel XHIGH + includeThoughts + googleSearch beside functionDeclarations.
        using (var r1 = JsonDocument.Parse(spy.Requests[0]))
        {
            var gen = r1.RootElement.GetProperty("generationConfig");
            var tc = gen.GetProperty("thinkingConfig");
            if (tc.GetProperty("thinkingLevel").GetString() != "XHIGH"
                || !tc.GetProperty("includeThoughts").GetBoolean()
                || gen.GetProperty("maxOutputTokens").GetInt32() != 200)
                throw new Exception("Round 1 thinkingConfig or maxOutputTokens did not match Gemini 3/4 xhigh.");
            var toolsJson = r1.RootElement.GetProperty("tools").GetRawText();
            if (!toolsJson.Contains("\"googleSearch\"", StringComparison.Ordinal)
                || !toolsJson.Contains("\"functionDeclarations\"", StringComparison.Ordinal))
                throw new Exception("Round 1 tools did not carry googleSearch alongside functionDeclarations: " + toolsJson);
            if (!r1.RootElement.TryGetProperty("toolConfig", out var toolConfig)
                || !toolConfig.TryGetProperty("includeServerSideToolInvocations", out var includeServerSide)
                || !includeServerSide.GetBoolean())
                throw new Exception("Round 1 omitted toolConfig.includeServerSideToolInvocations when combining googleSearch and functionDeclarations.");
        }

        // Verify persisted replay request (round 3): parallel tool batch rejoined, real thoughtSignature
        // replayed verbatim on first call only, unsigned legacy call gets skip_thought_signature_validator,
        // and foreign Claude / unsigned display reasoning was stripped.
        using (var r3 = JsonDocument.Parse(spy.Requests[2]))
        {
            var root = r3.RootElement;
            if (root.GetRawText().Contains("foreign claude thought", StringComparison.Ordinal)
                || root.GetRawText().Contains("unsigned display reasoning", StringComparison.Ordinal))
                throw new Exception("Foreign or unsigned display reasoning leaked into Gemini replay payload.");

            var contents = root.GetProperty("contents").EnumerateArray().ToList();
            // Expected turns: user("check"), model([call1, call2]), user([resp1, resp2]), model("done"),
            // model([legacy_call]), user([legacy_resp]), model("legacy done"), user("continue")
            var signedModelTurn = contents.FirstOrDefault(c =>
                c.GetProperty("role").GetString() == "model"
                && c.GetProperty("parts").EnumerateArray().Count(p => p.TryGetProperty("functionCall", out _)) == 2);
            if (signedModelTurn.ValueKind == JsonValueKind.Undefined)
                throw new Exception("Persisted parallel tool calls were not rejoined into one model turn: " + spy.Requests[2]);

            var signedParts = signedModelTurn.GetProperty("parts").EnumerateArray()
                .Where(p => p.TryGetProperty("functionCall", out _)).ToList();
            var sig0 = signedParts[0].TryGetProperty("thoughtSignature", out var s0) ? s0.GetString() : null;
            var sig1 = signedParts[1].TryGetProperty("thoughtSignature", out var s1) ? s1.GetString() : null;
            var id0 = signedParts[0].GetProperty("functionCall").TryGetProperty("id", out var i0) ? i0.GetString() : null;
            var id1 = signedParts[1].GetProperty("functionCall").TryGetProperty("id", out var i1) ? i1.GetString() : null;
            if (sig0 != "c2lnLTE=" || sig1 is not null)
                throw new Exception($"Replayed parallel batch signatures drifted: first='{sig0}' second='{sig1}'.");
            if (string.IsNullOrEmpty(id0) || !id0.StartsWith("gemini_call_", StringComparison.Ordinal) || id1 != "call_2")
                throw new Exception($"Synthetic or explicit CallId drifted: first='{id0}' second='{id1}'.");

            var responseTurn = contents.FirstOrDefault(c =>
                c.GetProperty("role").GetString() == "user"
                && c.GetProperty("parts").EnumerateArray().Count(p => p.TryGetProperty("functionResponse", out _)) == 2);
            if (responseTurn.ValueKind == JsonValueKind.Undefined)
                throw new Exception("Persisted parallel tool results were not rejoined into one user turn.");

            var legacyTurn = contents.FirstOrDefault(c =>
                c.GetProperty("role").GetString() == "model"
                && c.GetProperty("parts").EnumerateArray().Any(p =>
                    p.TryGetProperty("functionCall", out var fc)
                    && fc.TryGetProperty("id", out var fid) && fid.GetString() == "legacy_call"));
            if (legacyTurn.ValueKind == JsonValueKind.Undefined)
                throw new Exception("Legacy tool turn vanished from replay.");
            var legacyPart = legacyTurn.GetProperty("parts").EnumerateArray()
                .First(p => p.TryGetProperty("functionCall", out _));
            var expectedSkip = Convert.ToBase64String(Encoding.UTF8.GetBytes(ReasoningReplayPolicy.MissingThoughtSignature));
            var legacySig = legacyPart.TryGetProperty("thoughtSignature", out var ls) ? ls.GetString() : null;
            if (legacySig != expectedSkip)
                throw new Exception($"Unsigned legacy call did not receive skip_thought_signature_validator (saw '{legacySig}').");

            var tc3 = root.GetProperty("generationConfig").GetProperty("thinkingConfig");
            if (tc3.GetProperty("thinkingLevel").GetString() != "LOW")
                throw new Exception("Round 3 thinkingLevel did not update to LOW.");
        }
    }

    private static async Task CheckThinkingModesAndKeylessRelayAsync()
    {
        // Custom keyless relay with path prefix: verifies ParseEndpoint path preservation, gemini-2.5-*
        // thinkingBudget mapping, and no Authorization header leak.
        using var spy = new Spy(expectedPath: "/gemini/v1beta/models/gemini-2.5-pro:streamGenerateContent",
            expectedApiKey: "not-needed", expectedCustomHeader: ("X-Relay-Auth", "token-1"));
        var relay = new ModelProvider
        {
            Id = "custom-gemini-relay",
            Name = "Gemini Relay",
            IsCustom = true,
            BaseUrl = "https://relay.test/gemini/v1beta",
            Protocol = ModelProtocols.Gemini,
            Model = "gemini-2.5-pro",
        };
        relay.CustomHeaders["X-Relay-Auth"] = "token-1";

        using var client = ChatClientFactory.Create(relay, relay.Model, null, transport: spy, maxRetries: 0);
        var pipeline = new ChatPipeline(client);
        await foreach (var _ in pipeline.SendAsync(relay, [ChatTurn.User("hi")], reasoningEffort: "high")) { }

        if (spy.Requests.Count != 1)
            throw new Exception($"Expected 1 relay request, saw {spy.Requests.Count}.");
        using var doc = JsonDocument.Parse(spy.Requests[0]);
        var tc = doc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        if (tc.GetProperty("thinkingBudget").GetInt32() != 24576 || !tc.GetProperty("includeThoughts").GetBoolean())
            throw new Exception("Gemini 2.5 high reasoning did not map to thinkingBudget=24576 + includeThoughts=true.");
    }

    private static void CheckErrorClassification()
    {
        var invalidKey400 = new ClientError("API key not valid. Please pass a valid API key. (API_KEY_INVALID)", 400, "INVALID_ARGUMENT");
        var badArg400 = new ClientError("Invalid value at 'contents'", 400, "INVALID_ARGUMENT");
        var unauth401 = new ClientError("Request is missing required authentication credential.", 401, "UNAUTHENTICATED");
        var forbidden403 = new ClientError("Permission denied on resource.", 403, "PERMISSION_DENIED");
        var throttled429 = new ClientError("Quota exceeded.", 429, "RESOURCE_EXHAUSTED");
        var unavailable503 = new ServerError("The service is currently unavailable.", 503, "UNAVAILABLE");

        if (ProviderFailure.StatusOf(invalidKey400) != 400
            || ProviderFailure.StatusOf(unauth401) != 401
            || ProviderFailure.StatusOf(forbidden403) != 403
            || ProviderFailure.StatusOf(throttled429) != 429
            || ProviderFailure.StatusOf(unavailable503) != 503)
            throw new Exception("ProviderFailure.StatusOf did not extract Google.GenAI.ApiException.StatusCode.");

        if (!ProviderFailure.AuthenticationFailed(invalidKey400)
            || !ProviderFailure.AuthenticationFailed(unauth401)
            || !ProviderFailure.AuthenticationFailed(forbidden403)
            || ProviderFailure.AuthenticationFailed(badArg400)
            || ProviderFailure.AuthenticationFailed(throttled429))
            throw new Exception("ProviderFailure.AuthenticationFailed misclassified Google.GenAI errors.");

        if (ProviderFailure.Recoverable(invalidKey400)
            || ProviderFailure.Recoverable(badArg400)
            || ProviderFailure.Recoverable(unauth401)
            || ProviderFailure.Recoverable(forbidden403)
            || !ProviderFailure.Recoverable(throttled429)
            || !ProviderFailure.Recoverable(unavailable503))
            throw new Exception("ProviderFailure.Recoverable misclassified Google.GenAI errors.");
    }

    private sealed class Spy(string expectedPath, string expectedApiKey, (string Name, string Value)? expectedCustomHeader = null)
        : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!string.Equals(request.RequestUri?.AbsolutePath, expectedPath, StringComparison.Ordinal))
                throw new Exception($"Unexpected Gemini path: '{request.RequestUri?.AbsolutePath}' (expected '{expectedPath}').");
            if (request.Headers.Authorization is not null)
                throw new Exception("Google Gemini native request must not send an Authorization header.");
            if (!request.Headers.TryGetValues("x-goog-api-key", out var keys) || keys.Single() != expectedApiKey)
                throw new Exception("Google Gemini native request lost x-goog-api-key header.");
            if (expectedCustomHeader is { } custom
                && (!request.Headers.TryGetValues(custom.Name, out var vals) || vals.Single() != custom.Value))
                throw new Exception($"Google Gemini native request lost custom header '{custom.Name}'.");

            Requests.Add(await request.Content!.ReadAsStringAsync(token));

            string sse;
            if (expectedPath.Contains("gemini-4-neon", StringComparison.Ordinal) && Requests.Count == 1)
            {
                // Round 1: thought summary + parallel function calls (first omits id and carries thoughtSignature).
                sse =
                    "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"plan\",\"thought\":true}]}}]}\n\n"
                    + "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"inspect\",\"args\":{}},\"thoughtSignature\":\"c2lnLTE=\"},{\"functionCall\":{\"id\":\"call_2\",\"name\":\"inspect\",\"args\":{}}}]},\"finishReason\":\"STOP\"}]}\n\n";
            }
            else
            {
                sse =
                    "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\",\"groundingMetadata\":{\"webSearchQueries\":[\"axmol gemini\"],\"groundingChunks\":[{\"web\":{\"uri\":\"https://axmol.dev/docs\",\"title\":\"Axmol Docs\"}}]}}],\"usageMetadata\":{\"promptTokenCount\":120,\"candidatesTokenCount\":30,\"thoughtsTokenCount\":50,\"totalTokenCount\":200}}\n\n";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
