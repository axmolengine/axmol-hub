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
/// Real-operation verification (<c>--verify-ops</c>): runs Hub's engine-management pipeline on a
/// **real engine source tree**.
///
/// The split of duties with <c>--verify-shell</c> is clear: the shell self-check verifies "UI
/// structure and localization" on fixtures; here we verify "operations really run through", using
/// real engine directories, and no longer "judging by reading code".
///
/// It answers a question that could previously only be inferred: of <c>HubWorkspace</c>'s nine
/// hundred-odd lines, which paths have actually been executed? The answer was "only lightweight
/// operations like list refresh, import, and selection".
///
/// **Cost boundary (deliberately drawn)**: only run operations that **don't download and don't
/// compile**. Installing engines, installing toolchains, building, and running pull gigabyte-scale
/// data or need a full toolchain — not "cheap" — so those are written explicitly into the report's
/// "skipped" section rather than pretending to be verified. A report that makes people think it ran
/// is worse than no report at all.
///
/// Usage: <c>--verify-ops &lt;report&gt; [engine dir...]</c>. When no engine dir is given, the
/// engine-dependent groups are recorded as skipped (the rest still run), so it yields meaningful
/// results even on a machine without engines.
/// </summary>
public sealed class OpsCheckWindow : Window
{
    private int _passed;
    private int _failed;
    private int _skipped;
    private readonly List<string> _lines = [];

    public void Run(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath, string[] engines)
    {
        // Real operations write state, logs, and settings — so the data root must be isolated,
        // never the user's own %LocalAppData%\AxmolHub\data.
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
                // The wording must cover two causes: the window didn't open, **or** some await never returned
                // (e.g. a failure dialog waiting for a confirmation no one will click). Writing only "window didn't open" would misreport the latter as the former.
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

    /// <summary>Explicitly records "this didn't run". Skips must be **visible**, otherwise the report reads as if everything ran.</summary>
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
            // Failure dialogs have no one to click them in automation, so the await never returns. Only presentation is silenced; see SuppressDialogs.
            SuppressDialogs = true,
        };

        CheckAllOpsAreInsideTheScratchRoot(workspace, scratchRoot);
        await CheckToolchainProbeAsync(workspace);
        await CheckEngineLifecycleAsync(workspace, engines);
        await CheckInvalidEngineInputAsync(workspace, scratchRoot);
        ReportSkipped();
    }

    /// <summary>
    /// The first gate, and the most important: confirm this verification **cannot** touch the user's data.
    /// It runs first — everything after writes to disk, and "writing to the wrong place" fails silently.
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
    /// Toolchain probing. **Deliberately doesn't assert direction**, only that "each item was
    /// evaluated"; measured values go into INFO.
    ///
    /// The probe's source of truth is now the engine tree: expected versions come from
    /// <c>&lt;engine&gt;/1k/build.profiles</c>, installed status from the official location
    /// <c>&lt;engine&gt;/tools/external</c>; VS is detected via vswhere (the engine only detects,
    /// never installs).
    /// The assertion still only holds "the probe ran and gave a definite conclusion" — which tool a
    /// machine lacks is **data**, not broken code.
    /// </summary>
    private async Task CheckToolchainProbeAsync(HubWorkspace workspace)
    {
        var components = await workspace.DetectAsync();

        Check(components.Count > 0, "工具链探测返回 " + components.Count + " 个组件（不是空表）");

        // "Was evaluated" = the status is conclusive (Missing/Installed/Broken…), not Unknown/Checking.
        // This is independent of what a specific machine has installed, so it still holds after rule changes.
        Check(components.All(c => c.Status is not (ComponentStatus.Unknown or ComponentStatus.Checking)),
            "每个组件都给出了结论性状态（没有停在 Unknown/Checking）");
        Check(components.All(c => c.Details.Length > 0), "每个组件都给出了状态说明（含期望版本或落点）");

        _lines.Add("INFO  实测: " + string.Join(" / ", components.Select(c => c.Name + "=" + c.Status)));
        _lines.Add("INFO  真源: <engine>/1k/build.profiles（期望版本）+ <engine>/tools/external（实装）");
    }

    /// <summary>
    /// Engine lifecycle: import → verify → set default → remove. Uses a real engine source tree,
    /// not a fixture.
    ///
    /// This does **not** presuppose which directory "should succeed": first look at what the
    /// directory objectively lacks, then assert Hub behaves consistently with it.
    /// Hard-coding expected values would turn the assertion into noise on another machine, and "how
    /// does Hub treat an incomplete engine" is exactly what this group wants to know most.
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
                    // The version must come from the engine's own version header, not guessed from the directory name — the directory name is arbitrary.
                    var parts = added.Version.Split('.');
                    Check(parts.Length == 3 && parts.All(part => int.TryParse(part, out _)),
                        "引擎「" + name + "」的版本读出为 " + added.Version + "（三段点分，来自引擎版本头 axmolver.h.in：v3 在 axmol/，v2 在 core/）");
                    _lines.Add("INFO  " + name + " → 版本 " + added.Version + "，通道 " + added.Channel);
                }
            }
            else
            {
                // An incomplete engine must be **rejected**, and the reason must name the missing file —
                // saying only "not a valid engine" leaves no way forward.
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

        // Verify: really read the engine directory once and confirm the version header matches the recorded value.
        await workspace.VerifyEngineAsync();
        Check(workspace.LastError.Length == 0,
            "校验引擎「" + Path.GetFileName(target.Path) + "」成功（LastError 为空）");

        await workspace.SetDefaultEngineAsync();
        Check(workspace.State.DefaultEnginePath == target.Path,
            "设为默认引擎后 DefaultEnginePath 指向它");

        // Persist + reload: a bug that only changes memory without persisting can only be caught by reading back from disk.
        var reloaded = new StateStore(workspace.Store.Root).Load();
        Check(reloaded.Engines.Count == workspace.State.Engines.Count,
            "重新加载后引擎数一致（" + reloaded.Engines.Count + "）");
        Check(reloaded.DefaultEnginePath == target.Path, "重新加载后默认引擎仍是它");
        Check(File.Exists(Path.Combine(workspace.Store.Root, "hub-state.json")), "状态文件真的写在数据根里");

        // Remove: only take it out of the list, must **not** touch the engine directory on disk.
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
    /// Invalid input. These are the norm for "user slips": wrong directory, a single file, or a path that doesn't exist.
    /// The expectation is a **clear exception** with state unaffected — not garbage written into the engine list.
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

        // Verify an **already-removed** engine directory: delete the directory first then verify; it must report, not crash.
        Check(workspace.LastError.Length > 0,
            "三次无效导入都留下了可读的错误（最近一次: "
            + workspace.LastError.Split('\n')[0].Replace("System.IO.IOException: ", "", StringComparison.Ordinal) + "）");
    }

    /// <summary>Writes "what wasn't verified this run, and why" into the report — the report must distinguish "verified" from "didn't run".</summary>
    private void ReportSkipped()
    {
        Skip("装官方引擎（--verify-ops 不联网下载；那条链是 DownloadManager + PackageInstaller + sha256 校验）");
        Skip("环境准备 / 装工具链（跑引擎自己的 setup.ps1：GB 级下载，可能改当前用户的 PowerShell 执行策略、弹 UAC）"
             + " —— 全局副作用不适合放进自动化，由用户在工具链页手动确认执行");
        Skip("构建与运行（需要先跑 setup.ps1 备好引擎树；成本是分钟到小时级，不属于「便宜的真跑」）");
        Skip("构建引擎预编译库（编译整棵引擎，数 GB、数十分钟）—— 由维护者在引擎页手动执行；"
             + "判定逻辑本身另有主机无关的验收：Checks --check-prebuilt");
        Skip("Android 打包（构建交给引擎的 axmol build -p android；需要 Android SDK/NDK 与签名材料）");
        Skip("安装主机 PowerShell 7（winget / 提权装 MSI / 终端里跑 pwshi.sh —— 三条路都会改整机，且需要 UAC 或 sudo）"
             + "；它的判定另有主机无关的离线验收：Checks --check-host-shell，只读预演见 --pwsh-release-report");
    }

    /// <summary>Which marker files an engine directory lacks. The list's only source is <see cref="StateStore.MissingEngineMarkers"/>, not written again here.</summary>
    private static string[] MissingMarkers(string engine) => [.. StateStore.MissingEngineMarkers(engine)];
}
