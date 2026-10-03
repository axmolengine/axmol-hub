namespace AxmolHub.Core;

/// <summary>一条要执行的外部命令（Android 打包侧的 gradle/keytool 等仍用它描述命令）。</summary>
public sealed record BuildCommand(string Executable, string[] Arguments, string WorkingDirectory);

/// <summary>
/// 引擎已完成构建之后的事情：运行、部署、WebAssembly 本地预览，以及产物查找。
///
/// **构建本身已委派给引擎**（<c>axmol build</c>）—— 这里不再拼 CMake 命令、
/// 不再在 C# 里硬编码 NDK / gradle / build-tools 版本（那些版本的真源是
/// <see cref="BuildProfile"/>，安装由官方 <c>setup.ps1</c> 负责）。
///
/// 保留的<b>辅助</b>环境构造：仅用于 Hub 自己还要直接调的工具（<c>adb</c>、<c>keytool</c>、
/// <c>emrun</c>），来源是**引擎树内的工具目录**，不是 Hub 自持安装。
/// </summary>
public sealed class PlatformBuildService(ProcessRunner runner)
{
    /// <summary>
    /// 这个目标会执行的引擎命令（<c>plan</c> 动词与日志都用它）。
    /// 与 <c>ProjectService.BuildAsync</c> **共用** <see cref="ProjectBuildOptions"/>，
    /// 所以 plan 报出的 <c>-xc</c> 就是 build 实际会用的那一份（含日志捕获补丁与预编译库选项）。
    /// </summary>
    public static AxmolInvocation Plan(ProjectEntry project, EngineEntry engine, bool configureOnly, EnginePrebuiltState prebuiltState)
    {
        var target = BuildTargets.Get(project.Platform);
        return AxmolCommandMap.Build(target, project.Path, project.Configuration, configureOnly,
            ProjectBuildOptions.CmakeOptions(project, engine, target, prepareFiles: false, prebuiltState));
    }

    /// <summary>
    /// 辅助工具环境：继承父进程，再前置引擎树里的工具目录。
    /// **不用于构建**（构建由引擎自己准备环境）；只给 Hub 直接调用的 adb/keytool/emrun 用。
    /// </summary>
    public Dictionary<string, string> CreateEnvironment(EngineEntry engine, BuildTarget target)
        => CreateEnvironment(EngineToolchain.ToolRoot(engine), target);

    /// <inheritdoc cref="CreateEnvironment(EngineEntry, BuildTarget)"/>
    public Dictionary<string, string> CreateEnvironment(string toolRoot, BuildTarget target)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[entry.Key.ToString()!] = entry.Value?.ToString() ?? "";
        }

        // 工具根的上一级上两级就是引擎树（<engine>/tools/external）。
        var engineRoot = Path.GetFullPath(Path.Combine(toolRoot, "..", ".."));
        environment["AX_ROOT"] = engineRoot;
        environment["AXMOL_ROOT"] = engineRoot;

        var root = toolRoot;
        var paths = new List<string>();
        void AddPath(params string[] parts)
        {
            var path = Path.Combine(parts);
            if (Directory.Exists(path)) paths.Add(path);
        }

        AddPath(root, "cmake", "bin");
        AddPath(root, "ninja");
        AddPath(root, "axslcc", "bin");
        AddPath(root, "nuget");
        AddPath(root, "LLVM", "bin");
        AddPath(engineRoot, "tools", "cmdline");

        if (target.Family == "android")
        {
            var sdk = Path.Combine(root, "adt", "sdk");
            var jdk = Path.Combine(root, "jdk");
            environment["ANDROID_HOME"] = sdk;
            environment["ANDROID_SDK_ROOT"] = sdk;
            environment["JAVA_HOME"] = jdk;
            AddPath(jdk, "bin");
            AddPath(sdk, "platform-tools");
            AddPath(sdk, "cmdline-tools", "latest", "bin");
            if (NewestNdk(sdk) is { } ndk)
            {
                environment["ANDROID_NDK_ROOT"] = environment["ANDROID_NDK_HOME"] = ndk;
            }
        }

        if (target.Family == "wasm")
        {
            var emsdk = Path.Combine(root, "emsdk");
            environment["EMSDK"] = emsdk;
            AddPath(emsdk, "upstream", "emscripten");
            AddPath(emsdk, "upstream", "bin");
        }

        // 调用方会直接 CreateDirectory(environment["HOME"])，所以这两项必须存在。
        foreach (var name in new[] { "HOME", "TEMP" })
        {
            if (!environment.TryGetValue(name, out var value) || value.Length == 0)
            {
                environment[name] = name == "HOME"
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Path.GetTempPath();
            }
        }

        var existing = environment.TryGetValue("PATH", out var path) ? path : "";
        environment["PATH"] = string.Join(Path.PathSeparator, paths.Concat(existing.Split(Path.PathSeparator).Where(part => part.Length > 0)));
        return environment;
    }

    private static string? NewestNdk(string sdk)
    {
        var parent = Path.Combine(sdk, "ndk");
        return Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).OrderDescending().FirstOrDefault()
            : null;
    }

    public static string FindArtifact(ProjectEntry project)
    {
        var target = BuildTargets.Get(project.Platform);
        var build = ProjectService.BuildDirectory(project);
        // Android 的产物是 Gradle 产出的 APK，落在引擎的构建目录里 —— 做一次受控发现，
        // 不允许原生库（lib<App>.so）冒充产物。优先当前配置。
        if (target.Family == "android")
        {
            var apks = Directory.EnumerateFiles(build, "*.apk", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
            return apks.FirstOrDefault(path => path.Contains(project.Configuration, StringComparison.OrdinalIgnoreCase))
                ?? apks.FirstOrDefault()
                ?? throw new FileNotFoundException($"Android APK is missing for {project.Name}. Build this target first.");
        }

        return EngineBuildLayout.FindArtifact(build, project.Name, project.Configuration, target.Family)
            ?? throw new FileNotFoundException($"Target build artifact is missing: {project.Name} ({project.Platform}/{project.Configuration}).");
    }

    public async Task<ProcessResult> RunAsync(ProjectEntry project, EngineEntry engine, EngineCommandLine commandLine,
        CancellationToken cancellation = default, bool openBrowser = true, string? deviceSerial = null)
    {
        var target = BuildTargets.Get(project.Platform);
        if (!target.CanBuildOn(BuildTargets.Host)) throw new PlatformNotSupportedException("Run requires the matching build host.");
        ProjectService.ValidateBuildReceipt(project, engine);

        if (target.Family == "wasm") return await PreviewWebAsync(project, engine, openBrowser, cancellation);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (target.Family == "android")
        {
            if (string.IsNullOrEmpty(deviceSerial)) throw new InvalidOperationException("Select an authorized Android device before Run. CLI: deploy <data-root> <project> <serial>.");
            // 引擎的 deploy 走 adb，设备选择沿用 adb 自己的环境变量约定。
            overrides["ANDROID_SERIAL"] = deviceSerial;
        }

        var invocation = target.Family == "android"
            ? AxmolCommandMap.Deploy(target, project.Path, project.Configuration)
            : AxmolCommandMap.Run(target, project.Path, project.Configuration);
        return await commandLine.RunAsync(engine, invocation, project.Path, cancellation, environment: overrides);
    }

    /// <summary>
    /// WebAssembly 本地预览。刻意**不走** <c>axmol run</c>：引擎的 run 会直接拉起浏览器，
    /// 而 Hub 的 <c>serve</c> 要的是「起服务 → 打印地址 → 等就绪 → 由调用方决定是否打开」。
    /// 除这一点外都用引擎树的 emsdk。
    /// </summary>
    private async Task<ProcessResult> PreviewWebAsync(ProjectEntry project, EngineEntry engine, bool openBrowser, CancellationToken cancellation)
    {
        var artifact = FindArtifact(project);
        var emscripten = Path.Combine(EngineToolchain.ToolRoot(engine), "emsdk", "upstream", "emscripten");
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var environment = CreateEnvironment(engine, BuildTargets.Get(project.Platform));

        // emrun 官方提供 wasm MIME 与 COOP/COEP；浏览器由操作系统打开，取消只停止自己的服务。
        using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var url = $"http://127.0.0.1:{port}/{Uri.EscapeDataString(Path.GetFileName(artifact))}";
        var server = runner.RunAsync(python, ["-u", Path.Combine(emscripten, "emrun.py"), "--no-browser", "--hostname", "127.0.0.1", "--port", port.ToString(), artifact],
            Path.GetDirectoryName(artifact)!, environment, serverStop.Token, ProcessRunner.Infinite);
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
}
