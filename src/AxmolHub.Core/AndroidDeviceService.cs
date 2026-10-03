using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed record AndroidDevice(string Serial, string State, string Details)
{
    public override string ToString() => Serial + " · " + State;
}

public sealed class AndroidDeviceService(ProcessRunner runner, string toolsRoot)
{
    private string Adb => Path.GetFullPath(Path.Combine(toolsRoot, "adt/sdk/platform-tools/adb" + (OperatingSystem.IsWindows() ? ".exe" : "")));
    public static IReadOnlyList<AndroidDevice> ParseDevices(string output)
        => output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("List of devices") && !line.StartsWith('*'))
            .Select(line => line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries)).Where(parts => parts.Length >= 2)
            .Select(parts => new AndroidDevice(parts[0], parts[1], parts.Length > 2 ? parts[2] : "")).ToArray();
    private static void ValidateSerial(string serial)
    { if (!Regex.IsMatch(serial, @"^[A-Za-z0-9_.:-]+$")) throw new ArgumentException("Invalid Android device serial."); }
    private async Task<T> WithServerAsync<T>(Dictionary<string, string> environment, CancellationToken cancellation, Func<int, Task<T>> action)
    {
        if (!File.Exists(Adb)) throw new FileNotFoundException("Install the managed Android platform tools first.", Adb);
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Directory.CreateDirectory(environment["ANDROID_USER_HOME"]);
        Directory.CreateDirectory(environment["HOME"]); Directory.CreateDirectory(environment["TEMP"]);
        environment.Remove("ADB_SERVER_SOCKET"); environment.Remove("ANDROID_ADB_SERVER_PORT");
        // Use a dedicated port and private authorization key, so the user's existing 5037 service is left untouched.
        var started = false;
        try
        {
            started = true;
            var start = await runner.RunAsync(Adb, ["-P", port.ToString(), "start-server"], toolsRoot, environment, cancellation, TimeSpan.FromSeconds(30));
            if (start.ExitCode != 0) throw new InvalidOperationException("Managed ADB server failed: " + start.Error);
            return await action(port);
        }
        finally
        {
            if (started)
            {
                try { await runner.RunAsync(Adb, ["-P", port.ToString(), "kill-server"], toolsRoot, environment, timeout: TimeSpan.FromSeconds(10)); }
                catch (Exception ex) { runner.Write("Managed ADB cleanup: " + ex.Message); }
            }
        }
    }
    public Task<IReadOnlyList<AndroidDevice>> DevicesAsync(Dictionary<string, string> environment, CancellationToken cancellation = default)
        => WithServerAsync(environment, cancellation, async port =>
        {
            var result = await runner.RunAsync(Adb, ["-P", port.ToString(), "devices", "-l"], toolsRoot, environment, cancellation, TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0) throw new InvalidOperationException("ADB device query failed: " + result.Error);
            return ParseDevices(result.Output);
        });
    public async Task<ProcessResult> DeployAsync(ProjectEntry project, string serial, Dictionary<string, string> environment, CancellationToken cancellation = default)
    {
        ValidateSerial(serial);
        var apk = await Task.Run(() => AndroidPackageService.VerifiedApk(project), cancellation); var component = AndroidPackageService.LaunchComponent(project);
        return await WithServerAsync(environment, cancellation, async port =>
        {
            async Task<ProcessResult> Invoke(params string[] arguments)
            {
                var result = await runner.RunAsync(Adb, ["-P", port.ToString(), "-s", serial, .. arguments], toolsRoot, environment, cancellation, TimeSpan.FromMinutes(5));
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
        });
    }
}
