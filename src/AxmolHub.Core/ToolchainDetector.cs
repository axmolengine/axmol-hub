namespace AxmolHub.Core;

public enum ComponentStatus { Unknown, Checking, Missing, Installing, Installed, Broken, UpdateAvailable }
public sealed record ToolchainComponent(string Name, ComponentStatus Status, string Details, string? Executable = null);

public sealed class ToolchainDetector(ProcessRunner runner, string toolsRoot)
{
    public string? VisualStudioPath { get; private set; }
    public string? CMakePath { get; private set; }
    public string? NinjaPath { get; private set; }
    public string? CompilerPath { get; private set; }
    public string? MsvcRoot { get; private set; }
    public string? SdkRoot { get; private set; }
    public string? SdkVersion { get; private set; }

    public async Task<List<ToolchainComponent>> DetectAsync(CancellationToken cancellation = default)
    {
        var result = new List<ToolchainComponent>();
        // Hub 只使用自己目录内的工具，不回退到系统 PATH 或其他 VS 实例。
        var vsRoot = Path.Combine(toolsRoot, "vs2022");
        var toolsets = Path.Combine(vsRoot, "VC/Tools/MSVC");
        MsvcRoot = Directory.Exists(toolsets) ? Directory.EnumerateDirectories(toolsets).OrderDescending().FirstOrDefault(p =>
            File.Exists(Path.Combine(p, "bin/Hostx64/x64/cl.exe")) && File.Exists(Path.Combine(p, "lib/x64/libcmt.lib"))) : null;
        VisualStudioPath = MsvcRoot == null ? null : vsRoot;
        CompilerPath = MsvcRoot == null ? null : Path.Combine(MsvcRoot, "bin/Hostx64/x64/cl.exe");
        result.Add(new("Managed MSVC v143", VisualStudioPath == null ? ComponentStatus.Missing : ComponentStatus.Installed, VisualStudioPath ?? $"Required: {vsRoot}. Use Microsoft Visual Studio Installer."));

        var sdkRoot = Path.Combine(toolsRoot, "windows-sdk");
        SdkRoot = sdkRoot;
        var sdkVersion = sdkRoot != null && Directory.Exists(Path.Combine(sdkRoot, "Lib"))
            ? Directory.EnumerateDirectories(Path.Combine(sdkRoot, "Lib")).OrderDescending().FirstOrDefault(path =>
                File.Exists(Path.Combine(path, "um/x64/kernel32.lib")) && File.Exists(Path.Combine(path, "ucrt/x64/ucrt.lib")) &&
                File.Exists(Path.Combine(sdkRoot, "Include", Path.GetFileName(path), "um/Windows.h")) &&
                File.Exists(Path.Combine(sdkRoot, "bin", Path.GetFileName(path), "x64/rc.exe"))) : null;
        SdkVersion = sdkVersion == null ? null : Path.GetFileName(sdkVersion);
        result.Add(new("Managed Windows SDK", sdkVersion == null ? ComponentStatus.Missing : ComponentStatus.Installed, sdkVersion ?? $"Required: {sdkRoot}. System SDK installations are excluded."));
        CMakePath = FindExecutable("cmake.exe", Path.Combine(toolsRoot, "cmake/bin"));
        NinjaPath = FindExecutable("ninja.exe", Path.Combine(toolsRoot, "ninja"));
        foreach (var (name, executable, arguments) in new[]
        {
            ("CMake", CMakePath, new[] { "--version" }), ("Ninja", NinjaPath, new[] { "--version" }),
            ("Git", FindExecutable("git.exe", Path.Combine(toolsRoot, "git/cmd")), new[] { "--version" }),
            ("Axmol shader compiler", FindExecutable("axslcc.exe", Path.Combine(toolsRoot, "axslcc")), new[] { "--version" }),
            ("NuGet", FindExecutable("nuget.exe", Path.Combine(toolsRoot, "nuget")), new[] { "help", "-ForceEnglishOutput" }),
            ("Python (optional for Windows C++)", FindExecutable("python.exe", Path.Combine(toolsRoot, "python")), new[] { "--version" })
        })
        {
            if (executable == null) { result.Add(new(name, ComponentStatus.Missing, "Executable not found.")); continue; }
            try
            {
                var probe = await runner.RunAsync(executable, arguments, toolsRoot, cancellation: cancellation, timeout: TimeSpan.FromSeconds(15));
                result.Add(new(name, probe.ExitCode == 0 ? ComponentStatus.Installed : ComponentStatus.Broken, (probe.Output + probe.Error).Trim() + $" — {executable}", executable));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { result.Add(new(name, ComponentStatus.Broken, ex.Message, executable)); }
        }
        return result;
    }

    public Dictionary<string, string> BuildEnvironment(EngineEntry engine)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sdkBin = SdkVersion == null ? "" : Path.Combine(SdkRoot!, "bin", SdkVersion, "x64");
        var paths = new[] { Path.GetDirectoryName(CompilerPath), Path.GetDirectoryName(CMakePath), Path.GetDirectoryName(NinjaPath),
            Path.Combine(toolsRoot, "git/cmd"), Path.Combine(toolsRoot, "git/mingw64/bin"), Path.Combine(toolsRoot, "axslcc"),
            Path.Combine(toolsRoot, "python"), Path.Combine(toolsRoot, "nuget"), sdkBin, Path.Combine(windows, "System32"), Path.GetDirectoryName(PowerShell), windows };
        var includes = MsvcRoot == null || SdkVersion == null ? "" : string.Join(";", new[] { Path.Combine(MsvcRoot, "include") }
            .Concat(new[] { "ucrt", "shared", "um", "winrt" }.Select(part => Path.Combine(SdkRoot!, "Include", SdkVersion, part))));
        var libraries = MsvcRoot == null || SdkVersion == null ? "" : string.Join(";", Path.Combine(MsvcRoot, "lib/x64"), Path.Combine(SdkRoot!, "Lib", SdkVersion, "ucrt/x64"), Path.Combine(SdkRoot!, "Lib", SdkVersion, "um/x64"));
        var profile = Path.GetFullPath(Path.Combine(toolsRoot, "../cache/build-profile"));
        var temporary = Path.Combine(profile, "temp");
        var roaming = Path.Combine(profile, "AppData/Roaming");
        var local = Path.Combine(profile, "AppData/Local");
        foreach (var directory in new[] { temporary, roaming, local }) Directory.CreateDirectory(directory);
        return new()
        {
            ["SystemRoot"] = windows, ["WINDIR"] = windows, ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["OS"] = "Windows_NT", ["PROCESSOR_ARCHITECTURE"] = "AMD64", ["COMSPEC"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["TEMP"] = temporary, ["TMP"] = temporary, ["USERPROFILE"] = profile, ["APPDATA"] = roaming, ["LOCALAPPDATA"] = local,
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ["VSLANG"] = "1033",
            ["AX_ROOT"] = engine.Path, ["AXMOL_ROOT"] = engine.Path, ["PATH"] = string.Join(";", paths.Where(p => !string.IsNullOrEmpty(p))),
            ["INCLUDE"] = includes, ["LIB"] = libraries, ["LIBPATH"] = libraries, ["CL"] = "", ["_CL_"] = "", ["LINK"] = "", ["_LINK_"] = "",
            ["WindowsSdkDir"] = SdkRoot ?? "", ["WindowsSDKVersion"] = SdkVersion == null ? "" : SdkVersion + "\\", ["VCToolsInstallDir"] = MsvcRoot ?? "",
            ["CMAKE_PREFIX_PATH"] = "", ["CMAKE_TOOLCHAIN_FILE"] = "", ["CMAKE_GENERATOR"] = "Ninja",
            ["NUGET_PACKAGES"] = Path.Combine(toolsRoot, "../cache/nuget-packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(toolsRoot, "../cache/nuget-http"),
            ["NUGET_SCRATCH"] = Path.Combine(toolsRoot, "../cache/nuget-scratch"),
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "NUL",
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "http.sslBackend", ["GIT_CONFIG_VALUE_0"] = "openssl"
        };
    }
    public static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
    public static string? FindExecutable(string name, params string?[] preferred)
    {
        foreach (var directory in preferred)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(path) && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }
}
