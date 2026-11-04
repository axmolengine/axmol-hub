using System.Globalization;
using System.Text.Json;

namespace AxmolHub.Agent;

/// <summary>Provider error classification shared by retry and run endings, not a second retry loop.</summary>
public static class ProviderFailure
{
    public static bool QuotaExhausted(string? body)
    {
        if (body is null) return false;
        try
        {
            using var json = JsonDocument.Parse(body);
            var error = json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var nested) ? nested : json.RootElement;
            foreach (var key in new[] { "type", "code" })
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is "insufficient_quota" or "usage_limit_reached" or "quota_exceeded"
                        or "billing_hard_limit_reached") return true;
        }
        catch (JsonException) { }
        return body.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || body.Contains("usage_limit_reached", StringComparison.OrdinalIgnoreCase)
            || body.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase)
            || body.Contains("billing_hard_limit_reached", StringComparison.OrdinalIgnoreCase)
            || body.Contains("usage limit has been reached", StringComparison.OrdinalIgnoreCase);
    }

    public static int? StatusOf(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var status = current switch
            {
                System.ClientModel.ClientResultException result => (int?)result.Status,
                System.Net.Http.HttpRequestException request => (int?)request.StatusCode,
                _ => null,
            };
            if (status is > 0) return status;
        }
        return null;
    }

    public static bool AuthenticationFailed(Exception exception)
        => StatusOf(exception) is 401 or 403
           || exception is ProviderResponseException { ErrorCode: "invalid_api_key" or "authentication_error"
               or "permission_denied" or "unauthorized" or "forbidden" };

    /// <summary>A provider refusal that may be retried within the goal's bounded allowance.
    /// Stream errors retain their origin and code even when the transport status was 200; arbitrary local
    /// exceptions are not provider failures and must not be silently retried.</summary>
    public static bool ProviderRefused(Exception exception)
    {
        if (AuthenticationFailed(exception) || QuotaExhausted(exception.Message)
            || exception is ProviderResponseException stream && QuotaExhausted(stream.ErrorCode)) return false;
        return StatusOf(exception) is >= 400 || exception is ProviderResponseException;
    }

    public static bool Recoverable(Exception exception)
    {
        if (exception is OperationCanceledException or RequestRetryException
            || AuthenticationFailed(exception) || QuotaExhausted(exception.Message)
            || exception is ProviderResponseException coded && QuotaExhausted(coded.ErrorCode)
            || Core.ContextOverflow.IsContextOverflow(exception.Message)
            || ChatPipeline.DeclinedStreamUsage(exception.Message)) return false;
        if (StatusOf(exception) is { } status) return status is 408 or 429 or 500 or 502 or 503 or 504;
        if (exception is ProviderResponseException stream)
            return stream.ErrorCode is "rate_limit_exceeded" or "rate_limit_error" or "too_many_requests"
                or "server_error" or "internal_server_error" or "service_unavailable" or "overloaded_error"
                or "timeout" or "request_timeout" or "empty_response" or "upstream_stream_read_error"
                || ChatPipeline.RateLimited(stream.Message)
                || stream.Message.Contains("different", StringComparison.OrdinalIgnoreCase)
                   && stream.Message.Contains("OpenAI resource", StringComparison.OrdinalIgnoreCase)
                || stream.Message.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
                || stream.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase);
        return exception is System.Net.Http.HttpRequestException or IOException or TimeoutException;
    }

    public static TimeSpan Backoff(int retriesSoFar) => TimeSpan.FromMilliseconds(Math.Min(
        Core.ProviderRetryDefaults.BackoffCeiling.TotalMilliseconds,
        Core.ProviderRetryDefaults.RateLimitBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(retriesSoFar, 0, 10))));

    public static TimeSpan? WaitFor(Exception exception) => (exception as ProviderResponseException)?.RetryDelay;

    public static ProviderResponseException FromStream(Microsoft.Extensions.AI.ErrorContent error)
    {
        TimeSpan? delay = null;
        // Adapters may preserve the provider's error envelope as raw data or as additional properties.
        foreach (var value in new object?[] { error.RawRepresentation, error.AdditionalProperties })
        {
            if (value is null) continue;
            try { delay ??= ReadRetryAfter(JsonSerializer.SerializeToElement(value)); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }
        }
        return new ProviderResponseException(string.IsNullOrWhiteSpace(error.Message)
            ? "The provider failed the response without saying why." : error.Message, error.ErrorCode, delay);
    }

    internal static TimeSpan? ReadRetryAfter(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.Replace('_', '-').ToLowerInvariant();
            if (key is "retry-after" or "retry-after-ms" or "x-ms-retry-after-ms")
            {
                var parsed = RetryAfter(property.Value.ToString(), DateTimeOffset.UtcNow, key.EndsWith("-ms"));
                if (parsed is not null) return parsed;
            }
        }
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object && ReadRetryAfter(property.Value) is { } parsed)
                return parsed;
        return null;
    }

    public static TimeSpan? RetryAfter(string? value, DateTimeOffset now, bool milliseconds = false)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && number >= 0)
            return TimeSpan.FromSeconds(Math.Min(milliseconds ? number / 1000 : number, 86400));
        if (!milliseconds && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
            return TimeSpan.FromSeconds(Math.Clamp((date - now).TotalSeconds, 0, 86400));
        return null;
    }
}

/// <summary>A failure explicitly emitted by the model endpoint, not a local execution exception.
/// Keeping the code separate from the display message prevents stream-level failures from bypassing retry.</summary>
public sealed class ProviderResponseException(string message, string? errorCode = null, TimeSpan? retryDelay = null) : InvalidOperationException(message)
{
    public string? ErrorCode { get; } = errorCode;
    public TimeSpan? RetryDelay { get; } = retryDelay;
}

/// <summary>Hands a long provider wait to the persisted goal scheduler instead of occupying a run slot.</summary>
public sealed class ProviderCooldownException(TimeSpan delay) : Exception($"Provider cooldown: {delay.TotalSeconds:0}s")
{
    public TimeSpan Delay { get; } = delay;
}
