using System.Text;
using AxmolHub.Core;
using AxmolHub.Agent;

internal static class SecretStoreChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        void Assert(bool condition, string name)
        {
            if (!condition)
                throw new Exception("FAILED: " + name);
            Console.WriteLine("PASS: " + name);
        }

        SecretStoreFailure Rejects(Func<string> action)
        {
            try
            {
                action();
            }
            catch (SecretStoreException ex)
            {
                return ex.Failure;
            }

            return (SecretStoreFailure)(-1);
        }

        Exception Throws(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null!;
        }

        var scratch = Path.Combine(root, "secret-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            // ── the blob format: what a stored key is made of ──
            var key = SecretKeyFile.Create();
            Assert(key.Length == SecretBlobFormat.KeySize, "the data key is exactly the size the format expects");
            var secret = "sk-check-" + Guid.NewGuid().ToString("N");
            var blob = SecretBlobFormat.Protect(key, secret, "orcarouter");
            Assert(SecretBlobFormat.Classify(blob) == SecretBlobOrigin.Ours, "a fresh blob is recognised as ours");
            Assert(SecretBlobFormat.VersionOf(blob) == SecretBlobFormat.CurrentVersion, "the version byte survives the write");
            Assert(!Encoding.UTF8.GetString(blob).Contains(secret, StringComparison.Ordinal), "the ciphertext contains no trace of the key");
            Assert(SecretBlobFormat.Unprotect(blob, key, "orcarouter") == secret, "a blob decrypts back to the exact key it was given");
            var flipped = (byte[])blob.Clone();
            flipped[^1] ^= 0x01;
            Assert(Rejects(() => SecretBlobFormat.Unprotect(flipped, key, "orcarouter")) == SecretStoreFailure.WrongKey, "one flipped byte anywhere in the blob is refused, not silently mis-decrypted");
            Assert(Rejects(() => SecretBlobFormat.Unprotect(blob, key, "custom-other")) == SecretStoreFailure.WrongKey, "a blob renamed onto another provider id fails: the id is authenticated, not just a file name");
            Assert(Rejects(() => SecretBlobFormat.Unprotect(blob, SecretKeyFile.Create(), "orcarouter")) == SecretStoreFailure.WrongKey, "a different data key cannot open it");
            var bumped = (byte[])blob.Clone();
            bumped[4] = (byte)(SecretBlobFormat.CurrentVersion + 1);
            Assert(Rejects(() => SecretBlobFormat.Unprotect(bumped, key, "orcarouter")) == SecretStoreFailure.UnsupportedVersion, "a future format version is named, rather than crashing or claiming a corrupt file");
            // The shape a Windows data root arrives with. This is the whole reason the header exists: without it, a
            // copied profile reports "your key is wrong" and the user re-enters a credential that was never the issue.
            var dpapiShaped = new byte[40];
            dpapiShaped[0] = 0x01;
            for (var i = 4; i < dpapiShaped.Length; i++)
                dpapiShaped[i] = (byte)i;
            Assert(SecretBlobFormat.Classify(dpapiShaped) == SecretBlobOrigin.DpapiLikely, "a DPAPI-shaped blob is identified as a foreign backend before any crypto runs");
            Assert(Rejects(() => SecretBlobFormat.Unprotect(dpapiShaped, key, "orcarouter")) == SecretStoreFailure.ForeignBackend, "and it says so instead of reporting a wrong key");
            // The same classification against bytes DPAPI actually produced on this machine, not a hand-made 0x01
            // header. The rule is a shape heuristic, and a heuristic that only ever sees its own synthetic example
            // drifts into "nothing is recognisable" — this is also the exact blob a copied Windows data directory
            // arrives with, which is the case the header exists for.
            if (OperatingSystem.IsWindows())
            {
                var realDpapi = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), "AxmolHub.Secrets.v1"u8.ToArray(), System.Security.Cryptography.DataProtectionScope.CurrentUser);
                Assert(SecretBlobFormat.Classify(realDpapi) == SecretBlobOrigin.DpapiLikely, "a real DPAPI blob from this machine reads as a foreign backend, not as one of ours");
                Assert(Rejects(() => SecretBlobFormat.Unprotect(realDpapi, key, "orcarouter")) == SecretStoreFailure.ForeignBackend, "and the refusal names the reason rather than claiming the key is wrong");
            }

            // ── where the blobs go, and what an id cannot do to it ──
            var blobs = new SecretBlobFiles(scratch);
            foreach (var hostileId in new[]
            {
                "../../evil",
                "/etc/passwd",
                @"C:\Windows\win.ini",
                "..",
                ".",
                "a/b"
            }

            )
            {
                var resolved = Path.GetFullPath(blobs.PathFor(hostileId));
                Assert(Path.GetDirectoryName(resolved) == Path.GetFullPath(blobs.Directory), $"a provider id of '{hostileId}' cannot move a write outside the secrets directory");
            }

            // ── the data key's location, including the headless override ──
            var previous = Environment.GetEnvironmentVariable(SecretKeyFile.EnvironmentVariable);
            try
            {
                var fromEnvironment = Path.Combine(scratch, "env-key");
                Environment.SetEnvironmentVariable(SecretKeyFile.EnvironmentVariable, fromEnvironment);
                Assert(SecretKeyFile.ResolvePath() == Path.GetFullPath(fromEnvironment), "HUB_SECRET_KEY_FILE decides the data key location when nothing is passed");
                Assert(SecretKeyFile.ResolvePath(Path.Combine(scratch, "explicit")) == Path.GetFullPath(fromEnvironment), "and it outranks an explicit path, because the operator's environment is the deliberate choice");
                Environment.SetEnvironmentVariable(SecretKeyFile.EnvironmentVariable, "");
                Assert(SecretKeyFile.ResolvePath() == SecretKeyFile.DefaultPath, "an empty override counts as unset rather than as a key at the root of the filesystem");
            }
            finally
            {
                Environment.SetEnvironmentVariable(SecretKeyFile.EnvironmentVariable, previous);
            }

            // ── which tier a platform gets ──
            Assert(SecretStoreResolver.Decide(HubPlatform.Windows, false) == SecretStoreKind.Dpapi, "Windows gets DPAPI regardless of anything probed");
            Assert(SecretStoreResolver.Decide(HubPlatform.Linux, true) == SecretStoreKind.SecretService && SecretStoreResolver.Decide(HubPlatform.Linux, false) == SecretStoreKind.EncryptedFile, "Linux prefers the keyring and falls back to the file tier when no provider answers");
            Assert(SecretStoreResolver.Decide(HubPlatform.MacOS, true) == SecretStoreKind.Keychain, "macOS selects the native Keychain tier");
            Assert(!SecretStoreResolver.ShouldProbeKeyring(HubPlatform.Windows) && !SecretStoreResolver.ShouldProbeKeyring(HubPlatform.MacOS) && SecretStoreResolver.ShouldProbeKeyring(HubPlatform.Linux), "only the platform with a keyring tier pays for a probe");
            // The factory's own table, asserted with the platform supplied: a Windows runner cannot become Linux, so
            // without this seam the EncryptedFile arm of the switch would only ever be reached by the CI matrix.
            var factoryFile = (AesGcmFileSecretStore)SecretStoreFactory.Create(Path.Combine(scratch, "factory"), HubPlatform.Linux);
            Assert(factoryFile.Descriptor.Kind == SecretStoreKind.EncryptedFile, "the factory hands Linux the file tier, and does not throw any more");
            if (OperatingSystem.IsWindows())
                Assert(SecretStoreFactory.Create(Path.Combine(scratch, "factory"), HubPlatform.Windows).Descriptor.Kind == SecretStoreKind.Dpapi, "and still hands Windows DPAPI");
            Assert(OperatingSystem.IsMacOS() ? SecretStoreFactory.Create(scratch, HubPlatform.MacOS).Descriptor.Kind == SecretStoreKind.Keychain : Throws(() => SecretStoreFactory.Create(scratch, HubPlatform.MacOS)) is PlatformNotSupportedException, "the Keychain factory is only instantiated on macOS");
            // ── the file store as the app uses it ──
            var storeRoot = Path.Combine(scratch, "data");
            var keyPath = Path.Combine(scratch, "ai-secret.key");
            var protectedPaths = new List<string>();
            var store = new AesGcmFileSecretStore(storeRoot, keyPath, protectedPaths.Add);
            Assert(store.Read("orcarouter")is null, "an empty store reads as unauthenticated, not as an error");
            store.Write("orcarouter", secret);
            Assert(store.Read("orcarouter") == secret, "the store round-trips a key through the filesystem");
            Assert(protectedPaths.Count == 1 && protectedPaths[0].EndsWith(".tmp", StringComparison.Ordinal), "the data key is made private under its temporary name, before it is visible under its real one");
            Assert(!File.Exists(keyPath + ".tmp"), "no half-written key file is left behind");
            Assert(store.Descriptor.DegradedReason is null, "a healthy store reports nothing to report");
            // The split the whole model rests on: metadata in JSON, secret in the store.
            new CredentialStore(storeRoot, store).Save([new ProviderCredential { Id = "orcarouter", ProviderId = "orcarouter", Label = "OrcaRouter", Source = CredentialSources.ApiKey, Secret = secret, }]);
            var credentialsJson = File.ReadAllText(Path.Combine(storeRoot, "ai", "credentials.json"));
            Assert(!credentialsJson.Contains(secret, StringComparison.Ordinal), "credentials.json never carries key material, even with the file tier in use");
            Assert(new CredentialStore(storeRoot, store).Load().Single().Secret == secret, "the credential rehydrates its secret from the store on load");
            // The copied-data-directory case, which is the reason the key lives outside the data root.
            var orphaned = new AesGcmFileSecretStore(storeRoot, Path.Combine(scratch, "a-different-key"));
            Assert(orphaned.Read("orcarouter")is null && orphaned.Descriptor.DegradedReason is { Length: > 0 }, "a data root copied without its key reads as unauthenticated and says why, instead of throwing during load");
            // A store that says nothing about itself still answers the interface. Reached through ISecretStore on
            // purpose: a default interface member is invisible on the concrete type, which is the constraint any
            // consumer of the descriptor has to live with anyway.
            Assert(((ISecretStore)new BareSecretStore()).Descriptor.Kind == SecretStoreKind.None, "a store that does not declare a backend is reported as no backend, not as a secure one");
            // ── the OAuth callback, both routes through one parser ──
            const string expectedState = "test-state-value";
            Assert(BrowserKeyOAuthFlow.BuildAuthorizationUrl("https://www.orcarouter.ai/auth", new AiProviderOAuth
            {
                AppName = "Axmol Hub",
                AppId = "app-test",
                ReferralCode = "ref-test",
                Scope = "api",
            }, "http://127.0.0.1:56450/", "challenge", expectedState)
                .Contains("callback_url=http%3A%2F%2F127.0.0.1%3A56450%2F", StringComparison.Ordinal),
                "OrcaRouter authorization uses the registered loopback root callback");
            Assert(BrowserKeyOAuthFlow.ReadCallback($"?code=abc123&state={expectedState}", expectedState)is { State: CallbackState.Accepted, Code: "abc123" }, "a matching callback yields its code");
            Assert(BrowserKeyOAuthFlow.ReadCallback("http://127.0.0.1:9/?code=abc123&state=" + expectedState, expectedState).State == CallbackState.Accepted, "the registered loopback root callback goes through the same state check");
            Assert(BrowserKeyOAuthFlow.ReadCallback($"code=abc123&state={expectedState}", expectedState).State == CallbackState.Accepted, "with or without the leading question mark");
            Assert(BrowserKeyOAuthFlow.ReadCallback("http://127.0.0.1:9/?code=ab%20c&state=" + expectedState, expectedState).Code == "ab c", "percent escapes are decoded, so a code copied from a browser bar matches one from the listener");
            Assert(BrowserKeyOAuthFlow.ReadCallback("?code=abc&state=other", expectedState).State == CallbackState.StateMismatch, "a state that does not match is refused");
            Assert(BrowserKeyOAuthFlow.ReadCallback("?code=abc", expectedState).State == CallbackState.StateMismatch, "and so is a callback with no state at all — the check runs before the code is even looked at");
            Assert(BrowserKeyOAuthFlow.ReadCallback($"?error=access_denied&state={expectedState}", expectedState)is { State: CallbackState.Declined, Error: "access_denied" }, "a declined consent is its own outcome, not a missing code");
            Assert(BrowserKeyOAuthFlow.ReadCallback($"?state={expectedState}", expectedState).State == CallbackState.NoCode, "a matching state with no code says so rather than handing back an empty token");
            Assert(BrowserKeyOAuthFlow.ReadCallback(null, expectedState).State == CallbackState.StateMismatch, "nothing pasted at all is not a sign-in");
            // ── the Linux browser route's argument order, asserted without a browser ──
            var order = UrlLauncher.LinuxCommandOrder("/usr/bin/firefox:-custom %s --kiosk: :xdg-browser", "https://x/y?z=1").Select(argv => string.Join(' ', argv)).ToArray();
            Assert(order[0] == "/usr/bin/firefox https://x/y?z=1", "the first $BROWSER entry wins, with the url appended");
            Assert(order[1] == "-custom https://x/y?z=1 --kiosk", "an entry may name the url slot with %s and keep its own arguments around it");
            Assert(order[2] == "xdg-browser https://x/y?z=1", "a blank entry is dropped and the rest of the list is still tried in order");
            Assert(order[^2] == "xdg-open https://x/y?z=1" && order[^1] == "gio open https://x/y?z=1", "xdg-open then gio open are the standing fallbacks");
            Assert(UrlLauncher.LinuxCommandOrder(null, "u").Count == 2 && UrlLauncher.FromBrowserEntry("   ", "u")is null, "an unset variable leaves the two standards, and a blank entry is never a browser");
            // Order is only half the contract: every candidate has to survive the failure of the ones before it. A stale
            // $BROWSER entry is the common case on a real desktop, and if its ENOENT ended the search then xdg-open and
            // gio would never run, and a machine with a perfectly good browser would be told it has none.
            var launchTried = new List<string>();
            Assert(!UrlLauncher.TryOpenLinux("https://x/y", argv =>
            {
                launchTried.Add(string.Join(' ', argv));
                throw new InvalidOperationException("no such file");
            }) && launchTried.Count >= 2, "a candidate that cannot exec does not end the search: all of them get tried before reporting no" + " browser (tried " + launchTried.Count + ": [" + string.Join(" | ", launchTried) + "])");
            var launches = 0;
            Assert(UrlLauncher.TryOpenLinux("https://x/y", argv =>
            {
                if (launches++ == 0)
                    throw new InvalidOperationException("the first $BROWSER entry is stale");
                return true;
            }) && launches == 2, "the first failure is skipped and the candidate that does launch is the one counted as opened");
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return;
    }
}
