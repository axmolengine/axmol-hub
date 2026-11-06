using System.Text.Json;
using static AxmolHub.ChatSessionCoordinator;
using static AxmolHub.ChatExecutionCoordinator;
using AxmolHub.Agent;
using AxmolHub.Core;
using static AxmolHub.ChatWorkspace;

namespace AxmolHub;
/// <summary>Owns credentials, provider configuration and the model catalog, including learned capabilities.</summary>
internal sealed partial class ChatProviderCoordinator
{
    internal ChatSessionCoordinator Sessions { private get; set; } = null!;
    internal ChatExecutionCoordinator Execution { private get; set; } = null!;

    internal ChatProviderCoordinator(string dataRoot)
    {
        _dataRoot = dataRoot;
        try
        {
            _secrets = SecretStoreFactory.Create(dataRoot);
        }
        catch (PlatformNotSupportedException)
        {
            _secrets = null;
        }

        _providers = new ProviderStore(dataRoot);
        _credentials = new CredentialStore(dataRoot, _secrets ?? new NoSecretStore());
        _modelLists = new ModelListStore(dataRoot);
    }

    private readonly string _dataRoot;
    private readonly ProviderStore _providers;
    private readonly CredentialStore _credentials;
    internal HttpClient? ModelListTransportOverride => _modelListHttp;
    private readonly ModelListStore _modelLists;
    private readonly ISecretStore? _secrets;
    private readonly List<ModelProvider> _providerList = [];
    private readonly List<ProviderCredential> _credentialList = [];
    private string? _selectedProviderId;
    private string? _selectedModelName;
    internal bool CanStoreSecrets => _secrets is not null;
    internal SecretStoreDescriptor SecretBackend => _secrets?.Descriptor ?? SecretStoreDescriptor.None;
    internal IReadOnlyList<ModelProvider> Providers => _providerList;
    internal IReadOnlyList<ChatModelOption> AvailableChatModels
        => _providerList
            .Where(provider => provider.Enabled && provider.IsCallReady)
            .SelectMany(provider => provider.Models
                .Where(model => model.Enabled)
                .OrderByDescending(model => model.InUse)
                .Select(model => new ChatModelOption(provider, model.Name)))
            .ToList();
    internal ChatModelOption? SelectedChatModel
        => Sessions.Active is { } viewed ? ModelFor(viewed.Id) : FindChatModel(_selectedProviderId, _selectedModelName) ?? AvailableChatModels.FirstOrDefault();

    internal ChatModelOption? ModelFor(string conversationId)
    {
        var conversation = Sessions.Registry.Peek(conversationId) ?? Sessions.Registry.Load(conversationId);
        if (conversation is null) return null;

        if (string.IsNullOrWhiteSpace(conversation.ModelName))
        {
            var active = _providerList.FirstOrDefault(provider => provider.Id == conversation.ProviderId)
                ?.ActiveModel?.Name;
            return FindChatModel(conversation.ProviderId, active)
                   ?? AvailableChatModels.FirstOrDefault(choice => choice.Provider.Id == conversation.ProviderId);
        }

        var named = FindChatModel(conversation.ProviderId, conversation.ModelName);
        return named ?? (ChatRouting.Normalize(conversation.Routing) == ChatRouting.Auto
            ? AvailableChatModels.FirstOrDefault(choice => choice.Provider.Id == conversation.ProviderId)
            : null);
    }

    internal string? UnresolvableSessionModel(string conversationId)
        => ModelFor(conversationId) is null
           && (Sessions.Registry.Peek(conversationId) ?? Sessions.Registry.Load(conversationId)) is { } conversation
           && ChatRouting.Normalize(conversation.Routing) != ChatRouting.Auto
           && !string.IsNullOrWhiteSpace(conversation.ModelName)
            ? conversation.ModelName
            : null;
    internal ChatModelOption? ResolvedModel(string conversationId, out string? refusalKey)
    {
        var choice = ModelFor(conversationId);
        if (choice is not null)
        {
            refusalKey = null;
            return choice;
        }

        refusalKey = "NoAvailableChatModels";
        if (UnresolvableSessionModel(conversationId) is { } gone)
            Execution.Audit(conversationId,
                $"Model unavailable: this session names {gone}, which the provider does not offer any more — "
                + "refusing rather than answering on another model");
        return null;
    }

    internal ModelProvider? ActiveProvider => SelectedChatModel?.Provider;

    internal bool SelectChatModel(string providerId, string modelName)
    {
        var choice = FindChatModel(providerId, modelName);
        if (choice is null) return false;

        var reasoningEffort = Sessions.SelectModelReasoningEffort(choice);
        _selectedProviderId = choice.Provider.Id;
        _selectedModelName = choice.ModelName;
        if (Sessions.Active is not null)
        {
            Sessions.Registry.TryUpdate(Sessions.Active.Id, conversation =>
            {
                // A notice row goes down only for a real change: re-picking the model this session is already on is
                // not a fact about the transcript, and a session restored to its own model when it opens is not a
                // switch either. Where it lands is where this history stopped being one model's, which is the
                // place a reader would otherwise have to guess at — and the answer to why the turns above it
                // sound like a different assistant.
                var switched = !string.Equals(conversation.ProviderId, choice.Provider.Id, StringComparison.Ordinal)
                               || !string.Equals(conversation.ModelName, choice.ModelName,
                                   StringComparison.OrdinalIgnoreCase);
                conversation.ProviderId = choice.Provider.Id;
                conversation.ModelName = choice.ModelName;
                conversation.ReasoningEffort = reasoningEffort;
                if (switched)
                    conversation.Append(ChatTurn.Notice(string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("ModelChangedFormat"), choice.ToString())));
            });
        }

        Execution.Changed?.Invoke();
        return true;
    }

    private ChatModelOption? FindChatModel(string? providerId, string? modelName)
        => string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelName)
            ? null
            : AvailableChatModels.FirstOrDefault(choice =>
                choice.Provider.Id == providerId
                && string.Equals(choice.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
    internal void LoadProviders()
    {
        _providerList.Clear();
        _providerList.AddRange(_providers.Load());

        // A saved provider records only its id; the built-in defaults (base URL, model, affiliate flag,
        // description, auth methods) come from the manifest. Re-deriving them here means editing
        // ai-providers.json updates existing installs instead of only new ones.
        var cachedModelLists = _modelLists.Load()
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var provider in _providerList)
        {
            var builtIn = provider.IsCustom ? null : AiProviderManifest.CreateBuiltIn(provider.Id);
            if (builtIn is not null)
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
                provider.CapabilitySource = builtIn.CapabilitySource;
                provider.ExtraHeaders = builtIn.ExtraHeaders;
                provider.RequestSemantics = builtIn.RequestSemantics;
                provider.WebToolsBackend = provider.WebToolsBackend.Length == 0
                    ? builtIn.WebToolsBackend : provider.WebToolsBackend;
                // ??= on purpose, unlike the three lines above: a hosted tool is a claim about the deployment this
                // person is standing on, and they are the only one who can test it. The manifest is the seed.
                // The `?? []` is for the compiler, not for a real case — `CreateBuiltIn` always hands over a list —
                // but the copy is what keeps a provider from sharing the manifest's own list.
                provider.ServerTools ??= [.. builtIn.ServerTools ?? []];
                // ??= for the same reason as ServerTools: the wire is a claim about the deployment this person is
                // standing on, so a saved declaration survives the manifest and a manifest-only change still
                // reaches an install that never said anything. An unknown value never gets here — the manifest
                // filters it through ModelProtocols.IsKnown when it projects the entry.
                provider.Protocol ??= builtIn.Protocol;
                // = and not ??=, the other way round from ServerTools above: this is what the shipped preset says
                // about the models it lists, not a claim about this person's deployment, so a correction in the
                // manifest has to reach an install that already stored a provider row. Nothing personal lives here
                // for it to overwrite — a person overrules a preset through provider.Protocol, or by naming one
                // model's wire in the manifest's modelProtocols table.
                provider.DeclaredProtocols = builtIn.DeclaredProtocols;
                // ??= and not = : a persisted number is now a deliberate human setting — except where nothing was
                // ever stored, which is what made an install run every model against 8192 while its own gateway
                // published 128000 for the preset and the saved row said null.
                provider.MaxContextTokens ??= builtIn.MaxContextTokens;
            }

            cachedModelLists.TryGetValue(provider.Id, out var modelCache);
            provider.ReasoningModels = MergeReasoningModels(
                builtIn?.ReasoningModels,
                modelCache?.ReasoningModels);
            provider.ModelCapabilities = ReportedCapabilities(modelCache?.Capabilities);
        }

        // First run (nothing saved yet): adopt the single default preset so the conversation page has a
        // configured provider to name. A user who has deliberately removed it is not re-seeded — the file
        // exists, so this branch is skipped.
        if (_providerList.Count == 0 && AiProviderManifest.DefaultProviderId() is { } defaultId)
        {
            if (AiProviderManifest.CreateBuiltIn(defaultId) is { } provider) _providerList.Add(provider);
        }

        LoadCredentials();
        Execution.Changed?.Invoke();
    }

    private void LoadCredentials()
    {
        _credentialList.Clear();
        var loaded = _credentials.Load();
        _credentialList.AddRange(loaded);
        // Persist mappings recovered from older valid token bundles before they can be cleared.
        if (_credentialList.Any(c => c.OpenAiRegistration is not null))
            _credentials.Save(_credentialList, writeSecrets: false);

        // Migration: no credential file, but the provider list has a legacy key stored under the provider id.
        // Writing the migrated list immediately is deliberate — it makes the upgrade a one-time event rather
        // than a condition re-evaluated on every launch.
        if (_credentialList.Count == 0)
        {
            var legacy = CredentialStore.MigrateFromProviders(_providerList);
            if (legacy.Count > 0)
            {
                _credentialList.AddRange(legacy);
                _credentials.Save(_credentialList, writeSecrets: false);
            }
        }

        foreach (var provider in _providerList)
        {
            // Keep every saved credential intact even when an older installation stored several; the oldest is the
            // one this provider answers with, and removing it leaves the provider signed out rather than
            // silently adopting a sibling row.
            var owned = _credentialList.Where(credential => credential.ProviderId == provider.Id).ToList();
            provider.Credential = provider.SelectedCredentialId is { } selected
                ? owned.FirstOrDefault(credential => credential.Id == selected)
                : owned.OrderBy(credential => credential.CreatedAt).FirstOrDefault();
            BindOpenAiTokens(provider);
        }
    }

    internal ProviderCredential? CredentialFor(string providerId)
        => _providerList.FirstOrDefault(provider => provider.Id == providerId)?.Credential;
    internal IReadOnlyList<ProviderCredential> Credentials => _credentialList;
    internal bool HasPlanCredentials(string providerId)
        => _credentialList.Any(credential => credential.ProviderId == providerId
            && credential.Secret is not null && IsPlanCredential(credential));

    internal ProviderCredential? AddCredential(string providerId, string label, string? secret, string source)
    {
        if (_providerList.FirstOrDefault(candidate => candidate.Id == providerId) is not { } provider) return null;
        if (!string.IsNullOrEmpty(secret) && !CanStoreSecrets) return null;
        if (provider.RequiresCredential && string.IsNullOrEmpty(secret)) return null;

        // One credential per provider, so a key replaces whatever it already held — including a ChatGPT plan
        // registration, whose fields are cleared here rather than left behind on a record that now speaks apiKey.
        var existing = CredentialFor(providerId);
        if (existing is not null)
        {
            lock (existing)
            {
                if (!string.IsNullOrEmpty(secret)) _secrets!.Write(existing.Id, secret);
                if (!string.IsNullOrEmpty(secret)) existing.Secret = secret;
                existing.AuthKind = "apiKey";
                existing.AccountId = null;
                existing.Scope = null;
                existing.OpenAiRegistration = null;
                existing.PlanUsagePaused = false;
                existing.Source = source;
                if (label.Trim().Length > 0) existing.Label = label.Trim();
                BindOpenAiTokens(provider);
                SaveCredentials(writeSecrets: false);
            }
            Execution.Changed?.Invoke();
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

        if (credential.Secret is { Length: > 0 }) _secrets!.Write(credential.Id, credential.Secret);
        _credentialList.Add(credential);
        SaveCredentials(writeSecrets: false);
        provider.Credential = credential;
        provider.SelectedCredentialId = credential.Id;
        BindOpenAiTokens(provider);
        _providers.Save(_providerList);
        Execution.Changed?.Invoke();
        return credential;
    }

    internal ProviderCredential? AddOAuthCredential(string providerId, string? accountId, string? scope, string secret, string authKind = "apiKey")
    {
        if (!CanStoreSecrets) return null;
        // A sign-in that came back without a key did not authenticate anything. Storing the record anyway would
        // overwrite a working secret on re-auth and mark the provider as ready while the client factory still
        // refuses it — the state this guard exists to keep out.
        if (string.IsNullOrEmpty(secret)) return null;
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return null;

        var existing = CredentialFor(providerId);
        if (existing is not null)
        {
            lock (existing)
            {
                _secrets!.Write(existing.Id, secret);
                existing.Secret = secret;
                existing.AuthKind = authKind;
                existing.Scope = scope;
                existing.Source = CredentialSources.OAuth;
                existing.AccountId = accountId;
                if (accountId is { Length: > 0 }) existing.Label = AccountLabel(providerId, accountId);
                BindOpenAiTokens(provider);
                SaveCredentials(writeSecrets: false);
            }
            Execution.Changed?.Invoke();
            return existing;
        }
        var credential = new ProviderCredential
        {
            Id = "cred-" + Guid.NewGuid().ToString("N")[..12], ProviderId = providerId,
            Label = accountId is { Length: > 0 } ? AccountLabel(providerId, accountId) : DefaultCredentialLabel(providerId),
            Source = CredentialSources.OAuth, AuthKind = authKind, AccountId = accountId, Scope = scope, Secret = secret,
        };
        _secrets!.Write(credential.Id, secret);
        _credentialList.Add(credential);
        SaveCredentials(writeSecrets: false);
        provider.Credential = credential;
        provider.SelectedCredentialId = credential.Id;
        BindOpenAiTokens(provider);
        _providers.Save(_providerList);
        Execution.Changed?.Invoke();
        return credential;
    }

    private void BindOpenAiTokens(ModelProvider provider)
    {
        if (provider.Id == "openai")
        {
            var mode = provider.IsOpenAiPlan ? "plan:" + provider.Credential!.Id
                : provider.IsOpenAiOAuth ? "openaiOAuth" : "apiKey";
            var cached = _modelLists.Load().FirstOrDefault(entry => entry.Id == ModelCacheId(provider.Id));
            if (provider.ModelAuthKind != mode)
            {
                provider.Models.Clear();
                provider.Model = "";
                provider.ModelAuthKind = mode;
                provider.Models.AddRange((cached?.Models ?? []).Select(name => new ProviderModel { Name = name, Enabled = true }));
                provider.Normalize();
                _providers.Save(_providerList);
            }
            var preset = AiProviderManifest.CreateBuiltIn(provider.Id);
            provider.ReasoningModels = MergeReasoningModels(preset?.ReasoningModels, cached?.ReasoningModels);
            provider.ModelCapabilities = ReportedCapabilities(cached?.Capabilities);
        }
        provider.OpenAiTokenProvider = provider.IsOpenAiPlan && provider.Credential is { } credential && _secrets is not null
            ? new OpenAiDynamicTokenSession(provider, credential, _secrets, OpenAiDynamicTokenExchange.Create(), _dataRoot).GetAsync
            : null;
    }

    private string ModelCacheId(string providerId)
        => _providerList.FirstOrDefault(provider => provider.Id == providerId) is { } provider
            ? provider.IsOpenAiPlan ? providerId + ":plan:" + provider.Credential!.Id
                : provider.IsOpenAiOAuth ? providerId + ":openaiOAuth" : providerId
            : providerId;
    private string AccountLabel(string providerId, string accountId)
    {
        var name = _providerList.FirstOrDefault(provider => provider.Id == providerId)?.Name ?? providerId;
        // The id can be long; the tail is the distinctive part of a numeric account id, so keep that.
        var shown = accountId.Length <= 12 ? accountId : "…" + accountId[^12..];
        return name + " · " + shown;
    }

    private string DefaultCredentialLabel(string providerId)
        => _providerList.FirstOrDefault(provider => provider.Id == providerId)?.Name ?? providerId;
    private static bool IsPlanCredential(ProviderCredential credential)
    {
        if (credential.ProviderId != "openai" || credential.AuthKind != "openaiOAuth") return false;
        if (credential.OpenAiRegistration is not null) return true; // A stale process must not delete a renewed session.
        if (credential.Secret is null) return true; // A missing secret cannot prove this is not a plan account.
        try { return OpenAiTokens.Decode(credential.Secret).IsDynamicRegistration; }
        catch (InvalidOperationException) { return true; } // Never delete an unreadable OAuth credential without its lock.
    }

    internal bool RemoveCredential(string credentialId) => RemoveCredentialCore(credentialId, planLockHeld: false);

    private bool RemoveCredentialCore(string credentialId, bool planLockHeld)
    {
        var credential = _credentialList.FirstOrDefault(candidate => candidate.Id == credentialId);
        if (credential is null || (!planLockHeld && IsPlanCredential(credential))) return false;

        lock (credential)
        {
            _credentials.DeleteSecret(credential.Id);
            _credentialList.Remove(credential);
            credential.Secret = null;
            if (_providerList.FirstOrDefault(provider => provider.Id == credential.ProviderId) is { } provider
                && ReferenceEquals(provider.Credential, credential))
            {
                provider.Credential = null;
                // An empty selection deliberately prevents old saved accounts from being auto-selected on restart.
                provider.SelectedCredentialId = "";
                provider.OpenAiTokenProvider = null;
                _providers.Save(_providerList);
            }
        }

        _credentials.Save(_credentialList, writeSecrets: false);
        Execution.Changed?.Invoke();
        return true;
    }

    internal ProviderModel? AddModel(string providerId, string name)
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

    internal bool RemoveModel(string providerId, string name)
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

    internal int RemoveAllModels(string providerId)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null || provider.Models.Count == 0) return 0;

        var removed = provider.Models.Count;
        provider.Models.Clear();
        provider.Normalize();
        SaveProviders();
        return removed;
    }

    internal bool SetActiveModel(string providerId, string name)
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

    internal bool SetModelEnabled(string providerId, string name, bool enabled)
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

    internal bool SetProviderEnabled(string providerId, bool enabled)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        if (provider.Enabled == enabled) return true;

        provider.Enabled = enabled;
        SaveProviders();
        return true;
    }

    internal bool SetPreferredAuthMethod(string providerId, string? method)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        if (provider.PreferredAuthMethod == method) return true;

        provider.PreferredAuthMethod = method;
        SaveProviders();
        return true;
    }

    internal bool SetModelContextTokens(string providerId, string name, int? tokens)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        var model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (model is null) return false;

        var corrected = tokens is > 0 ? tokens : null;
        if (model.MaxContextTokens == corrected) return true;

        model.MaxContextTokens = corrected;
        SaveProviders();
        Execution.Changed?.Invoke();
        return true;
    }

    internal (int Tokens, int OutputReserve, CapabilitySource Source) ContextWindowFor(string providerId, string name)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        var window = ContextBudget.For(provider, name);
        return (window.Tokens, window.OutputReserve, window.Source);
    }

    internal bool EnableCatalogModel(string providerId, string name)
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

    internal IReadOnlyList<ModelProvider> AvailablePresets()
        => AiProviderManifest.Load()
            .Where(entry => _providerList.All(provider => provider.Id != entry.Id))
            .Select(entry => AiProviderManifest.CreateBuiltIn(entry.Id))
            .OfType<ModelProvider>()
            .ToList();
    internal ModelProvider? AddPreset(string presetId)
    {
        if (_providerList.Any(provider => provider.Id == presetId)) return null;
        if (AiProviderManifest.CreateBuiltIn(presetId) is not { } provider) return null;

        _providerList.Add(provider);
        SaveProviders();
        return provider;
    }

    internal void SaveProviders()
    {
        _providers.Save(_providerList);
        Execution.Changed?.Invoke();
    }

    internal void RetrySecretReads()
    {
        if (_secrets is null) return;
        foreach (var credential in _credentialList)
        {
            lock (credential)
            {
                if (credential.Secret is null) credential.Secret = _secrets.Read(credential.Id);
            }
        }
        foreach (var provider in _providerList) BindOpenAiTokens(provider);
        Execution.Changed?.Invoke();
    }

    internal void SaveCredentials(bool writeSecrets = true)
    {
        _credentials.Save(_credentialList, writeSecrets);
        Execution.Changed?.Invoke();
    }

    internal ProviderCredential? SetApiKey(string providerId, string key)
        => string.IsNullOrWhiteSpace(key) ? null : AddCredential(providerId, "", key, CredentialSources.ApiKey);
    internal async Task<ProviderCredential> SavePlanCredentialAsync(ModelProvider provider, ProviderCredential? selected,
        OpenAiTokens? previous, string? observedSecret, OpenAiTokens tokens, CancellationToken cancellationToken)
    {
        if (_secrets is null) throw new InvalidOperationException("Secret storage is unavailable.");
        await using var credentialLock = previous is not null && selected is not null
            ? await OpenAiDynamicTokenSession.LockCredentialAsync(_dataRoot, selected.Id, cancellationToken)
            : null;
        if (!_providerList.Contains(provider) || !ReferenceEquals(provider.Credential, selected)
            || (previous is not null && (selected?.Secret != observedSecret
                || _secrets.Read(selected!.Id) != observedSecret)))
            throw new InvalidOperationException("The provider credential changed during sign-in. Retry the request.");
        if (previous is not null && (previous.ClientId != tokens.ClientId || previous.HostId != tokens.HostId
            || previous.Issuer != tokens.Issuer || previous.Subject != tokens.Subject))
            throw new InvalidOperationException("The signed-in OpenAI account does not match the saved registration.");
        // A legacy credential cannot be converted, but a successful sign-in replaces it in place: a second row
        // would leave behind an account that can only ever ask the user to sign in again. An API-key row is
        // left alone (signing in with ChatGPT must not destroy it), and "add another account" also adds a row.
        var replacing = selected is not null && (previous is not null || (selected.AuthKind == "openaiOAuth" && !provider.IsOpenAiPlan));
        var credential = replacing ? selected! : new ProviderCredential
        {
            Id = Guid.NewGuid().ToString("N"), ProviderId = provider.Id, Source = CredentialSources.OAuth,
            AuthKind = "openaiOAuth", Label = tokens.Email ?? "ChatGPT",
        };
        var encoded = tokens.Encode();
        var oldSecret = credential.Secret;
        var oldScope = credential.Scope;
        var oldAccount = credential.AccountId;
        var oldRegistration = credential.OpenAiRegistration;
        var added = !_credentialList.Contains(credential);
        var metadataSaved = false;
        try
        {
            credential.Scope = tokens.Scope;
            credential.AccountId = tokens.Subject;
            credential.OpenAiRegistration = OpenAiRegistration.FromTokens(tokens);
            if (added) _credentialList.Add(credential);
            // Make the non-secret identity durable first. A crash cannot strand an unreferenced new token.
            _credentials.Save(_credentialList, writeSecrets: false);
            metadataSaved = true;
            _secrets.Write(credential.Id, encoded);
            credential.Secret = encoded;
        }
        catch
        {
            credential.Secret = oldSecret;
            // If metadata was saved, keep its valid registration so the account remains recoverable.
            if (!metadataSaved)
            {
                credential.Scope = oldScope;
                credential.AccountId = oldAccount;
                credential.OpenAiRegistration = oldRegistration;
                if (added) _credentialList.Remove(credential);
            }
            throw;
        }
        provider.Credential = credential;
        provider.SelectedCredentialId = credential.Id;
        provider.PreferredAuthMethod = ProviderAuthMethods.OAuth;
        BindOpenAiTokens(provider);
        _providers.Save(_providerList);
        Execution.Changed?.Invoke();
        return credential;
    }

    internal async Task<OAuthSignInOutcome?> SignInWithOAuthAsync(
        string providerId,
        Action<string>? onManualUrl = null,
        CancellationToken cancellationToken = default,
        IProgress<DeviceCodeChallenge>? deviceProgress = null,
        Action<IOpenAiLoginAttempt>? openAiProgress = null)
    {
        if (!CanStoreSecrets) return null;

        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider?.OAuth is not { IsUsableFlow: true } oauth) return null;

        using var ownedHttp = _oauthHttp is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) } : null;
        var http = _oauthHttp ?? ownedHttp!;
        void OpenOrReport(string url)
        {
            // Try the browser first; when that fails the URL is handed to the caller instead of being
            // swallowed, because a sign-in with no browser and no link is a dead end.
            if (!BrowserOpener(url)) onManualUrl?.Invoke(url);
        }

        try
        {
            // No ConfigureAwait(false): the continuation calls AddOAuthCredential → SaveCredentials → Changed,
            // which ChatPanel.Reload consumes by mutating Avalonia controls. Keep it on the captured UI context
            // (same rule as RefreshModelsAsync), or the sign-in would touch the visual tree from a worker thread.
            //
            // The two flows are chosen by the manifest's declared shape rather than by provider id: which
            // endpoints a preset talks to is a fact about the service, and a device-code provider has no
            // discovery document to find on its own.
            if (oauth.EffectiveFlow == ProviderOAuthFlows.OpenAi)
            {
                var selected = provider.Credential;
                var observedSecret = selected?.Secret;
                // A provider owns one credential, so this sign-in resumes the registration the current one carries
                // instead of registering another host; a first sign-in has nothing to resume and starts clean.
                var previous = !provider.IsOpenAiPlan ? null
                    : selected?.HasSecret == true ? OpenAiTokens.Decode(observedSecret!)
                    : selected?.OpenAiRegistration?.AuthorizationContext();
                var hostId = previous?.HostId ?? new OpenAiHostIdStore(_dataRoot).GetOrCreate();
                var tokens = await new OpenAiDynamicLogin(OpenAiDynamicTokenExchange.Create()).SignInAsync(
                    hostId, previous, attempt => openAiProgress?.Invoke(attempt), url => { BrowserOpener(url); }, cancellationToken);
                if (!_providerList.Contains(provider) || !ReferenceEquals(provider.Credential, selected))
                    return new OAuthSignInOutcome(Error: "The provider credential changed during sign-in. Retry the request.");
                var saved = await SavePlanCredentialAsync(provider, selected, previous, observedSecret, tokens, cancellationToken);
                return new OAuthSignInOutcome(Credential: saved);
            }
            var result = oauth.EffectiveFlow == ProviderOAuthFlows.DeviceCode
                ? await new DeviceCodeOAuthFlow(http, OpenOrReport, DeviceCodeWait)
                    .SignInAsync(oauth, deviceProgress, cancellationToken)
                : await new OrcaRouterOAuthFlow(http, OpenOrReport, OAuthTimeout).SignInAsync(oauth, cancellationToken);
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

    internal Func<string, bool> BrowserOpener { get; set; } = UrlLauncher.TryOpen;
    internal HttpClient? OAuthHttp
    {
        get => _oauthHttp;
        set => _oauthHttp = value;
    }

    internal async Task<KeyCheckOutcome> CheckApiKeyAsync(
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
            using var owned = _oauthHttp is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) } : null;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            // The same static headers an inference call carries. Without them a provider that filters clients
            // answers 401 here for a reason that is not the key — and 401 is read below as "the key was rejected",
            // which is the one verdict that blocks the dialog.
            ProviderStaticHeaders.ApplyTo(request, provider.Headers);

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

    internal async Task<ModelFetchResult> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return ModelFetchResult.Unreachable("No such provider.");

        var catalogCredential = provider.Credential;
        var catalogMode = ModelCacheId(providerId);
        var catalogAccount = catalogCredential?.AccountId;

        // The injected client is *not* disposed. It is owned by whoever set ModelListHttp — the self-check reuses
        // one handler across a dozen fetches, and disposing it here would make every fetch after the first
        // fail with ObjectDisposedException, which surfaces as "the endpoint is unavailable" and reads like a
        // product bug. A client built here is ours, so only that one is disposed.
        using var owned = _modelListHttp is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) } : null;
        var client = _modelListHttp ?? owned!;
        // No ConfigureAwait(false) here: the continuation below ends in SaveProviders() → Changed, which is
        // consumed by ChatPanel.Reload — code that mutates Avalonia controls. It must resume on the captured
        // UI context; resuming on the thread pool would touch the visual tree from a worker thread and hang
        // the whole UI (a CPU-bound cross-thread re-entrancy, not a network wait).
        if (provider.NeedsOpenAiRelogin)
            return ModelFetchResult.Unreachable("This legacy ChatGPT credential requires signing in again.");
        var result = provider.IsOpenAiPlan
            ? await OpenAiPlanModels.FetchAsync(client, provider, cancellationToken)
            : await ModelList.FetchAsync(client, provider, cancellationToken);
        if (!_providerList.Contains(provider) || !ReferenceEquals(provider.Credential, catalogCredential)
            || ModelCacheId(providerId) != catalogMode || provider.Credential?.AccountId != catalogAccount)
            return ModelFetchResult.Unreachable("The provider credential changed during the catalog request. Retry the request.");
        if (!result.Reachable) return result;

        // Split against the *previous* fetch, not against nothing. "Not in this response" alone cannot tell a
        // retired model from a hand-written one — both are simply absent — and guessing wrong in either
        // direction is bad: dropping a name the user typed deletes work they did on purpose, and keeping one
        // the provider retired offers a name that will 404 on first use and blames their key.
        var cachedEntry = _modelLists.Load().FirstOrDefault(entry => entry.Id == ModelCacheId(providerId));
        var reported = cachedEntry?.Models ?? [];
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
        var declared = AiProviderManifest.CreateBuiltIn(providerId);
        provider.ReasoningModels = MergeReasoningModels(declared?.ReasoningModels, result.ReasoningModels);
        provider.ModelCapabilities = ReportedCapabilities(result.Capabilities);
        _modelLists.Save(ModelCacheId(providerId), result.Models, result.ReasoningModels, result.Capabilities);
        SaveProviders();
        return result;
    }

    internal IReadOnlyList<string> CachedModels(string providerId)
        => _modelLists.Load().FirstOrDefault(entry => entry.Id == ModelCacheId(providerId))?.Models ?? [];
    internal HttpClient? ModelListHttp
    {
        get => _modelListHttp;
        set => _modelListHttp = value;
    }

    private HttpClient? _modelListHttp;
    internal HttpClient? WebFetchHttp
    {
        get => _webFetchHttp;
        set => _webFetchHttp = value;
    }

    private HttpClient? _webFetchHttp;
    internal HttpClient? DeepSeekWebApiHttp { get; set; }
    internal TimeSpan OAuthTimeout { get; set; } = OrcaRouterOAuthFlow.DefaultTimeout;
    internal TimeSpan DeviceCodeWait { get; set; } = DeviceCodeOAuthFlow.DefaultWait;

    private HttpClient? _oauthHttp;
    /// <summary>
    /// Adds a custom endpoint. <paramref name = "problem"/> carries the text key of whatever stopped it, and is
    /// <c>null</c> on success: the caller shows it verbatim instead of guessing, which is how an empty model box
    /// once got reported as a rejected URL. The model may be empty — the endpoint's own catalog fills it in
    /// later, and a gateway that lists nothing can stay model-less without blocking the provider.
    /// </summary>
    internal ModelProvider? AddProvider(
        string name,
        string baseUrl,
        string model,
        string? apiKey,
        out string? problem,
        string? protocol = null,
        IReadOnlyDictionary<string, string>? customHeaders = null)
    {
        if (protocol is not null && !ModelProtocols.IsKnown(protocol))
        {
            problem = "ProviderWireApiInvalid";
            return null;
        }

        var trimmedModel = model.Trim();
        var provider = new ModelProvider
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..12],
            Name = name.Trim(),
            IsCustom = true,
            BaseUrl = baseUrl.Trim(),
            Protocol = protocol,
            CustomHeaders = customHeaders is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : customHeaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            // No model, no row: a blank ProviderModel would render as an empty row in the list and read as a
            // model whose name is "".
            Models = trimmedModel.Length == 0 ? [] : [new ProviderModel { Name = trimmedModel, InUse = true }],
            // Whether the user's own endpoint demands a key is unknowable, so a custom provider is never
            // gated on one — declaring the key method says only that a key can be pasted here, which is what
            // keeps the 「鉴权」 entrance while leaving the provider usable keyless (a local server). Browser
            // sign-in is deliberately absent: there is no server to sign in to, so the picker would otherwise
            // invent a button that cannot work.
            AuthMethods = [ProviderAuthMethods.ApiKey],
        };

        if ((problem = Validate(provider)) is not null) return null;
        problem = null;

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
                problem = "ApiKeyPlatformUnsupported";
                return null;
            }
        }

        return provider;
    }

    /// <inheritdoc cref = "AddProvider"/>
    internal bool UpdateProvider(
        string providerId,
        string name,
        string baseUrl,
        string model,
        string? apiKey,
        out string? problem,
        string? protocol = null,
        IReadOnlyDictionary<string, string>? customHeaders = null)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null)
        {
            // No field is to blame: the row being edited no longer exists.
            problem = null;
            return false;
        }

        if (provider.IsCustom && protocol is not null && !ModelProtocols.IsKnown(protocol))
        {
            problem = "ProviderWireApiInvalid";
            return false;
        }

        var edited = new ModelProvider
        {
            Id = provider.Id,
            Name = provider.IsCustom ? name.Trim() : provider.Name,
            IsCustom = provider.IsCustom,
            BaseUrl = provider.IsCustom ? baseUrl.Trim() : provider.BaseUrl,
            Protocol = provider.IsCustom ? protocol ?? provider.Protocol : provider.Protocol,
            CustomHeaders = provider.IsCustom && customHeaders is not null
                ? customHeaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                : provider.CustomHeaders,
        };
        if ((problem = Validate(edited)) is not null) return false;
        problem = null;

        // Rotate through the existing credential path before changing configuration, so a refused key
        // cannot leave a half-applied endpoint edit. A blank key deliberately leaves the credential alone.
        if (!string.IsNullOrEmpty(apiKey)
            && AddCredential(providerId, "", apiKey, CredentialSources.ApiKey) is null)
        {
            problem = "ApiKeyPlatformUnsupported";
            return false;
        }

        provider.Name = edited.Name;
        provider.BaseUrl = edited.BaseUrl;
        provider.Protocol = edited.Protocol;
        provider.CustomHeaders = edited.CustomHeaders;

        // Endpoint-only edits must not touch the model list or its activation flags. Legacy callers can
        // still rename the active model without using Model's inserting setter.
        var renamed = model.Trim();
        if (!string.Equals(renamed, provider.Model, StringComparison.Ordinal))
        {
            if (provider.ActiveModel is { } current)
            {
                // A rename onto an existing name keeps that row rather than creating a duplicate.
                if (provider.Models.Any(candidate => !ReferenceEquals(candidate, current)
                        && string.Equals(candidate.Name, renamed, StringComparison.OrdinalIgnoreCase)))
                    provider.Models.Remove(current);
                else
                    current.Name = renamed;
            }
            else if (renamed.Length > 0)
            {
                provider.Models.Add(new ProviderModel { Name = renamed, InUse = true });
            }
            provider.Normalize();
        }

        SaveProviders();
        return true;
    }

    internal bool RemoveProvider(string providerId)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null || provider.Id == AiProviderManifest.DefaultProviderId()) return false;

        // A plan session must be signed out through its asynchronous locked revocation path first.
        if (_credentialList.Any(credential => credential.ProviderId == providerId && IsPlanCredential(credential))) return false;

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
        _modelLists.Save(providerId + ":openaiOAuth", null);

        Execution.Changed?.Invoke();
        return true;
    }

    internal Func<OpenAiTokens, CancellationToken, Task<bool>> RevokeOpenAiPlan { get; set; }
        = (tokens, cancellationToken) => OpenAiSessionRevocation.Create().RevokeAsync(tokens, cancellationToken);

    internal async Task<(bool Removed, bool Revoked)> DisconnectOpenAiPlanAsync(CancellationToken cancellationToken = default)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == "openai");
        if (provider?.IsOpenAiOAuth != true || provider.Credential is not { } credential || _secrets is null)
            return (false, false);
        await using var credentialLock = await OpenAiDynamicTokenSession.LockCredentialAsync(_dataRoot, credential.Id, cancellationToken);
        if (!ReferenceEquals(provider.Credential, credential)) return (false, false);
        var persisted = _secrets.Read(credential.Id);
        OpenAiTokens? tokens = null;
        try { if (persisted is not null) tokens = OpenAiTokens.Decode(persisted); }
        catch (InvalidOperationException) { } // A damaged payload still needs a local recovery path.
        if (tokens?.IsDynamicRegistration == true)
        {
            var registration = OpenAiRegistration.FromTokens(tokens);
            if (credential.OpenAiRegistration is { } saved && (saved.ClientId != registration.ClientId
                || saved.HostId != registration.HostId || saved.Issuer != registration.Issuer || saved.Subject != registration.Subject))
                throw new InvalidOperationException("The OpenAI credential changed during sign-out. Retry the request.");
            credential.OpenAiRegistration = registration;
        }
        // Durably retain the verified identity before deleting tokens, including after a crash or save conflict.
        if (credential.OpenAiRegistration is not null)
            _credentials.Save(_credentialList, writeSecrets: false);
        // Local deletion precedes network revocation so a crash cannot silently restore the session.
        _secrets.Delete(credential.Id);
        credential.Secret = null;
        provider.OpenAiTokenProvider = null;
        if (credential.OpenAiRegistration is null)
        {
            _credentialList.Remove(credential);
            provider.Credential = null;
            provider.SelectedCredentialId = "";
        }
        _credentials.Save(_credentialList, writeSecrets: false);
        _modelLists.Save("openai:plan:" + credential.Id, null);
        BindOpenAiTokens(provider);
        _providers.Save(_providerList);
        Execution.Changed?.Invoke();
        try
        {
            return (true, tokens?.IsDynamicRegistration == true
                && await RevokeOpenAiPlan(tokens, cancellationToken));
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
        {
            return (true, false);
        }
    }

    internal bool DisconnectProvider(string providerId)
    {
        var provider = _providerList.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return false;

        // Refuse the entire operation rather than partially deleting accounts or bypassing plan revocation.
        if (_credentialList.Any(credential => credential.ProviderId == providerId && IsPlanCredential(credential))) return false;
        var removed = false;
        foreach (var credential in _credentialList.Where(credential => credential.ProviderId == providerId).ToList())
        {
            removed |= RemoveCredential(credential.Id);
        }

        if (removed)
        {
            provider.Credential = null;
            _credentials.Save(_credentialList, writeSecrets: false);
            Execution.Changed?.Invoke();
        }

        return removed;
    }


    internal IReadOnlyList<string> SensitiveValues()
        => [.. _credentialList.Select(credential => credential.Secret).Where(secret => secret is { Length: > 0 })!];

    internal async Task<bool> ForgoStreamUsageAsync(string conversationId, ModelProvider provider)
    {
        var backedOff = false;
        await Execution.ApplyOnUiAsync(() =>
        {
            if (!_providerList.Contains(provider) || !provider.WantsStreamUsage) return;
            provider.ExtraOptions[ModelProvider.StreamUsageOption] = "false";
            _providers.Save(_providerList);
            Execution.Audit(conversationId, $"{provider.Name} declined the stream usage report; stopped asking");
            backedOff = true;
        }).ConfigureAwait(false);
        return backedOff;
    }

    internal async Task NoteReasoningEvidenceAsync(string conversationId, ChatModelOption choice, string? effort, bool rejected)
    {
        if (effort is not { Length: > 0 } level || level == ChatReasoningEfforts.Default) return;
        await Execution.ApplyOnUiAsync(() =>
        {
            var provider = choice.Provider;
            if (!_providerList.Contains(provider)) return;
            var cache = _modelLists.Load().FirstOrDefault(entry => entry.Id == ModelCacheId(provider.Id));
            var reasoning = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
            if (cache?.ReasoningModels is { } cached)
                foreach (var (name, profile) in cached)
                    reasoning[name] = profile.Clone();

            var observed = reasoning.GetValueOrDefault(choice.ModelName) ?? new AiModelReasoning();
            var bucket = rejected ? observed.RejectedEfforts : observed.ObservedEfforts;
            if (bucket.Contains(level, StringComparer.OrdinalIgnoreCase)) return;
            bucket.Add(level);
            reasoning[choice.ModelName] = observed;

            // Capabilities ride along: the same entry holds the windows the gateway reported, and Save replaces
            // the whole provider record — leaving them out would erase a model's window every time a reply
            // proved that model could think.
            _modelLists.Save(ModelCacheId(provider.Id), cache?.Models ?? [], reasoning, cache?.Capabilities);
            provider.ReasoningModels = MergeReasoningModels(
                AiProviderManifest.CreateBuiltIn(provider.Id)?.ReasoningModels, reasoning);
            Execution.Audit(conversationId, $"Reasoning {(rejected ? "refused" : "confirmed")}: {level} on {choice.ModelName}");
            Execution.Changed?.Invoke();
        }).ConfigureAwait(false);
    }

    internal Task NoteLearnedWindowAsync(string conversationId, ChatModelOption choice, int tokens)
        => NoteWindowAsync(conversationId, choice, tokens, CapabilitySource.LearnedFromRefusal);
    internal Task NoteObservedWindowAsync(string conversationId, ChatModelOption choice, int tokens)
        => NoteWindowAsync(conversationId, choice, tokens, CapabilitySource.EndpointReported);
    private async Task NoteWindowAsync(string conversationId, ChatModelOption choice, int tokens, CapabilitySource source)
    {
        await Execution.ApplyOnUiAsync(() =>
        {
            var provider = choice.Provider;
            if (!_providerList.Contains(provider)) return;
            var cache = _modelLists.Load().FirstOrDefault(entry => entry.Id == ModelCacheId(provider.Id));
            var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
            if (cache?.Capabilities is { } cached)
                foreach (var (name, caps) in cached)
                    capabilities[name] = caps;

            var known = capabilities.GetValueOrDefault(choice.ModelName) ?? new ModelCapabilities();
            if (known.ContextTokens == tokens && known.Source == source) return;
            capabilities[choice.ModelName] = known.WithContext(tokens, source);

            _modelLists.Save(ModelCacheId(provider.Id), cache?.Models ?? [], cache?.ReasoningModels, capabilities);
            provider.ModelCapabilities = ReportedCapabilities(capabilities);
        }).ConfigureAwait(false);
    }


    private static string GrantedScopeOf(string message)
    {
        var start = message.IndexOf('\'') + 1;
        var end = message.IndexOf('\'', start);
        return start > 0 && end > start ? message[start..end] : "";
    }
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
    /// Indexes what the endpoint said about its models, dropping the records that say nothing — "the endpoint told
    /// us" has to stay readable as a set of facts, and 205 empty records would not.
    ///
    /// <para>This used to merge a declared record with a fetched one field by field, because the manifest's per-model
    /// wire rode <see cref="ModelCapabilities.EndpointTypes"/> and whole-record replacement ate it. The wire has its
    /// own home now (<see cref="ModelProvider.DeclaredProtocols"/>), so nothing of the manifest's is left in this
    /// table: what remains is an observation, and the precedence question moved to
    /// <c>ChatClientFactory.ProtocolFor</c>, where one function owns it.</para>
    /// </summary>
    private static Dictionary<string, ModelCapabilities> ReportedCapabilities(
        IReadOnlyDictionary<string, ModelCapabilities>? fetched)
    {
        var result = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
        if (fetched is null) return result;
        foreach (var (name, caps) in fetched)
            if (!caps.IsEmpty) result[name] = caps;

        return result;
    }

    private static Dictionary<string, AiModelReasoning> MergeReasoningModels(
        IReadOnlyDictionary<string, AiModelReasoning>? declared,
        IReadOnlyDictionary<string, AiModelReasoning>? fetched)
    {
        var result = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
        if (declared is not null)
            foreach (var (name, profile) in declared)
                result[name] = profile;

        if (fetched is not null)
        {
            foreach (var (name, profile) in fetched)
            {
                result.TryGetValue(name, out var declaredProfile);
                result[name] = new AiModelReasoning
                {
                    Efforts = profile.Efforts.Count > 0
                        ? [.. profile.Efforts]
                        : declaredProfile is null ? [] : [.. declaredProfile.Efforts],
                    DefaultEffort = profile.DefaultEffort ?? declaredProfile?.DefaultEffort,
                    RequestOptions = declaredProfile?.RequestOptions ?? [],
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Validates a provider's editable fields. Shared by the settings page's draft (live "can I save yet"
    /// feedback, through <see cref = "ValidateDraft"/>) and by
    /// <see cref = "AddProvider"/>/<see cref = "UpdateProvider"/> (the real gate) so the two cannot drift — the UI
    /// must not be the only thing standing between a half-filled provider and a saved file.
    ///
    /// <para>No model check: a provider with no models is a legal state. The models are fetched from the
    /// endpoint itself after it exists, and a gateway that serves nothing it lists is still worth saving.</para>
    /// </summary>
    /// <returns><c>null</c> when valid; otherwise the text key of the problem to show.</returns>
    internal static string? Validate(ModelProvider provider)
    {
        if (provider.Name.Trim().Length == 0) return "ProviderNameRequired";
        if (!IsHttpUrl(provider.BaseUrl)) return "ProviderBaseUrlInvalid";
        return null;
    }

    /// <summary>
    /// The same rule as <see cref = "Validate"/>, applied to the two fields the draft has collected so far —
    /// before there is a provider object to hand it. What it returns is what greys out 保存 and what the draft
    /// says out loud, so the field at fault is named while the person is still typing it.
    /// </summary>
    internal static string? ValidateDraft(string name, string baseUrl, string? headersJson = null)
    {
        var problem = Validate(new ModelProvider
        {
            Name = name.Trim(),
            BaseUrl = baseUrl.Trim(),
        });
        if (problem is not null) return problem;
        return TryParseCustomHeaders(headersJson, out _) ? null : "ProviderCustomHeadersInvalid";
    }

    /// <summary>Parses the optional JSON object of string-valued custom request headers.</summary>
    internal static bool TryParseCustomHeaders(
        string? json,
        out Dictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
                headers[property.Name] = property.Value.GetString() ?? "";
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }


    /// <summary>
    /// Accepts only an absolute http/https URL. A bare host ("localhost:11434/v1") looks right to a person
    /// but <c>new Uri(...)</c> will not produce a usable endpoint from it, and the failure would only surface
    /// on the first message — so it is rejected here, with the field to fix.
    ///
    /// The rule itself lives on <see cref = "AiProviderEntry.IsUsableBaseUrl"/> so the preset catalog is held to
    /// exactly the same standard as a hand-typed endpoint; this is the name the UI and dialogs already call.
    /// </summary>
    internal static bool IsHttpUrl(string url) => AiProviderEntry.IsUsableBaseUrl(url);
    /// <summary>
    /// Whether this provider may be removed from the list. The one exception is the manifest's default
    /// provider: it is re-seeded on every load, so removing it would silently undo itself, and the app has
    /// nothing to fall back on if it is gone. Everything else — built-in presets included — is removable,
    /// because a removed preset returns to the "add provider" catalog and can be added back.
    /// </summary>
    internal static bool CanRemoveProvider(ModelProvider provider)
        => provider.Id != AiProviderManifest.DefaultProviderId();
    private sealed class NoSecretStore : ISecretStore
    {
        public string? Read(string providerId) => null;
        public void Write(string providerId, string key) => throw new PlatformNotSupportedException();
        public void Delete(string providerId)
        {
        }
    }

}
