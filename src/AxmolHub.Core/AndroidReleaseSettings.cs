using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

// Only stores non-password configuration; passwords are passed explicitly to each call and never written to metadata, Gradle files, or logs.
public sealed class AndroidReleaseSettings
{
    public string ApplicationId { get; set; } = "";
    public int VersionCode { get; set; } = 1;
    public string VersionName { get; set; } = "1.0";
    public string KeystorePath { get; set; } = "";
    public string KeyAlias { get; set; } = "";
    public static string PathFor(ProjectEntry project) => Path.Combine(project.Path, ".axmol-hub.android-release.json");
    public static AndroidReleaseSettings? Load(ProjectEntry project) => File.Exists(PathFor(project))
        ? System.Text.Json.JsonSerializer.Deserialize<AndroidReleaseSettings>(File.ReadAllText(PathFor(project))) : null;
    public static AndroidReleaseSettings Require(ProjectEntry project)
    {
        var settings = Load(project) ?? throw new InvalidOperationException("Configure Android release signing for this project first.");
        settings.Validate(); return settings;
    }
    public void Validate(bool requireKeystore = true)
    {
        if (!Regex.IsMatch(ApplicationId, @"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$")) throw new InvalidDataException("Invalid Android release application ID.");
        if (VersionCode is < 1 or > 2100000000) throw new InvalidDataException("Android versionCode must be 1..2100000000.");
        if (string.IsNullOrWhiteSpace(VersionName) || VersionName.Length > 100 || VersionName.Any(char.IsControl)) throw new InvalidDataException("Invalid Android versionName.");
        if (string.IsNullOrWhiteSpace(KeyAlias) || KeyAlias.Any(char.IsControl) || KeyAlias.Equals("androiddebugkey", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a release key alias, not androiddebugkey.");
        if (!Path.IsPathFullyQualified(KeystorePath)) throw new InvalidDataException("Choose an absolute keystore path.");
        if (requireKeystore && !File.Exists(KeystorePath)) throw new FileNotFoundException("Release keystore is missing.", KeystorePath);
    }
    public void Save(ProjectEntry project) { Validate(); StateStore.WriteJson(PathFor(project), this); }
    public string Fingerprint()
    {
        using var key = File.OpenRead(KeystorePath);
        var keyHash = Convert.ToHexString(SHA256.HashData(key));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { Settings = this, KeystoreSha256 = keyHash }))));
    }
}

public sealed class AndroidSigningPasswords(string storePassword, string keyPassword)
{
    public string StorePassword { get; } = storePassword;
    public string KeyPassword { get; } = keyPassword;
    public string[] SensitiveValues => [StorePassword, KeyPassword];
    public void Validate()
    {
        if (StorePassword.Length < 6 || KeyPassword.Length < 6 || StorePassword.Any(char.IsControl) || KeyPassword.Any(char.IsControl))
            throw new InvalidDataException("Keystore and key passwords must have at least 6 characters without control characters.");
    }
    public Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> original)
    {
        Validate(); var environment = new Dictionary<string, string>(original);
        environment["HUB_ANDROID_STORE_PASSWORD"] = StorePassword;
        environment["HUB_ANDROID_KEY_PASSWORD"] = KeyPassword;
        return environment;
    }
    public static AndroidSigningPasswords FromEnvironment() => new(
        System.Environment.GetEnvironmentVariable("HUB_ANDROID_STORE_PASSWORD") ?? "",
        System.Environment.GetEnvironmentVariable("HUB_ANDROID_KEY_PASSWORD") ?? "");
}

public sealed class AndroidSigningService(ProcessRunner runner, string toolsRoot)
{
    private string Keytool => Path.Combine(toolsRoot, "jdk/bin/keytool" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    public async Task CreateAsync(AndroidReleaseSettings settings, AndroidSigningPasswords passwords, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation = default)
    {
        settings.Validate(false); passwords.Validate();
        if (File.Exists(settings.KeystorePath)) throw new IOException("Keystore already exists; it will not be overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(settings.KeystorePath)!);
        var temporary = settings.KeystorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var result = await runner.RunAsync(Keytool, ["-J-Dfile.encoding=UTF-8", "-J-Duser.language=en", "-genkeypair", "-storetype", "JKS", "-keystore", temporary,
                "-storepass:env", "HUB_ANDROID_STORE_PASSWORD", "-keypass:env", "HUB_ANDROID_KEY_PASSWORD", "-alias", settings.KeyAlias,
                "-dname", "CN=Android Release", "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000"], toolsRoot, passwords.Environment(environment), cancellation,
                sensitiveValues: passwords.SensitiveValues);
            if (result.ExitCode != 0) throw new InvalidOperationException("Release keystore creation failed.\n" + result.Error);
            File.Move(temporary, settings.KeystorePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<string> ValidateAsync(AndroidReleaseSettings settings, AndroidSigningPasswords passwords, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation = default)
    {
        settings.Validate(); passwords.Validate();
        var directory = Path.Combine(toolsRoot, "../cache/android/signing-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var signingEnvironment = passwords.Environment(environment);
            // certreq must unlock the private key; merely listing certificates cannot detect a wrong keyPassword or a certificate-only alias.
            foreach (var operation in new[] { new[] { "-certreq", "-file", Path.Combine(directory, "request.csr"), "-keypass:env", "HUB_ANDROID_KEY_PASSWORD" },
                new[] { "-exportcert", "-file", Path.Combine(directory, "certificate.der") } })
            {
                var result = await runner.RunAsync(Keytool, ["-J-Dfile.encoding=UTF-8", "-J-Duser.language=en", .. operation, "-keystore", settings.KeystorePath,
                    "-storepass:env", "HUB_ANDROID_STORE_PASSWORD", "-alias", settings.KeyAlias], toolsRoot, signingEnvironment, cancellation,
                    sensitiveValues: passwords.SensitiveValues);
                if (result.ExitCode != 0) throw new InvalidOperationException("Release signing key validation failed. Check keystore, alias and passwords.\n" + result.Error + result.Output);
            }
            using var certificate = new X509Certificate2(File.ReadAllBytes(Path.Combine(directory, "certificate.der")));
            if (certificate.Subject.Contains("CN=Android Debug", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A debug certificate cannot sign a release.");
            if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                throw new InvalidDataException("Release certificate is expired or not yet valid.");
            return Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
