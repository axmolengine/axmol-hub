using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;
using static CheckSupport;

internal static class DefaultChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var messages = new List<string>();
        var runner = new ProcessRunner(message =>
        {
            lock (messages)
                messages.Add(message);
        });
        var script = Path.Combine(root, "process fixture.ps1");
        File.WriteAllText(script, "param([string]$Value)\n[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n[Console]::WriteLine($Value)\n[Console]::Error.WriteLine('fixture stderr')\nexit 7\n");
        var hostile = "spaces ; & $() ` \" quote 中文";
        var result = await runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Value", hostile], root);
        Check(result.ExitCode == 7 && result.Output.Trim() == hostile && result.Error.Contains("fixture stderr"), "ArgumentList, stdout, stderr, nonzero exit");
        var secretFixture = "fixture-secret-\"quoted\"";
        var redacted = await runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-File", script, "-Value", secretFixture], root, sensitiveValues: [secretFixture]);
        Check(redacted.Output.Trim() == "[REDACTED]" && !messages.Any(line => line.Contains(secretFixture) || line.Contains(System.Text.Json.JsonSerializer.Serialize(secretFixture)[1..^1])), "Secret argument with quotes is redacted from command logging and captured output");
        var sleeper = Path.Combine(root, "sleep.ps1");
        File.WriteAllText(sleeper, "Start-Sleep -Seconds 30");
        await Reject<TimeoutException>(() => runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-File", sleeper], root, timeout: TimeSpan.FromMilliseconds(300)), "Timeout stops process");
        using (var cancellation = new CancellationTokenSource(300))
            await Reject<OperationCanceledException>(() => runner.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-File", sleeper], root, cancellation: cancellation.Token), "Cancellation stops process");
        var engineRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("../axmol-2.11.5");
        var engine = StateStore.ValidateEngine(engineRoot);
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        // The toolchain's source of truth has moved from "Hub's data-root/tools" to the **engine tree**: expected versions
        // come from <engine>/1k/build.profiles, and the landing spot is <engine>/tools/external from the official setup.ps1.
        var managedTools = Path.GetFullPath(Path.Combine(root, "tools"));
        var engineTools = EngineToolchain.ToolRoot(engine);
        Check(engineTools == Path.Combine(engine.Path, "tools", "external") && !engineTools.StartsWith(managedTools, StringComparison.OrdinalIgnoreCase), "Toolchain resolution targets the engine tree instead of a Hub-managed tools root");
        var engineComponents = await new EngineToolchain(runner).InspectAsync(engine, "windows-x64");
        Check(engineComponents.Count >= 3 && engineComponents.All(c => c.Status is not (ComponentStatus.Unknown or ComponentStatus.Checking)), "Engine toolchain probe reports every component with a conclusive state");
        Check(engineComponents.All(c => c.Details.Length > 0) && engineComponents.Any(c => c.Name == "CMake"), "Engine toolchain probe names components and explains their state");
        var profile = BuildProfile.Load(engine.Path);
        Check(profile.Cmake is { Length: > 0 } && profile.Ndk is { Length: > 0 } && profile.TargetSdk is { Length: > 0 }, "Engine build profile supplies the expected tool versions (source of truth)");
        // The auxiliary environment (used when Hub calls adb/keytool/emrun directly) must be rooted at the engine tree and
        // must no longer point at Hub's tools directory.
        var auxiliary = new PlatformBuildService(runner).CreateEnvironment(engine, BuildTargets.Get("wasm32"));
        Check(auxiliary["AX_ROOT"] == engine.Path && auxiliary["EMSDK"] == Path.Combine(engineTools, "emsdk") && auxiliary["PATH"].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).All(part => !part.StartsWith(managedTools, StringComparison.OrdinalIgnoreCase)), "Auxiliary environment is rooted at the engine tree, not at a Hub-managed tools root");
        Check(auxiliary.ContainsKey("HOME") && auxiliary.ContainsKey("TEMP"), "Auxiliary environment always exposes HOME/TEMP for child tools");
        var wrapper = Path.GetFullPath("src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1");
        var service = new ProjectService(runner, EngineCli(runner), new EnginePrebuiltState(root));
        await Reject<ArgumentException>(() => service.CreateAsync("bad;name", root, engine), "Unsafe template name rejected");
        var parent = Path.Combine(root, "projects " + Guid.NewGuid().ToString("N"));
        var project = await service.CreateAsync("HelloAxmol", parent, engine);
        Check(File.Exists(Path.Combine(project.Path, "Source/AppDelegate.cpp")) && StateStore.ReadProject(project.Path).Version == engine.Version, "Real official CLI creates project and exact version lock");
        Check(StateStore.ReadProject(project.Path).ProjectType == "cpp", "Default creation uses the official C++ template");
        Check(project.Platform == BuildTargets.DefaultForHost(engine.Version).Id && StateStore.ReadProject(project.Path).Platform == project.Platform, "New project locks the host default build target and reads it back");
        Check(BuildTargets.DefaultForHost("3.0.0", "macos", "arm64").Id == "macos-arm64" && BuildTargets.DefaultForHost("3.0.0", "macos", "x64").Id == "macos-x64", "macOS project defaults match the host architecture");
        Check(BuildTargets.DefaultForHost("3.0.0", "windows", "arm64").Id == "windows-x64", "Windows project default remains x64");
        // When the engine creates a project from a source tree that has .git, engine_version carries the short commit hash
        // (axmol.ps1 appends -<hash>). The hash holds no compatibility information, so Hub accepts it and normalises to
        // x.y.z; pre-release tags are still refused — the exact-version discipline is not relaxed.
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
        luaProject.ProjectType = "lua";
        StateStore.LockProject(luaProject);
        var absentTemplate = new EngineEntry(engine.Version, Path.Combine(root, "missing-lua-engine"));
        await Reject<InvalidDataException>(() => service.CreateAsync("MissingLua", parent, absentTemplate, projectType: "lua"), "Missing engine template is rejected before invoking the official CLI");
        await Reject<IOException>(() => service.CreateAsync("HelloAxmol", parent, engine), "Existing project never overwritten");
        await Reject<InvalidOperationException>(() => service.BuildAsync(project, engine), "Missing managed compiler blocks build without system fallback");
        var stateStore = new StateStore(Path.Combine(root, "state"));
        var state = new HubState
        {
            Engines = [engine],
            Projects = [project],
            DefaultEnginePath = engine.Path
        };
        stateStore.Save(state);
        Check(stateStore.Load().Projects.Single().Version == engine.Version, "State persistence");
        var preferencesStore = new PreferencesStore(Path.Combine(root, "preferences.json"));
        File.WriteAllText(Path.Combine(root, "preferences.json"), "{}");
        Check(preferencesStore.Load().UpdateChannel == UpdateChannels.Stable, "A pre-existing preferences file without an update-channel key keeps the Stable default");
        Check(UpdateChannels.All.SequenceEqual(new[] { UpdateChannels.Stable, UpdateChannels.Preview }) && UpdateChannels.IncludesPrereleases(UpdateChannels.Stable, false) == false && UpdateChannels.IncludesPrereleases(UpdateChannels.Preview, false) && UpdateChannels.IncludesPrereleases(UpdateChannels.Stable, true) && UpdateChannels.Normalize("unknown") == UpdateChannels.Stable, "Stable / Preview policy defaults and normalizes safely, while preview builds always include pre-releases");
        var preferences = new HubPreferences
        {
            Language = "en-US",
            DataRoot = Path.Combine(root, "独立资料库"),
            ProjectDirectory = Path.Combine(root, "用户项目")
        };
        preferences.UpdateChannel = UpdateChannels.Preview;
        preferencesStore.Save(preferences);
        Check(preferencesStore.Load().Language == "en-US" && preferencesStore.Load().ProjectDirectory == preferences.ProjectDirectory && preferencesStore.Load().DataRoot == preferences.DataRoot && preferencesStore.Load().UpdateChannel == UpdateChannels.Preview, "Language, update channel and selected directories survive restart");
        preferences.Language = "unsupported";
        preferences.UpdateChannel = "unknown";
        preferencesStore.Save(preferences);
        Check(preferencesStore.Load().Language == "en-US", "Unknown language falls back to the default (English)");
        Check(preferencesStore.Load().UpdateChannel == UpdateChannels.Stable, "Unknown update channel falls back to Stable");
        Check(Directory.Exists(PreferencesStore.VerifyDirectory(preferences.DataRoot!)) && !Directory.EnumerateFiles(preferences.DataRoot!, ".hub-write-check-*").Any(), "Selected directory checked for write access without residue");
        // Build discovery follows the selected target's actual artifact and does not depend on a Hub directory convention or helper script.
        var buildFixture = Path.Combine(root, "engine-layout-" + Guid.NewGuid().ToString("N"));
        var layoutProject = new ProjectEntry
        {
            Name = "Fixture",
            Path = buildFixture,
            Platform = "windows-x64",
            Configuration = "Debug"
        };
        var scannedBuild = Path.Combine(buildFixture, "build_win32_x64");
        Directory.CreateDirectory(Path.Combine(scannedBuild, "bin", "Fixture", "Debug"));
        File.WriteAllText(Path.Combine(scannedBuild, "bin", "Fixture", "Debug", "Fixture.exe"), "binary");
        Check(EngineBuildLayout.FindBuildDirectory(layoutProject) == scannedBuild && EngineBuildLayout.FindArtifact(scannedBuild, "Fixture", "Debug", "windows")is not null, "Engine build directory and artifact are discovered from the engine's own layout");
        File.WriteAllText(Path.Combine(buildFixture, "run.bat"), "set BUILD_DIR=build_declared\n");
        Directory.CreateDirectory(Path.Combine(buildFixture, "build_declared"));
        Check(EngineBuildLayout.FindBuildDirectory(layoutProject) == scannedBuild, "Build discovery ignores legacy run.bat and follows the selected target artifact");
        Check(EngineBuildLayout.FindArtifact(scannedBuild, "Fixture", "Release", "windows")is null && EngineBuildLayout.FindArtifact(scannedBuild, "Fixture", "Debug", "wasm")is null, "Artifact discovery never borrows another configuration's or target's output");
        var macBuild = Path.Combine(buildFixture, "build", "build");
        var macBundleExecutable = Path.Combine(macBuild, "Debug", "Fixture.app", "Contents", "MacOS", "Fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(macBundleExecutable)!);
        File.WriteAllText(macBundleExecutable, "binary");
        Check(EngineBuildLayout.FindArtifact(macBuild, "Fixture", "Debug", "macos") == macBundleExecutable, "macOS app bundles are discovered from the configuration directory beside bin");
        await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.SafePath(root, "../escape")), "Manifest path traversal rejected");
        await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.SafePath(root, "C:\\escape")), "Absolute manifest path rejected");
        var badZip = Path.Combine(root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create))
            zip.CreateEntry("../escape.txt");
        await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.ExtractSafely(badZip, Path.Combine(root, "extracted"))), "ZIP traversal rejected");
        // The official axmol-2.x release assets store every entry name with '\' — all 9198 entries of
        // axmol-2.11.5.zip. On Windows that happens to be the separator anyway; on a host where it is an ordinary
        // character the extraction used to place flat files literally named "axmol-2.11.5\core\..." in the staging
        // directory, so the engine root never existed and the install died with "missing axmol/axmolver.h.in (v3) or
        // core/axmolver.h.in (v2)" — a message that blamed the v2/v3 layout instead of the archive.
        var backslashZip = Path.Combine(root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFile.Open(backslashZip, ZipArchiveMode.Create))
        {
            using (var header = new StreamWriter(zip.CreateEntry("axmol-2.11.5\\core\\axmolver.h.in").Open()))
                header.Write("#define AX_VERSION_MAJOR 2\n#define AX_VERSION_MINOR 11\n#define AX_VERSION_PATCH 5\n");
            foreach (var marker in new[]
            {
                "1k\\1kiss.ps1",
                "tools\\cmdline\\axmol.ps1",
                "templates\\cpp\\axproj-template.json"
            }

            )
                using (var file = new StreamWriter(zip.CreateEntry("axmol-2.11.5\\" + marker).Open()))
                    file.Write("marker");
        }

        var backslashOut = Path.Combine(root, "backslash-extracted");
        await Task.Run(() => PackageInstaller.ExtractSafely(backslashZip, backslashOut));
        Check(File.Exists(Path.Combine(backslashOut, "axmol-2.11.5", "core", "axmolver.h.in")) && !Directory.EnumerateFiles(backslashOut).Any(entry => Path.GetFileName(entry).Contains('\\')), "Backslash-separated archive extracts into real directories, not flat backslash-named files");
        Check(StateStore.ValidateEngine(Path.Combine(backslashOut, "axmol-2.11.5"), "official-lts").Version == "2.11.5", "The v2 engine asset from the backslash archive validates through the core/ layout, not the axmol/ one");
        var backslashTraversalZip = Path.Combine(root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFile.Open(backslashTraversalZip, ZipArchiveMode.Create))
            zip.CreateEntry("..\\escape.txt");
        await Reject<InvalidDataException>(() => Task.Run(() => PackageInstaller.ExtractSafely(backslashTraversalZip, Path.Combine(root, "backslash-escape"))), "Backslash ZIP traversal rejected");
        // Deciding SDK integrity is back with the engine (it validates during the CMake configure step); Hub no longer
        // assembles or verifies an SDK of its own.
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
            using (var text = new StreamWriter(zip.CreateEntry("bin/tool.exe").Open()))
                text.Write("verified package executable");
            var packageBytes = packageStream.ToArray();
            using var packageClient = new HttpClient(new FixtureHandler(packageBytes));
            var packages = new PackageInstaller(new DownloadManager(packageClient, _ =>
            {
            }), lifecycleRoot, _ =>
            {
            });
            var package = new PackageEntry
            {
                Id = "fixture",
                Version = "1.0",
                Destination = "tools/fixture",
                VerifyFile = "bin/tool.exe",
                Url = "https://fixture.test/package.zip",
                Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes))
            };
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
            var entry = new ProjectEntry
            {
                Name = "Fixture",
                Path = platformRoot,
                Version = "2.11.5",
                Channel = "official-lts",
                BuildStatus = "Succeeded"
            };
            StateStore.LockProject(entry);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            // Walking every target needs a v3 engine (only v3 carries its exclusive targets, arm64/wasm64 included).
            entry.Version = "3.0.0";
            StateStore.LockProject(entry);
            foreach (var target in BuildTargets.All)
            {
                BuildTargets.Select(entry, target.Id);
                Check(StateStore.ReadProject(platformRoot).Platform == target.Id && targets.Add(entry.Platform), "Target persists independently: " + target.Id);
            }

            // A v2 engine must not select a v3-only target (win32 arm64 / linux arm64 / wasm64).
            entry.Version = "2.11.5";
            StateStore.LockProject(entry);
            await Reject<PlatformNotSupportedException>(() => Task.Run(() => BuildTargets.Select(entry, "windows-arm64")), "A v2 engine cannot target Windows ARM64");
            await Reject<PlatformNotSupportedException>(() => Task.Run(() => BuildTargets.Select(entry, "linux-arm64")), "A v2 engine cannot target Linux ARM64");
            await Reject<PlatformNotSupportedException>(() => Task.Run(() => BuildTargets.Select(entry, "wasm64")), "A v2 engine cannot target wasm64");
            // Cross-compile and run rules (user, 2026-10-03): Windows may cross-compile arm64 but running it needs an
            // arm64 host; Linux supports no cross-compiling, so v3's linux arm64 builds only on an arm64 machine.
            Check(BuildTargets.Get("windows-arm64").CanCrossBuild("x64") && !BuildTargets.Get("windows-arm64").CanRunLocally("x64") && BuildTargets.Get("windows-arm64").CanRunLocally("arm64"), "Windows ARM64 cross-compiles on x64 but only runs on an arm64 host");
            Check(!BuildTargets.Get("linux-arm64").CanCrossBuild("x64") && BuildTargets.Get("linux-arm64").CanCrossBuild("arm64"), "Linux ARM64 builds only on an arm64 host (no cross-compile)");
            Check(BuildTargets.Get("linux-x64").CanCrossBuild("x64") && !BuildTargets.Get("linux-x64").CanCrossBuild("arm64"), "Linux x64 builds on x64 but not cross-compiled on arm64");
            Check(BuildTargets.SameArch("x86_64", "x64") && BuildTargets.SameArch("aarch64", "arm64") && !BuildTargets.SameArch("x64", "arm64"), "x86_64/x64 and aarch64/arm64 are the same arch; x64 vs arm64 differ");
            Check(BuildTargets.Get("windows-x64").CanRunLocally("x64") && BuildTargets.Get("android-arm64").CanCrossBuild("x64"), "Windows x64 runs locally; Android cross-compiles regardless of host arch");
            Check(entry.BuildStatus == "Not built", "Target switch never reuses another target success status");
            BuildTargets.Select(entry, "windows-x64", "Debug");
            entry.BuildStatus = "Succeeded";
            BuildTargets.Select(entry, "windows-x64", "Release");
            Check(StateStore.ReadProject(platformRoot).Configuration == "Release" && entry.BuildStatus == "Not built", "Configuration persists and switching resets success status");
            await Reject<InvalidDataException>(() => Task.Run(() => BuildTargets.Select(entry, "windows-x64", "../../outside")), "Invalid build configuration cannot escape output directory");
            StateStore.WriteJson(StateStore.MetadataPath(platformRoot), new { engine = "axmol", version = "2.11.5", channel = "official-lts", platform = "windows-x64" });
            Check(StateStore.ReadProject(platformRoot).Configuration == "Debug", "Legacy project metadata defaults to Debug");
            var mismatchedService = new ProjectService(new ProcessRunner(_ =>
            {
            }), EngineCli(new ProcessRunner(_ =>
            {
            })), new EnginePrebuiltState(root));
            await Reject<InvalidOperationException>(() => mismatchedService.RunAsync(entry, new("2.11.5", root, "official-lts")), "Run refuses configuration different from locked project before starting a process");
            entry.Platform = "wasm32";
            entry.Configuration = "Release";
            var releasePlan = PlatformBuildService.Plan(entry, engine, false, new EnginePrebuiltState(root));
            Check(releasePlan.SubCommand == "build" && releasePlan.Arguments.Contains("-O3") && releasePlan.Arguments.Contains("wasm") && releasePlan.Arguments.Contains(platformRoot), "Release plan maps to the engine's own axmol build invocation");
            entry.Platform = "android-arm64";
            await Reject<InvalidOperationException>(() => Task.Run(() => BuildConfigurations.ValidateTarget(entry)), "Android Release cannot silently use the debug signing profile");
            entry.Configuration = "Debug";
            var releaseSettings = new AndroidReleaseSettings
            {
                ApplicationId = "com.axmolhub.example",
                KeyAlias = "upload",
                KeystorePath = Path.GetFullPath(Path.Combine(root, "fixture.jks"))
            };
            releaseSettings.Validate(false);
            Check(AndroidPackageService.ApkPath(new() { Path = platformRoot, Platform = "android-arm64", Configuration = "Release" }).EndsWith("app-release.apk"), "Android Release APK path cannot use the Debug artifact");
            await Reject<FileNotFoundException>(() => Task.Run(() => releaseSettings.Validate()), "Missing release keystore blocks signing");
            releaseSettings.VersionCode = 0;
            await Reject<InvalidDataException>(() => Task.Run(() => releaseSettings.Validate(false)), "Invalid release versionCode is rejected");
            releaseSettings.VersionCode = 1;
            releaseSettings.KeyAlias = "androiddebugkey";
            await Reject<InvalidDataException>(() => Task.Run(() => releaseSettings.Validate(false)), "Debug key alias cannot configure Release");
            releaseSettings.KeyAlias = "upload";
            releaseSettings.ApplicationId = "bad application";
            await Reject<InvalidDataException>(() => Task.Run(() => releaseSettings.Validate(false)), "Invalid release application ID is rejected");
            await Reject<InvalidDataException>(() => Task.Run(() => new AndroidSigningPasswords("short", "short").Validate()), "Invalid signing passwords are rejected");
            releaseSettings.ApplicationId = "com.axmolhub.example";
            File.WriteAllText(releaseSettings.KeystorePath, "fixture key bytes");
            releaseSettings.Save(entry);
            Check(AndroidReleaseSettings.Load(entry)?.KeyAlias == "upload" && !File.ReadAllText(AndroidReleaseSettings.PathFor(entry)).Contains("Password"), "Release settings persist without password fields");
            var keyFingerprint = releaseSettings.Fingerprint();
            File.AppendAllText(releaseSettings.KeystorePath, "changed");
            Check(releaseSettings.Fingerprint() != keyFingerprint, "Replacing a keystore invalidates its release receipt fingerprint");
            entry.Configuration = "Release";
            var androidReleasePlan = PlatformBuildService.Plan(entry, engine, false, new EnginePrebuiltState(root));
            Check(androidReleasePlan.Arguments.Contains("android") && androidReleasePlan.Arguments.Contains("-O3"), "Configured Android Release maps to the engine's release build invocation");
            entry.Configuration = "Debug";
            await Reject<InvalidDataException>(() => Task.Run(() => BuildTargets.Select(entry, "../../outside")), "Unknown target cannot escape build directories");
            var platformService = new PlatformBuildService(new ProcessRunner(_ =>
            {
            }));
            entry.Platform = "ios-arm64";
            Check(!BuildTargets.Get(entry.Platform).CanBuildOn("windows") && BuildTargets.Get(entry.Platform).Hosts.Contains("macos"), "Apple targets declare macOS-only hosts, so an Apple build on Windows is rejected before any process");
            entry.Platform = "wasm32";
            var wasmEngine = new EngineEntry("2.11.5", platformRoot);
            var wasmEnvironment = platformService.CreateEnvironment(wasmEngine, BuildTargets.Get(entry.Platform));
            // The toolchain is back with the engine: the auxiliary environment is rooted in the **engine tree**
            // (<engine>/tools/external) and no longer points at Hub's data-root/tools.
            var wasmTools = EngineToolchain.ToolRoot(wasmEngine);
            var hubTools = Path.GetFullPath(Path.Combine(root, "tools"));
            var wasmPath = wasmEnvironment["PATH"].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            Check(wasmEnvironment["AX_ROOT"] == platformRoot && wasmEnvironment["EMSDK"] == Path.Combine(wasmTools, "emsdk") && wasmPath.All(part => !part.StartsWith(hubTools, StringComparison.OrdinalIgnoreCase)), "Auxiliary environment is rooted at the engine tree instead of a Hub-managed tools root");
            // Artifact candidates are narrowed by target family: a Windows .exe cannot satisfy a WebAssembly build.
            var wasmNoise = Path.Combine(platformRoot, "build_wasm");
            Directory.CreateDirectory(Path.Combine(wasmNoise, "bin", "Fixture"));
            File.WriteAllText(Path.Combine(wasmNoise, "bin", "Fixture", "Fixture.exe"), "wrong-target");
            await Reject<FileNotFoundException>(() => Task.Run(() => PlatformBuildService.FindArtifact(entry)), "A Windows executable cannot satisfy a WebAssembly build");
            var concurrentStore = new StateStore(Path.Combine(platformRoot, "concurrent-state"));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => concurrentStore.SaveProject(new ProjectEntry { Name = "Concurrent" + index, Path = Path.Combine(platformRoot, index.ToString()), Version = "2.11.5" }))));
            Check(concurrentStore.Load().Projects.Count == 8, "Concurrent CLI project updates preserve other projects");
        }

        {
            // The packaging recipe's version-verification boundary comes from the manifest (recipe-manifest.json),
            // not from a literal in code: the same recipe must reach opposite verdicts on a declared and an
            // undeclared engine version.
            PackagingRecipes.RequireVerified(engine, PackagingRecipes.AndroidPackaging);
            await Reject<InvalidOperationException>(() => Task.Run(() => PackagingRecipes.RequireVerified(engine with { Version = "99.0.0" }, PackagingRecipes.AndroidPackaging)), "Unverified engine version cannot borrow another version packaging recipe");
            await Reject<InvalidOperationException>(() => Task.Run(() => PackagingRecipes.RequireVerified(engine, "recipe-that-is-not-declared")), "Recipe not declared for the engine version is refused");
        }

        {
            var androidRoot = Path.Combine(root, "android-fixture-" + Guid.NewGuid().ToString("N"));
            var entry = new ProjectEntry
            {
                Name = "Fixture",
                Path = androidRoot,
                Platform = "android-arm64",
                Version = "2.11.5",
                Channel = "local"
            };
            Directory.CreateDirectory(Path.Combine(androidRoot, "proj.android/app"));
            File.WriteAllText(Path.Combine(androidRoot, ".axproj"), "package_name=dev.axmol.fixture\n");
            File.WriteAllText(Path.Combine(androidRoot, "proj.android/app/AndroidManifest.xml"), "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\"><application><activity android:name=\".MainActivity\"><intent-filter><action android:name=\"android.intent.action.MAIN\"/><category android:name=\"android.intent.category.LAUNCHER\"/></intent-filter></activity></application></manifest>");
            Check(AndroidPackageService.LaunchComponent(entry) == "dev.axmol.fixture/dev.axmol.fixture.MainActivity", "Android launch component preserves namespace and relative activity");
            File.WriteAllText(Path.Combine(androidRoot, ".axproj"), "package_name=bad'; injected\n");
            await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.PackageName(entry)), "Invalid Android application ID rejected before Gradle generation");
            File.WriteAllText(Path.Combine(androidRoot, ".axproj"), "package_name=dev.axmol.fixture\n");
            var engineBuild = Path.Combine(androidRoot, "build");
            var nativeOutput = Path.Combine(engineBuild, "bin", "Fixture", "Debug", "libFixture.so");
            Directory.CreateDirectory(Path.GetDirectoryName(nativeOutput)!);
            File.WriteAllText(nativeOutput, "native only");
            await Reject<FileNotFoundException>(() => Task.Run(() => PlatformBuildService.FindArtifact(entry)), "Native library alone cannot satisfy an Android APK build");
            byte[] Elf(ushort machine, ulong alignment)
            {
                var bytes = new byte[120];
                bytes[0] = 0x7f;
                bytes[1] = (byte)'E';
                bytes[2] = (byte)'L';
                bytes[3] = (byte)'F';
                bytes[4] = 2;
                bytes[5] = 1;
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
            var apk = AndroidPackageService.ApkPath(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(apk)!);
            using (var zip = ZipFile.Open(apk, ZipArchiveMode.Create))
            {
                foreach (var file in new[]
                {
                    "AndroidManifest.xml",
                    "classes.dex",
                    "assets/axslc/fixture_vs"
                }

                )
                {
                    using var data = zip.CreateEntry(file).Open();
                    data.Write(Encoding.UTF8.GetBytes("fixture"));
                }

                foreach (var file in new[]
                {
                    "libFixture.so",
                    "libopenal.so",
                    "libc++_shared.so"
                }

                )
                {
                    using var data = zip.CreateEntry("lib/arm64-v8a/" + file).Open();
                    data.Write(Elf(183, 16384));
                }
            }

            AndroidPackageService.ValidateApk(apk, entry);
            Check(PlatformBuildService.FindArtifact(entry) == apk, "Android artifact resolves to packaged APK instead of native output");
            var bundle = AndroidPackageService.BundlePath(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
            using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
            {
                foreach (var file in new[]
                {
                    "base/manifest/AndroidManifest.xml",
                    "base/dex/classes.dex",
                    "base/assets/axslc/fixture_vs",
                    "BundleConfig.pb",
                    "META-INF/CERT.RSA"
                }

                )
                {
                    using var data = zip.CreateEntry(file).Open();
                    data.Write(Encoding.UTF8.GetBytes("fixture"));
                }

                foreach (var file in new[]
                {
                    "libFixture.so",
                    "libopenal.so",
                    "libc++_shared.so"
                }

                )
                {
                    using var data = zip.CreateEntry("base/lib/arm64-v8a/" + file).Open();
                    data.Write(Elf(183, 16384));
                }
            }

            AndroidPackageService.ValidateBundle(bundle, entry);
            Check(File.Exists(bundle), "AAB base-module layout passes structural checks independently of APK layout");
            using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Update))
                zip.GetEntry("BundleConfig.pb")!.Delete();
            await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.ValidateBundle(bundle, entry)), "AAB without bundle configuration is rejected");
            entry.Platform = "android-x64";
            await Reject<InvalidDataException>(() => Task.Run(() => AndroidPackageService.ValidateApk(apk, entry)), "APK built for another ABI is rejected");
            entry.Platform = "android-arm64";
            using (var zip = ZipFile.Open(apk, ZipArchiveMode.Update))
                zip.GetEntry("assets/axslc/fixture_vs")!.Delete();
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
            Check(androidPlan.Arguments.Contains("android") && androidPlan.Arguments.Contains("arm64") && androidEnvironment["JAVA_HOME"] == Path.Combine(androidTools, "jdk") && androidEnvironment["ANDROID_HOME"] == Path.Combine(androidTools, "adt", "sdk"), "Android build maps to axmol -p android and takes its JDK/SDK from the engine tree");
        }

        Console.WriteLine($"{count} checks passed. Real platform builds, device deployment and clean-host acceptance require separate evidence.");
    }
}
