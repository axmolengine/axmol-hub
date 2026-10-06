using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

// args[0] is the data root ONLY when it is an actual path. A leading "--" means the caller passed a
// flag in first position, in which case we fall back to the default so flags never get turned into
// directory names (that was how "--check-cli-json"/"--check-build-profiles" ended up as empty folders).
var root = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "artifacts/checks");
Directory.CreateDirectory(root);

// 构建已委派给引擎 cmdline；Checks 里的 ProjectService 共用仓库内的包装脚本。
EngineCommandLine EngineCli(ProcessRunner runner) => new(runner, Path.GetFullPath("src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1"));
if (args.Contains("--check-ai-providers"))
{
    void AssertRejects(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS: " + name); return; }
        throw new Exception("FAILED: " + name);
    }

    // Built-in manifest entry assembles the expected provider (no half-built objects).
    var orca = AiProviderManifest.CreateBuiltIn("orcarouter")
        ?? throw new Exception("orcarouter is missing from ai-providers.json.");
    if (orca.IsCustom) throw new Exception("Built-in provider must not be marked custom.");
    if (orca.BaseUrl != "https://api.orcarouter.ai/v1") throw new Exception($"Wrong base URL: {orca.BaseUrl}");
    // No model is seeded, and the list is asserted empty rather than merely "not the old value": a manifest
    // that quietly reintroduced a default would put a name in front of the user that goes stale the moment
    // the provider retires it, and it would 404 on first use with the user's key blamed for it.
    if (orca.Models.Count != 0) throw new Exception($"Manifest must not seed a model (found {orca.Models.Count}).");
    if (!orca.ApiKeyRequired) throw new Exception("orcarouter should require an API key.");
    if (!orca.Affiliate) throw new Exception("orcarouter should be flagged affiliate.");
    if (string.IsNullOrEmpty(orca.ReferralUrl)) throw new Exception("orcarouter should carry a referral URL.");
    Console.WriteLine("PASS: orcarouter manifest entry assembles the expected provider.");

    if (AiProviderManifest.CreateBuiltIn("nonexistent") is not null)
        throw new Exception("Unknown provider id should return null.");
    Console.WriteLine("PASS: unknown provider id returns null.");

    // The catalog: several presets, every one complete enough to be adopted with nothing typed, and every one
    // searchable by description (the picker filters on it, so an empty blurb would silently hide the preset
    // from anyone who does not already know the vendor's name).
    var catalog = AiProviderManifest.Load();
    if (catalog.Count < 3) throw new Exception($"The preset catalog should offer several providers, found {catalog.Count}.");
    foreach (var entry in catalog)
    {
        if (entry.Id.Length == 0 || entry.Name.Length == 0) throw new Exception($"A preset is missing id or name: {entry.Id}");
        if (entry.Description.Length == 0) throw new Exception($"Preset '{entry.Id}' has no description for the picker card.");
        // No preset may name a default model. A shipped name rots the moment the vendor adds or retires a
        // model, and a rotted default is offered in the list and then fails on first use with a 404 that
        // blames the user's key. The list is read from the endpoint (ModelCatalog) after authenticating.
        if (entry.GetType().GetProperty("DefaultModel") is not null)
            throw new Exception($"Preset '{entry.Id}' still exposes a DefaultModel property; the model list must come from the endpoint.");
        if (!AiProviderEntry.IsUsableBaseUrl(entry.BaseUrl)) throw new Exception($"Preset '{entry.Id}' has a base URL the client cannot use: {entry.BaseUrl}");
    }
    Console.WriteLine($"PASS: the catalog carries {catalog.Count} complete presets, each with a description and no baked-in model.");

    // Every preset must declare how it authenticates, and the declaration must survive the JSON round trip
    // with unknown values filtered out. Without this a preset could silently become unusable.
    foreach (var entry in catalog)
    {
        if (entry.EffectiveAuthMethods.Count == 0)
            throw new Exception($"Preset '{entry.Id}' declares no usable auth method, so no account could ever be added.");
        if (entry.AuthMethods.Any(method => !ProviderAuthMethods.IsKnown(method)))
            throw new Exception($"Preset '{entry.Id}' declares an auth method the app does not implement: {string.Join(",", entry.AuthMethods)}");
    }
    if (!orca.SupportsOAuth) throw new Exception("orcarouter should declare OAuth support.");
    if (orca.OAuth is null) throw new Exception("A preset that supports OAuth must carry its OAuth parameters.");
    if (orca.OAuth.Scope != "api") throw new Exception($"orcarouter must request the narrow 'api' scope, got '{orca.OAuth.Scope}'.");
    if (!AiProviderEntry.IsUsableBaseUrl(orca.OAuth.DiscoveryUrl)) throw new Exception("The OAuth discovery URL must be a usable URL.");
    if (string.IsNullOrEmpty(orca.OAuth.AppName)) throw new Exception("The OAuth app name is shown on the consent screen and must not be empty.");
    Console.WriteLine("PASS: every preset declares a usable auth method and orcarouter carries its OAuth parameters.");

    // The manifest entry and the runtime provider must agree on auth support: the settings page renders a
    // ModelProvider, so a difference here would offer a button the flow cannot honour.
    var manifestOrca = AiProviderManifest.Find("orcarouter") ?? throw new Exception("orcarouter vanished from the manifest.");
    if (manifestOrca.SupportsOAuth != orca.SupportsOAuth)
        throw new Exception("The manifest entry and the runtime provider disagree about OAuth support.");
    if (!manifestOrca.EffectiveAuthMethods.SequenceEqual(orca.EffectiveAuthMethods))
        throw new Exception("The manifest entry and the runtime provider disagree about auth methods.");
    Console.WriteLine("PASS: manifest entry and runtime provider agree on the auth surface.");

    // A preset without an authMethods array, or with only values the app cannot implement, falls back to
    // apiKey rather than becoming unusable. This is the compatibility path for manifests written earlier.
    //
    // The options matter: the file is camelCase and the model is PascalCase, so the default (case-sensitive)
    // options deserialize to an *empty* object without throwing — and an empty object happens to also fall back
    // to apiKey, which would make this assertion pass while reading nothing.
    var legacyFileJson = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var legacy = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>(
        """{"id":"legacy","name":"Legacy","baseUrl":"https://example.test/v1","model":"m","authMethods":["carrier-pigeon"]}""",
        legacyFileJson);
    if (legacy is null || !legacy.EffectiveAuthMethods.SequenceEqual([ProviderAuthMethods.ApiKey]))
        throw new Exception("An unknown or absent authMethods list should fall back to apiKey.");
    if (legacy.Id != "legacy") throw new Exception("The legacy fixture was not actually deserialized.");
    if (legacy.SupportsOAuth) throw new Exception("A provider with no OAuth block must not claim OAuth support.");
    Console.WriteLine("PASS: an unrecognised authMethods list falls back to apiKey instead of breaking the preset.");

    // DefaultProviderId must ignore a default that names a provider the catalog does not carry.
    if (AiProviderManifest.DefaultProviderId() is null)
        throw new Exception("DefaultProviderId returned null for the shipped manifest.");
    Console.WriteLine("PASS: the shipped manifest resolves a default provider id.");

    // Localized descriptions fall back to English rather than resolving empty — the picker shows this copy, so
    // an empty string would render a blank card.
    var ollama = AiProviderManifest.CreateBuiltIn("ollama") ?? throw new Exception("ollama is missing from the catalog.");
    if (ollama.ApiKeyRequired) throw new Exception("A local Ollama endpoint should not require an API key.");
    if (ollama.Describe("zh-CN").Length == 0 || ollama.Describe("en-US").Length == 0)
        throw new Exception("A preset description resolved empty for a supported language.");
    if (ollama.Describe("fr-FR") != ollama.Description)
        throw new Exception("An unsupported language should fall back to the English description.");
    Console.WriteLine("PASS: preset descriptions resolve per language and fall back to English.");

    // The seed contract: exactly one default, and it is a real catalog entry. A default that names a
    // non-existent provider would leave a fresh install with nothing configured.
    var defaultId = AiProviderManifest.DefaultProviderId();
    if (defaultId is null || catalog.All(entry => entry.Id != defaultId))
        throw new Exception($"The manifest's default provider '{defaultId}' is not in the catalog.");
    Console.WriteLine($"PASS: the manifest names one real default preset to seed ('{defaultId}').");

    // Factory validation: custom needs base URL + model; cloud needs a key; a keyless local endpoint works.
    AssertRejects(() => ChatClientFactory.Create(new ModelProvider { IsCustom = true, Model = "m" }), "custom provider without base URL is rejected");
    AssertRejects(() => ChatClientFactory.Create(new ModelProvider { IsCustom = true, BaseUrl = "http://localhost:11434/v1" }), "custom provider without model is rejected");
    AssertRejects(() => ChatClientFactory.Create(new ModelProvider { BaseUrl = "https://api.orcarouter.ai/v1", Model = "orcarouter/auto", ApiKeyRequired = true }), "required API key missing is rejected");
    if (ChatClientFactory.Create(new ModelProvider { IsCustom = true, Name = "Local", BaseUrl = "http://localhost:11434/v1", Model = "llama3" }) is null)
        throw new Exception("Keyless local provider should produce a client.");
    Console.WriteLine("PASS: factory validates providers and assembles a keyless local client.");

    // ProviderStore keeps keys out of JSON; CredentialStore owns the key and rehydrates it from the
    // secret store. The split is the whole point: a provider is a declaration, an account is a secret.
    var secretStore = new InMemorySecretStore();
    var providerStore = new ProviderStore(root);
    providerStore.Save([new ModelProvider { Id = "orcarouter", Name = "OrcaRouter", BaseUrl = "https://api.orcarouter.ai/v1", ApiKeyRequired = true, Model = "orcarouter/auto" }]);
    var credentialStore = new CredentialStore(root, secretStore);
    var credential = new ProviderCredential
    {
        Id = "orcarouter",
        ProviderId = "orcarouter",
        Label = "Default",
        Source = CredentialSources.ApiKey,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
    credential.Secret = "sk-secret-123";
    credentialStore.Save([credential]);

    var providersJson = File.ReadAllText(Path.Combine(root, "ai", "providers.json"));
    if (providersJson.Contains("sk-secret-123")) throw new Exception("API key leaked into providers.json.");
    var credentialsJson = File.ReadAllText(Path.Combine(root, "ai", "credentials.json"));
    if (credentialsJson.Contains("sk-secret-123")) throw new Exception("API key leaked into credentials.json.");

    var roundTrip = credentialStore.Load().Single(item => item.Id == "orcarouter");
    if (roundTrip.Secret != "sk-secret-123") throw new Exception("API key was not rehydrated from the secret store.");
    Console.WriteLine("PASS: ProviderStore and CredentialStore keep keys out of JSON and rehydrate them.");

    // A credential that names a provider id is what makes ModelProvider.ApiKey a live projection rather
    // than a stored field. Bind it and read it back.
    var liveProvider = providerStore.Load().Single(item => item.Id == "orcarouter");
    liveProvider.Credential = roundTrip;
    if (liveProvider.ApiKey != "sk-secret-123") throw new Exception("ModelProvider.ApiKey did not project from its credential.");
    Console.WriteLine("PASS: ModelProvider.ApiKey projects from the provider's credential.");

    // Credential shape: the secret is what makes a credential usable, and it must survive the JSON/OS-store
    // split whichever provenance produced it. Provenance itself is metadata the UI never has to branch on —
    // a pasted key and a browser sign-in are the same kind of thing, which is the point of the one-to-one rule.
    var oauthCredential = new ProviderCredential
    {
        Id = "orcarouter-oauth",
        ProviderId = "orcarouter",
        Label = "Work",
        Source = CredentialSources.OAuth,
        AccountId = "acct-42",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
    oauthCredential.Secret = "sk-yoex-456";
    credentialStore.Save([credential, oauthCredential]);
    var both = credentialStore.Load().Where(item => item.ProviderId == "orcarouter").ToArray();
    if (both.Length != 2) throw new Exception("Expected two stored credential records.");
    if (both.Any(item => item.Secret is null or ""))
        throw new Exception("Credentials lost their secrets on round-trip.");
    if (both.Select(item => item.Source).Distinct().Count() != 2)
        throw new Exception("Provenance did not survive the round-trip, so the two would be indistinguishable.");
    Console.WriteLine("PASS: credential records round-trip with their secrets and their provenance intact.");

    // A provider owns a *list* of models with exactly one marked in use, and `Model` is a projection of that
    // mark. The projection is what lets the client factory and the pipeline stay unaware that a provider can
    // hold several models — so it is asserted rather than assumed.
    var multi = new ModelProvider { Id = "multi", Name = "Multi", BaseUrl = "https://example.test/v1" };
    if (multi.Models.Count != 0) throw new Exception("A fresh provider should start with no models.");
    if (multi.Model != "") throw new Exception("Model should project to empty when no model exists.");

    // The legacy single-value setter is the compatibility path for a providers.json written before the list
    // existed: assigning it must produce a list with that one model marked in use.
    multi.Model = "gpt-5.1-codex-mini";
    if (multi.Models.Count != 1 || !multi.Models[0].InUse)
        throw new Exception("Assigning Model should synthesize a one-entry list marked in use.");
    if (multi.Model != "gpt-5.1-codex-mini") throw new Exception("Model did not project back the assigned value.");

    // Assigning the same name again must not stack a duplicate row. This is the guarded setter's job, and the
    // deserialization path depends on it (a file carries both "Model" and "Models").
    multi.Model = "gpt-5.1-codex-mini";
    if (multi.Models.Count != 1) throw new Exception("Re-assigning the same name should not add a second model.");

    // A hand-edited file can carry several models with none marked. Normalize is what repairs it, and without
    // this the provider would send an empty model name and fail with a confusing 400.
    var unmarked = new ModelProvider
    {
        Id = "unmarked",
        Models = [new ProviderModel { Name = "a" }, new ProviderModel { Name = "b" }],
    };
    unmarked.Normalize();
    if (unmarked.Models.Count(model => model.InUse) != 1 || !unmarked.Models[0].InUse)
        throw new Exception("Normalize should mark exactly the first model when none is marked.");

    // Two marks (a hand-merge) collapse to the first; blanks and case-insensitive duplicates are dropped.
    var messy = new ModelProvider
    {
        Id = "messy",
        Models =
        [
            new ProviderModel { Name = "  a  ", InUse = true },
            new ProviderModel { Name = "A" },
            new ProviderModel { Name = "b", InUse = true },
            new ProviderModel { Name = "   " },
        ],
    };
    messy.Normalize();
    if (messy.Models.Count != 2) throw new Exception($"Normalize should drop blanks and duplicates, got {messy.Models.Count}.");
    if (messy.Models[0].Name != "a") throw new Exception("Normalize should trim names.");
    if (messy.Models.Count(model => model.InUse) != 1 || !messy.Models[0].InUse)
        throw new Exception("Normalize should keep only the first mark.");
    Console.WriteLine("PASS: a provider holds a model list with exactly one in use, and Model projects it.");

    // The JSON round trip is the path that actually exercises the setter ordering: a file written by an older
    // build carries "model" as a string, a newer one carries "models". Both must load to the same state.
    //
    // The options must mirror ProviderStore's: it reads with PropertyNameCaseInsensitive, because the file is
    // camelCase while the model is PascalCase. Deserializing with the defaults silently produces an *empty*
    // object (no exception), which is how a test can "pass" while reading nothing at all.
    var fileJson = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    var legacyJson = """{"id":"legacy-models","name":"Legacy","baseUrl":"https://example.test/v1","model":"only-one"}""";
    var fromLegacy = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>(legacyJson, fileJson)
                     ?? throw new Exception("Legacy provider JSON failed to deserialize.");
    fromLegacy.Normalize();
    if (fromLegacy.Models.Count != 1 || fromLegacy.Model != "only-one")
        throw new Exception($"A legacy single-model file should load as a one-entry list. [models={fromLegacy.Models.Count} model='{fromLegacy.Model}']");

    var newJson = """{"id":"new-models","name":"New","baseUrl":"https://example.test/v1","models":[{"name":"x","inUse":true},{"name":"y"}]}""";
    var fromNew = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>(newJson, fileJson)
                  ?? throw new Exception("Provider JSON with a models array failed to deserialize.");
    fromNew.Normalize();
    if (fromNew.Models.Count != 2 || fromNew.Model != "x")
        throw new Exception("A file with a models array should keep both and project the marked one.");

    // Both keys present (what a file looks like after the first save by a mixed build): the setter must not
    // duplicate the model the array already carries. This is the ordering hazard the guarded setter exists for,
    // so it is driven through the *real* load path too — not only the in-memory setter.
    var bothJson = """{"id":"both","name":"Both","baseUrl":"https://example.test/v1","model":"x","models":[{"name":"x","inUse":true},{"name":"y"}]}""";
    var fromBoth = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>(bothJson, fileJson)
                   ?? throw new Exception("Provider JSON with both keys failed to deserialize.");
    fromBoth.Normalize();
    if (fromBoth.Models.Count != 2)
        throw new Exception($"A file carrying both keys should not duplicate the model, got {fromBoth.Models.Count}.");
    if (fromBoth.Model != "x") throw new Exception("The marked model should win over the legacy scalar.");

    // And the round trip through the store itself, so the case-insensitive options above cannot drift from the
    // real ones without this failing.
    var modelRoot = Path.Combine(root, "model-roundtrip");
    Directory.CreateDirectory(modelRoot);
    var modelStore = new ProviderStore(modelRoot);
    modelStore.Save([new ModelProvider
    {
        Id = "saved",
        Name = "Saved",
        BaseUrl = "https://example.test/v1",
        Models = [new ProviderModel { Name = "first", InUse = true }, new ProviderModel { Name = "second" }],
    }]);
    var reloaded = modelStore.Load().Single();
    if (reloaded.Models.Count != 2 || reloaded.Model != "first")
        throw new Exception($"A saved model list should round-trip, got {reloaded.Models.Count} / '{reloaded.Model}'.");

    // A hand-written file with the models in reverse order and the second one marked must honour the mark
    // rather than defaulting to the first entry.
    File.WriteAllText(Path.Combine(modelRoot, "ai", "providers.json"),
        """[{"id":"hand","name":"Hand","baseUrl":"https://example.test/v1","models":[{"name":"a"},{"name":"b","inUse":true}]}]""");
    var handEdited = modelStore.Load().Single();
    if (handEdited.Model != "b")
        throw new Exception($"A hand-edited file should honour the marked model, got '{handEdited.Model}'.");
    Console.WriteLine("PASS: legacy, new and mixed providers.json shapes all load to the same model list.");

    // ── Key validation is declared per provider, and absence means "do not check" ──
    // The load-bearing property is the negative one: a provider with no declaration must report
    // "unsupported" rather than "rejected", because a gateway that closes /models would otherwise have a
    // working key refused. Asserting only the positive case would let a fail-closed default through.
    void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        Console.WriteLine("PASS: " + name);
    }

    var manifest = AiProviderManifest.Load();
    Assert(manifest.Count > 0, "the built-in provider manifest loads");

    var probed = manifest.Where(entry => entry.KeyValidation is not null).ToArray();
    Assert(probed.Length > 0, $"at least one preset declares a key probe (actual {probed.Length})");
    Assert(probed.All(entry => entry.KeyValidation!.IsKnown),
        "every declared probe is one Hub knows how to run");
    Assert(probed.All(entry => entry.AuthMethods.Contains(ProviderAuthMethods.ApiKey)),
        "only providers that take a key declare a probe");

    // A keyless endpoint has nothing to probe, and must not claim otherwise.
    Assert(manifest.Where(entry => !entry.AuthMethods.Contains(ProviderAuthMethods.ApiKey))
            .All(entry => entry.KeyValidation is null),
        "a provider that needs no key declares no probe");

    // The probe path must stay relative: an absolute URL would let a hand-edited manifest aim the check at
    // a host of its choosing, which is a request-forgery primitive rather than a validation feature.
    Assert(!new AiProviderKeyValidation { Type = AiProviderKeyValidation.HttpGetModels, Path = "https://evil.test/steal" }.IsKnown,
        "a probe path pointing at another host is rejected");
    Assert(!new AiProviderKeyValidation { Type = AiProviderKeyValidation.HttpGetModels, Path = "//evil.test/models" }.IsKnown,
        "a protocol-relative probe path is rejected");
    Assert(!new AiProviderKeyValidation { Type = "postTheKeySomewhere", Path = "/models" }.IsKnown,
        "an unknown probe type is rejected rather than ignored at run time");

    // The probe must reach the provider as a ModelProvider, since that is what the check reads at run time.
    var deepseek = AiProviderManifest.CreateBuiltIn("deepseek")!;
    Assert(deepseek.KeyValidation is { IsKnown: true },
        "a declared probe survives the trip from manifest entry to provider");
    Assert(AiProviderManifest.CreateBuiltIn("ollama")!.KeyValidation is null,
        "a keyless provider arrives with no probe, which is what makes it skip validation");

    Console.WriteLine("PASS: key validation is opt-in per provider and fails open when undeclared.");
    return;
}
if (args.Contains("--check-ai-sessions"))
{
    // Conversation model: title is derived from the first user turn, and the first line only.
    var conversation = Conversation.Create("orcarouter");
    if (conversation.Mode != ChatModes.Agent)
        throw new Exception($"A new conversation should start in the default agent mode, got '{conversation.Mode}'.");
    Console.WriteLine("PASS: new conversations default to agent mode.");
    conversation.Append(ChatTurn.User("How do I add a sprite?\nSecond line ignored"));
    conversation.Append(ChatTurn.Assistant("Use Sprite::create."));
    if (conversation.Title != "How do I add a sprite?") throw new Exception($"Wrong derived title: {conversation.Title}");
    if (Conversation.DeriveTitle(new string('a', 80)).Length != 49) throw new Exception("Long title was not truncated to 48 chars plus ellipsis.");
    Console.WriteLine("PASS: conversation derives its title from the first user turn.");

    // Persistence: save, reload, and the index must agree without reading message bodies.
    var store = new ConversationStore(root);
    store.Save(conversation);
    var reloaded = store.Load(conversation.Id) ?? throw new Exception("Saved conversation did not reload.");
    if (reloaded.Messages.Count != 2 || reloaded.Messages[1].Text != "Use Sprite::create.") throw new Exception("Conversation turns did not round-trip.");
    var listed = store.List().Single(summary => summary.Id == conversation.Id);
    if (listed.MessageCount != 2 || listed.Title != conversation.Title) throw new Exception("Index entry disagrees with the conversation.");
    Console.WriteLine("PASS: ConversationStore round-trips turns and maintains the index.");

    // What is on disk has to stay readable: the default JSON encoder escapes quotes, backticks, angle
    // brackets and every non-ASCII character, turning a Chinese transcript into escaped-code-unit soup.
    var readable = Conversation.Create("orcarouter");
    readable.Append(ChatTurn.User("列出当前 Hub 登记的项目"));
    readable.Append(ChatTurn.Assistant("| 项目 | 状态 |\n|---|---|\n| HelloCpp | `Configured` | \"ok\" +1 'x' <b>"));
    store.Save(readable);
    var savedText = File.ReadAllText(Path.Combine(root, "ai", "sessions", readable.Id + ".json"));
    if (!savedText.Contains("项目", StringComparison.Ordinal)
        || !savedText.Contains("`Configured`", StringComparison.Ordinal)
        || !savedText.Contains("\\\"ok\\\"", StringComparison.Ordinal)
        || !savedText.Contains("<b>", StringComparison.Ordinal)
        || savedText.Contains("\\u00", StringComparison.Ordinal))
        throw new Exception("The session file escaped characters that should stay literal.");
    store.Delete(readable.Id);
    Console.WriteLine("PASS: session files keep CJK, backticks, quotes and angle brackets readable.");

    conversation.Mode = ChatModes.Plan;
    conversation.ReasoningEffort = ChatReasoningEfforts.High;
    conversation.Messages.Add(ChatTurn.User("Inspect this file", "file content"));
    store.Save(conversation);
    reloaded = store.Load(conversation.Id) ?? throw new Exception("Conversation with mode/context did not reload.");
    if (reloaded.Mode != ChatModes.Plan || reloaded.ReasoningEffort != ChatReasoningEfforts.High
        || reloaded.Messages[^1].AttachedContext != "file content")
        throw new Exception("Composer settings or attached context did not round-trip.");
    Console.WriteLine("PASS: Conversation mode, reasoning effort, and attached context persist.");

    // Branch provenance is optional on purpose: a branch records where it was cut, and a session written
    // before the field existed must still load (the store ignores absent properties).
    var branch = Conversation.Create("orcarouter");
    branch.Append(ChatTurn.User("从这条提问分叉"));
    branch.BranchSourceId = conversation.Id;
    branch.BranchSourceIndex = 1;
    store.Save(branch);
    var reloadedBranch = store.Load(branch.Id) ?? throw new Exception("Branched conversation did not reload.");
    if (reloadedBranch.BranchSourceId != conversation.Id || reloadedBranch.BranchSourceIndex != 1)
        throw new Exception("Branch provenance did not round-trip through the session file.");
    File.WriteAllText(Path.Combine(root, "ai", "sessions", "legacy-no-provenance.json"),
        """{"Id":"legacy-no-provenance","Title":"legacy","ProviderId":"orcarouter","ModelName":"","Messages":[]}""");
    if (store.Load("legacy-no-provenance") is not { BranchSourceId: null, BranchSourceIndex: null })
        throw new Exception("A session file predating branch provenance no longer loads.");
    store.Delete("legacy-no-provenance");
    store.Delete(branch.Id);
    Console.WriteLine("PASS: branch provenance round-trips, and pre-branch session files still load.");

    // A hostile id may not escape the sessions directory.
    var escaped = false;
    try { store.Delete("../../evil"); } catch (ArgumentException) { escaped = true; }
    if (!escaped && File.Exists(Path.Combine(root, "evil.json"))) throw new Exception("Conversation id escaped the sessions directory.");
    Console.WriteLine("PASS: ConversationStore sanitizes ids (no path traversal).");

    // Deletion removes both the file and the index entry.
    store.Delete(conversation.Id);
    if (store.Load(conversation.Id) is not null) throw new Exception("Deleted conversation still loads.");
    if (store.List().Any(summary => summary.Id == conversation.Id)) throw new Exception("Deleted conversation is still listed.");
    Console.WriteLine("PASS: ConversationStore deletes conversations and their index entries.");

    // Pinned sessions sort above newer ones, and the pin survives an index round-trip.
    var older = Conversation.Create("orcarouter");
    older.Append(ChatTurn.User("older"));
    older.UpdatedAt = DateTimeOffset.Now.AddHours(-1);
    store.Save(older);
    var newer = Conversation.Create("orcarouter");
    newer.Append(ChatTurn.User("newer"));
    store.Save(newer);
    if (store.List()[0].Id != newer.Id) throw new Exception("The most recently updated conversation did not sort first.");
    older.Pinned = true;
    store.Save(older);
    var byPin = store.List();
    if (byPin[0].Id != older.Id || !byPin[0].Pinned) throw new Exception("A pinned conversation did not sort first, or lost its pin through the index.");
    Console.WriteLine("PASS: ConversationStore sorts pinned conversations first.");
    store.Delete(older.Id);
    store.Delete(newer.Id);

    // ContextTrimmer: system turns survive, oldest turns are dropped, and order is preserved.
    var history = new List<ChatTurn> { ChatTurn.System("You are Axmol's assistant.") };
    for (var index = 0; index < 20; index++) history.Add(ChatTurn.User($"message number {index} " + new string('x', 300)));
    var trimmed = ContextTrimmer.Trim(history, budget: 400);
    if (trimmed.Count == 0) throw new Exception("Trimmer produced an empty prompt.");
    if (trimmed[0].Role != ChatRoles.System) throw new Exception("Trimmer dropped the system turn.");
    if (trimmed.Count >= history.Count) throw new Exception("Trimmer did not drop anything under a tight budget.");
    var kept = trimmed.Where(turn => turn.Role == ChatRoles.User).ToList();
    if (kept[^1].Text != history[^1].Text) throw new Exception("Trimmer did not keep the newest turn.");
    if (trimmed.Select(ContextTrimmer.EstimateTokens).Sum() > 470) throw new Exception("Trimmer exceeded the budget by more than one message.");
    Console.WriteLine("PASS: ContextTrimmer keeps the system turn and the newest turns within budget.");

    // A single oversized message must not produce an empty prompt.
    var oversize = ContextTrimmer.Trim([ChatTurn.User(new string('y', 4000))], budget: 10);
    if (oversize.Count != 1) throw new Exception("Trimmer dropped an oversized newest turn, leaving no prompt.");
    Console.WriteLine("PASS: ContextTrimmer keeps at least the newest turn.");
    if (ContextTrimmer.EstimateTokens(ChatTurn.User("question", "attached file contents"))
        <= ContextTrimmer.EstimateTokens("question"))
        throw new Exception("Context estimator ignored attached file contents.");
    Console.WriteLine("PASS: ContextTrimmer accounts for attached context.");

    var openAiProvider = AiProviderManifest.CreateBuiltIn("openai")!;
    var deepSeekProvider = AiProviderManifest.CreateBuiltIn("deepseek")!;
    if (!ModelCatalog.SupportsReasoningEffort(openAiProvider, "gpt-6.1-sol")
        || ModelCatalog.SupportsReasoningEffort(deepSeekProvider, "deepseek-flash")
        || ModelCatalog.SupportsReasoningEffort(openAiProvider, "unknown-model"))
        throw new Exception("Reasoning support should be explicit, model-scoped, and unknown by default.");
    var openAiReasoning = ModelCatalog.ReasoningFor(openAiProvider, "gpt-6.1-sol");
    if (openAiReasoning is not { DefaultEffort: ChatReasoningEfforts.Low }
        || !openAiReasoning.Efforts.SequenceEqual(["low", "medium", "high", "xhigh", "max", "ultra"]))
        throw new Exception("GPT-6.1-Sol's declared reasoning choices or default were not loaded.");
    var parsedReasoning = ModelList.ParseMetadata(Encoding.UTF8.GetBytes(
        """{"object":"list","data":[{"id":"deepseek-flash","effort":{"supported_levels":["low","high","max"],"default_level":"high"}},{"id":"deepseek-v4-pro","effort":{"supported_levels":["low","high","max"],"default_level":"high"}}]}"""));
    if (!parsedReasoning.Models.SequenceEqual(["deepseek-flash", "deepseek-v4-pro"])
        || !parsedReasoning.ReasoningModels["deepseek-flash"].Efforts.SequenceEqual(["low", "high", "max"])
        || parsedReasoning.ReasoningModels["deepseek-flash"].DefaultEffort != "high")
        throw new Exception("DeepSeek /models effort metadata was not parsed as declared.");
    deepSeekProvider.ReasoningModels["deepseek-flash"] = new AiModelReasoning
    {
        Efforts = [.. parsedReasoning.ReasoningModels["deepseek-flash"].Efforts],
        DefaultEffort = parsedReasoning.ReasoningModels["deepseek-flash"].DefaultEffort,
        RequestOptions = deepSeekProvider.ReasoningModels["deepseek-flash"].RequestOptions,
    };
    if (!ModelCatalog.SupportsReasoningEffort(deepSeekProvider, "deepseek-flash", ChatReasoningEfforts.Max)
        || ModelCatalog.SupportsReasoningEffort(deepSeekProvider, "deepseek-flash", ChatReasoningEfforts.Medium))
        throw new Exception("DeepSeek effort choices should match the live /models metadata.");
    Console.WriteLine("PASS: reasoning efforts come from explicit model metadata, including DeepSeek /models.");

    // Pipeline: turn <-> ChatMessage conversion is lossless, and streaming yields the fake text.
    var turns = new List<ChatTurn> { ChatTurn.System("sys"), ChatTurn.User("hello"), ChatTurn.Assistant("hi"), new(ChatRoles.Tool, "result", DateTimeOffset.Now) };
    var wireMessages = ChatPipeline.ToChatMessages(turns);
    if (wireMessages.Count != 4) throw new Exception("ToChatMessages lost or added messages.");
    for (var index = 0; index < turns.Count; index++)
        if (wireMessages[index].Text != turns[index].Text) throw new Exception($"Conversion lost text at index {index}.");
    if (wireMessages[3].Role != ChatRole.Tool) throw new Exception("Tool role did not map to ChatRole.Tool.");
    var toolTurns = new[]
    {
        ChatTurn.FunctionCall("call-1", "get_projects", "{\"filter\":\"active\"}"),
        ChatTurn.FunctionResult("call-1", "[{\"name\":\"Demo\"}]"),
    };
    var toolMessages = ChatPipeline.ToChatMessages(toolTurns);
    if (toolMessages[0].Contents.Single() is not FunctionCallContent
        || toolMessages[1].Contents.Single() is not FunctionResultContent)
        throw new Exception("Persisted tool protocol did not convert back to function call/result contents.");
    var attachedMessage = ChatPipeline.ToChatMessage(ChatTurn.User("Question", "attached reference"));
    if (!attachedMessage.Text.Contains("attached reference", StringComparison.Ordinal))
        throw new Exception("Attached context was not included in the model-facing user message.");
    Console.WriteLine("PASS: ChatPipeline converts turns to messages losslessly.");

    var fake = new FakeChatClient(["Hel", "lo!"]);
    var pipeline = new ChatPipeline(fake);
    var streamed = new StringBuilder();
    var provider = AiProviderManifest.CreateBuiltIn("orcarouter")!;
    provider.MaxContextTokens = 8192;
    foreach (var provider2 in new[] { provider })
        await foreach (var chunk in pipeline.SendAsync(provider2, turns))
            streamed.Append(chunk);
    if (streamed.ToString() != "Hello!") throw new Exception($"Streamed text was '{streamed}' instead of 'Hello!'.");
    if (fake.LastMessages is null || fake.LastMessages.Count != 4) throw new Exception("Pipeline did not forward the full trimmed history.");
    Console.WriteLine("PASS: ChatPipeline streams assistant text through the fake client.");

    var reasoningClient = new FakeChatClient(["ok"]);
    await foreach (var _ in new ChatPipeline(reasoningClient).SendAsync(
                       deepSeekProvider,
                       [ChatTurn.User("test")],
                       reasoningEffort: ChatReasoningEfforts.Max,
                       modelName: "deepseek-flash")) { }
    var rawReasoningOptions = reasoningClient.LastOptions?.RawRepresentationFactory?.Invoke(reasoningClient)
        as OpenAI.Chat.ChatCompletionOptions;
#pragma warning disable SCME0001 // Assert the provider-specific JSON extension passed to the OpenAI adapter.
    var reasoningPatch = rawReasoningOptions?.Patch.ToString();
#pragma warning restore SCME0001
    if (reasoningPatch is null
        || !reasoningPatch.Contains("/reasoning_effort", StringComparison.Ordinal)
        || !reasoningPatch.Contains("\"max\"", StringComparison.Ordinal)
        || !reasoningPatch.Contains("/thinking", StringComparison.Ordinal)
        || !reasoningPatch.Contains("\"enabled\"", StringComparison.Ordinal))
        throw new Exception($"DeepSeek reasoning effort and thinking mode were not sent as raw request fields: {reasoningPatch ?? "<null>"}");
    Console.WriteLine("PASS: DeepSeek effort and thinking settings reach the OpenAI-compatible request.");

    var defaultDeepSeekClient = new FakeChatClient(["ok"]);
    await foreach (var _ in new ChatPipeline(defaultDeepSeekClient).SendAsync(
                       deepSeekProvider,
                       [ChatTurn.User("test")],
                       modelName: "deepseek-flash")) { }
    if (defaultDeepSeekClient.LastOptions?.Reasoning?.Effort != ReasoningEffort.High)
        throw new Exception("DeepSeek Auto did not honor the /models default effort.");
    Console.WriteLine("PASS: DeepSeek Auto uses the default effort declared by /models.");

    var defaultOpenAiClient = new FakeChatClient(["ok"]);
    await foreach (var _ in new ChatPipeline(defaultOpenAiClient).SendAsync(
                       openAiProvider,
                       [ChatTurn.User("test")],
                       modelName: "gpt-6.1-sol")) { }
    if (defaultOpenAiClient.LastOptions?.Reasoning?.Effort != ReasoningEffort.Low)
        throw new Exception("GPT-6.1-Sol Auto did not use the declared low default.");
    Console.WriteLine("PASS: GPT-6.1-Sol Auto uses its manifest default effort.");

    var extraHighClient = new FakeChatClient(["ok"]);
    await foreach (var _ in new ChatPipeline(extraHighClient).SendAsync(
                       openAiProvider,
                       [ChatTurn.User("test")],
                       reasoningEffort: ChatReasoningEfforts.XHigh,
                       modelName: "gpt-6.1-sol")) { }
    if (extraHighClient.LastOptions?.Reasoning?.Effort != ReasoningEffort.ExtraHigh)
        throw new Exception("GPT-6.1-Sol xhigh was not mapped to Microsoft.Extensions.AI ExtraHigh.");
    Console.WriteLine("PASS: GPT-6.1-Sol xhigh maps to the OpenAI adapter's ExtraHigh effort.");

    var ultraClient = new FakeChatClient(["ok"]);
    await foreach (var _ in new ChatPipeline(ultraClient).SendAsync(
                       openAiProvider,
                       [ChatTurn.User("test")],
                       reasoningEffort: ChatReasoningEfforts.Ultra,
                       modelName: "gpt-6.1-sol")) { }
    var rawUltraOptions = ultraClient.LastOptions?.RawRepresentationFactory?.Invoke(ultraClient)
        as OpenAI.Chat.ChatCompletionOptions;
#pragma warning disable SCME0001
    var ultraPatch = rawUltraOptions?.Patch.ToString();
#pragma warning restore SCME0001
    if (ultraPatch is null || !ultraPatch.Contains("\"ultra\"", StringComparison.Ordinal))
        throw new Exception("GPT-6.1-Sol ultra was not forwarded as an API effort value.");
    Console.WriteLine("PASS: GPT-6.1-Sol ultra is forwarded through the provider-specific request field.");

    // The pipeline applies the budget it is given: an oversized history reaches the client trimmed.
    var longHistory = new List<ChatTurn>();
    for (var index = 0; index < 40; index++) longHistory.Add(ChatTurn.User($"turn {index} " + new string('z', 400)));
    var tiny = AiProviderManifest.CreateBuiltIn("orcarouter")!;
    tiny.MaxContextTokens = 300;
    await foreach (var _ in new ChatPipeline(fake).SendAsync(tiny, longHistory)) { }
    if (fake.LastMessages is null || fake.LastMessages.Count >= longHistory.Count) throw new Exception("Pipeline did not trim the oversized history before calling the client.");
    Console.WriteLine("PASS: ChatPipeline trims oversized history before the model call.");

    var toolClient = new ToolLoopChatClient();
    var startedTools = 0;
    var completedTools = 0;
    var toolResult = new StringBuilder();
    var toolIdentity = "";
    var readOnlyTool = AIFunctionFactory.Create(
        (Func<string>)(() => "[{\"name\":\"Demo\"}]"),
        new AIFunctionFactoryOptions
        {
            Name = "get_projects",
            Description = "Return registered projects.",
        });
    await foreach (var chunk in new ChatPipeline(toolClient).SendAsync(
                       provider,
                       [ChatTurn.User("List projects")],
                       tools: [readOnlyTool],
                       onToolStarted: info =>
                       {
                           startedTools++;
                           // The transcript is written from this triple, so a call the pipeline cannot identify
                           // is a call whose result can never be paired with it.
                           toolIdentity = $"{info.Name}|{info.CallId}|{info.ArgumentsJson}";
                           return Task.CompletedTask;
                       },
                       onToolCompleted: (_, result, failed) =>
                       {
                           if (failed) throw new Exception("Read-only tool unexpectedly failed.");
                           completedTools++;
                           toolResult.Append(result);
                           return Task.CompletedTask;
                       }))
        streamed.Append(chunk);
    if (toolClient.CallCount != 2 || startedTools != 1 || completedTools != 1
        || toolResult.ToString() != "[{\"name\":\"Demo\"}]")
        throw new Exception($"Tool call → execution → result → final answer loop did not complete. Result handed back: {toolResult}");
    if (toolIdentity != "get_projects|call-1|{}")
        throw new Exception($"The pipeline reported the call as 「{toolIdentity}」.");
    if (!toolClient.SecondRequest.Any(message => message.Contents.Any(content => content is FunctionCallContent))
        || !toolClient.SecondRequest.Any(message => message.Contents.Any(content => content is FunctionResultContent)))
        throw new Exception("The tool loop did not send the function call and result back to the model.");
    if (!toolClient.LastOptions!.Tools!.Any(tool => tool.Name == "get_projects"))
        throw new Exception("The allowed read-only tool was not exposed in chat options.");
    streamed.Clear();
    Console.WriteLine("PASS: ChatPipeline completes the read-only function-call loop.");

    // What the model streamed before asking for a call is stored on the call turn, and must come back as the
    // one assistant message it was sent as: text then call in a single message, not two assistant messages.
    var spoken = ChatPipeline.ToChatMessages([
        ChatTurn.User("改之前先看一眼"),
        ChatTurn.FunctionCall("c1", "get_projects", "{}", "先说的话"),
    ]);
    var spokenAssistant = spoken.Single(message => message.Role == ChatRole.Assistant);
    if (spoken.Count != 2 || spokenAssistant.Contents.Count != 2
        || spokenAssistant.Contents[0] is not TextContent { Text: "先说的话" }
        || spokenAssistant.Contents[1] is not FunctionCallContent { CallId: "c1" })
        throw new Exception("The text streamed before a tool call did not replay as one assistant message.");
    var bareCall = ChatPipeline.ToChatMessages([ChatTurn.FunctionCall("c2", "get_projects", "{}")]);
    if (bareCall.Single().Contents.Count != 1 || bareCall.Single().Contents[0] is not FunctionCallContent)
        throw new Exception("A call turn without leading text gained an empty text content.");
    Console.WriteLine("PASS: pre-call text replays inside the assistant's tool-call message.");

    // Cancellation propagates as OperationCanceledException.
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = false;
    try { await foreach (var _ in new ChatPipeline(new FakeChatClient(["x"], honorCancellation: true)).SendAsync(provider, [ChatTurn.User("hi")], cancellationToken: cancellation.Token)) { } }
    catch (OperationCanceledException) { cancelled = true; }
    if (!cancelled) throw new Exception("Cancellation was not propagated.");
    Console.WriteLine("PASS: ChatPipeline propagates cancellation.");
    return;
}
if (args.Contains("--check-ai-memory"))
{
    var memoryRoot = Path.Combine(root, "memory");
    if (Directory.Exists(memoryRoot)) Directory.Delete(memoryRoot, recursive: true);
    var projectDir = Path.Combine(memoryRoot, "project");
    var dataRoot = Path.Combine(memoryRoot, "data");
    Directory.CreateDirectory(projectDir);
    Directory.CreateDirectory(dataRoot);

    if (MemoryStore.RootFor(MemoryScope.Project, projectDir, dataRoot) != Path.Combine(projectDir, ".agents", "memory"))
        throw new Exception("Project memory does not live in .agents/memory inside the workspace.");
    if (MemoryStore.RootFor(MemoryScope.Global, null, dataRoot) != Path.Combine(dataRoot, "ai", "memory"))
        throw new Exception("Global memory does not live beside the other ai/ state.");
    if (MemoryStore.RootFor(MemoryScope.Project, null, dataRoot) is not null)
        throw new Exception("Project memory invented a root without a workspace.");
    if (MemoryStore.IndexFileName(MemoryScope.Project) == MemoryStore.IndexFileName(MemoryScope.Global))
        throw new Exception("The project index would overwrite a global-style MEMORY.md.");

    // A repository may already keep its own memory. Hub reads it and must never rewrite it.
    var projectRoot = MemoryStore.RootFor(MemoryScope.Project, projectDir, dataRoot)!;
    Directory.CreateDirectory(projectRoot);
    var foreignIndex = Path.Combine(projectRoot, "MEMORY.md");
    const string foreignText = "# 人工维护的项目记忆\n\n这一行不属于 Hub，不能被改写。\n";
    File.WriteAllText(foreignIndex, foreignText);

    var first = MemoryStore.Write(projectRoot, MemoryScope.Project, "build-conventions.md",
        "构建约定", "本工程用 Ninja 而不是 MSBuild", "project", "构建走 1k/1kiss.ps1，不要直接调 msbuild。", append: false);
    var second = MemoryStore.Write(projectRoot, MemoryScope.Project, "shading-rules.md",
        "着色器规则", "axslcc 输出的 SPIR-V 必须过一遍 spirv-val", "decision", "着色器改完必须重编。", append: false);
    if (!first.Written || !second.Written) throw new Exception($"A topic write was refused: {first.Message} {second.Message}");

    var indexText = File.ReadAllText(Path.Combine(projectRoot, "AXHUB.md"));
    if (!indexText.Contains("[构建约定](topics/build-conventions.md)") || !indexText.Contains("本工程用 Ninja"))
        throw new Exception($"The derived index does not match the topic frontmatter:{Environment.NewLine}{indexText}");
    if (File.ReadAllText(foreignIndex) != foreignText)
        throw new Exception("Writing a memory topic rewrote the repository's own MEMORY.md.");
    if (MemoryStore.ReadText(projectRoot, MemoryScope.Project, "MEMORY.md") != foreignText)
        throw new Exception("A project's existing MEMORY.md was not readable.");
    if (MemoryStore.ReadText(projectRoot, MemoryScope.Project, "build-conventions.md") is not { } body
        || !body.Contains("1kiss.ps1") || body.Contains("---"))
        throw new Exception("Reading a topic did not return its body alone.");

    // Append keeps one file with both facts; replace leaves only the new one.
    MemoryStore.Write(projectRoot, MemoryScope.Project, "build-conventions.md", "", "", "", "补充：构建目录是 build-hub。", append: true);
    var appended = MemoryStore.ReadText(projectRoot, MemoryScope.Project, "build-conventions.md")!;
    if (!appended.Contains("1kiss.ps1") || !appended.Contains("build-hub"))
        throw new Exception("Appending replaced the topic instead of adding to it.");
    MemoryStore.Write(projectRoot, MemoryScope.Project, "build-conventions.md", "", "", "", "只剩这一条。", append: false);
    var replaced = MemoryStore.ReadText(projectRoot, MemoryScope.Project, "build-conventions.md")!;
    if (replaced.Contains("1kiss.ps1") || !replaced.Contains("只剩这一条"))
        throw new Exception("A replace left the old body behind.");

    foreach (var bad in new[] { "../evil.md", "MEMORY.md", "AXHUB.md", "Build.md", "no-extension", "a--b.md", "-lead.md", "C:\\evil.md", "" })
        if (MemoryStore.Write(projectRoot, MemoryScope.Project, bad, "t", "d", "note", "内容", append: false).Written)
            throw new Exception($"'{bad}' was accepted as a memory topic name.");
    if (MemoryStore.Write(projectRoot, MemoryScope.Project, "ok.md", "t", "d", "note", "   ", append: false).Written)
        throw new Exception("An empty memory write was accepted.");
    if (MemoryStore.Write(null, MemoryScope.Project, "ok.md", "t", "d", "note", "内容", append: false).Written)
        throw new Exception("A project memory write succeeded with no workspace.");
    if (!MemoryStore.Write(MemoryStore.RootFor(MemoryScope.Global, null, dataRoot), MemoryScope.Global, "user-prefers-chinese.md",
            "语言偏好", "回复用中文", "user", "用户要求中文回复。", append: false).Written)
        throw new Exception("Global memory refused a write with no workspace bound.");
    if (!File.ReadAllText(Path.Combine(dataRoot, "ai", "memory", "MEMORY.md")).Contains("语言偏好"))
        throw new Exception("The global index was not written as MEMORY.md.");

    // The index goes into the system prompt on every turn, so its size is bounded — and an entry that did not
    // fit has to be counted rather than silently dropped, or the model cannot know to ask for it.
    var manyRoot = Path.Combine(memoryRoot, "many");
    for (var index = 0; index < 60; index++)
        MemoryStore.Write(manyRoot, MemoryScope.Project, $"topic-{index:D2}.md", $"标题 {index:D2}",
            new string('描', 40), "note", "内容", append: false);
    var many = MemoryStore.Index(manyRoot);
    if (many.Count != 60) throw new Exception($"Only {many.Count} of 60 topics reached the index.");
    var rendered = MemoryStore.RenderIndex(many);
    if (rendered.Length > MemoryStore.MaxIndexCharacters + 32)
        throw new Exception($"The rendered index grew past its cap: {rendered.Length} characters.");
    if (!rendered.Contains("…(+")) throw new Exception("An index that dropped entries did not say how many.");
    if (!File.ReadAllText(Path.Combine(manyRoot, "AXHUB.md")).Contains("…(+"))
        throw new Exception("The index written to disk is not the truncated one.");
    Console.WriteLine("PASS: memory topics persist, the index is derived, and a foreign MEMORY.md survives untouched.");

    var logDay = new DateTimeOffset(2026, 10, 6, 9, 30, 0, TimeSpan.Zero);
    var logFile = MemoryLog.FileFor(projectDir, logDay);
    if (!logFile.EndsWith(Path.Combine(".agents", "memory", "2026-10-06.md"), StringComparison.OrdinalIgnoreCase))
        throw new Exception($"The daily log path is wrong: {logFile}");

    var runLines = MemoryLog.LinesForRun(new MemoryRunSummary("abcdef12-3456", "修构建", "auto",
        ["read_file（允许）", "file_write（批准）"], "完成", Compacted: true), logDay);
    if (!MemoryLog.Append(logFile, runLines).Written) throw new Exception("The first log append failed.");
    var quietLines = MemoryLog.LinesForRun(new MemoryRunSummary("99999999-0000", "闲聊", "ask", [], null, false), logDay);
    if (!quietLines.Any(line => line.Contains("没有调用工具"))) throw new Exception("A run with no tools logged nothing about itself.");

    // Two sessions share one daily file, so the append has to survive being concurrent rather than merely sequential.
    await Task.WhenAll(
        Task.Run(() => { for (var index = 0; index < 20; index++) MemoryLog.Append(logFile, [$"- A{index:D3}"]); }),
        Task.Run(() => { for (var index = 0; index < 20; index++) MemoryLog.Append(logFile, [$"- B{index:D3}"]); }));
    var logged = File.ReadAllText(logFile);
    var lost = Enumerable.Range(0, 20)
        .SelectMany(index => new[] { $"A{index:D3}", $"B{index:D3}" })
        .Where(marker => !logged.Contains(marker, StringComparison.Ordinal))
        .ToList();
    if (lost.Count > 0) throw new Exception($"Parallel appends lost {lost.Count} lines, for example {lost[0]}.");

    var capped = Path.Combine(memoryRoot, "capped.md");
    MemoryLogAppend last = default;
    for (var index = 0; index < 400; index++) last = MemoryLog.Append(capped, [new string('z', 200)], 4096);
    if (last.Written || !File.ReadAllText(capped).Contains("上限") || new FileInfo(capped).Length > 4096 + 200)
        throw new Exception($"The log cap did not hold (written={last.Written}, size={new FileInfo(capped).Length}).");
    Console.WriteLine("PASS: the daily log survives parallel sessions and stops at its cap.");

    Directory.Delete(memoryRoot, recursive: true);
    return;
}
if (args.Contains("--check-ai-context"))
{
    // The diff is what the user approves, so headers and caps are asserted as text: a wrong hunk header is not
    // cosmetic, it is a preview that does not describe what lands on disk.
    if (FileDiff.Unified("same\n", "same\n", "a.txt").Length != 0)
        throw new Exception("Identical text produced a diff.");

    var created = FileDiff.Unified("", "one\ntwo\n", "src/new.cpp");
    if (!created.Contains("--- /dev/null") || !created.Contains("+++ b/src/new.cpp")
        || !created.Contains("@@ -0,0 +1,2 @@") || !created.Contains("+one") || !created.Contains("+two"))
        throw new Exception($"A created file did not diff as a pure addition:{Environment.NewLine}{created}");

    var deleted = FileDiff.Unified("one\ntwo\n", "", "src/gone.cpp");
    if (!deleted.Contains("+++ /dev/null") || !deleted.Contains("@@ -1,2 +0,0 @@") || !deleted.Contains("-one"))
        throw new Exception($"A deleted file did not diff as a pure removal:{Environment.NewLine}{deleted}");

    var nine = string.Join('\n', Enumerable.Range(1, 9).Select(index => $"line{index}")) + "\n";
    var middle = FileDiff.Unified(nine, nine.Replace("line5\n", "LINE5\n"), "src/a.cpp");
    if (!middle.Contains("@@ -2,7 +2,7 @@") || !middle.Contains("-line5") || !middle.Contains("+LINE5")
        || middle.Split('\n').Count(line => line.StartsWith(' ')) != 6)
        throw new Exception($"A one-line edit did not produce one hunk with three context lines each side:{Environment.NewLine}{middle}");

    var crlf = FileDiff.Unified("a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\n", "src/crlf.cpp");
    if (crlf.Contains('\r') || !crlf.Contains("-b") || !crlf.Contains("+B"))
        throw new Exception($"A CRLF file leaked carriage returns into the diff:{Environment.NewLine}{crlf.Replace("\r", "\\r")}");

    var twenty = string.Join('\n', Enumerable.Range(1, 20).Select(index => $"l{index}")) + "\n";
    var twoEdits = twenty.Replace("l3\n", "L3\n").Replace("l18\n", "L18\n");
    if (FileDiff.Unified(twenty, twoEdits, "src/b.cpp").Split("@@").Length - 1 != 4)
        throw new Exception("Two distant edits did not produce two hunks.");

    var huge = FileDiff.Unified(
        string.Join('\n', Enumerable.Range(1, 5000).Select(index => $"old{index}")) + "\n",
        string.Join('\n', Enumerable.Range(1, 5000).Select(index => $"new{index}")) + "\n",
        "src/huge.cpp");
    if (!huge.Contains("@@ -1,5000 +1,5000 @@") || !huge.Contains("more changed lines") || huge.Split('\n').Length > 63)
        throw new Exception($"A whole-file rewrite was not capped ({huge.Split('\n').Length} lines).");

    // The prefix/suffix trim is what keeps a small edit inside the edit-script bound. Without it this degrades
    // to "the file was rewritten", which is the negative control for the assertion below.
    var big = string.Join('\n', Enumerable.Range(1, 2000).Select(index => $"row{index}")) + "\n";
    if (!FileDiff.Unified(big, big.Replace("row1000\n", "ROW1000\n"), "src/big.cpp").Contains("@@ -997,7 +997,7 @@"))
        throw new Exception("A one-line edit in a 2000-line file lost its hunk.");
    Console.WriteLine("PASS: the unified diff names its hunks correctly and stays inside the preview cap.");

    // An independent recount is the oracle: CutPoint and SplitsToolCall must agree with a second, dumb
    // implementation at every boundary, or one of them is reasoning about a shape the other does not handle.
    static bool OrphanCallBefore(IReadOnlyList<ChatTurn> messages, int cut)
    {
        for (var i = 0; i < cut; i++)
        {
            if (messages[i].Role != ChatRoles.Assistant || messages[i].ToolCallId is not { Length: > 0 } callId) continue;
            var answered = false;
            for (var j = 0; j < cut; j++)
                if (messages[j].Role == ChatRoles.Tool && messages[j].ToolCallId == callId) { answered = true; break; }
            if (!answered) return true;
        }
        return false;
    }

    var transcript = new List<ChatTurn> { ChatTurn.User("修一下构建") };
    for (var step = 0; step < 6; step++)
    {
        transcript.Add(ChatTurn.FunctionCall($"c{step}", "read_file", """{"path":"a.cpp"}"""));
        transcript.Add(ChatTurn.FunctionResult($"c{step}", "ok"));
        transcript.Add(ChatTurn.Assistant($"第 {step} 步做完了"));
    }
    for (var keep = 0; keep <= 6; keep++)
    {
        var cut = ContextCompression.CutPoint(transcript, keep);
        if (cut > transcript.Count - keep) throw new Exception($"The cut kept fewer than the requested {keep} turns.");
        if (OrphanCallBefore(transcript, cut)) throw new Exception($"keepRecent={keep} archived an unanswered tool call.");
    }
    for (var boundary = 1; boundary < transcript.Count; boundary++)
        if (ContextCompression.SplitsToolCall(transcript, boundary) != OrphanCallBefore(transcript, boundary))
            throw new Exception($"SplitsToolCall disagreed with the oracle at cut {boundary}.");

    // The case that matters: "everything but the newest four" lands between a call and its result, and the cut
    // has to walk back. This is exactly what the workspace's one-step backoff cannot do.
    var pairStraddling = new List<ChatTurn>
    {
        ChatTurn.User("看一下"), ChatTurn.FunctionCall("c0", "read_file", "{}"), ChatTurn.FunctionResult("c0", "ok"),
        ChatTurn.User("再看"), ChatTurn.FunctionCall("c1", "read_file", "{}"), ChatTurn.FunctionResult("c1", "ok"),
    };
    if (!OrphanCallBefore(pairStraddling, pairStraddling.Count - 4))
        throw new Exception("The fixture no longer straddles a call/result pair, so it proves nothing.");
    if (ContextCompression.CutPoint(pairStraddling, 4) != 1)
        throw new Exception($"The cut did not walk back off the tool call (landed at {ContextCompression.CutPoint(pairStraddling, 4)}).");
    if (ContextCompression.CutPoint([], 4) != 0 || ContextCompression.CutPoint([ChatTurn.User("hi")], 4) != 0)
        throw new Exception("A short transcript did not collapse to a zero cut.");
    Console.WriteLine("PASS: the compression cut never archives half of a tool call.");

    // ── The bounds on one tool result ──
    if (ToolResultCap.TokensFor(8192) != 1024 || ToolResultCap.TokensFor(128000) != ToolResultCap.MaximumTokens
        || ToolResultCap.TokensFor(1000) != ToolResultCap.MinimumTokens)
        throw new Exception("The result cap is not proportional to the window.");
    var longResult = string.Concat(Enumerable.Repeat("0123456789", 12000));
    var cappedResult = ToolResultCap.Apply(longResult, 8192);
    if (cappedResult.Length > ToolResultCap.TokensFor(8192) * ContextTrimmer.CharactersPerToken + 64
        || !cappedResult.Contains("characters truncated")
        || !cappedResult.StartsWith(longResult[..512], StringComparison.Ordinal)
        || !cappedResult.EndsWith(longResult[^512..], StringComparison.Ordinal))
        throw new Exception($"The capped result lost its head, its tail, or its marker ({cappedResult.Length} characters).");
    if (ToolResultCap.Apply("short", 8192) != "short") throw new Exception("A result inside the cap was rewritten.");

    // ── The window cannot open on an orphaned tool result ──
    var trimmedOrphan = ContextTrimmer.Trim(
        [ChatTurn.FunctionResult("c0", "上一轮的答案"), ChatTurn.User("新问题"), ChatTurn.Assistant("回答")], 4096);
    if (trimmedOrphan.Count == 0 || trimmedOrphan[0].Role == ChatRoles.Tool)
        throw new Exception("Trimming kept an orphaned tool result at the front of the window.");
    var heavyCall = ChatTurn.FunctionCall("c1", "file_write", """{"old_string":"aaaa","new_string":"bbbb"}""");
    if (ContextTrimmer.EstimateTokens(heavyCall) <= ContextTrimmer.EstimateTokens(heavyCall with { ToolArguments = null }))
        throw new Exception("A tool call's arguments were not charged against the budget.");
    Console.WriteLine("PASS: one tool result cannot push the conversation out of the window.");

    // ── Inside the loop, results shrink but messages never disappear ──
    var loopMessages = new List<ChatMessage>
    {
        new(ChatRole.System, "system rules"),
        new(ChatRole.User, "第一个问题"),
    };
    for (var index = 0; index < 6; index++)
    {
        loopMessages.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"call-{index}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{index}.cpp" })]));
        loopMessages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{index}", new string('x', 4000))]));
    }
    var elided = ToolLoopContextGuard.Elide(loopMessages, 1200);
    if (elided.Count != loopMessages.Count) throw new Exception("Elision removed messages instead of shrinking them.");
    if (!elided.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId)
             .SequenceEqual(elided.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId)))
        throw new Exception("Elision broke the call/result pairing.");
    if (elided[0].Text != "system rules" || elided[1].Text != "第一个问题")
        throw new Exception("Elision touched the system prompt or the question being answered.");
    var resultTexts = elided.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
        .Select(result => result.Result?.ToString() ?? "").ToList();
    if (resultTexts.Take(2).Any(text => !text.Contains("elided"))
        || resultTexts.Skip(2).Any(text => text.Contains("elided") || text.Length != 4000))
        throw new Exception($"Elision did not spare exactly the newest {ToolLoopContextGuard.KeepRecentResults} results.");

    // ── The loop ends on its own, and stays inside the window while it runs ──
    var insistent = new InsistentToolClient();
    var probeProvider = new ModelProvider
        { Id = "probe", Name = "probe", BaseUrl = "https://example.invalid/v1", Model = "probe" };
    var probeTool = AIFunctionFactory.Create((Func<string>)(() => new string('y', 20000)),
        new AIFunctionFactoryOptions { Name = "get_projects", Description = "probe" });
    var answered = new StringBuilder();
    string? capFailure = null;
    try
    {
        await foreach (var chunk in new ChatPipeline(insistent)
                           .SendAsync(probeProvider, [ChatTurn.User("一直调用工具")], tools: [probeTool]))
            answered.Append(chunk);
    }
    catch (Exception exception)
    {
        capFailure = exception.GetType().Name + ": " + exception.Message;
    }
    if (capFailure is not null) throw new Exception($"The iteration cap surfaced as an exception instead of an answer: {capFailure}");
    if (insistent.CallCount != ChatPipeline.MaximumToolIterations + 1)
        throw new Exception($"The loop made {insistent.CallCount} requests instead of {ChatPipeline.MaximumToolIterations} tool rounds plus one final answer.");
    if (answered.Length == 0) throw new Exception("The loop hit its cap without leaving the user any text.");
    if (insistent.LastOptions?.AllowMultipleToolCalls != false)
        throw new Exception("A request went out still allowing several tool calls in one response.");
    if (insistent.LastOptions?.Tools is not null)
        throw new Exception("The final request still offered tools, so the model could ask for another round.");

    // Every round returned 20 000 characters against an 8192-token window, so by the last request the older
    // results must have been elided — with the messages themselves still there, pairing intact.
    var lastResults = insistent.LastRequest.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
        .Select(result => result.Result?.ToString() ?? "").ToList();
    if (lastResults.Count < ChatPipeline.MaximumToolIterations)
        throw new Exception($"The final request carried only {lastResults.Count} tool results.");
    if (!lastResults.Any(text => text.Contains("elided")) || !lastResults.Any(text => !text.Contains("elided")))
        throw new Exception("The loop either elided nothing or elided the result it is reasoning about.");
    if (lastResults.Any(text => text.Length > ToolResultCap.TokensFor(ContextTrimmer.DefaultBudgetTokens) * ContextTrimmer.CharactersPerToken + 64))
        throw new Exception("A tool result reached the model without being capped.");
    if (!insistent.LastRequest.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId)
             .SequenceEqual(insistent.LastRequest.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId)))
        throw new Exception("The loop's own request lost the call/result pairing.");
    Console.WriteLine("PASS: the tool loop stops at its cap, caps every result, and elides the old ones.");
    return;
}
if (args.Contains("--check-ai-workspace"))
{
    // The sandbox is a pure function over strings plus one link check, so it is asserted here rather than
    // through a screen: a guardrail that only exists in the UI layer does not exist in `full` mode.
    var guardRoot = Path.Combine(root, "guard");
    // A junction left behind by an earlier failed run makes Directory.Delete(recursive: true) throw
    // UnauthorizedAccessException, so the link goes first — otherwise one red run poisons the next.
    var staleLink = Path.Combine(guardRoot, "ws", "link");
    if (Directory.Exists(staleLink)) Directory.Delete(staleLink, recursive: false);
    if (Directory.Exists(guardRoot)) Directory.Delete(guardRoot, recursive: true);
    var workspace = Path.Combine(guardRoot, "ws");
    Directory.CreateDirectory(Path.Combine(workspace, "src"));
    Directory.CreateDirectory(Path.Combine(workspace, ".git"));
    Directory.CreateDirectory(Path.Combine(workspace, "hubdata"));
    Directory.CreateDirectory(Path.Combine(workspace, "engine-2.11.5", "core"));
    File.WriteAllText(Path.Combine(workspace, "src", "hello.cpp"), "int main() { return 0; }\r\n");
    var guards = new WorkspaceGuards(Path.Combine(workspace, "hubdata"), [Path.Combine(workspace, "engine-2.11.5")]);

    var allowed = WorkspacePaths.ResolveRead(workspace, "src/hello.cpp", guards);
    if (!allowed.IsAllowed || allowed.Full != Path.Combine(workspace, "src", "hello.cpp")
        || allowed.Relative != Path.Combine("src", "hello.cpp"))
        throw new Exception($"A plain relative path was not resolved inside the workspace ({allowed.Verdict}).");

    // Forward slashes are what a model emits; they must resolve without becoming a second code path.
    if (!WorkspacePaths.ResolveRead(workspace, "src/hello.cpp", guards).IsAllowed)
        throw new Exception("A forward-slash relative path was refused.");

    foreach (var escaping in new[] { "../outside.cpp", "src/../../outside.cpp", "/etc/passwd", "C:\\Windows\\win.ini", "" })
    {
        var verdict = WorkspacePaths.ResolveWrite(workspace, escaping, guards).Verdict;
        if (verdict != WorkspacePathVerdict.EscapesWorkspace)
            throw new Exception($"'{escaping}' was answered with {verdict} instead of EscapesWorkspace.");
    }

    // Protected roots are refused identically by both verbs: the one rule no approval mode relaxes.
    foreach (var path in new[] { ".git/config", "hubdata/state.json", "engine-2.11.5/core/axmol.h" })
    {
        if (WorkspacePaths.ResolveWrite(workspace, path, guards).Verdict != WorkspacePathVerdict.ProtectedRoot
            || WorkspacePaths.ResolveRead(workspace, path, guards).Verdict != WorkspacePathVerdict.ProtectedRoot)
            throw new Exception($"'{path}' was not refused as a protected root by both verbs.");
    }

    // The allowlist is a WRITE rule: reading a build log is legitimate, writing one is not.
    File.WriteAllText(Path.Combine(workspace, "src", "build.log"), "error: nope\n");
    if (WorkspacePaths.ResolveRead(workspace, "src/build.log", guards).Verdict != WorkspacePathVerdict.Allowed)
        throw new Exception("A build log inside the workspace could not be read.");
    foreach (var blocked in new[] { "src/build.log", "src/tool.exe", "src/notes.nitwit" })
        if (WorkspacePaths.ResolveWrite(workspace, blocked, guards).Verdict != WorkspacePathVerdict.ExtensionNotAllowed)
            throw new Exception($"Writing '{blocked}' was not refused by the extension allowlist.");

    // A file that is absent is a write target, not a read target; a directory is neither.
    if (WorkspacePaths.ResolveWrite(workspace, "src/new.cpp", guards).Verdict != WorkspacePathVerdict.Allowed)
        throw new Exception("A new .cpp file was refused as a write target.");
    if (WorkspacePaths.ResolveRead(workspace, "src/new.cpp", guards).Verdict != WorkspacePathVerdict.NotAFile
        || WorkspacePaths.ResolveRead(workspace, "src", guards).Verdict != WorkspacePathVerdict.NotAFile
        || WorkspacePaths.ResolveWrite(workspace, "src", guards).Verdict != WorkspacePathVerdict.NotAFile)
        throw new Exception("A missing file or a directory was not reported as NotAFile.");
    // Names without a usable extension are still writable when the name itself is known build metadata.
    foreach (var name in new[] { "CMakeLists.txt", ".gitignore", "Makefile" })
        if (WorkspacePaths.ResolveWrite(workspace, name, guards).Verdict != WorkspacePathVerdict.Allowed)
            throw new Exception($"{name} was refused as a write target.");

    if (WorkspacePaths.ResolveRead(null, "src/hello.cpp", guards).Verdict != WorkspacePathVerdict.NoWorkspace
        || WorkspacePaths.ResolveRead(Path.Combine(guardRoot, "nope"), "a.cpp", guards).Verdict != WorkspacePathVerdict.MissingWorkspace)
        throw new Exception("A missing workspace was not reported as such.");
    if (WorkspacePaths.VerifyCommandRoot(null, guards) != WorkspacePathVerdict.NoWorkspace
        || WorkspacePaths.VerifyCommandRoot(workspace, guards) != WorkspacePathVerdict.Allowed
        || WorkspacePaths.VerifyCommandRoot(Path.Combine(workspace, "hubdata"), guards) != WorkspacePathVerdict.ProtectedRoot)
        throw new Exception("The command root was not verified against the same guards.");

    // The refusal text is part of the contract: a sentence without "do not retry" buys nine more attempts.
    foreach (var verdict in new[]
             {
                 WorkspacePathVerdict.NoWorkspace, WorkspacePathVerdict.EscapesWorkspace,
                 WorkspacePathVerdict.ProtectedRoot, WorkspacePathVerdict.ReparsePoint,
                 WorkspacePathVerdict.ExtensionNotAllowed,
             })
    {
        var sentence = WorkspacePaths.ResultFor(verdict, "x");
        if (sentence.Length == 0 || !sentence.Contains("retry", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"The refusal for {verdict} does not tell the model to stop retrying.");
    }
    Console.WriteLine("PASS: workspace paths stay inside the sandbox and refuse protected roots.");

    if (OperatingSystem.IsWindows())
    {
        var outside = Path.Combine(guardRoot, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(workspace, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                   { UseShellExecute = false, CreateNoWindow = true })!)
            mklink.WaitForExit();
        if (!Directory.Exists(link)) throw new Exception("The junction fixture could not be created.");
        try
        {
            // SafePath only normalizes strings, so containment passes; the link walk is what catches this.
            if (WorkspacePaths.ResolveWrite(workspace, "link/evil.cpp", guards).Verdict != WorkspacePathVerdict.ReparsePoint
                || WorkspacePaths.ResolveRead(workspace, "link/evil.cpp", guards).Verdict != WorkspacePathVerdict.ReparsePoint)
                throw new Exception("A write through a directory junction was not refused.");
        }
        finally
        {
            // Removed even when the assertion above fails: a dangling junction breaks the next run's cleanup.
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        }
        Console.WriteLine("PASS: a directory junction inside the workspace cannot be written through.");
    }

    // The anchored edit: every verdict is a different thing the model must do next, so each is asserted.
    if (FileEdit.Apply(null, "", "hello\n", false) is not { Verdict: FileEditVerdict.Created, Updated: "hello\r\n" or "hello\n" })
        throw new Exception("Creating a file with an empty anchor did not report Created.");
    if (FileEdit.Apply(null, "anchor", "x", false).Verdict != FileEditVerdict.NotFound)
        throw new Exception("An anchor against a missing file was not NotFound.");
    if (FileEdit.Apply("existing", "", "x", false).Verdict != FileEditVerdict.AlreadyExists)
        throw new Exception("Creating over an existing file was not refused.");
    if (FileEdit.Apply("a\nb\nc\n", "zzz", "y", false).Verdict != FileEditVerdict.NotFound)
        throw new Exception("A missing anchor was not NotFound.");
    if (FileEdit.Apply("a\nb\na\n", "a", "z", false) is not { Verdict: FileEditVerdict.Ambiguous, Matches: 2 })
        throw new Exception("Two matches without replace_all was not Ambiguous.");
    if (FileEdit.Apply("a\nb\na\n", "a", "z", true) is not { Verdict: FileEditVerdict.Applied, Updated: "z\nb\nz\n", Matches: 2 })
        throw new Exception("replace_all did not replace every match.");
    if (FileEdit.Apply("a\nb\n", "b", "b", false).Verdict != FileEditVerdict.Unchanged)
        throw new Exception("An identical replacement was not Unchanged.");

    // Models emit \n; the file is CRLF. The edit must land anyway and must not convert the rest of the file.
    const string crlfFile = "int main() {\r\n    return 0;\r\n}\r\n";
    var crlfEdit = FileEdit.Apply(crlfFile, "    return 0;\n", "    return 1;\n", false);
    if (crlfEdit.Verdict != FileEditVerdict.Applied || crlfEdit.Updated != "int main() {\r\n    return 1;\r\n}\r\n")
        throw new Exception($"A CRLF file was not edited by an LF anchor ({crlfEdit.Verdict}: {crlfEdit.Updated.Replace("\r", "\\r")}).");
    if (!FileEdit.ResultFor(FileEditVerdict.Ambiguous, 3, "src/a.cpp").Contains("3 places"))
        throw new Exception("The ambiguous refusal does not say how many places matched.");
    Console.WriteLine("PASS: an anchored edit lands once, loudly, and never rewrites the file's line endings.");

    // Shell choice is per host and must be assertable on any host, so it is a pure function of two values.
    var windows = CommandShells.For("windows", pwshAvailable: false);
    // WindowsShell.PowerShell mixes separators on purpose (Path.Combine over a literal with '/'), so compare
    // normalized rather than asserting a spelling the Hub itself does not use.
    if (!windows.Executable.Replace('/', '\\').EndsWith("\\WindowsPowerShell\\v1.0\\powershell.exe", StringComparison.OrdinalIgnoreCase)
        || !windows.ArgumentsFor("dir").SequenceEqual(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "dir"]))
        throw new Exception($"Windows did not get the system PowerShell with Bypass: {windows.Executable} {string.Join(' ', windows.PrefixArguments)}");
    var unixPwsh = CommandShells.For("linux", pwshAvailable: true);
    if (unixPwsh.Executable != "pwsh" || unixPwsh.ArgumentsFor("ls").Contains("-ExecutionPolicy")
        || !unixPwsh.ArgumentsFor("ls").SequenceEqual(["-NoProfile", "-NonInteractive", "-Command", "ls"]))
        throw new Exception("Unix pwsh was given Windows-only arguments.");
    var unixSh = CommandShells.For("macos", pwshAvailable: false);
    if (unixSh.Executable != "/bin/sh" || !unixSh.ArgumentsFor("ls").SequenceEqual(["-c", "ls"]))
        throw new Exception("Unix without pwsh did not fall back to /bin/sh -c.");
    if (OperatingSystem.IsWindows() != CommandShells.ForCurrent().Executable.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
        throw new Exception("ForCurrent disagreed with the host it is running on.");
    Console.WriteLine("PASS: the command tool picks one shell per host and never mixes their arguments.");

    Directory.Delete(guardRoot, recursive: true);
    return;
}
if (args.Contains("--check-ai-tools"))
{
    // The tools are the part that touches the disk, so they are asserted here rather than through a window.
    // Every refusal is model-facing: one that does not say what to do instead becomes a retry loop nobody sees.
    var toolRoot = Path.Combine(root, "tools");
    if (Directory.Exists(toolRoot)) Directory.Delete(toolRoot, recursive: true);
    var workspace = Path.Combine(toolRoot, "ws");
    var dataRoot = Path.Combine(toolRoot, "data");
    var toolEngineRoot = Path.Combine(toolRoot, "engine");
    Directory.CreateDirectory(Path.Combine(workspace, "src"));
    Directory.CreateDirectory(dataRoot);
    Directory.CreateDirectory(toolEngineRoot);
    File.WriteAllText(Path.Combine(workspace, "src", "main.cpp"), "int main()\n{\n    return 0;\n}\n");
    File.WriteAllBytes(Path.Combine(workspace, "src", "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFE, 0xFF]);
    File.WriteAllText(Path.Combine(workspace, "src", "long.txt"),
        string.Concat(Enumerable.Range(1, 50).Select(index => $"line-{index:D2}\n")));

    var guards = new WorkspaceGuards(dataRoot, [toolEngineRoot]);
    var log = new HubLog(Path.Combine(toolRoot, "log"));
    string? appliedRoot = null;
    var scope = new WorkspaceToolScope(workspace, guards, dataRoot, "conversation-1", ["SUPERSECRET"], log,
        path =>
        {
            appliedRoot = path;
            return Task.FromResult($"Workspace set to {path}.");
        });
    var tools = new WorkspaceTools(scope);
    var homeless = new WorkspaceTools(new WorkspaceToolScope(null, guards, dataRoot, "c", [], null, null));

    // ── read_file: a window, no line numbers, and one sentence per way it can fail ──
    var whole = tools.ReadFile("src/main.cpp");
    if (!whole.Contains("lines 1-4 of 4 (UTF-8)") || !whole.Contains("int main()")
        || whole.Contains("\n1\t", StringComparison.Ordinal) || whole.Contains("\n1|", StringComparison.Ordinal))
        throw new Exception($"read_file did not return the file as a plain window:{Environment.NewLine}{whole}");
    var window = tools.ReadFile("src/long.txt", 10, 5);
    if (!window.Contains("lines 10-14 of 50") || !window.Contains("line-10") || !window.Contains("line-14")
        || window.Contains("line-15") || !window.Contains("offset=15"))
        throw new Exception($"The read window was wrong:{Environment.NewLine}{window}");
    if (!homeless.ReadFile("a.txt").Contains("no workspace directory", StringComparison.Ordinal))
        throw new Exception("read_file without a workspace did not say so.");    if (!tools.ReadFile("../outside.txt").Contains("Refused", StringComparison.Ordinal)
        || !tools.ReadFile("src/logo.png").Contains("not UTF-8", StringComparison.Ordinal))
        throw new Exception("read_file let an escaping path or a binary through.");
    File.WriteAllText(Path.Combine(workspace, "src", "huge.txt"), new string('x', WorkspaceTools.MaxReadFileBytes + 16));
    if (!tools.ReadFile("src/huge.txt").Contains("read limit", StringComparison.Ordinal))
        throw new Exception("An oversized file was read whole instead of being refused with a window hint.");
    Console.WriteLine("PASS: read_file returns a window of text only, and says what to do in every refusal.");

    // ── file_write: the five verdicts, the pre-image, and the same guards ──
    var edited = tools.FileWrite("src/main.cpp", "    return 0;", "    return 1;");
    if (!edited.Contains("Edited") || !File.ReadAllText(Path.Combine(workspace, "src", "main.cpp")).Contains("return 1;"))
        throw new Exception($"An anchored edit did not land:{Environment.NewLine}{edited}");
    var undoPath = edited.Contains("Undo copy: ") ? edited[(edited.IndexOf("Undo copy: ", StringComparison.Ordinal) + 11)..].Trim() : "";
    if (undoPath.Length == 0 || !File.Exists(undoPath) || !File.ReadAllText(undoPath).Contains("return 0;"))
        throw new Exception($"The pre-image was not kept before the write ('{undoPath}').");
    if (!tools.FileWrite("src/notes.md", "", "# 记录\n").Contains("Created"))
        throw new Exception("An empty anchor did not create the file.");
    if (!tools.FileWrite("src/main.cpp", "不存在的锚点", "x").Contains("was not found"))
        throw new Exception("A missing anchor was not reported as NotFound.");
    File.WriteAllText(Path.Combine(workspace, "src", "twice.txt"), "dup\ndup\n");
    if (!tools.FileWrite("src/twice.txt", "dup", "one").Contains("matched 2 places"))
        throw new Exception("An anchor matching twice was not reported as Ambiguous.");
    if (!tools.FileWrite("src/main.cpp", "    return 1;", "    return 1;").Contains("identical"))
        throw new Exception("A no-op edit was not reported as Unchanged.");
    if (!tools.FileWrite("src/main.cpp", "", "whole file").Contains("already exists"))
        throw new Exception("An empty anchor against an existing file did not refuse to overwrite it.");
    if (!tools.FileWrite("src/tool.exe", "", "MZ").Contains("not a text file")
        || !tools.FileWrite("../escape.cpp", "a", "b").Contains("Refused"))
        throw new Exception("file_write ignored the write allowlist or the sandbox.");
    if (File.ReadAllText(Path.Combine(workspace, "src", "twice.txt")) != "dup\ndup\n")
        throw new Exception("A refused edit changed the file anyway.");

    var undoRoot = ChatUndoStore.DirectoryFor(dataRoot, "conversation-1")!;
    for (var index = 0; index < ChatUndoStore.MaxFiles + 10; index++)
        ChatUndoStore.Store(dataRoot, "conversation-1", $"file-{index:D3}.txt", "content");
    if (Directory.GetFiles(undoRoot).Length > ChatUndoStore.MaxFiles)
        throw new Exception($"The undo directory grew past its bound ({Directory.GetFiles(undoRoot).Length} files).");
    if (ChatUndoStore.Store(null, "conversation-1", "a.txt", "x").Stored
        || ChatUndoStore.Store(dataRoot, "", "a.txt", "x").Stored)
        throw new Exception("An undo copy was written with no data root or no conversation.");
    Console.WriteLine("PASS: file_write edits once, keeps the pre-image, and leaves the file alone on every refusal.");

    // ── run_command: the header, redaction, both ends of a long output, and the idle timeout ──
    var echoed = await tools.RunCommand("echo key=SUPERSECRET");
    if (!echoed.Contains("cwd: ") || !echoed.Contains(workspace) || !echoed.Contains("exit: 0"))
        throw new Exception($"The command result did not name its shell, directory and exit code:{Environment.NewLine}{echoed}");
    if (echoed.Contains("SUPERSECRET", StringComparison.Ordinal) || !echoed.Contains("[REDACTED]", StringComparison.Ordinal))
        throw new Exception("A secret reached the transcript instead of being redacted.");
    if (!echoed.Contains($"full output in {log.FilePath}", StringComparison.Ordinal))
        throw new Exception("A truncated-by-policy result did not point at the log that has the whole thing.");

    File.WriteAllText(Path.Combine(workspace, "src", "big.txt"),
        string.Concat(Enumerable.Range(1, 4000).Select(index => $"row-{index:D4} padding padding padding\n")));
    var verbose = await tools.RunCommand(OperatingSystem.IsWindows() ? "Get-Content src/big.txt" : "cat src/big.txt");
    if (!verbose.Contains("characters of output omitted") || !verbose.Contains("row-0001") || !verbose.Contains("row-4000"))
        throw new Exception($"A long output was not shaped to both ends:{Environment.NewLine}{verbose[..Math.Min(400, verbose.Length)]}");

    var stalled = await tools.RunCommand(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 20" : "sleep 20", 1);
    if (!stalled.Contains("killed after 1s", StringComparison.Ordinal))
        throw new Exception($"An idle command was not killed by its timeout:{Environment.NewLine}{stalled}");
    if (!(await homeless.RunCommand("echo hi")).Contains("no workspace directory", StringComparison.Ordinal))
        throw new Exception("run_command without a workspace did not say so.");
    Console.WriteLine("PASS: run_command names its sandbox, redacts secrets, keeps both ends and dies when idle.");

    // ── set_workspace: choosing the sandbox is itself guarded ──
    if (!(await tools.SetWorkspace("relative/path")).Contains("not an absolute path", StringComparison.Ordinal)
        || !(await tools.SetWorkspace(Path.Combine(toolRoot, "nope"))).Contains("does not exist", StringComparison.Ordinal)
        || !(await tools.SetWorkspace(toolEngineRoot)).Contains("protected location", StringComparison.Ordinal))
        throw new Exception("set_workspace accepted a relative, a missing or a protected directory.");
    if (!(await tools.SetWorkspace(workspace)).Contains("Workspace set to") || appliedRoot != Path.GetFullPath(workspace))
        throw new Exception("A valid directory was not handed to the caller to persist.");

    // ── memory through the same tool surface ──
    if (!tools.MemoryWrite("project", "build-rules.md", "构建走 1kiss.ps1。", "构建约定", "改构建前先看", "project")
            .Contains("Saved topics/build-rules.md"))
        throw new Exception("A project memory write was refused.");
    if (tools.MemoryRead("project", "build-rules.md") != "构建走 1kiss.ps1。")
        throw new Exception("Reading back a memory topic did not return its body alone.");
    if (!tools.MemoryWrite("project", "build-rules.md", "补充一行。", "", "", "", "append").Contains("Saved")
        || tools.MemoryRead("project", "build-rules.md") != "构建走 1kiss.ps1。\n\n补充一行。")
        throw new Exception("Appending to a memory topic did not keep what was there.");
    if (!tools.MemoryRead("project", "AXHUB.md").Contains("构建约定"))
        throw new Exception("The derived project index was not readable through the tool.");
    if (!tools.MemoryWrite("global", "prefers-chinese.md", "回复用中文。", "语言偏好", "always", "user").Contains("Saved"))
        throw new Exception("A global memory write was refused with no workspace bound.");
    if (!tools.MemoryRead("neither", "x.md").Contains("neither project nor global")
        || !tools.MemoryWrite("project", "../evil.md", "x").Contains("not a memory topic name")
        || !homeless.MemoryWrite("project", "x.md", "内容").Contains("no workspace directory", StringComparison.Ordinal))
        throw new Exception("The memory tools accepted a bad scope, a traversing name or a missing workspace.");
    Console.WriteLine("PASS: the memory tools write both scopes, append, and refuse a bad name or scope.");

    // ── the frozen card text for every tool ──
    string Preview(string name, string json)
        => ToolPreviews.PreviewFor(name, json, scope, [workspace]);
    var writePreview = Preview("file_write", """{"path":"src/main.cpp","old_string":"    return 1;","new_string":"    return 2;"}""");
    if (!writePreview.Contains("--- a/src/main.cpp") || !writePreview.Contains("+    return 2;")
        || !writePreview.Contains("-    return 1;"))
        throw new Exception($"A write did not preview as a diff:{Environment.NewLine}{writePreview}");
    if (!Preview("file_write", """{"path":"../escape.cpp","old_string":"a","new_string":"b"}""").Contains("Refused"))
        throw new Exception("A write outside the workspace previewed as something other than a refusal.");
    if (!Preview("file_write", """{"path":"src/twice.txt","old_string":" absent ","new_string":"x"}""").Contains("was not found"))
        throw new Exception("A write that would change nothing previewed as a diff.");
    var commandPreview = Preview("run_command", """{"command":"cmake --build build","timeout_seconds":60}""");
    if (!commandPreview.Contains("cmake --build build") || !commandPreview.Contains(workspace)
        || !commandPreview.Contains("idle timeout 60s"))
        throw new Exception($"A command did not preview with its shell, directory and timeout:{Environment.NewLine}{commandPreview}");
    var workspacePreview = Preview("set_workspace", JsonSerializer.Serialize(new { path = workspace }));
    if (!workspacePreview.Contains("exists") || !workspacePreview.Contains("registered Hub project"))
        throw new Exception($"A workspace preview did not state the facts:{Environment.NewLine}{workspacePreview}");
    if (!Preview("set_workspace", JsonSerializer.Serialize(new { path = toolEngineRoot })).Contains("protected location"))
        throw new Exception("A protected directory did not preview as one.");
    if (!Preview("memory_write", """{"scope":"project","name":"a.md","mode":"append","content":"内容"}""")
            .Contains("memory_append · project · a.md"))
        throw new Exception("A memory write did not preview with its scope, name and mode.");
    if (Preview("read_file", """{"path":"src/main.cpp"}""") != "read_file · src/main.cpp"
        || Preview("memory_read", """{"scope":"global","name":"MEMORY.md"}""") != "memory_read · global · MEMORY.md"
        || Preview("没登记过的工具", "{}") != "没登记过的工具")
        throw new Exception("A read-only or unknown call did not preview as itself.");
    if (ToolPreviews.PreviewFor("file_write", "{ not json", scope) is not { Length: > 0 })
        throw new Exception("Arguments that do not parse produced no card text at all.");
    Console.WriteLine("PASS: every tool previews what approving it would actually do.");

    // ── the wire shape: every body binds, and every parameter name is the one the model is told to send ──
    // AIFunctionFactory exports parameters by name and applies no naming policy to them, so a serializer-level
    // snake_case policy renames the schema without renaming what the invoker looks for. The mismatch is invisible
    // at build time and arrives at run time as "missing required parameter", which reads like a model bug.
    var bound = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
    foreach (var (wireName, body) in new (string, Delegate)[]
             {
                 ("read_file", (Delegate)tools.ReadFile), ("file_write", tools.FileWrite),
                 ("run_command", tools.RunCommand), ("set_workspace", tools.SetWorkspace),
                 ("memory_read", tools.MemoryRead), ("memory_write", tools.MemoryWrite),
             })
        bound[wireName] = AIFunctionFactory.Create(body, new AIFunctionFactoryOptions { Name = wireName });

    var schema = string.Join("\n", bound.Values.Select(function => function.JsonSchema.GetRawText()));
    foreach (var expected in new[] { "old_string", "new_string", "replace_all", "timeout_seconds" })
        if (!schema.Contains(expected, StringComparison.Ordinal))
            throw new Exception($"The wire schema does not advertise '{expected}'.");
    foreach (var leaked in new[] { "oldString", "newString", "replaceAll", "timeoutSeconds" })
        if (schema.Contains(leaked, StringComparison.Ordinal))
            throw new Exception($"The wire schema advertises the C# spelling '{leaked}' instead of snake_case.");

    // And the names the schema advertises are the names an invocation actually accepts — the two halves of the
    // same contract, asserted together because only one of them fails loudly.
    var boundEdit = await bound["file_write"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
    {
        ["path"] = "src/twice.txt", ["old_string"] = "dup", ["new_string"] = "single", ["replace_all"] = true,
    }));
    if (!Convert.ToString(boundEdit)!.Contains("Edited") || File.ReadAllText(Path.Combine(workspace, "src", "twice.txt")) != "single\nsingle\n")
        throw new Exception($"A call using the schema's own parameter names did not bind ({boundEdit}).");
    Console.WriteLine("PASS: all six tools bind, and the schema names are the names a call is accepted by.");

    log.Write("self-check finished");
    Directory.Delete(toolRoot, recursive: true);
    return;
}
if (args.Contains("--check-ai-tool-policy"))
{
    // The permission model is one pure function over two small enums, so all twelve cells are asserted rather
    // than sampled: a transposed table is the difference between "auto lets a build run" and "auto stops at a
    // build", and neither reads as an error at compile time.
    var table = new (string Mode, ToolRisk Risk, bool Expected)[]
    {
        (ToolApprovalModes.Ask, ToolRisk.ReadOnly, false),
        (ToolApprovalModes.Ask, ToolRisk.WorkspaceWrite, true),
        (ToolApprovalModes.Ask, ToolRisk.SystemCommand, true),
        (ToolApprovalModes.Auto, ToolRisk.ReadOnly, false),
        (ToolApprovalModes.Auto, ToolRisk.WorkspaceWrite, false),
        (ToolApprovalModes.Auto, ToolRisk.SystemCommand, true),
        (ToolApprovalModes.Full, ToolRisk.ReadOnly, false),
        (ToolApprovalModes.Full, ToolRisk.WorkspaceWrite, false),
        (ToolApprovalModes.Full, ToolRisk.SystemCommand, false),
        // The assistant's own notes never ask, in any mode: a note that costs a card is a note never written.
        (ToolApprovalModes.Ask, ToolRisk.AssistantNote, false),
        (ToolApprovalModes.Auto, ToolRisk.AssistantNote, false),
        (ToolApprovalModes.Full, ToolRisk.AssistantNote, false),
    };
    foreach (var (mode, risk, expected) in table)
    {
        if (ToolApprovalPolicy.RequiresApproval(mode, risk) != expected)
            throw new Exception($"Approval decision for {mode} × {risk} was wrong.");
    }
    Console.WriteLine("PASS: the approval decision table asks exactly where it should.");

    // Fail closed on anything unrecognized, including null: the settings file is user-editable, and a newer
    // Hub may write a mode this build has never heard of.
    foreach (var unknown in new[] { null, "", "yolo", "ASK", "auto-approve" })
    {
        if (ToolApprovalModes.Normalize(unknown) != ToolApprovalModes.Ask)
            throw new Exception($"Unrecognized approval mode 「{unknown}」 did not fall back to ask.");
    }
    if (!ToolApprovalPolicy.RequiresApproval(null, ToolRisk.WorkspaceWrite))
        throw new Exception("A null approval mode let a workspace write through.");
    Console.WriteLine("PASS: an unrecognized approval mode falls back to asking.");

    // The per-session override rides on the session file, and a file written before approval existed must
    // still load — with the pending count reading as zero rather than throwing.
    var policyStore = new ConversationStore(root);
    var workspaceRoot = Path.Combine(root, "project");
    var policyConversation = Conversation.Create("orcarouter");
    policyConversation.Append(ChatTurn.User("写一个文件"));
    policyConversation.ApprovalMode = ToolApprovalModes.Auto;
    policyConversation.WorkspaceRoot = workspaceRoot;
    policyConversation.AutoApprovedTools.Add("file_write");
    policyConversation.Messages.Add(ChatTurn.FunctionCall("c1", "file_write", """{"path":"x.cpp"}""") with
    {
        ApprovalState = ChatApprovalStates.Pending,
        ApprovalPreview = "--- a/x.cpp\n+++ b/x.cpp\n+hi",
    });
    policyStore.Save(policyConversation);

    var policyReloaded = policyStore.Load(policyConversation.Id)
                         ?? throw new Exception("Session with approval settings did not reload.");
    if (policyReloaded.ApprovalMode != ToolApprovalModes.Auto || policyReloaded.WorkspaceRoot != workspaceRoot
        || policyReloaded.AutoApprovedTools.Count != 1 || policyReloaded.AutoApprovedTools[0] != "file_write")
        throw new Exception("The session's approval settings did not round-trip.");
    var pendingCall = policyReloaded.Messages[^1];
    if (pendingCall.ApprovalState != ChatApprovalStates.Pending
        || pendingCall.ApprovalPreview is not { Length: > 0 }
        || pendingCall.ToolName != "file_write")
        throw new Exception("A pending tool call lost its state, its preview, or its name.");
    var pendingSummary = policyStore.List().First(summary => summary.Id == policyConversation.Id);
    if (pendingSummary.PendingApprovals != 1)
        throw new Exception($"The index did not report the pending approval (reported {pendingSummary.PendingApprovals}).");

    File.WriteAllText(Path.Combine(root, "ai", "sessions", "legacy-no-approval.json"),
        """{"Id":"legacy-no-approval","Title":"legacy","ProviderId":"orcarouter","Messages":[{"Role":"assistant","Text":"","ToolCallId":"c1","ToolName":"get_projects"}]}""");
    if (policyStore.Load("legacy-no-approval") is not
        { ApprovalMode: null, WorkspaceRoot: null, AutoApprovedTools: { Count: 0 } } legacy)
        throw new Exception("A session file predating tool approval no longer loads.");
    if (legacy.Messages[0].ApprovalState is not null || legacy.Messages[0].ApprovalPreview is not null)
        throw new Exception("A tool call from before approval gained a state.");
    // The file was written by hand, so it is not in the index — what matters is that summarizing it, which is
    // what the index is built from, reports no pending decision rather than throwing.
    if (ConversationSummary.From(legacy).PendingApprovals != 0)
        throw new Exception("A legacy session advertised pending approvals it cannot have.");

    // A resolved call stops counting as pending, which is what keeps the badge from outliving the decision.
    var resolved = policyStore.Load(policyConversation.Id)!;
    resolved.Messages[^1] = resolved.Messages[^1] with { ApprovalState = ChatApprovalStates.Approved };
    policyStore.Save(resolved);
    if (policyStore.List().First(summary => summary.Id == policyConversation.Id).PendingApprovals != 0)
        throw new Exception("An approved call was still counted as pending in the index.");

    policyStore.Delete("legacy-no-approval");
    policyStore.Delete(policyConversation.Id);
    Console.WriteLine("PASS: approval fields round-trip, pending counts derive, and pre-approval sessions still load.");

    // The three answers a gate can give are asserted here, at the pipeline, rather than through a screen: this
    // is the layer where "the call did not run" is decided, and a UI built later can only be as correct as it is.
    var gateProvider = new ModelProvider { Id = "orcarouter", Name = "OrcaRouter", BaseUrl = "https://api.orcarouter.ai/v1" };
    // A function that objects to being run: every gate answer other than Allow has to keep it from executing.
    var gatedFunction = AIFunctionFactory.Create(
        (Func<string>)(() => throw new Exception("A gated tool ran without being allowed.")),
        new AIFunctionFactoryOptions
        {
            Name = "get_projects",
            Description = "Return registered projects.",
        });

    // Deny: the refusal has to reach the model, or the model asks again.
    var order = new List<string>();
    var denyCompleted = 0;
    var denyResult = "";
    var denyFailed = false;
    var denyClient = new ToolLoopChatClient();
    var denyAnswer = new StringBuilder();
    await foreach (var chunk in new ChatPipeline(denyClient).SendAsync(
                       gateProvider,
                       [ChatTurn.User("write a file")],
                       tools: [gatedFunction],
                       gate: (info, _) =>
                       {
                           order.Add("gate:" + info.Name);
                           return Task.FromResult(ChatPipeline.ToolGateOutcome.Deny);
                       },
                       onToolStarted: info =>
                       {
                           order.Add("started:" + info.Name);
                           return Task.CompletedTask;
                       },
                       onToolCompleted: (_, result, failed) =>
                       {
                           denyCompleted++;
                           denyResult = result;
                           denyFailed = failed;
                           return Task.CompletedTask;
                       }))
        denyAnswer.Append(chunk);
    // The call is recorded before the gate is asked, which is what lets a pending call be marked after the fact
    // instead of needing a side table of its own.
    if (string.Join(",", order) != "started:get_projects,gate:get_projects")
        throw new Exception($"The pipeline asked the gate in the wrong order: {string.Join(",", order)}.");
    if (denyCompleted != 1 || denyFailed != true || denyResult != ToolApprovalResults.Denied)
        throw new Exception($"A refused call was not reported to the model as a failure: 「{denyResult}」 (failed={denyFailed}).");
    if (denyClient.CallCount != 2 || denyAnswer.ToString() != "Projects loaded.")
        throw new Exception("A refusal stopped the reply loop instead of letting the model answer about it.");
    if (!denyClient.SecondRequest.Any(message => message.Contents.Any(content =>
            content is FunctionResultContent result && result.Result as string == ToolApprovalResults.Denied)))
        throw new Exception("The refusal the model was sent is not the refusal it was given.");
    Console.WriteLine("PASS: a denied call never runs, and the model is told it was refused.");

    // Pending: the stream ends, nothing is written as the call's result, and the placeholder stays inside.
    using var parkCancellation = new CancellationTokenSource();
    var parkCompleted = 0;
    var parkClient = new ToolLoopChatClient(honorCancellation: true);
    var parkAnswer = new StringBuilder();
    var parkThrew = false;
    try
    {
        await foreach (var chunk in new ChatPipeline(parkClient).SendAsync(
                           gateProvider,
                           [ChatTurn.User("write a file")],
                           tools: [gatedFunction],
                           gate: (_, _) =>
                           {
                               parkCancellation.Cancel();
                               return Task.FromResult(ChatPipeline.ToolGateOutcome.Pending);
                           },
                           onToolCompleted: (_, _, _) =>
                           {
                               parkCompleted++;
                               return Task.CompletedTask;
                           },
                           cancellationToken: parkCancellation.Token))
            parkAnswer.Append(chunk);
    }
    catch (OperationCanceledException)
    {
        parkThrew = true;
    }

    // Whether the turn ends by cancellation or by the stream simply stopping is the pipeline's choice; either is
    // how the caller learns to wait, and ChatWorkspace reads the run's phase rather than the exception for that.
    if (parkAnswer.Length > 0)
        throw new Exception($"Parking let the reply keep going (threw={parkThrew}): 「{parkAnswer}」.");
    if (parkCompleted != 0)
        throw new Exception("A parked call was written a tool result, which is a decision nobody made.");
    if (parkClient.SecondRequest.Count > 0)
        throw new Exception("The follow-up request went out with the approval placeholder: " + parkClient.SecondRequest.Count + " messages.");
    Console.WriteLine("PASS: a parked call ends the turn with no result, and its placeholder never reaches the model.");
    return;
}
if (args.Contains("--check-release-receipt"))
{
    var entry = new StateStore(root).Load().Projects.Single(p => p.Name == "HelloAndroidRelease");
    var config = AndroidReleaseSettings.PathFor(entry); var original = File.ReadAllText(config);
    var apk = AndroidPackageService.VerifiedApk(entry);
    try
    {
        var settings = AndroidReleaseSettings.Require(entry); settings.VersionCode++; settings.Save(entry);
        try { AndroidPackageService.VerifiedApk(entry); throw new Exception("Changed release version was deployable without rebuilding."); }
        catch (InvalidDataException) { Console.WriteLine("PASS: Real signed Release APK refuses deployment after release settings change."); }
    }
    finally { File.WriteAllText(config, original); }
    if (AndroidPackageService.VerifiedApk(entry) != apk) throw new Exception("Restoring release settings did not restore receipt validation.");
    var passwordFile = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cache/android/release-check-passwords.json")));
    var secrets = new[] { passwordFile.RootElement.GetProperty("StorePassword").GetString()!, passwordFile.RootElement.GetProperty("KeyPassword").GetString()! };
    var stage = AndroidPackageService.StageDirectory(entry);
    var files = new[] { config, Path.Combine(stage, "app/build.gradle"), Path.Combine(stage, "gradle.properties"), Path.Combine(stage, ".hub-apk.json") };
    if (files.Any(file => secrets.Any(secret => File.ReadAllText(file).Contains(secret)))) throw new Exception("Release password persisted in project or packaging files.");
    Console.WriteLine("PASS: Real release receipt matches selected settings and password values are absent from project, Gradle files and receipts.");
    return;
}
if (args.Contains("--check-release-debug-key"))
{
    var stateEntry = new StateStore(root).Load(); var toolsEntry = Path.Combine(root, "tools");
    var selectedEngine = stateEntry.Engines.Single(); var runnerEntry = new ProcessRunner(Console.WriteLine);
    var environmentEntry = new PlatformBuildService(runnerEntry).CreateEnvironment(selectedEngine, BuildTargets.Get("android-arm64"));
    var clone = Path.Combine(root, "cache/android/debug-rejection-check.jks");
    File.Copy(Path.Combine(root, "cache/android/debug.keystore"), clone, overwrite: false);
    var passwords = new AndroidSigningPasswords("android", "android");
    var renamed = await runnerEntry.RunAsync(Path.Combine(toolsEntry, "jdk/bin/keytool.exe"), ["-changealias", "-keystore", clone, "-alias", "androiddebugkey", "-destalias", "pretend-upload",
        "-storepass:env", "HUB_ANDROID_STORE_PASSWORD", "-keypass:env", "HUB_ANDROID_KEY_PASSWORD"], root, passwords.Environment(environmentEntry));
    if (renamed.ExitCode != 0) throw new Exception("Debug certificate fixture could not be prepared.");
    try
    {
        await new AndroidSigningService(runnerEntry, toolsEntry).ValidateAsync(new() { ApplicationId = "com.axmolhub.fixture", KeyAlias = "pretend-upload", KeystorePath = clone }, passwords, environmentEntry);
        throw new Exception("Renamed debug certificate was accepted for Release.");
    }
    catch (InvalidDataException ex) when (ex.Message.Contains("debug certificate")) { Console.WriteLine("PASS: Renaming the debug alias cannot bypass release certificate validation."); }
    finally { File.Delete(clone); }
    return;
}
if (args.Contains("--prepare-release-check"))
{
    var stateEntry = new StateStore(root); var hubEntry = stateEntry.Load();
    var selectedEngine = hubEntry.Engines.Single(); var toolsEntry = Path.Combine(root, "tools");
    var messagesEntry = new List<string>(); var runnerEntry = new ProcessRunner(message => { messagesEntry.Add(message); Console.WriteLine(message); });
    var environmentEntry = new PlatformBuildService(runnerEntry).CreateEnvironment(selectedEngine, BuildTargets.Get("android-arm64"));
    foreach (var name in new[] { "HOME", "TEMP" }) Directory.CreateDirectory(environmentEntry[name]);
    var serviceEntry = new ProjectService(runnerEntry, EngineCli(runnerEntry), new EnginePrebuiltState(root));
    var entry = hubEntry.Projects.FirstOrDefault(p => p.Name == "HelloAndroidRelease")
        ?? await serviceEntry.CreateAsync("HelloAndroidRelease", Path.Combine(root, "projects"), selectedEngine);
    var settings = new AndroidReleaseSettings { ApplicationId = "com.axmolhub.releasecheck", VersionCode = 7, VersionName = "0.1.3",
        KeyAlias = "releasecheck", KeystorePath = Path.Combine(root, "cache/android/release-check.jks") };
    var passwords = new AndroidSigningPasswords("Store-" + Guid.NewGuid().ToString("N"), "Key-" + Guid.NewGuid().ToString("N"));
    var signerEntry = new AndroidSigningService(runnerEntry, toolsEntry);
    await signerEntry.CreateAsync(settings, passwords, environmentEntry);
    var certificate = await signerEntry.ValidateAsync(settings, passwords, environmentEntry);
    async Task MustReject(Func<Task> action, string name)
    { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException) { Console.WriteLine("PASS: " + name); return; } throw new Exception("FAILED: " + name); }
    await MustReject(() => signerEntry.CreateAsync(settings, passwords, environmentEntry), "Existing keystore is never overwritten");
    await MustReject(() => signerEntry.ValidateAsync(settings, new("Incorrect-store-password", passwords.KeyPassword), environmentEntry), "Wrong keystore password is rejected");
    await MustReject(() => signerEntry.ValidateAsync(settings, new(passwords.StorePassword, "Incorrect-key-password"), environmentEntry), "Wrong private key password is rejected");
    var missingAlias = new AndroidReleaseSettings { ApplicationId = settings.ApplicationId, KeyAlias = "missing", KeystorePath = settings.KeystorePath };
    await MustReject(() => signerEntry.ValidateAsync(missingAlias, passwords, environmentEntry), "Missing alias is rejected");
    settings.Save(entry); BuildTargets.Select(entry, "android-arm64", "Release"); stateEntry.SaveProject(entry);
    var probe = await runnerEntry.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-Command", "[Console]::WriteLine($env:HUB_ANDROID_STORE_PASSWORD); [Console]::Error.WriteLine($env:HUB_ANDROID_KEY_PASSWORD)"], root,
        passwords.Environment(environmentEntry), sensitiveValues: passwords.SensitiveValues);
    if (probe.Output.Contains(passwords.StorePassword) || probe.Error.Contains(passwords.KeyPassword) || messagesEntry.Any(line => line.Contains(passwords.StorePassword) || line.Contains(passwords.KeyPassword))
        || File.ReadAllText(AndroidReleaseSettings.PathFor(entry)).Contains(passwords.StorePassword)) throw new Exception("Signing secret escaped redaction.");
    // 仅此维护者验收路径写入一次性测试凭据；产品从不保存密码，文件位于忽略的 artifacts 资料库。
    StateStore.WriteJson(Path.Combine(root, "cache/android/release-check-passwords.json"), new { passwords.StorePassword, passwords.KeyPassword });
    Console.WriteLine("PASS: New release key, private-key unlock, certificate SHA256, non-secret config persistence and stdout/stderr redaction. Certificate: " + certificate);
    return;
}
if (args.Contains("--check-android-verification"))
{
    var hub = new StateStore(root).Load(); var entry = hub.Projects.Single(p => p.Name == "HelloAndroid");
    var selectedEngine = hub.Engines.Single(e => e.Version == entry.Version && e.Channel == entry.Channel);
    var packageRunner = new ProcessRunner(Console.WriteLine);
    var environmentEntry = new PlatformBuildService(packageRunner).CreateEnvironment(selectedEngine, BuildTargets.Get(entry.Platform));
    var file = Path.Combine(AndroidPackageService.StageDirectory(entry), "gradle/verification-metadata.xml");
    var original = File.ReadAllText(file);
    try
    {
        var document = System.Xml.Linq.XDocument.Parse(original);
        var artifact = document.Descendants().Single(node => node.Name.LocalName == "component" && (string?)node.Attribute("group") == "com.android.tools.build" && (string?)node.Attribute("name") == "gradle")
            .Elements().Single(node => (string?)node.Attribute("name") == "gradle-8.11.1.jar");
        foreach (var hash in artifact.Elements().Where(node => node.Name.LocalName == "sha256")) hash.SetAttributeValue("value", new string('0', 64));
        document.Save(file);
        var command = new AndroidPackageService(packageRunner, Path.Combine(root, "tools")).GradleCommand(entry, environmentEntry, "--offline", "--dependency-verification=strict", "help");
        var rejected = await packageRunner.RunAsync(command.Executable, command.Arguments, command.WorkingDirectory, environmentEntry, timeout: TimeSpan.FromMinutes(2));
        if (rejected.ExitCode == 0 || !(rejected.Output + rejected.Error).Contains("Dependency verification failed")) throw new InvalidOperationException("Gradle did not reject the altered dependency checksum.");
        Console.WriteLine("PASS: Real Gradle rejects an altered AGP SHA-256 before executing build tasks.");
    }
    finally { File.WriteAllText(file, original); }
    return;
}
// `--prepare-android-verification` 已随构建委派退役：它用 Hub 自己的 gradle 编排重新生成
// `manifests/android-gradle-verification.xml`（依赖 Hub 托管的 JDK/gradle 与工程 staging）。
// 构建现在由引擎完成，该清单只能由引擎的 Android 流程重新产出。
if (args.Contains("--prepare-packaging"))
{
    var manifest = PackageManifest.Read(Path.GetFullPath("installer/packaging-manifest.json")).Packages.Single();
    using var clientEntry = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var runnerEntry = new ProcessRunner(Console.WriteLine);
    // DownloadManager 只有在摘要对得上时才返回路径，所以下面装的是已校验内容。
    var archive = await new DownloadManager(clientEntry, Console.WriteLine).DownloadAsync(new Uri(manifest.Url), manifest.Sha256, Path.Combine(root, "cache"));
    // NuGet 文件夹源要求 <id>.<version>.nupkg 这个命名，缓存里的名字是摘要。
    var feed = Path.Combine(root, "feed");
    Directory.CreateDirectory(feed);
    var package = Path.Combine(feed, manifest.Id + "." + manifest.Version + ".nupkg");
    File.Copy(archive, package, overwrite: true);
    var destination = Path.Combine(root, "vpk");
    // dotnet tool install 遇到已存在的工具会直接报错，先清掉让准备步骤可重复执行。
    if (Directory.Exists(destination)) Directory.Delete(destination, true);
    var install = await runnerEntry.RunAsync("dotnet", ["tool", "install", manifest.Id, "--tool-path", destination, "--version", manifest.Version, "--add-source", feed], root);
    var tool = Path.Combine(destination, OperatingSystem.IsWindows() ? "vpk.exe" : "vpk");
    if (install.ExitCode != 0 || !File.Exists(tool)) throw new IOException("Private packaging tool preparation failed.");
    StateStore.WriteJson(Path.Combine(destination, ".hub-install.json"), new { manifest.Id, manifest.Version, manifest.Url, manifest.Sha256 });
    Console.WriteLine("Packaging tool ready: " + tool);
    return;
}
if (args.Contains("--build-game") || args.Contains("--run-game"))
{
    var log = new HubLog(Path.Combine(root, "logs"));
    void Write(string line) { Console.WriteLine(line); log.Write(line); }
    var runnerEntry = new ProcessRunner(Write);
    var storeEntry = new StateStore(root);
    var hubState = storeEntry.Load();
    var projectEntry = hubState.Projects.Single(p => p.Name == "HelloAxmol");
    var engineEntry = hubState.Engines.Single(e => e.Version == projectEntry.Version && e.Channel == projectEntry.Channel);
    var serviceEntry = new ProjectService(runnerEntry, EngineCli(runnerEntry), new EnginePrebuiltState(root));
    if (args.Contains("--build-game"))
    {
        projectEntry.BuildStatus = "Building";
        storeEntry.Save(hubState);
        try
        {
            await serviceEntry.BuildAsync(projectEntry, engineEntry);
            Write($"Built executable: {serviceEntry.FindExecutable(projectEntry)}");
            projectEntry.BuildStatus = "Succeeded";
        }
        catch { projectEntry.BuildStatus = "Failed"; throw; }
        finally { storeEntry.Save(hubState); }
    }
    else
    {
        var resultEntry = await serviceEntry.RunAsync(projectEntry, engineEntry);
        Write($"Actual game exited: {resultEntry.ExitCode}");
        if (resultEntry.ExitCode != 0) throw new Exception("Game exited with error.");
    }
    return;
}
// `--prepare-windows` / `--install-msvc`（下载并安装托管 MSVC / Windows SDK）已随工具链自持退役：
// 环境准备现在就是跑引擎自己的 setup.ps1，见下面的 --install-tools。
if (args.Contains("--install-tools"))
{
    var hub = new StateStore(root).Load();
    var engineEntry = hub.Engines.FirstOrDefault(candidate => candidate.Path == hub.DefaultEnginePath) ?? hub.Engines.FirstOrDefault()
        ?? throw new InvalidOperationException("Import an Axmol engine first.");
    var commandLine = EngineCli(new ProcessRunner(Console.WriteLine));
    var platform = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "win32";
    var outcome = await new EngineSetupService(commandLine).RunAsync(engineEntry, new SetupOptions(platform));
    Console.WriteLine($"{engineEntry}: {outcome.Describe()}");
    if (!outcome.Succeeded) throw new InvalidOperationException(outcome.Describe());
    return;
}
if (args.Contains("--install-engine"))
{
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    // 清单现在列出多个版本，所以这里不能再 Single()；`--engine-version <v>` 可指定，
    // 不给就取最新 LTS —— 与界面「安装」按钮的默认语义一致。
    var releases = new EngineReleases(root, Path.GetFullPath("manifests"));
    var requested = Array.IndexOf(args, "--engine-version");
    var release = requested >= 0 && requested + 1 < args.Length
        ? releases.Find(args[requested + 1]) ?? throw new ArgumentException("No such engine release in the manifest: " + args[requested + 1])
        : releases.LatestLts();
    var package = release.Package;
    var installer = new PackageInstaller(new DownloadManager(http, Console.WriteLine), root, Console.WriteLine);
    var path = await installer.InstallAsync(package);
    var engineEntry = StateStore.ValidateEngine(path, package.Channel);
    var processRunner = new ProcessRunner(Console.WriteLine);
    var projectService = new ProjectService(processRunner, EngineCli(processRunner), new EnginePrebuiltState(root));
    var created = await projectService.CreateAsync("HelloAxmol", Path.Combine(root, "projects"), engineEntry);
    new StateStore(root).Save(new HubState { Engines = [engineEntry], Projects = [created], DefaultEnginePath = path });
    Console.WriteLine($"Axmol {release.Version} verified and project created. Run the engine's setup.ps1 to prepare its toolchain, then Build/Run.");
    return;
}
var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    count++;
    Console.WriteLine("PASS: " + name);
}
async Task Reject<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Check(true, name); return; }
    throw new Exception("FAILED: " + name);
}

// ---------------------------------------------------------------------------
// 下载契约：原子落盘、缓存复用、坏哈希拒绝、断点续传、流式 SHA-256。
// 自带 return、主机无关、不联网（FixtureHandler 注入），可在 CI 上跑。
// ---------------------------------------------------------------------------
if (args.Contains("--check-download"))
{
    var downloadBytes = Encoding.UTF8.GetBytes("verified download fixture with enough content to exercise the streaming path");
    var downloadHandler = new FixtureHandler(downloadBytes);
    using var downloadClient = new HttpClient(downloadHandler);
    var downloadManager = new DownloadManager(downloadClient, _ => { });
    var downloadCache = Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N"));
    var downloadSha = Convert.ToHexString(SHA256.HashData(downloadBytes));

    var downloadFile = await downloadManager.DownloadAsync(new Uri("https://fixture.test/file.zip"), downloadSha, downloadCache);
    Check(File.ReadAllBytes(downloadFile).SequenceEqual(downloadBytes), "SHA-256 download and atomic final file");
    await downloadManager.DownloadAsync(new Uri("https://fixture.test/file.zip"), downloadSha, downloadCache);
    Check(downloadHandler.Requests == 1, "Verified cache reused");
    await Reject<InvalidDataException>(() => downloadManager.DownloadAsync(new Uri("https://fixture.test/file.zip"), new string('0', 64), downloadCache), "Bad hash rejected");
    Check(!Directory.EnumerateFiles(downloadCache, "*.partial").Any() && !File.Exists(Path.Combine(downloadCache, new string('0', 64) + ".zip")), "Failed downloads leave no installed/cache artifact");
    await Reject<ArgumentException>(() => downloadManager.DownloadAsync(new Uri("http://fixture.test/file.zip"), downloadSha, downloadCache), "HTTP package rejected");

    // 断点续传：第一次响应在传输中途截断，重试时用 Range 从断点续传，最终 sha256 正确。
    var resumeBytes = Encoding.UTF8.GetBytes("resumable download fixture with enough bytes to split across two requests");
    var resumeSha = Convert.ToHexString(SHA256.HashData(resumeBytes));
    var resumeCache = Path.Combine(root, "cache-resume-" + Guid.NewGuid().ToString("N"));
    var resumeHandler = new FixtureHandler(resumeBytes) { TruncateFirstResponseTo = resumeBytes.Length / 2 };
    using var resumeClient = new HttpClient(resumeHandler);
    var resumeDownloads = new DownloadManager(resumeClient, _ => { });
    var resumeFile = await resumeDownloads.DownloadAsync(new Uri("https://fixture.test/resume.zip"), resumeSha, resumeCache);
    Check(File.ReadAllBytes(resumeFile).SequenceEqual(resumeBytes), "Interrupted download resumes from the breakpoint and verifies the whole-file hash");
    Check(resumeHandler.Requests >= 2 && resumeHandler.SawRange, "Resume issues a Range request after interruption");

    // 服务器不支持 Range（返回 200）：从 0 重写，正确落盘。
    var noRangeBytes = Encoding.UTF8.GetBytes("server ignores range and returns the full body");
    var noRangeSha = Convert.ToHexString(SHA256.HashData(noRangeBytes));
    var noRangeCache = Path.Combine(root, "cache-norange-" + Guid.NewGuid().ToString("N"));
    var noRangeHandler = new FixtureHandler(noRangeBytes) { IgnoreRange = true };
    using var noRangeClient = new HttpClient(noRangeHandler);
    var noRangeDownloads = new DownloadManager(noRangeClient, _ => { });
    var noRangeFile = await noRangeDownloads.DownloadAsync(new Uri("https://fixture.test/norange.zip"), noRangeSha, noRangeCache);
    Check(File.ReadAllBytes(noRangeFile).SequenceEqual(noRangeBytes), "Server without Range support falls back to a clean full download");

    // 坏 partial（续传后 sha256 不匹配）→ 删除重下 → 正确。
    var corruptBytes = Encoding.UTF8.GetBytes("corrupted resume content that will not match its digest");
    var corruptSha = Convert.ToHexString(SHA256.HashData(corruptBytes));
    var corruptCache = Path.Combine(root, "cache-corrupt-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(corruptCache);
    var corruptTarget = Path.Combine(corruptCache, corruptSha.ToLowerInvariant() + ".zip");
    File.WriteAllBytes(corruptTarget + ".partial", Encoding.UTF8.GetBytes("leftover garbage that is not the right prefix"));
    var corruptHandler = new FixtureHandler(corruptBytes);
    using var corruptClient = new HttpClient(corruptHandler);
    var corruptDownloads = new DownloadManager(corruptClient, _ => { });
    var corruptFile = await corruptDownloads.DownloadAsync(new Uri("https://fixture.test/corrupt.zip"), corruptSha, corruptCache);
    Check(File.ReadAllBytes(corruptFile).SequenceEqual(corruptBytes), "Corrupted resume content is discarded and re-downloaded cleanly");

    Console.WriteLine($"{count} checks passed.");
    return;
}

// ---------------------------------------------------------------------------
// 工具版本真源 + 引擎树工具链探测。自带 return、主机无关、不联网、不安装 ——
// 因此可以在 CI 上对一棵真实引擎树跑。真源是引擎自带 1k/build.profiles，
// 落点是官方 setup.ps1 的 tools/external。
// ---------------------------------------------------------------------------
if (args.Contains("--check-build-profiles"))
{
    var flagIndex = Array.IndexOf(args, "--check-build-profiles");
    if (flagIndex + 1 >= args.Length || args[flagIndex + 1].StartsWith("--"))
        throw new ArgumentException("--check-build-profiles requires the path to a real Axmol engine tree.");
    var probeRoot = Path.GetFullPath(args[flagIndex + 1]);
    if (!Directory.Exists(probeRoot)) throw new DirectoryNotFoundException(probeRoot);

    var probeEngine = StateStore.ValidateEngine(probeRoot);
    Check(File.Exists(BuildProfile.FileFor(probeRoot)), "Engine ships 1k/build.profiles as the tool version source of truth");

    var probeProfile = BuildProfile.Load(probeRoot);
    foreach (var key in new[] { "axslcc", "cmake", "ninja", "vs", "llvm", "jdk", "cmdlinetools", "ndk", "target_sdk", "min_sdk", "gradle", "agp", "buildtools", "emsdk" })
        Check(probeProfile.Get(key) is { Length: > 0 }, $"build.profiles defines '{key}'");

    // 版本真源必须**随引擎版本走**：这是 Hub 不再自持版本的核心理由。
    var probePinned = probeEngine.Version.StartsWith("3.") ? (Ndk: "r27d", TargetSdk: "37") : (Ndk: "r23d", TargetSdk: "36");
    Check(probeProfile.Ndk == probePinned.Ndk, $"Axmol {probeEngine.Version} pins NDK {probePinned.Ndk} (read {probeProfile.Ndk})");
    Check(probeProfile.TargetSdk == probePinned.TargetSdk, $"Axmol {probeEngine.Version} pins target_sdk {probePinned.TargetSdk} (read {probeProfile.TargetSdk})");

    var probeToolRoot = EngineToolchain.ToolRoot(probeEngine);
    Check(probeToolRoot.StartsWith(probeRoot, StringComparison.OrdinalIgnoreCase) && probeToolRoot.Contains("tools"), "Tool detection targets the engine tree (setup.ps1 tools/external), not a Hub data-root");

    // ---- 版本要求语义：必须与 1kiss 的 find_prog 一致（Hub 说"就绪"= 引擎不会再去装一份）----
    // `x~y+`：以 '+' 结尾时引擎让**区间上界失效**，退化成 >= x。照抄，不"顺手修正"。
    var rangeWithPlus = ToolRequirement.Parse("4.2.0~4.4.3+");
    Check(rangeWithPlus.Satisfies("4.3.2") && rangeWithPlus.Satisfies("9.9.9") && !rangeWithPlus.Satisfies("4.1.9"),
        "A requirement ending in '+' degrades to a lower bound (the engine drops the range's upper bound)");
    // `x~y`（不带 +）：真正的闭区间。
    var closedRange = ToolRequirement.Parse("17.0.10~17.0.20.1+".Replace("+", string.Empty));
    Check(closedRange.Satisfies("17.0.15") && closedRange.Satisfies("17.0.20") && !closedRange.Satisfies("17.0.21"),
        "A range without '+' is a closed interval");
    // `x.y.*`：通配（引擎用 PowerShell 的 -like）。
    var wildcard = ToolRequirement.Parse("5.5.1.*");
    Check(wildcard.Satisfies("5.5.1.6542") && !wildcard.Satisfies("6.14.0.1"),
        "A wildcard requirement matches by prefix and rejects a newer major");
    // 纯版本号是**字符串相等**，不是数值相等。
    var exact = ToolRequirement.Parse("22.0");
    Check(exact.Satisfies("22.0") && !exact.Satisfies("22.0.0") && !exact.Satisfies("9.0"),
        "A bare version requires exact string equality (22.0 does not accept 22.0.0)");
    // `17.9+`：单段下限，VS 的 4 段版本要能比。
    var lowerBound = ToolRequirement.Parse("17.9+");
    Check(lowerBound.Satisfies("18.10.12224.181") && !lowerBound.Satisfies("17.8.0"),
        "A lower bound compares numeric segments (17.9+ accepts 18.10.x, rejects 17.8)");
    Check(ToolRequirement.Parse("19.0.0~19.1.7+").Satisfies("21.1.1"), "A lower bound accepts a much newer version");

    var probeTarget = OperatingSystem.IsWindows() ? "windows-x64" : OperatingSystem.IsMacOS() ? "macos-arm64" : "linux-x64";
    var probeToolchain = new EngineToolchain(new ProcessRunner(_ => { }));
    var probeRows = await probeToolchain.InspectAsync(probeEngine, probeTarget);
    Check(probeRows.Count >= 3, $"Engine toolchain probe reports components for {probeTarget}");
    foreach (var name in new[] { "CMake", "Ninja", "Axmol shader compiler" })
        Check(probeRows.Any(row => row.Name == name), $"Probe covers {name}");
    Check(probeRows.All(row => row.Details.Length > 0), "Every probed component carries a status description");
    Check(probeRows.Where(row => row.Status == ComponentStatus.Installed).All(row => row.Executable is not null),
        "Reported installed components point at the executable that will actually be used");
    // axslcc 在引擎里是 -mode BOTH（引擎树优先），而这棵树的 axslcc 版本恰好满足要求；
    // 所以它必须解析到引擎树内 —— 这条能证明"查找顺序"不是想当然写的。
    var axslcc = probeRows.Single(row => row.Name == "Axmol shader compiler");
    Check(axslcc.Status != ComponentStatus.Installed
          || axslcc.Executable!.StartsWith(probeToolRoot, StringComparison.OrdinalIgnoreCase),
        "Engine-first tools resolve inside the engine tree (axslcc uses -mode BOTH)");
    // NDK 代号 → revision 前两段：r27d → 27.3（major 取全部数字、minor = 字母 - 'a'）。
    // 这是引擎 setup_android_sdk 里最容易抄错的位运算，单独钉住。
    Check(EngineToolchain.NdkRevisionFor("r27d") == "27.3" && EngineToolchain.NdkRevisionFor("r23d") == "23.3"
          && EngineToolchain.NdkRevisionFor("r25") == "25.0" && EngineToolchain.NdkRevisionFor("r27") == "27.0",
        "NDK codenames map to the revision prefix the engine compares (r27d -> 27.3)");
    if (OperatingSystem.IsWindows())
        Check(probeRows.Any(row => row.Name == "Visual Studio"), "Visual Studio is detected (the engine detects it but never installs it)");

    Console.WriteLine($"{count} build.profile checks passed for Axmol {probeEngine.Version}.");
    return;
}

// ---------------------------------------------------------------------------
// 预编译引擎库（Windows 目标）。自带 return、主机无关 —— 全部用夹具，
// 不需要真实引擎、不需要 Windows，所以可以进 CI。
//
// 这里守的是**引擎那边不会报错**的那些性质：AX_PREBUILT_DIR 指向的目录不合格时，
// 引擎会静默退回源码构建（AXGameEngineSetup.cmake:22），所以「能不能用」必须 Hub 判准。
// ---------------------------------------------------------------------------
if (args.Contains("--check-prebuilt"))
{
    var prebuiltRoot = Path.Combine(root, "prebuilt-check-" + Guid.NewGuid().ToString("N"));
    var enginePath = Path.Combine(prebuiltRoot, "engine");
    var projectPath = Path.Combine(prebuiltRoot, "game");
    Directory.CreateDirectory(enginePath);
    Directory.CreateDirectory(projectPath);

    var fixtureEngine = new EngineEntry("2.11.5", enginePath, "local");
    var prebuiltState = new EnginePrebuiltState(prebuiltRoot);
    var windowsTarget = BuildTargets.Get("windows-x64");
    var fixtureProject = new ProjectEntry { Name = "game", Path = projectPath, Platform = "windows-x64", Configuration = "Debug", Version = "2.11.5" };

    // 造一个「内容完整」的引擎构建目录：CMakeCache.txt + lib/<配置> + bin/<配置> + runtime/axslc + freetype 头。
    var buildDirectory = Path.Combine(enginePath, "build");

    /// 干净重来：每次都从零造，否则上一步留下的文件会让下一步的断言假绿
    /// （例如「没有着色器」那一步会被上一步造出来的着色器文件救回来）。
    void CompleteBuild(string configuration)
    {
        if (Directory.Exists(buildDirectory)) Directory.Delete(buildDirectory, recursive: true);
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "# fixture cache");
        Directory.CreateDirectory(Path.Combine(buildDirectory, "bin", configuration));
        Directory.CreateDirectory(Path.Combine(buildDirectory, "engine", "3rdparty", "freetype", "include"));
        var shaders = Path.Combine(buildDirectory, "runtime", "axslc");
        Directory.CreateDirectory(shaders);
        File.WriteAllText(Path.Combine(shaders, "positionTextureColor_vs"), "fixture");
        var libraries = Path.Combine(buildDirectory, "lib", configuration);
        Directory.CreateDirectory(libraries);
        File.WriteAllText(Path.Combine(libraries, "axmol.lib"), "fixture");
    }

    void SaveRecord(string target, string configuration, string token)
        => prebuiltState.Save(fixtureEngine, new EngineBuildRecord
        {
            EnginePath = enginePath, EngineVersion = "2.11.5", Channel = "local",
            Target = target, Platform = "win32", Architecture = "x64",
            Configuration = configuration, BuildDirectory = "build",
            EngineToken = token, BuiltAt = DateTimeOffset.Now,
        });

    var token = ProjectService.EngineInstallationToken(fixtureEngine);

    // 1) 还没构建过 —— 最常见的初始状态。
    Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Debug", prebuiltState).Status == PrebuiltStatus.NotBuilt,
        "Without a build record the prebuilt libraries are reported as not built");

    // 2) 内容完整 + 记录匹配 → Ready，且相对路径是引擎根下的干净路径（正斜杠）。
    CompleteBuild("Debug");
    SaveRecord("windows-x64", "Debug", token);
    var ready = EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Debug", prebuiltState);
    Check(ready.Usable && ready.RelativeDirectory == "build" && !ready.RelativeDirectory!.Contains('\\'),
        "A complete engine build is reported ready with an engine-root-relative path (build)");

    // 3) 只有 Debug 库却要 Release：报出**实际存在哪些配置**，而不是只说"缺"。
    var missingConfiguration = EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState);
    Check(missingConfiguration.Status == PrebuiltStatus.ConfigurationMissing && missingConfiguration.Detail.Contains("Debug"),
        "A missing configuration reports the configurations the engine build actually has");

    // 4) 内容不全（没有预编译着色器）→ 不算就绪。
    CompleteBuild("Release");
    File.Delete(Path.Combine(buildDirectory, "runtime", "axslc", "positionTextureColor_vs"));
    Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState).Status == PrebuiltStatus.MissingContents,
        "An engine build without runtime/axslc is not accepted as prebuilt");

    // 5) 引擎被重装/修复过 → 记录失效，不能拿旧产物当真。
    CompleteBuild("Release");
    SaveRecord("windows-x64", "Release", "token-from-an-older-installation");
    Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState).Status == PrebuiltStatus.EngineChanged,
        "A record from an older engine installation is refused");

    // 6) 记录里的目标与请求的不一致 → 拒绝（目录名本身判断不出平台，只认记录）。
    SaveRecord("windows-arm64", "Release", token);
    Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState).Status == PrebuiltStatus.TargetMismatch,
        "A record built for another target is refused");

    // 7) 平台闸门：预编译库只对 Windows 目标成立（引擎也只认 WIN32/LINUX）。
    SaveRecord("windows-x64", "Release", token);
    Check(!EnginePrebuilt.Supported(BuildTargets.Get("android-arm64"))
          && EnginePrebuilt.Inspect(fixtureEngine, BuildTargets.Get("android-arm64"), "Release", prebuiltState).Status == PrebuiltStatus.PlatformUnsupported,
        "Non-Windows targets are refused before reading the disk");

    // 8) 每项目选项的存取（写项目目录内的独立 JSON，不动 .axmol-hub.json）。
    new PrebuiltSettings { Enabled = true }.Save(fixtureProject);
    Check(PrebuiltSettings.Load(fixtureProject)?.Enabled == true
          && File.Exists(Path.Combine(projectPath, ".axmol-hub.prebuilt.json"))
          && !File.Exists(Path.Combine(projectPath, ".axmol-hub.json")),
        "Prebuilt settings persist in the project directory without touching the project metadata");

    // 9) 开关打开且就绪 → 交给 CMake 的就是引擎根的相对路径；否则**明确失败**而不是静默退回源码构建。
    //    夹具项目是 Debug，所以这里要让引擎那一份也回到 Debug 才算就绪。
    CompleteBuild("Debug");
    SaveRecord("windows-x64", "Debug", token);
    var options = ProjectBuildOptions.CmakeOptions(fixtureProject, fixtureEngine, windowsTarget, prepareFiles: false, prebuiltState);
    Check(options.Contains("-DAX_PREBUILT_DIR=build"), "An enabled, ready prebuilt build contributes -DAX_PREBUILT_DIR=build");
    prebuiltState.Clear(fixtureEngine);
    await Reject<PrebuiltUnavailableException>(
        () => Task.Run(() => ProjectBuildOptions.CmakeOptions(fixtureProject, fixtureEngine, windowsTarget, prepareFiles: false, prebuiltState)),
        "An enabled but unavailable prebuilt build fails the build instead of silently compiling from source");

    Console.WriteLine($"{count} prebuilt checks passed.");
    return;
}

// ---------------------------------------------------------------------------
// CLI --json 契约（文档：docs/cli-json-contract.md）。这是**端到端**检查：真起 CLI 进程，
// 只认 stdout。纯形状断言永远证明不了"stdout 里恰好只有一份 JSON" —— help 那次真错
// （help 文本打头、后面跟着信封）就是这么漏过去的。所以核心断言是"整段 stdout 必须被
// JsonDocument.Parse 吃下"，多一个字符都不行。解析在进程内做，不依赖 jq/python，
// 三个平台行为一致。
//
// 放在主流程之前、自带 return：契约检查不需要真实引擎与工具链，因此必须能在
// 干净的 CI 机器上单独跑（主流程恰恰需要真实引擎树，CI 目前跑不了）。
// ---------------------------------------------------------------------------
if (args.Contains("--check-cli-json"))
{
    var cliIndex = Array.IndexOf(args, "--check-cli-json");
    if (cliIndex + 1 >= args.Length || args[cliIndex + 1].StartsWith("--"))
        throw new ArgumentException("--check-cli-json requires the path to AxmolHub.Cli.dll or to the self-contained host executable.");
    var cliPath = Path.GetFullPath(args[cliIndex + 1]);
    if (!File.Exists(cliPath)) throw new FileNotFoundException("Build src/AxmolHub.Cli first: the CLI artifact does not exist.", cliPath);

    // 框架依赖产物是 dll，要借 dotnet 起；自包含产物本身就是宿主可执行文件。
    var executable = Path.GetExtension(cliPath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : cliPath;
    string[] leading = executable == "dotnet" ? [cliPath] : [];
    var workspace = Path.GetFullPath(".");
    var scratch = Path.Combine(root, "cli-contract");
    if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    Directory.CreateDirectory(scratch);

    var transcript = new List<string>();
    var cli = new ProcessRunner(line => { lock (transcript) transcript.Add(line); });

    async Task<(int Code, string Output, string Error)> Invoke(string[] arguments)
    {
        var result = await cli.RunAsync(executable, [.. leading, .. arguments], workspace, timeout: TimeSpan.FromMinutes(2));
        return (result.ExitCode, result.Output, result.Error);
    }

    // 解析失败时把 stdout 原文倒出来再失败，否则只剩一句无信息量的 FAILED。
    JsonDocument? Parse(string output)
    {
        try { return JsonDocument.Parse(output); }
        catch (JsonException)
        {
            Console.Error.WriteLine("--- stdout was not exactly one JSON document ---");
            Console.Error.WriteLine(output.Length == 0 ? "(stdout was empty)" : output);
            return null;
        }
    }

    // 失败时把子进程的完整 transcript 倒出来；成功时保持安静（否则 14 行 JSON 会被抄两遍）。
    void Judge(bool condition, string name)
    {
        if (condition) { Check(true, name); return; }
        Console.Error.WriteLine("--- CLI transcript ---");
        foreach (var line in transcript) Console.Error.WriteLine(line);
        throw new Exception("FAILED: " + name);
    }

    bool Has(JsonElement parent, params string[] names) => names.All(name => parent.TryGetProperty(name, out _));

    // 递归确认没有 PascalCase 属性名。命名策略一旦退回 System.Text.Json 的默认值，
    // 整个契约就和文档对不上了 —— 而那只会在消费方那边炸，不会在这里炸。
    bool CamelCaseOnly(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Length > 0 && char.IsUpper(property.Name[0])) return false;
                if (!CamelCaseOnly(property.Value)) return false;
            }
            return true;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) if (!CamelCaseOnly(item)) return false;
            return true;
        }
        return true;
    }

    // ---- 1) 成功路径 ----
    var targets = await Invoke(["targets", "--json"]);
    var targetsDocument = Parse(targets.Output);
    Judge(targets.Code == 0, "targets --json exits 0");
    Judge(targetsDocument is not null, "targets --json writes exactly one parseable JSON document to stdout");
    if (targetsDocument is not null)
    {
        using (targetsDocument)
        {
            var envelope = targetsDocument.RootElement;
            Judge(envelope.GetProperty("schema").GetInt32() == CliContract.SchemaVersion, "the envelope declares the contract schema version");
            Judge(envelope.GetProperty("command").GetString() == "targets", "the envelope names the verb that ran");
            Judge(envelope.GetProperty("ok").GetBoolean(), "a successful command reports ok:true");
            Judge(envelope.GetProperty("exitCode").GetInt32() == 0, "the envelope exitCode equals the process exit code");
            Judge(!envelope.TryGetProperty("error", out _), "a successful envelope carries no error member");
            Judge(Has(envelope, "schema", "command", "ok", "exitCode", "data"), "the envelope is written with the documented member names");
            Judge(CamelCaseOnly(envelope), "no PascalCase member survives anywhere in the envelope");
            var list = envelope.GetProperty("data").GetProperty("targets");
            Judge(list.GetArrayLength() == BuildTargets.All.Count, "data.targets lists every build target");
            Judge(list.EnumerateArray().All(target => Has(target, "id", "name", "family", "architecture", "hosts", "simulator", "current")),
                "every target descriptor uses the documented field names");
            Judge(list.EnumerateArray().Any(target => target.GetProperty("id").GetString() == "windows-x64"), "target ids stay verbatim in JSON");
        }
    }

    // --json 是全局标志：放在动词前面必须和放在后面逐字节相同。
    var flagFirst = await Invoke(["--json", "targets"]);
    Judge(flagFirst.Code == 0 && flagFirst.Output == targets.Output, "--json is position independent");

    // ---- 2) help：这里曾真的漏过（help 文本打头 + 信封，stdout 整段不可解析） ----
    var helpJson = await Invoke(["help", "--json"]);
    var helpDocument = Parse(helpJson.Output);
    Judge(helpJson.Code == 0 && helpDocument is not null, "help --json writes exactly one parseable JSON document to stdout");
    if (helpDocument is not null)
    {
        using (helpDocument)
        {
            var commands = helpDocument.RootElement.GetProperty("data").GetProperty("commands");
            Judge(commands.GetArrayLength() == 13 && commands.EnumerateArray().Any(command => command.GetString() == "install-tools"),
                "help enumerates every verb as data instead of printing prose to stdout");
        }
    }

    // ---- 3) 不带 --json 时人读输出必须原样不动 ----
    var human = await Invoke(["targets"]);
    Judge(human.Code == 0 && !human.Output.TrimStart().StartsWith('{') && human.Output.Contains("current="),
        "without --json the human layout is untouched");

    // ---- 4) 失败也必须给 JSON：消费方不该被迫去解析 stderr ----
    var failure = await Invoke(["select", Path.Combine(scratch, "data"), Path.Combine(scratch, "not-a-project"), "windows-x64", "--json"]);
    var failureDocument = Parse(failure.Output);
    Judge(failure.Code == 1, "a failing command keeps its process exit code");
    Judge(failureDocument is not null, "a failing command still writes exactly one parseable JSON document to stdout");
    if (failureDocument is not null)
    {
        using (failureDocument)
        {
            var envelope = failureDocument.RootElement;
            Judge(!envelope.GetProperty("ok").GetBoolean() && envelope.GetProperty("exitCode").GetInt32() == 1,
                "the failure envelope reports ok:false together with the real exit code");
            Judge(envelope.GetProperty("error").GetProperty("type").GetString() == nameof(InvalidDataException),
                "the failure envelope names the exception type");
            Judge(!string.IsNullOrWhiteSpace(envelope.GetProperty("error").GetProperty("message").GetString()),
                "the failure envelope carries the exception message");
            Judge(!envelope.TryGetProperty("data", out _), "an exception failure carries no data member");
        }
    }
    Judge(failure.Error.Length > 0, "the human diagnostic still goes to stderr rather than stdout");

    // ---- 5) verify 是刻意的例外：ok:false + exitCode:2 但 data 仍要在（"组件缺失"是数据不是异常）----
    var verify = await Invoke(["verify", Path.Combine(scratch, "clean-root"), "windows-x64", "--json"]);
    var verifyDocument = Parse(verify.Output);
    Judge(verify.Code == 2, "verify exits 2 when components are missing");
    Judge(verifyDocument is not null, "verify with missing components still writes exactly one parseable JSON document");
    if (verifyDocument is not null)
    {
        using (verifyDocument)
        {
            var envelope = verifyDocument.RootElement;
            Judge(!envelope.GetProperty("ok").GetBoolean() && envelope.GetProperty("exitCode").GetInt32() == 2,
                "verify separates ok:false from exitCode 2 instead of throwing");
            var data = envelope.GetProperty("data");
            Judge(data.GetProperty("target").GetString() == "windows-x64" && data.GetProperty("components").GetArrayLength() > 0,
                "verify keeps its component inventory in data even when it fails");
            Judge(data.GetProperty("components").EnumerateArray().All(component => Has(component, "name", "status", "details", "executable")),
                "component descriptors use the documented field names");
            Judge(!envelope.TryGetProperty("error", out _), "missing components are data, not an error member");
        }
    }

    // ---- 6) 未知动词 ----
    var unknown = await Invoke(["nonsense", "--json"]);
    var unknownDocument = Parse(unknown.Output);
    Judge(unknown.Code == 1 && unknownDocument is not null, "an unknown verb also fails in JSON");
    if (unknownDocument is not null)
    {
        using (unknownDocument)
        {
            Judge(!unknownDocument.RootElement.GetProperty("ok").GetBoolean()
                && unknownDocument.RootElement.GetProperty("error").GetProperty("type").GetString() == nameof(ArgumentException),
                "an unknown verb is reported as an ArgumentException");
        }
    }

    // ---- 7) 载荷编码本身（进程内，不依赖上面任何一次调用） ----
    using (var bare = JsonDocument.Parse(CliContract.Encode("noop", true, 0)))
    {
        var members = bare.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);
        Judge(members.SequenceEqual(["command", "exitCode", "ok", "schema"]),
            "an envelope without payload or error has exactly the four base members");
    }
    using (var child = JsonDocument.Parse(CliContract.Encode("run", true, 7, new ChildExitPayload(7))))
    {
        Judge(child.RootElement.GetProperty("data").GetProperty("exitCode").GetInt32() == 7,
            "run/serve/deploy report the child exit code inside data while ok stays true");
    }
    var described = CliContract.Describe(new InvalidDataException("boom"));
    Judge(described.Type == nameof(InvalidDataException) && described.Message == "boom",
        "Describe keeps the exception type and message and leaves the stack to stderr");

    Console.WriteLine($"{count} CLI --json contract checks passed.");
    return;
}

var messages = new List<string>();
var runner = new ProcessRunner(message => { lock (messages) messages.Add(message); });
var script = Path.Combine(root, "process fixture.ps1");
File.WriteAllText(script, "param([string]$Value)\n[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n[Console]::WriteLine($Value)\n[Console]::Error.WriteLine('fixture stderr')\nexit 7\n");
var hostile = "spaces ; & $() ` \" quote 中文";
var result = await runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Value", hostile], root);
Check(result.ExitCode == 7 && result.Output.Trim() == hostile && result.Error.Contains("fixture stderr"), "ArgumentList, stdout, stderr, nonzero exit");
var secretFixture = "fixture-secret-\"quoted\"";
var redacted = await runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-File", script, "-Value", secretFixture], root, sensitiveValues: [secretFixture]);
Check(redacted.Output.Trim() == "[REDACTED]" && !messages.Any(line => line.Contains(secretFixture) || line.Contains(System.Text.Json.JsonSerializer.Serialize(secretFixture)[1..^1])),
    "Secret argument with quotes is redacted from command logging and captured output");
var sleeper = Path.Combine(root, "sleep.ps1");
File.WriteAllText(sleeper, "Start-Sleep -Seconds 30");
await Reject<TimeoutException>(() => runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-File", sleeper], root, timeout: TimeSpan.FromMilliseconds(300)), "Timeout stops process");
using (var cancellation = new CancellationTokenSource(300))
    await Reject<OperationCanceledException>(() => runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-File", sleeper], root, cancellation: cancellation.Token), "Cancellation stops process");

var engineRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("../axmol-2.11.5");
var engine = StateStore.ValidateEngine(engineRoot);
Directory.CreateDirectory(Path.Combine(root, "tools"));
// 工具链真源已从「Hub 的 data-root/tools」换成**引擎树**：
// 期望版本来自 <engine>/1k/build.profiles，落点是官方 setup.ps1 的 <engine>/tools/external。
var managedTools = Path.GetFullPath(Path.Combine(root, "tools"));
var engineTools = EngineToolchain.ToolRoot(engine);
Check(engineTools == Path.Combine(engine.Path, "tools", "external") && !engineTools.StartsWith(managedTools, StringComparison.OrdinalIgnoreCase),
    "Toolchain resolution targets the engine tree instead of a Hub-managed tools root");
var engineComponents = await new EngineToolchain(runner).InspectAsync(engine, "windows-x64");
Check(engineComponents.Count >= 3 && engineComponents.All(c => c.Status is not (ComponentStatus.Unknown or ComponentStatus.Checking)),
    "Engine toolchain probe reports every component with a conclusive state");
Check(engineComponents.All(c => c.Details.Length > 0) && engineComponents.Any(c => c.Name == "CMake"),
    "Engine toolchain probe names components and explains their state");
var profile = BuildProfile.Load(engine.Path);
Check(profile.Cmake is { Length: > 0 } && profile.Ndk is { Length: > 0 } && profile.TargetSdk is { Length: > 0 },
    "Engine build profile supplies the expected tool versions (source of truth)");
// 辅助环境（Hub 直调 adb/keytool/emrun 时用）必须以引擎树为根，且不得再指向 Hub 的 tools 目录。
var auxiliary = new PlatformBuildService(runner).CreateEnvironment(engine, BuildTargets.Get("wasm32"));
Check(auxiliary["AX_ROOT"] == engine.Path && auxiliary["EMSDK"] == Path.Combine(engineTools, "emsdk")
    && auxiliary["PATH"].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .All(part => !part.StartsWith(managedTools, StringComparison.OrdinalIgnoreCase)),
    "Auxiliary environment is rooted at the engine tree, not at a Hub-managed tools root");
Check(auxiliary.ContainsKey("HOME") && auxiliary.ContainsKey("TEMP"), "Auxiliary environment always exposes HOME/TEMP for child tools");

var wrapper = Path.GetFullPath("src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1");
var service = new ProjectService(runner, EngineCli(runner), new EnginePrebuiltState(root));
await Reject<ArgumentException>(() => service.CreateAsync("bad;name", root, engine), "Unsafe template name rejected");
var parent = Path.Combine(root, "projects " + Guid.NewGuid().ToString("N"));
var project = await service.CreateAsync("HelloAxmol", parent, engine);
Check(File.Exists(Path.Combine(project.Path, "Source/AppDelegate.cpp")) && StateStore.ReadProject(project.Path).Version == engine.Version, "Real official CLI creates project and exact version lock");
Check(StateStore.ReadProject(project.Path).ProjectType == "cpp", "Default creation uses the official C++ template");
// 引擎从带 .git 的源码树创建工程时，engine_version 会带上短提交号（axmol.ps1 追加 -<hash>）。
// 提交号不含兼容性信息，Hub 接受它并归一化到 x.y.z；但预发布标签仍然被拒 —— 精确版本纪律不放宽。
{
    var versionRoot = Path.Combine(root, "project-version-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(versionRoot);
    File.WriteAllText(Path.Combine(versionRoot, "CMakeLists.txt"), "# fixture");
    void WriteVersion(string value) => File.WriteAllText(Path.Combine(versionRoot, ".axproj"), $"engine_version={value}\nproject_type=cpp\n");
    WriteVersion("3.0.0-30e6f4d");
    Check(StateStore.ReadProject(versionRoot).Version == "3.0.0", "Engine source-tree commit suffix is accepted and normalized to x.y.z");
    WriteVersion("3.0.0-alpha33");
    await Reject<InvalidDataException>(() => Task.Run(() => StateStore.ReadProject(versionRoot)), "Prerelease tags are still rejected by the exact version rule");
    WriteVersion("3.0");
    await Reject<InvalidDataException>(() => Task.Run(() => StateStore.ReadProject(versionRoot)), "A short version is still rejected by the exact version rule");
}
await Reject<ArgumentException>(() => service.CreateAsync("InvalidScript", parent, engine, projectType: "../lua"), "Invalid scripting type is rejected before creating directories");
Check(!Directory.Exists(Path.Combine(parent, "InvalidScript")), "Invalid scripting choice leaves no project destination");
var luaProject = await service.CreateAsync("HelloLua", parent, engine, projectType: "lua");
Check(File.Exists(Path.Combine(luaProject.Path, "Content/src/main.lua")) && File.Exists(Path.Combine(luaProject.Path, "Source/lua_module_register.h")) && File.Exists(Path.Combine(luaProject.Path, "Content/src/axmol/init.lua")), "Real official Lua template includes scripts and C++ bindings");
Check(StateStore.ReadProject(luaProject.Path).ProjectType == "lua", "Lua scripting type persists in project metadata");
StateStore.WriteJson(StateStore.MetadataPath(luaProject.Path), new { engine = "axmol", version = luaProject.Version, channel = luaProject.Channel, platform = luaProject.Platform });
Check(StateStore.ReadProject(luaProject.Path).ProjectType == "lua", "Legacy metadata infers Lua scripting from the official project profile");
StateStore.LockProject(luaProject);
luaProject.ProjectType = "cpp";
StateStore.LockProject(luaProject);
await Reject<InvalidDataException>(() => Task.Run(() => StateStore.ReadProject(luaProject.Path)), "Scripting metadata mismatch cannot silently reinterpret a Lua project");
luaProject.ProjectType = "lua"; StateStore.LockProject(luaProject);
var absentTemplate = new EngineEntry(engine.Version, Path.Combine(root, "missing-lua-engine"));
await Reject<InvalidDataException>(() => service.CreateAsync("MissingLua", parent, absentTemplate, projectType: "lua"), "Missing engine template is rejected before invoking the official CLI");
await Reject<IOException>(() => service.CreateAsync("HelloAxmol", parent, engine), "Existing project never overwritten");
await Reject<InvalidOperationException>(() => service.BuildAsync(project, engine), "Missing managed compiler blocks build without system fallback");
var stateStore = new StateStore(Path.Combine(root, "state"));
var state = new HubState { Engines = [engine], Projects = [project], DefaultEnginePath = engine.Path };
stateStore.Save(state);
Check(stateStore.Load().Projects.Single().Version == engine.Version, "State persistence");
var preferencesStore = new PreferencesStore(Path.Combine(root, "preferences.json"));
var preferences = new HubPreferences { Language = "en-US", DataRoot = Path.Combine(root, "独立资料库"), ProjectDirectory = Path.Combine(root, "用户项目") };
preferencesStore.Save(preferences);
Check(preferencesStore.Load().Language == "en-US" && preferencesStore.Load().ProjectDirectory == preferences.ProjectDirectory && preferencesStore.Load().DataRoot == preferences.DataRoot, "Language and selected directories survive restart");
preferences.Language = "unsupported";
preferencesStore.Save(preferences);
Check(preferencesStore.Load().Language == "en-US", "Unknown language falls back to the default (English)");
Check(Directory.Exists(PreferencesStore.VerifyDirectory(preferences.DataRoot!)) && !Directory.EnumerateFiles(preferences.DataRoot!, ".hub-write-check-*").Any(), "Selected directory checked for write access without residue");
// 构建目录已由引擎决定（不再是 Hub 的 build-hub*），所以断言的是**发现规则**：
// 引擎在工程里生成的 run 脚本写着 BUILD_DIR，那是权威来源；没有它才扫描 build*。
var buildFixture = Path.Combine(root, "engine-layout-" + Guid.NewGuid().ToString("N"));
var layoutProject = new ProjectEntry { Name = "Fixture", Path = buildFixture, Platform = "windows-x64", Configuration = "Debug" };
var scannedBuild = Path.Combine(buildFixture, "build_win32_x64");
Directory.CreateDirectory(Path.Combine(scannedBuild, "bin", "Fixture", "Debug"));
File.WriteAllText(Path.Combine(scannedBuild, "bin", "Fixture", "Debug", "Fixture.exe"), "binary");
Check(EngineBuildLayout.FindBuildDirectory(layoutProject) == scannedBuild
    && EngineBuildLayout.FindArtifact(scannedBuild, "Fixture", "Debug", "windows") is not null,
    "Engine build directory and artifact are discovered from the engine's own layout");
File.WriteAllText(Path.Combine(buildFixture, "run.bat"), "set BUILD_DIR=build_declared\n");
Directory.CreateDirectory(Path.Combine(buildFixture, "build_declared"));
Check(EngineBuildLayout.FindBuildDirectory(layoutProject)!.EndsWith("build_declared"),
    "The engine's run script BUILD_DIR is the authoritative build directory");
Check(EngineBuildLayout.FindArtifact(scannedBuild, "Fixture", "Release", "windows") is null
    && EngineBuildLayout.FindArtifact(scannedBuild, "Fixture", "Debug", "wasm") is null,
    "Artifact discovery never borrows another configuration's or target's output");

await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.SafePath(root, "../escape")), "Manifest path traversal rejected");
await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.SafePath(root, "C:\\escape")), "Absolute manifest path rejected");
var badZip = Path.Combine(root, Guid.NewGuid() + ".zip");
using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create)) zip.CreateEntry("../escape.txt");
await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.ExtractSafely(badZip, Path.Combine(root, "extracted"))), "ZIP traversal rejected");

// SDK 完整性判定已交还引擎（它在 CMake 配置阶段校验），Hub 不再自己拼装/校验一份 SDK。
var cancelledScript = Path.Combine(root, "must-not-start.ps1");
var startedMarker = Path.Combine(root, "unexpected-start.txt");
File.WriteAllText(cancelledScript, "param([string]$Marker)\nSet-Content -LiteralPath $Marker -Value started");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await Reject<OperationCanceledException>(() => runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-File", cancelledScript, "-Marker", startedMarker], root, cancellation: cancelled.Token), "Already-cancelled process never starts");
}
Check(!File.Exists(startedMarker), "Cancellation prevents process side effects");
{
var lifecycleRoot = Path.Combine(root, "lifecycle-" + Guid.NewGuid().ToString("N"));
using var packageStream = new MemoryStream();
using (var zip = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true))
using (var text = new StreamWriter(zip.CreateEntry("bin/tool.exe").Open())) text.Write("verified package executable");
var packageBytes = packageStream.ToArray();
using var packageClient = new HttpClient(new FixtureHandler(packageBytes));
var packages = new PackageInstaller(new DownloadManager(packageClient, _ => { }), lifecycleRoot, _ => { });
var package = new PackageEntry { Id = "fixture", Version = "1.0", Destination = "tools/fixture", VerifyFile = "bin/tool.exe", Url = "https://fixture.test/package.zip", Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)) };
var installed = await packages.InstallAsync(package);
File.WriteAllText(Path.Combine(installed, "bin/tool.exe"), "broken");
File.WriteAllText(Path.Combine(installed, "user-note.txt"), "preserve user file");
await packages.RepairAsync(package);
Check(File.ReadAllText(Path.Combine(installed, "bin/tool.exe")) == "verified package executable" && Directory.EnumerateFiles(Path.Combine(lifecycleRoot, "backups"), "user-note.txt", SearchOption.AllDirectories).Any(), "Repair restores verified files and retains previous installation");
var receipt = Path.Combine(installed, ".hub-install.json");
var originalReceipt = File.ReadAllText(receipt);
File.Delete(receipt);
await Reject<InvalidOperationException>(() => Task.Run(() => packages.Uninstall(package)), "Imported/unowned directories cannot be uninstalled");
File.WriteAllText(receipt, originalReceipt);
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await Reject<OperationCanceledException>(() => packages.RepairAsync(package, cancellation: cancelled.Token), "Cancelled repair leaves current installation intact");
}
Check(File.Exists(Path.Combine(installed, "bin/tool.exe")), "Cancelled repair preserves installed executable");
var recovery = packages.Uninstall(package);
Check(!Directory.Exists(installed) && File.Exists(Path.Combine(recovery, "bin/tool.exe")), "Uninstall removes managed installation and retains recovery files");
}
{
    var platformRoot = Path.Combine(root, "platform-fixture-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(platformRoot);
    File.WriteAllText(Path.Combine(platformRoot, "CMakeLists.txt"), "# fixture");
    var entry = new ProjectEntry { Name = "Fixture", Path = platformRoot, Version = "2.11.5", Channel = "official-lts", BuildStatus = "Succeeded" };
    StateStore.LockProject(entry);
    var directories = new HashSet<string>();
    // 遍历全部目标需要一个 v3 引擎（v3 才含 arm64/wasm64 等专属目标）。
    entry.Version = "3.0.0";
    StateStore.LockProject(entry);
    foreach (var target in BuildTargets.All)
    {
        BuildTargets.Select(entry, target.Id);
        Check(StateStore.ReadProject(platformRoot).Platform == target.Id && directories.Add(BuildTargets.BuildDirectory(entry)), "Target persists and build directory is isolated: " + target.Id);
    }
    // v2 引擎不得选中 v3 专属目标（win32 arm64 / linux arm64 / wasm64）。
    entry.Version = "2.11.5";
    StateStore.LockProject(entry);
    await Reject<PlatformNotSupportedException>(() => Task.Run(() => BuildTargets.Select(entry, "windows-arm64")), "A v2 engine cannot target Windows ARM64");
    await Reject<PlatformNotSupportedException>(() => Task.Run(() => BuildTargets.Select(entry, "linux-arm64")), "A v2 engine cannot target Linux ARM64");
    await Reject<PlatformNotSupportedException>(() => Task.Run(() => BuildTargets.Select(entry, "wasm64")), "A v2 engine cannot target wasm64");

    // 交叉编译与运行规则（用户 2026-10-03）：Windows 可交叉编译 arm64 但运行需 arm64 宿主；
    // Linux 不支持交叉编译，v3 的 linux arm64 只能在 arm64 机器上构建。
    Check(BuildTargets.Get("windows-arm64").CanCrossBuild("x64") && !BuildTargets.Get("windows-arm64").CanRunLocally("x64")
        && BuildTargets.Get("windows-arm64").CanRunLocally("arm64"),
        "Windows ARM64 cross-compiles on x64 but only runs on an arm64 host");
    Check(!BuildTargets.Get("linux-arm64").CanCrossBuild("x64") && BuildTargets.Get("linux-arm64").CanCrossBuild("arm64"),
        "Linux ARM64 builds only on an arm64 host (no cross-compile)");
    Check(BuildTargets.Get("linux-x64").CanCrossBuild("x64") && !BuildTargets.Get("linux-x64").CanCrossBuild("arm64"),
        "Linux x64 builds on x64 but not cross-compiled on arm64");
    Check(BuildTargets.SameArch("x86_64", "x64") && BuildTargets.SameArch("aarch64", "arm64") && !BuildTargets.SameArch("x64", "arm64"),
        "x86_64/x64 and aarch64/arm64 are the same arch; x64 vs arm64 differ");
    Check(BuildTargets.Get("windows-x64").CanRunLocally("x64") && BuildTargets.Get("android-arm64").CanCrossBuild("x64"),
        "Windows x64 runs locally; Android cross-compiles regardless of host arch");

    Check(entry.BuildStatus == "Not built", "Target switch never reuses another target success status");
    BuildTargets.Select(entry, "windows-x64", "Debug");
    var debugDirectory = BuildTargets.BuildDirectory(entry);
    entry.BuildStatus = "Succeeded";
    BuildTargets.Select(entry, "windows-x64", "Release");
    Check(StateStore.ReadProject(platformRoot).Configuration == "Release" && entry.BuildStatus == "Not built", "Configuration persists and switching resets success status");
    Check(BuildTargets.BuildDirectory(entry) != debugDirectory && debugDirectory.EndsWith("build-hub"), "Release output is isolated while legacy Debug output remains compatible");
    await Reject<InvalidDataException>(() => Task.Run(() => BuildTargets.Select(entry, "windows-x64", "../../outside")), "Invalid build configuration cannot escape output directory");
    StateStore.WriteJson(StateStore.MetadataPath(platformRoot), new { engine = "axmol", version = "2.11.5", channel = "official-lts", platform = "windows-x64" });
    Check(StateStore.ReadProject(platformRoot).Configuration == "Debug", "Legacy project metadata defaults to Debug");
    var mismatchedService = new ProjectService(new ProcessRunner(_ => { }), EngineCli(new ProcessRunner(_ => { })), new EnginePrebuiltState(root));
    await Reject<InvalidOperationException>(() => mismatchedService.RunAsync(entry, new("2.11.5", root, "official-lts")), "Run refuses configuration different from locked project before starting a process");
    entry.Platform = "wasm32";
    entry.Configuration = "Release";
    var releasePlan = PlatformBuildService.Plan(entry, engine, false, new EnginePrebuiltState(root));
    Check(releasePlan.SubCommand == "build" && releasePlan.Arguments.Contains("-O3")
        && releasePlan.Arguments.Contains("wasm") && releasePlan.Arguments.Contains(platformRoot),
        "Release plan maps to the engine's own axmol build invocation");
    entry.Platform = "android-arm64";
    await Reject<InvalidOperationException>(() => Task.Run(() => BuildConfigurations.ValidateTarget(entry)), "Android Release cannot silently use the debug signing profile");
    entry.Configuration = "Debug";
    var releaseSettings = new AndroidReleaseSettings { ApplicationId = "com.axmolhub.example", KeyAlias = "upload", KeystorePath = Path.GetFullPath(Path.Combine(root, "fixture.jks")) };
    releaseSettings.Validate(false);
    Check(AndroidPackageService.ApkPath(new() { Path = platformRoot, Platform = "android-arm64", Configuration = "Release" }).EndsWith("app-release.apk"), "Android Release APK path cannot use the Debug artifact");
    await Reject<FileNotFoundException>(() => Task.Run(() => releaseSettings.Validate()), "Missing release keystore blocks signing");
    releaseSettings.VersionCode = 0;
    await Reject<InvalidDataException>(() => Task.Run(() => releaseSettings.Validate(false)), "Invalid release versionCode is rejected");
    releaseSettings.VersionCode = 1; releaseSettings.KeyAlias = "androiddebugkey";
    await Reject<InvalidDataException>(() => Task.Run(() => releaseSettings.Validate(false)), "Debug key alias cannot configure Release");
    releaseSettings.KeyAlias = "upload"; releaseSettings.ApplicationId = "bad application";
    await Reject<InvalidDataException>(() => Task.Run(() => releaseSettings.Validate(false)), "Invalid release application ID is rejected");
    await Reject<InvalidDataException>(() => Task.Run(() => new AndroidSigningPasswords("short", "short").Validate()), "Invalid signing passwords are rejected");
    releaseSettings.ApplicationId = "com.axmolhub.example"; File.WriteAllText(releaseSettings.KeystorePath, "fixture key bytes");
    releaseSettings.Save(entry);
    Check(AndroidReleaseSettings.Load(entry)?.KeyAlias == "upload" && !File.ReadAllText(AndroidReleaseSettings.PathFor(entry)).Contains("Password"), "Release settings persist without password fields");
    var keyFingerprint = releaseSettings.Fingerprint(); File.AppendAllText(releaseSettings.KeystorePath, "changed");
    Check(releaseSettings.Fingerprint() != keyFingerprint, "Replacing a keystore invalidates its release receipt fingerprint");
    entry.Configuration = "Release";
    var androidReleasePlan = PlatformBuildService.Plan(entry, engine, false, new EnginePrebuiltState(root));
    Check(androidReleasePlan.Arguments.Contains("android") && androidReleasePlan.Arguments.Contains("-O3"), "Configured Android Release maps to the engine's release build invocation");
    entry.Configuration = "Debug";
    await Reject<InvalidDataException>(() => Task.Run(() => BuildTargets.Select(entry, "../../outside")), "Unknown target cannot escape build directories");
    var platformService = new PlatformBuildService(new ProcessRunner(_ => { }));
    entry.Platform = "ios-arm64";
    Check(!BuildTargets.Get(entry.Platform).CanBuildOn("windows") && BuildTargets.Get(entry.Platform).Hosts.Contains("macos"),
        "Apple targets declare macOS-only hosts, so an Apple build on Windows is rejected before any process");
    entry.Platform = "wasm32";
    var wasmEngine = new EngineEntry("2.11.5", platformRoot);
    var wasmEnvironment = platformService.CreateEnvironment(wasmEngine, BuildTargets.Get(entry.Platform));
    // 工具链已交还引擎：辅助环境以**引擎树**为根（<engine>/tools/external），不再指向 Hub 的 data-root/tools。
    var wasmTools = EngineToolchain.ToolRoot(wasmEngine);
    var hubTools = Path.GetFullPath(Path.Combine(root, "tools"));
    var wasmPath = wasmEnvironment["PATH"].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    Check(wasmEnvironment["AX_ROOT"] == platformRoot && wasmEnvironment["EMSDK"] == Path.Combine(wasmTools, "emsdk")
        && wasmPath.All(part => !part.StartsWith(hubTools, StringComparison.OrdinalIgnoreCase)),
        "Auxiliary environment is rooted at the engine tree instead of a Hub-managed tools root");
    // 产物候选按目标族收窄：Windows 的 .exe 不能满足 WebAssembly 的构建。
    var wasmNoise = Path.Combine(platformRoot, "build_wasm");
    Directory.CreateDirectory(Path.Combine(wasmNoise, "bin", "Fixture"));
    File.WriteAllText(Path.Combine(wasmNoise, "bin", "Fixture", "Fixture.exe"), "wrong-target");
    await Reject<FileNotFoundException>(() => Task.Run(() => PlatformBuildService.FindArtifact(entry)), "A Windows executable cannot satisfy a WebAssembly build");
    var concurrentStore = new StateStore(Path.Combine(platformRoot, "concurrent-state"));
    await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => concurrentStore.SaveProject(new ProjectEntry { Name = "Concurrent" + index, Path = Path.Combine(platformRoot, index.ToString()), Version = "2.11.5" }))));
    Check(concurrentStore.Load().Projects.Count == 8, "Concurrent CLI project updates preserve other projects");
}
{
    // 打包配方的版本验证边界来自清单（recipe-manifest.json），不是代码里的字面量：
    // 同一个配方在已声明与未声明的版本上必须给出相反结论。
    PackagingRecipes.RequireVerified(engine, PackagingRecipes.AndroidPackaging);
    await Reject<InvalidOperationException>(() => Task.Run(() => PackagingRecipes.RequireVerified(engine with { Version = "99.0.0" }, PackagingRecipes.AndroidPackaging)), "Unverified engine version cannot borrow another version packaging recipe");
    await Reject<InvalidOperationException>(() => Task.Run(() => PackagingRecipes.RequireVerified(engine, "recipe-that-is-not-declared")), "Recipe not declared for the engine version is refused");
}
{
    var androidRoot = Path.Combine(root, "android-fixture-" + Guid.NewGuid().ToString("N"));
    var entry = new ProjectEntry { Name = "Fixture", Path = androidRoot, Platform = "android-arm64", Version = "2.11.5", Channel = "local" };
    Directory.CreateDirectory(Path.Combine(androidRoot, "proj.android/app"));
    File.WriteAllText(Path.Combine(androidRoot, ".axproj"), "package_name=dev.axmol.fixture\n");
    File.WriteAllText(Path.Combine(androidRoot, "proj.android/app/AndroidManifest.xml"), "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\"><application><activity android:name=\".MainActivity\"><intent-filter><action android:name=\"android.intent.action.MAIN\"/><category android:name=\"android.intent.category.LAUNCHER\"/></intent-filter></activity></application></manifest>");
    Check(AndroidPackageService.LaunchComponent(entry) == "dev.axmol.fixture/dev.axmol.fixture.MainActivity", "Android launch component preserves namespace and relative activity");
    File.WriteAllText(Path.Combine(androidRoot, ".axproj"), "package_name=bad'; injected\n");
    await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.PackageName(entry)), "Invalid Android application ID rejected before Gradle generation");
    File.WriteAllText(Path.Combine(androidRoot, ".axproj"), "package_name=dev.axmol.fixture\n");
    Directory.CreateDirectory(BuildTargets.BuildDirectory(entry));
    File.WriteAllText(Path.Combine(BuildTargets.BuildDirectory(entry), "libFixture.so"), "native only");
    await Reject<FileNotFoundException>(() => Task.Run(() => PlatformBuildService.FindArtifact(entry)), "Native library alone cannot satisfy an Android APK build");
    byte[] Elf(ushort machine, ulong alignment)
    {
        var bytes = new byte[120]; bytes[0] = 0x7f; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F'; bytes[4] = 2; bytes[5] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), machine);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), alignment);
        return bytes;
    }
    await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.ValidateElf(new MemoryStream(Elf(62, 16384)), "arm64-v8a")), "ELF architecture mismatch blocks APK packaging");
    await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.ValidateElf(new MemoryStream(Elf(183, 4096)), "arm64-v8a")), "4 KB native load alignment cannot claim 16 KB support");
    var apk = AndroidPackageService.ApkPath(entry); Directory.CreateDirectory(Path.GetDirectoryName(apk)!);
    using (var zip = ZipFile.Open(apk, ZipArchiveMode.Create))
    {
        foreach (var file in new[] { "AndroidManifest.xml", "classes.dex", "assets/axslc/fixture_vs" })
        { using var data = zip.CreateEntry(file).Open(); data.Write(Encoding.UTF8.GetBytes("fixture")); }
        foreach (var file in new[] { "libFixture.so", "libopenal.so", "libc++_shared.so" })
        { using var data = zip.CreateEntry("lib/arm64-v8a/" + file).Open(); data.Write(Elf(183, 16384)); }
    }
    AndroidPackageService.ValidateApk(apk, entry);
    Check(PlatformBuildService.FindArtifact(entry) == apk, "Android artifact resolves to packaged APK instead of native output");
    var bundle = AndroidPackageService.BundlePath(entry); Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
    using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
    {
        foreach (var file in new[] { "base/manifest/AndroidManifest.xml", "base/dex/classes.dex", "base/assets/axslc/fixture_vs", "BundleConfig.pb", "META-INF/CERT.RSA" })
        { using var data = zip.CreateEntry(file).Open(); data.Write(Encoding.UTF8.GetBytes("fixture")); }
        foreach (var file in new[] { "libFixture.so", "libopenal.so", "libc++_shared.so" })
        { using var data = zip.CreateEntry("base/lib/arm64-v8a/" + file).Open(); data.Write(Elf(183, 16384)); }
    }
    AndroidPackageService.ValidateBundle(bundle, entry);
    Check(File.Exists(bundle), "AAB base-module layout passes structural checks independently of APK layout");
    using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Update)) zip.GetEntry("BundleConfig.pb")!.Delete();
    await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.ValidateBundle(bundle, entry)), "AAB without bundle configuration is rejected");
    entry.Platform = "android-x64";
    await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.ValidateApk(apk, entry)), "APK built for another ABI is rejected");
    entry.Platform = "android-arm64";
    using (var zip = ZipFile.Open(apk, ZipArchiveMode.Update)) zip.GetEntry("assets/axslc/fixture_vs")!.Delete();
    await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.ValidateApk(apk, entry)), "APK without compiled shaders is rejected");
    var apkHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(apk)));
    StateStore.WriteJson(Path.Combine(AndroidPackageService.StageDirectory(entry), ".hub-apk.json"), new { Target = entry.Platform, ApplicationId = AndroidPackageService.PackageName(entry), Sha256 = apkHash });
    Check(AndroidPackageService.VerifiedApk(entry) == apk, "APK deployment uses the matching build hash receipt");
    File.AppendAllText(apk, "changed");
    await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.VerifiedApk(entry)), "APK changed after build cannot be deployed");
    var devices = AndroidDeviceService.ParseDevices("* daemon started successfully\nList of devices attached\nusb123 device product:fixture\nusb456 unauthorized\nemulator-5554 offline\n");
    Check(devices.Count == 3 && devices.Select(device => device.State).SequenceEqual(new[] { "device", "unauthorized", "offline" }), "ADB authorization and offline states remain distinct");
    await Reject<ArgumentException>(() => new AndroidDeviceService(new ProcessRunner(_ => throw new Exception("Process must not start")), Path.Combine(root, "tools")).DeployAsync(entry, "bad;serial", new()), "Unsafe device serial cannot start a deployment process");
    var androidPlan = PlatformBuildService.Plan(entry, engine, false, new EnginePrebuiltState(root));
    var androidEnvironment = new PlatformBuildService(runner).CreateEnvironment(engine, BuildTargets.Get(entry.Platform));
    var androidTools = EngineToolchain.ToolRoot(engine);
    Check(androidPlan.Arguments.Contains("android") && androidPlan.Arguments.Contains("arm64")
        && androidEnvironment["JAVA_HOME"] == Path.Combine(androidTools, "jdk")
        && androidEnvironment["ANDROID_HOME"] == Path.Combine(androidTools, "adt", "sdk"),
        "Android build maps to axmol -p android and takes its JDK/SDK from the engine tree");
}
Console.WriteLine($"{count} checks passed. Real platform builds, device deployment and clean-host acceptance require separate evidence.");

sealed class FixtureHandler(byte[] bytes) : HttpMessageHandler
{
    public int Requests { get; private set; }
    public bool IgnoreRange { get; set; }
    public bool SawRange { get; private set; }
    // When set, the first non-Range request returns only this many bytes while the
    // Content-Length header still claims the full size, simulating a mid-transfer drop.
    public int? TruncateFirstResponseTo { get; set; }
    private bool _truncated;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range is not null && !IgnoreRange)
        {
            SawRange = true;
            var from = range.From ?? 0;
            var to = range.To ?? bytes.LongLength - 1;
            if (from >= bytes.LongLength)
            {
                // Range start is past the end; a real server replies 416.
                var unsatisfied = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request };
                unsatisfied.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(bytes.LongLength);
                return Task.FromResult(unsatisfied);
            }
            var length = (int)(to - from + 1);
            var slice = new byte[length];
            Array.Copy(bytes, (int)from, slice, 0, length);
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent(slice) };
            partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, bytes.LongLength);
            return Task.FromResult(partial);
        }
        if (TruncateFirstResponseTo is { } cut && !_truncated)
        {
            _truncated = true;
            var slice = new byte[cut];
            Array.Copy(bytes, 0, slice, 0, cut);
            var content = new ByteArrayContent(slice);
            content.Headers.ContentLength = bytes.LongLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
}

/// <summary>In-memory <see cref="ISecretStore"/> backing the <c>--check-ai-providers</c> assertions.</summary>
sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();
    public string? Read(string providerId) => _values.TryGetValue(providerId, out var value) ? value : null;
    public void Write(string providerId, string key) => _values[providerId] = key;
    public void Delete(string providerId) => _values.Remove(providerId);
}

/// <summary>A scripted <see cref="IChatClient"/> for the <c>--check-ai-sessions</c> assertions: yields fixed
/// text chunks and records the messages it was asked to answer, so the pipeline can be tested with no network
/// at all. This is the payoff of routing every provider through the M.E.AI abstraction — the seam is fakeable.</summary>
sealed class FakeChatClient(IReadOnlyList<string> chunks, bool honorCancellation = false) : IChatClient
{
    public IList<ChatMessage>? LastMessages { get; private set; }
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        LastOptions = options;
        foreach (var chunk in chunks)
        {
            if (honorCancellation) cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, chunk);
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>A scripted <see cref="IChatClient"/> that asks for one tool call and then answers: the follow-up
/// request is what proves the result was handed back. <c>honorCancellation</c> matters because parking works by
/// cancelling the stream — a client that ignored the token would answer a turn that was supposed to stop.</summary>
sealed class ToolLoopChatClient(bool honorCancellation = false) : IChatClient
{
    public int CallCount { get; private set; }
    public IList<ChatMessage> SecondRequest { get; private set; } = [];
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastOptions = options;
        var request = messages.ToList();
        if (CallCount == 1)
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call-1", "get_projects", new Dictionary<string, object?>()),
                });
        }
        else
        {
            // A real client refuses to send on a cancelled token, and that is what ends a parked turn, so the
            // stand-in has to behave the same way or the assertion would pass on a fake nobody could ship.
            if (honorCancellation) cancellationToken.ThrowIfCancellationRequested();
            SecondRequest = request;
            if (!request.Any(message => message.Contents.Any(content => content is FunctionResultContent)))
                throw new Exception("The function result was missing from the follow-up request.");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Projects loaded.");
        }

        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Asks for another tool call on every turn: the only thing that can end this loop is the pipeline's
/// iteration cap, which is exactly what the assertion needs to prove.</summary>
sealed class InsistentToolClient : IChatClient
{
    public int CallCount { get; private set; }
    public ChatOptions? LastOptions { get; private set; }
    public IList<ChatMessage> LastRequest { get; private set; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Checks only use the streaming path.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastOptions = options;
        LastRequest = messages.ToList();
        yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent($"call-{CallCount}", "get_projects", new Dictionary<string, object?>()),
            new TextContent($"第 {CallCount} 轮"),
        });
        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
