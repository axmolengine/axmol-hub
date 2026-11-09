using System.Net;
using System.Text;
using System.Text.Json;
using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

internal static class ClaudeMessagesChecks
{
    // One offline round-trip covers the consequential failure: ordered signed blocks lost at storage.
    internal static async Task RunAsync()
    {
        using var spy = new Spy();
        var provider = new ModelProvider { Id = "offline", Name = "Offline", IsCustom = true,
            Protocol = ModelProtocols.Messages, BaseUrl = "https://example.invalid/v1", Model = "claude-opus-5-5" };
        using var client = ChatClientFactory.Create(provider, provider.Model, null, transport: spy, maxRetries: 0);
        var pipeline = new ChatPipeline(client);
        var function = AIFunctionFactory.Create(() => "ok", "inspect");
        var turns = new List<ChatTurn> { ChatTurn.User("check") };
        List<JsonElement>? finalContent = null;
        var answer = new StringBuilder();
        await foreach (var text in pipeline.SendAsync(provider, turns.ToArray(), tools: [function], reasoningEffort: "low",
            maxOutputTokens: 100, onToolStarted: call => {
                turns.Add(ChatTurn.FunctionCall(call.CallId, call.Name, call.ArgumentsJson, reasoning: call.Reasoning,
                    batchId: call.BatchId, claudeThinking: call.ClaudeThinking, claudeContent: call.ClaudeContent));
                finalContent = null;
                return Task.CompletedTask;
            }, onToolCompleted: (call, result, failed) => {
                turns.Add(ChatTurn.FunctionResult(call.CallId, result, failed)); return Task.CompletedTask;
            }, onClaudeContent: blocks => finalContent = blocks)) answer.Append(text);
        turns.Add(ChatTurn.Assistant("done") with { ClaudeContent = finalContent });
        var restored = JsonSerializer.Deserialize<List<ChatTurn>>(JsonSerializer.Serialize(turns))!;
        restored.Add(ChatTurn.User("continue"));
        await foreach (var _ in pipeline.SendAsync(provider, restored, tools: [function], reasoningEffort: "low",
            maxOutputTokens: 100)) { }
        if (spy.Requests.Count != 3) throw new Exception("Messages did not complete a tool round and restore.");
        for (var index = 1; index < spy.Requests.Count; index++)
        {
            using var doc = JsonDocument.Parse(spy.Requests[index]);
            var root = doc.RootElement;
            var blocks = root.GetProperty("messages")[1].GetProperty("content");
            static string? Text(JsonElement block, string name) => block.TryGetProperty(name, out var value) ? value.GetString() : null;
            var messages = root.GetProperty("messages");
            if (messages.GetArrayLength() != (index == 2 ? 5 : 3)
                || (index == 2 && Text(messages[3].GetProperty("content")[0], "text") != "done"))
                throw new Exception("Restored history lost a turn or the answer it ended on.");
            var types = blocks.EnumerateArray().Select(block => Text(block, "type")).ToArray();
            if (!types.SequenceEqual(new[] { "thinking", "redacted_thinking", "tool_use", "text", "thinking", "tool_use" }))
                throw new Exception("Replayed blocks lost the order the model sent: " + string.Join(',', types));
            var calls = blocks.EnumerateArray().Where(block => Text(block, "type") == "tool_use").ToArray();
            if (calls.Length != 2 || Text(calls[0], "id") != "call_1" || Text(calls[1], "id") != "call_2")
                throw new Exception("Replayed calls lost their identity.");
            if (Text(blocks[0], "thinking") != "original thought" || Text(blocks[0], "signature") != "opaque-signature"
                || Text(blocks[1], "data") != "opaque-redacted"
                || Text(blocks[4], "thinking") != "after the first call" || Text(blocks[4], "signature") != "late-signature"
                || Text(blocks[3], "text") != "between calls \n")
                throw new Exception("Replayed signed or text blocks changed their bytes.");
            if (Text(root.GetProperty("thinking"), "type") != "adaptive"
                || Text(root.GetProperty("output_config"), "effort") != "low")
                throw new Exception("Messages reasoning parameters are not the Claude form.");
        }
        if (finalContent is not { Count: 1 } || answer.ToString() != "between calls \ndone")
            throw new Exception("Ordered capture changed the visible streaming answer.");
        Console.WriteLine("PASS: Messages replays signed thinking in the order the model sent it, bytes unchanged.");
    }

    private sealed class Spy : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!request.RequestUri!.AbsolutePath.Equals("/v1/messages", StringComparison.Ordinal))
                throw new Exception("Wrong Messages endpoint path.");
            Requests.Add(await request.Content!.ReadAsStringAsync(token));
            var frames = new List<object> {
                new { type = "message_start", message = new { id = "msg_" + Requests.Count, type = "message",
                    role = "assistant", model = "claude-opus-5-5", content = Array.Empty<object>(),
                    stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 10, output_tokens = 0 } } }
            };
            if (Requests.Count == 1)
            {
                frames.Add(new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } });
                frames.Add(new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "original thought" } });
                frames.Add(new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "opaque-" } });
                frames.Add(new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "signature" } });
                frames.Add(new { type = "content_block_stop", index = 0 });
                frames.Add(new { type = "content_block_start", index = 1, content_block = new { type = "redacted_thinking", data = "opaque-redacted" } });
                frames.Add(new { type = "content_block_stop", index = 1 });
                frames.Add(new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "call_1", name = "inspect", input = new { } } });
                frames.Add(new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "{}" } });
                frames.Add(new { type = "content_block_stop", index = 2 });
                frames.Add(new { type = "content_block_start", index = 3, content_block = new { type = "text", text = "between " } });
                frames.Add(new { type = "content_block_delta", index = 3, delta = new { type = "text_delta", text = "calls \n" } });
                frames.Add(new { type = "content_block_stop", index = 3 });
                frames.Add(new { type = "content_block_start", index = 4, content_block = new { type = "thinking", thinking = "after the first call", signature = "" } });
                frames.Add(new { type = "content_block_delta", index = 4, delta = new { type = "signature_delta", signature = "late-signature" } });
                frames.Add(new { type = "content_block_stop", index = 4 });
                frames.Add(new { type = "content_block_start", index = 5, content_block = new { type = "tool_use", id = "call_2", name = "inspect", input = new { } } });
                frames.Add(new { type = "content_block_delta", index = 5, delta = new { type = "input_json_delta", partial_json = "{}" } });
                frames.Add(new { type = "content_block_stop", index = 5 });
            }
            else
            {
                frames.Add(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "done" } });
                frames.Add(new { type = "content_block_stop", index = 0 });
            }
            frames.Add(new { type = "message_delta", delta = new { stop_reason = Requests.Count == 1 ? "tool_use" : "end_turn",
                stop_sequence = (string?)null }, usage = new { output_tokens = 10 } });
            frames.Add(new { type = "message_stop" });
            var body = string.Join("", frames.Select(frame => "event: "
                + JsonSerializer.SerializeToElement(frame).GetProperty("type").GetString()
                + "\ndata: " + JsonSerializer.Serialize(frame) + "\n\n"));
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }
}
