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
            4 => [Error()],
            _ => [new(ChatRole.Assistant, "done")],
        });
        var answer = new StringBuilder();
        await foreach (var text in new ChatPipeline(sequence).SendAsync(provider, [ChatTurn.User("go")],
            tools: [tool], retryOptions: retry)) answer.Append(text);
        if (executions != 1 || sequence.Requests != 5 || answer.ToString() != "done"
            || !waits.SequenceEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5) }))
            throw new Exception("Request recovery replayed a tool, leaked a failed call, or failed to reset backoff.");

        foreach (var code in new[] { "insufficient_quota", "invalid_api_key", "invalid_request_error" })
        {
            var refused = new SequenceClient(_ => [Error(code)]);
            try { await Drain(refused, retry); throw new Exception("Permanent error returned success."); }
            catch (ProviderResponseException) { }
            if (refused.Requests != 1) throw new Exception("A permanent failure was retried.");
        }
        var partial = new SequenceClient(_ => [new(ChatRole.Assistant, "partial"), Error()]);
        try { await Drain(partial, retry); throw new Exception("Partial response returned success."); }
        catch (RequestRetryException ex) when (ex.Partial && ex.Retries == 0) { }
        if (partial.Requests != 1) throw new Exception("Delivered text was replayed.");
        var empty = new SequenceClient(n => n < 3 ? [] : [new(ChatRole.Assistant, "done")]);
        await Drain(empty, retry);
        if (empty.Requests != 3) throw new Exception("Empty responses did not recover.");

        // Actual SDK HTTP attempts and SSE error envelopes must share one counter, on both wires.
        foreach (var protocol in new[] { ModelProtocols.Chat, ModelProtocols.Responses })
        {
            provider.Protocol = protocol;
            var wire = new MixedWire();
            waits.Clear();
            using var client = ChatClientFactory.Create(provider, "m", null, transport: wire, maxRetries: 2,
                networkTimeout: Timeout.InfiniteTimeSpan);
            try { await Drain(client, retry); throw new Exception("Exhausted mixed retries returned success."); }
            catch (RequestRetryException ex) when (ex.Retries == 2) { }
            if (wire.Attempts != 3 || !waits.SequenceEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7) }))
                throw new Exception($"HTTP/SSE budget or Retry-After drifted: {wire.Attempts}, {string.Join(',', waits)}.");
        }
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
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
            return Task.FromResult(response);
        }
    }
}
