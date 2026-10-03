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

    public static void ValidateProjectType(string projectType)
    {
        if (projectType is not ("cpp" or "lua")) throw new ArgumentException("Scripting must be cpp or lua.");
    }

    public async Task<ProjectEntry> CreateAsync(string name, string parent, EngineEntry engine, CancellationToken cancellation = default, string projectType = "cpp")
    {
        ValidateProjectType(projectType);
        if (!File.Exists(Path.Combine(engine.Path, "templates", projectType, "axproj-template.json")))
            throw new InvalidDataException("Selected engine does not contain the requested project template.");
        // The name enters the official template substitution logic, so restrict it to a safe C++ / CMake identifier.
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]{0,63}$")) throw new ArgumentException("Project name must start with a letter and contain only letters, digits or underscores (max 64).");
        parent = Path.GetFullPath(parent);
        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new ProjectDestinationExistsException(path);
        Directory.CreateDirectory(parent);
        await commandLine.RunAsync(engine, new AxmolInvocation("new", ["-p", $"dev.axmol.{name.ToLowerInvariant()}", "-d", parent, "-l", projectType, name]), parent, cancellation);
        var project = StateStore.ReadProject(path);
        if (project.Version != engine.Version) throw new InvalidDataException("Created project engine version does not match selected engine.");
        if (project.ProjectType != projectType) throw new InvalidDataException("Created project scripting type does not match selection.");
        project.Channel = engine.Channel;
        StateStore.LockProject(project);
        return project;
    }

    public Task ConfigureAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation = default)
        => BuildAsync(project, engine, true, cancellation);

    public async Task BuildAsync(ProjectEntry project, EngineEntry engine, bool configureOnly = false, CancellationToken cancellation = default, AndroidSigningPasswords? androidPasswords = null)
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
        if (locked.Version != project.Version || locked.Channel != project.Channel || locked.Platform != project.Platform || locked.Configuration != project.Configuration) throw new InvalidOperationException("Project metadata changed. Reopen the project before building.");
        StateStore.ValidateEngine(engine.Path, engine.Channel);

        // The single assembly point for -xc is ProjectBuildOptions — build and plan share it, otherwise plan would omit options.
        var extraCmake = ProjectBuildOptions.CmakeOptions(project, engine, target, prepareFiles: true, prebuiltState);
        await commandLine.RunAsync(engine, AxmolCommandMap.Build(target, project.Path, project.Configuration, configureOnly, extraCmake), project.Path, cancellation);
        WriteBuildReceipt(project, engine);
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

    public Task<ProcessResult> RunAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation = default, string? androidDevice = null)
    {
        if (BuildTargets.Get(project.Platform).Family != "windows") return new PlatformBuildService(runner).RunAsync(project, engine, commandLine, cancellation, deviceSerial: androidDevice);
        return RunWindowsAsync(project, engine, cancellation);
    }

    private async Task<ProcessResult> RunWindowsAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation)
    {
        var target = BuildTargets.Get(project.Platform);
        // Windows can cross-compile arm64, but an arm64 native image can only be launched by an arm64 host.
        if (!target.CanRunLocally(BuildTargets.HostArch))
            throw new PlatformNotSupportedException($"{target.Name} was built for {target.Architecture}; a {target.Architecture} host is required to run it (current host: {BuildTargets.Host}/{BuildTargets.HostArch}).");
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Platform != project.Platform || locked.Configuration != project.Configuration || locked.Version != project.Version || locked.Channel != project.Channel)
            throw new InvalidOperationException("Project metadata changed. Reopen the project before Run.");
        if (project.Version != engine.Version || project.Channel != engine.Channel) throw new InvalidOperationException("Required Axmol version/channel is not installed.");
        ValidateBuildReceipt(project, engine);
        var build = BuildDirectory(project);
        var executable = EngineBuildLayout.FindArtifact(build, project.Name, project.Configuration, "windows")
            ?? throw new FileNotFoundException("Build " + project.Configuration + " successfully before Run.");
        // The engine artifact directory (bin/<App>/<Config>/) is already fully runnable: exe, dll, axslc
        // shaders and Content all sit at the same level. The Hub no longer copies out a separate run
        // directory; it only fills in the app-local runtime library and then launches the exe in the
        // artifact directory directly.
        await PrepareRuntime(Path.GetDirectoryName(executable)!, project.Configuration, target.Architecture, cancellation);
        // The official FileUtils treats the working directory as the resource root; explicitly use Content
        // so the launch location doesn't affect resolution.
        // Running the game = a long-running process. Use a detached launch (equivalent to double-clicking):
        // the exe's own AllocConsole gets a real console and log colors match a direct double-click; the Hub
        // doesn't redirect its output (a long-running process's logs go to a file / its own console anyway).
        return await runner.RunDetachedAsync(executable, Path.Combine(Path.GetDirectoryName(executable)!, "Content"), cancellation);
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
