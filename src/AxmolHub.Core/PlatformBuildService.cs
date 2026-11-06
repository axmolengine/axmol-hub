namespace AxmolHub.Core;

/// <summary>An external command to execute (still used to describe gradle/keytool and similar commands on the Android packaging side).</summary>
public sealed record BuildCommand(string Executable, string[] Arguments, string WorkingDirectory);

/// <summary>
/// What happens after the engine has finished building: run, deploy, local WebAssembly preview, and artifact
/// discovery.
///
/// **The build itself has been delegated to the engine** (<c>axmol build</c>) — this no longer assembles CMake
/// commands, nor hard-codes NDK / gradle / build-tools versions in C# (the source of truth for those versions
/// is <see cref="BuildProfile"/>, and installation is handled by the official <c>setup.ps1</c>).
///
/// The retained <b>auxiliary</b> environment construction is only for tools the Hub still invokes directly
/// (<c>adb</c>, <c>keytool</c>, <c>emrun</c>), sourced from the **tool directories inside the engine tree**,
/// not a Hub-managed installation.
/// </summary>
public sealed class PlatformBuildService(ProcessRunner runner)
{
    /// <summary>
    /// The engine command this target will execute (used by both the <c>plan</c> verb and logging).
    /// **Shares** <see cref="ProjectBuildOptions"/> with <c>ProjectService.BuildAsync</c>, so the <c>-xc</c>
    /// that plan reports is exactly the one build will actually use (including the prebuilt-library option).
    /// </summary>
    public static AxmolInvocation Plan(ProjectEntry project, EngineEntry engine, bool configureOnly, EnginePrebuiltState prebuiltState)
        => ProjectBuildOptions.Invocation(project, engine, configureOnly, prebuiltState);

    /// <summary>
    /// The auxiliary tool environment: inherit the parent process, then prepend the tool directories inside
    /// the engine tree. **Not used for builds** (the engine prepares its own build environment); only for the
    /// adb/keytool/emrun that the Hub invokes directly.
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

        // Two levels up from the tool root is the engine tree (<engine>/tools/external).
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

        // Callers directly CreateDirectory(environment["HOME"]), so both of these must exist.
        foreach (var name in new[] { "HOME", "TEMP" })
        {
            if (!environment.TryGetValue(name, out var value) || value.Length == 0)
            {
                environment[name] = name == "HOME"
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Path.GetTempPath();
            }
        }

        var existing = HostEnvironment.PathValue;
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
        // The Android artifact is the Gradle-produced APK, which lands in the engine's build directory — do a
        // controlled discovery, and don't allow the native library (lib<App>.so) to masquerade as the artifact.
        // Prefer the current configuration.
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

        if (target.Family == "wasm")
        {
            // A caller that wants an answer rather than a handle: serve, wait for the session to end, then
            // take the server down. The shell does not come here — it holds the preview open with
            // <see cref="StartWebPreviewAsync"/> and stops it from the 运行 button.
            var preview = await StartWebPreviewAsync(project, engine, openBrowser, cancellation);
            try
            {
                return await preview.Server;
            }
            finally
            {
                StopWebPreview(preview);
                preview.Stop.Dispose();
            }
        }

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (target.Family == "android")
        {
            if (string.IsNullOrEmpty(deviceSerial)) throw new InvalidOperationException("Select an authorized Android device before Run. CLI: deploy <data-root> <project> <serial>.");
            // The engine's deploy goes through adb; device selection follows adb's own environment-variable convention.
            overrides["ANDROID_SERIAL"] = deviceSerial;
        }

        var invocation = target.Family == "android"
            ? AxmolCommandMap.Deploy(target, project.Path, project.Configuration)
            : AxmolCommandMap.Run(target, project.Path, project.Configuration);
        return await commandLine.RunAsync(engine, invocation, project.Path, cancellation, environment: overrides);
    }

    /// <summary>
    /// Local WebAssembly preview. Deliberately does **not** go through <c>axmol run</c>: the engine's run
    /// launches the browser directly, whereas the Hub's <c>serve</c> wants "start the server → print the
    /// address → wait for readiness → let the caller decide whether to open". Aside from that, it uses the
    /// engine tree's emsdk.
    /// </summary>
    /// <summary>
    /// A local wasm server Hub is running on somebody else's behalf. The stop handle travels with it because
    /// the request outlives this call: <c>serve my wasm build</c> is not a command that finishes, and a
    /// <c>using</c> on the token source disposed the moment the method returned, leaving the browser pointed at
    /// a server nobody could stop. Whoever holds this owns killing it — see <see cref="StopWebPreview"/>.
    /// </summary>
    public sealed record WebPreview(Task<ProcessResult> Server, string Url, CancellationTokenSource Stop);

    /// <summary>
    /// Start the local WebAssembly preview and hand back the handle as soon as the server has proven it can
    /// serve the artifact. Deliberately does **not** go through <c>axmol run</c>: the engine's run launches the
    /// browser directly, whereas the Hub's <c>serve</c> wants "start the server → print the address → wait for
    /// readiness → let the caller decide whether to open". Aside from that it uses the engine tree's emsdk.
    /// </summary>
    public async Task<WebPreview> StartWebPreviewAsync(ProjectEntry project, EngineEntry engine, bool openBrowser, CancellationToken cancellation)
    {
        await HostEnvironment.EnsureAsync(cancellation).ConfigureAwait(false);
        var artifact = FindArtifact(project);
        var emscripten = Path.Combine(EngineToolchain.ToolRoot(engine), "emsdk", "upstream", "emscripten");
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var environment = CreateEnvironment(engine, BuildTargets.Get(project.Platform));

        // emrun officially provides the wasm MIME type and COOP/COEP; the browser is opened by the OS, and cancel only stops our own server.
        var serverStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
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
            return new WebPreview(server, url, serverStop);
        }
        catch
        {
            // Nothing was handed back, so nobody else can reach this server. It must not stay running.
            serverStop.Cancel();
            serverStop.Dispose();
            throw;
        }
    }

    /// <summary>Stop a preview this service started. Idempotent: the button that calls it may be pressed twice,
    /// and a second <c>Cancel</c> on a disposed source must not look like a failed shutdown.</summary>
    public static void StopWebPreview(WebPreview preview)
    {
        try
        {
            preview.Stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped by whoever got here first.
        }
    }
}
