using System.Net;
using System.Text;
using AxmolHub.Core;
using Microsoft.Extensions.AI;


/// <summary>Replies a fixed status to every request: the way to prove a client treats 403/404 as
/// "no answer, fall back" rather than "throw".</summary>
/// <summary>
/// An <see cref="IProgress{T}"/> that calls back on the reporting thread.
///
/// <para>The framework's own <c>Progress&lt;T&gt;</c> queues to the thread pool when there is no synchronization
/// context to post to, and a console host has none — so a check that reads what was reported on the next line
/// sometimes has nothing yet. The app does use <c>Progress&lt;T&gt;</c>, and that hand-off to the UI thread is
/// asserted by <c>--verify-shell</c>, where a dispatcher exists to pump it.</para>
/// </summary>
sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>
/// A scripted device-code endpoint: one issue response, then the scripted answers to each poll (the last one
/// repeating, so a loop that keeps waiting can be observed rather than having to be told in advance how many
/// polls it will make).
///
/// <para>Requests are recorded as "METHOD path body" because the assertions here are about what this app put on
/// the wire — the public client id, the device grant, and above all the absence of a client secret, which no
/// assertion about the returned token could ever see.</para>
/// </summary>
sealed class DeviceCodeTransport(string issueBody, params string[] pollBodies) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    /// <summary>How many polls this endpoint has answered.</summary>
    public int Polls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var isIssue = path.EndsWith("/login/device/code", StringComparison.Ordinal);
        Requests.Add($"{request.Method.Method} {path} {body}");

        var payload = isIssue
            ? issueBody
            : pollBodies.Length == 0
                ? "{}"
                : pollBodies[Math.Min(Polls++, pollBodies.Length - 1)];

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }
}

sealed class StatusCodeHandler(HttpStatusCode status) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
}

sealed class FixtureHandler(byte[] bytes) : HttpMessageHandler
{
    public int Requests { get; private set; }
    public bool IgnoreRange { get; set; }
    public bool SawRange { get; private set; }
    // When set, the first non-Range request returns only this many bytes while the
    // Content-Length header still claims the full size, simulating a mid-transfer drop.
    public int? TruncateFirstResponseTo { get; set; }
    private bool _truncated;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range is not null && !IgnoreRange)
        {
            SawRange = true;
            var from = range.From ?? 0;
            var to = range.To ?? bytes.LongLength - 1;
            if (from >= bytes.LongLength)
            {
                // Range start is past the end; a real server replies 416.
                var unsatisfied = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request };
                unsatisfied.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(bytes.LongLength);
                return Task.FromResult(unsatisfied);
            }
            var length = (int)(to - from + 1);
            var slice = new byte[length];
            Array.Copy(bytes, (int)from, slice, 0, length);
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent(slice) };
            partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, bytes.LongLength);
            return Task.FromResult(partial);
        }
        if (TruncateFirstResponseTo is { } cut && !_truncated)
        {
            _truncated = true;
            var slice = new byte[cut];
            Array.Copy(bytes, 0, slice, 0, cut);
            var content = new ByteArrayContent(slice);
            content.Headers.ContentLength = bytes.LongLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
}

/// <summary>
/// Answers every request with the same JSON body and keeps what was actually sent — the path, and every header
/// with its values — because the assertions this exists for are about something a request must carry and a
/// response can never show.
///
/// <para><b>The snapshot is taken inside <see cref="SendAsync"/>, not handed out as the
/// <see cref="HttpRequestMessage"/>.</b> The caller disposes its request as soon as its own method returns, and an
/// assertion that read headers off that object afterwards would be measuring the harness's lifetime rather than
/// the request. Values are joined with ", " when a name arrives more than once, so "sent twice" is visible instead
/// of hidden behind whichever arrived first.</para>
/// </summary>
sealed class RequestRecorder(byte[] payload) : HttpMessageHandler
{
    /// <summary>One request, as it went out. Indexed by header name, case-insensitively.</summary>
    internal sealed record Recorded(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
    {
        /// <summary>A header's value, or <c>null</c> when the request did not carry that name at all.</summary>
        public string? this[string name] => Headers.GetValueOrDefault(name);
    }

    public List<Recorded> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(", ", header.Value),
            StringComparer.OrdinalIgnoreCase);
        Requests.Add(new Recorded(request.Method.Method, request.RequestUri?.AbsolutePath ?? "", headers));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(Encoding.UTF8.GetString(payload), Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>In-memory <see cref="ISecretStore"/> backing the <c>--check-ai-providers</c> assertions.</summary>
sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();
    public string? Read(string providerId) => _values.TryGetValue(providerId, out var value) ? value : null;
    public void Write(string providerId, string key) => _values[providerId] = key;
    public void Delete(string providerId) => _values.Remove(providerId);
}

/// <summary>A scripted <see cref="IChatClient"/> for the <c>--check-ai-sessions</c> assertions: yields fixed
/// text chunks and records the messages it was asked to answer, so the pipeline can be tested with no network
/// at all. This is the payoff of routing every provider through the M.E.AI abstraction — the seam is fakeable.</summary>
sealed class FakeChatClient(IReadOnlyList<string> chunks, bool honorCancellation = false,
    (long Input, long Output, long Reasoning, long Cached)[]? usage = null, Exception? failure = null) : IChatClient
{
    public IList<ChatMessage>? LastMessages { get; private set; }
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        LastOptions = options;
        foreach (var chunk in chunks)
        {
            if (honorCancellation) cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, chunk);
            await Task.Yield();
        }

        // The third argument is the end-of-stream report a real gateway puts after the last chunk. It is a
        // parameter rather than a second client class because the read-back assertions need the same scripted
        // text path, and a usage chunk is the only part of it that differs.
        if (usage is not null)
            foreach (var report in usage)
            {
                yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant,
                [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = report.Input,
                        OutputTokenCount = report.Output,
                        ReasoningTokenCount = report.Reasoning,
                        CachedInputTokenCount = report.Cached,
                        TotalTokenCount = report.Input + report.Output,
                    }),
                ]);
                await Task.Yield();
            }
        if (failure is not null) throw failure;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>A scripted <see cref="IChatClient"/> that asks for one tool call and then answers: the follow-up
/// request is what proves the result was handed back. <c>honorCancellation</c> matters because parking works by
/// cancelling the stream — a client that ignored the token would answer a turn that was supposed to stop.</summary>
/// <summary>
/// One response asking for several calls — the shape a gateway returns however the request asked it not to, and the
/// shape the batch executor exists for. The calls come back in the order they are named, and the second request is
/// kept so a check can read what the model was actually shown.
/// </summary>
sealed class MultiCallChatClient(params string[] toolNames) : IChatClient
{
    private int _calls;

    public IList<ChatMessage> SecondRequest { get; private set; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (++_calls == 1)
        {
            var contents = new List<AIContent>(toolNames.Length);
            for (var index = 0; index < toolNames.Length; index++)
                contents.Add(new FunctionCallContent("m" + index, toolNames[index],
                    new Dictionary<string, object?>()));
            yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
        }
        else
        {
            SecondRequest = messages.ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "都读完了。");
        }

        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

sealed class ToolLoopChatClient(bool honorCancellation = false, string callName = "get_projects", int toolRounds = 1) : IChatClient{
    public int CallCount { get; private set; }
    public IList<ChatMessage> SecondRequest { get; private set; } = [];
    public IList<ChatMessage> ThirdRequest { get; private set; } = [];
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastOptions = options;
        var request = messages.ToList();
        // Preserve the cancellation boundary: a parked request never becomes an observed follow-up.
        if (honorCancellation && CallCount > toolRounds) cancellationToken.ThrowIfCancellationRequested();
        if (CallCount == 2) SecondRequest = request;
        if (CallCount == 3) ThirdRequest = request;
        if (CallCount <= toolRounds)
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent($"call-{CallCount}", callName, new Dictionary<string, object?>()),
                });
        }
        else
        {
            if (!request.Any(message => message.Contents.Any(content => content is FunctionResultContent)))
                throw new Exception("The function result was missing from the follow-up request.");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Projects loaded.");
        }

        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>One update: the endpoint's own error, in the shape the OpenAI responses wire carries it.
///
/// <para>An overloaded endpoint sends <c>event: error</c> with "Our servers are currently overloaded" and then
/// <c>response.failed</c>, and the SDK ends the stream there — so the turn arrived with no text, no reasoning and no
/// finish reason, and the person was told the model had returned nothing and to check the model configuration.
/// Measured 2026-10-21 on gpt-6-sol. The sentence has to survive the trip; that is what this fixture pins.</para></summary>
sealed class ErrorStreamChatClient(string message) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new ErrorContent(message) });
        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Asks for another tool call on every turn: the only thing that can end this loop is the pipeline's
/// iteration cap, which is exactly what the assertion needs to prove.</summary>
sealed class InsistentToolClient : IChatClient
{
    public int CallCount { get; private set; }
    public ChatOptions? LastOptions { get; private set; }
    public IList<ChatMessage> LastRequest { get; private set; } = [];

    /// <summary>How many tool calls this client returns before it answers in text — the model deciding the
    /// investigation is done, which is the only thing that ends the loop now that there is no round cap.</summary>
    public int RoundsBeforeAnswering { get; init; } = int.MaxValue;

    /// <summary>Ask for the same call every round instead of varying the argument. This is the spin shape, and
    /// it is what the spin guard's refusal and termination are measured against end to end.</summary>
    public bool SameCallEveryRound { get; init; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastOptions = options;
        LastRequest = messages.ToList();
        if (CallCount > RoundsBeforeAnswering)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("查完了，结论如下。")]);
            yield break;
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            // A distinct argument per round on purpose when the fixture is investigating: the spin guard
            // refuses an identical repeat, and that probe sets SameCallEveryRound to ask for the other thing.
            new FunctionCallContent($"call-{CallCount}", "get_projects",
                SameCallEveryRound
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["round"] = CallCount }),
            new TextContent($"第 {CallCount} 轮"),
        });
        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Answers the shipped OpenAI bridges from memory at a loopback URL and keeps the body they serialized.
/// The hosted-search cells need the bytes rather than the intention: a declaration that lands in the wrong field
/// is invisible from every other vantage point in this repository, and this one costs no request.</summary>
sealed class WireRecorder : System.Net.Http.HttpMessageHandler
{
    public string Body = "";

    protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(cancellationToken);
        var responses = request.RequestUri?.AbsolutePath.Contains("responses", StringComparison.Ordinal) == true;
        var json = responses
            ? "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\",\"model\":\"m\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}]}"
            : "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>A transport that answers 429 — optionally forever — and then a valid completion, counting every attempt
/// the pipeline made. The retry path is otherwise invisible from this repository: the policy that implements it
/// lives in a dependency, so without a counter here nothing knows how many requests a turn really costs, and a
/// dropped retry would look exactly like a working channel that simply was not needed today.</summary>
internal sealed class RateLimitWire(int failFirst, bool always = false, int retryAfterSeconds = 0,
    HttpStatusCode status = HttpStatusCode.TooManyRequests, string? errorBody = null, bool omitRetryAfter = false,
    bool networkFailure = false)
    : System.Net.Http.HttpMessageHandler
{
    public int Attempts;
    public bool SawRetryAfter;
    public string Failure = "";

    protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        Attempts++;
        if (always || Attempts <= failFirst)
        {
            if (networkFailure) throw new HttpRequestException("Simulated network interruption.");
            var throttled = new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    errorBody ?? ("{\"error\":{\"message\":\"Rate limit reached. Slow down.\",\"type\":\"rate_limit_error\","
                    + "\"code\":\"rate_limit_exceeded\"}}"), Encoding.UTF8, "application/json"),
            };
            // Zero, not absent: the policy has to be seen reading the provider's own wait, and a real delay would
            // make the suite depend on a clock. The cells that measure the wait ask for seconds instead — see the
            // hook below the streaming retry cell, where the number has to be the provider's, not the jitter's.
            if (!omitRetryAfter)
                throttled.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            SawRetryAfter = true;
            return System.Threading.Tasks.Task.FromResult(throttled);
        }

        var responses = request.RequestUri?.AbsolutePath.Contains("responses", StringComparison.Ordinal) == true;
        return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses
                ? "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\",\"model\":\"m\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}]}"
                : "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>An ISecretStore that implements nothing but the three required members, to prove
/// the default backend descriptor is the honest one.</summary>
internal sealed class BareSecretStore : ISecretStore
{
    public string? Read(string providerId) => null;
    public void Write(string providerId, string key) { }
    public void Delete(string providerId) { }
}

/// <summary>A change-gate handoff that records its own release, so an assertion can tell "the command finished and
/// gave the gate back" from "the gate was taken and nobody remembers where it went".</summary>
internal sealed class GateHandoff(Action onRelease) : IDisposable
{
    public void Dispose() => onRelease();
}
