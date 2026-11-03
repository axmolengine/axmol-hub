using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

internal static class AiProvidersChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        await RequestRetryChecks.RunAsync();
        void AssertRejects(Action action, string name)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("PASS: " + name);
                return;
            }

            throw new Exception("FAILED: " + name);
        }

        // Built-in manifest entry assembles the expected provider (no half-built objects).
        var orca = AiProviderManifest.CreateBuiltIn("orcarouter") ?? throw new Exception("orcarouter is missing from ai-providers.json.");
        if (orca.IsCustom)
            throw new Exception("Built-in provider must not be marked custom.");
        if (orca.BaseUrl != "https://api.orcarouter.ai/v1")
            throw new Exception($"Wrong base URL: {orca.BaseUrl}");
        // No model is seeded, and the list is asserted empty rather than merely "not the old value": a manifest
        // that quietly reintroduced a default would put a name in front of the user that goes stale the moment
        // the provider retires it, and it would 404 on first use with the user's key blamed for it.
        if (orca.Models.Count != 0)
            throw new Exception($"Manifest must not seed a model (found {orca.Models.Count}).");
        if (!orca.RequiresCredential)
            throw new Exception("orcarouter should require a credential.");
        if (!orca.Affiliate)
            throw new Exception("orcarouter should be flagged affiliate.");
        if (string.IsNullOrEmpty(orca.ReferralUrl))
            throw new Exception("orcarouter should carry a referral URL.");
        Console.WriteLine("PASS: orcarouter manifest entry assembles the expected provider.");
        if (AiProviderManifest.CreateBuiltIn("nonexistent")is not null)
            throw new Exception("Unknown provider id should return null.");
        Console.WriteLine("PASS: unknown provider id returns null.");
        // The catalog: several presets, every one complete enough to be adopted with nothing typed, and every one
        // searchable by description (the picker filters on it, so an empty blurb would silently hide the preset
        // from anyone who does not already know the vendor's name).
        var catalog = AiProviderManifest.Load();
        if (catalog.Count < 3)
            throw new Exception($"The preset catalog should offer several providers, found {catalog.Count}.");
        foreach (var entry in catalog)
        {
            if (entry.Id.Length == 0 || entry.Name.Length == 0)
                throw new Exception($"A preset is missing id or name: {entry.Id}");
            if (entry.Description.Length == 0)
                throw new Exception($"Preset '{entry.Id}' has no description for the picker card.");
            // No preset may name a default model. A shipped name rots the moment the vendor adds or retires a
            // model, and a rotted default is offered in the list and then fails on first use with a 404 that
            // blames the user's key. The list is read from the endpoint (ModelCatalog) after authenticating.
            if (entry.GetType().GetProperty("DefaultModel")is not null)
                throw new Exception($"Preset '{entry.Id}' still exposes a DefaultModel property; the model list must come from the endpoint.");
            if (!AiProviderEntry.IsUsableBaseUrl(entry.BaseUrl))
                throw new Exception($"Preset '{entry.Id}' has a base URL the client cannot use: {entry.BaseUrl}");
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
            // Declaring browser sign-in *is* the promise that it can be completed, because the declaration is now
            // what derives whether the preset is usable without a key. The rule is the manifest's own
            // IsUsableFlow rather than "has a discovery document": a device-code preset has no document to point at,
            // and asserting one existed would reject exactly the flow that does not need it.
            if (entry.AuthMethods.Contains(ProviderAuthMethods.OAuth) && entry.OAuth is not { IsUsableFlow: true })
                throw new Exception($"Preset '{entry.Id}' declares browser sign-in but carries no usable flow configuration.");
        }

        if (!orca.SupportsOAuth)
            throw new Exception("orcarouter should declare OAuth support.");
        if (orca.OAuth is null)
            throw new Exception("A preset that supports OAuth must carry its OAuth parameters.");
        if (orca.OAuth.Scope != "api")
            throw new Exception($"orcarouter must request the narrow 'api' scope, got '{orca.OAuth.Scope}'.");
        if (!AiProviderEntry.IsUsableBaseUrl(orca.OAuth.DiscoveryUrl))
            throw new Exception("The OAuth discovery URL must be a usable URL.");
        if (string.IsNullOrEmpty(orca.OAuth.AppName))
            throw new Exception("The OAuth app name is shown on the consent screen and must not be empty.");
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
        var legacyFileJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>("""{"id":"legacy","name":"Legacy","baseUrl":"https://example.test/v1","model":"m","authMethods":["carrier-pigeon"]}""", legacyFileJson);
        if (legacy is null || !legacy.EffectiveAuthMethods.SequenceEqual([ProviderAuthMethods.ApiKey]))
            throw new Exception("An unknown or absent authMethods list should fall back to apiKey.");
        if (legacy.Id != "legacy")
            throw new Exception("The legacy fixture was not actually deserialized.");
        if (legacy.SupportsOAuth)
            throw new Exception("A provider with no OAuth block must not claim OAuth support.");
        Console.WriteLine("PASS: an unrecognised authMethods list falls back to apiKey instead of breaking the preset.");
        // The declared methods are now the *only* statement of whether a provider needs a credential, so what each
        // shape means has to be pinned through the file path (that is where a preset arrives), not just in memory:
        // an explicit `none` is keyless, an unreadable neighbour does not undo that, and any method that obtains a
        // credential beats `none` — the alternative is a manifest typo silently turning a cloud endpoint local.
        //
        // `entrance` is the second, separate question (whether the settings page offers a way to authenticate), and
        // it is asserted per shape rather than derived, because the two really do diverge: an `oauth` declaration
        // with no discovery block needs a credential yet has no working entrance.
        foreach (var(methods, keyless, entrance)in new[]
        {
            ("""["none"]""", true, false),
            ("""["none","carrier-pigeon"]""", true, false),
            ("""["none","apiKey"]""", false, true),
            ("""["oauth"]""", false, false),
        }

        )
        {
            var declared = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>($$"""{"id":"decl","name":"Decl","baseUrl":"https://example.test/v1","authMethods":{{methods}}}""", legacyFileJson) ?? throw new Exception($"The {methods} fixture was not deserialized.");
            if (declared.RequiresCredential == keyless)
                throw new Exception($"authMethods {methods} should be {(keyless ? "keyless" : "gated on a credential")}.");
            if (declared.CanAuthenticate != entrance)
                throw new Exception($"authMethods {methods} should offer {(entrance ? "" : "no ")}way to authenticate.");
        }

        Console.WriteLine("PASS: a keyless declaration is read from the auth methods, and the auth entrance stays a separate question.");
        // DefaultProviderId must ignore a default that names a provider the catalog does not carry.
        if (AiProviderManifest.DefaultProviderId()is null)
            throw new Exception("DefaultProviderId returned null for the shipped manifest.");
        Console.WriteLine("PASS: the shipped manifest resolves a default provider id.");
        // Localized descriptions fall back to English rather than resolving empty — the picker shows this copy, so
        // an empty string would render a blank card.
        var ollama = AiProviderManifest.CreateBuiltIn("ollama") ?? throw new Exception("ollama is missing from the catalog.");
        // `["none"]` is a declaration, not an absence: the empty-list fallback would otherwise read a keyless preset
        // as "needs a key" the moment its array failed to parse. And it needs no entrance either — a 「鉴权」 button
        // on a local endpoint would offer to authenticate a server that has no accounts.
        if (ollama.RequiresCredential)
            throw new Exception("A local Ollama endpoint should not require a credential.");
        if (ollama.CanAuthenticate)
            throw new Exception("A keyless preset should not offer a way to authenticate.");
        if (!ProviderAuthMethods.IsKeyless(ollama.EffectiveAuthMethods))
            throw new Exception($"ollama's declared {string.Join(",", ollama.EffectiveAuthMethods)} should read as keyless.");
        if (ollama.Describe("zh-CN").Length == 0 || ollama.Describe("en-US").Length == 0)
            throw new Exception("A preset description resolved empty for a supported language.");
        if (ollama.Describe("fr-FR") != ollama.Description)
            throw new Exception("An unsupported language should fall back to the English description.");
        Console.WriteLine("PASS: preset descriptions resolve per language and fall back to English.");
        // A preset that names a capability source is naming a family Hub must actually ship, and a local server is the
        // one family whose window cannot be read off the OpenAI-shaped list. Both halves are asserted together because
        // either alone is useless: a registered source nothing points at is dead code, and a name nothing recognizes
        // falls back to the shared parser, which is exactly the silence this exists to end.
        Assert(ollama.CapabilitySource == OllamaModelsSource.IdValue, "the local preset names the source that can read a local server, instead of the OpenAI-shaped default");
        Assert(ModelCapabilitySources.For(ollama) is IRuntimeModelWindow, "the named source is registered, and it is one that can answer the runtime question");
        Assert(ModelCapabilitySources.All.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ModelCapabilitySources.All.Count, "two sources sharing an id would make the manifest name a coin flip");
        // The seed contract: exactly one default, and it is a real catalog entry. A default that names a
        // non-existent provider would leave a fresh install with nothing configured.
        var defaultId = AiProviderManifest.DefaultProviderId();
        if (defaultId is null || catalog.All(entry => entry.Id != defaultId))
            throw new Exception($"The manifest's default provider '{defaultId}' is not in the catalog.");
        Console.WriteLine($"PASS: the manifest names one real default preset to seed ('{defaultId}').");
        var openRouter = AiProviderManifest.CreateBuiltIn("openrouter")
                         ?? throw new Exception("openrouter is missing from ai-providers.json.");
        Assert(openRouter.BaseUrl == "https://openrouter.ai/api/v1"
               && openRouter.EffectiveAuthMethods.SequenceEqual([ProviderAuthMethods.ApiKey])
               && openRouter.KeyValidation is { IsKnown: true, Path: "/models" }
               && openRouter.ExtraHeaders.GetValueOrDefault("HTTP-Referer") == "https://github.com/axmolengine/axmol-hub"
               && openRouter.ExtraHeaders.GetValueOrDefault("X-OpenRouter-Title") == "Axmol Hub",
            "OpenRouter preset declares its official API, API-key validation, and app attribution headers");
        openRouter.Credential = new ProviderCredential
        {
            Id = "openrouter-catalog-probe",
            ProviderId = openRouter.Id,
            Secret = "sk-or-v1-test",
        };
        var openRouterCatalogBytes = System.Text.Encoding.UTF8.GetBytes("""{"data":[{"id":"openrouter-test/model"}]}""");
        var openRouterRecorder = new RequestRecorder(openRouterCatalogBytes);
        using (var recorder = new HttpClient(openRouterRecorder))
        {
            var catalogBatch = await new OpenAiCompatibleModelsSource().FetchAsync(recorder, openRouter, CancellationToken.None);
            var sent = openRouterRecorder.Requests.FirstOrDefault();
            Assert(sent is { Method: "GET", Path: "/api/v1/models" }
                   && sent["authorization"] == "Bearer sk-or-v1-test"
                   && sent["http-referer"] == "https://github.com/axmolengine/axmol-hub"
                   && sent["x-openrouter-title"] == "Axmol Hub"
                   && catalogBatch.Models.Contains("openrouter-test/model"),
                "OpenRouter catalog request uses its official models route with bearer auth and app attribution headers");
        }
        // Factory validation: custom needs base URL + model; a built-in that needs a credential is refused while it
        // has none; a keyless local endpoint works.
        AssertRejects(() => ChatClientFactory.Create(new ModelProvider { IsCustom = true, Model = "m" }), "custom provider without base URL is rejected");
        AssertRejects(() => ChatClientFactory.Create(new ModelProvider { IsCustom = true, BaseUrl = "http://localhost:11434/v1" }), "custom provider without model is rejected");
        var gated = AiProviderManifest.CreateBuiltIn("orcarouter")!;
        gated.Model = "orcarouter/auto";
        AssertRejects(() => ChatClientFactory.Create(gated), "an unauthenticated preset is rejected before the request");
        // Which credential satisfies it is not the factory's business. A browser sign-in mints the same kind of
        // record as a pasted key, and reading only the pasted-key shape is how a signed-in user gets told to go
        // copy a key they never had to make.
        var signedIn = AiProviderManifest.CreateBuiltIn("orcarouter")!;
        signedIn.Model = "orcarouter/auto";
        var signIn = new ProviderCredential
        {
            Id = "orcarouter",
            ProviderId = "orcarouter",
            Source = CredentialSources.OAuth,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        signIn.Secret = "sk-yoex-factory";
        signedIn.Credential = signIn;
        if (ChatClientFactory.Create(signedIn)is null)
            throw new Exception("A credential from browser sign-in should unlock the provider just like a pasted key.");
        // A custom endpoint's requirement is unknowable, so it is never gated. Dropping the `!IsCustom` half of the
        // predicate is the regression this guards: it would refuse exactly the local servers (Ollama, llama.cpp,
        // vLLM) that make someone add a custom provider in the first place.
        if (ChatClientFactory.Create(new ModelProvider { IsCustom = true, Name = "Local", BaseUrl = "http://localhost:11434/v1", Model = "llama3" })is null)
            throw new Exception("Keyless local provider should produce a client.");
        Console.WriteLine("PASS: factory gates a preset on any credential and never gates a self-supplied endpoint.");
        // Readiness is one rule, stated here in Core so the composer and the factory cannot each invent their own. The
        // state that pulled them apart is a credential record whose secret is not there — a blank submit, or a stored
        // entry the OS secret store no longer hands back. Judging readiness by the *record* offered the provider in
        // chat, and the first send answered "authenticate first" under a settings row that said 已鉴权.
        var recordOnly = AiProviderManifest.CreateBuiltIn("orcarouter")!;
        recordOnly.Model = "orcarouter/auto";
        recordOnly.Credential = new ProviderCredential
        {
            Id = "cred-zombie",
            ProviderId = "orcarouter"
        };
        if (!recordOnly.RequiresCredential || recordOnly.IsCallReady || recordOnly.ApiKey is { Length: > 0 })
            throw new Exception("A credential record without its secret still counts as ready.");
        AssertRejects(() => ChatClientFactory.Create(recordOnly), "a credential whose secret is missing is refused by the factory, not sent keyless");
        var pastedKey = AiProviderManifest.CreateBuiltIn("orcarouter")!;
        pastedKey.Credential = new ProviderCredential
        {
            Id = "cred-key",
            ProviderId = "orcarouter",
            Secret = "sk-live"
        };
        var keylessReady = new ModelProvider
        {
            IsCustom = true,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama3",
        };
        if (!pastedKey.IsCallReady || !signedIn.IsCallReady || !keylessReady.IsCallReady)
            throw new Exception("A provider that can really be called was reported as not ready.");
        Console.WriteLine("PASS: one readiness rule — a gated provider is ready only once its secret is actually there.");
        // ── The retry policy is Hub's own number, counted at the transport ──
        // A 429 the pipeline retries is a rate limit; one it does not is a dead connection the user reads as a broken
        // assistant. The client model shipped a default, so this was a behaviour nobody wrote down, nobody budgeted
        // against the 16-iteration tool loop, and no cell could see. These count requests against a loopback handler,
        // so what is asserted is what Hub does — not what a dependency happened to answer to in this version.
        var retryProvider = new ModelProvider
        {
            IsCustom = true,
            Name = "Retry",
            BaseUrl = "http://127.0.0.1:9/v1",
            Model = "m"
        };
        var retryMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "hi")
        };
        var onceThrottled = new RateLimitWire(failFirst: 1);
        var recovered = "";
        try
        {
            await ChatClientFactory.Create(retryProvider, "m", null, null, onceThrottled).GetResponseAsync(retryMessages);
        }
        catch (Exception ex)
        {
            recovered = "threw " + ex.GetType().Name;
        }

        if (!onceThrottled.SawRetryAfter || onceThrottled.Attempts != 2)
            throw new Exception($"A rate limit that could be recovered took {onceThrottled.Attempts} attempt(s) and " + (recovered.Length == 0 ? "answered" : recovered) + ", Retry-After seen: " + onceThrottled.SawRetryAfter + ".");
        // Exhausted, it stops at MaxRetries + 1 and hands back the provider's own words — which is what the message the
        // user sees is assembled from. The text is recorded from the run rather than written from memory: it comes out
        // of the client model, and a cell that guesses it can pass on the wrong string.
        var throttled = new RateLimitWire(failFirst: 0, always: true);
        var exhausted = "";
        try
        {
            await ChatClientFactory.Create(retryProvider, "m", null, null, throttled).GetResponseAsync(retryMessages);
        }
        catch (Exception ex)
        {
            exhausted = ex.GetType().Name + ": " + ex.Message;
        }

        if (throttled.Attempts != ProviderRetryDefaults.MaxRetries + 1 || exhausted.Length == 0)
            throw new Exception($"A provider that only ever answers 429 took {throttled.Attempts} attempt(s) and threw " + (exhausted.Length == 0 ? "nothing at all" : exhausted));
        // The streaming wire is what a chat turn actually uses, and the policy is documented not to restart a response
        // whose body has already begun. So this one measures instead of assuming it matches the two calls above — the
        // number lands in the PASS line, where the next reader can see what a rate limit costs a streamed answer.
        var streaming = new RateLimitWire(failFirst: 1);
        try
        {
            await foreach (var update in ChatClientFactory.Create(retryProvider, "m", null, null, streaming).GetStreamingResponseAsync(retryMessages, new ChatOptions()))
                _ = update;
        }
        catch (Exception ex)
        {
            streaming.Failure = ex.GetType().Name;
        }

        if (streaming.Attempts != 2)
            throw new Exception($"The streamed call took {streaming.Attempts} attempt(s) where the measured answer was " + "2 — a 429 that arrives before the body is consumed is retried on this wire too " + streaming.Failure);
        Console.WriteLine("PASS: the retry policy is stated, budgeted and counted — a 429 recovers inside 2 attempts, an " + "exhausted one stops at " + throttled.Attempts + ", the streamed call retries the same way, " + "and the provider's words come back as " + exhausted[..Math.Min(96, exhausted.Length)].Replace('\n', '·').Replace("\r", "") + ".");
        // ── The wait, reported instead of mistaken for a dead stream ──
        // The sleeping happens in the client pipeline, below the enumerator that re-arms a run's inactivity deadline,
        // so an obeyed Retry-After used to be indistinguishable from a provider that had stopped answering — and the
        // deadline usually fired first and blamed the network. This asks the transport for three seconds and reads
        // back what the policy tells its owner: the attempt now pending, the delay the provider named, and the
        // release. The delay is asserted exactly because the exponential default is never exact; a number that came
        // from jitter would fail here rather than look like coverage. Costs the suite three seconds, and that is the
        // point — three seconds of a wait is what a user was being told to go check their cable for.
        var slowWire = new RateLimitWire(failFirst: 1, retryAfterSeconds: 3);
        var notices = new List<string>();
        var seen = new List<TimeSpan>();
        try
        {
            await foreach (var update in ChatClientFactory.Create(retryProvider, "m", null, null, slowWire, onRetryWait: (attempt, delay) =>
            {
                seen.Add(delay);
                notices.Add(attempt == 0 ? "released" : $"attempt {attempt}");
            }).GetStreamingResponseAsync(retryMessages, new ChatOptions()))
                _ = update;
        }
        catch (Exception ex)
        {
            slowWire.Failure = ex.GetType().Name;
        }

        if (slowWire.Attempts != 2 || notices.Count != 2 || notices[0] != "attempt 2" || notices[1] != "released" || seen[0] != TimeSpan.FromSeconds(3))
            throw new Exception($"A rate limit that named a three-second wait reported [{string.Join(" | ", notices)}] " + $"over {slowWire.Attempts} attempt(s) with delays " + $"[{string.Join(", ", seen.Select(s => s.TotalSeconds.ToString("0.###")))}]" + (slowWire.Failure.Length > 0 ? ", " + slowWire.Failure : ""));
        // Synthetic token-rate errors, with and without a diagnostic suffix; not captured provider responses.
        var tokenRateLimits = new[]
        {
            "Your requests to model-a have exceeded token rate limit.",
            "Your requests to model-b have exceeded token rate limit.\n[hub-request-id=fixture-request-429]",
        };
        foreach (var protocol in new[] { ModelProtocols.Chat, ModelProtocols.Responses })
        {
            var probeProvider = new ModelProvider { IsCustom = true, Name = "Retry", BaseUrl = retryProvider.BaseUrl, Model = "m", Protocol = protocol };
            var quotaWire = new RateLimitWire(0, always: true, errorBody:
                """{"error":{"type":"insufficient_quota","message":"No credits"}}""");
            try { await ChatClientFactory.Create(probeProvider, "m", null, transport: quotaWire).GetResponseAsync(retryMessages); }
            catch (Exception) { }
            if (quotaWire.Attempts != 1) throw new Exception("Quota exhaustion was retried.");
            foreach (var text in tokenRateLimits)
            {
                var body = System.Text.Json.JsonSerializer.Serialize(new { error = new { message = text, type = "rate_limit_error" } });
                if (!ChatPipeline.RateLimited(body) || ChatPipeline.UsageExhausted(body))
                    throw new Exception("A token rate limit was confused with exhausted quota.");
                var limitedWire = new RateLimitWire(ProviderRetryDefaults.MaxRetries, errorBody: body);
                await ChatClientFactory.Create(probeProvider, "m", null, transport: limitedWire).GetResponseAsync(retryMessages);
                if (limitedWire.Attempts != ProviderRetryDefaults.MaxRetries + 1)
                    throw new Exception("Token-rate retries ended before the configured budget was used.");
            }
            // Observe the first token-rate backoff without sleeping through a real rate window.
            var tokenBackoffWire = new RateLimitWire(1, omitRetryAfter: true);
            try
            {
                await ChatClientFactory.Create(probeProvider, "m", null, transport: tokenBackoffWire,
                    maxInlineRetryWait: TimeSpan.Zero).GetResponseAsync(retryMessages);
                throw new Exception("A token-rate delay was not handed off.");
            }
            catch (ProviderCooldownException ex) when (
                ex.Delay >= ProviderRetryDefaults.RateLimitBaseDelay * (1 - ProviderRetryDefaults.JitterFraction)
                && ex.Delay <= ProviderRetryDefaults.RateLimitBaseDelay * (1 + ProviderRetryDefaults.JitterFraction)) { }
            if (tokenBackoffWire.Attempts != 1) throw new Exception("A parked backoff sent another request.");
            var backoff = new List<TimeSpan>();
            var transient = new RateLimitWire(2, status: System.Net.HttpStatusCode.ServiceUnavailable, omitRetryAfter: true);
            await ChatClientFactory.Create(probeProvider, "m", null, transport: transient,
                onRetryWait: (attempt, delay) => { if (attempt > 0) backoff.Add(delay); }).GetResponseAsync(retryMessages);
            if (transient.Attempts != 3 || backoff.Count != 2
                || backoff[0].TotalSeconds is < 0.6 or > 1.0 || backoff[1].TotalSeconds is < 1.2 or > 2.0)
                throw new Exception("Exponential jitter did not stay inside the retry envelope.");
            using var cancel = new CancellationTokenSource();
            var cancelWire = new RateLimitWire(1, retryAfterSeconds: 3);
            try
            {
                await ChatClientFactory.Create(probeProvider, "m", null, transport: cancelWire,
                    onRetryWait: (attempt, _) => { if (attempt > 0) cancel.Cancel(); })
                    .GetResponseAsync(retryMessages, cancellationToken: cancel.Token);
                throw new Exception("Cancelled retry returned success.");
            }
            catch (OperationCanceledException) { }
            if (cancelWire.Attempts != 1) throw new Exception("Cancellation sent another request.");
            var cooldownWire = new RateLimitWire(1, retryAfterSeconds: 3600);
            try
            {
                await ChatClientFactory.Create(probeProvider, "m", null, transport: cooldownWire,
                    maxInlineRetryWait: ProviderRetryDefaults.GoalMaxInlineWait).GetResponseAsync(retryMessages);
                throw new Exception("A long Retry-After waited inline or returned success.");
            }
            catch (ProviderCooldownException ex) when (ex.Delay == TimeSpan.FromHours(1)) { }
            if (cooldownWire.Attempts != 1) throw new Exception("A cooldown sent a polling request.");
            var networkWire = new RateLimitWire(1, networkFailure: true);
            await ChatClientFactory.Create(probeProvider, "m", null, transport: networkWire).GetResponseAsync(retryMessages);
            if (networkWire.Attempts != 2) throw new Exception("A network interruption was not recovered inside retries.");
            var authWire = new RateLimitWire(0, always: true, status: System.Net.HttpStatusCode.Unauthorized);
            try { await ChatClientFactory.Create(probeProvider, "m", null, transport: authWire).GetResponseAsync(retryMessages); }
            catch (Exception ex) when (ProviderFailure.AuthenticationFailed(ex)) { }
            if (authWire.Attempts != 1) throw new Exception("Authentication failure was retried.");
        }
        foreach (var body in new[] { "null", "[]", "\"temporarily busy\"", "{\"error\":null}" })
            if (ProviderFailure.QuotaExhausted(body)) throw new Exception("An unstructured error was treated as exhausted quota.");
        var retryNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        if (ProviderFailure.RetryAfter(retryNow.AddSeconds(90).ToString("r"), retryNow) != TimeSpan.FromSeconds(90)
            || ProviderFailure.RetryAfter("1500", retryNow, milliseconds: true) != TimeSpan.FromMilliseconds(1500))
            throw new Exception("Retry-After date or millisecond parsing drifted.");
        Console.WriteLine("PASS: the retry wait is reported with the provider's own number — " + notices[0] + " after " + seen[0].TotalSeconds.ToString("0.#") + "s, then released, over " + slowWire.Attempts + " attempts; both protocols bound jitter, allow five retries for token rate limits, skip quota retries and cancel waits.");
        // Which failures are a rate limit, judged on the words providers actually use, and which must stay plain. The
        // rule fails twice over if it is loose in either direction: a 429 that reads as a generic failure points a
        // person at credentials and settings that are fine, and anything else that reads as a limit tells them to wait
        // for a problem no waiting fixes.
        foreach (var limited in new[]
        {
            "HTTP 429 (rate_limit_error: rate_limit_exceeded)\n\nRate limit reached.",
            "429 Too Many Requests",
            "{\"error\":{\"message\":\"Requests to the Chat Completions API have exceeded your allocated rate limit.\"}}",
        }

        )
            if (!ChatPipeline.RateLimited(limited))
                throw new Exception($"A provider's rate limit was not recognised: {limited}");
        foreach (var notLimited in new[]
        {
            "Incorrect API key provided",
            "This model's maximum context length is 8192 tokens.",
            "HTTP 500 (server_error)",
            "chatcmpl-429ab8c1",
        }

        )
            if (ChatPipeline.RateLimited(notLimited))
                throw new Exception($"A failure that is not a rate limit was labelled one: {notLimited}");
        Console.WriteLine("PASS: a rate limit is named by the provider's own words, while a bad key, an overflow, a " + "server error and a bare id full of digits stay plain failures.");
        // A spent plan allowance arrives as 429 too, and it is not a rate limit: waiting seconds cannot fix a
        // five-hour window, so "try again shortly" is the one sentence that guarantees another failure. The
        // gateway spells it usage_limit_reached and states the reset in the same body; that spelling is what lets
        // the ending name the wait instead.
        foreach (var spent in new[]
        {
            """{"error":{"type":"usage_limit_reached","message":"The usage limit has been reached","plan_type":"plus","limit_window_minutes":300,"resets_in_seconds":9207}}""",
            "HTTP 429 The usage limit has been reached",
        })
            if (!ChatPipeline.UsageExhausted(spent))
                throw new Exception($"A spent usage allowance was not recognised: {spent}");
        foreach (var notExhausted in new[] { "HTTP 429 (rate_limit_error)", "usage_limit_hint", "The usage limit has been raised" }.Concat(tokenRateLimits))
            if (ChatPipeline.UsageExhausted(notExhausted))
                throw new Exception($"Something that only looks like a spent allowance was labelled one: {notExhausted}");
        Console.WriteLine("PASS: a spent plan allowance is named apart from a rate limit, so the ending can ask for the reset instead of a retry.");
        // ProviderStore keeps keys out of JSON; CredentialStore owns the key and rehydrates it from the
        // secret store. The split is the whole point: a provider is a declaration, an account is a secret.
        var secretStore = new InMemorySecretStore();
        var providerStore = new ProviderStore(root);
        providerStore.Save([new ModelProvider { Id = "orcarouter", Name = "OrcaRouter", BaseUrl = "https://api.orcarouter.ai/v1", AuthMethods = [ProviderAuthMethods.ApiKey], Model = "orcarouter/auto" }]);
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
        if (providersJson.Contains("sk-secret-123"))
            throw new Exception("API key leaked into providers.json.");
        // Nor does the file belong to the derived answers: a saved `apiKeyRequired` was a value nothing re-derived,
        // so it outlived the manifest that wrote it and quietly disagreed with the declared auth methods. Forgetting
        // `[JsonIgnore]` on a getter puts exactly that kind of stale answer on disk — STJ serializes getters too.
        foreach (var derived in new[]
        {
            "apiKeyRequired",
            "requiresCredential",
            "canAuthenticate"
        }

        )
            if (providersJson.Contains(derived, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"providers.json persists the derived value '{derived}'.");
        var credentialsJson = File.ReadAllText(Path.Combine(root, "ai", "credentials.json"));
        if (credentialsJson.Contains("sk-secret-123"))
            throw new Exception("API key leaked into credentials.json.");
        var roundTrip = credentialStore.Load().Single(item => item.Id == "orcarouter");
        if (roundTrip.Secret != "sk-secret-123")
            throw new Exception("API key was not rehydrated from the secret store.");
        Console.WriteLine("PASS: ProviderStore and CredentialStore keep keys out of JSON and rehydrate them.");
        // A credential that names a provider id is what makes ModelProvider.ApiKey a live projection rather
        // than a stored field. Bind it and read it back.
        var liveProvider = providerStore.Load().Single(item => item.Id == "orcarouter");
        liveProvider.Credential = roundTrip;
        if (liveProvider.ApiKey != "sk-secret-123")
            throw new Exception("ModelProvider.ApiKey did not project from its credential.");
        Console.WriteLine("PASS: ModelProvider.ApiKey projects from the provider's credential.");
        // An older providers.json still carries "apiKeyRequired", and nothing migrates it, because the field it used
        // to feed no longer exists. Ignoring it is the rule — and ignoring has to mean *the declaration wins*: a file
        // saying `false` must not unlock a preset whose endpoint demands a credential, or the leftover key would
        // become a way to switch the gate off from disk.
        var staleRoot = Path.Combine(root, "stale-flag");
        Directory.CreateDirectory(Path.Combine(staleRoot, "ai"));
        var staleStore = new ProviderStore(staleRoot);
        File.WriteAllText(Path.Combine(staleRoot, "ai", "providers.json"), """[{"id":"orcarouter","name":"OrcaRouter","baseUrl":"https://api.orcarouter.ai/v1","model":"orcarouter/auto","apiKeyRequired":false}]""");
        var stale = staleStore.Load().Single(item => item.Id == "orcarouter");
        if (!stale.RequiresCredential)
            throw new Exception("A leftover apiKeyRequired still decided whether the provider is usable.");
        Console.WriteLine("PASS: a leftover apiKeyRequired in an older providers.json is ignored, not obeyed.");
        // The provider-level enable flag is the one a settings row writes. An older providers.json carries no
        // "enabled" key, and the field must default to true — a provider whose file predates the flag should not
        // disappear from the picker the day it is first read by a newer build.
        var enabledStore = new ProviderStore(staleRoot);
        File.WriteAllText(Path.Combine(staleRoot, "ai", "providers.json"), """[{"id":"orcarouter","name":"OrcaRouter","baseUrl":"https://api.orcarouter.ai/v1","model":"orcarouter/auto"}]""");
        var noFlag = enabledStore.Load().Single(item => item.Id == "orcarouter");
        if (!noFlag.Enabled)
            throw new Exception("A provider loaded from a file without an 'enabled' key must default to enabled.");
        var toggled = new ModelProvider
        {
            Id = "orcarouter",
            Name = "OrcaRouter",
            BaseUrl = "https://api.orcarouter.ai/v1",
            AuthMethods = [ProviderAuthMethods.ApiKey],
            Model = "orcarouter/auto",
            Enabled = false,
        };
        enabledStore.Save([toggled]);
        if (enabledStore.Load().Single(item => item.Id == "orcarouter").Enabled)
            throw new Exception("A provider saved disabled came back enabled.");
        Console.WriteLine("PASS: the provider enable flag defaults to true for legacy files and survives the round-trip.");
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
        if (both.Length != 2)
            throw new Exception("Expected two stored credential records.");
        if (both.Any(item => item.Secret is null or ""))
            throw new Exception("Credentials lost their secrets on round-trip.");
        if (both.Select(item => item.Source).Distinct().Count() != 2)
            throw new Exception("Provenance did not survive the round-trip, so the two would be indistinguishable.");
        Console.WriteLine("PASS: credential records round-trip with their secrets and their provenance intact.");
        // A provider owns a *list* of models with exactly one marked in use, and `Model` is a projection of that
        // mark. The projection is what lets the client factory and the pipeline stay unaware that a provider can
        // hold several models — so it is asserted rather than assumed.
        var multi = new ModelProvider
        {
            Id = "multi",
            Name = "Multi",
            BaseUrl = "https://example.test/v1"
        };
        if (multi.Models.Count != 0)
            throw new Exception("A fresh provider should start with no models.");
        if (multi.Model != "")
            throw new Exception("Model should project to empty when no model exists.");
        // The legacy single-value setter is the compatibility path for a providers.json written before the list
        // existed: assigning it must produce a list with that one model marked in use.
        multi.Model = "gpt-5.1-codex-mini";
        if (multi.Models.Count != 1 || !multi.Models[0].InUse)
            throw new Exception("Assigning Model should synthesize a one-entry list marked in use.");
        if (multi.Model != "gpt-5.1-codex-mini")
            throw new Exception("Model did not project back the assigned value.");
        // Assigning the same name again must not stack a duplicate row. This is the guarded setter's job, and the
        // deserialization path depends on it (a file carries both "Model" and "Models").
        multi.Model = "gpt-5.1-codex-mini";
        if (multi.Models.Count != 1)
            throw new Exception("Re-assigning the same name should not add a second model.");
        // A hand-edited file can carry several models with none marked. Normalize is what repairs it, and without
        // this the provider would send an empty model name and fail with a confusing 400.
        var unmarked = new ModelProvider
        {
            Id = "unmarked",
            Models = [new ProviderModel
            {
                Name = "a"
            }, new ProviderModel
            {
                Name = "b"
            }

            ],
        };
        unmarked.Normalize();
        if (unmarked.Models.Count(model => model.InUse) != 1 || !unmarked.Models[0].InUse)
            throw new Exception("Normalize should mark exactly the first model when none is marked.");
        // Two marks (a hand-merge) collapse to the first; blanks and case-insensitive duplicates are dropped.
        var messy = new ModelProvider
        {
            Id = "messy",
            Models = [new ProviderModel
            {
                Name = "  a  ",
                InUse = true
            }, new ProviderModel
            {
                Name = "A"
            }, new ProviderModel
            {
                Name = "b",
                InUse = true
            }, new ProviderModel
            {
                Name = "   "
            }, ],
        };
        messy.Normalize();
        if (messy.Models.Count != 2)
            throw new Exception($"Normalize should drop blanks and duplicates, got {messy.Models.Count}.");
        if (messy.Models[0].Name != "a")
            throw new Exception("Normalize should trim names.");
        if (messy.Models.Count(model => model.InUse) != 1 || !messy.Models[0].InUse)
            throw new Exception("Normalize should keep only the first mark.");
        Console.WriteLine("PASS: a provider holds a model list with exactly one in use, and Model projects it.");
        // The JSON round trip is the path that actually exercises the setter ordering: a file written by an older
        // build carries "model" as a string, a newer one carries "models". Both must load to the same state.
        //
        // The options must mirror ProviderStore's: it reads with PropertyNameCaseInsensitive, because the file is
        // camelCase while the model is PascalCase. Deserializing with the defaults silently produces an *empty*
        // object (no exception), which is how a test can "pass" while reading nothing at all.
        var fileJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var legacyJson = """{"id":"legacy-models","name":"Legacy","baseUrl":"https://example.test/v1","model":"only-one"}""";
        var fromLegacy = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>(legacyJson, fileJson) ?? throw new Exception("Legacy provider JSON failed to deserialize.");
        fromLegacy.Normalize();
        if (fromLegacy.Models.Count != 1 || fromLegacy.Model != "only-one")
            throw new Exception($"A legacy single-model file should load as a one-entry list. [models={fromLegacy.Models.Count} model='{fromLegacy.Model}']");
        var newJson = """{"id":"new-models","name":"New","baseUrl":"https://example.test/v1","models":[{"name":"x","inUse":true},{"name":"y"}]}""";
        var fromNew = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>(newJson, fileJson) ?? throw new Exception("Provider JSON with a models array failed to deserialize.");
        fromNew.Normalize();
        if (fromNew.Models.Count != 2 || fromNew.Model != "x")
            throw new Exception("A file with a models array should keep both and project the marked one.");
        // Both keys present (what a file looks like after the first save by a mixed build): the setter must not
        // duplicate the model the array already carries. This is the ordering hazard the guarded setter exists for,
        // so it is driven through the *real* load path too — not only the in-memory setter.
        var bothJson = """{"id":"both","name":"Both","baseUrl":"https://example.test/v1","model":"x","models":[{"name":"x","inUse":true},{"name":"y"}]}""";
        var fromBoth = System.Text.Json.JsonSerializer.Deserialize<ModelProvider>(bothJson, fileJson) ?? throw new Exception("Provider JSON with both keys failed to deserialize.");
        fromBoth.Normalize();
        if (fromBoth.Models.Count != 2)
            throw new Exception($"A file carrying both keys should not duplicate the model, got {fromBoth.Models.Count}.");
        if (fromBoth.Model != "x")
            throw new Exception("The marked model should win over the legacy scalar.");
        // And the round trip through the store itself, so the case-insensitive options above cannot drift from the
        // real ones without this failing.
        var modelRoot = Path.Combine(root, "model-roundtrip");
        Directory.CreateDirectory(modelRoot);
        var modelStore = new ProviderStore(modelRoot);
        modelStore.Save([new ModelProvider { Id = "saved", Name = "Saved", BaseUrl = "https://example.test/v1", Models = [new ProviderModel { Name = "first", InUse = true }, new ProviderModel { Name = "second" }], }]);
        var reloaded = modelStore.Load().Single();
        if (reloaded.Models.Count != 2 || reloaded.Model != "first")
            throw new Exception($"A saved model list should round-trip, got {reloaded.Models.Count} / '{reloaded.Model}'.");
        // A hand-written file with the models in reverse order and the second one marked must honour the mark
        // rather than defaulting to the first entry.
        File.WriteAllText(Path.Combine(modelRoot, "ai", "providers.json"), """[{"id":"hand","name":"Hand","baseUrl":"https://example.test/v1","models":[{"name":"a"},{"name":"b","inUse":true}]}]""");
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
            if (!condition)
                throw new Exception("FAILED: " + name);
            Console.WriteLine("PASS: " + name);
        }

        var manifest = AiProviderManifest.Load();
        Assert(manifest.Count > 0, "the built-in provider manifest loads");
        var probed = manifest.Where(entry => entry.KeyValidation is not null).ToArray();
        Assert(probed.Length > 0, $"at least one preset declares a key probe (actual {probed.Length})");
        Assert(probed.All(entry => entry.KeyValidation!.IsKnown), "every declared probe is one Hub knows how to run");
        Assert(probed.All(entry => entry.AuthMethods.Contains(ProviderAuthMethods.ApiKey)), "only providers that take a key declare a probe");
        // A keyless endpoint has nothing to probe, and must not claim otherwise.
        Assert(manifest.Where(entry => !entry.AuthMethods.Contains(ProviderAuthMethods.ApiKey)).All(entry => entry.KeyValidation is null), "a provider that needs no key declares no probe");
        // The probe path must stay relative: an absolute URL would let a hand-edited manifest aim the check at
        // a host of its choosing, which is a request-forgery primitive rather than a validation feature.
        Assert(!new AiProviderKeyValidation { Type = AiProviderKeyValidation.HttpGetModels, Path = "https://evil.test/steal" }.IsKnown, "a probe path pointing at another host is rejected");
        Assert(!new AiProviderKeyValidation { Type = AiProviderKeyValidation.HttpGetModels, Path = "//evil.test/models" }.IsKnown, "a protocol-relative probe path is rejected");
        Assert(!new AiProviderKeyValidation { Type = "postTheKeySomewhere", Path = "/models" }.IsKnown, "an unknown probe type is rejected rather than ignored at run time");
        // The probe must reach the provider as a ModelProvider, since that is what the check reads at run time.
        var deepseek = AiProviderManifest.CreateBuiltIn("deepseek")!;
        Assert(deepseek.KeyValidation is { IsKnown: true }, "a declared probe survives the trip from manifest entry to provider");
        Assert(AiProviderManifest.CreateBuiltIn("ollama")!.KeyValidation is null, "a keyless provider arrives with no probe, which is what makes it skip validation");
        Console.WriteLine("PASS: key validation is opt-in per provider and fails open when undeclared.");
        // ── Which wire a provider answers on: one rule, three names, and only two of them spoken ──
        // IsKnown compares exactly, and the exactness is the load-bearing part. Real catalogs publish "openai" and
        // "anthropic" as endpoint *families* (OrcaRouter's supported_endpoint_types does, and the Copilot capture
        // carries the same two words); a family label is not a wire. Resolving "anthropic" to the messages body would
        // aim a bearer-issued credential at an endpoint that reads x-api-key, on a machine where nobody asked for that.
        Assert(new[] { ModelProtocols.Chat, ModelProtocols.Responses, ModelProtocols.Messages }.All(ModelProtocols.IsKnown), "the three wire names are all known");
        Assert(!new string? [] { "openai", "anthropic", "Messages", "chat ", "", null }.Any(ModelProtocols.IsKnown), "a family label, a wrong case, padding and silence all read as nothing declared");
        var wireMarked = new ModelProvider
        {
            Id = "custom-wire-marked",
            Name = "Marked",
            IsCustom = true,
            BaseUrl = "https://mirror.test/v1",
            Model = "claude-sonnet",
            Protocol = ModelProtocols.Messages,
        };
        wireMarked.ModelCapabilities["gpt-5"] = new ModelCapabilities
        {
            EndpointTypes = [ModelProtocols.Responses]
        };
        Assert(ChatClientFactory.ProtocolFor(wireMarked, "gpt-5") == ModelProtocols.Messages, "a wire declared on the provider outranks what the catalog said about the model");
        var wirePlain = new ModelProvider
        {
            Id = "custom-wire-plain",
            Name = "Plain",
            IsCustom = true,
            BaseUrl = "https://mirror.test/v1",
            Model = "m",
        };
        wirePlain.ModelCapabilities["solo"] = new ModelCapabilities
        {
            EndpointTypes = [ModelProtocols.Messages]
        };
        wirePlain.ModelCapabilities["both"] = new ModelCapabilities
        {
            EndpointTypes = [ModelProtocols.Chat, ModelProtocols.Messages]
        };
        wirePlain.ModelCapabilities["quiet"] = new ModelCapabilities();
        Assert(ChatClientFactory.ProtocolFor(wirePlain, "solo") == ModelProtocols.Messages, "a model that names only /v1/messages is not silently folded into chat");
        Assert(ChatClientFactory.ProtocolFor(wirePlain, "both") == ModelProtocols.Chat, "a model naming chat and messages is called on the wire this build speaks");
        Assert(ChatClientFactory.ProtocolFor(wirePlain, "quiet") == ModelProtocols.Chat, "a model nobody described stays on chat, which is every provider but Copilot");
        wirePlain.Protocol = "anthropic";
        Assert(ChatClientFactory.ProtocolFor(wirePlain, "quiet") == ModelProtocols.Chat, "a hand-typed wire name Hub does not know is nothing declared, not a fourth protocol");
        // The refusal is the point of naming a wire this build does not speak. Before it, a messages-only model
        // resolved to chat and posted an OpenAI body at the endpoint, which came back as the provider's own 404 page
        // — indistinguishable from a bad key or a dead gateway, and the one failure the settings screen could not
        // explain. The sentence has to name the wire, the provider and the way out, because it is what the transcript
        // prints after "助手请求失败：".
        string? wireRefusal = null;
        try
        {
            ChatClientFactory.Create(wireMarked, "claude-sonnet");
        }
        catch (InvalidOperationException exception)
        {
            wireRefusal = exception.Message;
        }

        Assert(wireRefusal is not null && wireRefusal.Contains("/v1/messages", StringComparison.Ordinal) && wireRefusal.Contains("OpenAI", StringComparison.Ordinal) && wireRefusal.Contains("Marked", StringComparison.Ordinal), "a provider on the messages wire is refused by name instead of sent an OpenAI body");
        Assert(ChatClientFactory.Create(wirePlain, "quiet")is not null, "nothing else changed: an undeclared provider still builds a client on chat");
        // Persisted, not derived: this is the one manifest-shaped wire fact a person must be able to state about an
        // endpoint Hub does not ship. The round trip through the real store is what separates it from ExtraHeaders
        // and RequestSemantics, which are facts about a service and are refreshed rather than remembered.
        var wireRoot = Path.Combine(root, "wire-roundtrip");
        Directory.CreateDirectory(wireRoot);
        var wireStore = new ProviderStore(wireRoot);
        wireStore.Save([new ModelProvider { Id = "saved-wire", Name = "Saved wire", IsCustom = true, BaseUrl = "https://mirror.test/v1", Model = "m", Protocol = ModelProtocols.Messages, }]);
        Assert(wireStore.Load().Single().Protocol == ModelProtocols.Messages, "a declared wire survives providers.json, because only the person on the endpoint can say it");
        // ── The claim is made for the endpoint; a per-model entry is the exception, and the specific one wins ──
        // Provider-wide wire declarations are valid for aggregating gateways; per-model declarations remain the
        // more specific override for endpoints that mix wires.
        var shippedPresets = AiProviderManifest.Load();
        Assert(shippedPresets.All(entry => entry.Protocol is null || ModelProtocols.IsKnown(entry.Protocol)), "every wire a preset declares for an endpoint is one this build speaks, checked at the source rather than on the provider");
        Assert(shippedPresets.SelectMany(entry => entry.ModelProtocols.Values).All(ModelProtocols.IsKnown), "every per-model wire a preset names is one this build speaks, checked at the source rather than on the provider");
        Assert(AiProviderManifest.CreateBuiltIn("deepseek")!.Protocol is null, "a preset that says nothing arrives with no wire rather than a guessed one");

        // A per-model line still outranks the endpoint: it is the more specific claim and the escape hatch a chat-only
        // model needs. Three edges ride along — the id matches without case, an entry naming a model the provider has
        // retired answers for nothing else, and a value Hub does not know is dropped instead of becoming a fourth wire.
        var wiredMixed = new ModelProvider
        {
            Id = "custom-wired-mixed",
            Name = "Mixed",
            IsCustom = true,
            BaseUrl = "https://mirror.test/v1",
            Model = "gpt-6.1-sol",
            Protocol = ModelProtocols.Responses,
        };
        wiredMixed.DeclaredProtocols["legacy-chat"] = ModelProtocols.Chat;
        wiredMixed.DeclaredProtocols["retired-model"] = ModelProtocols.Messages;
        wiredMixed.DeclaredProtocols["another-retired"] = "anthropic";
        Assert(ChatClientFactory.ProtocolFor(wiredMixed, "legacy-chat") == ModelProtocols.Chat
               && ChatClientFactory.ProtocolFor(wiredMixed, "LEGACY-CHAT") == ModelProtocols.Chat
               && ChatClientFactory.ProtocolFor(wiredMixed, "retired-model") == ModelProtocols.Messages
               && ChatClientFactory.ProtocolFor(wiredMixed, "gpt-6.1-sol") == ModelProtocols.Responses
               && ChatClientFactory.ProtocolFor(wiredMixed, "another-retired") == ModelProtocols.Responses,
            "a per-model entry outranks the endpoint, an unknown wire name is dropped, and a stale entry only ever answers for its own id (legacy-chat="
            + ChatClientFactory.ProtocolFor(wiredMixed, "legacy-chat")
            + " gpt-6.1-sol=" + ChatClientFactory.ProtocolFor(wiredMixed, "gpt-6.1-sol")
            + " another-retired=" + ChatClientFactory.ProtocolFor(wiredMixed, "another-retired") + ")");

        // The browser-signed provider is the one place nothing may override: the OAuth bundle only ever goes to the
        // responses wire, so a manifest line or a per-model entry naming something else must not move it.
        var oauthWired = AiProviderManifest.CreateBuiltIn("openai")
                         ?? throw new Exception("openai is missing from ai-providers.json.");
        oauthWired.Credential = new ProviderCredential { Id = "wire-oauth", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = "bundle" };
        oauthWired.DeclaredProtocols["gpt-anything"] = ModelProtocols.Chat;
        Assert(oauthWired.IsOpenAiOAuth && ChatClientFactory.ProtocolFor(oauthWired, "gpt-anything") == ModelProtocols.Responses,
            "a browser-signed provider stays on the responses wire whatever a manifest or a model entry says (wire="
            + ChatClientFactory.ProtocolFor(oauthWired, "gpt-anything") + ")");
        // ── The declared static headers reach the requests the app builds itself ──
        // A gateway that filters clients answers a headerless request the way it answers a stranger. On the catalog
        // that reads as "unreachable" — a provider with no models and no explanation — and on the key probe as a
        // 401, which this app reads as "the key was rejected", the one verdict that blocks the auth dialog for a
        // perfectly good key. Both call sites go through ProviderStaticHeaders now, so both are measured rather
        // than assumed: the shared source because it was the one missing them, Copilot's because its hand-written
        // loop was replaced by that rule and must not have lost what it did.
        var copilotCatalog = AiProviderManifest.CreateBuiltIn("github-copilot")
                             ?? throw new Exception("github-copilot is missing from ai-providers.json.");
        copilotCatalog.Credential = new ProviderCredential { Id = "catalog-probe", ProviderId = copilotCatalog.Id, Secret = "gh-test" };
        var copilotRecorder = new RequestRecorder(
            System.Text.Encoding.UTF8.GetBytes("""{"data":[{"id":"gpt-6-luna","capabilities":{"type":"chat"}}]}"""));
        using (var recorder = new HttpClient(copilotRecorder))
        {
            await new GithubCopilotModelsSource().FetchAsync(recorder, copilotCatalog, CancellationToken.None);
            var sent = copilotRecorder.Requests.FirstOrDefault();
            var declaredCopilot = copilotCatalog.ExtraHeaders.GetValueOrDefault("user-agent");
            // .NET parses User-Agent into product tokens and hands them back separated by ", ", so a declared value
            // that contains a space returns as "Axmol, Hub". That normalisation is the platform's and is identical on
            // the old hand-written loop; what this assertion is about is that the declared value still gets there.
            Assert(sent is not null && declaredCopilot is { Length: > 0 }
                   && sent["user-agent"]?.Replace(", ", " ") == declaredCopilot
                   && sent["x-github-api-version"] == "2026-06-01"
                   && sent["authorization"] == "Bearer gh-test",
                "moving Copilot's catalog headers onto the shared rule kept them all (user-agent="
                + (sent?["user-agent"] ?? "absent") + " declared=" + (declaredCopilot ?? "none")
                + " x-github-api-version=" + (sent?["x-github-api-version"] ?? "absent")
                + " authorization=" + (sent?["authorization"] ?? "absent") + ")");
        }

        // ── A plain API key on the responses wire posts to {baseUrl}/responses, exactly once ──
        // Nothing else in the suite pins this. The offline web-search check hands the SDK an endpoint that already
        // ends in "/responses" and routes on a substring, so a doubled segment would pass it, and the live-shaped
        // request-stability check reaches the responses wire through the OAuth transport, which rewrites the endpoint
        // to the Codex host. A provider configured with baseUrl ".../v1" is the case a person actually has, and
        var pathProvider = new ModelProvider
        {
            Id = "custom-responses-path",
            Name = "Responses path",
            IsCustom = true,
            BaseUrl = "https://gateway.test/v1",
            Model = "gpt-6.1-sol",
            Protocol = ModelProtocols.Responses,
        };
        var pathRecorder = new RequestRecorder(System.Text.Encoding.UTF8.GetBytes("{}"));
        using (var pathClient = ChatClientFactory.Create(pathProvider, null, null, transport: pathRecorder))
        {
            try
            {
                await foreach (var _ in pathClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
            }
            catch (Exception)
            {
                // The canned body is not a responses stream, so the call cannot finish. What is under test is the
                // request it made on the way in, which the transport has already recorded.
            }
        }

        var responsesPath = pathRecorder.Requests.FirstOrDefault()?.Path;
        Assert(responsesPath == "/v1/responses",
            "a provider on the responses wire posts to its own base URL plus /responses, once (path="
            + (responsesPath ?? "none") + " requests=" + pathRecorder.Requests.Count + ")");
        return;
    }
}
