using AxmolHub.Agent;
using AxmolHub.Core;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Net;
using System.Text;

internal static class RequestRetryChecks
{
    internal static async Task RunAsync()
    {
        var provider = new ModelProvider { IsCustom = true, Name = "retry fixture", BaseUrl = "https://fixture.test/v1", Model = "m" };
        var waits = new List<TimeSpan>();
        var retry = new RequestRetryOptions { MaxRetries = 2, Delay = (delay, token) =>
        { token.ThrowIfCancellationRequested(); waits.Add(delay); return Task.CompletedTask; } };
        var executions = 0;
        var tool = AIFunctionFactory.Create((Func<string>)(() => { executions++; return "read"; }),
            new AIFunctionFactoryOptions { Name = "probe" });
        var sequence = new SequenceClient(n => n switch
        {
            1 => [Error()],
            2 => [Call("discarded"), Error()],
            3 => [Call("valid")],
            4 => throw new AggregateException(new HttpIOException(HttpRequestError.ResponseEnded,
                "The response ended prematurely. (ResponseEnded)")),
            _ => [new(ChatRole.Assistant, "done")],
        });
        var answer = new StringBuilder();
        await foreach (var text in new ChatPipeline(sequence).SendAsync(provider, [ChatTurn.User("go")],
            tools: [tool], retryOptions: retry)) answer.Append(text);
        if (executions != 1 || sequence.Requests != 5 || answer.ToString() != "done"
            || !waits.SequenceEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5) }))
            throw new Exception("Request recovery replayed a tool, leaked a failed call, or failed to reset backoff.");

        foreach (var code in new[] { "insufficient_quota", "subscription_sharing_usage_limit_exceeded",
            "invalid_api_key", "invalid_request_error", "model_not_found" })
        {
            var refused = new SequenceClient(_ => [Error(code)]);
            try { await Drain(refused, retry); throw new Exception("Permanent error returned success."); }
            catch (ProviderResponseException) { }
            if (refused.Requests != 1) throw new Exception("A permanent failure was retried.");
        }
        var unknownProviderError = new SequenceClient(n => n == 1 ? [Error("gateway_unexpected_fault")]
            : [new(ChatRole.Assistant, "recovered")]);
        await Drain(unknownProviderError, retry);
        if (unknownProviderError.Requests != 2) throw new Exception("Unknown provider error did not back off.");
        var partial = new SequenceClient(_ => [new(ChatRole.Assistant, "partial"), Error()]);
        try { await Drain(partial, retry); throw new Exception("Partial response returned success."); }
        catch (RequestRetryException ex) when (ex.Partial && ex.Retries == 0) { }
        if (partial.Requests != 1) throw new Exception("Delivered text was replayed.");
        var empty = new SequenceClient(n => n < 3 ? [] : [new(ChatRole.Assistant, "done")]);
        await Drain(empty, retry);
        if (empty.Requests != 3) throw new Exception("Empty responses did not recover.");
        if (!ProviderFailure.RetryableMissingAnswer("stop", false, false)
            || ProviderFailure.RetryableMissingAnswer("CONTENT_FILTER", false, false)
            || ProviderFailure.RetryableMissingAnswer("length", false, false)
            || ProviderFailure.RetryableMissingAnswer("stop", true, false))
            throw new Exception("A terminal or tool-requesting empty answer was treated as retryable.");
        waits.Clear();
        var interrupted = new SequenceClient(n => n == 1 ? [Error("upstream_stream_read_error")]
            : [new(ChatRole.Assistant, "recovered")]);
        await Drain(interrupted, retry);
        if (interrupted.Requests != 2 || !waits.SequenceEqual([TimeSpan.FromSeconds(5)]))
            throw new Exception("An upstream stream interruption did not recover with backoff.");

        var ended = new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely. (ResponseEnded)");
        foreach (var failure in new Exception[] { ended, new AggregateException(new AggregateException(ended)) })
        {
            waits.Clear();
            var disconnected = new SequenceClient(n => n == 1 ? throw failure : [new(ChatRole.Assistant, "recovered")]);
            await Drain(disconnected, retry);
            if (disconnected.Requests != 2 || !waits.SequenceEqual([TimeSpan.FromSeconds(5)]))
                throw new Exception("A direct or aggregated transport interruption did not recover with backoff.");
        }
        foreach (var permanent in new Exception[] { new OperationCanceledException(), new InvalidOperationException("local bug"),
            new ProviderResponseException("quota exhausted", "insufficient_quota"),
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized),
            new RequestRetryException(ended, 2, TimeSpan.Zero, partial: true) })
            if (ProviderFailure.Recoverable(new AggregateException(ended, permanent)))
                throw new Exception("A mixed aggregate hid a non-retryable failure.");
        if (ProviderFailure.Recoverable(new AggregateException()))
            throw new Exception("An empty aggregate was treated as a transport failure.");

        IReadOnlyList<ChatTurn> summaryHistory = [ChatTurn.User("summarize this history"),
            ChatTurn.FunctionCall("prior", "probe", "{}", reasoning: "original reasoning"),
            ChatTurn.FunctionResult("prior", "read"), ChatTurn.Assistant("prior answer"), ChatTurn.User("continue")];
        // Actual SDK HTTP attempts and SSE error envelopes must share one counter, on both wires. A summary
        // retains its historical reasoning through recovery and forbids new calls without dropping schemas.
        foreach (var protocol in new[] { ModelProtocols.Chat, ModelProtocols.Responses })
        {
            provider.Protocol = protocol;
            provider.Model = "deepseek-flash";
            var wire = new MixedWire();
            waits.Clear();
            var reasoning = new ReasoningTable();
            using var client = ChatClientFactory.Create(provider, provider.Model, reasoning, transport: wire, maxRetries: 2,
                networkTimeout: Timeout.InfiniteTimeSpan);
            try
            {
                await foreach (var _ in new ChatPipeline(client, reasoning).SendAsync(provider, summaryHistory,
                    "Summarize only.", declaredTools: [tool], toolMode: ChatToolMode.None, retryOptions: retry)) { }
                throw new Exception("Exhausted mixed retries returned success.");
            }
            catch (RequestRetryException ex) when (ex.Retries == 2) { }
            foreach (var body in wire.Bodies)
            {
                using var json = System.Text.Json.JsonDocument.Parse(body);
                var root = json.RootElement;
                if (root.GetProperty("tool_choice").GetString() != "none" || root.GetProperty("tools").GetArrayLength() != 1)
                    throw new Exception("A summary could select new tools or lost its historical tool schemas.");
                if (protocol == ModelProtocols.Chat)
                {
                    var messages = root.GetProperty("messages").EnumerateArray().ToList();
                    var prior = messages.First(m => m.TryGetProperty("tool_calls", out _));
                    if (prior.GetProperty("reasoning_content").GetString() != "original reasoning"
                        || messages.Where(m => m.GetProperty("role").GetString() == "assistant")
                            .Any(m => !m.TryGetProperty("reasoning_content", out var thought) || string.IsNullOrEmpty(thought.GetString())))
                        throw new Exception("Summary recovery lost original reasoning or omitted a required thinking field.");
                }
            }
            if (wire.Attempts != 3 || !waits.SequenceEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7) }))
                throw new Exception($"HTTP/SSE budget or Retry-After drifted: {wire.Attempts}, {string.Join(',', waits)}.");
        }
        // A rebuilt reasoning-only segment must not buy a fresh HTTP/SSE allowance. The same client resets
        // that allowance only for the next model request after a successful tool-call response.
        var carriedWaits = new List<TimeSpan>();
        var carriedRetries = -1;
        var carried = new RequestRetryOptions { MaxRetries = 2, RetriesUsed = 1,
            OnRequestEnded = used => carriedRetries = used, Delay = (delay, _) =>
            { carriedWaits.Add(delay); return Task.CompletedTask; } };
        var carriedWire = new MixedWire();
        using (var carriedClient = ChatClientFactory.Create(provider, provider.Model, null, transport: carriedWire))
            try { await Drain(carriedClient, carried); throw new Exception("A carried retry budget returned success."); }
            catch (RequestRetryException ex) when (ex.Retries == 2) { }
        if (carriedWire.Attempts != 2 || carriedRetries != 2 || !carriedWaits.SequenceEqual([TimeSpan.FromSeconds(1)]))
            throw new Exception("Rebuilding a request granted a second transport/stream retry budget.");
        carriedWaits.Clear();
        var nextRequest = new SequenceClient(n => n switch
        { 1 => [Call("carried")], 2 => [Error()], _ => [new(ChatRole.Assistant, "done")] });
        await foreach (var _ in new ChatPipeline(nextRequest).SendAsync(provider, [ChatTurn.User("go")],
            tools: [tool], retryOptions: carried)) { }
        if (nextRequest.Requests != 3 || carriedRetries != 1 || !carriedWaits.SequenceEqual([TimeSpan.FromSeconds(5)]))
            throw new Exception("A successful tool round kept the preceding request's exhausted budget.");

        using var stop = new CancellationTokenSource();
        var cancelWait = new RequestRetryOptions { MaxRetries = 2, OnWait = (attempt, _) => { if (attempt > 0) stop.Cancel(); } };
        var longWait = new SequenceClient(_ => [new(ChatRole.Assistant, new List<AIContent> { new ErrorContent("busy")
        { ErrorCode = "rate_limit_exceeded", AdditionalProperties = new() { ["Retry-After"] = "3600" } } })]);
        try { await Drain(longWait, cancelWait, stop.Token); throw new Exception("Cancelled wait returned success."); }
        catch (OperationCanceledException) { }
        if (longWait.Requests != 1) throw new Exception("Cancellation sent another request.");
        Console.WriteLine("PASS: non-goal request recovery shares HTTP/SSE budgets, resets backoff, preserves Retry-After, never replays tools/text, and cancels long waits.");
    }

    private static ChatResponseUpdate Error(string code = "rate_limit_exceeded")
        => new(ChatRole.Assistant, new List<AIContent> { new ErrorContent("fixture error") { ErrorCode = code } });
    private static ChatResponseUpdate Call(string id)
        => new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent(id, "probe", new Dictionary<string, object?>()) });
    private static async Task Drain(IChatClient client, RequestRetryOptions retry, CancellationToken token = default)
    {
        await foreach (var _ in new RequestRetryClient(client, retry, null).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], cancellationToken: token)) { }
    }
    private sealed class SequenceClient(Func<int, ChatResponseUpdate[]> updates) : IChatClient
    {
        public int Requests;
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var update in updates(++Requests)) yield return update;
            await Task.CompletedTask;
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
    private sealed class MixedWire : HttpMessageHandler
    {
        public int Attempts;
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            var first = ++Attempts == 1;
            var error = "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"too_many_requests\",\"code\":\"rate_limit_exceeded\",\"message\":\"Your requests have exceeded token rate limit.\",\"headers\":{\"Retry-After\":\"7\"}}}\n\n";
            if (request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
                error = "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_fixture\",\"object\":\"response\",\"created_at\":1,\"status\":\"in_progress\",\"output\":[]}}\n\n"
                    + error + "event: response.failed\ndata: {\"type\":\"response.failed\",\"response\":{\"id\":\"resp_fixture\",\"object\":\"response\",\"created_at\":1,\"status\":\"failed\",\"output\":[],\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Your requests have exceeded token rate limit.\"}}}\n\n";
            var response = new HttpResponseMessage(first ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                Content = new StringContent(first ? "{\"error\":{\"code\":\"rate_limit_exceeded\"}}" : error,
                    Encoding.UTF8, first ? "application/json" : "text/event-stream"),
            };
            if (first) response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return response;
        }
    }
}
