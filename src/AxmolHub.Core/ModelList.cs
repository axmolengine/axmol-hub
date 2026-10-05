using System.Text.Json;

namespace AxmolHub.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Reading a provider's model list (GET {baseUrl}/models, the OpenAI protocol)
//
// Why the list is fetched rather than shipped: a model name written into a JSON file
// goes stale the moment the provider adds or retires one, and a stale default is worse
// than none — it is offered in the list and fails on first use with a 404 that blames
// the user's key. The manifest may recommend names for initial activation, but only
// models returned by the endpoint are added from the catalog.
//
// Three rules, each forced by what the endpoint can actually do:
//
//  1. **A keyless provider is not an error case.** Ollama answers `/v1/models` with no
//     credential, so the Authorization header is added only when there is a secret. A
//     header built from a null secret would send the literal string "Bearer" and read
//     as a broken key on a provider that never wanted one.
//
//  2. **"Could not ask" is not the same answer as "there are none".** A non-2xx, a
//     timeout, a body over the cap — all of them come back as a *problem*, and the
//     caller keeps whatever it already had. Treating a hotel-wifi timeout as an empty
//     list would silently wipe a provider's models every time the network blipped.
//
//  3. **A 200 with an empty array is an answer, and it replaces the cache.** A
//     provider that retired every model should show an empty list, not a stale one.
//     Distinguishing this from rule 2 is the whole reason the result is a record with
//     a `Problem` rather than a count.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What came back from asking a provider which models it serves.
///
/// <para><see cref="Problem"/> is non-null exactly when the question could not be answered. It is
/// deliberately a message rather than an exception: this fetch runs unattended after a sign-in and from a
/// refresh button, and a model list is worth less than the setting the user was on.</para>
/// </summary>
public sealed record ModelFetchResult(IReadOnlyList<string> Models, string? Problem)
{
    /// <summary>Whether the endpoint answered — including with an empty list.</summary>
    public bool Reachable => Problem is null;

    /// <summary>An answer that could not be obtained; <paramref name="problem"/> says why, in English for the log.</summary>
    public static ModelFetchResult Unreachable(string problem) => new([], problem);
}

/// <summary>
/// The provider-side half of the model list: one HTTPS request, bounded, parsed with no model classes.
///
/// <para>Static and <b>non-throwing</b> by design. This runs on a path the user did not explicitly ask to
/// be on the network — right after a browser sign-in closes — so a failure has to arrive as a value the
/// caller can ignore, not as an exception that unwinds the settings page.</para>
///
/// <para>The parser walks <c>JsonDocument</c> rather than deserializing a model, because the only field that
/// matters is <c>data[].id</c> and a gateway is free to attach whatever else it likes to each entry. Binding
/// the whole object would make an unexpected extra field a hard failure.</para>
/// </summary>
public static class ModelList
{
    /// <summary>
    /// The response is capped at 1 MiB. Past that we are not reading a model list — a gateway that echoes a
    /// whole directory would blow past it — so the fetch is abandoned rather than buffered.
    /// </summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>
    /// A model list longer than this is truncated. The cap is not about correctness (every entry kept is
    /// real) but about the UI: an endpoint advertising thousands of models would make the settings page
    /// unusable, and the ones at the end are the least likely to be what the user is looking for.
    /// </summary>
    public const int MaxModels = 500;

    /// <summary>
    /// Asks <paramref name="provider"/> which models it serves.
    ///
    /// <para>The URL is built by appending to the provider's own base URL rather than read from the
    /// manifest: a preset pointed at a self-hosted deployment must ask that deployment. The trailing slash
    /// is trimmed because <c>https://host/v1/</c> + <c>/models</c> would otherwise produce <c>//models</c>.</para>
    ///
    /// <para>Redirects are not followed across schemes and no host is read from the manifest — the base URL is
    /// either the manifest's own or something the user typed, and either way the answer is worth refusing
    /// rather than chasing.</para>
    /// </summary>
    public static async Task<ModelFetchResult> FetchAsync(
        HttpClient client,
        ModelProvider provider,
        CancellationToken cancellationToken = default)
    {
        if (!AiProviderEntry.IsUsableBaseUrl(provider.BaseUrl))
        {
            return ModelFetchResult.Unreachable("The provider's base URL is not usable.");
        }

        var url = provider.BaseUrl.TrimEnd('/') + "/models";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Rule 1. Ollama answers with no credential at all, so the header exists only when there is a
            // secret to put in it — an Authorization header carrying an empty token reads as a rejected key.
            if (provider.Credential?.Secret is { Length: > 0 } secret)
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A 401 here is the *fetch* failing, not the key check: the two are separate requests with
                // separate rules, and this one reports "could not read the list" for all of them alike.
                return ModelFetchResult.Unreachable($"The provider returned HTTP {(int)response.StatusCode}.");
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared > MaxBytes)
            {
                return ModelFetchResult.Unreachable($"The model list is too large ({declared} bytes).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ModelFetchResult(Parse(await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false)), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ModelFetchResult.Unreachable("The model list request was cancelled.");
        }
        catch (Exception exception)
        {
            // Anything the transport can throw — DNS, TLS, a refused connection, a timeout — lands here.
            return ModelFetchResult.Unreachable("The model list is unavailable: " + exception.Message);
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxBytes"/> and refuses anything larger, rather than trusting
    /// <c>Content-Length</c>: a chunked response declares no length at all, and the cap is the only thing
    /// standing between a misbehaving endpoint and an unbounded buffer.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxBytes)
            {
                throw new InvalidDataException($"The model list exceeds {MaxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Pulls <c>data[].id</c> out of the response.
    ///
    /// <para>De-duplicated case-insensitively and kept in the endpoint's order, because a gateway that lists
    /// its preferred model first has told us something worth preserving. Entries without a usable
    /// <c>id</c> are skipped rather than turned into empty rows.</para>
    ///
    /// <para>A body that is not this shape at all — an HTML error page from a proxy, an empty object — yields
    /// an <b>empty list, not a problem</b>. The endpoint answered 200; it just did not answer in the
    /// protocol. Callers treat that as "this provider offers no models it will tell us about", which is the
    /// truth, and the user can still type a name by hand.</para>
    ///
    /// <para>That holds for a body that is not <i>JSON</i> either, which is the common case behind a captive
    /// portal or a misconfigured proxy: <c>JsonDocument.Parse</c> throws on it, and an exception here would
    /// surface as a failed sign-in even though the credential was stored fine. A malformed body is caught and
    /// reported as the same empty list — the endpoint spoke, just not in the protocol.</para>
    ///
    /// <para><b>Public rather than internal</b> because the parsing rules are the protocol contract, not an
    /// implementation detail of the fetch: an assertion that only exercises it through a live request would be
    /// asserting on the network rather than on the rules, and could not cover the malformed shapes at all.</para>
    /// </summary>
    public static IReadOnlyList<string> Parse(byte[] payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];

            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;

                var name = (id.GetString() ?? "").Trim();
                if (name.Length == 0) continue;
                if (!seen.Add(name)) continue;

                models.Add(name);
                if (models.Count >= MaxModels) break;
            }

            return models;
        }
    }
}

/// <summary>One provider's last known model list, and when it was read.</summary>
public sealed class ModelListCacheEntry
{
    /// <summary>The provider this belongs to. The identity of a cache entry, so it is written by the caller.</summary>
    public string Id { get; set; } = "";

    public DateTimeOffset FetchedAt { get; set; }
    public List<string> Models { get; set; } = [];
}

/// <summary>
/// The cache document: <c>data-root/ai/models-cache.json</c>, one entry per provider id.
///
/// <para><b>Separate from <c>providers.json</c> on purpose.</b> That file is configuration — what the user
/// chose; this is an observation — what the endpoint said, and when. They change for different reasons and at
/// different rates, and the one thing this cache exists to answer is "which of these models did the endpoint
/// actually report?", which is exactly the question a configuration file cannot answer once the user starts
/// adding names by hand.</para>
/// </summary>
public sealed class ModelListCacheDocument
{
    public List<ModelListCacheEntry> Providers { get; set; } = [];
}

/// <summary>
/// Reads and writes <c>data-root/ai/models-cache.json</c>.
///
/// <para>Same contract as the other two AI stores: the file is optional (a missing one is an empty cache, not
/// an error), and a damaged one is treated as empty rather than thrown — losing a cache costs one refetch,
/// while refusing to start because of it would cost the user their whole settings page.</para>
/// </summary>
public sealed class ModelListStore(string root)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private string CachePath => Path.Combine(root, "ai", "models-cache.json");

    /// <summary>Every cached list, in file order.</summary>
    public List<ModelListCacheEntry> Load()
    {
        if (!File.Exists(CachePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<ModelListCacheDocument>(File.ReadAllText(CachePath), Json)
                ?.Providers ?? [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Replaces one provider's entry, or drops it when <paramref name="models"/> is <c>null</c>.
    ///
    /// <para>Writing is all-or-nothing per provider and happens only on a successful fetch, so a failed fetch
    /// physically cannot clear a good list (see <see cref="ModelList"/>'s rule 2). Passing <c>null</c> is how
    /// a removed provider's entry is forgotten.</para>
    /// </summary>
    public void Save(string providerId, IReadOnlyList<string>? models)
    {
        var document = new ModelListCacheDocument { Providers = Load() };
        document.Providers.RemoveAll(entry => entry.Id == providerId);

        if (models is not null)
        {
            document.Providers.Add(new ModelListCacheEntry
            {
                Id = providerId,
                FetchedAt = DateTimeOffset.Now,
                Models = [.. models],
            });
        }

        StateStore.WriteJson(CachePath, document);
    }
}