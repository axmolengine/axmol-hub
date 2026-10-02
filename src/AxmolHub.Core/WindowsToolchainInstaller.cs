using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed record PreparedBuildTools(string Executable, string Destination, string[] Arguments);

public sealed class WindowsToolchainInstaller(DownloadManager downloads, ProcessRunner runner, string root,
    string manifestPath, string signatureScript, Action<string> log)
{
    public async Task<string> InstallSdkAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var sdk = document.RootElement.GetProperty("windowsSdk");
        var version = sdk.GetProperty("targetVersion").GetString()!;
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+\.\d+$")) throw new InvalidDataException("Invalid SDK target version.");
        var destination = PackageInstaller.SafePath(root, sdk.GetProperty("destination").GetString()!);
        if (Directory.Exists(destination))
        {
            VerifySdk(destination, version);
            log($"Managed SDK already installed: {destination}");
            return destination;
        }
        var common = await DownloadAsync(sdk.GetProperty("common"), progress, cancellation);
        var x64 = await DownloadAsync(sdk.GetProperty("x64"), progress, cancellation);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var commonFolder = Path.Combine(staging, "common");
            var x64Folder = Path.Combine(staging, "x64");
            await Task.Run(() =>
            {
                PackageInstaller.ExtractSafely(common, commonFolder, cancellation);
                PackageInstaller.ExtractSafely(x64, x64Folder, cancellation);
            }, cancellation);
            // SDK 官方 NuGet 将头文件/工具和架构库分包，按 Windows Kits 标准布局组合。
            var contents = Path.Combine(commonFolder, "c");
            Directory.CreateDirectory(Path.Combine(contents, "Lib"));
            Directory.Move(Path.Combine(x64Folder, "c"), Path.Combine(contents, "Lib", version));
            VerifySdk(contents, version);
            // 保留原包的 NuGet 元数据和许可链接；不重新分发微软文件。
            Directory.CreateDirectory(Path.Combine(contents, "package-metadata"));
            File.Copy(Path.Combine(commonFolder, "Microsoft.Windows.SDK.CPP.nuspec"), Path.Combine(contents, "package-metadata/common.nuspec"));
            File.Copy(Path.Combine(x64Folder, "Microsoft.Windows.SDK.CPP.x64.nuspec"), Path.Combine(contents, "package-metadata/x64.nuspec"));
            StateStore.WriteJson(Path.Combine(contents, ".hub-install.json"), new { id = "windows-sdk", version = sdk.GetProperty("version").GetString(), targetVersion = version,
                commonSha256 = sdk.GetProperty("common").GetProperty("sha256").GetString(), x64Sha256 = sdk.GetProperty("x64").GetProperty("sha256").GetString() });
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(contents, destination);
            log($"Managed Windows SDK installed: {destination}");
            return destination;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    public static void VerifySdk(string root, string version)
    {
        foreach (var file in new[] { $"Include/{version}/um/Windows.h", $"Include/{version}/ucrt/stdio.h", $"Lib/{version}/um/x64/kernel32.lib",
            $"Lib/{version}/ucrt/x64/ucrt.lib", $"bin/{version}/x64/rc.exe", $"bin/{version}/x64/mt.exe" })
            if (!File.Exists(Path.Combine(root, file))) throw new InvalidDataException($"Managed Windows SDK is incomplete: {file}");
    }

    public async Task<PreparedBuildTools> PrepareBuildToolsAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var tools = document.RootElement.GetProperty("windowsBuildTools");
        var destination = PackageInstaller.SafePath(root, tools.GetProperty("destination").GetString()!);
        var archive = await DownloadAsync(tools, progress, cancellation);
        var executable = Path.ChangeExtension(archive, ".exe");
        File.Copy(archive, executable, overwrite: true);
        var signature = await runner.RunAsync(ToolchainDetector.PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", signatureScript, "-InstallerPath", executable], root, cancellation: cancellation, timeout: TimeSpan.FromSeconds(30));
        if (signature.ExitCode != 0) throw new InvalidDataException($"Microsoft installer signature validation failed.\n{signature.Error}");
        var channel = tools.GetProperty("channelUri").GetString()!;
        if (!Uri.TryCreate(channel, UriKind.Absolute, out var channelUri) || channelUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Build Tools channel requires HTTPS.");
        var args = new List<string> { "--installPath", destination, "--channelUri", channel, "--passive", "--wait", "--norestart", "--nocache" };
        foreach (var component in tools.GetProperty("components").EnumerateArray())
        {
            var id = component.GetString()!;
            if (!Regex.IsMatch(id, @"^Microsoft\.[A-Za-z0-9_.]+$")) throw new InvalidDataException("Invalid Microsoft component id.");
            args.Add("--add"); args.Add(id);
        }
        var prepared = new PreparedBuildTools(executable, destination, args.ToArray());
        StateStore.WriteJson(Path.Combine(root, "cache/build-tools-install-plan.json"), prepared);
        log($"Microsoft Build Tools installation prepared: {destination}. Windows UAC is required; existing VS instances are not selected for modification.");
        return prepared;
    }

    public async Task<int> InstallBuildToolsAsync(PreparedBuildTools prepared, Action? installationStarted = null)
    {
        if (HasBuildTools(prepared.Destination))
        {
            RecordBuildTools(prepared.Destination, 0);
            log($"Managed MSVC already installed: {prepared.Destination}");
            return 0;
        }
        // 微软安装器运行后不强杀进程树；取消由微软安装界面处理，防止损坏安装状态。
        installationStarted?.Invoke();
        var exitCode = await runner.RunElevatedAsync(prepared.Executable, prepared.Arguments, root);
        if (exitCode is not (0 or 3010)) throw new InvalidOperationException($"Microsoft Build Tools installation failed (exit {exitCode}). Check Windows installer logs in %TEMP%/dd_*.log.");
        if (!HasBuildTools(prepared.Destination))
            throw new InvalidDataException("Microsoft installer finished but managed MSVC tools are missing.");
        RecordBuildTools(prepared.Destination, exitCode);
        log(exitCode == 3010 ? "Microsoft Build Tools installed; Windows restart required." : "Microsoft Build Tools installed.");
        return exitCode;
    }

    private void RecordBuildTools(string destination, int exitCode)
    {
        // 微软安装目录使用管理员 ACL；Hub 状态写入自己的可写目录，不改微软目录权限。
        StateStore.WriteJson(Path.Combine(root, "installations/msvc-v143.json"), new { id = "msvc-v143", path = destination, exitCode, installedAt = DateTimeOffset.Now });
    }
    private static bool HasBuildTools(string destination)
    {
        var toolsets = Path.Combine(destination, "VC/Tools/MSVC");
        return Directory.Exists(toolsets) && Directory.EnumerateDirectories(toolsets).Any(p =>
            new[] { "bin/Hostx64/x64/cl.exe", "bin/Hostx64/x64/link.exe", "bin/Hostx64/x64/lib.exe", "include/vcruntime.h", "lib/x64/libcmt.lib" }.All(f => File.Exists(Path.Combine(p, f))));
    }

    private Task<string> DownloadAsync(JsonElement package, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        => downloads.DownloadAsync(new Uri(package.GetProperty("url").GetString()!), package.GetProperty("sha256").GetString()!, Path.Combine(root, "cache"), progress, cancellation);
}
