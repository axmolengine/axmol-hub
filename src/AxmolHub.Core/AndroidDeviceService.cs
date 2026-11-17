using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed record AndroidDevice(string Serial, string State, string Details)
{
    public string DisplayName
    {
        get
        {
            var model = Details.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(part => part.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..].Replace('_', ' ');
            return string.IsNullOrWhiteSpace(model) ? Serial : model;
        }
    }

    public override string ToString() => Serial + " · " + State;
}

public sealed class AndroidDeviceService(ProcessRunner runner, string sdkRoot)
{
    public AndroidDeviceService(ProcessRunner runner, EngineEntry engine)
        : this(runner, EngineToolchain.ResolveAndroidSdkRoot(engine)) { }

    private string Adb => Path.GetFullPath(Path.Combine(sdkRoot,
        "platform-tools", "adb" + (OperatingSystem.IsWindows() ? ".exe" : "")));
    public static IReadOnlyList<AndroidDevice> ParseDevices(string output)
        => output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("List of devices") && !line.StartsWith('*'))
            .Select(line => line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries)).Where(parts => parts.Length >= 2)
            .Select(parts => new AndroidDevice(parts[0], parts[1], parts.Length > 2 ? parts[2] : "")).ToArray();
    private static void ValidateSerial(string serial)
    { if (!Regex.IsMatch(serial, @"^[A-Za-z0-9_.:-]+$")) throw new ArgumentException("Invalid Android device serial."); }
    private void RequireAdb()
    {
        runner.Write("Android ADB: " + Adb);
        if (!File.Exists(Adb)) throw new FileNotFoundException("Android platform tools were not found in the project's engine.", Adb);
    }

    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(Dictionary<string, string>? environment = null, CancellationToken cancellation = default)
    {
        RequireAdb();
        string Context(string name) => environment is null
            ? Environment.GetEnvironmentVariable(name) ?? "<unset>"
            : environment.GetValueOrDefault(name, "<unset>");
        runner.Write($"ADB context: ADB_SERVER_SOCKET={Context("ADB_SERVER_SOCKET")}; ANDROID_ADB_SERVER_PORT={Context("ANDROID_ADB_SERVER_PORT")}; HOME={Context("HOME")}; ANDROID_USER_HOME={Context("ANDROID_USER_HOME")}; ADB_VENDOR_KEYS={Context("ADB_VENDOR_KEYS")}");
        // Reuse the inherited server and authorization context, just like the engine's adb commands.
        var result = await runner.RunAsync(Adb, ["devices", "-l"], sdkRoot, environment, cancellation, TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0) throw new InvalidOperationException("ADB device query failed: " + result.Error + result.Output);
        var devices = ParseDevices(result.Output);
        runner.Write($"ADB query succeeded: {devices.Count} device(s), {devices.Count(device => device.State == "device")} authorized and online.");
        return devices;
    }
    public async Task<ProcessResult> DeployAsync(ProjectEntry project, string serial, Dictionary<string, string> environment, CancellationToken cancellation = default)
    {
        ValidateSerial(serial);
        var apk = await Task.Run(() => AndroidPackageService.VerifiedApk(project), cancellation); var component = AndroidPackageService.LaunchComponent(project);
        RequireAdb();
        async Task<ProcessResult> Invoke(params string[] arguments)
        {
            var result = await runner.RunAsync(Adb, ["-s", serial, .. arguments], sdkRoot, environment, cancellation, TimeSpan.FromMinutes(5));
            if (result.ExitCode != 0) throw new InvalidOperationException("Android device command failed: " + result.Error + result.Output);
            return result;
        }
        var state = await Invoke("get-state");
        if (state.Output.Trim() != "device") throw new InvalidOperationException("Selected Android device is not authorized or online.");
        var abi = await Invoke("shell", "getprop", "ro.product.cpu.abilist");
        if (!abi.Output.Trim().Split(',').Contains(BuildTargets.Get(project.Platform).Architecture)) throw new InvalidOperationException("Selected Android device does not support this APK ABI.");
        var install = await Invoke("install", "-r", apk);
        if (!install.Output.Contains("Success")) throw new InvalidOperationException("Android APK installation was not confirmed: " + install.Output);
        var launch = await Invoke("shell", "am", "start", "-W", "-n", component);
        if (!Regex.IsMatch(launch.Output, @"(?m)^Status:\s*ok\s*$")) throw new InvalidOperationException("Android activity launch was not confirmed: " + launch.Output);
        return launch;
    }
}
