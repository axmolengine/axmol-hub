using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Restricts ChatGPT plan credentials to the public Responses endpoint.</summary>
internal sealed class OpenAiPlanTransport(ModelProvider provider, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>Creates a transport which never follows redirects with a plan token.</summary>
    public static OpenAiPlanTransport Create(ModelProvider provider)
        => new(provider, new HttpClientHandler { AllowAutoRedirect = false });

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri is not { } uri
            || uri.Scheme != Uri.UriSchemeHttps || uri.IdnHost != "api.openai.com" || !uri.IsDefaultPort
            || uri.AbsolutePath != "/v1/responses" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Refusing to send a ChatGPT plan token to an unsupported endpoint.");
        var credential = provider.Credential ?? throw new InvalidOperationException("OpenAI credential service is unavailable.");
        if (credential.PlanUsagePaused)
            throw new ProviderResponseException("ChatGPT plan requests are paused after a usage limit. Manage usage in ChatGPT settings, then resume in provider settings.",
                "subscription_sharing_usage_limit_exceeded");
        var get = provider.OpenAiTokenProvider ?? throw new InvalidOperationException("OpenAI credential service is unavailable.");
        var body = request.Content is null ? throw new InvalidOperationException("A ChatGPT plan request must contain JSON.")
            : PrepareRequest(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tokens = await get(attempt != 0, cancellationToken).ConfigureAwait(false);
            if (!tokens.IsDynamicRegistration) throw new OpenAiReauthenticationException();
            if (!tokens.HasPlanUsage) throw new InvalidOperationException("ChatGPT plan usage has not been authorized.");
            using var outbound = new HttpRequestMessage(request.Method, uri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers)
                if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    && !header.Key.Equals("ChatGPT-Account-Id", StringComparison.OrdinalIgnoreCase)
                    && !header.Key.Equals("originator", StringComparison.OrdinalIgnoreCase)
                    && !header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    outbound.Headers.TryAddWithoutValidation(header.Key, header.Value);
            outbound.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    outbound.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            if (RequestRetryScope.Current.Value is { } scope)
                await scope.ValidateSelectionAsync().ConfigureAwait(false);
            var response = await base.SendAsync(outbound, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                && response.Content.Headers.ContentLength is >= 0 and <= 65536)
            {
                var errorBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var error = JsonDocument.Parse(errorBody);
                    if (error.RootElement.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                        && code.GetString() == "subscription_sharing_usage_limit_exceeded")
                    {
                        credential.PlanUsagePaused = true;
                        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
                        response.Dispose();
                        throw new ProviderResponseException("ChatGPT plan usage limit reached. Manage usage in ChatGPT settings, then resume in provider settings."
                            + (requestId is { Length: > 0 and <= 128 } && requestId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? " Request ID: " + requestId : ""),
                            code.GetString());
                    }
                }
                catch (JsonException) { }
            }
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt != 0)
            {
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                {
                    var original = response.Content;
                    try
                    {
                        var stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        var checkedContent = new StreamContent(new OpenAiPlanEventStream(stream, original, () => credential.PlanUsagePaused = true));
                        foreach (var header in original.Headers) checkedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        response.Content = checkedContent;
                    }
                    catch { response.Dispose(); throw; }
                }
                return response;
            }
            response.Dispose();
        }
        throw new InvalidOperationException("OpenAI authorization was rejected.");
    }

    internal static byte[] PrepareRequest(byte[] json)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("A ChatGPT plan request must be a JSON object."); }
        catch (JsonException) { throw new InvalidOperationException("A ChatGPT plan request must contain valid JSON."); }
        if (root["input"] is not JsonArray input)
            throw new InvalidOperationException("A ChatGPT plan request requires the complete conversation as an input array.");
        foreach (var item in input.OfType<JsonObject>())
            if (item["role"]?.GetValue<string>() == "system") item["role"] = "developer";
        foreach (var field in new[] { "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata",
            "moderation", "multi_agent", "prompt", "prompt_cache_retention", "safety_identifier", "temperature",
            "top_logprobs", "top_p", "truncation", "user", "previous_response_id" }) root.Remove(field);
        root["store"] = false;
        root["stream"] = true;
        if (root["tools"] is JsonArray tools)
        {
            var functions = new JsonArray();
            var functionNames = new HashSet<string>(StringComparer.Ordinal);
            var retained = new JsonArray();
            foreach (var tool in tools.ToArray())
            {
                if (tool is not JsonObject obj || obj["type"] is not JsonValue typeValue
                    || !typeValue.TryGetValue<string>(out var type))
                    throw new InvalidOperationException("The ChatGPT plan request contains an invalid tool.");
                if (type == "function")
                {
                    functions.Add(obj.DeepClone());
                    if (obj["name"] is JsonValue name && name.TryGetValue<string>(out var functionName))
                        functionNames.Add(functionName);
                }
                else if (type is "namespace" or "web_search") retained.Add(obj.DeepClone());
                else throw new InvalidOperationException("The ChatGPT plan request contains an unsupported tool.");
            }
            if (functions.Count > 0)
                retained.Add(new JsonObject { ["type"] = "namespace", ["name"] = "hub",
                    ["description"] = "Axmol Hub tools", ["tools"] = functions });
            root["tools"] = retained;
            // The SDK drops the namespace when it reserializes a function call from history.
            foreach (var item in input.OfType<JsonObject>())
                if (item["type"] is JsonValue itemType && itemType.TryGetValue<string>(out var kind)
                    && kind == "function_call" && item["namespace"] is null
                    && item["name"] is JsonValue itemName && itemName.TryGetValue<string>(out var name)
                    && functionNames.Contains(name)) item["namespace"] = "hub";
            if (root["tool_choice"] is JsonObject choice && choice["type"]?.GetValue<string>() == "function")
                choice["namespace"] = "hub";
        }
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
}
