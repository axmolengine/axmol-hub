using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// 一次成功的**引擎根构建**记录 —— 引擎被编译出的那份预编译库（<c>axmol-sdk</c>）。
/// </summary>
public sealed class EngineBuildRecord
{
    public string EnginePath { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string Channel { get; set; } = "";

    /// <summary>Hub 目标 id（当前只支持 <c>windows-x64</c>）。</summary>
    public string Target { get; set; } = "";

    /// <summary>引擎 <c>-p</c> 平台名，如 <c>win32</c>。这是 <c>-DAX_PREBUILT_DIR</c> 是否适用的依据。</summary>
    public string Platform { get; set; } = "";

    /// <summary>引擎 <c>-a</c> 架构，如 <c>x64</c>。</summary>
    public string Architecture { get; set; } = "";

    /// <summary>构建配置（Release / Debug）。预编译库是按配置分目录的。</summary>
    public string Configuration { get; set; } = "";

    /// <summary>构建目录相对**引擎根**的路径，正斜杠（就是 <c>-DAX_PREBUILT_DIR</c> 的值）。</summary>
    public string BuildDirectory { get; set; } = "";

    /// <summary>构建时的引擎安装标识；引擎被修复/重装后与当前不符，记录即失效。</summary>
    public string EngineToken { get; set; } = "";

    public DateTimeOffset BuiltAt { get; set; }
}

/// <summary>
/// 引擎构建记录的存放位置：**Hub 数据根内**，按「引擎路径|版本|通道」的哈希落一个文件。
///
/// 与 <see cref="EngineModules"/> 的选择记录同构。放在数据根而不是引擎树里有两个理由：
/// <list type="number">
/// <item>记录描述的是「磁盘上这棵树被构建过」，把引擎移出列表再加入仍能命中同一条记录；</item>
/// <item>导入进来的引擎目前是**只读**的（只有 <c>PackageInstaller</c> 对 Hub 安装的引擎写
/// <c>.hub-install.json</c>），预编译记录不该破坏这条约定。</item>
/// </list>
/// 引擎被移动则哈希失配 → 视为没有记录（失败关闭，不会误信一条过期声明）。
/// </summary>
public sealed class EnginePrebuiltState(string root)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string Root { get; } = Path.GetFullPath(root);

    private string PathFor(EngineEntry engine)
    {
        var identity = Path.GetFullPath(engine.Path) + "|" + engine.Version + "|" + engine.Channel;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Root, "prebuilt", key + ".json");
    }

    public EngineBuildRecord? Load(EngineEntry engine)
    {
        var path = PathFor(engine);
        return File.Exists(path) ? JsonSerializer.Deserialize<EngineBuildRecord>(File.ReadAllText(path), Json) : null;
    }

    public void Save(EngineEntry engine, EngineBuildRecord record) => StateStore.WriteJson(PathFor(engine), record);

    /// <summary>引擎被修复/重装/卸载时清掉，避免留下一条指向过期产物的「已构建」声明。</summary>
    public void Clear(EngineEntry engine)
    {
        var path = PathFor(engine);
        if (File.Exists(path)) File.Delete(path);
    }
}
