using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>
/// The ways a provider can be authenticated. These are the values that appear in a manifest entry's
/// <c>authMethods</c> array, and they are what the picker turns into buttons — so adding a method is a data
/// change on the manifest, not a new branch in the UI.
///
/// <para><b>Why a local endpoint is <see cref="None"/> rather than "no methods declared".</b> Ollama needs no
/// credential at all, and that is a real, intended state — not a missing field. Keeping it explicit means the
/// picker can say "no sign-in needed" deliberately, and an entry that forgot to declare its methods is still
/// distinguishable from one that genuinely needs nothing.</para>
/// </summary>
public static class ProviderAuthMethods
{
    /// <summary>Paste a secret. The universal fallback — every authenticated provider supports it.</summary>
    public const string ApiKey = "apiKey";

    /// <summary>Browser sign-in (OAuth 2.0 + PKCE), yielding a credential without the user copying anything.</summary>
    public const string OAuth = "oauth";

    /// <summary>No credential is required (a local endpoint such as Ollama).</summary>
    public const string None = "none";

    public static bool IsKnown(string method)
        => method is ApiKey or OAuth or None;

    /// <summary>Whether a declared method list says "this endpoint needs no credential". Only an explicit
    /// <see cref="None"/> alongside nothing that obtains a credential counts: an empty or unreadable list is
    /// treated as needing one, because the alternative is a provider that looks local when the manifest merely
    /// failed to say what it is.</summary>
    public static bool IsKeyless(IReadOnlyList<string> methods)
        => methods.Contains(None) && !methods.Any(method => method is ApiKey or OAuth);
}

/// <summary>
/// The OAuth configuration for a provider, declared in the manifest next to the endpoint it belongs to.
///
/// Only the <b>discovery</b> URL is declared, never the authorization/token endpoints themselves: OrcaRouter's
/// own documentation is explicit that clients must read them from <c>/.well-known/openid-configuration</c>
/// rather than hard-coding, because the document is what reflects the correct host for the deployment being
/// talked to (a self-hosted relay would otherwise silently send users to the wrong site). The document is
/// fetched at sign-in time, not at load time, so a manifest entry costs nothing until someone actually
/// signs in.
/// </summary>
public sealed class AiProviderOAuth
{
    /// <summary>RFC 8414 discovery document. Read the endpoints from here rather than hard-coding them.</summary>
    public string DiscoveryUrl { get; set; } = "";

    /// <summary>
    /// The scope to request. OrcaRouter grants <c>api</c> (a normal inference key) or <c>connector</c> (a key
    /// restricted to their Connect carrier, which cannot call the inference API and needs a workspace
    /// Admin/Owner to approve). We ask for <c>api</c> and <b>refuse a key whose granted scope is wider</b> —
    /// the docs call for that comparison in as many words.
    /// </summary>
    public string Scope { get; set; } = "api";

    /// <summary>Shown on the consent screen so the user knows who is asking. Also the name used to revoke.</summary>
    public string AppName { get; set; } = "Axmol Hub";

    /// <summary>
    /// Partner identity (an OAuth <c>client_id</c> in effect). Not a secret — it ships in the client binary —
    /// so it is only trusted together with a callback URL registered on the partner profile. When it resolves,
    /// the consent screen shows a Verified badge and the minted key's usage is attributed to the partner,
    /// which is what carries the referral arrangement. Empty when not an approved partner: the flow still
    /// works, it just shows an unverified consent screen.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// Referral code sent alongside the authorization request. Distinct from <see cref="AppId"/>: this credits
    /// <b>sign-ups</b> made during the flow, while the app id attributes <b>usage</b> of the minted key.
    /// </summary>
    public string? ReferralCode { get; set; }
}

/// <summary>
/// A declared way of asking a provider "is this key any good?" before it is stored.
///
/// <para>Only <see cref="HttpGetModels"/> exists today, and that is deliberate: a validation protocol is
/// something a provider either honours or does not, and inventing shapes for endpoints we have not seen
/// would give the manifest fields that look meaningful and are never exercised. A provider with no such
/// protocol simply omits <c>keyValidation</c> and its keys are stored unverified.</para>
/// </summary>
public sealed class AiProviderKeyValidation
{
    public const string HttpGetModels = "httpGetModels";

    /// <summary>Which probe to run. Unknown values are ignored, the same way an unknown auth method is dropped.</summary>
    public string Type { get; set; } = HttpGetModels;

    /// <summary>
    /// The path appended to <c>BaseUrl</c> for the probe. A relative path starting with <c>/</c>; an absolute
    /// URL is rejected, because a manifest that could point a probe at someone else's host would turn a
    /// validation feature into a request forgery primitive.
    /// </summary>
    public string Path { get; set; } = "/models";

    /// <summary>Whether this declaration describes a probe we actually know how to run.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsKnown => Type == HttpGetModels
        && Path.StartsWith('/')
        && !Path.StartsWith("//", StringComparison.Ordinal)
        && !Path.Contains("://", StringComparison.Ordinal);
}

/// <summary>Model-specific reasoning metadata and request-body fields declared by a built-in provider manifest.</summary>
public sealed class AiModelReasoning
{
    /// <summary>Effort values accepted by this model and surfaced by the composer.</summary>
    public List<string> Efforts { get; set; } = [];

    /// <summary>The provider's default effort, when its model-list metadata declares one.</summary>
    public string? DefaultEffort { get; set; }

    /// <summary>Provider-specific JSON fields required to enable reasoning for this model.</summary>
    public Dictionary<string, JsonElement> RequestOptions { get; set; } = [];
}

/// <summary>One provider entry in the built-in manifest (<c>manifests/ai-providers.json</c>, camelCase keys).</summary>
public sealed class AiProviderEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// One-line English blurb shown on the preset card in the "add provider" search dialog. It lives in the
    /// manifest rather than in <c>HubTexts</c> on purpose: the presets are data that change without a code
    /// release, and a description belongs next to the endpoint it describes — the same reason the whole entry
    /// is a JSON file. <see cref="DescriptionZh"/> carries the Chinese copy; see <see cref="Describe"/>.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>Chinese copy of <see cref="Description"/>. Optional: a missing translation falls back to English.</summary>
    public string? DescriptionZh { get; set; }

    public string BaseUrl { get; set; } = "";
    public int? MaxContextTokens { get; set; }
    /// <summary>Which source knows how to read this provider's model catalog
    /// (<see cref="ModelCapabilitySources.All"/>). Absent means the OpenAI-compatible default.</summary>
    public string? CapabilitySource { get; set; }
    /// <summary>Model IDs to enable by default only when they are present in the fetched catalog.</summary>
    public List<string> DefaultEnabledModels { get; set; } = [];
    /// <summary>Explicit capabilities for models whose reasoning support is known.</summary>
    public Dictionary<string, AiModelReasoning> ReasoningModels { get; set; } = [];
    public bool Affiliate { get; set; }
    public string? ReferralUrl { get; set; }

    /// <summary>
    /// How this provider can be authenticated, in the order the picker should offer them.
    ///
    /// An entry that omits the field falls back to <c>["apiKey"]</c> rather than to nothing: a provider that
    /// ships without the field is far more likely to be a key-based one than a keyless one, and defaulting to
    /// "no methods" would silently render an unusable card. <see cref="ProviderAuthMethods.None"/> must be
    /// written out explicitly.
    /// </summary>
    public List<string> AuthMethods { get; set; } = [];

    /// <summary>Present only for a provider that offers OAuth.</summary>
    public AiProviderOAuth? OAuth { get; set; }

    /// <summary>
    /// How (and whether) a pasted key can be checked before it is stored.
    ///
    /// <para><b>Opt-in, and a missing entry means "do not check".</b> The temptation is to assume every
    /// OpenAI-compatible endpoint answers <c>GET /models</c>, and for the big three that is true — but a
    /// self-hosted relay or a gateway that fronts a narrow route can legitimately close that path, and a
    /// provider whose key is perfectly good would then be reported as invalid. Failing <i>closed</i> on a
    /// missing declaration would make the manifest a gate rather than a catalogue, so the absence of this
    /// field is a deliberate "this endpoint declares no probe": the key is stored as typed.</para>
    ///
    /// <para>Only the shape is declared, never a host: the request goes to the provider's own
    /// <see cref="BaseUrl"/>, so a user pointing a preset at a different deployment probes that deployment
    /// instead of the one the manifest was written against.</para>
    /// </summary>
    public AiProviderKeyValidation? KeyValidation { get; set; }

    /// <summary>
    /// The effective auth methods: what the manifest declared, or <c>["apiKey"]</c> when it declared nothing,
    /// with unknown values dropped and duplicates removed.
    ///
    /// The unknown-value filter matters because these strings become buttons: a typo in the manifest would
    /// otherwise produce a button that does nothing, which is a worse failure than dropping it.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveAuthMethods
    {
        get
        {
            var declared = AuthMethods.Where(ProviderAuthMethods.IsKnown).Distinct().ToList();
            return declared.Count > 0 ? declared : [ProviderAuthMethods.ApiKey];
        }
    }

    /// <summary>Whether this provider offers browser sign-in (and therefore has usable OAuth configuration).</summary>
    [JsonIgnore]
    public bool SupportsOAuth
        => EffectiveAuthMethods.Contains(ProviderAuthMethods.OAuth)
           && OAuth is { DiscoveryUrl.Length: > 0 };

    /// <summary>
    /// The blurb for a given UI language. Presets are shipped copy, not user data, so this is the one place in
    /// the provider layer that is genuinely bilingual — and it follows the same rule as <c>HubTexts</c>: the
    /// language is chosen by the *current* UI language, with English as the fallback rather than the default.
    /// </summary>
    public string Describe(string language)
        => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && DescriptionZh is { Length: > 0 } zh
            ? zh
            : Description;

    /// <summary>
    /// Whether <paramref name="url"/> is a base URL a client can actually be pointed at.
    ///
    /// Lives on the entry rather than only in the dialog because it is a property of the *data*: the manifest and
    /// the user's own custom endpoints must pass the same test, and a preset that fails it should never ship. An
    /// absolute http/https check is the whole rule — a bare host ("localhost:11434/v1") reads as fine to a person
    /// but <c>new Uri(...)</c> refuses it, and the failure would only surface on the first message.
    /// </summary>
    public static bool IsUsableBaseUrl(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}


public sealed class AiProviderManifestDocument
{
    public List<AiProviderEntry> Providers { get; set; } = [];

    /// <summary>
    /// The id of the single preset a fresh install starts with. Exactly one provider is seeded (Copilot seeds
    /// one too): seeding the whole catalog would hand the user four half-configured endpoints they never
    /// asked for, and the settings page would have to explain why DeepSeek is broken on day one.
    /// </summary>
    public string? DefaultProvider { get; set; }
}

/// <summary>
/// The built-in AI provider list, shipped as <c>manifests/ai-providers.json</c>.
///
/// This is deliberately a **data file, not a C# constant table**, for three reasons that only make sense
/// together:
/// <list type="number">
/// <item>OrcaRouter's OSS partner review scans the public repository for <c>orcarouter</c> inside
/// configuration files (<c>.toml/.json/.yaml/…</c>) — a README or a C# source file does not count, so the
/// provider declaration must live in a real JSON file in the repo.</item>
/// <item>The repo already ships data as <c>manifests/*.json</c> (engine-manifest, recipe-manifest); a
/// provider list is the same shape of "facts that change without a code release".</item>
/// <item>Adding OpenAI / DeepSeek / Ollama later is then one more JSON entry — no enum, no factory change,
/// no new Hub release.</item>
/// </list>
///
/// The manifest is resolved against the app output directory, the same layout <see cref="PackagingRecipes"/>
/// uses: the three exe projects copy <c>manifests/*.json</c> next to themselves, and Core stays a plain
/// library with no data of its own.
/// </summary>
public static class AiProviderManifest
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifests/ai-providers.json");

    /// <summary>All built-in provider entries. Missing manifest is an empty list, not an error — no provider is required to exist.</summary>
    public static IReadOnlyList<AiProviderEntry> Load()
    {
        if (!File.Exists(ManifestPath)) return [];
        var document = JsonSerializer.Deserialize<AiProviderManifestDocument>(File.ReadAllText(ManifestPath), Json);
        return document?.Providers ?? [];
    }

    /// <summary>
    /// The id of the preset a fresh install seeds. This is what makes the catalog a catalog: a saved
    /// <c>providers.json</c> is authoritative, and a missing one is filled with exactly this entry.
    ///
    /// A missing or unrecognised <c>defaultProvider</c> falls back to the first declared entry rather than
    /// <c>null</c> — "no provider at all" would leave a fresh install with nothing to talk to and no card to
    /// click, which is a worse failure than seeding a different one than intended.
    /// </summary>
    public static string? DefaultProviderId()
    {
        if (!File.Exists(ManifestPath)) return null;
        var entries = Load();
        if (entries.Count == 0) return null;

        var document = JsonSerializer.Deserialize<AiProviderManifestDocument>(File.ReadAllText(ManifestPath), Json);
        var declared = document?.DefaultProvider;
        return entries.Any(entry => entry.Id == declared) ? declared : entries[0].Id;
    }

    /// <summary>Finds a built-in provider by id; <c>null</c> when it is not declared.</summary>
    public static AiProviderEntry? Find(string id) => Load().FirstOrDefault(provider => provider.Id == id);

    /// <summary>Builds a runtime <see cref="ModelProvider"/> from a built-in manifest entry; <c>null</c> when the id is not declared.</summary>
    public static ModelProvider? CreateBuiltIn(string id)
    {
        var entry = Find(id);
        return entry is null
            ? null
            : new ModelProvider
            {
                Id = entry.Id,
                Name = entry.Name,
                Description = entry.Description,
                DescriptionZh = entry.DescriptionZh,
                BaseUrl = entry.BaseUrl,
                DefaultEnabledModels = [.. entry.DefaultEnabledModels],
                ReasoningModels = entry.ReasoningModels.ToDictionary(
                    pair => pair.Key,
                    pair => new AiModelReasoning
                    {
                        Efforts = [.. pair.Value.Efforts],
                        DefaultEffort = pair.Value.DefaultEffort,
                        RequestOptions = pair.Value.RequestOptions.ToDictionary(
                            option => option.Key,
                            option => option.Value.Clone(),
                            StringComparer.Ordinal),
                    },
                    StringComparer.OrdinalIgnoreCase),
                // No model is seeded. The manifest deliberately does not name one: a name written into a
                // shipped JSON file goes stale the moment the provider adds or retires a model, and a stale
                // default is worse than none — it is offered in the list and fails on first use with a 404
                // that blames the user's key. The list comes from GET {baseUrl}/models after authenticating
                // (see ModelCatalog), and a keyless provider gets it with no secret at all.
                MaxContextTokens = entry.MaxContextTokens,
                CapabilitySource = entry.CapabilitySource,
                Affiliate = entry.Affiliate,
                ReferralUrl = entry.ReferralUrl,
                AuthMethods = [.. entry.EffectiveAuthMethods],
                // Copied field by field rather than by reference: the manifest document is re-read on every
                // call, but a provider that outlives the call must not hold a reference into it.
                OAuth = entry.OAuth is { } oauth
                    ? new AiProviderOAuth
                    {
                        DiscoveryUrl = oauth.DiscoveryUrl,
                        Scope = oauth.Scope,
                        AppName = oauth.AppName,
                        AppId = oauth.AppId,
                        ReferralCode = oauth.ReferralCode,
                    }
                    : null,
                // Copied for the same reason as OAuth above, and filtered through IsKnown so a hand-edited
                // manifest cannot smuggle in an absolute URL and turn the probe into a request to another host.
                KeyValidation = entry.KeyValidation is { IsKnown: true } validation
                    ? new AiProviderKeyValidation { Type = validation.Type, Path = validation.Path }
                    : null,
            };
    }
}
