using System.Text.Json;
using AxmolHub.Core;
using static CheckSupport;

internal static class PrebuiltChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var prebuiltRoot = Path.Combine(root, "prebuilt-check-" + Guid.NewGuid().ToString("N"));
        var enginePath = Path.Combine(prebuiltRoot, "engine");
        var projectPath = Path.Combine(prebuiltRoot, "game");
        Directory.CreateDirectory(enginePath);
        Directory.CreateDirectory(projectPath);
        var fixtureEngine = new EngineEntry("2.11.5", enginePath, "local");
        var prebuiltState = new EnginePrebuiltState(prebuiltRoot);
        var windowsTarget = BuildTargets.Get("windows-x64");
        var fixtureProject = new ProjectEntry
        {
            Name = "game",
            Path = projectPath,
            Platform = "windows-x64",
            Configuration = "Debug",
            Version = "2.11.5"
        };
        CheckSettingsReaders(fixtureProject);

        // Build a "content-complete" engine build directory: CMakeCache.txt + lib/<configuration> + bin/<configuration>
        // + runtime/axslc + freetype headers.
        var buildDirectory = Path.Combine(enginePath, "build");
        /// Start clean: build everything from scratch each time, otherwise files left by the previous step make the
        /// next assertion falsely green (e.g. the "no shaders" step would be rescued by shader files an earlier step
        /// created).
        void CompleteBuild(string configuration)
        {
            if (Directory.Exists(buildDirectory))
                Directory.Delete(buildDirectory, recursive: true);
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

        void SaveRecord(string target, string configuration, string token) => prebuiltState.Save(fixtureEngine, new EngineBuildRecord { EnginePath = enginePath, EngineVersion = "2.11.5", Channel = "local", Target = target, Platform = "win32", Architecture = "x64", Configuration = configuration, BuildDirectory = "build", EngineToken = token, BuiltAt = DateTimeOffset.Now, });
        var token = ProjectService.EngineInstallationToken(fixtureEngine);
        // 1) Never built yet — the most common initial state.
        Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Debug", prebuiltState).Status == PrebuiltStatus.NotBuilt, "Without a build record the prebuilt libraries are reported as not built");
        // 2) Content complete + record matches → Ready, and the relative path is a clean path under the engine root
        //    (forward slashes).
        CompleteBuild("Debug");
        SaveRecord("windows-x64", "Debug", token);
        var ready = EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Debug", prebuiltState);
        Check(ready.Usable && ready.RelativeDirectory == "build" && !ready.RelativeDirectory!.Contains('\\'), "A complete engine build is reported ready with an engine-root-relative path (build)");
        // 3) Only Debug libraries but Release was asked for: report **which configurations actually exist**, instead of
        //    just saying "missing".
        var missingConfiguration = EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState);
        Check(missingConfiguration.Status == PrebuiltStatus.ConfigurationMissing && missingConfiguration.Detail.Contains("Debug"), "A missing configuration reports the configurations the engine build actually has");
        // 4) Incomplete content (no prebuilt shaders) → not ready.
        CompleteBuild("Release");
        File.Delete(Path.Combine(buildDirectory, "runtime", "axslc", "positionTextureColor_vs"));
        Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState).Status == PrebuiltStatus.MissingContents, "An engine build without runtime/axslc is not accepted as prebuilt");
        // 5) The engine was reinstalled/repaired → the record is void, old artifacts must not be taken on trust.
        CompleteBuild("Release");
        SaveRecord("windows-x64", "Release", "token-from-an-older-installation");
        Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState).Status == PrebuiltStatus.EngineChanged, "A record from an older engine installation is refused");
        // 6) The target recorded differs from the one requested → refuse (the directory name alone says nothing about
        //    the platform; only the record is trusted).
        SaveRecord("windows-arm64", "Release", token);
        Check(EnginePrebuilt.Inspect(fixtureEngine, windowsTarget, "Release", prebuiltState).Status == PrebuiltStatus.TargetMismatch, "A record built for another target is refused");
        // 7) Platform gate: prebuilt libraries only hold for Windows targets (the engine itself only accepts
        //    WIN32/LINUX).
        SaveRecord("windows-x64", "Release", token);
        Check(!EnginePrebuilt.Supported(BuildTargets.Get("android-arm64")) && EnginePrebuilt.Inspect(fixtureEngine, BuildTargets.Get("android-arm64"), "Release", prebuiltState).Status == PrebuiltStatus.PlatformUnsupported, "Non-Windows targets are refused before reading the disk");
        // 8) Per-project option storage (a separate JSON inside the project directory; .axmol-hub.json is untouched).
        new PrebuiltSettings
        {
            Enabled = true
        }.Save(fixtureProject);
        Check(PrebuiltSettings.Load(fixtureProject)?.Enabled == true && File.Exists(Path.Combine(projectPath, ".axmol-hub.prebuilt.json")) && !File.Exists(Path.Combine(projectPath, ".axmol-hub.json")), "Prebuilt settings persist in the project directory without touching the project metadata");
        // 9) Switch on and ready → what is handed to CMake is the relative path under the engine root; otherwise
        //    **fail explicitly** rather than silently falling back to a source build.
        //    The fixture project is Debug, so here the engine's own copy must also be back on Debug to count as ready.
        CompleteBuild("Debug");
        SaveRecord("windows-x64", "Debug", token);
        var options = ProjectBuildOptions.Invocation(fixtureProject, fixtureEngine, configureOnly: false, prebuiltState);
        var xc = Array.IndexOf(options.Arguments, "-xc");
        Check(xc >= 0 && options.Arguments[xc + 1] == "-DAX_PREBUILT_DIR=build", "An enabled, ready prebuilt build contributes -DAX_PREBUILT_DIR=build");
        prebuiltState.Clear(fixtureEngine);
        await Reject<PrebuiltUnavailableException>(() => Task.Run(() => ProjectBuildOptions.Invocation(fixtureProject, fixtureEngine, configureOnly: false, prebuiltState)), "An enabled but unavailable prebuilt build fails the build instead of silently compiling from source");
        Console.WriteLine($"{count} prebuilt checks passed.");
        return;
    }
    // The shared reader must accept old files without hiding damaged signing configuration.
    private static void CheckSettingsReaders(ProjectEntry project)
    {
        var cases = new (string Path, string Json, Func<bool> Matches, Func<object?> Read)[]
        {
            (PrebuiltSettings.PathFor(project), "{\"Enabled\":true}",
                () => PrebuiltSettings.Load(project)?.Enabled == true, () => PrebuiltSettings.Load(project)),
            (AndroidReleaseSettings.PathFor(project), "{\"ApplicationId\":\"org.example.fixture\",\"VersionCode\":7}",
                () => AndroidReleaseSettings.Load(project) is { ApplicationId: "org.example.fixture", VersionCode: 7 },
                () => AndroidReleaseSettings.Load(project)),
        };
        try
        {
            var observations = new List<string>();
            var compatible = true;
            foreach (var sample in cases)
            {
                File.WriteAllText(sample.Path, sample.Json);
                var oldFile = sample.Matches();
                File.WriteAllText(sample.Path, sample.Json.Replace("Enabled", "enabled")
                    .Replace("ApplicationId", "applicationId").Replace("VersionCode", "versionCode"));
                var editedFile = sample.Matches();
                compatible &= oldFile && editedFile;
                observations.Add(Path.GetFileName(sample.Path) + ":" + oldFile + "/" + editedFile);
            }
            Check(compatible,
                "Project settings read legacy and hand-edited property casing: " + string.Join(", ", observations));

            var rejected = 0;
            foreach (var sample in cases)
            {
                File.WriteAllText(sample.Path, "{");
                try { sample.Read(); }
                catch (JsonException) { rejected++; }
            }
            Check(rejected == cases.Length, "Corrupt project settings still surface JSON errors instead of silently resetting configuration");

            foreach (var sample in cases) File.Delete(sample.Path);
            Check(cases.All(sample => sample.Read() is null), "Missing project settings retain the unconfigured state");
        }
        finally
        {
            foreach (var sample in cases) File.Delete(sample.Path);
        }
    }

}
