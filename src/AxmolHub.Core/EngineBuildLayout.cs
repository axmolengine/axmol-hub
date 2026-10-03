using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// 构建目录与产物位置。
///
/// **构建目录由引擎决定，不由 Hub 决定** —— 所以这里只「发现」，不假定名字。
/// 权威来源是引擎在工程目录里生成的 <c>run.bat</c>：它写着
/// <c>set BUILD_DIR=&lt;CMAKE_BUILD_RELATIVE_DIR&gt;</c>，即 <c>PROJECT_BINARY_DIR</c>
/// 相对工程目录的路径（见 <c>templates/common/run.bat.in</c> 与
/// <c>templates/common/cmake/modules/AXGamePlatformSetup.cmake</c>）。
/// 兜底才在工程目录下扫描 <c>build*</c>。
///
/// 产物口径同样是引擎的（<c>run.bat</c>）：<c>&lt;buildDir&gt;/bin/&lt;App&gt;/&lt;Config&gt;/&lt;App&gt;[.exe]</c>；
/// 实测引擎自身的构建（<c>&lt;engine&gt;/build</c>）也是这个形状。
/// </summary>
public static class EngineBuildLayout
{
    // `set BUILD_DIR=xxx`（Windows 模板）与 `BUILD_DIR=xxx`（Unix 模板）。
    private static readonly Regex BuildDirectory = new(@"(?i)^\s*(?:set\s+)?BUILD_DIR\s*=\s*(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>发现工程的构建目录；找不到返回 <c>null</c>（调用方决定这是不是错误）。</summary>
    public static string? FindBuildDirectory(ProjectEntry project)
    {
        if (DeclaredBuildDirectory(project.Path) is { } declared) return declared;

        if (!Directory.Exists(project.Path)) return null;
        var family = BuildTargets.Get(project.Platform).Family;
        var builds = Directory.EnumerateDirectories(project.Path)
            .Where(directory => Path.GetFileName(directory).StartsWith("build", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToArray();
        return builds.FirstOrDefault(build => FindArtifact(build, project.Name, project.Configuration, family) is not null)
            ?? builds.FirstOrDefault();
    }

    /// <summary>读引擎生成的 run 脚本里的 <c>BUILD_DIR</c>。</summary>
    private static string? DeclaredBuildDirectory(string projectPath)
    {
        foreach (var name in new[] { "run.bat", "run.sh" })
        {
            var file = Path.Combine(projectPath, name);
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file))
            {
                var match = BuildDirectory.Match(line);
                if (!match.Success) continue;
                var value = match.Groups[1].Value.Trim().Trim('"').TrimEnd('\\', '/');
                if (value.Length == 0) continue;
                var resolved = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectPath, value));
                if (Directory.Exists(resolved)) return resolved;
            }
        }

        return null;
    }

    /// <summary>在指定构建目录里找产物；找不到返回 <c>null</c>。</summary>
    public static string? FindArtifact(string buildDirectory, string projectName, string configuration, string family)
        => Candidates(buildDirectory, projectName, configuration, family).FirstOrDefault(File.Exists);

    /// <summary>
    /// 产物候选路径，**先配置匹配、再单配置布局**。多配置生成器（VS/Xcode）把配置写进路径，
    /// 单配置生成器不写 —— 两种都要认，否则换生成器就要改 Hub。
    /// 候选按目标族收窄：Windows 的 <c>.exe</c> 不能满足 WebAssembly 或 Android 的构建。
    /// </summary>
    public static IEnumerable<string> Candidates(string buildDirectory, string projectName, string configuration, string family)
    {
        var bin = Path.Combine(buildDirectory, "bin", projectName);
        var executables = family switch
        {
            "windows" or "uwp" => new[] { projectName + ".exe" },
            "android" => new[] { "lib" + projectName + ".so" },
            "wasm" => new[] { projectName + ".html", projectName + ".wasm", projectName + ".js" },
            _ => new[] { projectName },
        };
        // 多配置生成器把配置写进路径：bin/<App>/<Config>/<App>[.exe]，先试它。
        foreach (var executable in executables)
        {
            yield return Path.Combine(bin, configuration, executable);
        }

        foreach (var executable in executables)
        {
            yield return Path.Combine(bin, executable);
            yield return Path.Combine(bin, projectName + ".app/Contents/MacOS", projectName);
        }
    }
}
