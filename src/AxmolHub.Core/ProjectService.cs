using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed class ProjectDestinationExistsException(string destination)
    : IOException($"Project destination already exists: {destination}")
{
    public string Destination { get; } = destination;
}

public sealed class ProjectService(ProcessRunner runner, ToolchainDetector detector, string toolsRoot, string wrapper)
{
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
        await InvokeAsync(engine, ["new", "-p", $"dev.axmol.{name.ToLowerInvariant()}", "-d", parent, "-l", projectType, name], parent, cancellation);
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
        if (project.Platform != "windows-x64")
        {
            await new PlatformBuildService(runner, toolsRoot).BuildAsync(project, engine, configureOnly, cancellation, androidPasswords);
            return;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows builds require a Windows host.");
        if (project.Version != engine.Version || project.Channel != engine.Channel) throw new InvalidOperationException("Required Axmol version/channel is not installed.");
        if (detector.CompilerPath == null || detector.SdkVersion == null || detector.CMakePath == null || detector.NinjaPath == null)
            throw new InvalidOperationException("Hub managed MSVC, Windows SDK, CMake or Ninja is missing. System toolchains are not used.");
        if (!File.Exists(Path.Combine(toolsRoot, "axslcc/axslcc.exe"))) throw new InvalidOperationException("Install Hub managed Axmol shader compiler before building.");
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Version != project.Version || locked.Channel != project.Channel || locked.Platform != project.Platform || locked.Configuration != project.Configuration) throw new InvalidOperationException("Project metadata changed. Reopen the project before building.");
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        var buildDirectory = BuildTargets.BuildDirectory(project);
        PrepareEngineBuildDirectory(buildDirectory, EngineInstallationToken(engine));
        Directory.CreateDirectory(buildDirectory);
        // 不修改项目原有配置；CMake 子进程在专用构建目录读取隔离的 NuGet 源。
        File.WriteAllText(Path.Combine(buildDirectory, "NuGet.Config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <disabledPackageSources><clear /></disabledPackageSources>
              <fallbackPackageFolders><clear /></fallbackPackageFolders>
            </configuration>
            """);
        var args = new List<string>
        {
            "-S", project.Path, "-B", buildDirectory, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=" + project.Configuration,
            $"-DCMAKE_MAKE_PROGRAM={detector.NinjaPath}", $"-DCMAKE_C_COMPILER={detector.CompilerPath}", $"-DCMAKE_CXX_COMPILER={detector.CompilerPath}",
            $"-DCMAKE_RC_COMPILER={Path.Combine(detector.SdkRoot!, "bin", detector.SdkVersion, "x64/rc.exe")}",
            $"-DCMAKE_MT={Path.Combine(detector.SdkRoot!, "bin", detector.SdkVersion, "x64/mt.exe")}",
            $"-DAXSLCC_EXE={Path.Combine(toolsRoot, "axslcc/axslcc.exe")}", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON",
            $"-DNUGET_EXE={Path.Combine(toolsRoot, "nuget/nuget.exe")}",
            $"-DCMAKE_LINKER={Path.Combine(detector.MsvcRoot!, "bin/Hostx64/x64/link.exe")}",
            $"-DCMAKE_AR={Path.Combine(detector.MsvcRoot!, "bin/Hostx64/x64/lib.exe")}",
            $"-DGIT_EXECUTABLE={Path.Combine(toolsRoot, "git/cmd/git.exe")}",
            "-DCMAKE_FIND_USE_SYSTEM_ENVIRONMENT_PATH=OFF", "-DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF", "-DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF",
            "-DCMAKE_FIND_USE_CMAKE_SYSTEM_PATH=OFF",
            $"-DPWSH_EXECUTABLE={ToolchainDetector.PowerShell}"
        };
        var consoleInclude = PrepareWindowsLogCapture(project, engine, buildDirectory);
        args.Add("-DCMAKE_PROJECT_INCLUDE=" + consoleInclude);
        // CMake 会把编译器路径写入 .cmake 文件，Windows 反斜杠必须转换为正斜杠。
        args = args.Select(argument => argument.Replace('\\', '/')).ToList();
        // 官方 build 自动挑选系统 VS。隔离模式调用官方 CMake 工程并锁定工具路径。
        var environment = detector.BuildEnvironment(engine);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { args, environment }))));
        var stamp = Path.Combine(buildDirectory, ".hub-toolchain.json");
        if (!File.Exists(stamp) || System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(stamp)) != fingerprint)
            args.Insert(0, "--fresh");
        var configure = await runner.RunAsync(detector.CMakePath, args, buildDirectory, environment, cancellation);
        EnsureSucceeded(configure, "CMake configuration");
        StateStore.WriteJson(stamp, fingerprint);
        if (!configureOnly)
        {
            var build = await runner.RunAsync(detector.CMakePath, ["--build", buildDirectory, "--target", project.Name, "--parallel", "4"], buildDirectory, environment, cancellation);
            EnsureSucceeded(build, "Build");
            var compiled = FindBuildExecutable(project);
            PrepareRuntime(Path.GetDirectoryName(compiled)!, project.Configuration);
            PublishWindowsRuntime(project, compiled);
        }
    }
    public static string PrepareWindowsLogCapture(ProjectEntry project, EngineEntry engine, string buildDirectory)
    {
        var main = Path.Combine(project.Path, "proj.win32/main.cpp");
        var template = Path.Combine(engine.Path, "templates/common/proj.win32/main.cpp");
        // 仅适配未修改的官方入口，用户自定义入口保留其初始化和控制台行为。
        if (!File.Exists(main) || !File.Exists(template) || File.ReadAllText(main).Replace("\r\n", "\n") != File.ReadAllText(template).Replace("\r\n", "\n")) return "";
        var include = Path.Combine(buildDirectory, "HubLogCapture.cmake");
        File.WriteAllText(include, """
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
            """);
        return include;
    }
    private async Task InvokeAsync(EngineEntry engine, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellation)
    {
        var validated = StateStore.ValidateEngine(engine.Path, engine.Channel);
        if (validated.Version != engine.Version) throw new InvalidOperationException("Engine contents no longer match registered version.");
        var shell = OperatingSystem.IsWindows() ? ToolchainDetector.PowerShell : Path.Combine(toolsRoot, "powershell/pwsh");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-File", wrapper, "-EngineRoot", engine.Path };
        if (OperatingSystem.IsWindows()) args.InsertRange(2, ["-ExecutionPolicy", "Bypass"]);
        args.AddRange(arguments);
        var environment = OperatingSystem.IsWindows() ? detector.BuildEnvironment(engine) : new PlatformBuildService(runner, toolsRoot).CreateEnvironment(engine, BuildTargets.Get(OperatingSystem.IsMacOS() ? "macos-arm64" : "linux-x64"));
        if (!Directory.Exists(Path.Combine(engine.Path, ".git")))
        {
            // 官方 CLI 在发行 ZIP 中查询 git 会留下非零退出码；创建本身不依赖 Git。
            var gitRoot = Path.GetFullPath(Path.Combine(toolsRoot, "git"));
            environment["PATH"] = string.Join(Path.PathSeparator, environment["PATH"].Split(Path.PathSeparator).Where(p => !Path.GetFullPath(p).StartsWith(gitRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        }
        var result = await runner.RunAsync(shell, args, workingDirectory, environment, cancellation);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Axmol command failed (exit {result.ExitCode}).\n{result.Error}\n{result.Output}");
    }
    public string FindExecutable(ProjectEntry project)
    {
        if (project.Platform != "windows-x64") return PlatformBuildService.FindArtifact(project);
        var published = Path.Combine(BuildTargets.BuildDirectory(project), "run", project.Name, project.Name + ".exe");
        return File.Exists(published) ? published : FindBuildExecutable(project);
    }
    private static string FindBuildExecutable(ProjectEntry project)
    {
        var directory = Path.Combine(BuildTargets.BuildDirectory(project), "bin", project.Name);
        var path = new[] { Path.Combine(directory, project.Name + ".exe"), Path.Combine(directory, project.Configuration, project.Name + ".exe") }.FirstOrDefault(File.Exists)
            ?? Path.Combine(directory, project.Name + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Build " + project.Configuration + " successfully before Run.", path);
        return Path.GetFullPath(path);
    }
    private static void EnsureSucceeded(ProcessResult result, string action)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"{action} failed (exit {result.ExitCode}).\n{result.Error}\n{result.Output}");
    }
    public Task<ProcessResult> RunAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation = default, string? androidDevice = null)
    {
        if (project.Platform != "windows-x64") return new PlatformBuildService(runner, toolsRoot).RunAsync(project, engine, cancellation, deviceSerial: androidDevice);
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Platform != project.Platform || locked.Configuration != project.Configuration || locked.Version != project.Version || locked.Channel != project.Channel)
            throw new InvalidOperationException("Project metadata changed. Reopen the project before Run.");
        if (project.Version != engine.Version || project.Channel != engine.Channel) throw new InvalidOperationException("Required Axmol version/channel is not installed.");
        var engineStamp = Path.Combine(BuildTargets.BuildDirectory(project), ".hub-engine.json");
        if (!File.Exists(engineStamp) || System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(engineStamp)) != EngineInstallationToken(engine))
            throw new InvalidOperationException("Engine installation changed. Build again before Run.");
        var executable = FindExecutable(project);
        if (!executable.StartsWith(Path.Combine(BuildTargets.BuildDirectory(project), "run") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            PrepareRuntime(Path.GetDirectoryName(executable)!, project.Configuration);
            executable = PublishWindowsRuntime(project, executable);
        }
        ValidateWindowsRuntimeAssets(project, executable);
        PrepareRuntime(Path.GetDirectoryName(executable)!, project.Configuration);
        // 官方 FileUtils 以工作目录作为资源根；明确使用 Content，避免启动位置影响查找。
        return runner.RunAsync(executable, [], Path.Combine(Path.GetDirectoryName(executable)!, "Content"), detector.BuildEnvironment(engine), cancellation, TimeSpan.FromDays(1));
    }
    public static string PublishWindowsRuntime(ProjectEntry project, string compiledExecutable)
    {
        if (Path.GetFileName(project.Name) != project.Name || project.Name is "." or "..") throw new InvalidDataException("Invalid Windows runtime project name.");
        var build = Path.GetFullPath(BuildTargets.BuildDirectory(project));
        var run = Path.Combine(build, "run");
        if ((File.GetAttributes(build) & FileAttributes.ReparsePoint) != 0 || (Directory.Exists(run) && (File.GetAttributes(run) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Windows runtime output cannot be a directory link.");
        var source = Path.GetFullPath(compiledExecutable);
        if (!source.StartsWith(build + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Build executable is outside the project build directory.");
        Directory.CreateDirectory(run);
        var staging = Path.Combine(run, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        // 官方构建输出仍由 CMake 管理；Hub 运行目录复制真实资源，不依赖同步脚本生成的 junction。
        File.Copy(source, Path.Combine(staging, project.Name + ".exe"));
        foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.dll")) File.Copy(dll, Path.Combine(staging, Path.GetFileName(dll)));
        CopyRuntimeTree(Path.Combine(project.Path, "Content"), Path.Combine(staging, "Content"));
        CopyRuntimeTree(Path.Combine(build, "runtime/axslc"), Path.Combine(staging, "axslc"));
        ValidateWindowsRuntimeAssets(project, Path.Combine(staging, project.Name + ".exe"));
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
    public static void ValidateWindowsRuntimeAssets(ProjectEntry project, string executable)
    {
        var output = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var content = Path.Combine(output, "Content");
        var shaderDirectory = Path.Combine(output, "axslc");
        // 链接存在不代表目标可读。编译成功和运行前都验证实际文件，避免空源码进入 GL 编译断言。
        if (!Directory.Exists(content) || !Directory.Exists(shaderDirectory))
            throw new InvalidDataException("Windows runtime Content or axslc is missing. Build again before Run.");
        foreach (var name in new[] { "positionTextureColor_vs", "positionTextureColor_fs", "label_normal_fs", "positionColorLengthTexture_vs", "positionColorLengthTexture_fs", "positionColorTextureAsPointsize_vs", "positionColor_fs" })
        {
            var compiled = Path.Combine(BuildTargets.BuildDirectory(project), "runtime/axslc", name);
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
    public static void PrepareEngineBuildDirectory(string directory, string installationToken)
    {
        var stamp = Path.Combine(directory, ".hub-engine.json");
        if (File.Exists(stamp) && System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(stamp)) != installationToken)
        {
            // 解压修复后的源码可能保留旧时间戳；撤下旧缓存，避免继续运行旧二进制。
            var previous = directory + ".previous-" + Guid.NewGuid().ToString("N");
            Directory.Move(directory, previous);
        }
        Directory.CreateDirectory(directory);
        StateStore.WriteJson(stamp, installationToken);
    }
    private void PrepareRuntime(string output, string configuration)
    {
        // PATH 在 System32 之后，不能保证私有运行库。仅在本机 Debug 输出准备 app-local DLL。
        // 这些文件来自用户本机官方安装，不进入 Hub 发布包，不用于游戏正式分发。
        var redist = Path.Combine(toolsRoot, "vs2022/VC/Redist/MSVC");
        var version = Directory.Exists(redist) ? Directory.EnumerateDirectories(redist).OrderDescending().FirstOrDefault(path =>
            (configuration != "Debug" || File.Exists(Path.Combine(path, "debug_nonredist/x64/Microsoft.VC143.DebugCRT/msvcp140d.dll"))) &&
            File.Exists(Path.Combine(path, "x64/Microsoft.VC143.CRT/msvcp140.dll"))) : null;
        var ucrt = detector.SdkVersion == null ? null : Path.Combine(detector.SdkRoot!, "bin", detector.SdkVersion, "x64/ucrt/ucrtbased.dll");
        if (version == null || (configuration == "Debug" && (ucrt == null || !File.Exists(ucrt))))
            throw new InvalidOperationException("Hub managed Debug runtime is incomplete. Install MSVC Build Tools and Windows SDK before Run.");
        var sources = Directory.EnumerateFiles(Path.Combine(version, "x64/Microsoft.VC143.CRT"), "*.dll");
        if (configuration == "Debug") sources = sources.Concat(Directory.EnumerateFiles(Path.Combine(version, "debug_nonredist/x64/Microsoft.VC143.DebugCRT"), "*.dll")).Append(ucrt!);
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
}
