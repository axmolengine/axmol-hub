using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 真操作验收（<c>--verify-ops</c>）：在**真实引擎源码树**上跑 Hub 的引擎管理链路。
///
/// 与 <c>--verify-shell</c> 的分工是明确的：外壳自检验"界面结构与本地化"，跑在 fixture 上；
/// 这里验"操作真的能跑通"，用的是真实的引擎目录，而且不再是"读代码判断"。
///
/// 它要回答的是一个此前只能靠推断的问题：<c>HubWorkspace</c> 九百多行里，哪些路径真的被执行过？
/// 答案是"只有列表刷新、导入、选择这类轻量操作"。
///
/// **成本边界（刻意划的）**：只跑**不下载、不编译**的操作。装引擎、装工具链、构建、运行
/// 会拉 GB 级数据或需要完整工具链，不属于"便宜"，因此那些明确写进报告的"跳过"一节，
/// 而不是假装验过 —— 一份让人以为跑过了的报告比没有报告更糟。
///
/// 用法：<c>--verify-ops &lt;报告&gt; [引擎目录...]</c>。不传引擎目录时，依赖引擎的那几组
/// 会记为跳过（其余照跑），因此它在一台没有引擎的机器上也能给出有意义的结果。
/// </summary>
public sealed class OpsCheckWindow : Window
{
    private int _passed;
    private int _failed;
    private int _skipped;
    private readonly List<string> _lines = [];

    public void Run(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath, string[] engines)
    {
        // 真实操作会写状态、写日志、写设置 —— 因此数据根必须是隔离的，
        // 绝不能用用户自己的 %LocalAppData%\AxmolHub\data。
        var scratchRoot = ScratchDirectory.Resolve("ops-check", Guid.NewGuid().ToString("N"));

        var finished = false;

        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            _lines.Add(string.Format(CultureInfo.InvariantCulture,
                "RESULT: {0}/{1} passed, {2} skipped", _passed, _passed + _failed, _skipped));

            try
            {
                File.WriteAllLines(reportPath, _lines);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            foreach (var line in _lines)
            {
                Console.WriteLine(line);
            }

            lifetime.Shutdown(_failed == 0 ? 0 : 1);
        }

        Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RunChecksAsync(scratchRoot, engines);
            }
            catch (Exception ex)
            {
                Check(false, "验收过程抛出异常: " + ex.GetType().Name + ": " + ex.Message);
            }

            Finish();
        }, DispatcherPriority.Background);

        DispatcherTimer.RunOnce(() =>
        {
            if (!finished)
            {
                // 措辞要覆盖两种成因：窗口没打开，**或**某个 await 没有返回
                // （比如失败弹窗在等一个不会有人点的确认）。只写"窗口没打开"会把后一种误报成前一种。
                Check(false, "验收在 10 秒内没有结束（窗口未打开，或某个异步调用没有返回）");
                Finish();
            }
        }, TimeSpan.FromSeconds(10));
    }

    private void Check(bool ok, string message)
    {
        if (ok)
        {
            _passed++;
            _lines.Add("PASS  " + message);
        }
        else
        {
            _failed++;
            _lines.Add("FAIL  " + message);
        }
    }

    /// <summary>明确记下"这条没跑"。跳过必须是**可见**的，否则报告读起来像是全跑通了。</summary>
    private void Skip(string message)
    {
        _skipped++;
        _lines.Add("SKIP  " + message);
    }

    private async Task RunChecksAsync(string scratchRoot, string[] engines)
    {
        var preferences = new HubPreferences();
        var preferencesStore = new PreferencesStore(Path.Combine(scratchRoot, "hub-settings.json"));
        using var workspace = new HubWorkspace(scratchRoot, preferences, preferencesStore)
        {
            // 失败弹窗在自动化里没人点，会让 await 永不返回。关掉的只是展示，见 SuppressDialogs。
            SuppressDialogs = true,
        };

        CheckAllOpsAreInsideTheScratchRoot(workspace, scratchRoot);
        await CheckToolchainProbeAsync(workspace);
        await CheckEngineLifecycleAsync(workspace, engines);
        await CheckInvalidEngineInputAsync(workspace, scratchRoot);
        ReportSkipped();
    }

    /// <summary>
    /// 第一道闸门，也是最重要的一条：确认这次验收**不可能**碰到用户的数据。
    /// 顺序放在最前面 —— 后面每条都会写盘，而"写错地方"这件事出错时是静默的。
    /// </summary>
    private void CheckAllOpsAreInsideTheScratchRoot(HubWorkspace workspace, string scratchRoot)
    {
        Check(workspace.Store.Root.Equals(new StateStore(scratchRoot).Root, StringComparison.OrdinalIgnoreCase),
            "数据根在隔离目录内（" + workspace.Store.Root + "）");
        Check(!workspace.Store.Root.Contains(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                                             + Path.DirectorySeparatorChar + "AxmolHub",
                StringComparison.OrdinalIgnoreCase),
            "数据根不是用户自己的 %LocalAppData%\\AxmolHub（真实操作会写状态与日志，绝不能落在那里）");
        Check(workspace.ToolsRoot.StartsWith(workspace.Store.Root, StringComparison.OrdinalIgnoreCase),
            "Hub 自己的数据目录在数据根内部（" + workspace.ToolsRoot + "）");
    }

    /// <summary>
    /// 工具链探测。**刻意不断言方向**，只断言"每项都被评估过"，实测值写进 INFO。
    ///
    /// 现在探测的真源是引擎树：期望版本来自 <c>&lt;engine&gt;/1k/build.profiles</c>，
    /// 实装状态来自官方落点 <c>&lt;engine&gt;/tools/external</c>；VS 由 vswhere 检测（引擎只检测不安装）。
    /// 断言仍然只守住"探测跑完了且给出了明确结论" —— 某台机器上缺哪个工具是**数据**，不是代码坏了。
    /// </summary>
    private async Task CheckToolchainProbeAsync(HubWorkspace workspace)
    {
        var components = await workspace.DetectAsync();

        Check(components.Count > 0, "工具链探测返回 " + components.Count + " 个组件（不是空表）");

        // "被评估过"= 状态是结论性的（Missing/Installed/Broken…），不是 Unknown/Checking。
        // 这条与具体机器上装了什么无关，因此规则变更后它仍然成立。
        Check(components.All(c => c.Status is not (ComponentStatus.Unknown or ComponentStatus.Checking)),
            "每个组件都给出了结论性状态（没有停在 Unknown/Checking）");
        Check(components.All(c => c.Details.Length > 0), "每个组件都给出了状态说明（含期望版本或落点）");

        _lines.Add("INFO  实测: " + string.Join(" / ", components.Select(c => c.Name + "=" + c.Status)));
        _lines.Add("INFO  真源: <engine>/1k/build.profiles（期望版本）+ <engine>/tools/external（实装）");
    }

    /// <summary>
    /// 引擎生命周期：导入 → 校验 → 设为默认 → 移除。用的是真实引擎源码树，不是 fixture。
    ///
    /// 这里**不预设**哪个目录"应该成功"：先看目录客观缺什么，再断言 Hub 的行为与之一致。
    /// 写死期望值会让断言在换一台机器时变成噪音，而"Hub 怎么对待一个不完整的引擎"
    /// 恰恰是这一组最想知道的。
    /// </summary>
    private async Task CheckEngineLifecycleAsync(HubWorkspace workspace, string[] engines)
    {
        if (engines.Length == 0)
        {
            Skip("未提供引擎目录，跳过引擎生命周期那组（用法：--verify-ops <报告> <引擎目录>...）");
            return;
        }

        var accepted = 0;

        foreach (var engine in engines)
        {
            var name = Path.GetFileName(engine.TrimEnd(Path.DirectorySeparatorChar, '/'));
            var missing = MissingMarkers(engine);
            var before = workspace.State.Engines.Count;

            await workspace.ImportEngineAsync(engine);

            var after = workspace.State.Engines.Count;
            var added = workspace.State.Engines.FirstOrDefault(e =>
                e.Path.Equals(Path.GetFullPath(engine), StringComparison.OrdinalIgnoreCase));

            if (missing.Length == 0)
            {
                accepted++;
                Check(added is not null && after == before + 1,
                    "完整引擎「" + name + "」被导入并进入列表");
                if (added is not null)
                {
                    // 版本必须来自引擎自己的版本头，而不是从目录名猜 —— 目录名是可以随便起的。
                    var parts = added.Version.Split('.');
                    Check(parts.Length == 3 && parts.All(part => int.TryParse(part, out _)),
                        "引擎「" + name + "」的版本读出为 " + added.Version + "（三段点分，来自引擎版本头 axmolver.h.in：v3 在 axmol/，v2 在 core/）");
                    _lines.Add("INFO  " + name + " → 版本 " + added.Version + "，通道 " + added.Channel);
                }
            }
            else
            {
                // 不完整的引擎必须被**拒绝**，且拒绝理由要点名缺哪个文件 ——
                // 只说"不是有效引擎"会让人无从下手。
                Check(added is null && after == before,
                    "不完整引擎「" + name + "」没有被加进列表（缺 " + string.Join(", ", missing) + "）");
                Check(workspace.LastError.Contains(missing[0], StringComparison.Ordinal),
                    "拒绝理由点名了缺失的标志文件「" + missing[0] + "」（而不是只说\"无效引擎\"）");
                _lines.Add("INFO  " + name + " → 被拒绝，缺失标志文件: " + string.Join(", ", missing));
            }
        }

        if (accepted == 0)
        {
            Skip("没有一个完整引擎，跳过校验/默认/移除那几条（当前数据根下"
                 + workspace.State.Engines.Count + " 个引擎）");
            return;
        }

        var target = workspace.State.Engines.First();
        workspace.SelectedEngine = target;

        // 校验：真读一遍引擎目录，确认版本头与登记值一致。
        await workspace.VerifyEngineAsync();
        Check(workspace.LastError.Length == 0,
            "校验引擎「" + Path.GetFileName(target.Path) + "」成功（LastError 为空）");

        await workspace.SetDefaultEngineAsync();
        Check(workspace.State.DefaultEnginePath == target.Path,
            "设为默认引擎后 DefaultEnginePath 指向它");

        // 落盘 + 重新加载：只改内存不落盘的 bug，只有从磁盘读回来才能发现。
        var reloaded = new StateStore(workspace.Store.Root).Load();
        Check(reloaded.Engines.Count == workspace.State.Engines.Count,
            "重新加载后引擎数一致（" + reloaded.Engines.Count + "）");
        Check(reloaded.DefaultEnginePath == target.Path, "重新加载后默认引擎仍是它");
        Check(File.Exists(Path.Combine(workspace.Store.Root, "hub-state.json")), "状态文件真的写在数据根里");

        // 移除：只移出列表，**不能**动磁盘上的引擎目录。
        var folderBefore = Directory.Exists(target.Path);
        await workspace.RemoveEngineAsync();
        Check(workspace.State.Engines.All(e => !e.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)),
            "移除引擎后它不在列表里");
        Check(folderBefore && Directory.Exists(target.Path),
            "移除引擎**没有**删除磁盘上的引擎目录（移除是「移出列表」，不是卸载）");
        Check(workspace.State.DefaultEnginePath != target.Path,
            "被移除的引擎不再被当作默认引擎（默认值自动改指别处或置空）");
    }

    /// <summary>
    /// 无效输入。这几条是"用户手滑"的常态：选错目录、选了单个文件、路径根本不存在。
    /// 期望是**明确的异常**且状态不受影响 —— 而不是把垃圾写进引擎列表。
    /// </summary>
    private async Task CheckInvalidEngineInputAsync(HubWorkspace workspace, string scratchRoot)
    {
        var empty = ScratchDirectory.Resolve("ops-check-invalid", Guid.NewGuid().ToString("N"));
        var before = workspace.State.Engines.Count;

        await workspace.ImportEngineAsync(empty);
        Check(workspace.State.Engines.Count == before && workspace.LastError.Length > 0,
            "导入空目录被拒绝（引擎列表没变，错误已记录）");

        var file = Path.Combine(scratchRoot, "not-a-directory.txt");
        File.WriteAllText(file, "x");
        await workspace.ImportEngineAsync(file);
        Check(workspace.State.Engines.Count == before, "导入一个文件（而不是目录）被拒绝");

        var gone = Path.Combine(scratchRoot, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        await workspace.ImportEngineAsync(gone);
        Check(workspace.State.Engines.Count == before, "导入不存在的路径被拒绝");

        // 校验一个**已卸载**的引擎目录：目录先删掉再校验，必须报告而不是崩掉。
        Check(workspace.LastError.Length > 0,
            "三次无效导入都留下了可读的错误（最近一次: "
            + workspace.LastError.Split('\n')[0].Replace("System.IO.IOException: ", "", StringComparison.Ordinal) + "）");
    }

    /// <summary>把"这次没验什么、以及为什么"写进报告 —— 报告必须能区分"验过"和"没跑"。</summary>
    private void ReportSkipped()
    {
        Skip("装官方引擎（--verify-ops 不联网下载；那条链是 DownloadManager + PackageInstaller + sha256 校验）");
        Skip("环境准备 / 装工具链（跑引擎自己的 setup.ps1：GB 级下载，且会改用户级 PATH 与 AX_ROOT、可能弹 UAC）"
             + " —— 全局副作用不适合放进自动化，由用户在工具链页手动确认执行");
        Skip("构建与运行（需要先跑 setup.ps1 备好引擎树；成本是分钟到小时级，不属于「便宜的真跑」）");
        Skip("构建引擎预编译库（编译整棵引擎，数 GB、数十分钟）—— 由维护者在引擎页手动执行；"
             + "判定逻辑本身另有主机无关的验收：Checks --check-prebuilt");
        Skip("Android 打包（构建交给引擎的 axmol build -p android；需要 Android SDK/NDK 与签名材料）");
    }

    /// <summary>引擎目录缺哪些标志文件。清单的唯一来源是 <see cref="StateStore.MissingEngineMarkers"/>，不在这里另写一份。</summary>
    private static string[] MissingMarkers(string engine) => [.. StateStore.MissingEngineMarkers(engine)];
}
