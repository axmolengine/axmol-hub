using System.Text.Json;

namespace AxmolHub.Core;

public sealed record BuildCommand(string Executable, string[] Arguments, string WorkingDirectory);
public sealed record PlatformBuildPlan(string Target, string OutputDirectory, List<BuildCommand> Commands, Dictionary<string, string> Environment);

// 复用引擎 CMake 与官方 iOS/NDK/Emscripten toolchain，不运行会改全局环境的 setup。
public sealed class PlatformBuildService(ProcessRunner runner, string toolsRoot)
{
    private static string Suffix => OperatingSystem.IsWindows() ? ".exe" : "";
    public string Tool(string relative) => Path.GetFullPath(Path.Combine(toolsRoot, relative));
    public string Python => Tool(OperatingSystem.IsWindows() ? "python/python.exe" : "python/bin/python3");
    public string PowerShell => OperatingSystem.IsWindows() ? ToolchainDetector.PowerShell : Tool("powershell/pwsh");
    public string Emscripten => Tool("emsdk/upstream/emscripten");
    public string Ndk => Tool("android/sdk/ndk/27.3.13750724");
    public string Xcode => Tool("Xcode.app/Contents/Developer");

    public IReadOnlyList<ToolchainComponent> Inspect(string targetId, string? host = null)
    {
        var target = BuildTargets.Get(targetId);
        if (!target.CanBuildOn(host ?? BuildTargets.Host))
            return [new("Build host", ComponentStatus.Missing, $"{target.Name}: requires {string.Join(" / ", target.Hosts)}; current host: {host ?? BuildTargets.Host}.")];
        var files = RequiredFiles(target);
        return files.Select(item => new ToolchainComponent(item.Name, File.Exists(item.Path) ? ComponentStatus.Unknown : ComponentStatus.Missing,
            File.Exists(item.Path) ? "Present; run Verify to execute the tool probe." : "Required: " + item.Path, item.Path)).ToArray();
    }

    private List<(string Name, string Path)> RequiredFiles(BuildTarget target)
    {
        var files = new List<(string, string)>
        {
            ("CMake", Tool("cmake/bin/cmake" + Suffix)), ("Ninja", Tool("ninja/ninja" + Suffix)),
            ("Git", Tool(OperatingSystem.IsWindows() ? "git/cmd/git.exe" : "git/bin/git")),
            ("Axmol shader compiler", Tool("axslcc/axslcc" + Suffix)), ("PowerShell", PowerShell)
        };
        switch (target.Family)
        {
            case "android":
                files.Add(("JDK 17", Tool("jdk/bin/java" + Suffix)));
                files.Add(("Gradle 8.13", Tool("gradle/lib/gradle-gradle-cli-main-8.13.jar")));
                files.Add(("Android SDK 36", Tool("android/sdk/platforms/android-36/android.jar")));
                files.Add(("Android AAPT2", Tool("android/sdk/build-tools/35.0.0/aapt2" + Suffix)));
                files.Add(("APK signer", Tool("android/sdk/build-tools/35.0.0/lib/apksigner.jar")));
                files.Add(("ADB", Tool("android/sdk/platform-tools/adb" + Suffix)));
                files.Add(("Android NDK r27d", Path.Combine(Ndk, "build/cmake/android.toolchain.cmake")));
                files.Add(("Android NDK Clang", Path.Combine(Ndk, "toolchains/llvm/prebuilt", BuildTargets.Host == "macos" ? "darwin-x86_64" : BuildTargets.Host + "-x86_64", "bin/clang" + Suffix)));
                break;
            case "wasm":
                files.Add(("Python", Python));
                files.Add(("Emscripten 3.1.73", Path.Combine(Emscripten, "emcc.py")));
                files.Add(("Emscripten LLVM", Tool("emsdk/upstream/bin/clang" + Suffix)));
                files.Add(("Node.js", Tool("node/node" + Suffix)));
                break;
            case "linux":
                files.Add(("Managed GCC", Tool("gcc/bin/gcc")));
                files.Add(("Managed G++", Tool("gcc/bin/g++")));
                break;
            case "macos": case "ios": case "tvos":
                files.Add(("Xcode", Path.Combine(Xcode, "usr/bin/xcodebuild")));
                files.Add(("Xcode Clang", Path.Combine(Xcode, "Toolchains/XcodeDefault.xctoolchain/usr/bin/clang")));
                break;
            case "uwp":
                // 这些附加组件不能由桌面 MSVC 状态推断为已安装。
                var msvc = Tool("vs2022/VC/Tools/MSVC");
                var version = Directory.Exists(msvc) ? Directory.EnumerateDirectories(msvc).OrderDescending().FirstOrDefault() : null;
                files.Add(("MSVC UWP libraries", Path.Combine(version ?? msvc, "lib/x64/store/vccorlib.lib")));
                files.Add(("NuGet", Tool("nuget/nuget.exe")));
                break;
        }
        return files;
    }

    public async Task<List<ToolchainComponent>> VerifyAsync(string targetId, CancellationToken cancellation = default)
    {
        var rows = Inspect(targetId).ToList();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Status != ComponentStatus.Unknown) continue;
            var executable = row.Executable!;
            // SDK 文件存在只说明前置文件齐全，最终由 CMake 配置检查 SDK 完整性。
            if (executable.EndsWith(".cmake") || executable.EndsWith(".lib") || executable.EndsWith(".jar"))
            { rows[index] = row with { Details = "SDK prerequisite file present; target configuration is required for validation." }; continue; }
            try
            {
                var environment = PrepareEnvironment(CreateEnvironment(new("", toolsRoot), BuildTargets.Get(targetId)));
                if (executable.EndsWith(".py")) PrepareWebConfiguration(environment);
                var arguments = executable.EndsWith(".py") ? new[] { executable, "--version" } : row.Name == "PowerShell" ? ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"] :
                    row.Name == "Xcode" ? ["-version"] : row.Name is "Android AAPT2" or "ADB" ? ["version"] : row.Name == "NuGet" ? ["help", "-ForceEnglishOutput"] : new[] { "--version" };
                var probe = await runner.RunAsync(executable.EndsWith(".py") ? Python : executable, arguments,
                    toolsRoot, environment, cancellation, TimeSpan.FromSeconds(20));
                rows[index] = row with { Status = probe.ExitCode == 0 ? ComponentStatus.Installed : ComponentStatus.Broken, Details = (probe.Output + probe.Error).Trim() };
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { rows[index] = row with { Status = ComponentStatus.Broken, Details = ex.Message }; }
        }
        return rows;
    }

    public PlatformBuildPlan Plan(ProjectEntry project, EngineEntry engine, bool configureOnly, bool checkFiles = true, string? host = null)
    {
        var target = BuildTargets.Get(project.Platform);
        BuildConfigurations.ValidateTarget(project);
        if (!target.CanBuildOn(host ?? BuildTargets.Host)) throw new PlatformNotSupportedException($"{target.Name} requires {string.Join(" / ", target.Hosts)}. Current host: {host ?? BuildTargets.Host}.");
        if (target.Family == "windows") throw new InvalidOperationException("Use ProjectService for the verified Windows desktop build.");
        if (target.Family == "android") PackagingRecipes.RequireVerified(engine, PackagingRecipes.AndroidPackaging);
        if (checkFiles)
        {
            var missing = Inspect(target.Id).Where(item => item.Status == ComponentStatus.Missing).ToArray();
            if (missing.Length != 0) throw new InvalidOperationException("Missing target tools:\n" + string.Join('\n', missing.Select(item => item.Details)));
        }
        var directory = BuildTargets.BuildDirectory(project);
        var cmake = Tool("cmake/bin/cmake" + Suffix);
        var arguments = new List<string>
        {
            "-S", project.Path, "-B", directory, "-G", target.Family == "uwp" ? "Visual Studio 17 2022" : target.Family is "ios" or "tvos" or "macos" ? "Xcode" : "Ninja",
            "-DCMAKE_BUILD_TYPE=" + project.Configuration, "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON",
            "-DCMAKE_FIND_USE_SYSTEM_ENVIRONMENT_PATH=OFF", "-DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF", "-DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF",
            "-DAXSLCC_EXE=" + Tool("axslcc/axslcc" + Suffix),
            "-DGIT_EXECUTABLE=" + Tool(OperatingSystem.IsWindows() ? "git/cmd/git.exe" : "git/bin/git"), "-DPWSH_EXECUTABLE=" + PowerShell
        };
        if (target.Family is "android" or "wasm" or "linux") arguments.Add("-DCMAKE_MAKE_PROGRAM=" + Tool("ninja/ninja" + Suffix));
        switch (target.Family)
        {
            case "android": arguments.AddRange(["-DCMAKE_TOOLCHAIN_FILE=" + Path.Combine(Ndk, "build/cmake/android.toolchain.cmake"), "-DANDROID_ABI=" + target.Architecture,
                "-DANDROID_PLATFORM=android-23", "-DANDROID_STL=c++_shared", "-DANDROID_USE_LEGACY_TOOLCHAIN_FILE=false",
                "-D_AX_ANDROID_PROJECT_DIR=" + Path.Combine(AndroidPackageService.StageDirectory(project), "app")]); break;
            case "wasm": arguments.AddRange(["-DCMAKE_TOOLCHAIN_FILE=" + Path.Combine(Emscripten, "cmake/Modules/Platform/Emscripten.cmake"), "-DPython3_EXECUTABLE=" + Python]); break;
            case "linux": arguments.AddRange(["-DCMAKE_C_COMPILER=" + Tool("gcc/bin/gcc"), "-DCMAKE_CXX_COMPILER=" + Tool("gcc/bin/g++")]); break;
            case "macos": arguments.AddRange(["-DCMAKE_OSX_ARCHITECTURES=" + target.Architecture, "-DCMAKE_OSX_DEPLOYMENT_TARGET=10.15"]); break;
            case "ios": case "tvos": arguments.AddRange(["-DCMAKE_TOOLCHAIN_FILE=" + Path.Combine(engine.Path, "1k/ios.cmake"), "-DARCHS=" + target.Architecture,
                "-DPLAT=" + (target.Family == "tvos" ? "tvOS" : "iOS"), "-DSIMULATOR=" + (target.Simulator ? "TRUE" : "FALSE"), "-DCMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_ALLOWED=NO"]); break;
            case "uwp": arguments.AddRange(["-A", "x64", "-DCMAKE_SYSTEM_NAME=WindowsStore", "-DCMAKE_SYSTEM_VERSION=10.0", "-DCMAKE_GENERATOR_INSTANCE=" + Tool("vs2022"), "-DNUGET_EXE=" + Tool("nuget/nuget.exe")]); break;
        }
        var commands = new List<BuildCommand> { new(cmake, arguments.Select(a => a.Replace('\\', '/')).ToArray(), directory) };
        if (!configureOnly) commands.Add(new(cmake, ["--build", directory, "--config", project.Configuration, "--target", project.Name, "--parallel", "4"], directory));
        return new(target.Id, directory, commands, CreateEnvironment(engine, target));
    }

    public Dictionary<string, string> CreateEnvironment(EngineEntry engine, BuildTarget target)
    {
        var profile = Tool("../cache/platform-profile/" + target.Id);
        var temp = Path.Combine(profile, "temp");
        var paths = new List<string> { Tool("cmake/bin"), Tool("ninja"), Tool(OperatingSystem.IsWindows() ? "git/cmd" : "git/bin"), Tool("axslcc"), Path.GetDirectoryName(PowerShell)! };
        var env = new Dictionary<string, string>
        {
            ["AX_ROOT"] = engine.Path, ["AXMOL_ROOT"] = engine.Path, ["HOME"] = profile, ["USERPROFILE"] = profile,
            ["TMPDIR"] = temp, ["TEMP"] = temp, ["TMP"] = temp, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(profile, "gitconfig"),
            ["CMAKE_PREFIX_PATH"] = "", ["CMAKE_TOOLCHAIN_FILE"] = "", ["GRADLE_USER_HOME"] = Tool("../cache/gradle")
        };
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            env["SystemRoot"] = env["WINDIR"] = windows; env["COMSPEC"] = Path.Combine(windows, "System32", "cmd.exe"); env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            env["OS"] = "Windows_NT"; env["PROCESSOR_ARCHITECTURE"] = "AMD64";
            env["APPDATA"] = Path.Combine(profile, "AppData/Roaming"); env["LOCALAPPDATA"] = Path.Combine(profile, "AppData/Local");
            paths.Add(Path.Combine(windows, "System32"));
            paths.Add(Tool("git/mingw64/bin"));
            env["GIT_CONFIG_COUNT"] = "1"; env["GIT_CONFIG_KEY_0"] = "http.sslBackend"; env["GIT_CONFIG_VALUE_0"] = "openssl";
        }
        else { paths.AddRange(["/usr/bin", "/bin"]); env["LANG"] = "en_US.UTF-8"; }
        if (target.Family == "linux") { paths.Insert(0, Tool("gcc/bin")); env["CC"] = Tool("gcc/bin/gcc"); env["CXX"] = Tool("gcc/bin/g++"); }
        if (target.Family is "macos" or "ios" or "tvos") env["DEVELOPER_DIR"] = Xcode;
        if (target.Family == "android")
        {
            env["ANDROID_HOME"] = env["ANDROID_SDK_ROOT"] = Tool("android/sdk"); env["ANDROID_NDK_ROOT"] = Ndk;
            env["JAVA_HOME"] = Tool("jdk"); paths.Insert(0, Tool("jdk/bin"));
            env["ANDROID_USER_HOME"] = Path.Combine(profile, ".android");
            env["ANDROID_EMULATOR_HOME"] = Path.Combine(profile, "emulator");
        }
        if (target.Family == "wasm")
        {
            env["EMSDK"] = Tool("emsdk"); env["EM_CONFIG"] = Path.Combine(profile, ".emscripten");
            env["EM_CACHE"] = Tool("../cache/emscripten"); env["EMSDK_PYTHON"] = Python; env["EMSDK_NODE"] = Tool("node/node" + Suffix);
            paths.InsertRange(0, [Path.GetDirectoryName(Python)!, Emscripten, Tool("emsdk/upstream/bin"), Tool("node")]);
        }
        env["PATH"] = string.Join(Path.PathSeparator, paths);
        return env;
    }
    private static Dictionary<string, string> PrepareEnvironment(Dictionary<string, string> environment)
    {
        foreach (var key in new[] { "TEMP", "HOME", "APPDATA", "LOCALAPPDATA" })
            if (environment.TryGetValue(key, out var path)) Directory.CreateDirectory(path);
        File.WriteAllText(environment["GIT_CONFIG_GLOBAL"], "");
        return environment;
    }
    private void PrepareWebConfiguration(Dictionary<string, string> environment)
        => File.WriteAllText(environment["EM_CONFIG"], $"LLVM_ROOT = {JsonSerializer.Serialize(Tool("emsdk/upstream/bin").Replace('\\', '/'))}\nBINARYEN_ROOT = {JsonSerializer.Serialize(Tool("emsdk/upstream").Replace('\\', '/'))}\nNODE_JS = [{JsonSerializer.Serialize(environment["EMSDK_NODE"].Replace('\\', '/'))}]\n");

    public async Task BuildAsync(ProjectEntry project, EngineEntry engine, bool configureOnly, CancellationToken cancellation, AndroidSigningPasswords? androidPasswords = null)
    {
        if (BuildTargets.Get(project.Platform).Family == "uwp")
            throw new PlatformNotSupportedException("UWP target planning is available. Managed UWP components, SDK isolation and packaging must be completed before executing this target.");
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Version != engine.Version || locked.Channel != engine.Channel || locked.Platform != project.Platform || locked.Configuration != project.Configuration || locked.Version != project.Version || locked.Channel != project.Channel)
            throw new InvalidOperationException("Project engine/target lock changed. Reopen the project.");
        var validated = StateStore.ValidateEngine(engine.Path, engine.Channel);
        if (validated.Version != engine.Version) throw new InvalidOperationException("Engine contents changed.");
        var plan = Plan(project, engine, configureOnly);
        ProjectService.PrepareEngineBuildDirectory(plan.OutputDirectory, ProjectService.EngineInstallationToken(engine));
        PrepareEnvironment(plan.Environment);
        var complete = Path.Combine(plan.OutputDirectory, ".hub-build-complete.json");
        if (!configureOnly && File.Exists(complete)) File.Delete(complete);
        string? signingCertificate = null;
        if (!configureOnly && project.Configuration == "Release" && BuildTargets.Get(project.Platform).Family == "android")
            signingCertificate = await new AndroidSigningService(runner, toolsRoot).ValidateAsync(AndroidReleaseSettings.Require(project),
                androidPasswords ?? throw new InvalidOperationException("Enter release signing passwords before Build."), plan.Environment, cancellation);
        if (BuildTargets.Get(project.Platform).Family == "wasm")
        {
            PrepareWebConfiguration(plan.Environment);
        }
        // 目标工具变化必须重新配置，不复用另一组 compiler/cache。
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { plan.Commands, plan.Environment }))));
        var stamp = Path.Combine(plan.OutputDirectory, ".hub-toolchain.json");
        var commands = plan.Commands.ToArray();
        if (!File.Exists(stamp) || JsonSerializer.Deserialize<string>(File.ReadAllText(stamp)) != fingerprint)
            commands[0] = commands[0] with { Arguments = ["--fresh", .. commands[0].Arguments] };
        // 构建失败或取消后不允许通过上次成功产物进入 Run。
        foreach (var command in commands)
        {
            var result = await runner.RunAsync(command.Executable, command.Arguments, command.WorkingDirectory, plan.Environment, cancellation, TimeSpan.FromHours(2));
            if (result.ExitCode != 0) throw new InvalidOperationException($"{project.Platform} build failed (exit {result.ExitCode}).\n{result.Error}\n{result.Output}");
        }
        StateStore.WriteJson(stamp, fingerprint);
        if (!configureOnly)
        {
            if (BuildTargets.Get(project.Platform).Family == "android") await new AndroidPackageService(runner, toolsRoot).PackageAsync(project, engine, plan.Environment, cancellation, androidPasswords, signingCertificate);
            FindArtifact(project); StateStore.WriteJson(complete, ProjectService.EngineInstallationToken(engine));
        }
    }

    public static string FindArtifact(ProjectEntry project)
    {
        var directory = BuildTargets.BuildDirectory(project);
        var target = BuildTargets.Get(project.Platform);
        if (target.Family == "android") return File.Exists(AndroidPackageService.ApkPath(project)) ? AndroidPackageService.ApkPath(project) : throw new FileNotFoundException("Android APK is missing. Build this target with managed packaging tools.");
        var name = target.Family switch { "android" => "lib" + project.Name + ".so", "wasm" => project.Name + ".html", "uwp" => project.Name + ".exe", _ => project.Name };
        var paths = new[] { Path.Combine(directory, "bin", project.Name, name), Path.Combine(directory, "bin", project.Name, project.Configuration, name), Path.Combine(directory, "bin", project.Name, project.Name + ".app/Contents/MacOS", project.Name),
            Path.Combine(directory, "bin", project.Name, project.Configuration, project.Name + ".app/Contents/MacOS", project.Name),
            Path.Combine(directory, "bin", project.Name, project.Name + ".app", project.Name), Path.Combine(directory, "bin", project.Name, project.Configuration, project.Name + ".app", project.Name),
            Path.Combine(directory, "lib", name), Path.Combine(directory, name) };
        return paths.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Target build artifact is missing: " + name);
    }

    public async Task<ProcessResult> RunAsync(ProjectEntry project, EngineEntry engine, CancellationToken cancellation, bool openBrowser = true, string? deviceSerial = null)
    {
        var locked = StateStore.ReadProject(project.Path);
        if (locked.Platform != project.Platform || locked.Configuration != project.Configuration || locked.Version != engine.Version || locked.Channel != engine.Channel)
            throw new InvalidOperationException("Project engine/target lock changed. Reopen the project.");
        var target = BuildTargets.Get(project.Platform);
        if (!target.CanBuildOn(BuildTargets.Host)) throw new PlatformNotSupportedException("Run requires the matching build host.");
        var stamp = Path.Combine(BuildTargets.BuildDirectory(project), ".hub-build-complete.json");
        if (!File.Exists(stamp) || JsonSerializer.Deserialize<string>(File.ReadAllText(stamp)) != ProjectService.EngineInstallationToken(engine))
            throw new InvalidOperationException("Build this target successfully before Run.");
        if (target.Family == "android")
        {
            if (string.IsNullOrEmpty(deviceSerial)) throw new InvalidOperationException("Select an authorized Android device before Run. CLI: deploy <data-root> <project> <serial>.");
            return await new AndroidDeviceService(runner, toolsRoot).DeployAsync(project, deviceSerial, PrepareEnvironment(CreateEnvironment(engine, target)), cancellation);
        }
        if (target.Family == "uwp" || target.Family is "ios" or "tvos")
            throw new InvalidOperationException("This target requires packaging/deployment and a selected device or simulator. Open the target output; native library/build is not a deployed application.");
        var artifact = FindArtifact(project);
        var environment = PrepareEnvironment(CreateEnvironment(engine, target));
        if (target.Family == "wasm")
        {
            // emrun 官方提供 wasm MIME 与 COOP/COEP；浏览器由操作系统打开，取消只停止自己的服务。
            using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            socket.Start(); var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            var url = $"http://127.0.0.1:{port}/{Uri.EscapeDataString(Path.GetFileName(artifact))}";
            var server = runner.RunAsync(Python, ["-u", Path.Combine(Emscripten, "emrun.py"), "--no-browser", "--hostname", "127.0.0.1", "--port", port.ToString(), artifact], Path.GetDirectoryName(artifact)!, environment, serverStop.Token, TimeSpan.FromDays(1));
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var expected = await File.ReadAllBytesAsync(artifact, cancellation);
                var ready = false;
                for (var attempt = 0; attempt < 40 && !server.IsCompleted; attempt++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try { ready = (await http.GetByteArrayAsync(url, cancellation)).SequenceEqual(expected); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested) { }
                    if (ready) break;
                    await Task.Delay(250, cancellation);
                }
                if (!ready)
                {
                    if (server.IsCompleted) { var failure = await server; throw new IOException($"Web server exited: {failure.ExitCode}\n{failure.Error}"); }
                    throw new TimeoutException("Web preview server did not become ready.");
                }
                runner.Write("Web preview ready: " + url);
                if (openBrowser) runner.Open(url);
                return await server;
            }
            finally
            {
                serverStop.Cancel();
                try { await server; } catch (OperationCanceledException) { }
            }
        }
        return await runner.RunAsync(artifact, [], Path.GetDirectoryName(artifact)!, environment, cancellation, TimeSpan.FromDays(1));
    }
}
