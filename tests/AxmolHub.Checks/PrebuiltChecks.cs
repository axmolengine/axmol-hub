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
        var fixtureEngine = CommandEngine(enginePath, "local", "exit 7\n");
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
        var armTarget = BuildTargets.Get("windows-arm64");
        var buildDirectory = BuildCleanup.ForEngine(fixtureEngine, windowsTarget).Directories.Single();
        var armDirectory = BuildCleanup.ForEngine(fixtureEngine, armTarget).Directories.Single();
        var directoryName = Path.GetFileName(buildDirectory);
        string Cache(string directory) => Path.Combine(directory, "CMakeCache.txt");
        var cacheContent = $"CMAKE_HOME_DIRECTORY:INTERNAL={enginePath.Replace('\\', '/')}\n";
        void CompleteBuild(string directory, string configuration)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Cache(directory), cacheContent);
            Directory.CreateDirectory(Path.Combine(directory, "bin", configuration));
            Directory.CreateDirectory(Path.Combine(directory, "engine", "3rdparty", "freetype", "include"));
            var shaders = Path.Combine(directory, "runtime", "axslc");
            Directory.CreateDirectory(shaders);
            File.WriteAllText(Path.Combine(shaders, "positionTextureColor_vs"), "fixture");
            var libraries = Path.Combine(directory, "lib", configuration);
            Directory.CreateDirectory(libraries);
            File.WriteAllText(Path.Combine(libraries, "axmol.lib"), "fixture");
        }

        void SaveRecord(BuildTarget target, string configuration, string token) => prebuiltState.Save(fixtureEngine, new EngineBuildRecord
        {
            Target = target.Id, Configuration = configuration, EngineToken = token,
            BuildDirectory = Path.GetFileName(target == armTarget ? armDirectory : buildDirectory),
        });
        PrebuiltAvailability Inspect(BuildTarget target, string configuration) => EnginePrebuilt.Inspect(fixtureEngine, target, configuration, prebuiltState);
        var token = ProjectService.EngineInstallationToken(fixtureEngine);
        // 1) Never built yet — the most common initial state.
        Check(Inspect(windowsTarget, "Debug").Status == PrebuiltStatus.NotBuilt, "No record means no trusted prebuilt output");
        CompleteBuild(buildDirectory, "Debug");
        SaveRecord(windowsTarget, "Debug", token);
        var recordFile = Directory.GetFiles(Path.Combine(prebuiltRoot, "prebuilt")).Single();
        File.Move(recordFile, recordFile.Replace(".windows-x64.Debug.json", ".json", StringComparison.Ordinal));
        Check(Inspect(windowsTarget, "Debug").RelativeDirectory == directoryName
            && Inspect(windowsTarget, "Release").Status == PrebuiltStatus.ConfigurationMissing
            && Inspect(armTarget, "Debug").Status == PrebuiltStatus.NotBuilt,
            "A legacy record still works, but only for its own architecture and configuration");
        File.Delete(Path.Combine(buildDirectory, "runtime/axslc/positionTextureColor_vs"));
        Check(Inspect(windowsTarget, "Debug").Status == PrebuiltStatus.MissingContents, "Missing shaders make the engine library unusable");
        CompleteBuild(buildDirectory, "Release");
        SaveRecord(windowsTarget, "Release", "older-installation");
        Check(Inspect(windowsTarget, "Release").Status == PrebuiltStatus.EngineChanged, "A record from an older installation is refused");
        SaveRecord(windowsTarget, "Release", token);
        CompleteBuild(armDirectory, "Release");
        SaveRecord(armTarget, "Release", token);
        Check(Inspect(windowsTarget, "Debug").Usable && Inspect(windowsTarget, "Release").Usable
            && Inspect(armTarget, "Release").AbsoluteDirectory == armDirectory && !Inspect(armTarget, "Debug").Usable,
            "Build records are independent by configuration and architecture");

        var runner = new ProcessRunner(_ => { });
        var service = new ProjectService(runner, EngineCli(runner), prebuiltState);
        var engineBuild = new EngineBuildService(runner, EngineCli(runner), prebuiltState);
        var project = fixtureProject;
        var androidTarget = BuildTargets.Get("android-arm64");
        string[] Arguments() => ProjectBuildOptions.Invocation(project, fixtureEngine, false, prebuiltState).Arguments;
        Check(service.SetPrebuiltPreference(project, fixtureEngine, true) && !File.Exists(StateStore.MetadataPath(project.Path))
            && Arguments().Contains("-DAX_PREBUILT_DIR=" + directoryName), "Enabling ready libraries uses an engine-relative directory and separate project settings");

        File.WriteAllText(Path.Combine(project.Path, "CMakeLists.txt"), "# fixture only\n");
        BuildTargets.Select(project, androidTarget.Id);
        Check(!Arguments().Any(option => option.Contains("AX_PREBUILT_DIR", StringComparison.Ordinal))
            && !service.SetPrebuiltPreference(project, fixtureEngine, false) && PrebuiltSettings.Load(project)?.Enabled == true,
            "Android does not apply or overwrite the Windows preference");
        BuildTargets.Select(project, windowsTarget.Id);
        Check(Arguments().Contains("-DAX_PREBUILT_DIR=" + directoryName), "Switching back to Windows applies the saved preference");

        var receipt = Path.Combine(project.Path, ProjectService.MarkerDirectory, "build.json");
        StateStore.WriteJson(receipt, new { engine = token, platform = project.Platform, configuration = project.Configuration, usesPrebuilt = true });
        project.BuildStatus = "Succeeded";
        var projectOutput = BuildCleanup.ForProject(project, windowsTarget).Directories.Single();
        Directory.CreateDirectory(projectOutput);
        File.WriteAllText(Cache(projectOutput), "AX_PREBUILT_DIR:UNINITIALIZED=" + directoryName + "\n");
        Check(!service.SetPrebuiltPreference(project, fixtureEngine, true) && File.Exists(receipt) && project.BuildStatus == "Succeeded",
            "Confirming unchanged link preferences preserves the build receipt and status");
        Check(service.SetPrebuiltPreference(project, fixtureEngine, false) && !File.Exists(receipt) && project.BuildStatus == "Not built"
            && Arguments().Contains("-UAX_PREBUILT_DIR") && Arguments().Contains("-forceConfig"),
            "Disabling prebuilt linking invalidates the receipt and removes the cache entry on configure");
        Check(service.SetPrebuiltPreference(project, fixtureEngine, true), "Available libraries can be re-enabled after disabling");

        var cleanup = BuildCleanup.ForEngine(fixtureEngine, windowsTarget);
        var unaffected = Directory.CreateDirectory(Path.Combine(enginePath, "install")).FullName;
        var invoked = 0;
        Task CleanEngine() => engineBuild.BuildAsync(fixtureEngine, windowsTarget, "Debug", cleanup: cleanup, onCommand: (_, _) => invoked++);
        var unsafeSource = Path.Combine(buildDirectory, "CMakeLists.txt");
        File.WriteAllText(unsafeSource, "# unsafe source tree marker\n");
        await Reject<InvalidDataException>(CleanEngine, "An unsafe cleanup directory is rejected before invoking the engine");
        Check(invoked == 0 && Directory.Exists(buildDirectory) && Inspect(windowsTarget, "Release").Usable,
            "A refused cleanup neither invokes a build nor invalidates untouched libraries");
        File.Delete(unsafeSource);
        if (OperatingSystem.IsWindows())
        {
            var shader = Path.Combine(buildDirectory, "runtime", "axslc", "positionTextureColor_vs");
            File.SetAttributes(shader, FileAttributes.ReadOnly);
            var reportedPath = false;
            using (File.OpenRead(shader))
            {
                try { await CleanEngine(); }
                catch (IOException ex) when (ex.Message.Contains(shader, StringComparison.OrdinalIgnoreCase)) { reportedPath = true; }
            }
            Check(reportedPath && invoked == 0 && Directory.Exists(buildDirectory),
                "A locked shader reports its path and skips compilation so cleanup can be retried");
            // Exercise the read-only path after releasing the handle as the failed attempt may have cleared it.
            File.SetAttributes(shader, FileAttributes.ReadOnly);
        }
        await Reject<InvalidOperationException>(CleanEngine, "The isolated engine fails after the confirmed cleanup");
        Check(invoked == 1 && !Directory.Exists(buildDirectory) && Directory.Exists(unaffected)
            && !Inspect(windowsTarget, "Debug").Usable && !Inspect(windowsTarget, "Release").Usable && Inspect(armTarget, "Release").Usable,
            "Cleaning a shared build directory clears both configurations and preserves other architecture and install output");
        await Reject<PrebuiltUnavailableException>(() => Task.Run(Arguments), "An enabled library that disappeared never silently falls back to sources");
        await CheckProjectBuildCleanupAsync(project, windowsTarget, projectOutput, directoryName);
        await CheckAndroidCleanupAsync(project, androidTarget, fixtureEngine, service, receipt);
        Console.WriteLine($"{count} prebuilt checks passed.");
        return;
    }

    private static async Task CheckProjectBuildCleanupAsync(ProjectEntry project, BuildTarget target, string projectOutput, string directoryName)
    {
        // A relocated project cache or a build directory without CMakeCache.txt should clean directly without blocking.
        Directory.CreateDirectory(projectOutput);
        File.WriteAllText(Path.Combine(projectOutput, "CMakeCache.txt"),
            $"CMAKE_HOME_DIRECTORY:INTERNAL=d:/moved/Causals\nCMAKE_CACHEFILE_DIR:INTERNAL=d:/moved/Causals/{directoryName}\n");
        var relocated = BuildCleanup.ForProject(project, target);
        await relocated.ExecuteAsync(() => BuildCleanup.ForProject(project, target), () => { }, CancellationToken.None, _ => { });
        Check(!Directory.Exists(projectOutput), "Cleaning a relocated build directory removes it directly");

        Directory.CreateDirectory(projectOutput);
        File.WriteAllText(Path.Combine(projectOutput, "stale.txt"), "leftover");
        var signingFile = Path.Combine(projectOutput, "release.keystore");
        File.WriteAllText(signingFile, "fixture");
        var cleanup = BuildCleanup.ForProject(project, target);
        await Reject<InvalidDataException>(
            () => cleanup.ExecuteAsync(() => BuildCleanup.ForProject(project, target), () => { }, CancellationToken.None, _ => { }),
            "Signing files inside a build directory remain blocked");
        File.Delete(signingFile);

        await cleanup.ExecuteAsync(() => BuildCleanup.ForProject(project, target), () => { }, CancellationToken.None, _ => { });
        Check(!Directory.Exists(projectOutput), "A non-empty build directory without CMakeCache.txt is cleaned directly");
    }
    private static async Task CheckAndroidCleanupAsync(ProjectEntry project, BuildTarget target, EngineEntry engine, ProjectService service, string receipt)
    {
        BuildTargets.Select(project, target.Id);
        var gradle = Path.Combine(project.Path, "proj.android");
        var app = Path.Combine(gradle, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(gradle, "gradlew"), "");
        File.WriteAllText(Path.Combine(gradle, "settings.gradle"), "project(':app').projectDir = new File(settingsDir, 'app')\n");
        File.WriteAllText(Path.Combine(gradle, "build.gradle"), "tasks.register('clean', Delete) { delete rootProject.layout.buildDirectory }\n");
        File.WriteAllText(Path.Combine(app, "build.gradle"), "android { externalNativeBuild { cmake { path '../../CMakeLists.txt' } } }\n");
        var outputs = new[] { Path.Combine(app, "build"), Path.Combine(app, ".cxx"), Path.Combine(gradle, "build") };
        foreach (var directory in outputs) Directory.CreateDirectory(directory);
        var key = Path.Combine(gradle, "release.jks");
        File.WriteAllText(key, "fixture only");
        File.WriteAllText(receipt, "stale receipt");
        var cleanup = BuildCleanup.ForProject(project, target);
        var invoked = 0;
        await Reject<InvalidOperationException>(() => service.BuildAsync(project, engine, configureOnly: true,
            cleanup: cleanup, onCommand: (_, _) => invoked++), "An isolated Android configure failure is still reported after cleanup");
        Check(invoked == 1 && cleanup.Directories.SequenceEqual(outputs) && outputs.All(directory => !Directory.Exists(directory))
            && !File.Exists(receipt) && File.Exists(key) && File.Exists(Path.Combine(app, "build.gradle"))
            && PrebuiltSettings.Load(project)?.Enabled == true,
            "Android cleanup removes native and Gradle output but preserves source, signing file, and Windows preference");
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
