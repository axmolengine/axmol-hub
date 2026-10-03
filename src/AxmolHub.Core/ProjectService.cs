using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed class ProjectDestinationExistsException(string destination)
    : IOException($"Project destination already exists: {destination}")
{
    public string Destination { get; } = destination;
}

/// <summary>
/// 工程生命周期。
///
/// **构建/运行已委派给引擎自己的 cmdline**（<c>axmol build|run|deploy</c>）：
/// Hub 不再拼 CMake 参数、不锁工具路径、不手工造 <c>INCLUDE</c>/<c>LIB</c>。
/// 构建目录也由引擎决定（见 <see cref="EngineBuildLayout"/>），Hub 只负责发现它。
///
/// Hub 保留的增值：运行期 app-local 运行库（VC redist）补齐。运行直接启动引擎产物目录的 exe。
/// </summary>
public sealed class ProjectService(ProcessRunner runner, EngineCommandLine commandLine, EnginePrebuiltState prebuiltState)
{
    /// <summary>Hub 写在工程目录下的标记目录（构建收据等）。不参与官方工程结构。</summary>
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
        // 名称进入官方模板替换逻辑，限制为安全的 C++ / CMake 标识符。
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
        if (project.Version != engine.Version || project.Channel != engine.Channel) throw new InvalidOperationException("Required Axmol version/channel is not installed.");
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Version != project.Version || locked.Channel != project.Channel || locked.Platform != project.Platform || locked.Configuration != project.Configuration) throw new InvalidOperationException("Project metadata changed. Reopen the project before building.");
        StateStore.ValidateEngine(engine.Path, engine.Channel);

        // -xc 的唯一组装点在 ProjectBuildOptions —— build 与 plan 共用它，否则 plan 会漏报选项。
        var extraCmake = ProjectBuildOptions.CmakeOptions(project, engine, target, prepareFiles: true, prebuiltState);
        await commandLine.RunAsync(engine, AxmolCommandMap.Build(target, project.Path, project.Configuration, configureOnly, extraCmake), project.Path, cancellation);
        WriteBuildReceipt(project, engine);
    }

    /// <summary>构建收据：记录这次构建对应的引擎安装与目标/配置，供 Run 判断产物是否仍然有效。</summary>
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
        if (project.Platform != "windows-x64") return PlatformBuildService.FindArtifact(project);
        return EngineBuildLayout.FindArtifact(BuildDirectory(project), project.Name, project.Configuration, "windows")
            ?? throw new FileNotFoundException("Build " + project.Configuration + " successfully before Run.");
    }

    /// <summary>引擎决定构建目录，这里只是把它找出来；找不到说明还没构建过。</summary>
    public static string BuildDirectory(ProjectEntry project)
        => EngineBuildLayout.FindBuildDirectory(project)
           ?? throw new DirectoryNotFoundException($"No build directory was produced under {project.Path}. Build this target first.");

    public Task<ProcessResult> RunAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation = default, string? androidDevice = null)
    {
        if (project.Platform != "windows-x64") return new PlatformBuildService(runner).RunAsync(project, engine, commandLine, cancellation, deviceSerial: androidDevice);
        return RunWindowsAsync(project, engine, cancellation);
    }

    private async Task<ProcessResult> RunWindowsAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation)
    {
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Platform != project.Platform || locked.Configuration != project.Configuration || locked.Version != project.Version || locked.Channel != project.Channel)
            throw new InvalidOperationException("Project metadata changed. Reopen the project before Run.");
        if (project.Version != engine.Version || project.Channel != engine.Channel) throw new InvalidOperationException("Required Axmol version/channel is not installed.");
        ValidateBuildReceipt(project, engine);
        var build = BuildDirectory(project);
        var executable = EngineBuildLayout.FindArtifact(build, project.Name, project.Configuration, "windows")
            ?? throw new FileNotFoundException("Build " + project.Configuration + " successfully before Run.");
        // 引擎产物目录（bin/<App>/<Config>/）已完整可运行：exe、dll、axslc 着色器、Content 都在同级。
        // Hub 不再复制出一份独立运行目录，只补 app-local 运行库，然后直接启动产物目录里的 exe。
        await PrepareRuntime(Path.GetDirectoryName(executable)!, project.Configuration, cancellation);
        // 官方 FileUtils 以工作目录作为资源根；明确使用 Content，避免启动位置影响查找。
        // 运行游戏 = 长驻进程。用分离启动（等同双击）：让 exe 自己的 AllocConsole 拿到真控制台，
        // 日志颜色与直接双击一致；Hub 不重定向其输出（长驻进程的日志本就走文件/自带控制台）。
        return await runner.RunDetachedAsync(executable, Path.Combine(Path.GetDirectoryName(executable)!, "Content"), cancellation);
    }

    /// <summary>构建收据缺失或与当前引擎安装不符时，不允许用上一次的产物进入 Run。</summary>
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
    /// app-local 运行库。工具链已交还引擎（系统 VS），所以来源也从「Hub 托管的 MSVC」
    /// 改为**系统 Visual Studio 的 redist**；找不到就跳过 —— 引擎构建出的程序本来也依赖
    /// 系统已装的 VC 运行库，这里只是为本机 Debug 输出补一份 app-local 副本。
    /// </summary>
    private async Task PrepareRuntime(string output, string configuration, CancellationToken cancellation)
    {
        var redist = await VisualStudioRedistAsync(cancellation);
        if (redist is null)
        {
            runner.Write("App-local runtime: skipped (no Visual Studio redist found on this machine).");
            return;
        }

        var version = Directory.EnumerateDirectories(redist).OrderDescending().FirstOrDefault(path =>
            File.Exists(Path.Combine(path, "x64/Microsoft.VC143.CRT/msvcp140.dll")));
        if (version is null)
        {
            runner.Write("App-local runtime: skipped (Visual Studio redist has no x64 CRT).");
            return;
        }

        var sources = Directory.EnumerateFiles(Path.Combine(version, "x64/Microsoft.VC143.CRT"), "*.dll").ToList();
        if (configuration == "Debug")
        {
            var debug = Path.Combine(version, "debug_nonredist/x64/Microsoft.VC143.DebugCRT");
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

    /// <summary>用 vswhere 定位系统 Visual Studio 的 redist 目录（与引擎选取 MSVC 的口径一致）。</summary>
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
