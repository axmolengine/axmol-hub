using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Reads the selected ChatGPT account's plan model catalog.</summary>
internal static class OpenAiPlanModels
{
    private const string ModelsUrl = "https://api.openai.com/v1/models";

    public static async Task<ModelFetchResult> FetchAsync(HttpClient http, ModelProvider provider, CancellationToken token)
    {
        try
        {
            var get = provider.OpenAiTokenProvider ?? throw new InvalidOperationException("OpenAI credential service is unavailable.");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var credential = await get(attempt != 0, token).ConfigureAwait(false);
                if (!credential.IsDynamicRegistration) return ModelFetchResult.Unreachable("Sign in again to migrate this OpenAI credential.");
                if (!credential.HasPlanUsage) return ModelFetchResult.Unreachable("ChatGPT plan usage was not authorized. Sign in again and enable it.");
                using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!response.IsSuccessStatusCode)
                    return ModelFetchResult.Unreachable($"OpenAI catalog returned HTTP {(int)response.StatusCode}.") with
                    {
                        Diagnostic = await ModelCatalogFailure.DescribeAsync(request, response, token).ConfigureAwait(false),
                    };
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return Parse(await ModelList.ReadBoundedAsync(stream, token).ConfigureAwait(false));
            }
            return ModelFetchResult.Unreachable("OpenAI authorization was rejected.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException or IOException)
        { return ModelFetchResult.Unreachable(ex is InvalidOperationException ? ex.Message : "The OpenAI model catalog could not be read; retry later."); }
    }

    public static ModelFetchResult Parse(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return ModelFetchResult.Unreachable("OpenAI returned an invalid model catalog.");
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("visibility", out var visibility) || visibility.ValueKind != JsonValueKind.String || visibility.GetString() != "list"
                || !model.TryGetProperty("slug", out var slug) || slug.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(slug.GetString())) continue;
            var name = slug.GetString()!;
            if (seen.Add(name)) names.Add(name);
            if (names.Count == ModelList.MaxModels) break;
        }
        return new ModelFetchResult(names, null);
    }
}
