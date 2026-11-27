using System.Net;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent;
using AxmolHub.Core;

internal static class AiSpeedTierChecks
{
    internal static async Task RunAsync()
    {
        CheckCapabilitiesAndNormalization();
        CheckSubtaskIsolation();
        CheckFallbackClassification();
        await CheckWirePayloadsAsync().ConfigureAwait(false);
        Console.WriteLine("AI speed tier checks passed.");
    }

    private static void CheckCapabilitiesAndNormalization()
    {
        if (ChatSpeedTiers.Normalize(null) != ChatSpeedTiers.Default
            || ChatSpeedTiers.Normalize("unknown") != ChatSpeedTiers.Default
            || ChatSpeedTiers.Normalize("priority") != ChatSpeedTiers.Fast
            || ChatSpeedTiers.Normalize("ULTRAFAST") != ChatSpeedTiers.Ultrafast
            || !ChatSpeedTiers.TryParse("flex", out var flex) || flex != ChatSpeedTiers.Flex
            || ChatSpeedTiers.TryParse("bogus", out _))
            throw new Exception("Speed tier normalization or parsing failed.");

        var openAi = AiProviderManifest.CreateBuiltIn("openai")
            ?? throw new Exception("Built-in openai provider missing.");
        var anthropic = AiProviderManifest.CreateBuiltIn("anthropic")
            ?? throw new Exception("Built-in anthropic provider missing.");
        var gemini = AiProviderManifest.CreateBuiltIn("google-gemini")
            ?? throw new Exception("Built-in google-gemini provider missing.");
        var deepseek = AiProviderManifest.CreateBuiltIn("deepseek")
            ?? throw new Exception("Built-in deepseek provider missing.");

        // OpenAI supports all 4 tiers.
        foreach (var tier in ChatSpeedTiers.All)
            if (!SpeedTierPolicy.IsSupported(openAi, "gpt-5.4", tier))
                throw new Exception($"OpenAI should support speed tier '{tier}'.");

        // Anthropic supports Default + Fast only on supported Claude models; Flex and Ultrafast are unsupported.
        if (!SpeedTierPolicy.IsSupported(anthropic, "claude-opus-4-6", ChatSpeedTiers.Default)
            || !SpeedTierPolicy.IsSupported(anthropic, "claude-opus-4-6", ChatSpeedTiers.Fast)
            || SpeedTierPolicy.IsSupported(anthropic, "claude-opus-4-6", ChatSpeedTiers.Flex)
            || SpeedTierPolicy.IsSupported(anthropic, "claude-opus-4-6", ChatSpeedTiers.Ultrafast))
            throw new Exception("Anthropic Claude 4.x speed-tier capabilities did not match spec.");

        if (SpeedTierPolicy.IsSupported(anthropic, "claude-3-opus-20240229", ChatSpeedTiers.Fast))
            throw new Exception("Legacy Anthropic model should not advertise Fast tier.");

        // Google Gemini supports Default + Flex + Fast; Ultrafast is unsupported.
        if (!SpeedTierPolicy.IsSupported(gemini, "gemini-2.5-pro", ChatSpeedTiers.Default)
            || !SpeedTierPolicy.IsSupported(gemini, "gemini-2.5-pro", ChatSpeedTiers.Flex)
            || !SpeedTierPolicy.IsSupported(gemini, "gemini-2.5-pro", ChatSpeedTiers.Fast)
            || SpeedTierPolicy.IsSupported(gemini, "gemini-2.5-pro", ChatSpeedTiers.Ultrafast))
            throw new Exception("Google Gemini speed-tier capabilities did not match spec.");

        // Other providers (e.g. DeepSeek) default to Default only so the speed slider stays hidden.
        if (SpeedTierPolicy.IsSupported(deepseek, "deepseek-chat", ChatSpeedTiers.Fast)
            || SpeedTierPolicy.IsSupported(deepseek, "deepseek-chat", ChatSpeedTiers.Flex)
            || SpeedTierPolicy.SupportedTiers(deepseek, "deepseek-chat").Count != 1)
            throw new Exception("Unsupported provider should only allow Default speed tier.");

        // Custom / relay OpenAI-compatible providers serving GPT models must unlock OpenAI speed tiers.
        var relay = new ModelProvider
        {
            Id = "zhongkey-gpt",
            Name = "zhongkey-gpt",
            BaseUrl = "https://relay.example.com/v1",
            Protocol = ModelProtocols.Responses,
            Model = "gpt-6.2-sol",
        };
        if (SpeedTierPolicy.SupportedTiers(relay, "gpt-6.2-sol").Count != 4
            || SpeedTierPolicy.WireParameter(relay, "gpt-6.2-sol", ChatSpeedTiers.Ultrafast)?.Value != "ultrafast"
            || SpeedTierPolicy.WireParameter(relay, "gpt-6.2-sol", ChatSpeedTiers.Fast)?.Value != "priority"
            || SpeedTierPolicy.WireParameter(relay, "gpt-6.2-sol", ChatSpeedTiers.Flex)?.Value != "flex"
            || SpeedTierPolicy.WireParameter(relay, "gpt-6.2-sol", ChatSpeedTiers.Default) is not null)
            throw new Exception("Custom relay serving GPT model should support all OpenAI speed tiers.");

        // A relay serving DeepSeek should still hide speed tiers.
        if (SpeedTierPolicy.SupportedTiers(relay, "deepseek-chat").Count != 1)
            throw new Exception("Relay serving DeepSeek should not expose speed tiers.");
    }

    private static void CheckSubtaskIsolation()
    {
        if (ChatSpeedTiers.ForSubtask(ChatSpeedTiers.Ultrafast, ChatSpeedTiers.Default) != ChatSpeedTiers.Default)
            throw new Exception("Child session must not inherit session-level Ultrafast.");
        if (ChatSpeedTiers.ForSubtask(null, ChatSpeedTiers.Ultrafast) != ChatSpeedTiers.Default)
            throw new Exception("Child session must not inherit global Ultrafast.");
        if (ChatSpeedTiers.ForSubtask(ChatSpeedTiers.Flex, ChatSpeedTiers.Default) != ChatSpeedTiers.Flex)
            throw new Exception("Child session should preserve non-Ultrafast override.");
        if (ChatSpeedTiers.ForSubtask(null, ChatSpeedTiers.Fast) is not null)
            throw new Exception("Child session should keep null override when global default is not Ultrafast.");
    }

    private static void CheckFallbackClassification()
    {
        if (!ChatPipeline.DeclinedSpeedTier("Invalid parameter: service_tier 'ultrafast' is not supported"))
            throw new Exception("DeclinedSpeedTier should match service_tier rejection.");
        if (ProviderFailure.Recoverable(new InvalidOperationException("service_tier ultrafast is not supported")))
            throw new Exception("ProviderFailure.Recoverable should not retry an explicit service_tier refusal.");

        if (!ChatPipeline.ShouldFallbackSpeedTier(
                new InvalidOperationException("HTTP 403: Your plan does not have permission for ultrafast tier"),
                ChatSpeedTiers.Ultrafast))
            throw new Exception("ShouldFallbackSpeedTier should trigger on plan/permission error for Ultrafast.");

        if (ChatPipeline.ShouldFallbackSpeedTier(
                new InvalidOperationException("HTTP 403: Your plan does not have permission for ultrafast tier"),
                ChatSpeedTiers.Default))
            throw new Exception("ShouldFallbackSpeedTier must not trigger when already on Default.");

        if (ChatPipeline.ShouldFallbackSpeedTier(
                new InvalidOperationException("maximum context length exceeded"),
                ChatSpeedTiers.Fast))
            throw new Exception("ShouldFallbackSpeedTier must not mask context overflow.");
    }

    private static async Task CheckWirePayloadsAsync()
    {
        // 1. OpenAI Chat wire: Default omits service_tier; Flex/Fast/Ultrafast map to flex/priority/ultrafast.
        var openAi = AiProviderManifest.CreateBuiltIn("openai")!;
        openAi.Protocol = ModelProtocols.Chat;
        openAi.Model = "gpt-5.4";
        openAi.Credential = new ProviderCredential { Id = "offline", ProviderId = openAi.Id, Secret = "fixture-key" };

        var openAiDefaultBody = await CaptureOpenAiChatBodyAsync(openAi, ChatSpeedTiers.Default).ConfigureAwait(false);
        AssertNoProperty(openAiDefaultBody, "service_tier", "OpenAI Default");

        var openAiFlexBody = await CaptureOpenAiChatBodyAsync(openAi, ChatSpeedTiers.Flex).ConfigureAwait(false);
        AssertStringProperty(openAiFlexBody, "service_tier", "flex", "OpenAI Flex");

        var openAiFastBody = await CaptureOpenAiChatBodyAsync(openAi, ChatSpeedTiers.Fast).ConfigureAwait(false);
        AssertStringProperty(openAiFastBody, "service_tier", "priority", "OpenAI Fast");

        var openAiUltraBody = await CaptureOpenAiChatBodyAsync(openAi, ChatSpeedTiers.Ultrafast).ConfigureAwait(false);
        AssertStringProperty(openAiUltraBody, "service_tier", "ultrafast", "OpenAI Ultrafast");

        // 2. Google Gemini native wire: Default omits serviceTier; Flex/Fast map to flex/priority; Ultrafast degrades.
        var gemini = AiProviderManifest.CreateBuiltIn("google-gemini")!;
        gemini.Model = "gemini-2.5-pro";
        gemini.Credential = new ProviderCredential { Id = "offline", ProviderId = gemini.Id, Secret = "fixture-key" };

        AssertNoProperty(await CaptureGeminiNativeBodyAsync(gemini, ChatSpeedTiers.Default).ConfigureAwait(false),
            "serviceTier", "Gemini native Default");
        AssertStringProperty(await CaptureGeminiNativeBodyAsync(gemini, ChatSpeedTiers.Flex).ConfigureAwait(false),
            "serviceTier", "flex", "Gemini native Flex");
        AssertStringProperty(await CaptureGeminiNativeBodyAsync(gemini, ChatSpeedTiers.Fast).ConfigureAwait(false),
            "serviceTier", "priority", "Gemini native Fast");
        AssertNoProperty(await CaptureGeminiNativeBodyAsync(gemini, ChatSpeedTiers.Ultrafast).ConfigureAwait(false),
            "serviceTier", "Gemini native Ultrafast degraded");

        // 2b. Legacy OpenAI-compatible Gemini endpoint still uses snake_case service_tier on the Chat wire.
        var geminiCompat = new ModelProvider
        {
            Id = "google-gemini-compat",
            Name = "Gemini Compat",
            IsCustom = true,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/",
            Protocol = ModelProtocols.Chat,
            Model = "gemini-2.5-pro",
            Credential = new ProviderCredential { Id = "offline", ProviderId = "google-gemini-compat", Secret = "fixture-key" },
        };
        AssertStringProperty(await CaptureOpenAiChatBodyAsync(geminiCompat, ChatSpeedTiers.Fast).ConfigureAwait(false),
            "service_tier", "priority", "Gemini OpenAI-compat Fast");

        // 3. Anthropic Messages wire: Default omits service_tier; Fast sends service_tier=priority; Ultrafast omits.
        var anthropic = AiProviderManifest.CreateBuiltIn("anthropic")!;
        anthropic.Model = "claude-opus-4-6";
        anthropic.Credential = new ProviderCredential { Id = "offline", ProviderId = anthropic.Id, Secret = "fixture-key" };

        AssertNoProperty(await CaptureAnthropicBodyAsync(anthropic, ChatSpeedTiers.Default).ConfigureAwait(false),
            "service_tier", "Anthropic Default");
        AssertStringProperty(await CaptureAnthropicBodyAsync(anthropic, ChatSpeedTiers.Fast).ConfigureAwait(false),
            "service_tier", "priority", "Anthropic Fast");
        AssertNoProperty(await CaptureAnthropicBodyAsync(anthropic, ChatSpeedTiers.Ultrafast).ConfigureAwait(false),
            "service_tier", "Anthropic Ultrafast degraded");
    }

    private static async Task<string> CaptureOpenAiChatBodyAsync(ModelProvider provider, string speedTier)
    {
        using var spy = new WireSpy(
            "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        using var client = ChatClientFactory.Create(provider, provider.Model, null, transport: spy, maxRetries: 0);
        var pipeline = new ChatPipeline(client);
        await foreach (var _ in pipeline.SendAsync(provider, [ChatTurn.User("ping")], speedTier: speedTier)) { }
        if (spy.Requests.Count != 1) throw new Exception($"Expected 1 OpenAI wire request, saw {spy.Requests.Count}.");
        return spy.Requests[0];
    }

    private static async Task<string> CaptureAnthropicBodyAsync(ModelProvider provider, string speedTier)
    {
        const string sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-4-6\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        using var spy = new WireSpy(sse);
        using var client = ChatClientFactory.Create(provider, provider.Model, null, transport: spy, maxRetries: 0);
        var pipeline = new ChatPipeline(client);
        await foreach (var _ in pipeline.SendAsync(provider, [ChatTurn.User("ping")], speedTier: speedTier)) { }
        if (spy.Requests.Count != 1) throw new Exception($"Expected 1 Anthropic wire request, saw {spy.Requests.Count}.");
        return spy.Requests[0];
    }

    private static async Task<string> CaptureGeminiNativeBodyAsync(ModelProvider provider, string speedTier)
    {
        const string sse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n";
        using var spy = new WireSpy(sse);
        using var client = ChatClientFactory.Create(provider, provider.Model, null, transport: spy, maxRetries: 0);
        var pipeline = new ChatPipeline(client);
        await foreach (var _ in pipeline.SendAsync(provider, [ChatTurn.User("ping")], speedTier: speedTier)) { }
        if (spy.Requests.Count != 1) throw new Exception($"Expected 1 Gemini native wire request, saw {spy.Requests.Count}.");
        return spy.Requests[0];
    }

    private static void AssertNoProperty(string json, string property, string label)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty(property, out _))
            throw new Exception($"{label} should omit '{property}', but body was: {json}");
    }

    private static void AssertStringProperty(string json, string property, string expected, string label)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(property, out var value) || value.GetString() != expected)
            throw new Exception($"{label} expected {property}='{expected}', but body was: {json}");
    }

    private sealed class WireSpy(string ssePayload) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
