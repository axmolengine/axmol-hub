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
/// Hub 保留的增值：Windows 控制台日志捕获补丁、运行目录发布、运行期资源与着色器校验。
/// </summary>
public sealed class ProjectService(ProcessRunner runner, EngineCommandLine commandLine, EnginePrebuiltState prebuiltState)
{
    /// <summary>Hub 写在工程目录下的标记目录（构建收据、日志捕获补丁）。不参与官方工程结构。</summary>
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

    /// <summary>
    /// Windows 控制台日志捕获补丁的 CMake include 路径。官方入口默认是 GUI 子系统，
    /// Hub 的日志面板就看不到程序输出，所以只在 <c>proj.win32/main.cpp</c> 与官方模板**逐字相同**
    /// （即用户没动过入口）时注入；用户自定义入口保留其初始化和控制台行为。
    ///
    /// <paramref name="prepareFiles"/> 为 <c>false</c> 时**只算路径、不写盘** —— 给 <c>plan</c> 用，
    /// 让 plan 报出的 <c>-xc</c> 与真实 build 逐字一致，同时不产生磁盘副作用。
    /// </summary>
    public static string WindowsLogCaptureOption(ProjectEntry project, EngineEntry engine, bool prepareFiles)
    {
        var main = Path.Combine(project.Path, "proj.win32/main.cpp");
        var template = Path.Combine(engine.Path, "templates/common/proj.win32/main.cpp");
        if (!File.Exists(main) || !File.Exists(template) || File.ReadAllText(main).Replace("\r\n", "\n") != File.ReadAllText(template).Replace("\r\n", "\n")) return "";
        // 构建目录已由引擎决定，补丁不能再写进去 —— 落在工程侧 Hub 标记目录里。
        var directory = Path.Combine(project.Path, MarkerDirectory);
        var include = Path.Combine(directory, "HubLogCapture.cmake");
        if (!prepareFiles) return include;
        Directory.CreateDirectory(directory);
        File.WriteAllText(include, LogCaptureCmake);
        return include;
    }

    private const string LogCaptureCmake = """
            if(CMAKE_CURRENT_SOURCE_DIR STREQUAL CMAKE_SOURCE_DIR)
              function(hub_configure_log_capture)
                if(NOT TARGET "${APP_NAME}")
                  return()
                endif()
                set_property(SOURCE "${CMAKE_SOURCE_DIR}/proj.win32/main.cpp" APPEND PROPERTY COMPILE_DEFINITIONS _CONSOLE=1)
                get_target_property(hub_link_options "${APP_NAME}" LINK_OPTIONS)
                if(hub_link_options)
                  list(FILTER hub_link_options EXCLUDE REGEX "/(SUBSYSTEM|ENTRY):")
                  set_property(TARGET "${APP_NAME}" PROPERTY LINK_OPTIONS "${hub_link_options}")
                endif()
                target_link_options("${APP_NAME}" PRIVATE "LINKER:/SUBSYSTEM:WINDOWS" "LINKER:/ENTRY:mainCRTStartup")
              endfunction()
              cmake_language(DEFER CALL hub_configure_log_capture)
            endif()
            """;

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
        var published = Path.Combine(BuildDirectory(project), "run", project.Name, project.Name + ".exe");
        if (File.Exists(published)) return published;
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
        await PrepareRuntime(Path.GetDirectoryName(executable)!, project.Configuration, cancellation);
        executable = PublishWindowsRuntime(project, build, executable);
        await PrepareRuntime(Path.GetDirectoryName(executable)!, project.Configuration, cancellation);
        // 官方 FileUtils 以工作目录作为资源根；明确使用 Content，避免启动位置影响查找。
        return await runner.RunAsync(executable, [], Path.Combine(Path.GetDirectoryName(executable)!, "Content"), null, cancellation, TimeSpan.FromDays(1));
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

    public static string PublishWindowsRuntime(ProjectEntry project, string buildDirectory, string compiledExecutable)
    {
        if (Path.GetFileName(project.Name) != project.Name || project.Name is "." or "..") throw new InvalidDataException("Invalid Windows runtime project name.");
        var build = Path.GetFullPath(buildDirectory);
        var run = Path.Combine(build, "run");
        if ((File.GetAttributes(build) & FileAttributes.ReparsePoint) != 0 || (Directory.Exists(run) && (File.GetAttributes(run) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Windows runtime output cannot be a directory link.");
        var source = Path.GetFullPath(compiledExecutable);
        if (!source.StartsWith(build + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Build executable is outside the project build directory.");
        Directory.CreateDirectory(run);
        var staging = Path.Combine(run, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        // 官方构建输出仍由引擎管理；Hub 运行目录复制真实资源，不依赖同步脚本生成的 junction。
        File.Copy(source, Path.Combine(staging, project.Name + ".exe"));
        foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.dll")) File.Copy(dll, Path.Combine(staging, Path.GetFileName(dll)));
        CopyRuntimeTree(Path.Combine(project.Path, "Content"), Path.Combine(staging, "Content"));
        CopyRuntimeTree(Path.Combine(build, "runtime/axslc"), Path.Combine(staging, "axslc"));
        ValidateWindowsRuntimeAssets(project, build, Path.Combine(staging, project.Name + ".exe"));
        var destination = Path.Combine(run, project.Name);
        string? previous = null;
        if (Directory.Exists(destination))
        {
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Published runtime cannot be a directory link.");
            previous = Path.Combine(run, ".previous-" + Guid.NewGuid().ToString("N"));
            Directory.Move(destination, previous);
        }
        try { Directory.Move(staging, destination); }
        catch
        {
            if (previous != null && !Directory.Exists(destination)) Directory.Move(previous, destination);
            throw;
        }
        return Path.Combine(destination, project.Name + ".exe");
    }

    private static void CopyRuntimeTree(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Windows runtime source is missing: " + source);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime resource source cannot be a directory link: " + source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime resource file cannot be a link: " + file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyRuntimeTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public static void ValidateWindowsRuntimeAssets(ProjectEntry project, string buildDirectory, string executable)
    {
        var output = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var content = Path.Combine(output, "Content");
        var shaderDirectory = Path.Combine(output, "axslc");
        // 链接存在不代表目标可读。编译成功和运行前都验证实际文件，避免空源码进入 GL 编译断言。
        if (!Directory.Exists(content) || !Directory.Exists(shaderDirectory))
            throw new InvalidDataException("Windows runtime Content or axslc is missing. Build again before Run.");
        foreach (var name in new[] { "positionTextureColor_vs", "positionTextureColor_fs", "label_normal_fs", "positionColorLengthTexture_vs", "positionColorLengthTexture_fs", "positionColorTextureAsPointsize_vs", "positionColor_fs" })
        {
            var compiled = Path.Combine(buildDirectory, "runtime/axslc", name);
            var deployed = Path.Combine(shaderDirectory, name);
            if (!File.Exists(compiled) || !File.Exists(deployed) || new FileInfo(deployed).Length == 0)
                throw new InvalidDataException("Windows runtime shader is missing or empty: " + name + ". Build again before Run.");
            var source = File.ReadAllText(deployed);
            if (!source.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#version", StringComparison.Ordinal)
                || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(compiled)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(deployed))))
                throw new InvalidDataException("Windows runtime shader is invalid or stale: " + name + ". Build again before Run.");
        }
        var sourceContent = Path.Combine(project.Path, "Content");
        if (!Directory.Exists(sourceContent)) throw new InvalidDataException("Project Content directory is missing.");
        foreach (var source in Directory.EnumerateFiles(sourceContent, "*", SearchOption.AllDirectories))
        {
            var deployed = Path.Combine(content, Path.GetRelativePath(sourceContent, source));
            if (!File.Exists(deployed) || new FileInfo(deployed).Length != new FileInfo(source).Length)
                throw new InvalidDataException("Windows runtime resource is missing or stale: " + Path.GetRelativePath(sourceContent, source) + ". Build again before Run.");
        }
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
