using System;
using System.IO;

namespace AxmolHub.App;

/// <summary>
/// 自检与验收产物的落点。
///
/// 约定：**临时产物只写仓库根的 <c>tmp/</c>**，cache 类产物写 <c>cache/</c>（两者都已在 .gitignore 里），
/// 不去外面的目录散落文件 —— 报告、截图、夹具状态集中在一处，删起来是一刀而不是翻遍 %TEMP%。
///
/// 安装后的自包含产物里没有仓库根，此时退回系统临时目录：那些路径上没有 git 仓库可写，
/// 而自检也不该因为"找不到仓库"就失败。
/// </summary>
internal static class ScratchDirectory
{
    /// <summary>仓库根。从程序集位置往上找 <c>src/AxmolHub.App</c>，找不到返回 null。</summary>
    public static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "AxmolHub.App")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>项目内 <c>tmp/</c>（找不到仓库时是系统临时目录）。</summary>
    public static string TmpRoot()
    {
        var root = RepositoryRoot();
        var path = root is null ? Path.GetTempPath() : Path.Combine(root, "tmp");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>一个（会被创建的）子目录。</summary>
    public static string Resolve(params string[] segments)
    {
        var path = Path.Combine([TmpRoot(), .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>一个文件路径。所在目录会先建好，文件本身不创建。</summary>
    public static string FilePath(params string[] segments)
    {
        var path = Path.Combine([TmpRoot(), .. segments]);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return path;
    }
}
