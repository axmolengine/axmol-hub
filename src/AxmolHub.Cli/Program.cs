using System.Text.Json;
using AxmolHub.Core;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// 非 Windows 宿主的首个可执行入口；复用同一 Core，不复制平台构建逻辑。
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    if (args.Length == 0 || args[0] == "help")
    {
        Console.WriteLine("Axmol Hub CLI\n  targets\n  verify <data-root> <target>\n  create <data-root> <name> <parent> [cpp|lua] (legacy: <target> [cpp|lua])\n  select <data-root> <project> <target>\n  plan|configure|build <data-root> <project> [Debug|Release]\n  run|serve <data-root> <project>\n  devices <data-root> <android-target>\n  deploy <data-root> <project> <serial>\n  install-tools <data-root> <manifest>\nAll tools must be managed inside data-root/tools. No system developer tool fallback.");
        return 0;
    }
    if (args[0] == "targets")
    {
        foreach (var target in BuildTargets.All) Console.WriteLine($"{target.Id,-24} {target.Name,-26} host={string.Join('/', target.Hosts)} current={target.CanBuildOn(BuildTargets.Host)}");
        return 0;
    }
    if (args[0] == "select" && args.Length == 4)
    {
        var projectStore = new StateStore(args[1]);
        var project = StateStore.ReadProject(args[2]);
        BuildTargets.Select(project, args[3]);
        projectStore.SaveProject(project);
        Console.WriteLine($"Selected: {project.Platform}"); return 0;
    }
    if (args.Length < 3) throw new ArgumentException("Missing arguments. Run help.");
    var store = new StateStore(args[1]);
    Directory.CreateDirectory(Path.Combine(store.Root, "tools"));
    var log = new HubLog(Path.Combine(store.Root, "logs"));
    void Write(string message) { Console.Error.WriteLine(message); log.Write(message); }
    var runner = new ProcessRunner(Write);
    var tools = Path.Combine(store.Root, "tools");
    var platform = new PlatformBuildService(runner, tools);
    var detector = new ToolchainDetector(runner, tools);
    if (args[0] == "create" && args.Length is >= 4 and <= 6)
    {
        // 保留旧版显式目标参数，新建流程可只指定脚本方式，目标在构建前选择。
        var target = args.Length > 4 && args[4] is not ("cpp" or "lua") ? args[4] : null;
        if (target != null) BuildTargets.Get(target);
        var projectType = args.Length == 6 ? args[5] : args.Length == 5 && target == null ? args[4] : "cpp";
        var hub = store.Load();
        var selected = hub.Engines.FirstOrDefault(e => e.Path == hub.DefaultEnginePath) ?? throw new InvalidOperationException("Set a default engine in this data root first.");
        var creator = new ProjectService(runner, detector, tools, Path.Combine(AppContext.BaseDirectory, "Invoke-Axmol.ps1"));
        var project = await creator.CreateAsync(args[2], args[3], selected, stop.Token, projectType);
        if (target != null) BuildTargets.Select(project, target);
        store.SaveProject(project);
        Console.WriteLine("Created: " + project.Path); return 0;
    }
    if (args[0] == "install-tools")
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var installer = new PackageInstaller(new DownloadManager(http, Write), store.Root, Write);
        foreach (var package in PackageManifest.Read(args[2]).Packages)
        {
            var destination = PackageInstaller.SafePath(store.Root, package.Destination);
            if (!Directory.Exists(destination)) await installer.InstallAsync(package, cancellation: stop.Token);
            else Write("Existing installation retained: " + destination);
        }
        return 0;
    }
    if (args[0] == "verify")
    {
        var rows = args[2] == "windows-x64" ? await detector.DetectAsync(stop.Token) : await platform.VerifyAsync(args[2], stop.Token);
        foreach (var row in rows) Console.WriteLine($"{row.Name}: {row.Status}\n{row.Details}");
        return rows.Any(row => row.Status is ComponentStatus.Missing or ComponentStatus.Broken) ? 2 : 0;
    }
    if (args[0] == "devices")
    {
        var target = BuildTargets.Get(args[2]);
        if (target.Family != "android") throw new InvalidOperationException("Device query requires an Android target.");
        foreach (var device in await new AndroidDeviceService(runner, tools).DevicesAsync(platform.CreateEnvironment(new("", tools), target), stop.Token))
            Console.WriteLine($"{device.Serial}\t{device.State}\t{device.Details}");
        return 0;
    }
    var entry = StateStore.ReadProject(args[2]);
    if (args[0] is "plan" or "configure" or "build" && args.Length > 3)
    {
        BuildConfigurations.Validate(args[3]);
        if (args[0] == "plan") entry.Configuration = args[3];
        else BuildTargets.Select(entry, entry.Platform, args[3]);
    }
    var state = store.Load();
    var engine = state.Engines.FirstOrDefault(e => e.Version == entry.Version && e.Channel == entry.Channel)
        ?? throw new InvalidOperationException("Required engine version/channel is not registered in this data root.");
    var service = new ProjectService(runner, detector, tools, Path.Combine(AppContext.BaseDirectory, "Invoke-Axmol.ps1"));
    switch (args[0])
    {
        case "plan":
            Console.WriteLine(JsonSerializer.Serialize(platform.Plan(entry, engine, false, checkFiles: false), new JsonSerializerOptions { WriteIndented = true })); break;
        case "configure": case "build":
            if (entry.Platform == "windows-x64") await detector.DetectAsync(stop.Token);
            entry.BuildStatus = args[0] == "configure" ? "Configuring" : "Building"; store.SaveProject(entry);
            try
            {
                await service.BuildAsync(entry, engine, args[0] == "configure", stop.Token,
                    entry.Configuration == "Release" && BuildTargets.Get(entry.Platform).Family == "android" ? AndroidSigningPasswords.FromEnvironment() : null);
                entry.BuildStatus = args[0] == "configure" ? "Configured" : "Succeeded";
            }
            catch { entry.BuildStatus = "Failed"; throw; }
            finally { store.SaveProject(entry); }
            Console.WriteLine(args[0] == "configure" ? "Configured." : "Built: " + service.FindExecutable(entry)); break;
        case "run":
            if (entry.Platform == "windows-x64") await detector.DetectAsync(stop.Token);
            return (await service.RunAsync(entry, engine, stop.Token)).ExitCode;
        case "serve":
            if (entry.Platform != "wasm32") throw new InvalidOperationException("serve is only available for WebAssembly.");
            return (await platform.RunAsync(entry, engine, stop.Token, openBrowser: false)).ExitCode;
        case "deploy":
            if (args.Length != 4 || BuildTargets.Get(entry.Platform).Family != "android") throw new ArgumentException("deploy <data-root> <Android-project> <serial>");
            return (await platform.RunAsync(entry, engine, stop.Token, deviceSerial: args[3])).ExitCode;
        default: throw new ArgumentException("Unknown command. Run help.");
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
