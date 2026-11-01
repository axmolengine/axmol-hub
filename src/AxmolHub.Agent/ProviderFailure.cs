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

    public static int? StatusOf(Exception exception) => exception switch
    {
        System.ClientModel.ClientResultException result => result.Status,
        System.Net.Http.HttpRequestException request => (int?)request.StatusCode,
        _ => null,
    };

    public static bool AuthenticationFailed(Exception exception) => StatusOf(exception) is 401 or 403;

    public static bool Recoverable(Exception exception)
        => !QuotaExhausted(exception.Message) && !AuthenticationFailed(exception)
            && (StatusOf(exception) is 408 or 429 or 500 or 502 or 503 or 504
                || StatusOf(exception) is null && exception is (System.Net.Http.HttpRequestException
                    or IOException or TimeoutException));

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

/// <summary>Hands a long provider wait to the persisted goal scheduler instead of occupying a run slot.</summary>
public sealed class ProviderCooldownException(TimeSpan delay) : Exception($"Provider cooldown: {delay.TotalSeconds:0}s")
{
    public TimeSpan Delay { get; } = delay;
}
