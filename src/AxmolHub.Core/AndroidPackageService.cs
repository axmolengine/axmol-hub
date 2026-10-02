using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AxmolHub.Core;

// CMake 管理原生构建，Gradle 只包装已有原生库和官方 Java 入口。所有生成文件留在目标输出目录。
public sealed class AndroidPackageService(ProcessRunner runner, string toolsRoot)
{
    private string Tool(string path) => Path.GetFullPath(Path.Combine(toolsRoot, path));
    public string Java => Tool(OperatingSystem.IsWindows() ? "jdk/bin/java.exe" : "jdk/bin/java");
    public static string StageDirectory(ProjectEntry project) => Path.Combine(BuildTargets.BuildDirectory(project), "android");
    public static string ApkPath(ProjectEntry project) => Path.Combine(StageDirectory(project), "app/build/outputs/apk", project.Configuration.ToLowerInvariant(), "app-" + project.Configuration.ToLowerInvariant() + ".apk");
    public static string BundlePath(ProjectEntry project) => Path.Combine(StageDirectory(project), "app/build/outputs/bundle", project.Configuration.ToLowerInvariant(), "app-" + project.Configuration.ToLowerInvariant() + ".aab");
    private string BuildTools => Tool("android/sdk/build-tools/35.0.0");
    private static string Suffix => OperatingSystem.IsWindows() ? ".exe" : "";
    private static string Groovy(string value) => "'" + value.Replace('\\', '/').Replace("'", "\\'") + "'";
    public static string PackageName(ProjectEntry project)
    {
        var file = Path.Combine(project.Path, ".axproj");
        var match = Regex.Match(File.ReadAllText(file), @"(?m)^package_name\s*=\s*([^\r\n]+)");
        var name = match.Groups[1].Value.Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$")) throw new InvalidDataException("Invalid Android application ID in .axproj.");
        return name;
    }
    public BuildCommand GradleCommand(ProjectEntry project, IReadOnlyDictionary<string, string> environment, params string[] arguments)
        => new(Java, ["-Duser.home=" + environment["HOME"], "-Djava.io.tmpdir=" + environment["TEMP"], "-classpath", Tool("gradle/lib/gradle-gradle-cli-main-8.13.jar"), "org.gradle.launcher.GradleMain",
            "--no-daemon", "--console=plain", "--max-workers=2", "--gradle-user-home", environment["GRADLE_USER_HOME"], .. arguments], StageDirectory(project));

    public void PrepareProject(ProjectEntry project, EngineEntry engine, IReadOnlyDictionary<string, string> environment)
    {
        var target = BuildTargets.Get(project.Platform);
        if (target.Family != "android") throw new InvalidOperationException("Android packaging requires an Android target.");
        // 版本验证边界来自 module-manifest.json 的 verifiedRecipes，不再硬编码某个版本号。
        PackagingRecipes.RequireVerified(engine, PackagingRecipes.AndroidPackaging);
        var stage = StageDirectory(project); Directory.CreateDirectory(stage);
        RejectLinks(stage);
        var source = Path.Combine(project.Path, "proj.android/app");
        var packageName = PackageName(project);
        var release = project.Configuration == "Release" ? AndroidReleaseSettings.Require(project) : null;
        // 尚未接入项目自定义 Gradle 插件时明确阻止，避免悄悄忽略用户的打包配置。
        var template = Path.Combine(engine.Path, "templates/common/proj.android/app/build.gradle");
        if (File.ReadAllText(Path.Combine(source, "build.gradle")).Replace("\r\n", "\n") != File.ReadAllText(template).Replace("\r\n", "\n"))
            throw new InvalidOperationException("Custom Android app/build.gradle is not supported by this managed packaging profile.");
        CopyTree(Path.Combine(engine.Path, "core/platform/android"), Path.Combine(stage, "engine-android"));
        Directory.CreateDirectory(Path.Combine(stage, "app"));
        WriteChanged(Path.Combine(stage, "settings.gradle"), $"rootProject.name = {Groovy(project.Name)}\ninclude ':app', ':libaxmol'\nproject(':libaxmol').projectDir = file('engine-android/libaxmol')\n");
        WriteChanged(Path.Combine(stage, "build.gradle"), "buildscript { repositories { google(); mavenCentral() }; dependencies { classpath 'com.android.tools.build:gradle:8.11.1' } }\nallprojects { repositories { google(); mavenCentral() } }\n");
        WriteChanged(Path.Combine(stage, "local.properties"), "sdk.dir=" + Tool("android/sdk").Replace('\\', '/') + "\n");
        WriteChanged(Path.Combine(stage, "gradle.properties"), "android.useAndroidX=true\nandroid.builder.sdkDownload=false\nandroid.aapt2FromMavenOverride=" + Path.Combine(BuildTools, "aapt2" + Suffix).Replace('\\', '/')
            + "\norg.gradle.java.installations.auto-detect=false\norg.gradle.java.installations.auto-download=false\norg.gradle.java.home=" + Tool("jdk").Replace('\\', '/')
            + "\norg.gradle.jvmargs=-Xmx3g -Dfile.encoding=UTF-8 -Duser.home=\"" + environment["HOME"].Replace('\\', '/') + "\" -Djava.io.tmpdir=\"" + environment["TEMP"].Replace('\\', '/') + "\"\n");
        var keystore = Tool("../cache/android/debug.keystore");
        WriteChanged(Path.Combine(stage, "app/build.gradle"), $$"""
            apply plugin: 'com.android.application'
            android {
                namespace {{Groovy(packageName)}}
                compileSdk 36
                buildToolsVersion '35.0.0'
                ndkVersion '27.3.13750724'
                ndkPath {{Groovy(Tool("android/sdk/ndk/27.3.13750724"))}}
                defaultConfig {
                    applicationId {{Groovy(release?.ApplicationId ?? packageName)}}
                    minSdk 23
                    targetSdk 36
                    versionCode {{release?.VersionCode ?? 1}}
                    versionName {{Groovy(release?.VersionName ?? "1.0")}}
                    ndk { abiFilters {{Groovy(target.Architecture)}} }
                }
                sourceSets.main {
                    java.srcDir {{Groovy(Path.Combine(source, "src"))}}
                    res.srcDir {{Groovy(Path.Combine(source, "res"))}}
                    manifest.srcFile {{Groovy(Path.Combine(source, "AndroidManifest.xml"))}}
                    assets.srcDir 'src/main/assets'
                    jniLibs.srcDir 'src/main/jniLibs'
                }
                signingConfigs { debug {
                    storeFile file({{Groovy(keystore)}})
                    storePassword 'android'
                    keyAlias 'androiddebugkey'
                    keyPassword 'android'
                }
                {{(release == null ? "" : "release { storeFile file(" + Groovy(release.KeystorePath) + "); storePassword System.getenv('HUB_ANDROID_STORE_PASSWORD'); keyAlias " + Groovy(release.KeyAlias) + "; keyPassword System.getenv('HUB_ANDROID_KEY_PASSWORD') }")}}
                }
                buildTypes {
                    debug { debuggable true; jniDebuggable true; signingConfig signingConfigs.debug }
                    release { debuggable false; jniDebuggable false; minifyEnabled false; {{(release == null ? "" : "signingConfig signingConfigs.release")}} }
                }
                {{(release == null ? "packaging { jniLibs { keepDebugSymbols += ['**/*.so'] } }" : "")}}
                androidResources { noCompress += ['mp3', 'ogg', 'wav', 'mp4', 'ttf', 'ttc', 'otf'] }
                buildFeatures { aidl true }
            }
            dependencies { implementation project(':libaxmol'); implementation fileTree(dir: {{Groovy(Path.Combine(source, "libs"))}}, include: ['*.jar']) }
            """);
        var assets = Path.Combine(stage, "app/src/main/assets");
        SyncTree(Path.Combine(project.Path, "Content"), assets, path => !path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase), "axslc");
        var shaders = Path.Combine(stage, "app/build/runtime/axslc");
        if (Directory.Exists(shaders)) SyncTree(shaders, Path.Combine(assets, "axslc"));
        var jni = Path.Combine(stage, "app/src/main/jniLibs", target.Architecture);
        var library = NativeLibrary(project);
        Directory.CreateDirectory(jni);
        var nativeFiles = new[] { library, Path.Combine(BuildTargets.BuildDirectory(project), "lib/libopenal.so"),
            Tool("android/sdk/ndk/27.3.13750724/toolchains/llvm/prebuilt/" + (BuildTargets.Host == "macos" ? "darwin-x86_64" : BuildTargets.Host + "-x86_64") + "/sysroot/usr/lib/" + (target.Architecture == "arm64-v8a" ? "aarch64-linux-android" : "x86_64-linux-android") + "/libc++_shared.so") };
        foreach (var file in nativeFiles)
        {
            using (var native = File.OpenRead(file)) ValidateElf(native, target.Architecture);
            CopyChanged(file, Path.Combine(jni, Path.GetFileName(file)));
        }
        // 只清理 Hub 生成目录中的旧 JNI 文件，源码目录不受影响。
        foreach (var file in Directory.EnumerateFiles(jni)) if (!nativeFiles.Any(input => Path.GetFileName(input) == Path.GetFileName(file))) File.Delete(file);
    }
    public static string NativeLibrary(ProjectEntry project)
    {
        var root = BuildTargets.BuildDirectory(project);
        return new[] { Path.Combine(root, "lib" + project.Name + ".so"), Path.Combine(root, "lib", "lib" + project.Name + ".so") }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Android native library is missing.");
    }
    public async Task EnsureDebugKeyAsync(IReadOnlyDictionary<string, string> environment, CancellationToken cancellation)
    {
        var key = Tool("../cache/android/debug.keystore");
        if (File.Exists(key)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        var temporary = key + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var result = await runner.RunAsync(Tool("jdk/bin/keytool" + (OperatingSystem.IsWindows() ? ".exe" : "")),
                ["-J-Dfile.encoding=UTF-8", "-J-Duser.language=en", "-genkeypair", "-keystore", temporary, "-storepass", "android", "-keypass", "android", "-alias", "androiddebugkey", "-dname", "CN=Android Debug,O=Axmol Hub,C=US", "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000"], toolsRoot, environment, cancellation);
            RequireSuccess(result, "Debug signing key"); File.Move(temporary, key);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task PackageAsync(ProjectEntry project, EngineEntry engine, Dictionary<string, string> environment, CancellationToken cancellation, AndroidSigningPasswords? passwords = null, string? signingCertificate = null)
    {
        var release = project.Configuration == "Release" ? AndroidReleaseSettings.Require(project) : null;
        if (release != null)
        {
            if (passwords == null) throw new InvalidOperationException("Release signing passwords are required.");
            signingCertificate ??= await new AndroidSigningService(runner, toolsRoot).ValidateAsync(release, passwords, environment, cancellation);
        }
        PrepareProject(project, engine, environment);
        var verification = Path.Combine(AppContext.BaseDirectory, "manifests/android-gradle-verification.xml");
        if (!File.Exists(verification)) throw new InvalidOperationException("Verified Android Gradle dependency manifest is missing.");
        CopyChanged(verification, Path.Combine(StageDirectory(project), "gradle/verification-metadata.xml"));
        var shaderDirectory = Path.Combine(StageDirectory(project), "app/src/main/assets/axslc");
        if (!Directory.Exists(shaderDirectory) || !Directory.EnumerateFiles(shaderDirectory, "*", SearchOption.AllDirectories).Any()) throw new InvalidDataException("Android compiled shaders are missing.");
        if (release == null) await EnsureDebugKeyAsync(environment, cancellation);
        var signingEnvironment = release == null ? environment : passwords!.Environment(environment);
        var command = GradleCommand(project, environment, "--dependency-verification=strict", "assemble" + project.Configuration, "bundle" + project.Configuration);
        RequireSuccess(await runner.RunAsync(command.Executable, command.Arguments, command.WorkingDirectory, signingEnvironment, cancellation, TimeSpan.FromHours(1), passwords?.SensitiveValues), "Android APK packaging");
        var apk = ApkPath(project);
        ValidateApk(apk, project);
        var signer = await runner.RunAsync(Java, ["-jar", Path.Combine(BuildTools, "lib/apksigner.jar"), "verify", "--verbose", "--print-certs", apk], StageDirectory(project), environment, cancellation);
        RequireSuccess(signer, "APK signature verification");
        var apkCertificates = Regex.Matches(signer.Output, @"(?im)^Signer #\d+ certificate SHA-256 digest: ([a-f0-9:]+)");
        if (release != null && (apkCertificates.Count != 1 || !apkCertificates[0].Groups[1].Value.Replace(":", "").Equals(signingCertificate, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("APK signer does not match the selected release certificate.");
        var badging = await runner.RunAsync(Path.Combine(BuildTools, "aapt2" + Suffix), ["dump", "badging", apk], StageDirectory(project), environment, cancellation);
        RequireSuccess(badging, "APK manifest verification");
        if (release != null && (badging.Output.Contains("application-debuggable") || !badging.Output.Contains("name='" + release.ApplicationId + "'")
            || !badging.Output.Contains("versionCode='" + release.VersionCode + "'") || !badging.Output.Contains("versionName='" + release.VersionName + "'")))
            throw new InvalidDataException("Release APK manifest does not match the configured application ID/version or is debuggable.");
        var alignment = await runner.RunAsync(Path.Combine(BuildTools, "zipalign" + (OperatingSystem.IsWindows() ? ".exe" : "")), ["-c", "-P", "16", "-v", "4", apk], StageDirectory(project), environment, cancellation);
        RequireSuccess(alignment, "APK alignment verification");
        var bundle = BundlePath(project);
        ValidateBundle(bundle, project);
        if (release != null) await CanonicalizeReleaseBundleAsync(bundle, release, passwords!, signingEnvironment, cancellation);
        ValidateBundle(bundle, project);
        var bundleSignature = await runner.RunAsync(Tool("jdk/bin/jarsigner" + Suffix), ["-J-Dfile.encoding=UTF-8", "-J-Duser.language=en", "-verify", bundle], StageDirectory(project), environment, cancellation);
        RequireSuccess(bundleSignature, "AAB signature verification");
        if (!Regex.IsMatch(bundleSignature.Output, @"(?im)^jar verified[.,]")) throw new InvalidDataException("AAB signature verification was not confirmed.");
        if (release != null && (bundleSignature.Output + bundleSignature.Error).Contains("unsigned entries", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release AAB contains unsigned entries.");
        if (release != null)
        {
            var certificate = await runner.RunAsync(Tool("jdk/bin/keytool" + Suffix), ["-J-Duser.language=en", "-printcert", "-jarfile", bundle], StageDirectory(project), environment, cancellation);
            RequireSuccess(certificate, "AAB certificate verification");
            if (!Regex.Matches(certificate.Output, @"(?im)SHA256:\s*([a-f0-9:]+)").Any(match => match.Groups[1].Value.Replace(":", "").Equals(signingCertificate, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("AAB signer does not match the selected release certificate.");
        }
        using var bundleStream = File.OpenRead(bundle);
        var bundleHash = Convert.ToHexString(SHA256.HashData(bundleStream));
        using var stream = File.OpenRead(apk);
        StateStore.WriteJson(Path.Combine(StageDirectory(project), ".hub-apk.json"), new { Target = project.Platform, Configuration = project.Configuration,
            ApplicationId = release?.ApplicationId ?? PackageName(project), SourceApplicationId = PackageName(project), VersionCode = release?.VersionCode ?? 1,
            VersionName = release?.VersionName ?? "1.0", MinSdk = 23, TargetSdk = 36, Signing = release == null ? "Hub debug key" : "Release key",
            CertificateSha256 = signingCertificate, ReleaseSettingsHash = release?.Fingerprint(), Sha256 = Convert.ToHexString(SHA256.HashData(stream)), Apk = apk, Bundle = bundle, BundleSha256 = bundleHash });
    }
    public static void ValidateApk(string apk, ProjectEntry project)
        => ValidateArchive(apk, project, false);
    private async Task CanonicalizeReleaseBundleAsync(string bundle, AndroidReleaseSettings settings, AndroidSigningPasswords passwords, Dictionary<string, string> environment, CancellationToken cancellation)
    {
        // AGP 的 ZIP 条目顺序可能使 JarInputStream 无法验证；由 JDK 重签并将 META-INF 放到标准位置。
        var temporary = bundle + "." + Guid.NewGuid().ToString("N") + ".signed.aab";
        try
        {
            RequireSuccess(await runner.RunAsync(Tool("jdk/bin/jarsigner" + Suffix), ["-J-Dfile.encoding=UTF-8", "-J-Duser.language=en", "-keystore", settings.KeystorePath,
                "-storepass:env", "HUB_ANDROID_STORE_PASSWORD", "-keypass:env", "HUB_ANDROID_KEY_PASSWORD", "-sigfile", "CERT", "-digestalg", "SHA-256",
                "-signedjar", temporary, bundle, settings.KeyAlias], Path.GetDirectoryName(bundle)!, environment, cancellation, sensitiveValues: passwords.SensitiveValues), "Release AAB signing");
            var verified = await runner.RunAsync(Tool("jdk/bin/jarsigner" + Suffix), ["-J-Duser.language=en", "-verify", temporary], Path.GetDirectoryName(bundle)!, environment, cancellation,
                sensitiveValues: passwords.SensitiveValues);
            RequireSuccess(verified, "Canonical AAB verification");
            var output = verified.Output + verified.Error;
            if (!Regex.IsMatch(verified.Output, @"(?im)^jar verified[.,]") || output.Contains("unsigned entries", StringComparison.OrdinalIgnoreCase)
                || output.Contains("internal inconsistencies", StringComparison.OrdinalIgnoreCase) || output.Contains("not signed in JarInputStream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AAB signing is incomplete or incompatible with streaming verification.");
            File.Move(temporary, bundle, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void ValidateBundle(string bundle, ProjectEntry project)
        => ValidateArchive(bundle, project, true);
    private static void ValidateArchive(string archive, ProjectEntry project, bool bundle)
    {
        using var zip = ZipFile.OpenRead(archive); var abi = BuildTargets.Get(project.Platform).Architecture;
        var prefix = bundle ? "base/" : "";
        foreach (var file in new[] { bundle ? "manifest/AndroidManifest.xml" : "AndroidManifest.xml", bundle ? "dex/classes.dex" : "classes.dex", $"lib/{abi}/lib{project.Name}.so", $"lib/{abi}/libopenal.so", $"lib/{abi}/libc++_shared.so" })
            if (zip.GetEntry(prefix + file) == null) throw new InvalidDataException("Android archive is missing " + prefix + file);
        if (bundle && (zip.GetEntry("BundleConfig.pb") == null || !zip.Entries.Any(entry => entry.FullName.StartsWith("META-INF/") && (entry.FullName.EndsWith(".RSA") || entry.FullName.EndsWith(".EC") || entry.FullName.EndsWith(".DSA")))))
            throw new InvalidDataException("Android bundle configuration or signature is missing.");
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(prefix + "lib/") && entry.FullName.EndsWith(".so")))
        { using var native = entry.Open(); ValidateElf(native, abi); }
        if (!zip.Entries.Any(entry => entry.FullName.StartsWith(prefix + "assets/axslc/") && entry.Length > 0)) throw new InvalidDataException("Android archive has no compiled shader assets.");
        if (zip.Entries.Any(entry => entry.FullName.StartsWith(prefix + "lib/") && !entry.FullName.StartsWith($"{prefix}lib/{abi}/"))) throw new InvalidDataException("Android archive contains another target ABI.");
    }
    public static void ValidateElf(Stream stream, string abi)
    {
        var header = new byte[64]; stream.ReadExactly(header);
        var machine = abi switch { "arm64-v8a" => 183, "x86_64" => 62, _ => throw new InvalidDataException("Unsupported Android ABI.") };
        if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' || header[4] != 2 || header[5] != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16)) != 3 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) != machine) throw new InvalidDataException("Android native library does not match the target ELF ABI.");
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
        var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(54)); var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(56));
        if (offset < 64 || offset > 1024 * 1024 || size < 56 || size > 1024 || count == 0 || count > 1024) throw new InvalidDataException("Invalid ELF program header table.");
        var skip = new byte[4096];
        for (var remaining = offset - 64; remaining > 0;) { var take = (int)Math.Min((ulong)skip.Length, remaining); stream.ReadExactly(skip.AsSpan(0, take)); remaining -= (uint)take; }
        var program = new byte[size]; var loadSegments = 0;
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(program);
            if (BinaryPrimitives.ReadUInt32LittleEndian(program) != 1) continue;
            loadSegments++;
            if (BinaryPrimitives.ReadUInt64LittleEndian(program.AsSpan(48)) < 16384
                || BinaryPrimitives.ReadUInt64LittleEndian(program.AsSpan(8)) % 16384 != BinaryPrimitives.ReadUInt64LittleEndian(program.AsSpan(16)) % 16384)
                throw new InvalidDataException("Android native library is not aligned for 16 KB pages.");
        }
        if (loadSegments == 0) throw new InvalidDataException("Android native library has no loadable segment.");
    }
    public static string VerifiedApk(ProjectEntry project)
    {
        var apk = ApkPath(project); var receipt = Path.Combine(StageDirectory(project), ".hub-apk.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(receipt));
        using var stream = File.OpenRead(apk);
        var expectedApplicationId = project.Configuration == "Release" ? AndroidReleaseSettings.Require(project).ApplicationId : PackageName(project);
        if (project.Configuration == "Release" && (doc.RootElement.GetProperty("Configuration").GetString() != "Release"
            || doc.RootElement.GetProperty("ReleaseSettingsHash").GetString() != AndroidReleaseSettings.Require(project).Fingerprint()))
            throw new InvalidDataException("Release settings changed. Build again before deployment.");
        if (doc.RootElement.GetProperty("Target").GetString() != project.Platform || doc.RootElement.GetProperty("ApplicationId").GetString() != expectedApplicationId
            || !Convert.ToHexString(SHA256.HashData(stream)).Equals(doc.RootElement.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("APK changed after its verified build. Build this target again.");
        return apk;
    }
    public static string LaunchComponent(ProjectEntry project)
    {
        var manifest = XDocument.Load(Path.Combine(project.Path, "proj.android/app/AndroidManifest.xml"));
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var activity = manifest.Root?.Element("application")?.Elements("activity").FirstOrDefault(item => item.Elements("intent-filter").Any(filter =>
            filter.Elements("action").Any(action => (string?)action.Attribute(android + "name") == "android.intent.action.MAIN") &&
            filter.Elements("category").Any(category => (string?)category.Attribute(android + "name") == "android.intent.category.LAUNCHER")));
        var name = (string?)activity?.Attribute(android + "name") ?? throw new InvalidDataException("Android launcher activity is missing.");
        var package = PackageName(project);
        name = name.StartsWith('.') ? package + name : !name.Contains('.') ? package + "." + name : name;
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$")) throw new InvalidDataException("Invalid Android launcher activity.");
        return (project.Configuration == "Release" ? AndroidReleaseSettings.Require(project).ApplicationId : package) + "/" + name;
    }
    private static void RequireSuccess(ProcessResult result, string action)
    { if (result.ExitCode != 0) throw new InvalidOperationException($"{action} failed (exit {result.ExitCode}).\n{result.Error}\n{result.Output}"); }
    private static void WriteChanged(string path, string text)
    { RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text); }
    private static void CopyChanged(string source, string destination)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Android packaging input is missing.", source);
        RejectLinks(source); RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var input = new FileInfo(source); var output = new FileInfo(destination);
        if (!output.Exists || input.Length != output.Length || input.LastWriteTimeUtc != output.LastWriteTimeUtc) File.Copy(source, destination, overwrite: true);
    }
    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        RejectLinks(source); RejectLinks(destination);
        foreach (var file in Directory.EnumerateFiles(source)) CopyChanged(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Android packaging source contains a directory link.");
            if (Path.GetFileName(directory) is "build" or ".gradle") continue;
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
    private static void SyncTree(string source, string destination, Func<string, bool>? include = null, string? preserveDirectory = null)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(destination);
        var inputs = Directory.EnumerateFiles(source).Where(file => include?.Invoke(file) != false).ToArray();
        foreach (var file in inputs) CopyChanged(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var file in Directory.EnumerateFiles(destination)) if (!inputs.Any(input => Path.GetFileName(input) == Path.GetFileName(file))) { RejectLinks(file); File.Delete(file); }
        var directories = Directory.EnumerateDirectories(source).ToArray();
        foreach (var directory in directories)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Android assets contain a directory link.");
            SyncTree(directory, Path.Combine(destination, Path.GetFileName(directory)), include);
        }
        foreach (var directory in Directory.EnumerateDirectories(destination))
            if (Path.GetFileName(directory) != preserveDirectory && !directories.Any(input => Path.GetFileName(input) == Path.GetFileName(directory))) { RejectLinks(directory); Directory.Delete(directory, recursive: true); }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Android packaging does not follow filesystem links: " + current);
    }
}
