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

    /// <summary>
    /// How this provider can be authenticated (see <see cref="ProviderAuthMethods"/>), in the order the picker
    /// offers them. A built-in provider takes the list from its manifest entry on every load; a custom one keeps
    /// whatever it was saved with. Defaulted to key-only so a provider deserialized from an older file still behaves.
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

    /// <summary>Per-model capabilities derived from the built-in manifest and refreshed on every load.</summary>
    [JsonIgnore]
    public Dictionary<string, AiModelReasoning> ReasoningModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each model said about itself: window, output cap, input modalities. <b>Derived, not persisted</b>,
    /// for the same reason as <see cref="ReasoningModels"/> — this is an observation of what the endpoint
    /// reported, and the observation channel (<c>data/ai/models-cache.json</c>) is what re-derives it. A
    /// persisted guess at someone's context window is how an install keeps believing a 4k model holds 128k
    /// tokens long after the gateway stopped saying so.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, ModelCapabilities> ModelCapabilities { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The id of the <see cref="IModelCapabilitySource"/> that knows how to ask this provider for its
    /// catalog. A manifest fact, refreshed every load, so a preset can gain a protocol without a reinstall; a
    /// custom provider leaves it null and gets the OpenAI-compatible default.</summary>
    [JsonIgnore]
    public string? CapabilitySource { get; set; }

    /// <summary>
    /// Headers this provider needs on every outbound request besides the credential, declared by the manifest —
    /// see <see cref="AiProviderEntry.ExtraHeaders"/> for why they are declared at all. Empty for every preset
    /// that predates the field and for a custom endpoint, which has no manifest entry to ask.
    ///
    /// <para><b>Refreshed each load, never persisted</b>, like <see cref="CapabilitySource"/>: these are a fact
    /// about the service, not a choice the user made, and a copy saved into <c>providers.json</c> would outlive
    /// the manifest correction that fixes it.</para>
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which provider family's per-request header semantics this provider speaks — see
    /// <see cref="AiProviderEntry.RequestSemantics"/> for what that means and why it is not a header. Another
    /// manifest fact, so the same rule as <see cref="ExtraHeaders"/>: refreshed every load, never persisted.
    /// </summary>
    [JsonIgnore]
    public string? RequestSemantics { get; set; }

    /// <summary>
    /// The tools this provider's own service can run when a request offers them
    /// (<see cref="ProviderServerTools"/>). <b>Persisted</b>, and seeded from the manifest only when the saved
    /// entry says nothing — the one manifest-shaped field a person may want to overrule.
    ///
    /// <para><see cref="ExtraHeaders"/> and <see cref="RequestSemantics"/> above are <c>[JsonIgnore]</c> because
    /// they are facts about the service, and a stale copy in a user file would contradict a manifest correction.
    /// This is not that kind of fact: whether a gateway honours a hosted search, ignores the field, or answers 400
    /// to it is a property of <i>the deployment this person is standing on</i>, which nobody but them can test. So
    /// the file may say it. Nullable rather than an empty list by default because the two cases must stay apart
    /// after a reload — "written empty: do not offer it" versus "never written: take what the preset says".</para>
    /// </summary>
    public List<string>? ServerTools { get; set; }

    /// <summary>
    /// The two models Hub may switch between when a session is routed automatically, both of them named by the
    /// user. unset (or only half set) means auto routing changes the reasoning tier and never the model: which
    /// of a provider's tiers is "the cheap one" is a decision about someone's bill and their taste, and Hub
    /// guessing it is exactly the kind of hard-coded model-name knowledge this project deleted once already.
    /// </summary>
    public AutoRoutingModels? AutoRouting { get; set; }

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
    /// complete one. The predicate is <see cref="AiProviderOAuth.IsUsableFlow"/>, shared with
    /// <see cref="AiProviderEntry.SupportsOAuth"/>: this class used to require a discovery URL of its own, which
    /// is a rule that happens to be wrong for a device-code provider — GitHub publishes no discovery document for
    /// the flow, so the preset would have been declared, authenticated nothing, and not even rendered a button.
    /// </summary>
    [JsonIgnore]
    public bool SupportsOAuth
        => EffectiveAuthMethods.Contains(ProviderAuthMethods.OAuth)
           && OAuth is { IsUsableFlow: true };

    /// <summary>Whether this provider has a pasted-key entrance (false for a keyless local endpoint such as Ollama).</summary>
    [JsonIgnore]
    public bool SupportsApiKeyMethod => EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey);

    /// <summary>
    /// Whether a credential is needed before this provider's models can be used — answered from the declared
    /// auth methods, never from a separate flag the manifest would have to keep in sync with them.
    ///
    /// <para><b>A custom endpoint is never gated.</b> Its <c>["apiKey"]</c> says only that a key <i>can</i> be
    /// pasted, not that the server demands one, and there is no manifest entry to ask. Refusing to build a
    /// client until one appears would lock out exactly the local servers (Ollama, llama.cpp, vLLM) that people
    /// add a custom provider for, so an unknown requirement is treated as "not required".</para>
    ///
    /// <para>Any credential satisfies it: a browser sign-in mints the same
    /// <see cref="ProviderCredential"/> kind as a pasted key, so "logged in" and "has a key" are one state here.</para>
    /// </summary>
    [JsonIgnore]
    public bool RequiresCredential
        => !IsCustom && !ProviderAuthMethods.IsKeyless(EffectiveAuthMethods);

    /// <summary>Whether the UI should offer a way to authenticate this provider at all.</summary>
    [JsonIgnore]
    public bool CanAuthenticate => SupportsApiKeyMethod || SupportsOAuth;

    /// <summary>
    /// The credential resolved for this provider in the current session, or <c>null</c> when it has none.
    ///
    /// <para><b>One provider, one credential.</b> A provider id names a single
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
    /// Whether this provider can be called right now: it either needs no credential, or it holds one whose secret
    /// resolved. <see cref="ChatClientFactory"/> in the Agent project refuses to build a client for anything else,
    /// and this is the same rule stated once so the rest of the app can ask it instead of inventing its own.
    ///
    /// <para>The distinction between <see cref="Credential"/> and this is the whole point. A credential is a
    /// <i>record</i>; the secret lives in the OS store and is rehydrated at load — and it can be absent: a submit
    /// with the key field left blank, or a stored entry that no longer decrypts. Judging readiness by the record
    /// alone produced a provider that settings called "Authenticated", that the composer was happy to offer, and that then
    /// failed every send with "not authenticated yet" — three screens disagreeing about one state.</para>
    ///
    /// <para>A keyless endpoint is ready without a credential, and stays ready: that is what makes a local Ollama
    /// reachable on a machine where nothing was ever pasted.</para>
    /// </summary>
    [JsonIgnore]
    public bool IsCallReady => !RequiresCredential || Credential?.HasSecret == true;

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

    /// <summary>
    /// The option that turns the end-of-stream token report on or off. Written by a person who knows their
    /// gateway rejects the field, and written by Hub itself when a refusal names it — which is why the rule is
    /// "off only when it says false": an absent key means nobody has complained yet, and asking a gateway that
    /// answers anyway costs nothing.
    /// </summary>
    public const string StreamUsageOption = "include_stream_usage";

    /// <summary>Whether to ask this endpoint for a usage chunk at the end of each stream.</summary>
    [JsonIgnore]
    public bool WantsStreamUsage => ExtraOptions.TryGetValue(StreamUsageOption, out var value)
                                    && string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
        ? false
        : true;

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
