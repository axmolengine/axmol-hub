using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AxmolHub.Agent;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

internal static class ChatStreamCompatibilityChecks
{
    // A bad auxiliary field used to discard a whole reply. Cover it once, offline, through the actual SDK.
    internal static async Task RunAsync(string root)
    {
        const long fallback = 1794628000;
        foreach (var (value, expected) in new[] { ("1794627973", 1794627973L), ("1794627973537", 1794627973L),
            ("-1000000000001", -1000000001L), ("253402300799999", 253402300799L),
            ("9223372036854775807", fallback), ("null", fallback), ("\"bad\"", fallback), ("1e100", fallback) })
        {
            var json = "{\"created\":" + value + ",\"choices\":[{\"delta\":{\"content\":\"Hi\",\"tool_calls\":[]}}]}";
            var result = JsonNode.Parse(ChatStreamCompatibilityPolicy.Normalize(json, fallback))!;
            if (result["created"]!.GetValue<long>() != expected
                || !JsonNode.DeepEquals(result["choices"], JsonNode.Parse(json)!["choices"]))
                throw new Exception("Timestamp fallback changed content or failed for " + value);
            if (value == "1794627973" && ChatStreamCompatibilityPolicy.Normalize(json, fallback) != json)
                throw new Exception("Valid seconds were rewritten.");
        }
        foreach (var json in new[] { "[DONE]", "{\"error\":{\"message\":\"denied\"},\"created\":null}", "{\"choices\":[}" })
            if (ChatStreamCompatibilityPolicy.Normalize(json, fallback) != json) throw new Exception("A non-timestamp failure was hidden.");

        const string raw = "data: {\"id\":\"chatcmpl\",\"object\":\"chat.completion.chunk\",\"created\":1794627973537,\r\n"
            + "data: \"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\r\n\r\n"
            + "data: {\"id\":\"chatcmpl\",\"object\":\"chat.completion.chunk\",\"created\":null,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\r\n\r\n"
            + "data: [DONE]\r\n\r\n";
        var previousLog = WireLog.Directory;
        var logRoot = Path.Combine(root, "timestamp-wire");
        var text = new StringBuilder();
        try
        {
            WireLog.Directory = logRoot;
            var provider = new ModelProvider { IsCustom = true, BaseUrl = "http://127.0.0.1:9/v1", Model = "m", Protocol = ModelProtocols.Chat };
            using var handler = new Handler(raw);
            using var client = ChatClientFactory.Create(provider, "m", null, transport: handler, maxRetries: 0);
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) text.Append(update.Text);
        }
        finally { WireLog.Directory = previousLog; }
        if (text.ToString() != "Hi" || !Directory.EnumerateFiles(logRoot, "*.response.log", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path) == raw))
            throw new Exception("Timestamp handling lost the reply or rewrote raw wire evidence.");
        Console.WriteLine("PASS: auxiliary Chat timestamps preserve seconds, convert milliseconds, fall back locally, and retain raw wire evidence through fragmented SSE.");

        // A relay's empty continuation type must not abort the assistant's tool workflow.
        const string toolStream = "data: {\"id\":\"chatcmpl\",\"object\":\"chat.completion.chunk\",\"created\":1794634195,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"id\":\"call1\",\"type\":\"function\",\"index\":0,\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"chatcmpl\",\"object\":\"chat.completion.chunk\",\"created\":1794634195,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"id\":\"\",\"type\":\"\",\"index\":0,\"function\":{\"name\":\"\",\"arguments\":\"{\\\"path\\\":\"}}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"chatcmpl\",\"object\":\"chat.completion.chunk\",\"created\":1794634195,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"id\":\"\",\"type\":\"\",\"index\":0,\"function\":{\"name\":\"\",\"arguments\":\"\\\"file.txt\\\"}\"}}]},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"chatcmpl\",\"object\":\"chat.completion.chunk\",\"created\":1794634195,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
        var calls = new List<FunctionCallContent>();
        try
        {
            WireLog.Directory = logRoot;
            var provider = new ModelProvider { IsCustom = true, BaseUrl = "http://127.0.0.1:9/v1", Model = "m", Protocol = ModelProtocols.Chat };
            using var handler = new Handler(toolStream);
            using var client = ChatClientFactory.Create(provider, "m", null, transport: handler, maxRetries: 0);
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }
        finally { WireLog.Directory = previousLog; }
        var call = calls.Count == 1 ? calls[0] : null;
        var arguments = call?.Arguments;
        if (call is null || call.CallId != "call1" || call.Name != "read_file"
            || arguments is null || !arguments.TryGetValue("path", out var path)
            || path?.ToString() != "file.txt"
            || !Directory.EnumerateFiles(logRoot, "*.response.log", SearchOption.AllDirectories)
                .Any(file => File.ReadAllText(file) == toolStream))
            throw new Exception("Chat stream compatibility lost the tool call or rewrote raw wire evidence.");
        Console.WriteLine("PASS: streamed tool calls accept empty continuation types and preserve raw wire evidence.");
    }

    private sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], token);
    }
    private sealed class Handler(string raw) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var content = new StreamContent(new FragmentStream(Encoding.UTF8.GetBytes(raw)));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
