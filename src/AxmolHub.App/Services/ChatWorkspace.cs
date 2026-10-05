using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxmolHub.Agent;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub.App;

/// <summary>
/// The chat half of the shell's non-visual state — the counterpart of <see cref="HubWorkspace"/> for the AI
/// chat view. It owns the provider list, the conversation list, and the streaming pipeline, and it is the only
/// place that touches the OS credential store, so the panel above it stays a pure view.
///
/// It lives in the App rather than Core on purpose: it needs the Agent layer (the OpenAI-compatible client
/// factory and the platform secret store), and Core must stay dependency-free and offline-buildable.
///
/// **An <see cref="IChatClient"/> factory is injectable** (<see cref="ClientOverride"/>) so the shell
/// self-check can drive a scripted client and assert the whole chat view — streaming, conversation switching,
/// affiliate disclosure — with no network and no API key. This is the same seam the S2 checks use; here it
/// reaches the UI.
/// </summary>
public sealed class ChatWorkspace : IDisposable
{
    private readonly ProviderStore _providers;
    private readonly CredentialStore _credentials;
    private readonly ConversationStore _conversations;
    private readonly ModelListStore _modelLists;
    private readonly ISecretStore? _secrets;

    private readonly List<ModelProvider> _providerList = [];
    private readonly List<ProviderCredential> _credentialList = [];
    private Conversation? _active;
    private string? _selectedProviderId;
    private string? _selectedModelName;

    /// <summary>Set by the verification harness to answer with a scripted stream instead of a real endpoint.</summary>
    internal Func<ModelProvider, IChatClient>? ClientOverride { get; set; }

    /// <summary>Raised when the provider or conversation lists change and the panel should repaint.</summary>
    public event Action? Changed;

    public ChatWorkspace(string dataRoot)
    {
        _conversations = new ConversationStore(dataRoot);

        // The secret store is platform-specific and, on macOS/Linux, deliberately unimplemented rather than
        // falling back to plaintext. A failure here must not take the whole app down — chat simply cannot
        // store keys on those platforms yet, and the panel reports that when the user tries to configure one.
        // Windows (the shipping target) always succeeds.
        try { _secrets = SecretStoreFactory.Create(dataRoot); }
        catch (PlatformNotSupportedException) { _secrets = null; }

        _providers = new ProviderStore(dataRoot);
        _credentials = new CredentialStore(dataRoot, _secrets ?? new NoSecretStore());
        _modelLists = new ModelListStore(dataRoot);
        LoadProviders();
    }

    /// <summary>Whether API keys can be persisted at all on this platform (Windows today).</summary>
    public bool CanStoreSecrets => _secrets is not null;

    public IReadOnlyList<ModelProvider> Providers => _providerList;

    /// <summary>The provider/model choices available in chat: enabled providers with credentials (or no
    /// required key) and at least one enabled, configured model.</summary>
    public IReadOnlyList<ChatModelOption> AvailableChatModels
        => _providerList
            .Where(provider => provider.Enabled && (!provider.ApiKeyRequired || provider.Credential is not null))
            .SelectMany(provider => provider.Models
                .Where(model => model.Enabled)
                .OrderByDescending(model => model.InUse)
                .Select(model => new ChatModelOption(provider, model.Name)))
            .ToList();

    /// <summary>The choice attached to the active conversation, or the remembered/default choice for a new one.</summary>
    public ChatModelOption? SelectedChatModel
    {
        get
        {
            if (_active is not null)
            {
                var modelName = _active.ModelName;
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    modelName = _providerList.FirstOrDefault(provider => provider.Id == _active.ProviderId)
                        ?.ActiveModel?.Name;
                }

                return FindChatModel(_active.ProviderId, modelName)
                       ?? AvailableChatModels.FirstOrDefault(choice => choice.Provider.Id == _active.ProviderId);
            }

            return FindChatModel(_selectedProviderId, _selectedModelName) ?? AvailableChatModels.FirstOrDefault();
        }
    }

    public ModelProvider? ActiveProvider => SelectedChatModel?.Provider;

    public Conversation? ActiveConversation => _active;

    public IReadOnlyList<ConversationSummary> Conversations => _conversations.List();

    /// <summary>Selects a usable provider/model for the current conversation or the next new conversation.</summary>
    public bool SelectChatModel(string providerId, string modelName)
    {
        var choice = FindChatModel(providerId, modelName);
        if (choice is null) return false;

        _selectedProviderId = choice.Provider.Id;
        _selectedModelName = choice.ModelName;
        if (_active is not null)
        {
            _active.ProviderId = choice.Provider.Id;
            _active.ModelName = choice.ModelName;
            _conversations.Save(_active);
        }

        Changed?.Invoke();
        return true;
    }

    private ChatModelOption? FindChatModel(string? providerId, string? modelName)
        => string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelName)
            ? null
            : AvailableChatModels.FirstOrDefault(choice =>
                choice.Provider.Id == providerId
                && string.Equals(choice.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads the user's saved providers.
    ///
    /// The seeded list is what makes a fresh install usable: **exactly one** preset is adopted
    /// (<see cref="AiProviderManifest.DefaultProviderId"/>, currently OrcaRouter) — with no key, so the first
    /// call fails with a clear message rather than silently doing nothing. Adopting the whole catalog instead
    /// was tried and rejected: the settings page would open on four endpoints, three of them unusable, and the
    /// user never asked for any of them. The remaining presets reach the user through the "add provider"
    /// dialog, which is where choosing a provider actually belongs.
    /// </summary>
    public void LoadProviders()
    {
        _providerList.Clear();
        _providerList.AddRange(_providers.Load());

        // A saved provider records only its id; the built-in defaults (base URL, model, affiliate flag,
        // description, auth methods) come from the manifest. Re-deriving them here means editing
        // ai-providers.json updates existing installs instead of only new ones.
        foreach (var provider in _providerList.Where(provider => !provider.IsCustom))
        {
            if (AiProviderManifest.CreateBuiltIn(provider.Id) is { } builtIn)
            {
                provider.Name = provider.Name.Length == 0 ? builtIn.Name : provider.Name;
                provider.BaseUrl = provider.BaseUrl.Length == 0 ? builtIn.BaseUrl : provider.BaseUrl;
                provider.Model = provider.Model.Length == 0 ? builtIn.Model : provider.Model;
                provider.Affiliate = builtIn.Affiliate;
                provider.ReferralUrl ??= builtIn.ReferralUrl;
                // The blurb and auth methods are never persisted, so they can simply be refreshed from the
                // manifest each load. Auth methods in particular must follow the manifest: a provider that
                // gains OAuth support in a later release should offer it without a reinstall.
                provider.Description = builtIn.Description;
                provider.DescriptionZh = builtIn.DescriptionZh;
                provider.DefaultEnabledModels = [.. builtIn.DefaultEnabledModels];
                provider.AuthMethods = [.. builtIn.AuthMethods];
                provider.OAuth = builtIn.OAuth;
                // Same reasoning as the two lines above: the probe is a manifest fact, refreshed every load.
                provider.KeyValidation = builtIn.KeyValidation;
            }
        }

        // First run (nothing saved yet): adopt the single default preset so the conversation page has a
        // configured provider to name. A user who has deliberately removed it is not re-seeded — the file
        // exists, so this branch is skipped.
        if (_providerList.Count == 0 && AiProviderManifest.DefaultProviderId() is { } defaultId)
        {
            if (AiProviderManifest.CreateBuiltIn(defaultId) is { } provider) _providerList.Add(provider);
        }

        LoadCredentials();
        Changed?.Invoke();
    }

    /// <summary>
    /// Loads the credentials and binds the active one onto each provider.
    ///
    /// <para><b>The legacy path is handled here.</b> An install written before multi-account stored its key on
    /// the provider (<c>providers.json</c> → one secret per provider id). When no <c>credentials.json</c>
    /// exists yet but providers do carry a key, those are adopted as credentials — see
    /// <see cref="CredentialStore.MigrateFromProviders"/>, which reuses the provider id as the credential id so
    /// the secret already sitting in the OS credential store resolves without being moved.</para>
    /// </summary>
    private void LoadCredentials()
    {
        _credentialList.Clear();
        var loaded = _credentials.Load();
        var loadedCount = loaded.Count;
        _credentialList.AddRange(loaded);

        // Migration: no credential file, but the provider list has a legacy key stored under the provider id.
        // Writing the migrated list immediately is deliberate — it makes the upgrade a one-time event rather
        // than a condition re-evaluated on every launch.
        if (_credentialList.Count == 0)
        {
            var legacy = CredentialStore.MigrateFromProviders(_providerList);
            if (legacy.Count > 0)
            {
                _credentialList.AddRange(legacy);
                _credentials.Save(_credentialList);
            }
        }

        foreach (var provider in _providerList)
        {
            // Adopt the one credential this provider has. A list written by an install that predates the
            // one-to-one rule can still hold several for one provider, so extras are dropped rather than
            // silently left as orphans nobody can reach: the oldest survives, because that is the one a
            // re-auth has been rotating in place.
            var owned = _credentialList
                .Where(credential => credential.ProviderId == provider.Id)
                .OrderBy(credential => credential.CreatedAt)
                .ToList();

            foreach (var extra in owned.Skip(1))
            {
                _credentialList.Remove(extra);
                _credentials.DeleteSecret(extra.Id);
            }

            provider.Credential = owned.FirstOrDefault();
        }

        // Written back only when extras were actually dropped, so an ordinary launch touches nothing.
        if (_credentialList.Count != loadedCount) _credentials.Save(_credentialList);
    }

    /// <summary>
    /// The credential for a provider, or <c>null</c> when it has none.
    ///
    /// <para>Singular by design — see <see cref="ModelProvider.Credential"/> for why. A caller that wants to
    /// know how many exist (the self-check does, to prove the invariant) reads <see cref="Credentials"/>,
    /// which is the raw list.</para>
    /// </summary>
    public ProviderCredential? CredentialFor(string providerId)
        => _credentialList.FirstOrDefault(credential => credential.ProviderId == providerId);

    /// <summary>Every credential, for the self-check and the flat legacy list. Read-only.</summary>
    public IReadOnlyList<ProviderCredential> Credentials => _credentialList;

    /// <summary>
    /// Sets a provider's credential, returning it — or <c>null</c> when it was refused.
    ///
    /// <para><b>Replaces rather than appends.</b> A provider has exactly one credential (see
    /// <see cref="ModelProvider.Credential"/>), so authenticating again rotates the stored secret instead of
    /// stacking a second entry. This is also what makes the self-check's "one provider, one credential"
    /// invariant hold no matter how many times the user re-authenticates.</para>
    ///
    /// <para>A blank <paramref name="secret"/> keeps the existing one when there is one, which is what the
    /// dialog's "leave blank to keep the stored key" contract means; a blank secret on a provider that has
    /// none is a legitimate keyless add (an on-prem endpoint).</para>
    ///
    /// <para>On a platform with no secret store a credential that <b>has</b> a secret is refused rather than
    /// stored without one — a configured-looking entry that cannot authenticate is worse than an error.</para>
    /// </summary>
    public ProviderCredential? AddCredential(string providerId, string label, string? secret, string source)
    {
        if (_providerList.All(provider => provider.Id != providerId)) return null;
        if (!string.IsNullOrEmpty(secret) && !CanStoreSecrets) return null;

        var existing = CredentialFor(providerId);
        if (existing is not null)
        {
            if (!string.IsNullOrEmpty(secret)) existing.Secret = secret;
            existing.Source = source;
            if (label.Trim().Length > 0) existing.Label = label.Trim();
            SaveCredentials();
            Changed?.Invoke();
            return existing;
        }

        var credential = new ProviderCredential
        {
            Id = "cred-" + Guid.NewGuid().ToString("N")[..12],
            ProviderId = providerId,
            Label = label.Trim(),
            Source = source,
            Secret = string.IsNullOrEmpty(secret) ? null : secret,
        };

        if (credential.Label.Length == 0) credential.Label = DefaultCredentialLabel(providerId);

        _credentialList.Add(credential);
        SaveCredentials();
        if (_providerList.FirstOrDefault(provider => provider.Id == providerId) is { } provider)
        {
            provider.Credential = credential;
        }

        Changed?.Invoke();
        return credential;
    }

    /// <summary>
    /// Sets the credential produced by a sign-in. Separate from <see cref="AddCredential"/> because the OAuth
    /// result carries facts a pasted key does not — the account id and the granted scope — and dropping them
    /// would make "you are already connected as this account" impossible to tell apart from a fresh link.
    ///
    /// <para>Signing in again rotates the secret on the credential the provider already has. Under the
    /// one-to-one rule that is the only possible outcome, and it is also the one people want: re-authenticating
    /// refreshes access, it does not mean "now I have two accounts". A genuinely second account is a second
    /// provider.</para>
    /// </summary>
    public ProviderCredential? AddOAuthCredential(string providerId, string? accountId, string? scope, string secret)
    {
        if (!CanStoreSecrets) return null;
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return null;

        var existing = CredentialFor(providerId);
        if (existing is not null)
        {
            existing.Secret = secret;
            existing.Scope = scope;
            existing.Source = CredentialSources.OAuth;
            if (accountId is { Length: > 0 })
            {
                existing.AccountId = accountId;
                existing.Label = AccountLabel(providerId, accountId);
            }

            SaveCredentials();
            Changed?.Invoke();
            return existing;
        }

        var credential = new ProviderCredential
        {
            Id = "cred-" + Guid.NewGuid().ToString("N")[..12],
            ProviderId = providerId,
            Label = accountId is { Length: > 0 } ? AccountLabel(providerId, accountId) : DefaultCredentialLabel(providerId),
            Source = CredentialSources.OAuth,
            AccountId = accountId,
            Scope = scope,
            Secret = secret,
        };

        _credentialList.Add(credential);
        SaveCredentials();
        provider.Credential = credential;
        Changed?.Invoke();
        return credential;
    }

    /// <summary>A labelled fallback for an account the provider named; "OrcaRouter · 1234567" reads better than a bare id.</summary>
    private string AccountLabel(string providerId, string accountId)
    {
        var name = _providerList.FirstOrDefault(provider => provider.Id == providerId)?.Name ?? providerId;
        // The id can be long; the tail is the distinctive part of a numeric account id, so keep that.
        var shown = accountId.Length <= 12 ? accountId : "…" + accountId[^12..];
        return name + " · " + shown;
    }

    /// <summary>
    /// Label for a credential the provider did not name: the provider's own name.
    ///
    /// <para>No numbering, which is the visible consequence of the one-to-one rule. "OrcaRouter (2)" only ever
    /// made sense when one provider could own several accounts; with one account the suffix had nothing to
    /// disambiguate, and inventing a second one would have implied a rival that does not exist.</para>
    /// </summary>
    private string DefaultCredentialLabel(string providerId)
        => _providerList.FirstOrDefault(provider => provider.Id == providerId)?.Name ?? providerId;

    /// <summary>
    /// Removes a credential and its stored secret, leaving its provider unconfigured.
    ///
    /// <para>Clearing the pointer is the whole of the "take over" logic now: there is no second credential to
    /// promote, so a provider with no credential left is simply a provider that needs authenticating again —
    /// the same state it was in before the user connected it.</para>
    /// </summary>
    public bool RemoveCredential(string credentialId)
    {
        var credential = _credentialList.FirstOrDefault(candidate => candidate.Id == credentialId);
        if (credential is null) return false;

        _credentialList.Remove(credential);
        _credentials.DeleteSecret(credential.Id);

        if (_providerList.FirstOrDefault(provider => provider.Id == credential.ProviderId) is { } provider)
        {
            provider.Credential = null;
        }

        _credentials.Save(_credentialList);
        Changed?.Invoke();
        return true;
    }

    // ── Models ──
    //
    // A provider owns a *set* of model names with one marked in use, because one credential commonly reaches
    // several (the same key serves deepseek-chat and deepseek-reasoner). Switching between them is not a
    // reconfiguration, so it is a separate operation from editing the provider.

    /// <summary>Adds a model to a provider and returns it, or <c>null</c> when the provider is unknown or the
    /// name is blank/duplicate. The first model added becomes the one in use.</summary>
    public ProviderModel? AddModel(string providerId, string name)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return null;

        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        if (provider.Models.Any(model => string.Equals(model.Name, trimmed, StringComparison.OrdinalIgnoreCase))) return null;

        var model = new ProviderModel { Name = trimmed, InUse = provider.Models.Count == 0 };
        provider.Models.Add(model);
        provider.Normalize();
        SaveProviders();
        return model;
    }

    /// <summary>
    /// Removes a model. Removing the one in use hands the mark to the first remaining enabled model; if none
    /// remain enabled, the provider has no default until a model is enabled again.
    /// </summary>
    public bool RemoveModel(string providerId, string name)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        var model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (model is null) return false;

        provider.Models.Remove(model);
        provider.Normalize();
        SaveProviders();
        return true;
    }

    /// <summary>
    /// Removes every configured model from a provider in one save, returning how many were removed. The
    /// provider, its credential and the cached catalog are untouched — only the configured list is emptied,
    /// so re-adding from the catalog afterwards costs nothing.
    /// </summary>
    public int RemoveAllModels(string providerId)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null || provider.Models.Count == 0) return 0;

        var removed = provider.Models.Count;
        provider.Models.Clear();
        provider.Normalize();
        SaveProviders();
        return removed;
    }

    /// <summary>Marks a model as the one requests are sent with.</summary>
    public bool SetActiveModel(string providerId, string name)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        var target = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null || !target.Enabled) return false;

        foreach (var model in provider.Models) model.InUse = ReferenceEquals(model, target);
        SaveProviders();
        return true;
    }

    /// <summary>Controls whether a provider model is offered in chat.</summary>
    public bool SetModelEnabled(string providerId, string name, bool enabled)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        var model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (model is null) return false;

        if (model.Enabled == enabled) return true;

        model.Enabled = enabled;
        if (enabled && !provider.Models.Any(candidate => candidate.InUse && candidate.Enabled))
        {
            model.InUse = true;
        }
        else if (!enabled && model.InUse)
        {
            model.InUse = false;
            if (provider.Models.FirstOrDefault(candidate => candidate.Enabled) is { } next)
                next.InUse = true;
        }

        provider.Normalize();
        SaveProviders();
        return true;
    }

    /// <summary>Enables a model chosen from the provider's cached catalog, adding it if it is not configured yet.</summary>
    public bool EnableCatalogModel(string providerId, string name)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        var trimmed = name.Trim();
        if (provider is null || trimmed.Length == 0) return false;

        if (!CachedModels(providerId).Any(candidate =>
                string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var configured = provider.Models.FirstOrDefault(model =>
            string.Equals(model.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
        {
            return SetModelEnabled(providerId, configured.Name, true);
        }

        provider.Models.Add(new ProviderModel
        {
            Name = trimmed,
            Enabled = true,
            InUse = !provider.Models.Any(model => model.Enabled),
        });
        provider.Normalize();
        SaveProviders();
        return true;
    }

    /// <summary>
    /// The presets the user has not configured yet — the catalog the "add provider" dialog searches. Custom
    /// providers never appear here (they are not presets), and an already-configured preset is excluded so the
    /// dialog cannot add the same provider twice.
    /// </summary>
    public IReadOnlyList<ModelProvider> AvailablePresets()
        => AiProviderManifest.Load()
            .Where(entry => _providerList.All(provider => provider.Id != entry.Id))
            .Select(entry => AiProviderManifest.CreateBuiltIn(entry.Id))
            .OfType<ModelProvider>()
            .ToList();

    /// <summary>
    /// Adopts a built-in preset by id and returns it, or <c>null</c> when the id is not a declared preset or is
    /// already configured. This is the counterpart of <see cref="AddProvider"/> for the preset cards: the
    /// endpoint and model come from the manifest, so there is nothing for the user to type.
    /// </summary>
    public ModelProvider? AddPreset(string presetId)
    {
        if (_providerList.Any(provider => provider.Id == presetId)) return null;
        if (AiProviderManifest.CreateBuiltIn(presetId) is not { } provider) return null;

        _providerList.Add(provider);
        SaveProviders();
        return provider;
    }

    /// <summary>Persists the configured providers. No credential is written here (see <see cref="CredentialStore"/>).</summary>
    public void SaveProviders()
    {
        _providers.Save(_providerList);
        Changed?.Invoke();
    }

    /// <summary>Persists the credential list; secrets go to the OS credential store, never the JSON.</summary>
    public void SaveCredentials()
    {
        _credentials.Save(_credentialList);
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds a key credential for a provider — the "paste a key" path, which remains the universal one.
    /// Returns <c>null</c> on a platform without a secret store, rather than storing a keyless credential that
    /// would look configured and then fail.
    /// </summary>
    public ProviderCredential? SetApiKey(string providerId, string key)
        => string.IsNullOrWhiteSpace(key) ? null : AddCredential(providerId, "", key, CredentialSources.ApiKey);

    /// <summary>
    /// Runs the browser sign-in for a provider that offers it and stores the minted key as a credential.
    ///
    /// <para><b>The outcome is a record rather than an exception</b> because there are four distinct endings a
    /// caller has to render differently — success, the user closed the tab, the provider granted a wider scope
    /// than asked, and a transport failure — and folding them into one exception type would leave the UI
    /// string-matching on messages.</para>
    ///
    /// <para><b>The flow is built here, not injected, except for the HTTP handler and the browser opener.</b>
    /// Those two are the only things a self-check must not do for real, and they are exactly what the flow's
    /// constructor takes.</para>
    /// </summary>
    public async Task<OAuthSignInOutcome?> SignInWithOAuthAsync(
        string providerId,
        Action<string>? onManualUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanStoreSecrets) return null;

        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider?.OAuth is not { DiscoveryUrl.Length: > 0 } oauth) return null;

        var flow = new OrcaRouterOAuthFlow(
            _oauthHttp ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) },
            url =>
            {
                // Try the browser first; when that fails the URL is handed to the caller instead of being
                // swallowed, because a sign-in with no browser and no link is a dead end.
                if (!BrowserOpener(url)) onManualUrl?.Invoke(url);
            },
            OAuthTimeout);

        try
        {
            // No ConfigureAwait(false): the continuation calls AddOAuthCredential → SaveCredentials → Changed,
            // which ChatPanel.Reload consumes by mutating Avalonia controls. Keep it on the captured UI context
            // (same rule as RefreshModelsAsync), or the sign-in would touch the visual tree from a worker thread.
            var result = await flow.SignInAsync(oauth, cancellationToken);
            var credential = AddOAuthCredential(providerId, result.AccountId, result.Scope, result.Key);
            return credential is null
                ? new OAuthSignInOutcome(Error: "The credential could not be stored.")
                : new OAuthSignInOutcome(Credential: credential);
        }
        catch (OperationCanceledException)
        {
            return new OAuthSignInOutcome(Cancelled: true);
        }
        catch (TimeoutException)
        {
            return new OAuthSignInOutcome(Cancelled: true);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("scope", StringComparison.OrdinalIgnoreCase))
        {
            // The scope check is the one failure the user can actually act on (re-run the flow and grant less),
            // so it is distinguished from a generic transport error.
            return new OAuthSignInOutcome(ScopeRejected: GrantedScopeOf(exception.Message));
        }
        catch (Exception exception)
        {
            return new OAuthSignInOutcome(Error: exception.Message);
        }
    }

    /// <summary>
    /// The granted scope, pulled back out of the flow's refusal message. The flow refuses with a message that
    /// names both scopes; this extracts the granted one for the UI to show. Returning the raw message would be
    /// simpler but would put an English internal string in front of a Chinese user.
    /// </summary>
    private static string GrantedScopeOf(string message)
    {
        var start = message.IndexOf('\'') + 1;
        var end = message.IndexOf('\'', start);
        return start > 0 && end > start ? message[start..end] : "";
    }

    /// <summary>Opens a URL in the default browser. Windows and macOS have a launcher; elsewhere the caller
    /// falls back to showing the link. Injectable because launching a real browser from an assertion harness
    /// is both a side effect and a hang risk.</summary>
    internal Func<string, bool> BrowserOpener { get; set; } = TryOpenBrowser;

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Injectable transport for the sign-in flow; a self-check supplies one so no request leaves the box.</summary>
    internal HttpClient? OAuthHttp
    {
        get => _oauthHttp;
        set => _oauthHttp = value;
    }

    /// <summary>
    /// Asks the provider whether a pasted key is any good, when — and only when — its manifest entry declares
    /// a probe.
    ///
    /// <para><b>A provider that declares no probe is not a failed validation.</b> It returns
    /// <see cref="KeyCheckOutcome.Unsupported"/> and the caller stores the key as typed. This is the whole
    /// reason the field is opt-in: a gateway that closes <c>GET /models</c> would otherwise have its working
    /// keys reported as bad, and the user would be stuck with no way to authenticate at all.</para>
    ///
    /// <para>A transport failure is deliberately <b>not</b> a verdict either — it returns
    /// <see cref="KeyCheckOutcome.Unreachable"/>. Treating "could not ask" as "the key is bad" would let a
    /// laptop on hotel wifi refuse a key that works fine at home, and the error would be attributed to
    /// something the user cannot fix.</para>
    /// </summary>
    public async Task<KeyCheckOutcome> CheckApiKeyAsync(
        string providerId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider?.KeyValidation is not { IsKnown: true } validation) return KeyCheckOutcome.Unsupported;
        if (!AiProviderEntry.IsUsableBaseUrl(provider.BaseUrl)) return KeyCheckOutcome.Unreachable;
        if (!CanStoreSecrets) return KeyCheckOutcome.Unsupported;

        // The probe goes to the provider's own base URL, so a preset pointed at a different deployment
        // checks that deployment. The trailing slash matters: "https://host/v1" + "/models" is
        // "https://host/v1/models", but a base written as "https://host/v1/" would otherwise produce "//".
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var url = baseUrl + validation.Path;

        try
        {
            // Not disposed when injected: the caller owns it and may reuse it for several probes. Disposing
            // here would make the second key check fail with ObjectDisposedException, which this method
            // reports as Unreachable — "could not ask" — for a reason that has nothing to do with the network.
            using var owned = _oauthHttp is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await (_oauthHttp ?? owned!).SendAsync(request, cancellationToken).ConfigureAwait(false);

            // 401/403 is the answer, and it is an answer about the key: the endpoint understood the request
            // and refused the credential. A 404 or a 5xx means the probe itself is wrong, not the key, and
            // reporting those as "invalid key" would be the fail-closed behaviour this design avoids.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return KeyCheckOutcome.Rejected;
            }

            return KeyCheckOutcome.Accepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return KeyCheckOutcome.Unreachable;
        }
        catch (Exception)
        {
            return KeyCheckOutcome.Unreachable;
        }
    }

    /// <summary>
    /// Asks a provider which models it serves and, on a real answer, adopts that list.
    ///
    /// <para><b>Adopting replaces the models but never the choice.</b> The endpoint is authoritative about
    /// what exists, so a model it retired must disappear — a stale entry would be offered and then fail on
    /// first use. The model marked in use is preserved by name across the swap, so refreshing after a sign-in
    /// does not silently move the user onto a different model; when the model in use is gone from the new
    /// list, <see cref="ModelProvider.Normalize"/> hands the mark to what is left.</para>
    ///
    /// <para><b>A failed fetch changes nothing at all</b> — not the list, not the cache. A provider whose
    /// network blipped keeps the models it had, which is the difference between a list that is occasionally
    /// stale and a list that empties itself every time the wifi drops. The problem string comes back for the
    /// status line; it never reaches the user as an exception.</para>
    ///
    /// <para>Requires no credential for a keyless provider (Ollama), which is why this is callable before
    /// authentication as well as after it.</para>
    /// </summary>
    public async Task<ModelFetchResult> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return ModelFetchResult.Unreachable("No such provider.");

        // The injected client is *not* disposed. It is owned by whoever set ModelListHttp — the self-check reuses
        // one handler across a dozen fetches, and disposing it here would make every fetch after the first
        // fail with ObjectDisposedException, which surfaces as "the endpoint is unavailable" and reads like a
        // product bug. A client built here is ours, so only that one is disposed.
        using var owned = _modelListHttp is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
        var client = _modelListHttp ?? owned!;
        // No ConfigureAwait(false) here: the continuation below ends in SaveProviders() → Changed, which is
        // consumed by ChatPanel.Reload — code that mutates Avalonia controls. It must resume on the captured
        // UI context; resuming on the thread pool would touch the visual tree from a worker thread and hang
        // the whole UI (a CPU-bound cross-thread re-entrancy, not a network wait).
        var result = await ModelList.FetchAsync(client, provider, cancellationToken);
        if (!result.Reachable) return result;

        // Split against the *previous* fetch, not against nothing. "Not in this response" alone cannot tell a
        // retired model from a hand-written one — both are simply absent — and guessing wrong in either
        // direction is bad: dropping a name the user typed deletes work they did on purpose, and keeping one
        // the provider retired offers a name that will 404 on first use and blames their key.
        var reported = _modelLists.Load().FirstOrDefault(entry => entry.Id == providerId)?.Models ?? [];
        var kept = ManualModelsOf(provider, result.Models, reported).ToList();

        // An empty list is a real answer (rule 3 in ModelList): adopt it, so a provider that retired
        // everything shows nothing rather than showing what it used to serve.
        var previousModels = provider.Models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
        provider.Models = result.Models
            .Where(name => previousModels.ContainsKey(name)
                           || provider.DefaultEnabledModels.Any(defaultName =>
                               string.Equals(defaultName, name, StringComparison.OrdinalIgnoreCase)))
            .Select(name =>
            {
                var previous = previousModels.GetValueOrDefault(name);
                return new ProviderModel
                {
                    Name = name,
                    InUse = previous?.InUse == true,
                    Enabled = previous?.Enabled ?? true,
                };
            })
            .ToList();

        // Anything the user typed by hand that the endpoint has never reported is kept — the endpoint's
        // silence about a name is not evidence that the name is wrong, and a self-hosted gateway can
        // legitimately serve a model it declines to list.
        provider.Models.AddRange(kept);

        provider.Normalize();
        _modelLists.Save(providerId, result.Models);
        SaveProviders();
        return result;
    }

    /// <summary>
    /// The models to carry across a refresh: those the provider already had that <b>neither</b> the new
    /// response <b>nor</b> the previous fetch mentions.
    ///
    /// <para>The three-way test is the whole rule. In the new response → the endpoint serves it, adopt it.
    /// In the previous fetch but not the new one → the endpoint retired it, drop it. In neither → nobody
    /// but the user ever claimed this name exists, so it is theirs and it stays. Collapsing the last two
    /// cases is what makes a list that only ever grows, and it is invisible until someone tries to use a
    /// model the provider no longer has.</para>
    ///
    /// <para>Empty <paramref name="previouslyReported"/> means "no earlier fetch to compare against" — the
    /// first refresh, or a cache that was cleared — and every existing name is then kept, which is the
    /// conservative direction: nothing the user had disappears because the history was lost.</para>
    /// </summary>
    private static IEnumerable<ProviderModel> ManualModelsOf(
        ModelProvider provider,
        IReadOnlyList<string> fetched,
        IReadOnlyList<string> previouslyReported)
    {
        foreach (var model in provider.Models)
        {
            if (Contains(fetched, model.Name)) continue;
            if (previouslyReported.Count > 0 && Contains(previouslyReported, model.Name)) continue;
            yield return model;
        }

        static bool Contains(IReadOnlyList<string> names, string name)
            => names.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The model names last fetched for a provider, from the cache. Empty when it has never been fetched, so
    /// the caller can say "not fetched yet" rather than "fetched and found nothing".
    /// </summary>
    public IReadOnlyList<string> CachedModels(string providerId)
        => _modelLists.Load().FirstOrDefault(entry => entry.Id == providerId)?.Models ?? [];

    /// <summary>
    /// Injectable transport for the model-list fetch; a self-check supplies one so no request leaves the box.
    /// Separate from <see cref="OAuthHttp"/> because the two answer different questions and the check that
    /// drives one must not silently satisfy the other.
    /// </summary>
    internal HttpClient? ModelListHttp
    {
        get => _modelListHttp;
        set => _modelListHttp = value;
    }

    private HttpClient? _modelListHttp;

    /// <summary>
    /// How long the sign-in waits for the browser callback. Injectably short for a self-check: the real
    /// default is minutes, and an assertion harness that waited that long would look like a hang.
    /// </summary>
    internal TimeSpan OAuthTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private HttpClient? _oauthHttp;

    /// <summary>
    /// Validates a provider's editable fields. Shared by the dialog (live "can I save yet" feedback) and by
    /// <see cref="AddProvider"/>/<see cref="UpdateProvider"/> (the real gate) so the two cannot drift — the UI
    /// must not be the only thing standing between a half-filled provider and a saved file.
    /// </summary>
    /// <returns><c>null</c> when valid; otherwise the text key of the problem to show.</returns>
    public static string? Validate(ModelProvider provider)
    {
        if (provider.Name.Trim().Length == 0) return "ProviderNameRequired";
        if (!IsHttpUrl(provider.BaseUrl)) return "ProviderBaseUrlInvalid";
        if (provider.Model.Trim().Length == 0) return "ProviderModelRequired";
        return null;
    }
    /// <summary>
    /// Accepts only an absolute http/https URL. A bare host ("localhost:11434/v1") looks right to a person
    /// but <c>new Uri(...)</c> will not produce a usable endpoint from it, and the failure would only surface
    /// on the first message — so it is rejected here, with the field to fix.
    ///
    /// The rule itself lives on <see cref="AiProviderEntry.IsUsableBaseUrl"/> so the preset catalog is held to
    /// exactly the same standard as a hand-typed endpoint; this is the name the UI and dialogs already call.
    /// </summary>
    public static bool IsHttpUrl(string url) => AiProviderEntry.IsUsableBaseUrl(url);

    /// <summary>
    /// Adds a custom provider (a user-supplied OpenAI-compatible endpoint — the path to a local model) and
    /// returns it, or <c>null</c> when validation failed. The new provider becomes active: adding one is
    /// almost always followed by using it.
    /// </summary>
    public ModelProvider? AddProvider(string name, string baseUrl, string model, string? apiKey)
    {
        var provider = new ModelProvider
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..12],
            Name = name.Trim(),
            IsCustom = true,
            BaseUrl = baseUrl.Trim(),
            Models = [new ProviderModel { Name = model.Trim(), InUse = true }],
            // A custom endpoint defaults to keyless (a local server usually needs none); the user supplies one
            // when their endpoint wants it. It is never offered browser sign-in — there is no server to sign in
            // to, so declaring only the key method keeps the picker from inventing a button that cannot work.
            ApiKeyRequired = false,
            AuthMethods = [ProviderAuthMethods.ApiKey],
        };

        if (Validate(provider) is not null) return null;

        _providerList.Add(provider);
        SaveProviders();

        // The key becomes a credential, exactly as it would for a built-in provider. A custom endpoint that
        // wants no key simply gets none — that is the keyless case, not an error.
        if (!string.IsNullOrEmpty(apiKey))
        {
            var credential = AddCredential(provider.Id, "Default", apiKey, CredentialSources.ApiKey);
            // A platform without a secret store cannot persist a key; roll the provider back rather than
            // leaving a half-added entry behind.
            if (credential is null)
            {
                _providerList.Remove(provider);
                SaveProviders();
                return null;
            }
        }

        return provider;
    }

    /// <summary>
    /// Updates an existing provider in place. Built-in providers may have their model changed, but their base
    /// URL and name stay the manifest's (editing them would silently diverge from what the review scanned for;
    /// the user who wants a different endpoint adds a custom provider instead).
    ///
    /// <para>The <paramref name="apiKey"/> argument is kept so existing callers compile, but it is applied as
    /// <b>a new credential</b>, not as a field on the provider — which is now the same thing
    /// <see cref="SetApiKey"/> does. Passing one rotates the active credential's secret rather than stacking a
    /// second entry, matching the dialog's "leave blank to keep the stored key" contract.</para>
    /// </summary>
    public bool UpdateProvider(string providerId, string name, string baseUrl, string model, string? apiKey)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        var edited = new ModelProvider
        {
            Id = provider.Id,
            Name = provider.IsCustom ? name.Trim() : provider.Name,
            IsCustom = provider.IsCustom,
            BaseUrl = provider.IsCustom ? baseUrl.Trim() : provider.BaseUrl,
            // Built through the list rather than the Model setter: the validation below reads Model either way,
            // and this keeps the temporary object from relying on a setter with an insertion side effect.
            Models = [new ProviderModel { Name = model.Trim(), InUse = true }],
        };
        if (Validate(edited) is not null) return false;

        provider.Name = edited.Name;
        provider.BaseUrl = edited.BaseUrl;

        // Rename the model in use rather than assigning to Model. The setter inserts, which is right when a
        // provider is being built and wrong here — editing a provider would otherwise grow the model list by
        // one on every save.
        var renamed = edited.Model.Trim();
        if (provider.ActiveModel is { } current)
        {
            // A rename onto a name that already exists would create a duplicate row; the existing entry wins
            // and the renamed one is dropped.
            if (provider.Models.Any(model => !ReferenceEquals(model, current)
                    && string.Equals(model.Name, renamed, StringComparison.OrdinalIgnoreCase)))
            {
                provider.Models.Remove(current);
            }
            else
            {
                current.Name = renamed;
            }
        }
        else if (renamed.Length > 0)
        {
            provider.Models.Add(new ProviderModel { Name = renamed, InUse = true });
        }

        provider.Normalize();

        if (!string.IsNullOrEmpty(apiKey))
        {
            // An edit that replaces the key must not leave the old secret in the store. Under the one-to-one
            // rule this is the same "rotate" that AddCredential performs, so it is expressed as one call rather
            // than a second code path that could drift from it.
            if (!CanStoreSecrets) return false;
            if (AddCredential(providerId, "", apiKey, CredentialSources.ApiKey) is null)
            {
                return false;
            }
        }

        SaveProviders();
        return true;
    }

    /// <summary>
    /// Removes a provider <b>and everything that hangs off it</b>: the record, its accounts and their secrets.
    ///
    /// <para>The id it refuses is the manifest's <i>default</i> provider, not every built-in one. A built-in
    /// that merely gets deleted is not really gone — the next load re-seeds it from the manifest, so a
    /// "remove" would look like it worked and then quietly undo itself. The default is the one provider the
    /// app cannot function without, so it is the one that is pinned.</para>
    ///
    /// <para>Its credentials go with it. Leaving them would strand secrets in the OS credential store with no
    /// UI left that can reach them — the same leak <see cref="RemoveCredential"/> closes for a single
    /// credential.</para>
    /// </summary>
    public bool RemoveProvider(string providerId)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null || provider.Id == AiProviderManifest.DefaultProviderId()) return false;

        // Disconnect first, and through the same method the disconnect button uses. Removing a provider that
        // still had a key would leave an orphan secret in the OS store — invisible in the UI, and never
        // cleaned up, because the account list it belonged to no longer renders anywhere. Doing it here rather
        // than in the dialog means the guarantee holds for every caller (the CLI included), not just the one
        // that remembered to ask.
        DisconnectProvider(providerId);

        _providerList.Remove(provider);
        _providers.Save(_providerList);

        // The cached model list goes with the provider. It names models for an endpoint that no longer
        // exists in the list, and a custom provider id is never reused — so a leftover entry could only ever
        // be read by something that no longer exists. Clearing it here rather than lazily is also what stops
        // the file growing a row per provider ever added.
        _modelLists.Save(providerId, null);

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Disconnects a provider: its credential and stored secret go, the provider itself stays.
    ///
    /// <para>Distinct from <see cref="RemoveProvider"/> on purpose, and not a weaker version of it. "Forget
    /// this key" and "stop showing me this endpoint" are different intentions: the first is a security action
    /// (revoke access on this machine) that should be available for <b>every</b> provider including the pinned
    /// default, the second is a list-management action. Folding them into one button would force a user who
    /// simply wants to unlink an account to also reconfigure the endpoint afterwards.</para>
    ///
    /// <para>Iterates the whole list rather than taking <see cref="CredentialFor"/> and trusting it to be the
    /// only one: a file written before the one-to-one rule can still hold several, and this is the path that
    /// guarantees no secret outlives the provider that owned it. The invariant is enforced at load, but a
    /// "delete everything for this provider" that silently skipped entries would leak on any list that arrived
    /// from somewhere other than the loader.</para>
    /// </summary>
    public bool DisconnectProvider(string providerId)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        var removed = false;
        foreach (var credential in _credentialList.Where(credential => credential.ProviderId == providerId).ToList())
        {
            _credentialList.Remove(credential);
            _credentials.DeleteSecret(credential.Id);
            removed = true;
        }

        if (removed)
        {
            provider.Credential = null;
            _credentials.Save(_credentialList);
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>
    /// Whether this provider may be removed from the list. The one exception is the manifest's default
    /// provider: it is re-seeded on every load, so removing it would silently undo itself, and the app has
    /// nothing to fall back on if it is gone. Everything else — built-in presets included — is removable,
    /// because a removed preset returns to the "add provider" catalog and can be added back.
    /// </summary>
    public static bool CanRemoveProvider(ModelProvider provider)
        => provider.Id != AiProviderManifest.DefaultProviderId();

    // ───────────────────────── Conversations ─────────────────────────

    public Conversation StartConversation(string? providerId = null)
    {
        var choice = SelectedChatModel;
        var selectedProvider = providerId is null
            ? choice?.Provider
            : _providerList.FirstOrDefault(provider => provider.Id == providerId);
        var modelName = selectedProvider is not null && choice is not null && selectedProvider.Id == choice.Provider.Id
            ? choice.ModelName
            : selectedProvider?.Model ?? "";
        var conversation = Conversation.Create(selectedProvider?.Id ?? "");
        conversation.ModelName = modelName;
        _conversations.Save(conversation);
        _active = conversation;
        Changed?.Invoke();
        return conversation;
    }

    public Conversation? OpenConversation(string id)
    {
        _active = _conversations.Load(id);
        Changed?.Invoke();
        return _active;
    }

    public void DeleteConversation(string id)
    {
        _conversations.Delete(id);
        if (_active?.Id == id) _active = null;
        Changed?.Invoke();
    }

    /// <summary>Renames a conversation; an empty title is refused so a session can never lose its label.
    /// Renaming writes only the conversation file, so the pin and the messages are untouched.</summary>
    public bool RenameConversation(string conversationId, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return false;

        var conversation = _active?.Id == conversationId ? _active : _conversations.Load(conversationId);
        if (conversation is null) return false;

        conversation.Title = trimmed;
        _conversations.Save(conversation);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Pins or unpins a conversation. Pinned sessions sort above the rest (see ConversationStore).</summary>
    public bool SetPinned(string conversationId, bool pinned)
    {
        var conversation = _active?.Id == conversationId ? _active : _conversations.Load(conversationId);
        if (conversation is null) return false;

        conversation.Pinned = pinned;
        _conversations.Save(conversation);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops every conversation that never received a message. Starting a new session writes an
    /// empty one immediately, so without this the sidebar slowly fills with abandoned "new chat" rows.</summary>
    public int PruneEmptyConversations()
    {
        var emptyIds = _conversations.List()
            .Where(summary => summary.MessageCount == 0)
            .Select(summary => summary.Id)
            .ToList();

        foreach (var id in emptyIds) _conversations.Delete(id);
        if (_active is not null && emptyIds.Contains(_active.Id)) _active = null;
        if (emptyIds.Count > 0) Changed?.Invoke();
        return emptyIds.Count;
    }

    /// <summary>Removes one message from a conversation and persists the result. Removing is the primitive
    /// behind the per-message delete action; edit/regenerate are built on top of it.</summary>
    public bool RemoveTurn(string conversationId, int index)
    {
        var conversation = _active?.Id == conversationId ? _active : _conversations.Load(conversationId);
        if (conversation is null || index < 0 || index >= conversation.Messages.Count) return false;

        conversation.Messages.RemoveAt(index);
        _conversations.Save(conversation);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Streams an assistant reply to <paramref name="text"/> in the active conversation, appending the user
    /// turn first and the assistant turn when the stream completes.
    ///
    /// The assistant turn is written **even when the caller cancels mid-stream** (with what arrived so far):
    /// dropping it would leave the user turn with no reply and no trace of what the model had already said.
    /// An error, by contrast, is not written as an assistant turn — it is returned in the reply so the panel
    /// can show it as a system notice rather than as the model's words.
    /// </summary>
    public async IAsyncEnumerable<string> SendAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_active is null) throw new InvalidOperationException("No active conversation.");
        var choice = SelectedChatModel
            ?? throw new InvalidOperationException("No authenticated provider with a configured model is available.");

        var conversation = _active;
        conversation.ProviderId = choice.Provider.Id;
        conversation.ModelName = choice.ModelName;
        conversation.Append(ChatTurn.User(text));
        _conversations.Save(conversation);

        await foreach (var chunk in StreamReplyAsync(conversation, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>
    /// Replaces the user turn at <paramref name="index"/> with <paramref name="text"/>. Everything after the
    /// edited turn is dropped: that text was answered once already, so the reply and any later turns are stale
    /// the moment the question changes. Streaming is a separate call (<see cref="ResendAsync"/>) so the view
    /// can repaint the shortened history before the new reply starts arriving.
    /// </summary>
    public bool EditAndResend(int index, string text)
    {
        if (_active is null) throw new InvalidOperationException("No active conversation.");
        if (index < 0 || index >= _active.Messages.Count) return false;

        var conversation = _active;
        conversation.Messages.RemoveRange(index, conversation.Messages.Count - index);
        conversation.Append(ChatTurn.User(text));
        _conversations.Save(conversation);
        return true;
    }

    /// <summary>Drops the trailing assistant turn (if any) so the last user turn can be answered again.</summary>
    public bool Regenerate()
    {
        if (_active is null) throw new InvalidOperationException("No active conversation.");

        var conversation = _active;
        if (conversation.Messages.Count > 0 && conversation.Messages[^1].Role == ChatRoles.Assistant)
            conversation.Messages.RemoveAt(conversation.Messages.Count - 1);
        _conversations.Save(conversation);
        return true;
    }

    /// <summary>Continues a reply the model stopped early: the instruction is appended as a user turn so the
    /// persisted history reads honestly (the user really did ask it to go on).</summary>
    public async IAsyncEnumerable<string> ContinueAsync(
        string instruction,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_active is null) throw new InvalidOperationException("No active conversation.");

        var conversation = _active;
        conversation.Append(ChatTurn.User(instruction));
        _conversations.Save(conversation);

        await foreach (var chunk in StreamReplyAsync(conversation, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>Streams a reply against the conversation's current history without appending a user turn.
    /// Used by edit-and-resend and regenerate, whose history change already happened.</summary>
    public async IAsyncEnumerable<string> ResendAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_active is null) throw new InvalidOperationException("No active conversation.");

        await foreach (var chunk in StreamReplyAsync(_active, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>Streams against the conversation's current history and writes the assistant turn on the way
    /// out. Shared by send / edit / regenerate / continue so the partial-reply-on-cancel rule holds once.</summary>
    private async IAsyncEnumerable<string> StreamReplyAsync(
        Conversation conversation,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var choice = SelectedChatModel
            ?? throw new InvalidOperationException("No authenticated provider with a configured model is available.");
        var provider = choice.Provider;

        var received = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in StreamAsync(provider, choice.ModelName, conversation, cancellationToken).ConfigureAwait(false))
            {
                received.Append(chunk);
                yield return chunk;
            }
        }
        finally
        {
            // Runs on cancellation and on success alike — the partial reply is still the model's answer.
            // On an exception from StreamAsync it also runs; the caller sees the exception re-thrown after.
            if (received.Length > 0)
            {
                conversation.Append(new ChatTurn(ChatRoles.Assistant, received.ToString(), DateTimeOffset.Now));
                _conversations.Save(conversation);
            }
        }
    }

    private IAsyncEnumerable<string> StreamAsync(
        ModelProvider provider,
        string modelName,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        var client = ClientOverride?.Invoke(provider) ?? ChatClientFactory.Create(provider, modelName);
        var pipeline = new ChatPipeline(client);

        // The history handed to the pipeline is everything said so far *excluding* the trailing user turn we
        // just appended and are about to answer; the pipeline adds it back as the last message.
        var history = conversation.Messages.ToList();
        return pipeline.SendAsync(provider, history, cancellationToken: cancellationToken);
    }

    public sealed record ChatModelOption(ModelProvider Provider, string ModelName)
    {
        public override string ToString() => $"{Provider.Name} · {ModelName}";
    }

    public void Dispose()
    {
        // Nothing holds a live handle yet (the client and secret store are per-call), but the chat view's
        // lifetime should match the shell's — keep the seam so a future cached client is released here.
    }

    /// <summary>Stand-in used when the platform has no OS credential store; it simply refuses to hold keys,
    /// so <see cref="ProviderStore"/> still round-trips provider metadata.</summary>
    private sealed class NoSecretStore : ISecretStore
    {
        public string? Read(string providerId) => null;
        public void Write(string providerId, string key) => throw new PlatformNotSupportedException();
        public void Delete(string providerId) { }
    }
}

/// <summary>
/// How a browser sign-in ended. Exactly one of the fields carries the outcome, which is what lets the caller
/// render one status line per case without inspecting exception types or message text.
/// </summary>
public sealed record OAuthSignInOutcome(
    ProviderCredential? Credential = null,
    bool Cancelled = false,
    string? ScopeRejected = null,
    string? Error = null);

/// <summary>
/// What came back from asking a provider whether a key is valid.
////
/// <para>An enum rather than a bool, because "the key is wrong" and "we could not find out" are different
/// answers and the UI has to treat them differently: the first blocks the save and tells the user to check
/// the key, the second says nothing and stores it. Collapsing them into one flag is how a validation feature
/// ends up rejecting working keys on a network that happened to be down.</para>
/// </summary>
public enum KeyCheckOutcome
{
    /// <summary>No probe is declared for this provider, so the key is stored as typed.</summary>
    Unsupported,

    /// <summary>The provider accepted the key.</summary>
    Accepted,

    /// <summary>The provider understood the request and refused the key (401/403). This is a real verdict.</summary>
    Rejected,

    /// <summary>The probe could not be completed — offline, DNS, timeout, a base URL that is not usable.</summary>
    Unreachable,
}
