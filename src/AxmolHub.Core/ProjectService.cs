using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed class ProjectDestinationExistsException(string destination)
    : IOException($"Project destination already exists: {destination}")
{
    public string Destination { get; } = destination;
}

/// <summary>
/// Project lifecycle.
///
/// **Build/run has been delegated to the engine's own command line** (<c>axmol build|run|deploy</c>):
/// the Hub no longer assembles CMake arguments, pins tool paths, or hand-builds <c>INCLUDE</c>/<c>LIB</c>.
/// The build directory is also decided by the engine (see <see cref="EngineBuildLayout"/>); the Hub only
/// discovers it.
///
/// The value the Hub retains: filling in the runtime app-local runtime library (VC redist). Run directly
/// launches the exe in the engine's artifact directory.
/// </summary>
public sealed class ProjectService(ProcessRunner runner, EngineCommandLine commandLine, EnginePrebuiltState prebuiltState)
{
    /// <summary>The marker directory the Hub writes into the project directory (build receipt etc.). Not part of the official project structure.</summary>
    public const string MarkerDirectory = ".hub";
    private const string ReconfigureMarker = "engine-upgrade.pending";
    internal static bool NeedsReconfigure(ProjectEntry project)
        => File.Exists(Path.Combine(project.Path, MarkerDirectory, ReconfigureMarker));

    public static void ValidateProjectType(string projectType)
    {
        if (projectType is not ("cpp" or "lua")) throw new ArgumentException("Scripting must be cpp or lua.");
    }

    public async Task<ProjectEntry> CreateAsync(string name, string parent, EngineEntry engine, CancellationToken cancellation = default,
        string projectType = "cpp", string? cameraMode = null, bool portrait = false, bool isolated = false,
        bool repair = false, bool forceOverwrite = false, Action<string>? onLine = null, Action<string, string>? onCommand = null)
    {
        ValidateProjectType(projectType);
        if (cameraMode is not null && cameraMode is not ("classic" or "2d" or "3d"))
            throw new ArgumentException("Camera mode must be classic, 2d or 3d.", nameof(cameraMode));
        if (projectType != "cpp" || EngineReleases.CompareVersions(engine.Version, "3.0.0") < 0)
            cameraMode = null;
        if (!File.Exists(Path.Combine(engine.Path, "templates", projectType, "axproj-template.json")))
            throw new InvalidDataException("Selected engine does not contain the requested project template.");
        // The name enters the official template substitution logic, so restrict it to a safe C++ / CMake identifier.
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]{0,63}$")) throw new ArgumentException("Project name must start with a letter and contain only letters, digits or underscores (max 64).");
        if (forceOverwrite && !repair) throw new ArgumentException("Overwrite requires repair.", nameof(forceOverwrite));
        parent = Path.GetFullPath(parent);
        var path = Path.Combine(parent, name);
        var packageName = $"dev.axmol.{name.ToLowerInvariant()}";
        if (repair)
        {
            // Never let the engine's repair mode turn an arbitrary folder into a project or silently
            // rebind a project to another engine/language. The engine may still modify project files.
            if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, ".axproj")))
                throw new InvalidDataException("Repair requires an existing Axmol project with .axproj.");
            var existing = StateStore.ReadProject(path);
            if (existing.Version != engine.Version || existing.ProjectType != projectType
                || (File.Exists(StateStore.MetadataPath(path)) && existing.Channel != engine.Channel))
                throw new InvalidDataException("Repair requires the same engine version, channel and project language.");
            var profile = File.ReadAllText(Path.Combine(path, ".axproj"));
            var profileVersion = Regex.Match(profile, @"(?m)^engine_version\s*=\s*(\d+\.\d+\.\d+)(?:-[0-9a-fA-F]{7,})?\s*$");
            if (!profileVersion.Success || profileVersion.Groups[1].Value != engine.Version)
                throw new InvalidDataException("Repair requires .axproj to match the selected engine version.");
            packageName = Regex.Match(profile, @"(?m)^package_name\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
            if (packageName.Length == 0)
                throw new InvalidDataException("Repair requires an Axmol project with a package name in .axproj.");
        }
        else if (Directory.Exists(path) || File.Exists(path)) throw new ProjectDestinationExistsException(path);

        Directory.CreateDirectory(parent);
        var arguments = new List<string> { "-p", packageName, "-d", parent, "-l", projectType };
        if (cameraMode is not null) arguments.AddRange(["-m", cameraMode]);
        arguments.Add(name);
        if (portrait) arguments.Add("--portrait");
        if (isolated) arguments.Add("-isolated");
        if (repair) arguments.Add("-repair");
        if (forceOverwrite) arguments.Add("-forceOverwrite");
        await commandLine.RunAsync(engine, new AxmolInvocation("new", arguments.ToArray()), parent, cancellation,
            onLine: onLine, onCommand: onCommand);
        cancellation.ThrowIfCancellationRequested();
        var project = StateStore.ReadProject(path);
        if (project.Version != engine.Version) throw new InvalidDataException("Created project engine version does not match selected engine.");
        if (project.ProjectType != projectType) throw new InvalidDataException("Created project scripting type does not match selection.");
        project.Channel = engine.Channel;
        if (!repair)
        {
            project.Platform = BuildTargets.DefaultForHost(engine.Version).Id;
            StateStore.LockProject(project);
        }
        else if (!File.Exists(StateStore.MetadataPath(path)))
        {
            project.Platform = BuildTargets.DefaultForHost(engine.Version).Id;
            StateStore.LockProject(project);
        }
        return project;
    }

    /// <summary>
    /// Updates the engine binding in the Hub metadata and the engine project profile. The returned action
    /// restores every touched file and the in-memory entry, so a failed Hub-state save cannot leave the
    /// project half-upgraded.
    /// </summary>
    public static Action UpdateEngineBinding(ProjectEntry project, EngineEntry engine)
    {
        var validatedEngine = StateStore.ValidateEngine(engine.Path, engine.Channel);
        if (validatedEngine.Version != engine.Version || validatedEngine.Channel != engine.Channel)
            throw new InvalidDataException("Selected engine metadata does not match its installation.");
        if (EngineReleases.CompareVersions(engine.Version, project.Version) <= 0)
            throw new InvalidOperationException("UpgradeEngineNoLongerNewer");

        var profile = Path.Combine(project.Path, ".axproj");
        var metadata = StateStore.MetadataPath(project.Path);
        var receipt = Path.Combine(project.Path, MarkerDirectory, "build.json");
        var reconfigure = Path.Combine(project.Path, MarkerDirectory, ReconfigureMarker);
        var oldProfile = Snapshot(profile);
        var oldMetadata = Snapshot(metadata);
        var oldReceipt = Snapshot(receipt);
        var oldReconfigure = Snapshot(reconfigure);
        var oldVersion = project.Version;
        var oldChannel = project.Channel;
        var restored = false;

        void Restore()
        {
            if (restored) return;
            restored = true;
            RestoreFile(profile, oldProfile);
            RestoreFile(metadata, oldMetadata);
            RestoreFile(receipt, oldReceipt);
            RestoreFile(reconfigure, oldReconfigure);
            project.Version = oldVersion;
            project.Channel = oldChannel;
        }

        try
        {
            if (File.Exists(profile))
            {
                var text = File.ReadAllText(profile);
                var versionLine = new Regex(@"(?m)^engine_version\s*=\s*[^\r\n]+");
                if (versionLine.IsMatch(text))
                {
                    var updated = versionLine.Replace(text, "engine_version=" + engine.Version, 1);
                    if (updated != text) WriteAtomic(profile, TextEol.ToLf(updated));
                }
            }

            project.Version = engine.Version;
            project.Channel = engine.Channel;
            StateStore.LockProject(project);
            if (File.Exists(receipt)) File.Delete(receipt);
            Directory.CreateDirectory(Path.GetDirectoryName(reconfigure)!);
            WriteAtomic(reconfigure, "Reconfigure with the upgraded engine before the next build.\n");
        }
        catch
        {
            Restore();
            throw;
        }

        return Restore;
    }

    private static byte[]? Snapshot(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static void RestoreFile(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        WriteAtomic(path, bytes);
    }

    private static void WriteAtomic(string path, string text)
        => WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(text));

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ConfigureAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation = default)
        => BuildAsync(project, engine, true, cancellation);

    public async Task BuildAsync(ProjectEntry project, EngineEntry engine, bool configureOnly = false, CancellationToken cancellation = default,
        AndroidSigningPasswords? androidPasswords = null, Action<string>? onLine = null, Action<string, string>? onCommand = null)
    {
        BuildConfigurations.ValidateTarget(project);
        var target = BuildTargets.Get(project.Platform);
        if (!target.CanBuildOn(BuildTargets.Host))
            throw new PlatformNotSupportedException($"{target.Name} requires {string.Join(" / ", target.Hosts)}. Current host: {BuildTargets.Host}.");
        if (target.RequiresMajor(BuildTargets.MajorVersion(project.Version)))
            throw new PlatformNotSupportedException($"{target.Name} requires Axmol {target.MinimumMajorVersion}+ (project is on Axmol {project.Version}).");
        if (!target.CanCrossBuild(BuildTargets.HostArch))
            throw new PlatformNotSupportedException($"{target.Name} cannot be cross-compiled on {BuildTargets.Host}/{BuildTargets.HostArch} (its target arch is {target.Architecture}).");
        if (project.Version != engine.Version || project.Channel != engine.Channel) throw new InvalidOperationException("Required Axmol version/channel is not installed.");
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Version != project.Version || locked.Channel != project.Channel || locked.Platform != project.Platform || locked.Configuration != project.Configuration
            || locked.AdvancedBuildEnabled != project.AdvancedBuildEnabled || locked.AdditionalBuildParameters != project.AdditionalBuildParameters)
            throw new InvalidOperationException("Project metadata changed. Reopen the project before building.");
        StateStore.ValidateEngine(engine.Path, engine.Channel);

        var invocation = ProjectBuildOptions.Invocation(project, engine, configureOnly, prebuiltState);
        var reconfigure = NeedsReconfigure(project);
        await commandLine.RunAsync(engine, invocation, project.Path, cancellation, onLine: onLine, onCommand: onCommand);
        WriteBuildReceipt(project, engine);
        if (reconfigure) File.Delete(Path.Combine(project.Path, MarkerDirectory, ReconfigureMarker));
    }

    /// <summary>Build receipt: records which engine installation and target/configuration this build corresponds to, so Run can judge whether the artifacts are still valid.</summary>
    private void WriteBuildReceipt(ProjectEntry project, EngineEntry engine)
    {
        var directory = Path.Combine(project.Path, MarkerDirectory);
        Directory.CreateDirectory(directory);
        StateStore.WriteJson(Path.Combine(directory, "build.json"), new
        {
            engine = EngineInstallationToken(engine), platform = project.Platform, configuration = project.Configuration,
            buildDirectory = EngineBuildLayout.FindBuildDirectory(project) ?? "",
            additionalBuildParameters = project.AdvancedBuildEnabled ? project.AdditionalBuildParameters : "",
        });
    }

    public string FindExecutable(ProjectEntry project)
    {
        if (BuildTargets.Get(project.Platform).Family != "windows") return PlatformBuildService.FindArtifact(project);
        return EngineBuildLayout.FindArtifact(BuildDirectory(project), project.Name, project.Configuration, "windows")
            ?? throw new FileNotFoundException("Build " + project.Configuration + " successfully before Run.");
    }

    /// <summary>The engine decides the build directory; this just locates it. Not found means it hasn't been built yet.</summary>
    public static string BuildDirectory(ProjectEntry project)
        => EngineBuildLayout.FindBuildDirectory(project)
           ?? throw new DirectoryNotFoundException($"No build directory was produced under {project.Path}. Build this target first.");

    /// <summary>
    /// The waiting contract: start the target and answer with a process's exit code. <c>axmolhub-cli run</c>
    /// uses it because a script piping the verb wants the number the game exited with, and every non-Windows
    /// target uses it because there the command finishing <b>is</b> the answer — a deploy that returned 0 put
    /// the app on the phone. The shell's 运行 is not in this set: it launches a Windows game with
    /// <see cref="LaunchAsync"/> and lets go.
    /// </summary>
    public async Task<ProcessResult> RunAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation = default, string? androidDevice = null)
    {
        if (BuildTargets.Get(project.Platform).Family != "windows")
        {
            return await new PlatformBuildService(runner).RunAsync(project, engine, commandLine, cancellation, deviceSerial: androidDevice);
        }

        using var process = await StartWindowsAsync(project, engine, cancellation);
        await process.WaitForExitAsync(cancellation);
        runner.Write($"Exit code: {process.ExitCode}");
        return new ProcessResult(process.ExitCode, "", "");
    }

    /// <summary>
    /// Validate the artifact, prepare the runtime and hand the game over. Returns the pid of the process Hub
    /// started and does not wait for it: pressing 运行 is not the start of a debugging session, and once the
    /// window is on screen Hub has no answer left to give back. The pid serves the two questions that stay
    /// honest — did it die on contact, and is it still holding the output directory a rebuild wants.
    /// </summary>
    public async Task<int> LaunchAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation = default)
    {
        using var process = await StartWindowsAsync(project, engine, cancellation);
        return process.Id;
    }

    private async Task<System.Diagnostics.Process> StartWindowsAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation)
    {
        var executable = ResolveLaunchable(project, engine);
        var target = BuildTargets.Get(project.Platform);
        // The engine artifact directory (bin/&lt;App&gt;/&lt;Config&gt;/) is already fully runnable: exe, dll, axslc
        // shaders and Content all sit at the same level. The Hub no longer copies out a separate run
        // directory; it only fills in the app-local runtime library and then launches the exe in the
        // artifact directory directly.
        await PrepareRuntime(Path.GetDirectoryName(executable)!, project.Configuration, target.Architecture, cancellation);
        // Launch detached, as if double-clicked: the exe's own AllocConsole gives it a real console with the
        // log colors a direct launch has, and nothing is piped back — a game's output belongs to its own
        // console or its own log file. FileUtils treats the working directory as the resource root, so Content
        // is stated explicitly and the launch location does not affect resource resolution.
        return runner.StartDetached(executable, Path.Combine(Path.GetDirectoryName(executable)!, "Content"));
    }

    /// <summary>Every check a Windows run needs before anything is started, in one place so the launch and the
    /// rebuild ask the same question about the same artifact. Throws the engine's own English sentences, which
    /// <c>HubStrings</c> turns back into the user's language in the error dialog.</summary>
    private string ResolveLaunchable(ProjectEntry project, EngineEntry engine)
    {
        var target = BuildTargets.Get(project.Platform);
        // Windows can cross-compile arm64, but an arm64 native image can only be launched by an arm64 host.
        if (!target.CanRunLocally(BuildTargets.HostArch))
            throw new PlatformNotSupportedException($"{target.Name} was built for {target.Architecture}; a {target.Architecture} host is required to run it (current host: {BuildTargets.Host}/{BuildTargets.HostArch}).");
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Platform != project.Platform || locked.Configuration != project.Configuration || locked.Version != project.Version || locked.Channel != project.Channel
            || locked.AdvancedBuildEnabled != project.AdvancedBuildEnabled || locked.AdditionalBuildParameters != project.AdditionalBuildParameters)
            throw new InvalidOperationException("Project metadata changed. Reopen the project before Run.");
        if (project.Version != engine.Version || project.Channel != engine.Channel) throw new InvalidOperationException("Required Axmol version/channel is not installed.");
        ValidateBuildReceipt(project, engine);
        var build = BuildDirectory(project);
        return EngineBuildLayout.FindArtifact(build, project.Name, project.Configuration, "windows")
            ?? throw new FileNotFoundException("Build " + project.Configuration + " successfully before Run.");
    }

    /// <summary>When the build receipt is missing or doesn't match the current engine installation, the previous artifacts must not be used to enter Run.</summary>
    public static void ValidateBuildReceipt(ProjectEntry project, EngineEntry engine)
    {
        var receipt = Path.Combine(project.Path, MarkerDirectory, "build.json");
        if (!File.Exists(receipt)) throw new InvalidOperationException("Build this target successfully before Run.");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(receipt));
        var root = document.RootElement;
        if (root.GetProperty("engine").GetString() != EngineInstallationToken(engine))
            throw new InvalidOperationException("Engine installation changed. Build again before Run.");
        if (root.GetProperty("platform").GetString() != project.Platform || root.GetProperty("configuration").GetString() != project.Configuration)
            throw new InvalidOperationException("Project target changed. Build again before Run.");
        var parameters = root.TryGetProperty("additionalBuildParameters", out var saved) ? saved.GetString() ?? "" : "";
        if (parameters != (project.AdvancedBuildEnabled ? project.AdditionalBuildParameters : ""))
            throw new InvalidOperationException("Additional build parameters changed. Build again before Run.");
    }

    public static string EngineInstallationToken(EngineEntry engine)
    {
        var receipt = Path.Combine(engine.Path, ".hub-install.json");
        return File.Exists(receipt) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(receipt))) : engine.Path + ":" + engine.Version;
    }

    /// <summary>
    /// The app-local runtime library. The toolchain has been handed back to the engine (system VS), so the
    /// source changed from "Hub-managed MSVC" to the **system Visual Studio redist**; if not found, skip —
    /// programs built by the engine already depend on the system-installed VC runtime anyway, so this only
    /// adds an app-local copy for local Debug output.
    /// </summary>
    private async Task PrepareRuntime(string output, string configuration, string architecture, CancellationToken cancellation)
    {
        var redist = await VisualStudioRedistAsync(cancellation);
        if (redist is null)
        {
            runner.Write("App-local runtime: skipped (no Visual Studio redist found on this machine).");
            return;
        }

        // The CRT directory is chosen by target architecture (the windows target arch is x64/arm64, matching the redist subdirectory names).
        var version = Directory.EnumerateDirectories(redist).OrderDescending().FirstOrDefault(path =>
            File.Exists(Path.Combine(path, architecture + "/Microsoft.VC143.CRT/msvcp140.dll")));
        if (version is null)
        {
            runner.Write($"App-local runtime: skipped (Visual Studio redist has no {architecture} CRT).");
            return;
        }

        var sources = Directory.EnumerateFiles(Path.Combine(version, architecture + "/Microsoft.VC143.CRT"), "*.dll").ToList();
        if (configuration == "Debug")
        {
            var debug = Path.Combine(version, "debug_nonredist/" + architecture + "/Microsoft.VC143.DebugCRT");
            if (!Directory.Exists(debug)) runner.Write("App-local runtime: Debug CRT is not present; the published app uses the machine's runtime.");
            else sources.AddRange(Directory.EnumerateFiles(debug, "*.dll"));
        }

        var receipt = new List<object>();
        foreach (var source in sources)
        {
            var destination = Path.Combine(output, Path.GetFileName(source));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
            if (!File.Exists(destination) || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(destination))) != hash)
            {
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(source, temporary); File.Move(temporary, destination, overwrite: true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }

            receipt.Add(new { file = Path.GetFileName(source), source, sha256 = hash });
        }

        StateStore.WriteJson(Path.Combine(output, configuration == "Debug" ? ".hub-debug-runtime.json" : ".hub-release-runtime.json"), receipt);
    }

    /// <summary>Uses vswhere to locate the system Visual Studio redist directory (the same criterion the engine uses to pick MSVC).</summary>
    private async Task<string?> VisualStudioRedistAsync(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            var result = await runner.RunAsync(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
                Path.GetDirectoryName(vswhere)!, cancellation: cancellation, timeout: TimeSpan.FromSeconds(20));
            var path = result.Output.Trim();
            if (path.Length == 0) return null;
            var redist = Path.Combine(path, "VC", "Redist", "MSVC");
            return Directory.Exists(redist) ? redist : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            runner.Write("App-local runtime lookup failed: " + ex.Message);
            return null;
        }
    }
}
