using System.Net.Http;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>The Messages transport boundary: shared provider headers, selection validation, bounded HTTP
/// retries and byte recording. The SDK's own retry loop is disabled so each request spends one allowance.</summary>
internal sealed class AnthropicRequestHandler(IReadOnlyDictionary<string, string> headers, string? semantics,
    string? interactionId, int maxRetries, Action<int, TimeSpan>? onWait, TimeSpan? maxInlineWait) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        var scope = RequestRetryScope.Current.Value ?? new RequestRetryScope(new()
        {
            MaxRetries = maxRetries,
            OnWait = onWait,
        });
        while (true)
        {
            await scope.ValidateSelectionAsync().ConfigureAwait(false);
            using var attempt = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) attempt.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (body is not null)
            {
                attempt.Content = new ByteArrayContent(body);
                foreach (var header in request.Content!.Headers)
                    attempt.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (var (name, value) in ProviderRequestPolicy.Headers(headers, semantics, interactionId,
                body is null ? null : Encoding.UTF8.GetString(body)))
            {
                if (name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)) continue;
                attempt.Headers.Remove(name);
                attempt.Headers.TryAddWithoutValidation(name, value);
            }
            var pair = WireLog.Directory is null ? -1 : WireLog.NextPair();
            if (pair >= 0 && body is not null)
            {
                var json = Encoding.UTF8.GetString(body);
                WireLog.TryWrite($"{pair:D4}.request.json", json);
                WireLogPolicy.Summarize(pair, attempt.RequestUri?.ToString(), json);
            }
            HttpResponseMessage response;
            try { response = await base.SendAsync(attempt, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && scope.CanRetry && ProviderFailure.Recoverable(ex))
            {
                var wait = scope.Reserve(null);
                if (maxInlineWait is { } ceiling && wait > ceiling) throw new ProviderCooldownException(wait);
                await scope.WaitAsync(wait, token).ConfigureAwait(false);
                continue;
            }
            scope.TransportDelay = null;
            foreach (var name in new[] { "retry-after-ms", "x-ms-retry-after-ms", "Retry-After" })
                if (response.Headers.TryGetValues(name, out var values)
                    && ProviderFailure.RetryAfter(values.FirstOrDefault(), DateTimeOffset.UtcNow, name.EndsWith("-ms")) is { } delay)
                { scope.TransportDelay = delay; break; }
            if ((int)response.StatusCode >= 400)
            {
                var error = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (pair >= 0) WireLog.TryWrite($"{pair:D4}.response.log", error);
                if (scope.CanRetry && ProviderFailure.RetryableStatus((int)response.StatusCode)
                    && !ProviderFailure.QuotaExhausted(error))
                {
                    var wait = scope.Reserve(scope.TransportDelay);
                    response.Dispose();
                    if (maxInlineWait is { } ceiling && wait > ceiling) throw new ProviderCooldownException(wait);
                    await scope.WaitAsync(wait, token).ConfigureAwait(false);
                    continue;
                }
            }
            else if (response.Content is { } content)
            {
                Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
                if (RequestRetryScope.Current.Value is { } shared
                    && content.Headers.ContentType?.MediaType == "text/event-stream")
                    stream = new RetryMetadataPolicy.ErrorMetadataStream(stream, shared);
                if (pair >= 0) stream = new TeeStream(stream, $"{pair:D4}.response.log");
                var replacement = new StreamContent(stream);
                foreach (var header in content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = replacement;
            }
            return response;
        }
    }
}
