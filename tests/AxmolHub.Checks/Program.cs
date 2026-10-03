using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxmolHub.Core;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/checks");
Directory.CreateDirectory(root);
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
    var environmentEntry = new PlatformBuildService(runnerEntry, toolsEntry).CreateEnvironment(selectedEngine, BuildTargets.Get("android-arm64"));
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
    var environmentEntry = new PlatformBuildService(runnerEntry, toolsEntry).CreateEnvironment(selectedEngine, BuildTargets.Get("android-arm64"));
    foreach (var name in new[] { "HOME", "TEMP" }) Directory.CreateDirectory(environmentEntry[name]);
    var serviceEntry = new ProjectService(runnerEntry, new ToolchainDetector(runnerEntry, toolsEntry), toolsEntry, Path.GetFullPath("src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1"));
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
    var probe = await runnerEntry.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-Command", "[Console]::WriteLine($env:HUB_ANDROID_STORE_PASSWORD); [Console]::Error.WriteLine($env:HUB_ANDROID_KEY_PASSWORD)"], root,
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
    var environmentEntry = new PlatformBuildService(packageRunner, Path.Combine(root, "tools")).CreateEnvironment(selectedEngine, BuildTargets.Get(entry.Platform));
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
if (args.Contains("--prepare-android-verification"))
{
    var hub = new StateStore(root).Load();
    var entry = hub.Projects.Single(p => p.Name == "HelloAndroid");
    var selectedEngine = hub.Engines.Single(e => e.Version == entry.Version && e.Channel == entry.Channel);
    var packageRunner = new ProcessRunner(Console.WriteLine);
    var platform = new PlatformBuildService(packageRunner, Path.Combine(root, "tools"));
    var plan = platform.Plan(entry, selectedEngine, false);
    Directory.CreateDirectory(plan.OutputDirectory);
    foreach (var key in new[] { "HOME", "TEMP", "APPDATA", "LOCALAPPDATA" }) if (plan.Environment.TryGetValue(key, out var path)) Directory.CreateDirectory(path);
    File.WriteAllText(plan.Environment["GIT_CONFIG_GLOBAL"], "");
    var complete = Path.Combine(plan.OutputDirectory, ".hub-build-complete.json");
    if (File.Exists(complete)) File.Delete(complete);
    foreach (var command in plan.Commands)
    {
        var nativeResult = await packageRunner.RunAsync(command.Executable, command.Arguments, command.WorkingDirectory, plan.Environment, timeout: TimeSpan.FromHours(2));
        if (nativeResult.ExitCode != 0) throw new InvalidOperationException("Native Android preparation failed.");
    }
    var packaging = new AndroidPackageService(packageRunner, Path.Combine(root, "tools"));
    await packaging.EnsureDebugKeyAsync(plan.Environment, default);
    packaging.PrepareProject(entry, selectedEngine, plan.Environment);
    var gradle = packaging.GradleCommand(entry, plan.Environment, "--write-verification-metadata", "sha256", "assembleDebug", "bundleDebug");
    var build = await packageRunner.RunAsync(gradle.Executable, gradle.Arguments, gradle.WorkingDirectory, plan.Environment, timeout: TimeSpan.FromHours(1));
    if (build.ExitCode != 0) throw new InvalidOperationException("Gradle verification bootstrap failed.");
    File.Copy(Path.Combine(AndroidPackageService.StageDirectory(entry), "gradle/verification-metadata.xml"), Path.GetFullPath("manifests/android-gradle-verification.xml"), overwrite: true);
    Console.WriteLine("Maintainer dependency hashes generated from fixed official repositories. A strict production build is required next.");
    return;
}
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
    var detectorEntry = new ToolchainDetector(runnerEntry, Path.Combine(root, "tools"));
    foreach (var component in await detectorEntry.DetectAsync()) Write($"{component.Name}: {component.Status}");
    var storeEntry = new StateStore(root);
    var hubState = storeEntry.Load();
    var projectEntry = hubState.Projects.Single(p => p.Name == "HelloAxmol");
    var engineEntry = hubState.Engines.Single(e => e.Version == projectEntry.Version && e.Channel == projectEntry.Channel);
    var serviceEntry = new ProjectService(runnerEntry, detectorEntry, Path.Combine(root, "tools"), Path.GetFullPath("src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1"));
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
if (args.Contains("--prepare-windows") || args.Contains("--install-msvc"))
{
    var log = new HubLog(Path.Combine(root, "logs"));
    void Write(string line) { Console.WriteLine(line); log.Write(line); }
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var runnerEntry = new ProcessRunner(Write);
    var windows = new WindowsToolchainInstaller(new DownloadManager(http, Write), runnerEntry, root, Path.GetFullPath("manifests/toolchain-manifest.json"),
        Path.GetFullPath("src/AxmolHub.Core/Scripts/Verify-MicrosoftSignature.ps1"), Write);
    await windows.InstallSdkAsync();
    var prepared = await windows.PrepareBuildToolsAsync();
    if (args.Contains("--install-msvc")) await windows.InstallBuildToolsAsync(prepared);
    Console.WriteLine("Windows SDK installed; signed MSVC installation plan prepared.");
    return;
}
if (args.Contains("--install-tools"))
{
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var packages = PackageManifest.Read("manifests/toolchain-manifest.json");
    var installer = new PackageInstaller(new DownloadManager(http, Console.WriteLine), root, Console.WriteLine);
    foreach (var package in packages.Packages)
    {
        var destination = PackageInstaller.SafePath(root, package.Destination);
        if (Directory.Exists(destination)) continue;
        await installer.InstallAsync(package);
    }
    var tools = await new ToolchainDetector(new ProcessRunner(Console.WriteLine), Path.Combine(root, "tools")).DetectAsync();
    foreach (var tool in tools) Console.WriteLine($"{tool.Name}: {tool.Status}: {tool.Details}");
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
    var toolsRoot = Path.Combine(root, "tools");
    var detectorEntry = new ToolchainDetector(processRunner, toolsRoot);
    await detectorEntry.DetectAsync();
    var projectService = new ProjectService(processRunner, detectorEntry, toolsRoot, Path.GetFullPath("src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1"));
    var created = await projectService.CreateAsync("HelloAxmol", Path.Combine(root, "projects"), engineEntry);
    new StateStore(root).Save(new HubState { Engines = [engineEntry], Projects = [created], DefaultEnginePath = path });
    Console.WriteLine($"Axmol {release.Version} verified and project created. Game Build/Run still requires managed MSVC and Windows SDK.");
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
            Judge(commands.GetArrayLength() == 12 && commands.EnumerateArray().Any(command => command.GetString() == "install-tools"),
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
var result = await runner.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Value", hostile], root);
Check(result.ExitCode == 7 && result.Output.Trim() == hostile && result.Error.Contains("fixture stderr"), "ArgumentList, stdout, stderr, nonzero exit");
var secretFixture = "fixture-secret-\"quoted\"";
var redacted = await runner.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-File", script, "-Value", secretFixture], root, sensitiveValues: [secretFixture]);
Check(redacted.Output.Trim() == "[REDACTED]" && !messages.Any(line => line.Contains(secretFixture) || line.Contains(System.Text.Json.JsonSerializer.Serialize(secretFixture)[1..^1])),
    "Secret argument with quotes is redacted from command logging and captured output");
var sleeper = Path.Combine(root, "sleep.ps1");
File.WriteAllText(sleeper, "Start-Sleep -Seconds 30");
await Reject<TimeoutException>(() => runner.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-File", sleeper], root, timeout: TimeSpan.FromMilliseconds(300)), "Timeout stops process");
using (var cancellation = new CancellationTokenSource(300))
    await Reject<OperationCanceledException>(() => runner.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-File", sleeper], root, cancellation: cancellation.Token), "Cancellation stops process");

var detector = new ToolchainDetector(runner, Path.Combine(root, "tools"));
Directory.CreateDirectory(Path.Combine(root, "tools"));
var components = await detector.DetectAsync();
Check(components.All(c => c.Status == ComponentStatus.Missing), "Existing system tools never satisfy managed tools");
var engineRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("../axmol-2.11.5");
var engine = StateStore.ValidateEngine(engineRoot);
var environment = detector.BuildEnvironment(engine);
// 断言原意是"构建 PATH 里不能有从宿主继承来的开发工具路径"，原先写成"不含 D 盘"只是它的一个
// 代理判断 —— 只在本仓库不在 D 盘时成立，而 GitHub 的 Windows runner 工作目录正是 D:\a\...，
// 所以那条断言在 CI 上必然失败。改成直接表达原意、并且与机器无关的写法：
// 子环境 PATH 的每一项都必须能归到「受管工具根」或「操作系统目录」里，归不进去的就是继承来的。
var managedTools = Path.GetFullPath(Path.Combine(root, "tools"));
var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
var buildPath = environment["PATH"].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.TrimEnd('\\')).ToArray();
bool Managed(string entry) => entry.StartsWith(managedTools, StringComparison.OrdinalIgnoreCase);
bool OperatingSystemOwned(string entry) => entry.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase);
var leaked = buildPath.Where(entry => !Managed(entry) && !OperatingSystemOwned(entry)).ToArray();
Check(leaked.Length == 0, "Build PATH contains no inherited developer directories (leaked: " + string.Join(", ", leaked) + ")");
// 防退化：上面那条在 PATH 为空时也会通过，所以必须同时要求受管目录确实在起作用。
Check(buildPath.Count(Managed) > 0, "Build PATH resolves tools from the managed root (" + buildPath.Count(Managed) + " entries)");
Check(!environment["PATH"].Contains("Microsoft Visual Studio") && environment["INCLUDE"] == "" && environment["LIB"] == "", "Build environment excludes inherited developer paths");
var oldSdk = Environment.GetEnvironmentVariable("DXSDK_DIR");
try
{
    Environment.SetEnvironmentVariable("DXSDK_DIR", "D:\\must-not-inherit");
    var isolated = await runner.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-Command", "[Console]::Write($env:DXSDK_DIR)"], root, environment);
    Check(isolated.ExitCode == 0 && isolated.Output.Trim().Length == 0, "Complete child environment excludes inherited DXSDK_DIR");
}
finally { Environment.SetEnvironmentVariable("DXSDK_DIR", oldSdk); }

var wrapper = Path.GetFullPath("src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1");
var service = new ProjectService(runner, detector, Path.Combine(root, "tools"), wrapper);
await Reject<ArgumentException>(() => service.CreateAsync("bad;name", root, engine), "Unsafe template name rejected");
var parent = Path.Combine(root, "projects " + Guid.NewGuid().ToString("N"));
var project = await service.CreateAsync("HelloAxmol", parent, engine);
Check(File.Exists(Path.Combine(project.Path, "Source/AppDelegate.cpp")) && StateStore.ReadProject(project.Path).Version == engine.Version, "Real official CLI creates project and exact version lock");
Check(StateStore.ReadProject(project.Path).ProjectType == "cpp", "Default creation uses the official C++ template");
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
Check(preferencesStore.Load().Language == "zh-CN", "Unknown language falls back to Chinese");
Check(Directory.Exists(PreferencesStore.VerifyDirectory(preferences.DataRoot!)) && !Directory.EnumerateFiles(preferences.DataRoot!, ".hub-write-check-*").Any(), "Selected directory checked for write access without residue");
var buildFixture = Path.Combine(root, "engine-cache-" + Guid.NewGuid().ToString("N"));
ProjectService.PrepareEngineBuildDirectory(buildFixture, "original-installation");
File.WriteAllText(Path.Combine(buildFixture, "old-game.exe"), "stale binary");
ProjectService.PrepareEngineBuildDirectory(buildFixture, "repaired-installation");
Check(!File.Exists(Path.Combine(buildFixture, "old-game.exe")) && Directory.EnumerateDirectories(root, Path.GetFileName(buildFixture) + ".previous-*").Any(p => File.Exists(Path.Combine(p, "old-game.exe"))), "Engine repair invalidates stale build artifacts and retains recovery cache");

await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.SafePath(root, "../escape")), "Manifest path traversal rejected");
await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.SafePath(root, "C:\\escape")), "Absolute manifest path rejected");
var badZip = Path.Combine(root, Guid.NewGuid() + ".zip");
using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create)) zip.CreateEntry("../escape.txt");
await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.ExtractSafely(badZip, Path.Combine(root, "extracted"))), "ZIP traversal rejected");

var bytes = Encoding.UTF8.GetBytes("verified download fixture");
var handler = new FixtureHandler(bytes);
using var client = new HttpClient(handler);
var downloads = new DownloadManager(client, _ => { });
var cache = Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N"));
var sha = Convert.ToHexString(SHA256.HashData(bytes));
var downloaded = await downloads.DownloadAsync(new Uri("https://fixture.test/file.zip"), sha, cache);
Check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "SHA-256 download and atomic final file");
await downloads.DownloadAsync(new Uri("https://fixture.test/file.zip"), sha, cache);
Check(handler.Requests == 1, "Verified cache reused");
await Reject<InvalidDataException>(() => downloads.DownloadAsync(new Uri("https://fixture.test/file.zip"), new string('0', 64), cache), "Bad hash rejected");
Check(!Directory.EnumerateFiles(cache, "*.partial").Any() && !File.Exists(Path.Combine(cache, new string('0', 64) + ".zip")), "Failed downloads leave no installed/cache artifact");
await Reject<ArgumentException>(() => downloads.DownloadAsync(new Uri("http://fixture.test/file.zip"), sha, cache), "HTTP package rejected");
var incompleteSdk = Path.Combine(root, "incomplete-sdk");
Directory.CreateDirectory(incompleteSdk);
await Reject<InvalidDataException>(() => Task.Run(() => WindowsToolchainInstaller.VerifySdk(incompleteSdk, "10.0.26100.0")), "Incomplete SDK rejected");
var cancelledScript = Path.Combine(root, "must-not-start.ps1");
var startedMarker = Path.Combine(root, "unexpected-start.txt");
File.WriteAllText(cancelledScript, "param([string]$Marker)\nSet-Content -LiteralPath $Marker -Value started");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await Reject<OperationCanceledException>(() => runner.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-File", cancelledScript, "-Marker", startedMarker], root, cancellation: cancelled.Token), "Already-cancelled process never starts");
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
    var assetRoot = Path.Combine(root, "windows-assets-fixture-" + Guid.NewGuid().ToString("N"));
    var assetsProject = new ProjectEntry { Name = "Assets", Path = assetRoot };
    var assetsOutput = Path.Combine(BuildTargets.BuildDirectory(assetsProject), "bin/Assets");
    var assetsExe = Path.Combine(assetsOutput, "Assets.exe");
    Directory.CreateDirectory(Path.Combine(assetRoot, "Content"));
    Directory.CreateDirectory(Path.Combine(assetsOutput, "Content"));
    Directory.CreateDirectory(Path.Combine(assetsOutput, "axslc"));
    Directory.CreateDirectory(Path.Combine(BuildTargets.BuildDirectory(assetsProject), "runtime/axslc"));
    foreach (var shader in new[] { "positionTextureColor_vs", "positionTextureColor_fs", "label_normal_fs", "positionColorLengthTexture_vs", "positionColorLengthTexture_fs", "positionColorTextureAsPointsize_vs", "positionColor_fs" })
    {
        File.WriteAllText(Path.Combine(assetsOutput, "axslc", shader), "#version 300 es\nvoid main(){}\n");
        File.Copy(Path.Combine(assetsOutput, "axslc", shader), Path.Combine(BuildTargets.BuildDirectory(assetsProject), "runtime/axslc", shader));
    }
    File.WriteAllText(Path.Combine(assetRoot, "Content/image.png"), "fixture");
    File.WriteAllText(Path.Combine(assetsOutput, "Content/image.png"), "fixture");
    ProjectService.ValidateWindowsRuntimeAssets(assetsProject, assetsExe);
    Check(true, "Windows startup accepts complete deployed resources and matching shaders");
    var runtimeShader = Path.Combine(assetsOutput, "axslc/positionTextureColor_vs");
    File.WriteAllText(runtimeShader, "");
    await Reject<InvalidDataException>(() => Task.Run(() => ProjectService.ValidateWindowsRuntimeAssets(assetsProject, assetsExe)), "Empty runtime shader is rejected before launching GL");
    File.WriteAllText(runtimeShader, "#version 300 es\nvoid main(){ }\n");
    await Reject<InvalidDataException>(() => Task.Run(() => ProjectService.ValidateWindowsRuntimeAssets(assetsProject, assetsExe)), "Stale deployed shader is rejected");
    File.Copy(Path.Combine(BuildTargets.BuildDirectory(assetsProject), "runtime/axslc/positionTextureColor_vs"), runtimeShader, true);
    File.Delete(Path.Combine(assetsOutput, "Content/image.png"));
    await Reject<InvalidDataException>(() => Task.Run(() => ProjectService.ValidateWindowsRuntimeAssets(assetsProject, assetsExe)), "Missing deployed project resource is rejected");
    Directory.Delete(Path.Combine(assetsOutput, "Content"));
    await Reject<InvalidDataException>(() => Task.Run(() => ProjectService.ValidateWindowsRuntimeAssets(assetsProject, assetsExe)), "Missing runtime resource directory is rejected");
    File.WriteAllText(assetsExe, "compiled executable fixture");
    var publishedExe = ProjectService.PublishWindowsRuntime(assetsProject, assetsExe);
    ProjectService.ValidateWindowsRuntimeAssets(assetsProject, publishedExe);
    Check(File.Exists(Path.Combine(Path.GetDirectoryName(publishedExe)!, "Content/image.png")) && (File.GetAttributes(Path.Combine(Path.GetDirectoryName(publishedExe)!, "axslc")) & FileAttributes.ReparsePoint) == 0,
        "Windows publication copies actual resources independently of missing CMake output links");
    var previousExe = File.ReadAllText(publishedExe);
    File.Delete(Path.Combine(BuildTargets.BuildDirectory(assetsProject), "runtime/axslc/positionTextureColor_vs"));
    await Reject<InvalidDataException>(() => Task.Run(() => ProjectService.PublishWindowsRuntime(assetsProject, assetsExe)), "Incomplete shader build cannot replace the published Windows runtime");
    Check(File.ReadAllText(publishedExe) == previousExe, "Failed Windows resource publication preserves previous runtime");
    var platformRoot = Path.Combine(root, "platform-fixture-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(platformRoot);
    File.WriteAllText(Path.Combine(platformRoot, "CMakeLists.txt"), "# fixture");
    var entry = new ProjectEntry { Name = "Fixture", Path = platformRoot, Version = "2.11.5", Channel = "official-lts", BuildStatus = "Succeeded" };
    StateStore.LockProject(entry);
    var directories = new HashSet<string>();
    foreach (var target in BuildTargets.All)
    {
        BuildTargets.Select(entry, target.Id);
        Check(StateStore.ReadProject(platformRoot).Platform == target.Id && directories.Add(BuildTargets.BuildDirectory(entry)), "Target persists and build directory is isolated: " + target.Id);
    }
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
    var mismatchedService = new ProjectService(new ProcessRunner(_ => { }), new ToolchainDetector(new ProcessRunner(_ => { }), root), root, "");
    await Reject<InvalidOperationException>(() => mismatchedService.RunAsync(entry, new("2.11.5", root, "official-lts")), "Run refuses configuration different from locked project before starting a process");
    entry.Platform = "wasm32";
    var releasePlanService = new PlatformBuildService(new ProcessRunner(_ => { }), Path.Combine(platformRoot, "tools"));
    var releasePlan = releasePlanService.Plan(entry, new("2.11.5", root, "official-lts"), false, checkFiles: false, host: "windows");
    Check(releasePlan.Commands[0].Arguments.Contains("-DCMAKE_BUILD_TYPE=Release") && releasePlan.Commands[1].Arguments.Contains("Release") && releasePlan.OutputDirectory.EndsWith("-release"), "Release platform plan uses Release configuration and separate output");
    entry.Platform = "android-arm64";
    await Reject<InvalidOperationException>(() => Task.Run(() => releasePlanService.Plan(entry, new("2.11.5", root, "official-lts"), false, checkFiles: false)), "Android Release cannot silently use the debug signing profile");
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
    var androidReleasePlan = releasePlanService.Plan(entry, new("2.11.5", root, "official-lts"), false, checkFiles: false);
    Check(androidReleasePlan.Commands[0].Arguments.Contains("-DCMAKE_BUILD_TYPE=Release") && androidReleasePlan.OutputDirectory.EndsWith("-release"), "Configured Android Release reaches the isolated native build plan");
    entry.Configuration = "Debug";
    await Reject<InvalidDataException>(() => Task.Run(() => BuildTargets.Select(entry, "../../outside")), "Unknown target cannot escape build directories");
    var platformService = new PlatformBuildService(new ProcessRunner(_ => { }), Path.Combine(platformRoot, "tools"));
    entry.Platform = "ios-arm64";
    await Reject<PlatformNotSupportedException>(() => Task.Run(() => platformService.Plan(entry, new("2.11.5", platformRoot), false, false, "windows")), "Apple build on Windows is rejected before any process");
    entry.Platform = "wasm32";
    var wasmEnvironment = platformService.CreateEnvironment(new("2.11.5", platformRoot), BuildTargets.Get(entry.Platform));
    // 同 "Build PATH contains no inherited developer directories"：这里原先也是拿 "D:" 当代理判断，
    // 只在工作根不在 D 盘时成立。改为按受管根归一化。
    var wasmTools = Path.GetFullPath(Path.Combine(platformRoot, "tools"));
    var wasmPath = wasmEnvironment["PATH"].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.TrimEnd('\\')).ToArray();
    var wasmLeaked = wasmPath.Where(entry => !entry.StartsWith(wasmTools, StringComparison.OrdinalIgnoreCase) && !entry.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
    Check(!wasmEnvironment.ContainsKey("DXSDK_DIR") && wasmLeaked.Length == 0 && wasmEnvironment["EM_CONFIG"].StartsWith(platformRoot),
        "Web build environment isolates developer paths and emsdk activation (leaked: " + string.Join(", ", wasmLeaked) + ")");
    await Reject<InvalidOperationException>(() => Task.Run(() => platformService.Plan(entry, new("2.11.5", platformRoot), false)), "Missing target tools block build without system fallback");
    Directory.CreateDirectory(BuildTargets.BuildDirectory(entry));
    File.WriteAllText(Path.Combine(BuildTargets.BuildDirectory(entry), "Fixture.exe"), "wrong-target");
    await Reject<FileNotFoundException>(() => Task.Run(() => PlatformBuildService.FindArtifact(entry)), "Windows executable cannot satisfy a WebAssembly build");
    var concurrentStore = new StateStore(Path.Combine(platformRoot, "concurrent-state"));
    await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => concurrentStore.SaveProject(new ProjectEntry { Name = "Concurrent" + index, Path = Path.Combine(platformRoot, index.ToString()), Version = "2.11.5" }))));
    Check(concurrentStore.Load().Projects.Count == 8, "Concurrent CLI project updates preserve other projects");
}
{
    var moduleRoot = Path.Combine(root, "modules-" + Guid.NewGuid().ToString("N"));
    var modules = new EngineModules(moduleRoot, Path.GetFullPath("manifests"));
    var plan = modules.Plan(engine, ["android", "web"]);
    Check(plan.Packages.Count(p => p.Id == "cmake") == 1 && plan.Packages.Count == 13, "Android and Web share dependencies without duplicate installs");
    Check(plan.DownloadBytes > 0 && plan.InstalledBytes > plan.DownloadBytes && !plan.HasUnknownSize, "Module package sizes come from measured manifests");
    var deferred = modules.Plan(engine, ["ios", "tvos", "linux", "uwp"]);
    Check(deferred.Packages.Count == 0 && deferred.Installers.Length == 0 && deferred.DeferredModules.Length == 4, "Source-only and pending modules never install Windows packages");
    modules.Save(engine, ["android", "web", "android"]);
    Check(modules.Load(engine).ModuleIds.SequenceEqual(new[] { "android", "web" }), "Module choices persist with duplicate choices removed");
    var anotherEngine = engine with { Path = Path.Combine(moduleRoot, "other-engine") };
    Check(!modules.HasSelection(anotherEngine) && modules.Load(anotherEngine).ModuleIds.Length == 0, "Module selection belongs to the exact engine installation");
    modules.Save(engine, []);
    Check(modules.HasSelection(engine) && modules.Load(engine).ModuleIds.Length == 0, "Explicitly deselected modules remain deselected after restart");
    await Reject<InvalidDataException>(() => Task.Run(() => modules.Save(engine, ["unknown"])), "Unknown module cannot be persisted");
    await Reject<InvalidOperationException>(() => Task.Run(() => modules.ForEngine(engine with { Version = "99.0.0" })), "Unverified engine version cannot use another version module profile");
    // 打包配方的版本验证边界同样来自清单，不是代码里的字面量：同一个配方在已声明与未声明的版本上必须给出相反结论。
    PackagingRecipes.RequireVerified(engine, PackagingRecipes.AndroidPackaging);
    await Reject<InvalidOperationException>(() => Task.Run(() => PackagingRecipes.RequireVerified(engine with { Version = "99.0.0" }, PackagingRecipes.AndroidPackaging)), "Unverified engine version cannot borrow another version packaging recipe");
    await Reject<InvalidOperationException>(() => Task.Run(() => PackagingRecipes.RequireVerified(engine, "recipe-that-is-not-declared")), "Recipe not declared for the engine version is refused");
    var package = modules.Packages()["cmake"];
    var destination = PackageInstaller.SafePath(moduleRoot, package.Destination);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(destination, package.VerifyFile))!);
    File.WriteAllText(Path.Combine(destination, package.VerifyFile), "fixture");
    Check(!modules.IsPackageInstalled(package), "Loose tool files never imply a managed module installation");
    StateStore.WriteJson(Path.Combine(destination, ".hub-install.json"), new { package.Id, package.Version, package.Sha256 });
    Check(modules.IsPackageInstalled(package) && modules.Plan(engine, ["web"]).Packages.All(p => p.Id != "cmake"), "Verified receipt excludes an installed dependency from the install plan");
    StateStore.WriteJson(Path.Combine(destination, ".hub-install.json"), new { package.Id, package.Version, Sha256 = "invalid" });
    Directory.CreateDirectory(Path.Combine(moduleRoot, "cache"));
    File.WriteAllText(Path.Combine(moduleRoot, "cache", package.Sha256.ToLowerInvariant() + ".zip"), "corrupt cache");
    Check(!modules.IsPackageInstalled(package) && modules.Plan(engine, ["web"]).DownloadBytes == modules.Plan(anotherEngine, ["web"]).Packages.Sum(p => p.DownloadBytes ?? 0), "Wrong receipt and corrupt cache cannot report installed or zero-download state");
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
    var androidPlan = new PlatformBuildService(runner, Path.Combine(root, "tools")).Plan(entry, engine, false, checkFiles: false);
    Check(androidPlan.Commands[0].Arguments.Any(argument => argument.StartsWith("-D_AX_ANDROID_PROJECT_DIR=") && argument.Contains("build-hub-android-arm64/android/app"))
        && androidPlan.Environment["JAVA_HOME"].StartsWith(root) && androidPlan.Environment["ANDROID_USER_HOME"].StartsWith(root), "Android shaders, Java and SDK authorization data stay in managed directories");
}
Console.WriteLine($"{count} checks passed. Real platform builds, device deployment and clean-host acceptance require separate evidence.");

sealed class FixtureHandler(byte[] bytes) : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
}
