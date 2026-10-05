using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>
/// A user-facing model provider, assembled from a built-in manifest entry (see
/// <see cref="AiProviderManifest"/>) plus any user overrides persisted in <c>data-root/ai/providers.json</c>.
///
/// The provider layer has exactly one wire protocol — OpenAI-compatible — so every provider, built-in or
/// custom, drives the same <c>IChatClient</c> factory (see <c>ChatClientFactory</c> in the Agent project).
/// The fields below differ only in their defaults, not in their meaning. This is the reason "add another
/// provider" is a data change (one more manifest entry), never a protocol change.
/// </summary>
public sealed class ModelProvider
{
    /// <summary>Stable id. Built-in providers use the manifest id (e.g. <c>orcarouter</c>); custom ones use <c>custom-&lt;guid&gt;</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name shown in the provider picker.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Preset blurb (English; the Chinese copy is picked by <see cref="Describe"/>). Set only when the provider
    /// came from a manifest preset and shown on the "add provider" search card. A custom provider leaves it
    /// empty — the user typed the endpoint themselves, so a catalog blurb would be describing nothing.
    ///
    /// It is **not** persisted: it is derived from the manifest on every load, exactly like the base URL, so
    /// editing <c>ai-providers.json</c> updates the card text without touching anyone's saved provider file.
    /// </summary>
    [JsonIgnore]
    public string Description { get; set; } = "";

    /// <summary>Chinese preset blurb; <c>null</c> falls back to <see cref="Description"/>.</summary>
    [JsonIgnore]
    public string? DescriptionZh { get; set; }

    /// <summary>The preset blurb for the current UI language (same rule as <see cref="AiProviderEntry.Describe"/>).</summary>
    public string Describe(string language)
        => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && DescriptionZh is { Length: > 0 } zh
            ? zh
            : Description;

    /// <summary>Whether this provider is a user-defined custom endpoint rather than a built-in manifest entry.</summary>
    public bool IsCustom { get; set; }

    /// <summary>OpenAI-compatible base URL. Built-in providers have a default; a custom provider requires the user to fill it.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Whether an API key is mandatory. Built-in providers take this from the manifest; custom providers default to false (a local endpoint needs no key).</summary>
    public bool ApiKeyRequired { get; set; }

    /// <summary>
    /// How this provider can be authenticated (see <see cref="ProviderAuthMethods"/>), in the order the picker
    /// offers them. A custom provider always supports a pasted key; a built-in one takes the list from its
    /// manifest entry. Defaulted to key-only so a provider deserialized from an older file still behaves.
    /// </summary>
    public List<string> AuthMethods { get; set; } = [ProviderAuthMethods.ApiKey];

    /// <summary>OAuth configuration for a provider that offers browser sign-in; <c>null</c> otherwise.</summary>
    public AiProviderOAuth? OAuth { get; set; }

    /// <summary>
    /// The declared key probe for this provider, or <c>null</c> when it declares none.
    ///
    /// <b>Not persisted</b>, for the same reason as <see cref="BaseUrl"/> and <see cref="OAuth"/>: it is a
    /// fact about the endpoint published in the manifest, so editing <c>ai-providers.json</c> must take effect
    /// without anyone re-saving their provider file. A custom provider has no manifest entry, so it never
    /// probes — its keys are stored as typed.
    /// </summary>
    [JsonIgnore]
    public AiProviderKeyValidation? KeyValidation { get; set; }

    /// <summary>
    /// Models from the built-in manifest that should be enabled when first discovered in the live catalog.
    /// It is derived from the manifest and is not persisted; later refreshes preserve the user's saved choice.
    /// </summary>
    [JsonIgnore]
    public List<string> DefaultEnabledModels { get; set; } = [];

    /// <summary>
    /// The auth methods this provider actually offers: the declared list with unknown values dropped, or
    /// <c>["apiKey"]</c> when nothing usable was declared.
    ///
    /// Mirrors <see cref="AiProviderEntry.EffectiveAuthMethods"/> because the UI holds a
    /// <see cref="ModelProvider"/>, never the manifest entry — so the filtering has to be available here too,
    /// or the picker would render a button for a method nobody implements.
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

    /// <summary>
    /// Whether this provider offers browser sign-in, and whether it carries enough OAuth configuration to
    /// complete one. A declared <c>oauth</c> method with no discovery URL is treated as unsupported rather
    /// than offered and then failing halfway through.
    /// </summary>
    [JsonIgnore]
    public bool SupportsOAuth
        => EffectiveAuthMethods.Contains(ProviderAuthMethods.OAuth)
           && OAuth is { DiscoveryUrl.Length: > 0 };

    /// <summary>Whether this provider needs a credential at all (false for a local endpoint such as Ollama).</summary>
    [JsonIgnore]
    public bool SupportsApiKeyMethod => EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey);

    /// <summary>
    /// The credential resolved for this provider in the current session, or <c>null</c> when it has none.
    ///
    /// <para><b>One provider, one credential.</b> This is the opencode shape: a provider id names a single
    /// account, and having a second account for the same service means adding a second provider that points at
    /// the same base URL. The earlier one-to-many design needed an "active" pointer on top of the list, and that
    /// pointer was the part that made the model hard to reason about — there was a state ("has credentials but
    /// none selected") that had no representation in the UI and therefore had to be explained away.</para>
    ///
    /// <para>Deliberately <b>not persisted</b>: the list belongs to <see cref="CredentialStore"/> and this is
    /// the single winner the client factory needs. Keeping it a plain field (rather than making the factory
    /// take a credential) is what lets <c>ChatClientFactory</c> stay unaware of credentials at all.</para>
    /// </summary>
    [JsonIgnore]
    public ProviderCredential? Credential { get; set; }

    /// <summary>
    /// The secret of this provider's credential. Replaces the old per-provider key field: it is a projection of
    /// <see cref="Credential"/>, nothing more, so the "never serialize the key" rule still holds by
    /// construction.
    /// </summary>
    [JsonIgnore]
    public string? ApiKey => Credential?.Secret;

    /// <summary>
    /// The provider's default model name, projected from the model that is marked in use rather than stored
    /// separately. A chat conversation may choose another configured model without changing this default.
    ///
    /// <para>Settable for two reasons: JSON deserialization of an older file that wrote a single
    /// <c>Model</c> property (the setter folds it into the list — see <see cref="Normalize"/>, which the
    /// store calls after loading), and the assistant page's existing "which model answers" readout.</para>
    /// </summary>
    public string Model
    {
        get => ActiveModel?.Name ?? "";
        set
        {
            // Guarded against the deserialization ordering problem: an older file carries both a single
            // "Model" property and (after the first save) a "Models" array. The serializer may run this setter
            // after Models is already populated, so it only fills the gap rather than inserting a duplicate.
            if (string.IsNullOrWhiteSpace(value)) return;
            if (Models.Any(model => string.Equals(model.Name, value.Trim(), StringComparison.OrdinalIgnoreCase))) return;

            Models.Insert(0, new ProviderModel { Name = value.Trim(), InUse = true });
            foreach (var other in Models.Skip(1)) other.InUse = false;
        }
    }

    /// <summary>
    /// The models this provider can call, in the order the user added them. Empty is legitimate for a provider
    /// that has just been added and not yet had a model picked.
    ///
    /// A provider owning several model names is the normal case rather than an exotic one: one DeepSeek key
    /// reaches both <c>deepseek-chat</c> and <c>deepseek-reasoner</c>, and switching between them is a choice
    /// about the question being asked, not a reconfiguration of the endpoint.
    /// </summary>
    public List<ProviderModel> Models { get; set; } = [];

    /// <summary>The provider's default model, marked in use, or the first enabled model when the mark is missing.
    /// A conversation may override it. <c>null</c> when no models are enabled.</summary>
    [JsonIgnore]
    public ProviderModel? ActiveModel
    {
        get
        {
            var marked = Models.FirstOrDefault(model => model.Enabled && model.InUse);
            if (marked is not null) return marked;
            return Models.FirstOrDefault(model => model.Enabled);
        }
    }

    /// <summary>
    /// Brings a deserialized provider into a consistent state. Called by the store after loading, because
    /// <see cref="Models"/> is populated by JSON binding while the "one enabled default, or none" rule is not
    /// something the serializer can enforce.
    /// </summary>
    public void Normalize()
    {
        Models.RemoveAll(model => model.Name.Trim().Length == 0);
        foreach (var model in Models) model.Name = model.Name.Trim();

        // De-duplicate case-insensitively, keeping the first occurrence: the same model listed twice would
        // otherwise produce two rows that behave identically.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Models.RemoveAll(model => !seen.Add(model.Name));

        var inUse = Models.Where(model => model.Enabled && model.InUse).ToList();
        foreach (var model in Models)
        {
            model.InUse = inUse.Count > 0 && ReferenceEquals(model, inUse[0]);
        }

        if (inUse.Count == 0 && Models.FirstOrDefault(model => model.Enabled) is { } firstEnabled)
        {
            firstEnabled.InUse = true;
        }
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Context budget in tokens; <c>null</c> means "use the manifest default".</summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>Provider-specific extension options (e.g. <c>reasoning_effort</c>), passed through without interpretation.</summary>
    public Dictionary<string, string?> ExtraOptions { get; set; } = new();

    /// <summary>Whether this provider participates in an affiliate program; the UI must disclose it when true.</summary>
    public bool Affiliate { get; set; }

    /// <summary>The referral URL shown together with the affiliate disclosure.</summary>
    public string? ReferralUrl { get; set; }

    /// <summary>
    /// The provider picker binds the object directly, so what it shows is this. Returning the display name
    /// (falling back to the id) keeps the ComboBox free of a display-member binding and means a rename in the
    /// manifest shows up without touching the view.
    /// </summary>
    public override string ToString() => Name.Length > 0 ? Name : Id;
}
