using System.Text.Json;
using AxmolHub.Core;
using static CheckSupport;

internal static class EngineUpgradeChecks
{
    internal static async Task RunAsync(string root)
    {
        var fixture = Path.Combine(root, "engine-upgrade-" + Guid.NewGuid().ToString("N"));
        var engineRoot = Path.Combine(fixture, "engine");
        var projectRoot = Path.Combine(fixture, "project");
        Directory.CreateDirectory(Path.Combine(engineRoot, "core"));
        Directory.CreateDirectory(Path.Combine(engineRoot, "tools", "cmdline"));
        Directory.CreateDirectory(Path.Combine(engineRoot, "templates", "cpp"));
        Directory.CreateDirectory(Path.Combine(engineRoot, "1k"));
        File.WriteAllText(Path.Combine(engineRoot, "core", "axmolver.h.in"), "#define AX_VERSION_MAJOR 2\n#define AX_VERSION_MINOR 11\n#define AX_VERSION_PATCH 10\n");
        File.WriteAllText(Path.Combine(engineRoot, "tools", "cmdline", "axmol.ps1"), "");
        File.WriteAllText(Path.Combine(engineRoot, "templates", "cpp", "axproj-template.json"), "{}");
        File.WriteAllText(Path.Combine(engineRoot, "1k", "1kiss.ps1"), "");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".hub"));
        File.WriteAllText(Path.Combine(projectRoot, "CMakeLists.txt"), "# fixture\n");
        File.WriteAllText(Path.Combine(projectRoot, ".axproj"), "engine_version=2.10.0\r\nproject_type=cpp\r\ncustom=value\r\n");

        var engine = StateStore.ValidateEngine(engineRoot, "fixture");
        var project = new ProjectEntry
        {
            Name = "UpgradeFixture", Path = projectRoot, Version = "2.10.0", Channel = "local",
            Platform = "windows-x64", Configuration = "Release", ProjectType = "cpp", BuildStatus = "Succeeded",
        };
        StateStore.LockProject(project);
        var receipt = Path.Combine(projectRoot, ".hub", "build.json");
        var marker = Path.Combine(projectRoot, ".hub", "engine-upgrade.pending");
        File.WriteAllText(receipt, "{}");

        var restore = ProjectService.UpdateEngineBinding(project, engine);
        var profile = File.ReadAllText(Path.Combine(projectRoot, ".axproj"));
        var metadata = StateStore.ReadProject(projectRoot);
        Check(project.Version == "2.11.10" && metadata.Version == "2.11.10" && metadata.Channel == "fixture"
            && metadata.Platform == "windows-x64" && metadata.Configuration == "Release"
            && profile.Contains("engine_version=2.11.10\n") && profile.Contains("custom=value\n")
            && !File.Exists(receipt) && File.Exists(marker),
            "Engine upgrade updates binding, preserves settings, and invalidates the previous build");

        restore();
        Check(project.Version == "2.10.0" && StateStore.ReadProject(projectRoot).Version == "2.10.0"
            && File.ReadAllText(Path.Combine(projectRoot, ".axproj")).Contains("engine_version=2.10.0")
            && File.Exists(receipt) && !File.Exists(marker),
            "Engine upgrade rollback restores project metadata, profile and build receipt");

        var forcedBuild = AxmolCommandMap.Build(BuildTargets.Get("windows-x64"), projectRoot, "Debug", false, force: true);
        Check(forcedBuild.Arguments.Contains("-forceConfig"), "Upgraded projects force regeneration through the engine build option");
        Check(EngineReleases.CompareVersions("2.11.10", "2.11.9") > 0, "Engine upgrade versions compare numerically");
        // Exercise the real Base64 wrapper and the engine's two array-forwarding hops. A map-only
        // assertion misses switches becoming plain strings (or stray booleans) before plugin binding.
        File.WriteAllText(Path.Combine(engineRoot, "tools", "cmdline", "axmol.ps1"), """
            param([switch]$help, [switch]$wait)
            $Global:LASTEXITCODE = 0
            function axmol_build {
                $sub_args = $args
                . (Join-Path $PSScriptRoot 'build.ps1') @sub_args
            }
            $sub_args = $args[1..($args.Count - 1)]
            $plugin = @{ proc = ${function:axmol_build} }
            . $plugin.proc @sub_args
            exit $LASTEXITCODE
            """);
        File.WriteAllText(Path.Combine(engineRoot, "tools", "cmdline", "build.ps1"), """
            param([switch]$configOnly, [switch]$forceConfig)
            if ($env:HUB_FIXTURE_EXIT) { exit ([int]$env:HUB_FIXTURE_EXIT) }
            [Console]::WriteLine((ConvertTo-Json -Compress @{
                configOnly = [bool]$configOnly
                forceConfig = [bool]$forceConfig
                configuration = $(if ($args -contains '-O3') { 'Release' } elseif ($args -contains '-O0') { 'Debug' } else { 'RelWithDebInfo' })
                built = !$configOnly
                argv = @($args)
            }))
            """);
        var extra = ProjectBuildArguments.Parse(true, "-j 2 -xc '-DSEARCH_PATH=C:\\SDK path,-DQUOTED=\"value\"' -xb '--target,Hello3D'");
        var cli = EngineCli(new ProcessRunner(_ => { }));
        foreach (var configureOnly in new[] { false, true })
        foreach (var force in new[] { false, true })
        {
            var configuration = configureOnly ? "Release" : "Debug";
            var invocation = AxmolCommandMap.Build(BuildTargets.Get("wasm64"), projectRoot, configuration,
                configureOnly, force: force, additionalBuild: extra);
            var result = await cli.RunAsync(engine, invocation, projectRoot);
            using var output = JsonDocument.Parse(result.Output);
            var data = output.RootElement;
            var expected = invocation.Arguments.Where(arg => arg is not ("-configOnly" or "-forceConfig")).ToArray();
            Check(data.GetProperty("configOnly").GetBoolean() == configureOnly
                && data.GetProperty("forceConfig").GetBoolean() == force
                && data.GetProperty("built").GetBoolean() == !configureOnly
                && data.GetProperty("configuration").GetString() == configuration
                && data.GetProperty("argv").EnumerateArray().Select(arg => arg.GetString()).SequenceEqual(expected),
                $"Build wrapper binds mode switches and preserves optimization/advanced argv (configure={configureOnly}, force={force})");
        }
        try
        {
            await cli.RunAsync(engine, AxmolCommandMap.Build(BuildTargets.Get("wasm64"), projectRoot, "Release", false),
                projectRoot, environment: new Dictionary<string, string> { ["HUB_FIXTURE_EXIT"] = "7" });
            throw new Exception("Build wrapper lost the failing exit code.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("failed (exit 7)"))
        {
            Check(true, "Build wrapper preserves a real plugin failure");
        }
    }
}
