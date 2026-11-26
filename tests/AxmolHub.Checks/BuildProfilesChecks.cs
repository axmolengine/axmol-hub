using AxmolHub.Core;
using static CheckSupport;

internal static class BuildProfilesChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var flagIndex = Array.IndexOf(args, "--check-build-profiles");
        if (flagIndex + 1 >= args.Length || args[flagIndex + 1].StartsWith("--"))
            throw new ArgumentException("--check-build-profiles requires the path to a real Axmol engine tree.");
        var probeRoot = Path.GetFullPath(args[flagIndex + 1]);
        if (!Directory.Exists(probeRoot))
            throw new DirectoryNotFoundException(probeRoot);
        var probeEngine = StateStore.ValidateEngine(probeRoot);
        Check(File.Exists(BuildProfile.FileFor(probeRoot)), "Engine ships 1k/build.profiles as the tool version source of truth");
        var probeProfile = BuildProfile.Load(probeRoot);
        foreach (var key in new[]
        {
            "axslcc",
            "cmake",
            "ninja",
            "vs",
            "llvm",
            "jdk",
            "cmdlinetools",
            "ndk",
            "target_sdk",
            "min_sdk",
            "gradle",
            "agp",
            "buildtools",
            "emsdk"
        }

        )
            Check(probeProfile.Get(key)is { Length: > 0 }, $"build.profiles defines '{key}'");
        // The version source of truth must **track the engine version**: that is the core reason Hub no longer carries
        // its own versions.
        var probePinned = probeEngine.Version.StartsWith("3.") ? (Ndk: "r27d", TargetSdk: "37") : (Ndk: "r23d", TargetSdk: "36");
        Check(probeProfile.Ndk == probePinned.Ndk, $"Axmol {probeEngine.Version} pins NDK {probePinned.Ndk} (read {probeProfile.Ndk})");
        Check(probeProfile.TargetSdk == probePinned.TargetSdk, $"Axmol {probeEngine.Version} pins target_sdk {probePinned.TargetSdk} (read {probeProfile.TargetSdk})");
        var probeToolRoot = EngineToolchain.ToolRoot(probeEngine);
        Check(probeToolRoot.StartsWith(probeRoot, StringComparison.OrdinalIgnoreCase) && probeToolRoot.Contains("tools"), "Tool detection targets the engine tree (setup.ps1 tools/external), not a Hub data-root");
        // ---- Version-requirement semantics: must match 1kiss's find_prog (Hub saying "ready" = the engine will not go
        //      and install another copy) ----
        // `x~y+`: when it ends with '+' the engine **neutralises the interval's upper bound**, degrading to >= x.
        // Copy that exactly, no "fix it while we're here".
        var rangeWithPlus = ToolRequirement.Parse("4.2.0~4.4.3+");
        Check(rangeWithPlus.Satisfies("4.3.2") && rangeWithPlus.Satisfies("9.9.9") && !rangeWithPlus.Satisfies("4.1.9"), "A requirement ending in '+' degrades to a lower bound (the engine drops the range's upper bound)");
        // `x~y` (no +): a genuinely closed interval.
        var closedRange = ToolRequirement.Parse("17.0.10~17.0.20.1+".Replace("+", string.Empty));
        Check(closedRange.Satisfies("17.0.15") && closedRange.Satisfies("17.0.20") && !closedRange.Satisfies("17.0.21"), "A range without '+' is a closed interval");
        // `x.y.*`: wildcard (the engine uses PowerShell's -like).
        var wildcard = ToolRequirement.Parse("5.5.1.*");
        Check(wildcard.Satisfies("5.5.1.6542") && !wildcard.Satisfies("6.14.0.1"), "A wildcard requirement matches by prefix and rejects a newer major");
        // A bare version number means **string equality**, not numeric equality.
        var exact = ToolRequirement.Parse("22.0");
        Check(exact.Satisfies("22.0") && !exact.Satisfies("22.0.0") && !exact.Satisfies("9.0"), "A bare version requires exact string equality (22.0 does not accept 22.0.0)");
        // `17.9+`: a single-segment lower bound; VS's 4-segment versions must compare against it.
        var lowerBound = ToolRequirement.Parse("17.9+");
        Check(lowerBound.Satisfies("18.10.12224.181") && !lowerBound.Satisfies("17.8.0"), "A lower bound compares numeric segments (17.9+ accepts 18.10.x, rejects 17.8)");
        Check(ToolRequirement.Parse("19.0.0~19.1.7+").Satisfies("21.1.1"), "A lower bound accepts a much newer version");
        var probeTarget = OperatingSystem.IsWindows() ? "windows-x64" : OperatingSystem.IsMacOS() ? "macos-arm64" : "linux-x64";
        var probeToolchain = new EngineToolchain(new ProcessRunner(_ =>
        {
        }));
        var probeRows = await probeToolchain.InspectAsync(probeEngine, probeTarget);
        Check(probeRows.Count >= 3, $"Engine toolchain probe reports components for {probeTarget}");
        foreach (var name in new[]
        {
            "CMake",
            "Ninja",
            "axslcc"
        }

        )
            Check(probeRows.Any(row => row.Name == name), $"Probe covers {name}");
        Check(probeRows.All(row => row.Details.Length > 0), "Every probed component carries a status description");
        Check(probeRows.Where(row => row.Status == ComponentStatus.Installed).All(row => row.Executable is not null), "Reported installed components point at the executable that will actually be used");
        // axslcc is -mode BOTH in the engine (engine tree first), and this tree's axslcc version happens to satisfy the
        // requirement; so it must resolve inside the engine tree — this is what proves the "lookup order" was not
        // written on a hunch.
        var axslcc = probeRows.Single(row => row.Name == "axslcc");
        Check(axslcc.Status != ComponentStatus.Installed || axslcc.Executable!.StartsWith(probeToolRoot, StringComparison.OrdinalIgnoreCase), "Engine-first tools resolve inside the engine tree (axslcc uses -mode BOTH)");
        // NDK code name → the first two revision parts: r27d → 27.3 (major takes all the digits, minor = letter - 'a').
        // This is the bit arithmetic in the engine's setup_android_sdk that is easiest to copy wrong, so pin it alone.
        Check(EngineToolchain.NdkRevisionFor("r27d") == "27.3" && EngineToolchain.NdkRevisionFor("r23d") == "23.3" && EngineToolchain.NdkRevisionFor("r25") == "25.0" && EngineToolchain.NdkRevisionFor("r27") == "27.0", "NDK codenames map to the revision prefix the engine compares (r27d -> 27.3)");
        if (OperatingSystem.IsWindows())
            Check(probeRows.Any(row => row.Name == "Visual Studio"), "Visual Studio is detected (the engine detects it but never installs it)");
        Console.WriteLine($"{count} build.profile checks passed for Axmol {probeEngine.Version}.");
        return;
    }
}
