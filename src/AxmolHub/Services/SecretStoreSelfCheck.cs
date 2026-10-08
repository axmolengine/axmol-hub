using System.Text;
using AxmolHub.Agent;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// <c>AxmolHub --check-secrets [scratch-dir]</c> — the secret store exercised on the host that will use it.
///
/// <para><b>Why it exists.</b> Everything else in this repository that proves a provider can be configured runs
/// either on Windows (<c>AxmolHub.Checks</c>, <c>net8.0-windows</c>) or inside Avalonia (<c>--verify-shell</c>,
/// which needs a display). The one claim neither can reach is "a Linux machine can store a key", and the CI's
/// Linux job only compiles the GUI. This runs the production entry point — <see cref="SecretStoreFactory"/> with
/// no arguments of its own — before any UI exists, so the ubuntu job asserts the real thing.</para>
///
/// <para><b>It never touches a user's data root or profile.</b> The scratch directory is a temp folder, and
/// <c>HUB_SECRET_KEY_FILE</c> is pointed into it for the duration of the run — which also means the run exercises
/// the headless key-location override rather than only the default.</para>
///
/// <para>Output is <c>PASS:</c> lines plus a final <c>backend=&lt;kind&gt;</c>, in English: this is terminal text,
/// not interface text. Exit code is the number of failed assertions.</para>
/// </summary>
internal static class SecretStoreSelfCheck
{
    public static int Run(string? scratchDirectory)
    {
        var scratch = string.IsNullOrWhiteSpace(scratchDirectory)
            ? Path.Combine(Path.GetTempPath(), "axmolhub-secret-selfcheck-" + Environment.ProcessId)
            : Path.GetFullPath(scratchDirectory);

        var failures = 0;
        void Check(bool condition, string name)
        {
            if (condition) { Console.WriteLine("PASS: " + name); return; }
            failures++;
            Console.WriteLine("FAIL: " + name);
        }

        Directory.CreateDirectory(scratch);
        var dataRoot = Path.Combine(scratch, "data");
        var keyFile = Path.Combine(scratch, "ai-secret.key");

        // Tier C: the key location override, honoured by the file tier. Set before the factory runs because the
        // store resolves the path in its constructor.
        Environment.SetEnvironmentVariable(SecretKeyFile.EnvironmentVariable, keyFile);

        ISecretStore? store = null;
        try
        {
            store = SecretStoreFactory.Create(dataRoot);
        }
        catch (PlatformNotSupportedException)
        {
            // A platform with no backend is a known state, not a failure: macOS Keychain is deferred, and the CI
            // matrix must not go red over a decision the product took deliberately.
            Console.WriteLine("PASS: no secret store on this platform, and the factory says so instead of storing a plaintext key");
            Console.WriteLine("backend=none");
            return 0;
        }

        const string providerId = "selfcheck-provider";
        const string secret = "sk-selfcheck-0123456789abcdef";
        var descriptor = store.Descriptor;

        Check(descriptor.Persists, $"the backend reports it persists (kind={descriptor.Kind})");
        Check(descriptor.Kind != SecretStoreKind.None, "a backend was selected");

        store.Write(providerId, secret);
        Check(store.Read(providerId) == secret, "a stored key reads back unchanged");

        // The guarantee the whole split exists for: the secret must not appear in the JSON half.
        var credential = new ProviderCredential
        {
            Id = providerId,
            ProviderId = providerId,
            Label = "self-check",
            Source = CredentialSources.ApiKey,
            Secret = secret,
        };
        new CredentialStore(dataRoot, store).Save([credential]);
        var json = File.ReadAllText(Path.Combine(dataRoot, "ai", "credentials.json"));
        Check(!json.Contains(secret, StringComparison.Ordinal), "credentials.json carries no key material");
        Check(new CredentialStore(dataRoot, store).Load().Single().Secret == secret,
            "the credential round-trips through the store, not through the JSON");

        var blob = BlobPath(dataRoot, providerId);
        if (File.Exists(blob))
        {
            var bytes = File.ReadAllBytes(blob);
            Check(!Contains(bytes, secret), "the blob file holds no plaintext");

            // Flip the last byte. A store that returns the original value here is not authenticating anything.
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(blob, bytes);
            Check(ReadsNothingUsable(store, providerId, secret), "a tampered blob does not return the key");
        }
        else
        {
            // Only the keyring tier legitimately keeps nothing in ai/secrets; every file-backed tier that fails
            // to write there is the bug this check exists to catch.
            Check(descriptor.Kind == SecretStoreKind.SecretService,
                $"the store wrote a blob at {blob}, or is the keyring tier (kind={descriptor.Kind})");
        }

        store.Delete(providerId);
        Check(store.Read(providerId) is null, "delete removes the stored key");

        if (store is AesGcmFileSecretStore file)
        {
            Check(file.KeyFilePath == Path.GetFullPath(keyFile), "HUB_SECRET_KEY_FILE decided where the data key lives");
            if (!OperatingSystem.IsWindows() && File.Exists(keyFile))
            {
                Check(File.GetUnixFileMode(keyFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "the data key file is mode 600");
            }
        }

        CheckBrowserLauncher(Check);

        Console.WriteLine("backend=" + descriptor.Kind.ToString().ToLowerInvariant());
        TryDelete(scratch);
        return failures;
    }

    /// <summary>
    /// The browser route the sign-in flow depends on, asserted on the machine that has to run it. Only Linux is
    /// probed: Windows and macOS branches are one line each and already carry the shipping history, whereas the
    /// Linux one is the new code — <c>$BROWSER</c> precedence, and the rule that a machine with no browser must
    /// hand back <c>false</c> (so the caller shows the link) rather than throw.
    ///
    /// <para><b>The fall-through is asserted through the launcher seam, not by really failing a launch.</b> A
    /// stale <c>$BROWSER</c> that correctly falls through now reaches <c>xdg-open</c>, which would open the
    /// person's real browser on <c>example.com</c> in the middle of a self-check. Injecting the launcher keeps the
    /// assertion about the search order honest without that side effect.</para>
    /// </summary>
    private static void CheckBrowserLauncher(Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux()) return;

        var configured = Environment.GetEnvironmentVariable("BROWSER");
        var url = "https://example.com/axmolhub-selfcheck";
        try
        {
            Environment.SetEnvironmentVariable("BROWSER", "/bin/true");
            check(UrlLauncher.TryOpen(url), "$BROWSER is exec'd with the url as its argument");

            // Every candidate has to survive the failure of the ones before it. Ending the search at the first
            // ENOENT was what cost a native Linux desktop its browser: xdg-open never ran, the sign-in was told
            // there was no browser at all, and the user got the fallback for a machine that had one.
            var tried = new List<string>();
            try
            {
                var opened = UrlLauncher.TryOpenLinux(url, argv =>
                {
                    tried.Add(string.Join(' ', argv));
                    throw new InvalidOperationException("no such file");
                });
                check(!opened && tried.Count >= 2,
                    "an unusable browser entry degrades instead of ending the search ("
                    + tried.Count + " tried, nothing thrown, last was ["
                    + (tried.Count > 0 ? tried[^1] : "") + "])");
            }
            catch (Exception ex)
            {
                check(false, "an unusable browser entry degrades instead of throwing (got " + ex.GetType().Name + ")");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROWSER", configured);
        }
    }

    /// <summary>A tampered blob must not hand back the key, whether the store answers with null or with an exception.</summary>
    private static bool ReadsNothingUsable(ISecretStore store, string providerId, string secret)
    {
        try
        {
            return store.Read(providerId) != secret;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string BlobPath(string dataRoot, string providerId)
        => new SecretBlobFiles(dataRoot).PathFor(providerId);

    private static bool Contains(byte[] haystack, string needle)
        => Encoding.UTF8.GetString(haystack).Contains(needle, StringComparison.Ordinal);

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not a failed assertion; the next run makes a new one.
        }
    }
}
