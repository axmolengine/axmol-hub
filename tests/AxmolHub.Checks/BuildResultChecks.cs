using System.IO.Compression;
using System.Text.Json;
using AxmolHub.Core;
using static CheckSupport;

internal static class BuildResultChecks
{
    internal static async Task RunAsync(string root)
    {
        var fixture = Path.Combine(root, "build-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            DefaultChecks.RunBuildLayoutChecks(fixture);
            // An isolated command-line engine exercises the real invocation and receipt path without a toolchain.
            var engineRoot = Path.Combine(fixture, "engine");
            foreach (var directory in new[] { "core", "tools/cmdline", "templates/cpp", "1k" }) Directory.CreateDirectory(Path.Combine(engineRoot, directory));
            File.WriteAllText(Path.Combine(engineRoot, "core/axmolver.h.in"), "#define AX_VERSION_MAJOR 2\n#define AX_VERSION_MINOR 11\n#define AX_VERSION_PATCH 5\n");
            File.WriteAllText(Path.Combine(engineRoot, "templates/cpp/axproj-template.json"), "{}");
            File.WriteAllText(Path.Combine(engineRoot, "1k/1kiss.ps1"), "");
            var script = Path.Combine(engineRoot, "tools/cmdline/axmol.ps1");
            File.WriteAllText(script, "exit 0\n");
            var engine = StateStore.ValidateEngine(engineRoot, "fixture");
            var project = new ProjectEntry { Name = "HelloAxmol", Path = Path.Combine(fixture, "project"), Version = engine.Version, Channel = engine.Channel, Platform = "android-arm64", Configuration = "Debug" };
            Directory.CreateDirectory(project.Path);
            File.WriteAllText(Path.Combine(project.Path, "CMakeLists.txt"), "# fixture\n");
            File.WriteAllText(Path.Combine(project.Path, ".axproj"), "engine_version=2.11.5\nproject_type=cpp\n");
            StateStore.LockProject(project);
            var apkDirectory = Path.GetFullPath(Path.Combine(project.Path, "proj.android/app/build/outputs/apk/debug"));
            Directory.CreateDirectory(apkDirectory);
            var apk = Path.Combine(apkDirectory, "app-debug.apk");
            using (var archive = ZipFile.Open(apk, ZipArchiveMode.Create)) archive.CreateEntry("lib/arm64-v8a/libHelloAxmol.so");
            var protocol = ProjectBuildResult.Marker + JsonSerializer.Serialize(new
            {
                version = 1, buildDir = (string?)null, config = "Debug", targetOS = "android", targetCPU = "arm64",
                artifacts = new[] { new { target = project.Name, path = apk } }
            });
            void Emit(string line) => File.WriteAllText(script, "[Console]::WriteLine('" + line.Replace("'", "''") + "')\nexit 0\n");
            var runner = new ProcessRunner(_ => { });
            var service = new ProjectService(runner, EngineCli(runner), new EnginePrebuiltState(fixture));
            var receipt = Path.Combine(project.Path, ProjectService.MarkerDirectory, "build.json");
            Emit(protocol);
            await service.BuildAsync(project, engine);
            var reported = ProjectService.ReadBuildResult(project);
            Check(reported.BuildDirectory is null && reported.Source == BuildResultSource.EngineProtocol && service.FindExecutable(project) == apk,
                "Android protocol result with null buildDir survives the real build/receipt/artifact flow on an old version number");
            foreach (var invalid in new[] { "{bad-json", protocol[ProjectBuildResult.Marker.Length..].Replace("\"version\":1", "\"version\":2"), protocol[ProjectBuildResult.Marker.Length..].Replace("\"arm64\"", "\"x64\"") })
            {
                Emit(ProjectBuildResult.Marker + invalid);
                await Reject<InvalidDataException>(() => service.BuildAsync(project, engine), "Invalid engine result cannot fall back to an existing legacy APK");
                Check(!File.Exists(receipt), "Rejected build result leaves no stale success receipt");
            }
            Emit(protocol);
            await service.BuildAsync(project, engine);
            File.WriteAllText(script, "exit 7\n");
            await Reject<InvalidOperationException>(() => service.BuildAsync(project, engine), "Nonzero engine exit is not a successful build");
            Check(!File.Exists(receipt), "Failed process invalidates the preceding artifact receipt");
            Emit(protocol);
            await service.BuildAsync(project, engine);
            await service.BuildAsync(project, engine, configureOnly: true);
            Check(!File.Exists(receipt), "Configure-only does not register runnable artifacts even if output contains a result");

            File.WriteAllText(script, "exit 0\n");
            var otherAbi = Path.Combine(apkDirectory, "app-x64-debug.apk");
            File.WriteAllText(otherAbi, "unselected fixture");
            var metadata = Path.Combine(apkDirectory, "output-metadata.json");
            StateStore.WriteJson(metadata, new { variantName = "debug", elements = new[] {
                new { outputFile = Path.GetFileName(apk), filters = new[] { new { filterType = "ABI", value = "arm64-v8a" } } },
                new { outputFile = Path.GetFileName(otherAbi), filters = new[] { new { filterType = "ABI", value = "x86_64" } } }
            } });
            await service.BuildAsync(project, engine);
            Check(ProjectService.ReadBuildResult(project).Source == BuildResultSource.LegacyDiscovery && service.FindExecutable(project) == apk,
                "Legacy Android build reads Gradle variant/ABI metadata without requiring a native build directory");
            var flavor = Path.Combine(project.Path, "proj.android/app/build/outputs/apk/demo/debug");
            Directory.CreateDirectory(flavor);
            File.Copy(apk, Path.Combine(flavor, "demo.apk"));
            StateStore.WriteJson(Path.Combine(flavor, "output-metadata.json"), new { variantName = "demoDebug", elements = new[] { new { outputFile = "demo.apk", filters = Array.Empty<object>() } } });
            // Both outputs target the same ABI, so neither timestamps nor arbitrary flavor ordering can decide.
            StateStore.WriteJson(metadata, new { variantName = "debug", elements = new[] { new { outputFile = Path.GetFileName(apk), filters = Array.Empty<object>() } } });
            await Reject<InvalidDataException>(() => service.BuildAsync(project, engine), "Ambiguous Gradle variants never pick the newest APK");
            Directory.Delete(Path.GetDirectoryName(flavor)!, true);
            File.Delete(metadata);
            File.Delete(otherAbi);
            await service.BuildAsync(project, engine);
            Check(service.FindExecutable(project) == apk, "Legacy standard-output fallback checks APK ABI when Gradle metadata is absent");
            File.Delete(apk);
            await Reject<InvalidDataException>(() => Task.Run(() => service.FindExecutable(project)), "Recorded artifact lookup never rescans when its file disappears");
        }
        finally { Directory.Delete(fixture, true); }
    }
}
