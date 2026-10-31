using System.Net;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

internal static class OpenAiChecks
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        Console.WriteLine("PASS: " + name);
    }
    private static async Task Fails(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or SecretStoreException)
        { Check(true, name); return; }
        throw new Exception("FAILED: " + name);
    }
    private static Dictionary<string, string> Query(string url)
        => url[(url.IndexOf('?') + 1)..].Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string TokenBody(string access = "access-new", string refresh = "refresh-new")
        => JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, expires_in = 3600 });

    // File-backed fixture only: synthetic credentials never leave the test directory.
    private sealed class ProcessSecretStore(string root) : ISecretStore
    {
        private string PathFor(string id) => Path.Combine(root, id + ".secret");
        public string? Read(string id) => File.Exists(PathFor(id)) ? File.ReadAllText(PathFor(id)) : null;
        public void Write(string id, string value) => File.WriteAllText(PathFor(id), value);
        public void Delete(string id) { if (File.Exists(PathFor(id))) File.Delete(PathFor(id)); }
    }

    public static async Task RefreshWorkerAsync(string root, string id)
    {
        var store = new ProcessSecretStore(root);
        var credential = new ProviderCredential { Id = "account", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = store.Read("account") };
        var provider = AiProviderManifest.CreateBuiltIn("openai")!;
        provider.Credential = credential;
        File.WriteAllText(Path.Combine(root, id + ".ready"), "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(root, "go"))) await Task.Delay(20, timeout.Token);
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            File.WriteAllText(Path.Combine(root, id + ".refreshed"), "called");
            await Task.Delay(150, ct);
            return Json("""{"access_token":"rotated","refresh_token":"rotated","token_type":"Bearer","expires_in":3600}""");
        }));
        var session = new OpenAiDynamicTokenSession(provider, credential, store,
            new OpenAiDynamicTokenExchange(http, OpenAiIdentityValidator.Create()), root);
        var result = await session.GetAsync(false, timeout.Token);
        if (result.RefreshToken != "rotated") throw new InvalidOperationException("Refresh worker saw the wrong token.");
    }

    public static async Task MetadataWorkerAsync(string root, string[] args)
    {
        var id = args[^1];
        var store = new CredentialStore(root, new MacKeychainSecretStore(root, new KeychainFake()));
        var credentials = store.Load();
        File.WriteAllText(Path.Combine(root, id + ".ready"), "ready");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(Path.Combine(root, "go")))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Metadata test barrier timed out.");
            await Task.Delay(20);
        }
        credentials.Add(new ProviderCredential { Id = id, ProviderId = "openai", Label = id });
        store.Save(credentials, false);
    }

    public static async Task RunAsync(string root, bool native, bool loopback = false)
    {
        var scratch = Path.Combine(root, "openai-" + Guid.NewGuid().ToString("N"));
        var api = new KeychainFake();
        var store = new MacKeychainSecretStore(scratch, api);
        Check(store.Read("a") is null, "missing Keychain item is absent");
        store.Write("a", "first"); store.Write("a", "updated");
        Check(store.Read("a") == "updated" && api.Adds == 1 && api.Updates == 2, "Keychain creates then updates a generic password");
        Check(store.Service != new MacKeychainSecretStore(scratch + "-other", api).Service,
            "different data roots use different Keychain services");
        Check(store.Service == new MacKeychainSecretStore(Path.Combine(scratch, "."), api).Service,
            "normalized data roots share their Keychain service");
        store.Delete("a"); store.Delete("a");
        Check(store.Read("a") is null && store.Descriptor.Persists, "Keychain delete is idempotent and backend persists");
        var metadata = new ProviderCredential { Id = "a", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = "secret" };
        new CredentialStore(scratch, store).Save([metadata]);
        api.Status = -25308;
        Check(new CredentialStore(scratch, store).Load().Single() is { Id: "a", Secret: null }, "locked Keychain preserves credential metadata on startup");
        await Fails(() => { store.Write("a", "replacement"); return Task.CompletedTask; }, "locked Keychain writes fail explicitly");
        await Fails(() => { store.Delete("a"); return Task.CompletedTask; }, "denied Keychain deletion fails explicitly");
        api.Status = 0;
        store.Delete("a");
        if (native && OperatingSystem.IsMacOS())
        {
            var real = new MacKeychainSecretStore(scratch);
            try
            {
                real.Write("native-check", "unicode-秘密\nline2");
                Check(real.Read("native-check") == "unicode-秘密\nline2", "native Keychain round-trips Unicode and LF");
                real.Write("native-check", "rotated");
                Check(real.Read("native-check") == "rotated", "native Keychain updates an existing secret");
            }
            finally { real.Delete("native-check"); }
            Check(real.Read("native-check") is null, "native Keychain test item is removed");
        }

        // Lost metadata additions make other accounts disappear; conflicting edits must not silently win.
        var firstStore = new CredentialStore(scratch, store);
        var secondStore = new CredentialStore(scratch, store);
        var firstList = firstStore.Load();
        var secondList = secondStore.Load();
        firstList.Add(new ProviderCredential { Id = "first", ProviderId = "openai", Label = "first" });
        secondList.Add(new ProviderCredential { Id = "second", ProviderId = "openai", Label = "second" });
        firstStore.Save(firstList, false);
        secondStore.Save(secondList, false);
        secondStore.Save(secondList, false);
        Check(new CredentialStore(scratch, store).Load().Any(c => c.Id == "first")
            && new CredentialStore(scratch, store).Load().Any(c => c.Id == "second"),
            "independent metadata additions survive subsequent stale saves");
        var staleStore = new CredentialStore(scratch, store);
        var currentStore = new CredentialStore(scratch, store);
        var staleList = staleStore.Load();
        var currentList = currentStore.Load();
        staleList.Single(c => c.Id == "first").Label = "stale";
        currentList.Single(c => c.Id == "first").Label = "current";
        currentStore.Save(currentList, false);
        await Fails(() => { staleStore.Save(staleList, false); return Task.CompletedTask; },
            "conflicting metadata edits are rejected instead of overwriting another instance");

        var processRoot = Path.Combine(scratch, "metadata-processes");
        Directory.CreateDirectory(processRoot);
        System.Diagnostics.Process StartWorker(string id)
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(processRoot);
            start.ArgumentList.Add("--openai-metadata-worker");
            start.ArgumentList.Add(id);
            return System.Diagnostics.Process.Start(start)!;
        }
        using (var workerA = StartWorker("process-a"))
        using (var workerB = StartWorker("process-b"))
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(Path.Combine(processRoot, "process-a.ready")) || !File.Exists(Path.Combine(processRoot, "process-b.ready")))
                {
                    if (workerA.HasExited || workerB.HasExited || DateTime.UtcNow > deadline)
                        throw new InvalidOperationException("Metadata worker could not reach the test barrier.");
                    await Task.Delay(20);
                }
                File.WriteAllText(Path.Combine(processRoot, "go"), "go");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await Task.WhenAll(workerA.WaitForExitAsync(timeout.Token), workerB.WaitForExitAsync(timeout.Token));
                var records = new CredentialStore(processRoot, store).Load();
                Check(workerA.ExitCode == 0 && workerB.ExitCode == 0
                    && records.Any(c => c.Id == "process-a") && records.Any(c => c.Id == "process-b"),
                    "two separate processes preserve concurrent credential metadata additions");
            }
            finally
            {
                if (!workerA.HasExited) workerA.Kill(entireProcessTree: true);
                if (!workerB.HasExited) workerB.Kill(entireProcessTree: true);
            }
        }

        var refreshRoot = Path.Combine(scratch, "refresh-processes");
        Directory.CreateDirectory(refreshRoot);
        var processToken = new OpenAiTokens("expired", "old", null, DateTimeOffset.UtcNow.AddMinutes(-1), null)
        { Version = 1, ClientId = "oaiapp_test", HostId = "urn:uuid:test", Issuer = "https://auth.openai.com", Subject = "subject" };
        new ProcessSecretStore(refreshRoot).Write("account", processToken.Encode());
        System.Diagnostics.Process StartRefresh(string id)
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(refreshRoot); start.ArgumentList.Add("--openai-refresh-worker"); start.ArgumentList.Add(id);
            return System.Diagnostics.Process.Start(start)!;
        }
        using (var a = StartRefresh("a"))
        using (var b = StartRefresh("b"))
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(Path.Combine(refreshRoot, "a.ready")) || !File.Exists(Path.Combine(refreshRoot, "b.ready")))
                {
                    if (a.HasExited || b.HasExited || DateTime.UtcNow > deadline) throw new InvalidOperationException("Refresh workers failed to start.");
                    await Task.Delay(20);
                }
                File.WriteAllText(Path.Combine(refreshRoot, "go"), "go");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await Task.WhenAll(a.WaitForExitAsync(timeout.Token), b.WaitForExitAsync(timeout.Token));
                Check(a.ExitCode == 0 && b.ExitCode == 0 && Directory.GetFiles(refreshRoot, "*.refreshed").Length == 1
                    && OpenAiTokens.Decode(new ProcessSecretStore(refreshRoot).Read("account")!).RefreshToken == "rotated",
                    "two processes serialize token rotation and the second reloads the updated token");
            }
            finally
            {
                if (!a.HasExited) a.Kill(entireProcessTree: true);
                if (!b.HasExited) b.Kill(entireProcessTree: true);
            }
        }

        var selectionRoot = Path.Combine(scratch, "selection-race");
        var initialSelection = new ProviderStore(selectionRoot);
        initialSelection.Save([new ModelProvider { Id = "openai", SelectedCredentialId = "one" }]);
        var selectionA = new ProviderStore(selectionRoot);
        var selectionB = new ProviderStore(selectionRoot);
        var providersA = selectionA.Load();
        var providersB = selectionB.Load();
        providersA[0].SelectedCredentialId = "two";
        selectionA.Save(providersA);
        providersB[0].Name = "unrelated edit";
        selectionB.Save(providersB);
        Check(new ProviderStore(selectionRoot).Load()[0].SelectedCredentialId == "two",
            "stale provider metadata does not revert another process's credential selection");
        providersB[0].SelectedCredentialId = "three";
        await Fails(() => { selectionB.Save(providersB); return Task.CompletedTask; },
            "concurrent credential selection conflicts are rejected");

        var hostStore = new OpenAiHostIdStore(scratch);
        var hostIds = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(hostStore.GetOrCreate)));
        Check(hostIds.Distinct().Count() == 1 && hostIds[0].StartsWith("urn:uuid:")
            && new OpenAiHostIdStore(scratch).GetOrCreate() == hostIds[0],
            "OpenAI host identifier is stable across concurrent creation and reload");
        var hostPath = Path.Combine(scratch, "ai", "openai-host-id");
        File.WriteAllText(hostPath, "invalid\n");
        await Fails(() => { hostStore.GetOrCreate(); return Task.CompletedTask; }, "invalid host identifier is not silently replaced");
        Check(File.ReadAllText(hostPath) == "invalid\n", "invalid host identifier remains available for recovery");

        var legacyTokens = new OpenAiTokens("access", "refresh", null, DateTimeOffset.UtcNow, null);
        var dynamicTokens = legacyTokens with { Version = 1, ClientId = "oaiapp_test", HostId = "urn:uuid:test",
            Issuer = "https://auth.openai.com", Subject = "subject", Scope = "openid chatgpt.tokens.use.direct" };
        Check(!OpenAiTokens.Decode(legacyTokens.Encode()).IsDynamicRegistration
            && OpenAiTokens.Decode(dynamicTokens.Encode()).HasPlanUsage
            && !(dynamicTokens with { Scope = "openid" }).HasPlanUsage
            && !(dynamicTokens with { ClientId = "dynamic_agent_client" }).IsDynamicRegistration,
            "OpenAI credential versions distinguish legacy and authorized dynamic registrations");

        const string redirect = "http://127.0.0.1:1455/auth/callback";
        var newAuth = Query(OpenAiDynamicRegistration.AuthorizationUrl(redirect, "urn:uuid:test", "state", "nonce", "challenge"));
        Check(newAuth["client_id"] == "dynamic_agent_client" && newAuth["agent_name_hint"] == "Axmol Hub"
            && newAuth["ext_agent_host_id"] == "urn:uuid:test" && newAuth["redirect_uri"] == redirect
            && newAuth["resource"] == OpenAiDynamicRegistration.Resource && newAuth["nonce"] == "nonce"
            && newAuth["scope"] == OpenAiDynamicRegistration.Scope && newAuth["code_challenge_method"] == "S256",
            "dynamic registration authorization includes required transaction parameters");
        var returningAuth = Query(OpenAiDynamicRegistration.AuthorizationUrl(redirect, "urn:uuid:test", "state", "nonce", "challenge", dynamicTokens));
        Check(returningAuth["client_id"] == "oaiapp_test" && !returningAuth.ContainsKey("agent_name_hint")
            && !returningAuth.ContainsKey("prompt")
            && Query(OpenAiDynamicRegistration.AuthorizationUrl(redirect, "urn:uuid:test", "state", "nonce", "challenge", dynamicTokens, true))["prompt"] == "consent",
            "returning authorization reuses the selected registration");
        var dynamicCallback = redirect + "?code=code&state=state&client_id=oaiapp_test";
        Check(OpenAiDynamicRegistration.ReadCallback(dynamicCallback, redirect, "state") == ("code", "oaiapp_test")
            && OpenAiDynamicRegistration.ReadCallback(redirect + "?code=code&state=state", redirect, "state", "oaiapp_test") == ("code", "oaiapp_test"),
            "dynamic callbacks accept an issued ID or the selected returning registration");
        foreach (var invalid in new[] { redirect + "?code=code&state=wrong&client_id=oaiapp_test",
            redirect + "?code=code&state=state", redirect + "?code=code&state=state&client_id=dynamic_agent_client",
            redirect + "?code=code&state=state&state=state&client_id=oaiapp_test",
            "http://localhost:1455/auth/callback?code=code&state=state&client_id=oaiapp_test",
            redirect + "?error=access_denied&state=state&code=code&client_id=oaiapp_test" })
            await Fails(() => { OpenAiDynamicRegistration.ReadCallback(invalid, redirect, "state"); return Task.CompletedTask; }, "invalid dynamic callback is rejected");
        await Fails(() => { OpenAiDynamicRegistration.ReadCallback(dynamicCallback, redirect, "state", "oaiapp_other"); return Task.CompletedTask; },
            "returning callback cannot replace the selected registration");

        using var signingRsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(signingRsa) { KeyId = "test-key" };
        var publicKey = JsonWebKeyConverter.ConvertFromRSASecurityKey(signingKey);
        var jwksJson = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test-key", n = publicKey.N, e = publicKey.E, alg = "RS256", use = "sig" } } });
        using var jwksHttp = new HttpClient(new Handler((request, token) =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://auth.openai.com/.well-known/jwks.json", "identity verification fetches OpenAI signing keys");
            return Task.FromResult(Json(jwksJson));
        }));
        var validator = new OpenAiIdentityValidator(jwksHttp);
        string IdentityToken(string issuer, string audience, string nonce, string subject, DateTime expires, SecurityKey key)
            => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer, Audience = audience, Expires = expires, IssuedAt = DateTime.UtcNow,
                Claims = new Dictionary<string, object> { ["sub"] = subject, ["nonce"] = nonce, ["email"] = "user@example.com" },
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            });
        var identityToken = IdentityToken("https://auth.openai.com", "oaiapp_test", "nonce", "subject", DateTime.UtcNow.AddMinutes(5), signingKey);
        var identity = await validator.ValidateAsync(identityToken, "oaiapp_test", "nonce", "subject", CancellationToken.None);
        Check(identity == ("subject", "user@example.com"), "identity validation returns only verified identity claims");
        await Fails(async () => await validator.ValidateAsync(identityToken, "oaiapp_test", "wrong", null, CancellationToken.None), "identity validation rejects wrong nonce");
        await Fails(async () => await validator.ValidateAsync(identityToken, "oaiapp_other", "nonce", null, CancellationToken.None), "identity validation rejects wrong audience");
        await Fails(async () => await validator.ValidateAsync(identityToken, "oaiapp_test", "nonce", "other", CancellationToken.None), "identity validation rejects changed account");
        var wrongIssuer = IdentityToken("https://other.example", "oaiapp_test", "nonce", "subject", DateTime.UtcNow.AddMinutes(5), signingKey);
        await Fails(async () => await validator.ValidateAsync(wrongIssuer, "oaiapp_test", "nonce", null, CancellationToken.None), "identity validation rejects wrong issuer");
        var expiredIdentity = IdentityToken("https://auth.openai.com", "oaiapp_test", "nonce", "subject", DateTime.UtcNow.AddMinutes(-5), signingKey);
        await Fails(async () => await validator.ValidateAsync(expiredIdentity, "oaiapp_test", "nonce", null, CancellationToken.None), "identity validation rejects expired tokens");
        var forgedIdentity = IdentityToken("https://auth.openai.com", "oaiapp_test", "nonce", "subject", DateTime.UtcNow.AddMinutes(5), new RsaSecurityKey(otherRsa) { KeyId = "test-key" });
        await Fails(async () => await validator.ValidateAsync(forgedIdentity, "oaiapp_test", "nonce", null, CancellationToken.None), "identity validation rejects forged signatures");

        var grants = new List<Dictionary<string, string>>();
        using var exchangeHttp = new HttpClient(new Handler(async (request, ct) =>
        {
            Check(request.RequestUri!.AbsoluteUri == OpenAiDynamicRegistration.TokenEndpoint, "dynamic exchange targets the accounts token endpoint");
            var fields = Query("?" + await request.Content!.ReadAsStringAsync(ct));
            grants.Add(fields);
            return Json(JsonSerializer.Serialize(new { access_token = "new-access", refresh_token = "new-refresh", id_token = identityToken,
                scope = "openid chatgpt.tokens.use.direct", token_type = "Bearer", expires_in = 3600 }));
        }));
        var exchange = new OpenAiDynamicTokenExchange(exchangeHttp, validator);
        var exchanged = await exchange.ExchangeCodeAsync("code", "verifier", redirect, "oaiapp_test", "urn:uuid:test", "nonce", null, CancellationToken.None);
        Check(exchanged.HasPlanUsage && exchanged.Subject == "subject" && exchanged.RefreshToken == "new-refresh"
            && grants[0]["client_id"] == "oaiapp_test" && grants[0]["resource"] == OpenAiDynamicRegistration.Resource
            && grants[0]["redirect_uri"] == redirect && grants[0]["code_verifier"] == "verifier",
            "dynamic code exchange uses issued registration and stores verified identity and scope");
        var refreshedDynamic = await exchange.RefreshAsync(exchanged, CancellationToken.None);
        Check(refreshedDynamic.RefreshToken == "new-refresh" && refreshedDynamic.Subject == "subject"
            && grants[1]["grant_type"] == "refresh_token" && grants[1]["client_id"] == "oaiapp_test"
            && grants[1]["refresh_token"] == "new-refresh" && !grants[1].ContainsKey("scope"),
            "dynamic refresh retains registration and uses the issued client ID");
        await Fails(async () => await exchange.ExchangeCodeAsync("code", "verifier", redirect, "oaiapp_test", "urn:uuid:test", "wrong", null, CancellationToken.None),
            "dynamic code exchange cannot return an unverified identity");
        await Fails(async () => await exchange.RefreshAsync(legacyTokens, CancellationToken.None), "legacy credentials cannot use dynamic refresh");
        foreach (var invalidExpiry in new[] { "null", "\"3600\"", "0", "-1", "{}" })
        {
            using var invalidExpiryHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(
                "{\"access_token\":\"a\",\"refresh_token\":\"r\",\"token_type\":\"Bearer\",\"expires_in\":" + invalidExpiry + "}"))));
            var invalidExchange = new OpenAiDynamicTokenExchange(invalidExpiryHttp, validator);
            await Fails(async () => await invalidExchange.RefreshAsync(exchanged, default), "dynamic refresh rejects invalid expiration values");
        }

        foreach (var code in new[] { "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired", "refresh_token_reused", "refresh_token_invalidated" })
        {
            using var errorHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { error = code }), HttpStatusCode.BadRequest))));
            var requiresLogin = false;
            try { await new OpenAiDynamicTokenExchange(errorHttp, validator).RefreshAsync(exchanged, default); }
            catch (OpenAiReauthenticationException) { requiresLogin = true; }
            Check(requiresLogin, "dynamic refresh requires sign-in for " + code);
        }
        using (var invalidClientHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json("""{"error":"invalid_client"}""", HttpStatusCode.Unauthorized)))))
        {
            Exception? error = null;
            try { await new OpenAiDynamicTokenExchange(invalidClientHttp, validator).RefreshAsync(exchanged, default); }
            catch (InvalidOperationException ex) { error = ex; }
            Check(error is not null and not OpenAiReauthenticationException && error.Message.Contains("invalid_client"),
                "dynamic refresh distinguishes invalid client configuration from expired user authorization");
        }

        var revocationPosts = 0;
        using (var revokeHttp = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"issuer":"https://auth.openai.com","revocation_endpoint":"https://auth.openai.com/api/accounts/oauth/revoke"}""");
            revocationPosts++;
            var fields = Query("?" + await request.Content!.ReadAsStringAsync(ct));
            Check(request.RequestUri!.AbsoluteUri == "https://auth.openai.com/api/accounts/oauth/revoke"
                && fields["token"] == "new-refresh" && fields["client_id"] == "oaiapp_test"
                && fields["token_type_hint"] == "refresh_token", "revocation posts only the selected registration's refresh token");
            return new HttpResponseMessage(revocationPosts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        })))
        {
            var revocation = new OpenAiSessionRevocation(revokeHttp);
            Check(await revocation.RevokeAsync(exchanged, default) && revocationPosts == 2,
                "revocation retries server errors and recognizes an empty successful response");
            Check(!await revocation.RevokeAsync(legacyTokens, default) && revocationPosts == 2,
                "revocation never sends a legacy refresh token");
        }
        using (var hostileHttp = new HttpClient(new Handler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get, "untrusted revocation discovery cannot receive a token");
            return Task.FromResult(Json("""{"issuer":"https://auth.openai.com","revocation_endpoint":"https://attacker.example/revoke"}"""));
        })))
            Check(!await new OpenAiSessionRevocation(hostileHttp).RevokeAsync(exchanged, default), "revocation rejects an untrusted discovered endpoint");

        var expiringDynamic = exchanged with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var dynamicCredentialA = new ProviderCredential { Id = "dynamic-refresh", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = expiringDynamic.Encode() };
        var dynamicCredentialB = new ProviderCredential { Id = "dynamic-refresh", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = expiringDynamic.Encode() };
        var dynamicProviderA = AiProviderManifest.CreateBuiltIn("openai")!;
        var dynamicProviderB = AiProviderManifest.CreateBuiltIn("openai")!;
        dynamicProviderA.Credential = dynamicCredentialA;
        dynamicProviderB.Credential = dynamicCredentialB;
        store.Write(dynamicCredentialA.Id, dynamicCredentialA.Secret!);
        var dynamicRefreshes = 0;
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var rotatingHttp = new HttpClient(new Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref dynamicRefreshes);
            refreshEntered.TrySetResult();
            await refreshRelease.Task.WaitAsync(ct);
            return Json(JsonSerializer.Serialize(new { access_token = "rotated-access", refresh_token = "rotated-refresh", token_type = "Bearer", expires_in = 3600 }));
        })))
        {
            var rotatingExchange = new OpenAiDynamicTokenExchange(rotatingHttp, validator);
            var dynamicSessionA = new OpenAiDynamicTokenSession(dynamicProviderA, dynamicCredentialA, store, rotatingExchange, scratch);
            var dynamicSessionB = new OpenAiDynamicTokenSession(dynamicProviderB, dynamicCredentialB, store, rotatingExchange, scratch);
            var refreshA = dynamicSessionA.GetAsync(false, default);
            await refreshEntered.Task;
            var refreshB = dynamicSessionB.GetAsync(false, default);
            refreshRelease.SetResult();
            await Task.WhenAll(refreshA, refreshB);
            Check(dynamicRefreshes == 1 && refreshB.Result.RefreshToken == "rotated-refresh"
                && OpenAiTokens.Decode(store.Read(dynamicCredentialA.Id)!).RefreshToken == "rotated-refresh",
                "dynamic sessions coordinate rotating refresh and reload the saved credential");
        }
        store.Delete(dynamicCredentialA.Id);
        dynamicCredentialA.Secret = expiringDynamic.Encode();
        dynamicCredentialA.OpenAiRegistration = OpenAiRegistration.FromTokens(expiringDynamic);
        store.Write(dynamicCredentialA.Id, dynamicCredentialA.Secret);
        using (var terminalHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)))))
        {
            var terminalSession = new OpenAiDynamicTokenSession(dynamicProviderA, dynamicCredentialA, store,
                new OpenAiDynamicTokenExchange(terminalHttp, validator), scratch);
            await Fails(async () => await terminalSession.GetAsync(false, default), "terminal refresh asks for sign-in");
            Check(store.Read(dynamicCredentialA.Id) is null && dynamicCredentialA.Secret is null
                && dynamicCredentialA.OpenAiRegistration?.ClientId == expiringDynamic.ClientId,
                "terminal refresh clears unusable tokens without losing the registration");
            await Fails(async () => await terminalSession.GetAsync(false, default), "revoked dynamic credentials stay revoked");
        }

        // A login or sign-out while a refresh is in flight must not be overwritten by its result.
        foreach (var replaceProvider in new[] { false, true })
        {
            var raceCredential = new ProviderCredential { Id = "dynamic-race-" + replaceProvider, ProviderId = "openai", AuthKind = "openaiOAuth", Secret = expiringDynamic.Encode() };
            var raceProvider = AiProviderManifest.CreateBuiltIn("openai")!;
            raceProvider.Credential = raceCredential;
            store.Write(raceCredential.Id, raceCredential.Secret!);
            var raceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var raceRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var raceHttp = new HttpClient(new Handler(async (_, ct) =>
            {
                raceEntered.TrySetResult();
                await raceRelease.Task.WaitAsync(ct);
                return Json(JsonSerializer.Serialize(new { access_token = "stale", refresh_token = "stale-refresh", token_type = "Bearer", expires_in = 3600 }));
            }));
            var raceSession = new OpenAiDynamicTokenSession(raceProvider, raceCredential, store, new OpenAiDynamicTokenExchange(raceHttp, validator), scratch);
            var pendingRefresh = raceSession.GetAsync(false, default);
            await raceEntered.Task;
            var replacement = (exchanged with { AccessToken = "replacement", RefreshToken = "replacement-refresh" }).Encode();
            if (replaceProvider)
                raceProvider.Credential = new ProviderCredential { Id = "other", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = replacement };
            else
            {
                store.Write(raceCredential.Id, replacement);
                raceCredential.Secret = replacement;
            }
            raceRelease.SetResult();
            await Fails(async () => await pendingRefresh, "dynamic refresh refuses to overwrite a replacement credential");
            Check(replaceProvider ? store.Read(raceCredential.Id) == expiringDynamic.Encode() : store.Read(raceCredential.Id) == replacement,
                "dynamic refresh leaves credential storage unchanged after a concurrent replacement");
            store.Delete(raceCredential.Id);
        }

        string? loginNonce = null;
        string? loginChallenge = null;
        var loginExchangeCount = 0;
        using (var loginHttp = new HttpClient(new Handler(async (request, ct) =>
        {
            loginExchangeCount++;
            var fields = Query("?" + await request.Content!.ReadAsStringAsync(ct));
            Check(fields["client_id"] == "oaiapp_test" && fields["code"] == "login-code"
                && OAuthPkce.CodeChallenge(fields["code_verifier"]) == loginChallenge,
                "dynamic login exchanges the callback code with its original PKCE verifier");
            var signed = IdentityToken("https://auth.openai.com", "oaiapp_test", loginNonce!, "subject", DateTime.UtcNow.AddMinutes(5), signingKey);
            return Json(JsonSerializer.Serialize(new { access_token = "access", refresh_token = "refresh", id_token = signed,
                scope = "openid chatgpt.tokens.use.direct", token_type = "Bearer", expires_in = 3600 }));
        })))
        {
            var login = new OpenAiDynamicLogin(new OpenAiDynamicTokenExchange(loginHttp, validator));
            var result = await login.SignInAsync("urn:uuid:test", null, attempt =>
            {
                var parameters = Query(attempt.AuthorizationUrl);
                loginNonce = parameters["nonce"];
                loginChallenge = parameters["code_challenge"];
                Check(!attempt.Submit(redirect + "?code=login-code&state=wrong&client_id=oaiapp_test"), "dynamic login ignores mismatched callbacks");
                Check(!attempt.Submit("https://evil.example/auth/callback?code=login-code&state=" + parameters["state"] + "&client_id=oaiapp_test"), "dynamic login rejects foreign callback origins");
                Check(attempt.Submit(redirect + "?code=login-code&state=" + parameters["state"] + "&client_id=oaiapp_test"), "dynamic login accepts matching manual callback");
                Check(!attempt.Submit(redirect + "?code=login-code&state=" + parameters["state"] + "&client_id=oaiapp_test"), "dynamic login exchanges a callback only once");
            }, _ => throw new System.ComponentModel.Win32Exception("browser unavailable"), CancellationToken.None);
            Check(result.Subject == "subject" && result.HasPlanUsage, "dynamic login returns verified registration after manual callback when browser launch fails");
            var callsBeforeDenial = loginExchangeCount;
            await Fails(async () => await login.SignInAsync("urn:uuid:test", null, attempt =>
            {
                var state = Query(attempt.AuthorizationUrl)["state"];
                Check(attempt.Submit(redirect + "?error=access_denied&state=" + state), "dynamic login accepts a state-validated denial");
            }, _ => { }, CancellationToken.None), "dynamic login reports a denied authorization");
            Check(loginExchangeCount == callsBeforeDenial, "authorization denial does not exchange a code");
        }

        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Check(OAuthPkce.CodeChallenge(verifier) == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE matches RFC 7636 vector");
        var silentExchange = new OpenAiDynamicTokenExchange(
            new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No exchange is expected."))), validator);
        using (var cancel = new CancellationTokenSource())
            await Fails(async () => await new OpenAiDynamicLogin(silentExchange).SignInAsync(
                "urn:uuid:test", null, _ => cancel.Cancel(), _ => { }, cancel.Token),
                "cancelled dynamic login exits without an exchange");
        await Fails(async () => await new OpenAiDynamicLogin(silentExchange).SignInAsync(
            "urn:uuid:test", null, _ => { }, _ => { }, CancellationToken.None, TimeSpan.FromMilliseconds(30)),
            "dynamic login timeout releases the listener");

        if (loopback)
        {
            var loopNonce = "";
            var loopChallenge = "";
            using var loopbackHttp = new HttpClient(new Handler(async (request, token) =>
            {
                var fields = Query("?" + await request.Content!.ReadAsStringAsync(token));
                Check(fields["client_id"] == "oaiapp_test" && fields["code"] == "good-code"
                    && OAuthPkce.CodeChallenge(fields["code_verifier"]) == loopChallenge,
                    "loopback exchange uses the original verifier and issued registration");
                var signed = IdentityToken("https://auth.openai.com", "oaiapp_test", loopNonce, "subject", DateTime.UtcNow.AddMinutes(5), signingKey);
                return Json(JsonSerializer.Serialize(new { access_token = "access", refresh_token = "refresh", id_token = signed,
                    scope = "openid chatgpt.tokens.use.direct", token_type = "Bearer", expires_in = 3600 }));
            }));
            OpenAiDynamicLogin LoopbackLogin() => new(new OpenAiDynamicTokenExchange(loopbackHttp, validator));
            string Transaction(OpenAiDynamicLoginAttempt attempt)
            {
                var query = Query(attempt.AuthorizationUrl);
                loopNonce = query["nonce"];
                loopChallenge = query["code_challenge"];
                return query["state"];
            }
            using var occupied = new HttpListener();
            occupied.Prefixes.Add("http://127.0.0.1:1455/");
            occupied.Start();
            var fallback = await LoopbackLogin().SignInAsync("urn:uuid:test", null, attempt =>
                attempt.Submit(redirect + "?code=good-code&state=" + Transaction(attempt) + "&client_id=oaiapp_test"),
                _ => { }, CancellationToken.None);
            Check(fallback.Subject == "subject" && occupied.IsListening,
                "occupied callback port falls back to manual entry without stopping its owner");
            occupied.Stop();
            Task? callback = null;
            using var browser = new HttpClient();
            var automatic = await LoopbackLogin().SignInAsync("urn:uuid:test", null, attempt =>
            {
                var state = Transaction(attempt);
                callback = Task.Run(async () =>
                {
                    using var unrelated = await browser.GetAsync("http://127.0.0.1:1455/favicon.ico");
                    Check(unrelated.StatusCode == HttpStatusCode.BadRequest, "unrelated loopback requests do not finish the attempt");
                    using var accepted = await browser.GetAsync(redirect + "?code=good-code&state=" + state + "&client_id=oaiapp_test");
                    Check(accepted.IsSuccessStatusCode, "automatic loopback callback receives a success response");
                });
            }, _ => { }, CancellationToken.None);
            await callback!;
            Check(automatic.Subject == "subject", "automatic loopback callback exchanges its code");
            occupied.Start();
            Check(occupied.IsListening, "completed dynamic login releases the callback port");
            occupied.Stop();
        }

        var tokens = new OpenAiTokens("access-old", "refresh-old", "id-secret", DateTimeOffset.UtcNow.AddSeconds(-1), "account-1");
        var legacyCredential = new ProviderCredential { Id = "openai-legacy", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = tokens.Encode() };
        var credential = new ProviderCredential { Id = "openai-check", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = exchanged.Encode() };
        var provider = AiProviderManifest.CreateBuiltIn("openai")!;
        provider.Credential = credential;
        provider.Models.Add(new ProviderModel { Name = "fixture-model", Enabled = true });
        provider.Model = "fixture-model";
        var saved = new CredentialStore(scratch, store);
        saved.Save([credential]);
        var json = File.ReadAllText(Path.Combine(scratch, "ai", "credentials.json"));
        Check(!json.Contains(exchanged.AccessToken) && !json.Contains(exchanged.RefreshToken!), "credential metadata contains no token material");
        Check(saved.Load().Single().AuthKind == "openaiOAuth", "OAuth credential type survives restart");
        Check(provider.ApiKey == exchanged.AccessToken && ChatClientFactory.ProtocolFor(provider, provider.Model) == ModelProtocols.Responses, "OAuth bundle resolves to Responses and its access token");
        provider.Credential = new ProviderCredential { Secret = "plain-key" };
        Check(provider.ApiKey == "plain-key" && !provider.IsOpenAiOAuth, "API-key credentials remain plain keys");
        provider.Credential = credential;

        string BearerBody(string access, string refresh)
            => JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, token_type = "Bearer", expires_in = 3600 });
        ProviderCredential ExpiredCredential(string id) => new()
        {
            Id = id, ProviderId = "openai", AuthKind = "openaiOAuth",
            Secret = (expiringDynamic with { RefreshToken = "refresh-" + id }).Encode(),
        };
        static ModelProvider ProviderFor(ProviderCredential target)
        {
            var created = AiProviderManifest.CreateBuiltIn("openai")!;
            created.Credential = target;
            return created;
        }
        var unstoredCredential = ExpiredCredential("dynamic-unstored");
        store.Write(unstoredCredential.Id, unstoredCredential.Secret!);
        var storedBefore = store.Read(unstoredCredential.Id);
        using (var successHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(BearerBody("unstored", "unstored-refresh"))))))
        {
            api.Status = -25308;
            await Fails(async () => await new OpenAiDynamicTokenSession(ProviderFor(unstoredCredential), unstoredCredential, store,
                new OpenAiDynamicTokenExchange(successHttp, validator), scratch).GetAsync(false, default),
                "dynamic refresh persistence failure is reported instead of publishing an unstored token");
            api.Status = 0;
            Check(unstoredCredential.Secret == storedBefore && store.Read(unstoredCredential.Id) == storedBefore,
                "failed dynamic persistence leaves the previous credential intact and stores no new secret");
        }
        var cancelledCredential = ExpiredCredential("dynamic-cancelled");
        store.Write(cancelledCredential.Id, cancelledCredential.Secret!);
        using (var hangingHttp = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json(BearerBody("cancelled", "cancelled-refresh"));
        })))
        using (var cancelRefresh = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Fails(async () => await new OpenAiDynamicTokenSession(ProviderFor(cancelledCredential), cancelledCredential, store,
                new OpenAiDynamicTokenExchange(hangingHttp, validator), scratch).GetAsync(false, cancelRefresh.Token),
                "a cancelled dynamic refresh reports cancellation");
        Check(store.Read(cancelledCredential.Id) == cancelledCredential.Secret,
            "a cancelled dynamic refresh leaves the stored credential unchanged");
        var deletedCredential = ExpiredCredential("dynamic-deleted");
        store.Write(deletedCredential.Id, deletedCredential.Secret!);
        var deletionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var deletingHttp = new HttpClient(new Handler(async (_, token) =>
        {
            deletionStarted.SetResult();
            await deletionRelease.Task.WaitAsync(token);
            return Json(BearerBody("late-access", "late-refresh"));
        })))
        {
            var deletedProvider = ProviderFor(deletedCredential);
            var deletedRefresh = new OpenAiDynamicTokenSession(deletedProvider, deletedCredential, store,
                new OpenAiDynamicTokenExchange(deletingHttp, validator), scratch).GetAsync(false, default);
            await deletionStarted.Task;
            deletedProvider.Credential = null;
            store.Delete(deletedCredential.Id);
            deletionRelease.SetResult();
            await Fails(async () => await deletedRefresh, "deleting a credential rejects its outstanding dynamic refresh");
            Check(store.Read(deletedCredential.Id) is null,
                "a completed dynamic refresh cannot recreate a deleted Keychain item");
        }
        var offlineCredential = ExpiredCredential("dynamic-offline");
        store.Write(offlineCredential.Id, offlineCredential.Secret!);
        var before = offlineCredential.Secret;
        using (var offlineHttp = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline"))))
            try
            {
                await new OpenAiDynamicTokenSession(ProviderFor(offlineCredential), offlineCredential, store,
                    new OpenAiDynamicTokenExchange(offlineHttp, validator), scratch).GetAsync(false, default);
            }
            catch (HttpRequestException) { }
        Check(offlineCredential.Secret == before, "network failures preserve saved credentials");

        const string planCatalogJson = """{"models":[{"slug":"first","display_name":"First","visibility":"list"},{"slug":"hidden","visibility":"hide"},{"slug":"missing"},{"slug":"second","visibility":"list"},{"slug":"first","visibility":"list"}]}""";
        var planCatalog = OpenAiPlanModels.Parse(Encoding.UTF8.GetBytes(planCatalogJson));
        Check(planCatalog.Reachable && planCatalog.Models.SequenceEqual(["first", "second"])
            && !OpenAiPlanModels.Parse(Encoding.UTF8.GetBytes("{}")).Reachable,
            "plan model catalog keeps visible slugs in server order and rejects invalid responses");
        var planRequests = 0;
        using (var planHttp = new HttpClient(new Handler((request, _) =>
        {
            planRequests++;
            Check(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/models"
                && request.Headers.Authorization?.Parameter == "new-access"
                && !request.Headers.Contains("ChatGPT-Account-Id"), "plan catalog sends the token only to the public models endpoint");
            return Task.FromResult(Json(planCatalogJson));
        })))
        {
            provider.OpenAiTokenProvider = (_, _) => Task.FromResult(exchanged);
            var fetched = await OpenAiPlanModels.FetchAsync(planHttp, provider, default);
            provider.OpenAiTokenProvider = (_, _) => Task.FromResult(legacyTokens);
            var legacyCatalog = await OpenAiPlanModels.FetchAsync(planHttp, provider, default);
            provider.OpenAiTokenProvider = (_, _) => Task.FromResult(exchanged with { Scope = "openid" });
            var noPermission = await OpenAiPlanModels.FetchAsync(planHttp, provider, default);
            Check(fetched.Models.SequenceEqual(planCatalog.Models) && !legacyCatalog.Reachable && !noPermission.Reachable
                && planRequests == 1, "plan catalog refuses legacy and unauthorized tokens before sending requests");
        }

        var planForces = new List<bool>();
        provider.OpenAiTokenProvider = (force, _) => { planForces.Add(force); return Task.FromResult(exchanged with { AccessToken = force ? "rotated" : "initial" }); };
        var planSends = 0;
        using (var planClient = new HttpClient(new OpenAiPlanTransport(provider, new Handler(async (request, ct) =>
        {
            planSends++;
            using var prepared = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = prepared.RootElement;
            Check(request.Headers.Authorization?.Parameter == (planSends == 1 ? "initial" : "rotated")
                && !request.Headers.Contains("ChatGPT-Account-Id") && !request.Headers.Contains("originator")
                && root.GetProperty("store").ValueKind == JsonValueKind.False && root.GetProperty("stream").GetBoolean()
                && root.GetProperty("input")[0].GetProperty("role").GetString() == "developer"
                && !root.TryGetProperty("temperature", out _) && !root.TryGetProperty("previous_response_id", out _),
                "plan transport sends the current token and adapts preview request fields");
            return Json("{}", planSends == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        }))))
        {
            const string planBody = """{"input":[{"role":"system","content":"hello"}],"temperature":0.5,"previous_response_id":"old","store":true,"stream":false}""";
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(planBody) };
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", "old-account");
            using var response = await planClient.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK && planForces.SequenceEqual([false, true]), "plan transport retries an unauthorized request only once with refreshed credentials");
            foreach (var url in new[] { "https://chatgpt.com/backend-api/codex/responses", "https://api.openai.com/v1/models", "https://api.openai.com:8443/v1/responses", "https://api.openai.com/v1/responses?redirect=other" })
                await Fails(async () => await planClient.PostAsync(url, new StringContent("{}")), "plan transport refuses unsupported endpoints");
            using (var namespaced = JsonDocument.Parse(OpenAiPlanTransport.PrepareRequest(Encoding.UTF8.GetBytes("""{"input":[],"tools":[{"type":"function","name":"test","parameters":{"type":"object"}}],"tool_choice":{"type":"function","name":"test"}}"""))))
                Check(namespaced.RootElement.GetProperty("tools")[0].GetProperty("name").GetString() == "hub"
                    && namespaced.RootElement.GetProperty("tools")[0].GetProperty("tools")[0].GetProperty("name").GetString() == "test"
                    && namespaced.RootElement.GetProperty("tool_choice").GetProperty("namespace").GetString() == "hub",
                    "plan transport groups function tools in a namespace");
            using (var replay = JsonDocument.Parse(OpenAiPlanTransport.PrepareRequest(Encoding.UTF8.GetBytes("""{"input":[{"type":"function_call","name":"test","namespace":"other"},{"type":"function_call","name":"unknown"}],"tools":[{"type":"function","name":"test"}]}"""))))
                Check(replay.RootElement.GetProperty("input")[0].GetProperty("namespace").GetString() == "other"
                    && !replay.RootElement.GetProperty("input")[1].TryGetProperty("namespace", out _),
                    "plan replay preserves explicit namespaces and does not guess unknown functions");
            await Fails(async () => await planClient.PostAsync("https://api.openai.com/v1/responses", new StringContent("""{"input":[],"tools":[{"type":"file_search"}]}""")), "plan transport rejects unsupported tools");
            await Fails(async () => await planClient.PostAsync("https://api.openai.com/v1/responses", new StringContent("{}")), "plan transport rejects missing conversation input");
            provider.OpenAiTokenProvider = (_, _) => Task.FromResult(legacyTokens);
            await Fails(async () => await planClient.PostAsync("https://api.openai.com/v1/responses", new StringContent(planBody)), "plan transport refuses legacy credentials");
            provider.OpenAiTokenProvider = (_, _) => Task.FromResult(exchanged with { Scope = "openid" });
            await Fails(async () => await planClient.PostAsync("https://api.openai.com/v1/responses", new StringContent(planBody)), "plan transport refuses missing plan authorization");
            Check(planSends == 2, "refused plan requests never reach the network handler");
        }

        var limitedCalls = 0;
        provider.OpenAiTokenProvider = (_, _) => Task.FromResult(exchanged);
        credential.PlanUsagePaused = false;
        using (var limited = new HttpClient(new OpenAiPlanTransport(provider, new Handler((_, _) =>
        {
            limitedCalls++;
            return Task.FromResult(Json("""{"error":{"code":"subscription_sharing_usage_limit_exceeded"}}""", HttpStatusCode.TooManyRequests));
        }))))
        {
            await Fails(async () => await limited.PostAsync("https://api.openai.com/v1/responses", new StringContent("""{"input":[]}""")), "plan usage limit is surfaced");
            await Fails(async () => await limited.PostAsync("https://api.openai.com/v1/responses", new StringContent("""{"input":[]}""")), "paused plan requests are blocked locally");
            Check(credential.PlanUsagePaused && limitedCalls == 1, "usage limit pauses further requests without switching billing");
            credential.PlanUsagePaused = false;
        }
        var pausedByStream = false;
        using (var failureContent = new StringContent("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n"))
        using (var failureStream = new OpenAiPlanEventStream(await failureContent.ReadAsStreamAsync(), failureContent, () => pausedByStream = true))
        {
            try { await failureStream.CopyToAsync(Stream.Null); } catch (IOException) { }
            Check(pausedByStream, "streamed usage limit triggers the same pause");
        }

        // Exercise SDK serialization through the plan transport, not just hand-written request JSON.
        var planSdkBodies = new List<string>();
        provider.OpenAiTokenProvider = (_, _) => Task.FromResult(exchanged);
        var legacySecret = credential.Secret;
        credential.Secret = exchanged.Encode();
        using (var planSdk = ChatClientFactory.Create(provider, "fixture-model", null, transport: new Handler(async (request, token) =>
        {
            if (request.RequestUri?.AbsoluteUri != "https://api.openai.com/v1/responses"
                || request.Headers.Authorization?.Parameter != exchanged.AccessToken)
                throw new InvalidOperationException("The plan SDK used the wrong endpoint or credential.");
            planSdkBodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(StreamEvents(planSdkBodies.Count == 1, namespaced: true), Encoding.UTF8, "text/event-stream"),
            };
        })))
        {
            var history = new List<ChatMessage> { new(ChatRole.System, "Test instructions"), new(ChatRole.User, "Read the file") };
            var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "file contents", "read_file")] };
            var result = await planSdk.GetResponseAsync(history, options);
            var calls = result.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
            Check(calls.Count == 1 && calls[0].CallId == "call1" && calls[0].Name == "read_file",
                "plan SDK reconstructs a streamed tool call");
            history.AddRange(result.Messages);
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call1", "file contents")]));
            var text = new StringBuilder();
            await foreach (var update in planSdk.GetStreamingResponseAsync(history, options)) text.Append(update.Text);
            using var firstPlanBody = JsonDocument.Parse(planSdkBodies[0]);
            using var secondPlanBody = JsonDocument.Parse(planSdkBodies[1]);
            Check(text.ToString() == "done" && planSdkBodies.Count == 2
                && firstPlanBody.RootElement.GetProperty("tools")[0].GetProperty("name").GetString() == "hub"
                && secondPlanBody.RootElement.GetProperty("input").EnumerateArray().Any(item =>
                    item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                    && item.GetProperty("call_id").GetString() == "call1")
                && secondPlanBody.RootElement.GetProperty("input").EnumerateArray().Any(item =>
                    item.TryGetProperty("type", out var type) && type.GetString() == "function_call"
                    && item.TryGetProperty("namespace", out var ns) && ns.GetString() == "hub"),
                "plan SDK replays tool results through stateless namespaced requests");
        }
        credential.Secret = legacySecret;

        async Task<string?> PlanStreamFailure(string events)
        {
            using var content = new StringContent(events);
            using var stream = new OpenAiPlanEventStream(await content.ReadAsStreamAsync(), content);
            var buffer = new byte[7];
            try { while (await stream.ReadAsync(buffer.AsMemory()) != 0) { } return null; }
            catch (IOException ex) { return ex.Message; }
        }
        Check(await PlanStreamFailure("data: {\"type\":\"response.completed\"}\n\ndata: [DONE]\n\n") is null
            && (await PlanStreamFailure("data: {\"type\":\"response.output_text.delta\"}\n\n"))?.Contains("before response.completed") == true
            && (await PlanStreamFailure("data: [DONE]\n\n"))?.Contains("before response.completed") == true,
            "plan stream requires a completed event, including on early EOF or DONE");
        Check((await PlanStreamFailure("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n"))?.Contains("usage limit") == true
            && (await PlanStreamFailure("data: {\"type\":\"response.incomplete\"}\n\n"))?.Contains("incomplete") == true,
            "plan stream reports failed usage limits and incomplete responses");

        // Exercise the real tool loop: deleting previous_response_id after the loop has already sent an
        // increment is not stateless replay. The relay and plan need both calls and both results by round three.
        credential.Secret = exchanged.Encode();
        foreach (var (loopProvider, keepsChain, plan) in new[]
        {
            (new ModelProvider { Id = "relay", Name = "Relay", IsCustom = true, BaseUrl = "https://agentrouter.org/v1",
                Protocol = ModelProtocols.Responses, Model = "fixture-model" }, false, false),
            (provider, false, true),
            (new ModelProvider { Id = "api", Name = "API", IsCustom = true, BaseUrl = "https://api.openai.com/v1",
                Protocol = ModelProtocols.Responses, Model = "fixture-model" }, true, false),
        })
        {
            var bodies = new List<string>();
            using var loopClient = ChatClientFactory.Create(loopProvider, "fixture-model", null, transport: new Handler(async (request, token) =>
            {
                bodies.Add(await request.Content!.ReadAsStringAsync(token));
                using var posted = JsonDocument.Parse(bodies[^1]);
                if (!posted.RootElement.TryGetProperty("stream", out var stream) || !stream.GetBoolean())
                    return Json("""{"id":"resp_direct","object":"response","created_at":1,"status":"completed","model":"fixture-model","output":[]}""");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StreamEvents(bodies.Count <= 2, namespaced: plan, round: bodies.Count),
                        Encoding.UTF8, "text/event-stream"),
                };
            }));
            var answer = new StringBuilder();
            var toolRuns = 0;
            await foreach (var chunk in new ChatPipeline(loopClient).SendAsync(loopProvider, [ChatTurn.User("Read the file")],
                tools: [AIFunctionFactory.Create(() => { toolRuns++; return "file contents"; }, "read_file")]))
                answer.Append(chunk);
            var valid = answer.ToString() == "done" && toolRuns == 2 && bodies.Count == 3;
            var trace = new List<string>();
            for (var round = 1; round <= bodies.Count; round++)
            {
                using var body = JsonDocument.Parse(bodies[round - 1]);
                var requestBody = body.RootElement;
                var items = requestBody.GetProperty("input").EnumerateArray().ToArray();
                var hasChain = requestBody.TryGetProperty("previous_response_id", out var chain);
                trace.Add($"{round}:chain={(hasChain ? chain.GetString() : "-")},items=" + string.Join(",", items.Select(item =>
                    (item.TryGetProperty("type", out var type) ? type.GetString() : item.GetProperty("role").GetString())
                    + (item.TryGetProperty("call_id", out var id) ? ":" + id.GetString() : ""))));
                valid &= round == 1 ? !hasChain : keepsChain ? hasChain && chain.GetString() == $"resp{round - 1}" : !hasChain;
                if (keepsChain || round == 1) continue;
                var calls = items.Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call").ToArray();
                var outputs = items.Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output").ToArray();
                valid &= calls.Length == round - 1 && outputs.Length == round - 1
                    && items.Any(item => item.TryGetProperty("role", out var role) && role.GetString() == "user")
                    && calls.Select(item => item.GetProperty("call_id").GetString()).SequenceEqual(
                        Enumerable.Range(1, round - 1).Select(i => $"call{i}"))
                    && outputs.Select(item => item.GetProperty("call_id").GetString()).SequenceEqual(
                        Enumerable.Range(1, round - 1).Select(i => $"call{i}"))
                    && (!plan || calls.All(item => item.TryGetProperty("namespace", out var ns) && ns.GetString() == "hub"));
            }
            Check(valid, (plan ? "plan tool loop replays complete namespaced history"
                : keepsChain ? "OpenAI tool loop retains response chaining" : "relay tool loop replays complete paired history")
                + $" (tool runs={toolRuns}, answer={answer}, {string.Join("; ", trace)})");

            var direct = await loopClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            var directId = direct.ConversationId;
            var sentBeforeChain = bodies.Count;
            var nonStreamingRejected = false;
            var streamingRejected = false;
            try
            {
                await loopClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ConversationId = "resp_old" });
            }
            catch (InvalidOperationException) { nonStreamingRejected = true; }
            try
            {
                await foreach (var _ in loopClient.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ConversationId = "resp_old" })) { }
            }
            catch (InvalidOperationException) { streamingRejected = true; }
            var directChainSent = keepsChain && bodies.Count == sentBeforeChain + 2 && bodies.Skip(sentBeforeChain).All(body =>
            {
                using var posted = JsonDocument.Parse(body);
                return posted.RootElement.TryGetProperty("previous_response_id", out var id) && id.GetString() == "resp_old";
            });
            Check(keepsChain
                    ? directId == "resp_direct" && !nonStreamingRejected && !streamingRejected && directChainSent
                    : directId is null && nonStreamingRejected && streamingRejected && bodies.Count == sentBeforeChain,
                (plan ? "plan hides response ids and refuses explicit chains"
                    : keepsChain ? "OpenAI preserves explicit response chains" : "relay hides response ids and refuses explicit chains")
                + $" (id={directId ?? "-"}, rejected={nonStreamingRejected}/{streamingRejected}, sends={bodies.Count - sentBeforeChain})");
        }
        credential.Secret = legacySecret;

        // A re-login that failed between its two writes leaves a plan registration beside the legacy secret.
        // The secret is what requests carry, so it decides: the user is still asked to sign in.
        var halfCredential = new ProviderCredential { Id = "openai-half", ProviderId = "openai", AuthKind = "openaiOAuth",
            Secret = tokens.Encode(), OpenAiRegistration = OpenAiRegistration.FromTokens(exchanged) };
        var halfProvider = AiProviderManifest.CreateBuiltIn("openai")!;
        halfProvider.Credential = halfCredential;
        Check(halfProvider.NeedsOpenAiRelogin && !halfProvider.IsCallReady,
            "a plan registration left beside a legacy secret still asks for sign-in");
        halfCredential.Secret = exchanged.Encode();
        Check(halfProvider.IsOpenAiPlan && halfProvider.IsCallReady, "the stored plan secret decides the account state");

        var legacyProvider = AiProviderManifest.CreateBuiltIn("openai")!;
        legacyProvider.Credential = legacyCredential;
        Check(legacyProvider.NeedsOpenAiRelogin && !legacyProvider.IsCallReady && legacyProvider.ApiKey == "access-old",
            "legacy OAuth is retained but requires sign-in");
        await Fails(() => { using var rejected = ChatClientFactory.Create(legacyProvider, null, null); return Task.CompletedTask; },
            "legacy OAuth cannot create an inference client");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }

    private static string StreamEvents(bool tool, bool namespaced = false, int round = 1)
    {
        var item = tool
            ? JsonSerializer.SerializeToElement(new { id = $"fc{round}", type = "function_call", call_id = $"call{round}", name = "read_file", arguments = "{}", status = "completed", @namespace = namespaced ? "hub" : null })
            : JsonSerializer.SerializeToElement(new { id = $"msg{round}", type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text = "done", annotations = Array.Empty<object>() } } });
        var reasoning = JsonSerializer.SerializeToElement(new { id = $"rs{round}", type = "reasoning",
            summary = Array.Empty<object>(), encrypted_content = "fixture-encrypted-reasoning" });
        object Response(string status, object[] output) => new { id = $"resp{round}", @object = "response", created_at = 1, model = "fixture-model", status, output };
        var events = new List<object>
        {
            new { type = "response.created", response = Response("in_progress", []) },
            new { type = "response.output_item.added", output_index = 0, item = reasoning },
            new { type = "response.output_item.done", output_index = 0, item = reasoning },
            new { type = "response.output_item.added", output_index = 1, item },
        };
        if (tool)
        {
            events.Add(new { type = "response.function_call_arguments.delta", item_id = $"fc{round}", output_index = 1, delta = "{}" });
            events.Add(new { type = "response.function_call_arguments.done", item_id = $"fc{round}", output_index = 1, arguments = "{}" });
        }
        else
        {
            events.Add(new { type = "response.content_part.added", item_id = $"msg{round}", output_index = 1, content_index = 0,
                part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } });
            events.Add(new { type = "response.output_text.delta", item_id = $"msg{round}", output_index = 1, content_index = 0, delta = "done" });
            events.Add(new { type = "response.output_text.done", item_id = $"msg{round}", output_index = 1, content_index = 0, text = "done" });
        }
        events.Add(new { type = "response.output_item.done", output_index = 1, item });
        events.Add(new { type = "response.completed", response = Response("completed", [reasoning, item]) });
        return string.Concat(events.Select(value =>
        {
            var json = JsonSerializer.Serialize(value);
            var type = JsonDocument.Parse(json).RootElement.GetProperty("type").GetString();
            return "event: " + type + "\ndata: " + json + "\n\n";
        }));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class KeychainFake : IMacKeychainApi
    {
        private readonly Dictionary<(string, string), string> _items = [];
        public int Status, Adds, Updates;
        public (int Status, string? Value) Read(string service, string account)
            => Status != 0 ? (Status, null) : _items.TryGetValue((service, account), out var value) ? (0, value) : (MacKeychainSecretStore.NotFound, null);
        public int Add(string service, string account, string value)
        { Adds++; if (Status != 0) return Status; _items[(service, account)] = value; return 0; }
        public int Update(string service, string account, string value)
        { Updates++; if (Status != 0) return Status; if (!_items.ContainsKey((service, account))) return MacKeychainSecretStore.NotFound; _items[(service, account)] = value; return 0; }
        public int Delete(string service, string account)
        { if (Status != 0) return Status; return _items.Remove((service, account)) ? 0 : MacKeychainSecretStore.NotFound; }
    }
}
