using AxmolHub.Core;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// --json 是**全局**标志，可出现在任意位置。必须先摘掉它再按索引取参数，
// 否则 `create <root> <name> <parent> --json` 会让索引错位。
// 契约：docs/cli-json-contract.md
var json = Array.Exists(args, argument => argument == "--json");
if (json)
{
    args = Array.FindAll(args, argument => argument != "--json");
}

var command = args.Length > 0 ? args[0] : "help";

try
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await RunAsync(args, command, json, stop);
}
catch (OperationCanceledException ex)
{
    return Fail("Cancelled.", 130, ex);
}
catch (Exception ex)
{
    return Fail(ex.ToString(), 1, ex);
}

// 失败也必须给 JSON，否则消费方就得去解析 stderr —— 那正是这份契约要消除的东西。
int Fail(string humanMessage, int exitCode, Exception exception)
{
    if (json)
    {
        Console.WriteLine(CliContract.Encode(command, false, exitCode, error: CliContract.Describe(exception)));
    }

    Console.Error.WriteLine(humanMessage);
    return exitCode;
}

async Task<int> RunAsync(string[] arguments, string verb, bool asJson, CancellationTokenSource stop)
{
    // 统一出口：JSON 模式下由这里写出唯一一份信封；人读模式下各分支自己写文本。
    int Emit(bool ok, int exitCode, object? data = null)
    {
        if (asJson)
        {
            Console.WriteLine(CliContract.Encode(verb, ok, exitCode, data));
        }

        return exitCode;
    }

    const string help = "Axmol Hub CLI\n  targets\n  verify <data-root> <target>\n  create <data-root> <name> <parent> [cpp|lua] (legacy: <target> [cpp|lua])\n  select <data-root> <project> <target>\n  plan|configure|build <data-root> <project> [Debug|Release]\n  run|serve <data-root> <project>\n  devices <data-root> <android-target>\n  deploy <data-root> <project> <serial>\n  install-tools <data-root> <manifest>\nAll tools must be managed inside data-root/tools. No system developer tool fallback.";

    if (arguments.Length == 0 || verb == "help")
    {
        // 这条必须也判 asJson：help 文本打头会让 stdout 变成"文本 + JSON"，
        // 严格解析器立刻失败 —— 契约 §3 第 1 条。第一次跑就是在这里踩到的。
        if (!asJson)
        {
            Console.WriteLine(help);
        }

        return Emit(true, 0, new CommandsPayload(
        [
            "targets", "verify", "create", "select", "plan", "configure", "build",
            "run", "serve", "devices", "deploy", "install-tools",
        ]));
    }

    if (verb == "targets")
    {
        var descriptors = BuildTargets.All
            .Select(target => new TargetDescriptor(
                target.Id, target.Name, target.Family, target.Architecture,
                target.Hosts, target.Simulator, target.CanBuildOn(BuildTargets.Host)))
            .ToArray();

        if (!asJson)
        {
            foreach (var target in BuildTargets.All)
            {
                Console.WriteLine($"{target.Id,-24} {target.Name,-26} host={string.Join('/', target.Hosts)} current={target.CanBuildOn(BuildTargets.Host)}");
            }
        }

        return Emit(true, 0, new TargetsPayload(descriptors));
    }

    if (verb == "select" && arguments.Length == 4)
    {
        var projectStore = new StateStore(arguments[1]);
        var project = StateStore.ReadProject(arguments[2]);
        BuildTargets.Select(project, arguments[3]);
        projectStore.SaveProject(project);
        if (!asJson)
        {
            Console.WriteLine($"Selected: {project.Platform}");
        }

        return Emit(true, 0, new ProjectPayload(project));
    }

    if (arguments.Length < 3)
    {
        throw new ArgumentException("Missing arguments. Run help.");
    }

    var store = new StateStore(arguments[1]);
    Directory.CreateDirectory(Path.Combine(store.Root, "tools"));
    var log = new HubLog(Path.Combine(store.Root, "logs"));
    void Write(string message) { Console.Error.WriteLine(message); log.Write(message); }
    var runner = new ProcessRunner(Write);
    var tools = Path.Combine(store.Root, "tools");
    var platform = new PlatformBuildService(runner, tools);
    var detector = new ToolchainDetector(runner, tools);

    if (verb == "create" && arguments.Length is >= 4 and <= 6)
    {
        // 保留旧版显式目标参数，新建流程可只指定脚本方式，目标在构建前选择。
        var target = arguments.Length > 4 && arguments[4] is not ("cpp" or "lua") ? arguments[4] : null;
        if (target != null) BuildTargets.Get(target);
        var projectType = arguments.Length == 6 ? arguments[5] : arguments.Length == 5 && target == null ? arguments[4] : "cpp";
        var hub = store.Load();
        var selected = hub.Engines.FirstOrDefault(e => e.Path == hub.DefaultEnginePath) ?? throw new InvalidOperationException("Set a default engine in this data root first.");
        var creator = new ProjectService(runner, detector, tools, Path.Combine(AppContext.BaseDirectory, "Invoke-Axmol.ps1"));
        var project = await creator.CreateAsync(arguments[2], arguments[3], selected, stop.Token, projectType);
        if (target != null) BuildTargets.Select(project, target);
        store.SaveProject(project);
        if (!asJson)
        {
            Console.WriteLine("Created: " + project.Path);
        }

        return Emit(true, 0, new ProjectPayload(project));
    }

    if (verb == "install-tools")
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var installer = new PackageInstaller(new DownloadManager(http, Write), store.Root, Write);
        var installed = new List<string>();
        var retained = new List<string>();
        foreach (var package in PackageManifest.Read(arguments[2]).Packages)
        {
            var destination = PackageInstaller.SafePath(store.Root, package.Destination);
            if (!Directory.Exists(destination))
            {
                await installer.InstallAsync(package, cancellation: stop.Token);
                installed.Add(destination);
            }
            else
            {
                Write("Existing installation retained: " + destination);
                retained.Add(destination);
            }
        }

        return Emit(true, 0, new InstallToolsPayload(installed, retained));
    }

    if (verb == "verify")
    {
        var rows = arguments[2] == "windows-x64" ? await detector.DetectAsync(stop.Token) : await platform.VerifyAsync(arguments[2], stop.Token);
        if (!asJson)
        {
            foreach (var row in rows) Console.WriteLine($"{row.Name}: {row.Status}\n{row.Details}");
        }

        // "有组件缺失"是**数据**不是异常：ok:false + exitCode:2，但 data 仍然给出清单。
        var broken = rows.Any(row => row.Status is ComponentStatus.Missing or ComponentStatus.Broken);
        var payload = new VerifyPayload(
            arguments[2],
            rows.Select(row => new ComponentDescriptor(row.Name, row.Status.ToString(), row.Details, row.Executable)).ToArray());
        return Emit(!broken, broken ? 2 : 0, payload);
    }

    if (verb == "devices")
    {
        var target = BuildTargets.Get(arguments[2]);
        if (target.Family != "android") throw new InvalidOperationException("Device query requires an Android target.");
        var devices = await new AndroidDeviceService(runner, tools).DevicesAsync(platform.CreateEnvironment(new("", tools), target), stop.Token);
        if (!asJson)
        {
            foreach (var device in devices) Console.WriteLine($"{device.Serial}\t{device.State}\t{device.Details}");
        }

        return Emit(true, 0, new DevicesPayload(devices.Select(device => new DeviceDescriptor(device.Serial, device.State, device.Details)).ToArray()));
    }

    var entry = StateStore.ReadProject(arguments[2]);
    if (verb is "plan" or "configure" or "build" && arguments.Length > 3)
    {
        BuildConfigurations.Validate(arguments[3]);
        if (verb == "plan") entry.Configuration = arguments[3];
        else BuildTargets.Select(entry, entry.Platform, arguments[3]);
    }

    var state = store.Load();
    var engine = state.Engines.FirstOrDefault(e => e.Version == entry.Version && e.Channel == entry.Channel)
        ?? throw new InvalidOperationException("Required engine version/channel is not registered in this data root.");
    var service = new ProjectService(runner, detector, tools, Path.Combine(AppContext.BaseDirectory, "Invoke-Axmol.ps1"));
    switch (verb)
    {
        case "plan":
            var plan = platform.Plan(entry, engine, false, checkFiles: false);
            // 刻意保留：不带 --json 时 plan 仍输出**裸**的 plan JSON（既有约定）。
            // 见 docs/cli-json-contract.md §6，该不对称在 schema 2 退役。
            if (!asJson)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            return Emit(true, 0, new PlanPayload(plan));

        case "configure":
        case "build":
            if (entry.Platform == "windows-x64") await detector.DetectAsync(stop.Token);
            entry.BuildStatus = verb == "configure" ? "Configuring" : "Building"; store.SaveProject(entry);
            try
            {
                await service.BuildAsync(entry, engine, verb == "configure", stop.Token,
                    entry.Configuration == "Release" && BuildTargets.Get(entry.Platform).Family == "android" ? AndroidSigningPasswords.FromEnvironment() : null);
                entry.BuildStatus = verb == "configure" ? "Configured" : "Succeeded";
            }
            catch { entry.BuildStatus = "Failed"; throw; }
            finally { store.SaveProject(entry); }

            var executable = verb == "configure" ? null : service.FindExecutable(entry);
            if (!asJson)
            {
                Console.WriteLine(verb == "configure" ? "Configured." : "Built: " + executable);
            }

            return Emit(true, 0, new BuildPayload(entry, entry.BuildStatus, executable));

        case "run":
            if (entry.Platform == "windows-x64") await detector.DetectAsync(stop.Token);
            // 退出码故意就是被拉起程序的退出码，所以 ok:true 与 exitCode 可以不同向。
            var runExit = (await service.RunAsync(entry, engine, stop.Token)).ExitCode;
            return Emit(true, runExit, new ChildExitPayload(runExit));

        case "serve":
            if (entry.Platform != "wasm32") throw new InvalidOperationException("serve is only available for WebAssembly.");
            var serveExit = (await platform.RunAsync(entry, engine, stop.Token, openBrowser: false)).ExitCode;
            return Emit(true, serveExit, new ChildExitPayload(serveExit));

        case "deploy":
            if (arguments.Length != 4 || BuildTargets.Get(entry.Platform).Family != "android") throw new ArgumentException("deploy <data-root> <Android-project> <serial>");
            var deployExit = (await platform.RunAsync(entry, engine, stop.Token, deviceSerial: arguments[3])).ExitCode;
            return Emit(true, deployExit, new ChildExitPayload(deployExit));

        default:
            throw new ArgumentException("Unknown command. Run help.");
    }
}
