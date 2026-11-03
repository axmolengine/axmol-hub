using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Velopack;
using static AxmolHub.ThemeProbe;
using static AxmolHub.ShellCheckSupport;
using static AxmolHub.ShellCheckFixture;

namespace AxmolHub;
internal sealed class ShellLayoutChecks
{
    private readonly CheckReport _report;
    private readonly Action<bool, string> Check;
    private readonly ShellCheckFixture Fixture;
    internal ShellLayoutChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        _report = report;
        Check = check;
        Fixture = fixture;
    }

    internal async Task CheckProjectTrashFlowAsync(HubWorkspace workspace, string scratchRoot)
    {
        var projectPath = Path.Combine(scratchRoot, "trash-flow-project");
        var trashPath = Path.Combine(scratchRoot, "trash-flow-sink");
        Directory.CreateDirectory(Path.Combine(projectPath, "assets"));
        File.WriteAllText(Path.Combine(projectPath, "assets", "fixture.txt"), "trash fixture");
        var project = new ProjectEntry
        {
            Name = "Trash flow fixture", Path = projectPath, Version = "2.11.5", Channel = "local",
            Platform = "macos-arm64", Configuration = "Debug", ProjectType = "cpp",
        };
        var originalProjects = workspace.State.Projects.ToArray();
        var previousHandler = SystemTrash.MoveHandlerForCheck;
        var previousSuppressDialogs = workspace.SuppressDialogs;
        try
        {
            workspace.SuppressDialogs = true;
            workspace.State.Projects.Clear();
            workspace.State.Projects.Add(project);
            workspace.Store.Save(workspace.State);
            var nestedPath = Path.Combine(projectPath, "nested-project");
            Directory.CreateDirectory(nestedPath);
            var nested = new ProjectEntry { Name = "Nested fixture", Path = nestedPath };
            workspace.State.Projects.Add(nested);
            Check(Throws<InvalidOperationException>(() => workspace.ValidateProjectTrashAvailability(project)),
                "项目目录包含另一条登记项目时拒绝整体回收");
            workspace.State.Projects.Remove(nested);
            var protectedRoot = new ProjectEntry { Name = "Protected data root", Path = workspace.Store.Root };
            workspace.State.Projects.Add(protectedRoot);
            Check(Throws<InvalidOperationException>(() => workspace.ValidateProjectTrashAvailability(protectedRoot)),
                "Hub 数据根登记项不能被移到系统回收站");
            workspace.State.Projects.Remove(protectedRoot);

            var linkPath = Path.Combine(scratchRoot, "trash-flow-link");
            Directory.CreateSymbolicLink(linkPath, projectPath);
            var linkProject = new ProjectEntry { Name = "Link fixture", Path = linkPath };
            workspace.State.Projects.Add(linkProject);
            Check(Throws<InvalidOperationException>(() => workspace.ValidateProjectTrashAvailability(linkProject)),
                "项目根目录是符号链接时拒绝回收");
            workspace.State.Projects.Remove(linkProject);
            Directory.Delete(linkPath);
            workspace.Store.Save(workspace.State);
            SystemTrash.MoveHandlerForCheck = (_, _) => Task.FromResult(new TrashResult(TrashResultKind.Unsupported, "fixture failure"));
            var failed = await workspace.MoveProjectToTrashAsync(project);
            Check(!failed && workspace.State.Projects.Contains(project) && Directory.Exists(projectPath),
                "回收服务失败时项目目录和登记记录都保留");

            SystemTrash.MoveHandlerForCheck = (path, _) =>
            {
                Directory.CreateDirectory(trashPath);
                Directory.Move(path, Path.Combine(trashPath, Path.GetFileName(path)));
                return Task.FromResult(new TrashResult(TrashResultKind.Moved));
            };
            var moved = await workspace.MoveProjectToTrashAsync(project);
            Check(moved && !workspace.State.Projects.Contains(project)
                  && File.Exists(Path.Combine(trashPath, "trash-flow-project", "assets", "fixture.txt")),
                "只有回收服务成功后项目记录才移出列表，整个目录进入回收站结果");
        }
        finally
        {
            SystemTrash.MoveHandlerForCheck = previousHandler;
            workspace.SuppressDialogs = previousSuppressDialogs;
            workspace.State.Projects.Clear();
            workspace.State.Projects.AddRange(originalProjects);
            workspace.Store.Save(workspace.State);
            if (Directory.Exists(projectPath)) Directory.Delete(projectPath, recursive: true);
            if (Directory.Exists(trashPath)) Directory.Delete(trashPath, recursive: true);
        }
    }

    internal void CheckSnapHookPress(MainWindow shell)
    {
        if (!OperatingSystem.IsWindows())
        {
            _report.Skip("非 Windows 平台没有 Win32 窗口过程，跳过最大化按钮的真实非客户区点击");
            return;
        }

        Check(shell.SnapHookInstalledForCheck,
            "最大化按钮的 Win32 子类化真装上了：没有它既没有悬停弹出层，也收不到那一下点击");

        if (NamedDescendant<Button>(shell, "MaximizeButton") is not { } plate
            || NamedDescendant<Button>(shell, "CloseButton") is not { } close)
        {
            Check(false, "找不到最大化 / 关闭按钮，无法验证非客户区点击");
            return;
        }

        const uint wmNcHitTest = 0x0084;
        const uint wmNcMouseMove = 0x00A0;
        const uint wmNcLButtonDown = 0x00A1;
        const uint wmNcLButtonUp = 0x00A2;
        const int htMaxButton = 9;

        shell.SetWindowStateForCheck(WindowState.Normal);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (ScreenCentreOf(plate, shell) is not { } over || ScreenCentreOf(close, shell) is not { } away)
        {
            _report.Skip("窗口还没落到屏幕上，取不到按钮的屏幕坐标");
            return;
        }

        // The hit test stays narrowed to the plate: it is what raises the flyout, and answering it across the bar
        // would turn the whole caption into a non-client area — which would cost every other control its click.
        Check(TitleBarSnapHook.SendNonClientForCheck(shell, wmNcHitTest, over).ToInt64() == htMaxButton
              && TitleBarSnapHook.SendNonClientForCheck(shell, wmNcHitTest, away).ToInt64() != htMaxButton,
            "命中测试只把最大化按钮那一块报成 HTMAXBUTTON（弹出层要它），旁边那枚不是（实际 "
            + TitleBarSnapHook.SendNonClientForCheck(shell, wmNcHitTest, over).ToInt64() + " / "
            + TitleBarSnapHook.SendNonClientForCheck(shell, wmNcHitTest, away).ToInt64() + "）");

        // A real click: down shows the pressed plate, up runs the action. Asserting the toggle *and* the plate is
        // the difference between "the message arrived" and "the reader saw a button work".
        TitleBarSnapHook.SendNonClientForCheck(shell, wmNcLButtonDown, over);
        Check(shell.MaximizePressedForCheck,
            "非客户区按下时最大化按钮是按下的样子（:pressed 在这个点上永远不会触发）");
        TitleBarSnapHook.SendNonClientForCheck(shell, wmNcLButtonUp, over);
        Dispatcher.UIThread.RunJobs();
        Check(shell.WindowState == WindowState.Maximized
              && !shell.MaximizePressedForCheck
              && shell.MaximizeGlyphIsRestoreForCheck
              && shell.MaximizeTipForCheck == HubTexts.Get("RestoreWindow", HubStrings.Language),
            "那一下点击真的最大化了窗口，按下态收掉、图形与提示也跟着改（实际 " + shell.WindowState + "，提示「"
            + shell.MaximizeTipForCheck + "」）");

        // Dragged off the plate before letting go: the press was the plate's, the release was not, so nothing
        // happens — the same rule a real caption button has. The coordinates are re-measured because maximizing
        // moved every one of them.
        shell.SetWindowStateForCheck(WindowState.Normal);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (ScreenCentreOf(plate, shell) is { } overAgain && ScreenCentreOf(close, shell) is { } awayAgain)
        {
            TitleBarSnapHook.SendNonClientForCheck(shell, wmNcLButtonDown, overAgain);
            TitleBarSnapHook.SendNonClientForCheck(shell, wmNcMouseMove, awayAgain);
            TitleBarSnapHook.SendNonClientForCheck(shell, wmNcLButtonUp, awayAgain);
            Dispatcher.UIThread.RunJobs();
            Check(shell.WindowState == WindowState.Normal && !shell.MaximizePressedForCheck,
                "按住后把指针拖离按钮再松手：什么都不发生，状态不变、按下态也收掉（实际 "
                + shell.WindowState + "）");
        }

        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal void CheckRestoreDragPlacement(MainWindow shell, Border? dragArea)
    {
        if (OperatingSystem.IsWindows())
        {
            _report.Skip("Windows 上把窗口从全屏里拖出来是 DWM 的事，这里验证的是非 Windows 那一支");
            return;
        }

        if (dragArea is null)
        {
            _report.Skip("找不到标题栏的拖动层，还原落位没法测");
            return;
        }

        shell.SetWindowStateForCheck(WindowState.Maximized);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (shell.Bounds.Width <= shell.NormalClientSizeForCheck.Width + 1)
        {
            _report.Skip("没有窗口管理器把这个窗口真的最大化，非 Windows 的落位实测在这里量不到东西");
            return;
        }

        // Every input is read while the window is still maximized, which is the state the formula is defined on:
        // the client origin because the pointer sits in the client area, the window origin because that is what
        // Position carries, and the bar's width because the press proportion is measured along it.
        var clientOrigin = shell.PointToScreen(new Point(0, 0));
        var windowOrigin = shell.Position;
        var barWidth = shell.Bounds.Width;
        var restored = shell.NormalClientSizeForCheck;
        var press = BarPressPoint(dragArea, shell);
        var expected = MainWindow.RestoredOriginForDrag(
            clientOrigin, windowOrigin, barWidth, press, restored, shell.RenderScaling);

        shell.RestoreForDragForCheck(press);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(shell.WindowState == WindowState.Normal
              && Math.Abs(shell.Position.X - expected.X) <= 2
              && Math.Abs(shell.Position.Y - expected.Y) <= 2,
            "在非 Windows 的真实窗口上按公式落位：状态回到 Normal，窗口原点落在算出来的那一点（±2px，窗口管理器有权"
            + "微调）（实际 " + FmtPoint(shell.Position) + "，期望 " + FmtPoint(expected) + "，全屏时客户区原点 "
            + FmtPoint(clientOrigin) + "、窗口原点 " + FmtPoint(windowOrigin) + "）");

        // The live window has just been moved on purpose; the caption-plate assertions further down re-read screen
        // coordinates, so the window goes back where it was found.
        shell.Position = windowOrigin;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal void CheckHostShellCard(MainWindow shell)
    {
        var page = (ToolchainsPage)shell.NavigateTo("Toolchains");
        page.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var card = NamedDescendant<Border>(page, "HostShellCard");
        var line = NamedDescendant<TextBlock>(page, "HostShellStatus");
        var button = NamedDescendant<Button>(page, "InstallPwshButton");
        Check(card is not null && line is not null && button is not null,
            "工具链页底部有主机 PowerShell 卡（状态行 + 一个动作按钮），不是「尚未支持」的说明文字");
        // The cell above already recorded the missing controls as a FAIL; stopping here only avoids crashing
        // on a null further down — later assertions stay un-run rather than mis-reported.
        if (card is null || line is null || button is null)
        {
            return;
        }

        // This card must sit outside the scroll area — that is its reason to exist, and the property a layout
        // edit can most quietly destroy.
        Check(!card.GetVisualAncestors().Any(a => a is ScrollViewer),
            "PowerShell 卡不在 ScrollViewer 里（滚动区里的前置条件会被滚得看不见）");

        // Adding a card row means editing RowDefinitions. An old card squeezed out or clipped is the most
        // realistic regression from that change.
        Check(card.Parent is Grid { RowDefinitions.Count: 5 },
            "工具链页的根网格现在是 5 行（实际 " + (card.Parent as Grid)?.RowDefinitions.Count + "）");
        Check(Grid.GetRow(card) == 3, "PowerShell 卡占第 3 行（实际 " + Grid.GetRow(card) + "）");
        var setup = NamedDescendant<Button>(page, "RunEngineSetupButton");
        var setupCard = setup?.GetVisualAncestors().OfType<Border>().FirstOrDefault();
        Check(setup is not null && setupCard is not null && Grid.GetRow(setupCard) == 4,
            "「运行引擎 setup.ps1」那条卡被挤到第 4 行，仍然在页面上（没被新卡顶掉）");

        // Inject each of the four states in turn: the copy and the button's visibility must agree with
        // HostShellStatus's verdict. An enum switch missing a branch only ever shows up here — on a real
        // machine usually just one state is reachable.
        foreach (var (state, executable, version) in new (HostShellState State, string? Path, string? Version)[]
                 {
                     (HostShellState.Ready, "/usr/local/bin/pwsh", "7.6.6"),
                     (HostShellState.TooOld, "/usr/local/bin/pwsh", "7.3.9"),
                     (HostShellState.Missing, null, null),
                     (HostShellState.Unknown, "/usr/local/bin/pwsh", null),
                 })
        {
            page.SetHostShellForCheck(new HostShellStatus(state, executable, version));
            Dispatcher.UIThread.RunJobs();

            var sentence = state switch
            {
                HostShellState.Ready => string.Format(HubTexts.Get("HostShellStateReady", HubStrings.Language), version),
                HostShellState.TooOld => string.Format(HubTexts.Get("HostShellStateTooOld", HubStrings.Language), version, HostPowerShell.MinimumVersion),
                HostShellState.Missing => HubTexts.Get("HostShellStateMissing", HubStrings.Language),
                _ => HubTexts.Get("HostShellStateUnknown", HubStrings.Language),
            };
            Check(line!.Text == sentence + (executable is null ? "" : "\n" + executable),
                $"「{state}」的状态行由 HubTexts 拼出，路径另起一行（期望「{sentence}」，实际「{line.Text}」）");
            Check(button!.IsVisible == (state != HostShellState.Ready),
                $"「{state}」时按钮{(state == HostShellState.Ready ? "隐藏（已就绪不该再推销安装）" : "可见")}（实际 IsVisible={button.IsVisible}）");
        }

        // After the hand-off to the terminal the same button changes identity: Hub holds nothing cancellable
        // at that point, so all it can offer is a re-probe.
        page.SetHostShellForCheck(new HostShellStatus(HostShellState.Missing), awaitingTerminal: true);
        Dispatcher.UIThread.RunJobs();
        Check(Equals(button!.Content, HubTexts.Get("Verify", HubStrings.Language)),
            "终端转交期间按钮变成「重新检测」，复用已有文案键而不是再造一个（实际「" + button.Content + "」）");
        // `more` is a promise in this shell: clicking it pops a confirmation first. Re-check does not pop one,
        // so the promise must be withdrawn — this "icon still there but behavior changed" mismatch can only be
        // caught by asserting the class and the label together.
        Check(!button!.Classes.Contains("more"),
            "变成「重新检测」时摘掉 more 类（它还弹确认框的话就是在撒谎）");

        page.SetHostShellForCheck(new HostShellStatus(HostShellState.Missing));
        Dispatcher.UIThread.RunJobs();
        Check(Equals(button!.Content, HubTexts.Get("InstallPowerShell7", HubStrings.Language)),
            "缺失时按钮文案来自 HubTexts 的 InstallPowerShell7（实际「" + button.Content + "」）");
        Check(button!.Classes.Contains("more"),
            "安装按钮带 more 类（点击先弹确认，说明走哪条路与什么代价）");

        // Restore the real verdict for the groups that run after this one (and for the rendered-page capture),
        // so an injected "missing" cannot leak into a later assertion. This is the filesystem walk: no spawn.
        page.SetHostShellForCheck(HostPowerShell.Probe());
        Dispatcher.UIThread.RunJobs();
        Check(!string.IsNullOrWhiteSpace(line.Text),
            "本机真实探测重新画上去了（后面几组断言看到的不是注入值，实际「" + line.Text + "」）");
    }

    internal async Task CheckDataRootSwitch(string scratchRoot, MainWindow shell)
    {
        var preferencesPath = PreferencesPathFor(scratchRoot);
        var settings = OpenSettingsOn(shell, "storage");

        Check(NamedDescendant<Button>(settings, "ChooseDataDirectoryButton") is not null,
            "设置页提供切换数据目录的按钮（而不是一行「尚未迁移」说明）");

        var next = ScratchDirectory.Resolve("shell-check-switch", Guid.NewGuid().ToString("N"));
        var nextRoot = new StateStore(next).Root;

        // Captured before the switch. The point is that the *previous* root's log leaves the screen, not that
        // the box ends up empty: building the new root's chrome can legitimately write a first line while the
        // switch settles, and demanding an empty box turns that into a failure roughly one run in ten. Lines
        // carry an ISO timestamp, so a surviving one cannot be a coincidence of wording. The retained tail is
        // read rather than the box, because the box is only painted while the drawer is open.
        var previousLogLines = shell.ActivityLogTextForCheck
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var changed = shell.SwitchDataRoot(next);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(changed && shell.Workspace.Store.Root == nextRoot,
            "切换后工作区的数据根指向新目录（实际 " + shell.Workspace.Store.Root + "）");

        // The assistant's change gate is one of the seams re-wired when the workspace is rebuilt. A lambda that had
        // captured the *first* workspace would keep holding a gate nobody is looking at: the assistant would be
        // serialising against a dead queue while the page's busy truth said nothing was running. Taken through the
        // seam rather than through the workspace, because that is the only shape that can tell the two apart.
        var heldOnNewRoot = shell.TakeAssistantGateForCheck("cmake --build build");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(heldOnNewRoot is not null && shell.Workspace.IsBusy,
            "换根后助手的命令占的是新工作区的闸（缝重接过，实际忙 " + shell.Workspace.IsBusy + "）");
        heldOnNewRoot?.Dispose();
        await WaitUntilAsync(() => !shell.Workspace.IsBusy);

        Check(System.IO.Directory.Exists(nextRoot), "切换时新数据根被真的建出来（不是只改了个字符串）");

        // Persistence: if only the in-memory workspace changed while the settings file still says the old root, the next launch would silently revert.
        Check(new PreferencesStore(preferencesPath).Load().DataRoot == nextRoot,
            "新数据根已写入设置文件（下次启动仍指向它）");

        // The card is not in the page cache, so the sweep that drops the old pages would leave it standing on a
        // disposed workspace. Two facts to prove: the switch closed it, and reopening built a different page.
        Check(!shell.SettingsOverlayVisibleForCheck,
            "切换数据根时设置浮层一起关掉：留在屏上的会是一张指向已释放工作区的卡");

        var rebuilt = OpenSettingsOn(shell, "storage");
        Check(!ReferenceEquals(rebuilt, settings),
            "重新打开后是**新建**的设置页（旧页面持有旧工作区，必须整批丢弃）");

        Check(NamedDescendant<TextBox>(rebuilt, "DataLocation")?.Text == nextRoot,
            "重建出的设置页显示新数据根（实际 "
            + (NamedDescendant<TextBox>(rebuilt, "DataLocation")?.Text ?? "页面为空") + "）");

        var logAfterSwitch = shell.ActivityLogTextForCheck;
        var survived = previousLogLines.Where(line => logAfterSwitch.Contains(line, StringComparison.Ordinal)).ToArray();
        Check(previousLogLines.Length > 0 && survived.Length == 0,
            "切换后上一个根的日志行都不再摆在界面上（切换前 " + previousLogLines.Length
            + " 行，残留 " + survived.Length + " 行"
            + (survived.Length > 0 ? "，例如「" + survived[0] + "」" : "") + "）");

        // The state really comes from the new root: the new root is empty, so if the old root's two engines are still visible, the switch only changed a string.
        var installs = (InstallsPage)shell.NavigateTo("Installs");
        installs.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(NamedDescendant<TextBlock>(installs, "EmptyEngines") is { IsVisible: true },
            "切换后引擎页显示空态（状态来自新根，不是残留旧根的引擎列表）");

        // Switching to the same directory should be a no-op — otherwise every confirm click rebuilds all pages for nothing.
        Check(!shell.SwitchDataRoot(next) && ReferenceEquals(shell.CurrentPage, installs),
            "切到同一个目录时什么也不做（返回 false，页面实例不变）");

        // A drive root must be rejected: treating a whole drive as the data directory would create tools/ and logs/ inside it.
        Check(Throws<System.IO.IOException>(() => shell.SwitchDataRoot(System.IO.Path.GetPathRoot(nextRoot)!)),
            "拒绝把数据根切到盘符根目录（抛 IOException，而不是把整个盘当成数据目录）");

        shell.SwitchDataRoot(scratchRoot);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.Workspace.Store.Root == new StateStore(scratchRoot).Root,
            "再切回原数据根同样有效（双向可逆，不是一次性的）");
    }

    internal MainWindow CheckShell(string scratchRoot)
    {
        var shell = Fixture.OpenShell(scratchRoot);
        shell.Show();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        // What is asserted is "the window carries an icon", not "the icon paints right": Linux taskbars and
        // alt-Tab read _NET_WM_ICON, which is only written when Window.Icon has a value (Avalonia's X11 backend
        // calls XDeleteProperty when there is none). Pixel correctness is not proven here — the desktop-entry
        // and icon-theme chain is covered by --check-linux-integration.
        Check(shell.Icon is not null, "外壳窗口带着应用图标（Linux 任务栏与 alt-Tab 依赖 _NET_WM_ICON）");
        // Page keys are deliberately separate from **copy keys**: the engines page's page key is Engines, while its navigation copy key is Installs
        // (the WPF version already used the word "Installs", so HubTexts keeps it — the two are not the same string).
        // Settings has left this list for the bottom gear: it is still a full page (see MainWindow.PageKeys) but
        // no longer a navigation radio button, so it is asserted separately below.
        var navs = new (string Page, string Label, RadioButton Button)[]
        {
            ("Projects", "Projects", shell.NavProjects),
            ("Installs", "Installs", shell.NavInstalls),
            ("Toolchains", "Toolchains", shell.NavToolchains),
            ("Assistant", "Assistant", shell.NavAssistant),
        };
        Check(navs.All(nav => nav.Button.GroupName == "Navigation"), "四个导航项处在同一个 GroupName 里（否则会同时选中多项）");
        // Expected values are computed from HubTexts rather than hard-coded Chinese strings: what's asserted is "copy comes from HubTexts",
        // not "this machine happens to be Chinese". Hard-coding would make this assertion noise under an English setting.
        var expected = string.Join("/", navs.Select(nav => HubTexts.Get(nav.Label, HubStrings.Language)));
        var labels = string.Join("/", navs.Select(nav => NavLabel(nav.Button)));
        Check(labels == expected, "导航文案来自 HubTexts 而不是硬编码（期望 " + expected + "，实际 " + labels + "）");
        Check(navs.Select(nav => nav.Page).SequenceEqual(MainWindow.PageKeys.Where(key => key != "Settings")), "导航项与页面键（除设置外）逐一对应（" + string.Join("/", navs.Select(nav => nav.Page)) + "），新增导航项必须同时给出页面");
        // Navigate one by one, confirming the host content **really** changed and the left highlight follows — not just the checked state.
        var seen = new List<Control>();
        var hostFailures = new List<string>();
        var highlightFailures = new List<string>();
        foreach (var(page, _, button)in navs)
        {
            var control = shell.NavigateTo(page);
            seen.Add(control);
            if (!ReferenceEquals(shell.CurrentPage, control))
                hostFailures.Add(page);
            if (button.IsChecked != true || navs.Where(nav => nav.Button != button).Any(nav => nav.Button.IsChecked == true))
                highlightFailures.Add(page);
        }

        Check(hostFailures.Count == 0, $"导航遍历 {navs.Length} 页后内容宿主都对应目标页（失败：{string.Join(", ", hostFailures)}）");
        Check(highlightFailures.Count == 0, $"导航遍历 {navs.Length} 页后侧栏都只高亮目标项（失败：{string.Join(", ", highlightFailures)}）");
        Check(seen.Distinct().Count() == navs.Length, "四个导航页面是四个不同实例（没有把同一个控件复用成多页）");
        // Caching: navigating again must return the same instance. Otherwise every page switch rebuilds and selection plus scroll position are silently lost.
        Check(ReferenceEquals(shell.NavigateTo("Installs"), seen[Array.IndexOf(MainWindow.PageKeys, "Installs")]), "再次导航到同一页拿回同一实例（页面被缓存而不是每次重建）");
        // Every page must be a real page: any pre-P5 "not migrated" placeholder page gets caught right here.
        Check(shell.NavigateTo("Projects") is ProjectsPage, "项目页由 ProjectsPage 承载");
        Check(shell.NavigateTo("Installs") is InstallsPage, "引擎页由 InstallsPage 承载");
        Check(shell.NavigateTo("Toolchains") is ToolchainsPage, "工具链页由 ToolchainsPage 承载");
        Check(shell.NavigateTo("Assistant") is ChatPanel, "AI 助手页由 ChatPanel 承载");
        Check(shell.OpenSettingsForCheck("appearance") is SettingsPage, "设置由 SettingsPage 承载，摆在浮层上");
        // The gear stays an independent shortcut to settings (it did not move into the popup). The silent
        // failures it guards: the gear stops opening the card, or opening the card pretends to be a navigation
        // and takes the reader somewhere they did not ask to go.
        Check(shell.SettingsLabel == HubTexts.Get("Settings", HubStrings.Language), "齿轮按钮的提示文案来自 HubTexts（图标按钮没有文字，提示是它唯一的名称，实际「" + shell.SettingsLabel + "」）");
        shell.NavigateTo("Toolchains");
        shell.ClickSettingsGearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.SettingsOverlayVisibleForCheck, "点底部齿轮真的升起设置浮层（事件接线有效）");
        Check(shell.CurrentPage is ToolchainsPage && navs.Single(nav => nav.Page == "Toolchains").Button.IsChecked == true, "浮层是一次覆盖而不是一次导航：底下那页和它的高亮都不动（当前页 " + shell.CurrentPage?.GetType().Name + "）");
        // Beside the gear, the brand/host text block opens the bottom popup menu.
        // The bottom entry's popup: settings, three theme rows, two language rows, each drawn as one row of its
        // own. The silent failures: the block stops opening the menu, a row goes missing or its copy drifts from
        // HubTexts/HubTheme, a row stops switching what it claims to switch, the tick disagrees with the state
        // it is supposed to show, or the "设置" row stops lifting the card. All are asserted here.
        Check(shell.BottomMenuLabel == HubTexts.Get("BottomMenuTip", HubStrings.Language), "底部文字块的提示文案来自 HubTexts（文字命名的是机器，提示才说明它打开什么，实际「" + shell.BottomMenuLabel + "」）");
        shell.NavigateTo("Toolchains");
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var bottomMenu = shell.BottomMenuForCheck;
        Check(bottomMenu is { IsOpen: true }, "点底部入口真的弹出菜单（事件接线有效）");
        // Two levels, and the shape itself is the assertion: Settings plus Appearance, with Language and Theme
        // under Appearance and each carrying its own answers. A row that grew children, or a question that turned
        // back into a leaf, is what would quietly put a theme value on the first level again.
        var rows = bottomMenu!.Items.OfType<MenuItem>().Take(2).ToArray();
        var appearance = rows.Length > 1 ? rows[1] : null;
        var subRows = appearance?.Items.OfType<MenuItem>().ToArray() ?? [];
        var languageRow = subRows.FirstOrDefault(item => item.Tag as string == MainWindow.MenuRowTag.Language);
        var themeRow = subRows.FirstOrDefault(item => item.Tag as string == MainWindow.MenuRowTag.Theme);
        var languageRows = languageRow?.Items.OfType<MenuItem>().ToArray() ?? [];
        var themeRows = themeRow?.Items.OfType<MenuItem>().ToArray() ?? [];
        Check(rows.Length == 2 && rows[0].Items.Count == 0 && appearance is { Items.Count: 2 } && !appearance.IsChecked && languageRow is { Items.Count: 2 } && !languageRow.IsChecked && themeRow is { Items.Count: 3 } && !themeRow.IsChecked && languageRows.Length == SettingsPage.LanguageChoices.Length && themeRows.Length == HubTheme.All.Length && languageRows.Concat(themeRows).All(row => row.Items.Count == 0), "弹出菜单中的 Settings + Appearance 仍保持两层，外观下的语言 / 主题各带自己的答案（实际检查前两行，外观下 " + subRows.Length + " 行，主题 " + themeRows.Length + " 项、语言 " + languageRows.Length + " 项）");
        // A row's drawn shape is read off the header control the window built, not off the item's own visual
        // tree: a row inside a submenu nobody has opened has no template realized, so a walk that starts at the
        // item finds nothing and an unopened row would read as a broken one. The hint half of the tuple is the
        // sentinel for the sentence that used to sit under every row — it is empty on all of them, and this is
        // what says so if one comes back.
        string RowTitle(MenuItem item) => MainWindow.MenuRowShapeForCheck(item).Title;
        bool RowTicked(MenuItem item) => MainWindow.MenuRowShapeForCheck(item).Ticked;
        IEnumerable<MenuItem> RowsUnder(MenuItem item) => item.Items.OfType<MenuItem>().SelectMany(RowsUnder).Prepend(item);
        var everyRow = rows.SelectMany(RowsUnder).ToArray();
        Check(everyRow.All(row => MainWindow.MenuRowShapeForCheck(row).Title.Length > 0 && MainWindow.MenuRowShapeForCheck(row).Hint.Length == 0), "每一行都只有一行字：名字说完它是什么，底下不再挂解释句（" + everyRow.Length + " 行）");
        // The glyphs the rows carry are measured, not eyeballed: a non-square box sits off-centre in the
        // square slot the row gives it. A key that stops resolving fails here too, which is what keeps a glyph
        // from being deleted while a row still draws it.
        Check(RowTitle(rows[0]) == HubTexts.Get("Settings", HubStrings.Language) && !RowTicked(rows[0]) && rows[0].Items.Count == 0, "第一行是「设置」，整行只有名字，自己不勾选任何东西（实际「" + RowTitle(rows[0]) + "」）");
        Check(appearance is not null && RowTitle(appearance) == HubTexts.Get("Appearance", HubStrings.Language) && !RowTicked(appearance), "第二行是「外观」，它只负责展开，自己不勾选任何东西（实际「" + (appearance is null ? "<null>" : RowTitle(appearance)) + "」）");
        Check(languageRow is not null && RowTitle(languageRow) == HubTexts.Get("Language", HubStrings.Language) && !RowTicked(languageRow) && themeRow is not null && RowTitle(themeRow) == HubTexts.Get("Theme", HubStrings.Language) && !RowTicked(themeRow), "外观下是「界面语言」与「主题」两行，各行带自己的子菜单，自己不勾选（实际「" + (languageRow is null ? "<null>" : RowTitle(languageRow)) + " / " + (themeRow is null ? "<null>" : RowTitle(themeRow)) + "」）");
        var expectedThemeLabels = HubTheme.All.Select(value => HubTexts.Get(SettingsPage.ThemeTextKey(value), HubStrings.Language)).ToArray();
        Check(themeRows.Select(RowTitle).SequenceEqual(expectedThemeLabels, StringComparer.Ordinal), "主题子菜单三行按 HubTheme.All 的顺序排，文案来自 HubTexts（期望 " + string.Join("/", expectedThemeLabels) + "，实际 " + string.Join("/", themeRows.Select(RowTitle)) + "）");
        Check(themeRows.Count(item => item.IsChecked) == 1 && themeRows.Single(item => item.IsChecked).Tag as string == ThemeService.Current && themeRows.Select(RowTicked).SequenceEqual(themeRows.Select(item => item.IsChecked)), "主题子菜单里恰好一行被勾选且就是当前主题，画出来的勾号与 IsChecked 说的是同一件事（当前 " + ThemeService.Current + "）");
        Check(languageRows.Select(RowTitle).SequenceEqual(SettingsPage.LanguageChoices.Select(choice => choice.Display), StringComparer.Ordinal), "语言子菜单两行用各自语言的写法标出，不随界面语言翻译（否则读不懂当前界面的人正好看不见它）");
        Check(languageRows.Count(item => item.IsChecked) == 1 && languageRows.Single(item => item.IsChecked).Tag as string == HubStrings.Language && languageRows.Select(RowTicked).SequenceEqual(languageRows.Select(item => item.IsChecked)), "语言子菜单里恰好一行被勾选且就是当前语言（当前 " + HubStrings.Language + "）");
        Check(SettingsPage.LanguageChoices.Select(choice => choice.Tag).SequenceEqual(shell.SettingsPageForCheck?.DeclaredLanguages ?? []), "底部菜单的语言行与设置页下拉声明的是同一批语言（两处各存一份就是漂移的开始）");
        // The theme rows drive the shell's UseTheme — the same path the settings picker goes through (its
        // end-to-end coverage is in 5.6). Click the theme that is **not** current, then come back, so the run
        // ends dark and the render captures below keep their expected look.
        var menuTarget = ThemeService.Current == HubTheme.Dark ? HubTheme.Light : HubTheme.Dark;
        themeRows.Single(item => item.Tag as string == menuTarget).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(ThemeService.Current == menuTarget, "点未勾选的主题行真的切过去（当前 " + ThemeService.Current + "）");
        // The menu is rebuilt on every open, so the tick the reader sees next is read from the state rather
        // than from a group anyone has to re-sync — which is the machinery this shape deleted. Reopening is
        // also where a leaf click gets pressed on: `menu.Hide()` now has a submenu popup to take down with it,
        // and a flyout left standing would make every toggle below close the menu instead of opening it.
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.BottomMenuForCheck is { IsOpen: true }, "点主题叶子之后整块面板收干净：再点一次是重开菜单，不是把它关掉");
        var reopened = BottomMenuSubmenuRows(shell, MainWindow.MenuRowTag.Theme);
        var movedThemeRow = reopened.FirstOrDefault(item => item.Tag as string == menuTarget);
        Check(reopened.Count(item => item.IsChecked) == 1 && movedThemeRow is { IsChecked: true } && RowTicked(movedThemeRow), "重开菜单，勾落在新主题那一行上，画出来的勾也是那一行（当前 " + menuTarget + "）");
        reopened.Single(item => item.Tag as string == HubTheme.Dark).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(ThemeService.Current == HubTheme.Dark, "收尾切回深色（后面的渲染断言都在深色下拍）");
        // The language rows go through UseLanguage, which repaints the whole shell. Round-tripped so the suite
        // keeps running in the language every assertion below is written in. Rows are found by the tag they
        // carry rather than by position, because "the first language row" is the one already in force.
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        BottomMenuSubmenuRows(shell, MainWindow.MenuRowTag.Language).Single(item => item.Tag as string == HubTexts.EnglishLanguage).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(HubStrings.Language == HubTexts.EnglishLanguage && new PreferencesStore(PreferencesPathFor(shell.Workspace.Store.Root)).Load().Language == HubTexts.EnglishLanguage, "点语言行真的换过去并落了盘（当前 " + HubStrings.Language + "）");
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(RowTicked(BottomMenuSubmenuRows(shell, MainWindow.MenuRowTag.Language).Single(item => item.Tag as string == HubTexts.EnglishLanguage)), "重开菜单，勾落在英文那一行了：语言行的勾和主题行一样跟着当前状态重建");
        BottomMenuSubmenuRows(shell, MainWindow.MenuRowTag.Language).Single(item => item.Tag as string == HubTexts.ChineseLanguage).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(HubStrings.Language == HubTexts.ChineseLanguage, "再点另一行换回中文：菜单每开一次都是按当前状态重建的，反向也一样走得通");
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var menuSettings = shell.BottomMenuForCheck!.Items.OfType<MenuItem>().First();
        menuSettings.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(shell.SettingsOverlayVisibleForCheck, "弹出菜单里的「设置」真的升起设置浮层（事件接线有效）");
        Check(shell.CurrentPage is ToolchainsPage, "从菜单进浮层也不换底下那页：浮层是一次覆盖，不是一次导航");
        var projects = (ProjectsPage)shell.NavigateTo("Projects");
        projects.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(NamedDescendant<TextBox>(projects, "SearchBox")is { IsVisible: true } && NamedDescendant<Button>(projects, "OpenExistingButton")is { IsVisible: true } && NamedDescendant<Button>(projects, "NewProjectButton")is { IsVisible: true } && projects.GridForCheck.Columns.Count == 6, "项目页以搜索/添加/新建为主入口，列表保留完整信息列和紧凑列");
        var originalProjects = shell.Workspace.State.Projects.ToArray();
        var projectA = new ProjectEntry
        {
            Name = "Very long project name that must wrap without clipping 世界项目",
            Path = System.IO.Path.Combine(scratchRoot, "fixture-project-alpha"),
            Version = "3.0.0",
            Channel = "official-preview",
            Platform = "macos-arm64",
            Configuration = "Release",
            ProjectType = "lua",
            BuildStatus = "Succeeded",
            LastOpened = DateTimeOffset.Now.AddDays(-2),
        };
        var projectB = new ProjectEntry
        {
            Name = "SecondFixture",
            Path = System.IO.Path.Combine(scratchRoot, "fixture-project-beta"),
            Version = "2.11.5",
            Channel = "local",
            Platform = "wasm32",
            Configuration = "Debug",
            ProjectType = "cpp",
            BuildStatus = "Not built",
            LastOpened = DateTimeOffset.Now,
        };
        shell.Workspace.State.Projects.Clear();
        shell.Workspace.State.Projects.AddRange([projectA, projectB]);
        projects.Reload();
        var allRows = projects.RowsForCheck;
        Check(allRows.Length == 2 && allRows[0].Project == projectB && allRows.Any(x => x.Target.Length > 0 && x.Engine.Contains("official-preview", StringComparison.Ordinal) && x.ConfigurationAndStatus.Contains(HubStrings.Get("Succeeded"), StringComparison.Ordinal) && x.LastOpened.Length > 0 && x.CompactDetails.Contains(HubStrings.Get("lua"), StringComparison.Ordinal)), "项目行提供完整目标/配置状态/引擎渠道/时间及脚本类型，默认按最近打开倒序（实际 " + string.Join(" ｜ ", allRows.Select(x => x.Project.Name + "：" + x.CompactDetails)) + "）");
        projects.GridForCheck.SelectedItem = allRows[0];
        projects.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var rowActions = projects.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("project-row-action")).ToArray();
        Check(rowActions.Length >= 8 && rowActions.Count(button => button.Tag is ProjectEntry entry && entry == projectB) >= 4 && rowActions.Where(button => button.Tag is ProjectEntry).All(button => button.Content is not null), "项目行在键盘选择后显示打开/构建/运行/更多动作且带无障碍名称");
        projects.SearchForCheck.Text = "FIXTURE-PROJECT-ALPHA";
        // The rows are read after the dispatcher pass that applies the filter: the box raises its change event
        // there rather than inside the setter, and the read before it would see the unfiltered list. Same
        // "read it after the frame" rule as every other view assertion here.
        Dispatcher.UIThread.RunJobs();
        Check(projects.RowsForCheck.Select(x => x.Project).SequenceEqual([projectA]) && ReferenceEquals(shell.Workspace.SelectedProject, projectB), "项目搜索忽略大小写匹配完整路径，筛选不改动共享的已选项目（匹配到 " + string.Join(" / ", projects.RowsForCheck.Select(x => x.Project.Name)) + "，共享选中 " + (shell.Workspace.SelectedProject?.Name ?? "<null>") + "）");
        projects.SearchForCheck.Text = "no-such-project";
        Dispatcher.UIThread.RunJobs();
        var clearFilters = NamedDescendant<Button>(projects, "ClearFiltersButton");
        Check(projects.RowsForCheck.Length == 0 && clearFilters is { IsVisible: true }, "无匹配结果和清除条件入口会显示（行 " + projects.RowsForCheck.Length + "，清除入口 " + (clearFilters?.IsVisible.ToString() ?? "无") + "）");
        projects.SearchForCheck.Text = "";
        projects.EngineFilterForCheck.SelectedItem = projects.EngineFilterForCheck.Items.Cast<ProjectFilterChoice>().Single(x => x.Value == "3.0.0|official-preview");
        projects.PlatformFilterForCheck.SelectedItem = projects.PlatformFilterForCheck.Items.Cast<ProjectFilterChoice>().Single(x => x.Value == "macos-arm64");
        projects.Reload();
        Check(projects.RowsForCheck.Select(x => x.Project).SequenceEqual([projectA]), "刷新后仍保留组合引擎与平台筛选");
        projects.SearchForCheck.Text = "";
        projects.EngineFilterForCheck.SelectedIndex = 0;
        projects.PlatformFilterForCheck.SelectedIndex = 0;
        projects.SortForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        projects.UpdateLayout();
        projects.SyncColumnsForCheck();
        Check(projects.GridForCheck.Columns[5].IsVisible == (projects.GridForCheck.Bounds.Width < 840), "项目列表窄于 840px 时切换到含全部元数据的紧凑布局");
        projects.NewProjectForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        projects.ProjectNameForCheck.Text = "Draft retained on close";
        projects.CloseOverlayForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        projects.NewProjectForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(projects.OverlayVisibleForCheck && projects.ProjectNameForCheck.Text == "Draft retained on close", "关闭并重新打开新建覆盖层会保留草稿");
        projects.CancelOverlayForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Directory.CreateDirectory(projectA.Path);
        projects.ShowTrashConfirmationForCheck(projectA);
        projects.UpdateLayout();
        Check(projects.OverlayVisibleForCheck && projects.TrashNameForCheck.Text == projectA.Name && projects.TrashPathForCheck.Text == projectA.Path && projects.CancelOverlayForCheck.IsFocused, "回收确认显示项目名和绝对路径，并将默认焦点留给取消");
        projects.CloseOverlayForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!projects.OverlayVisibleForCheck && Directory.Exists(projectA.Path) && shell.Workspace.State.Projects.Contains(projectA), "关闭回收确认等同取消，不触碰目录或项目记录");
        shell.Workspace.State.Projects.Clear();
        shell.Workspace.State.Projects.AddRange(originalProjects);
        projects.Reload();
        Directory.Delete(projectA.Path);
        Check(NamedDescendant<StackPanel>(projects, "EmptyProjectsPanel")is { IsVisible: true } && NamedDescendant<StackPanel>(projects, "EmptyActions")is { IsVisible: true } && NamedDescendant<Button>(projects, "ClearFiltersButton")is { IsVisible: false }, "尚无项目时显示添加/新建入口，与无匹配结果状态区分");
        // The launch plan is the one part of this flow that fails **outside** the process: a wrong switch makes
        // devenv print "Invalid Command Line. Unknown Switch" and exit instead of opening, and a bundle handed to
        // the process starter throws "FileName should not be a directory". Nothing in the build catches either, so
        // pin both conventions — and pin the refusal, since a folder mistaken for an editor is that same crash.
        var editorFolder = System.IO.Path.Combine(scratchRoot, "SampleProject");
        var plainPlan = CodeEditors.LaunchPlan(System.IO.Path.Combine(scratchRoot, "sample-editor.exe"), editorFolder);
        var bundlePlan = CodeEditors.LaunchPlan("/Applications/Visual Studio Code.app", editorFolder);
        Check(plainPlan.FileName == System.IO.Path.Combine(scratchRoot, "sample-editor.exe") && plainPlan.Arguments.SequenceEqual(new[] { editorFolder }) && bundlePlan.FileName == "/usr/bin/open" && bundlePlan.Arguments.SequenceEqual(new[] { "-a", "/Applications/Visual Studio Code.app", editorFolder }) && !CodeEditors.Accepts(scratchRoot), "启动方式由路径决定：可执行文件直接带目录，应用包交给 open -a（都不带开关，devenv 的 /OpenFolder 会直接报错" + "而不打开），且普通目录不会被当成编辑器（实际 " + string.Join(" ", bundlePlan.Arguments) + "）");
        var parsedLine = MarkdownMessageRenderer.ParseLocalFileTarget("HubTexts.cs:661");
        var parsedColumn = MarkdownMessageRenderer.ParseLocalFileTarget("HubTexts.cs:661:12");
        var parsedFragment = MarkdownMessageRenderer.ParseLocalFileTarget("HubTexts.cs#L661");
        var parsedAbsolute = MarkdownMessageRenderer.ParseLocalFileTarget(System.IO.Path.Combine(scratchRoot, "HubTexts.cs") + ":661");
        Check(parsedLine.Path == "HubTexts.cs" && parsedLine.Line == 661 && parsedColumn.Path == "HubTexts.cs" && parsedColumn.Line == 661 && parsedColumn.Column == 12 && parsedFragment.Path == "HubTexts.cs" && parsedFragment.Line == 661 && parsedAbsolute.Path == System.IO.Path.Combine(scratchRoot, "HubTexts.cs") && parsedAbsolute.Line == 661, "Markdown 本地文件链接解析相对/绝对路径、:行号、:行号:列号 和 #L行号");
        Check(Throws<ArgumentException>(() => shell.NavigateTo("Nope")), "未知页面键抛 ArgumentException（而不是显示一个空宿主）");
        // Go through the real event path: changing IsChecked should trigger navigation. Only this proves the XAML/code wiring works —
        // the assertions above that call NavigateTo directly can't prove it.
        // Leave the target page first — NavigateTo now highlights synchronously, so staying on the same page and setting true triggers no event,
        // turning this assertion into an always-true decoration.
        shell.NavigateTo("Toolchains");
        shell.NavAssistant.IsChecked = true;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.CurrentPage is ChatPanel && shell.NavToolchains.IsChecked != true, "设置 NavAssistant.IsChecked 真的触发了导航（事件接线有效）");
        shell.SetStatus("状态栏自检");
        Check(shell.Status.Text == "状态栏自检", "SetStatus 写入底部状态栏");
        // Default-page assertion: matching the WPF version, startup lands on the **Projects** page (WPF used NavProjects IsChecked="True").
        var fresh = Fixture.OpenShell(scratchRoot);
        fresh.Show();
        fresh.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(fresh.CurrentPage is ProjectsPage && fresh.NavProjects.IsChecked == true, "启动默认停在项目页，与 WPF 版一致");
        fresh.Close();
        return shell;
    }

    internal async Task CheckInstallsPageAsync(string scratchRoot, MainWindow shell)
    {
        var installs = (InstallsPage)shell.NavigateTo("Installs");
        await Task.Yield();
        installs.UpdateLayout();

        var grid = NamedDescendant<DataGrid>(installs, "EnginesGrid");
        var rows = (grid?.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
        Check(rows == 2,
            "引擎页从 hub-state.json 读出 2 个引擎（实际 " + rows + "）");
        Check(grid is not null && grid.ItemsSource is not null,
            "引擎页的 DataGrid 已绑定 ItemsSource");

        // Headers are DynamicResource too. DataGrid columns are **not in the visual tree**, so whether
        // DynamicResource can resolve to Application.Resources must be tested for real — failing to resolve
        // means "blank headers", with no error at build or binding time. The columns are template columns now:
        // each Header is the TextBlock the resource built, and ToString() on it prints a type name.
        var headers = EngineHeaderTexts(grid);
        Check(headers == EngineHeaderExpectation(HubStrings.Language),
            "DataGrid 列头 DynamicResource 解析得出（实际 " + headers + "）");

        // The empty state is a panel now, and the TextBlock inside it is always present: the panel's own
        // visibility is what says whether the copy can float over the rows.
        Check(NamedDescendant<Control>(installs, "EmptyEnginesPanel") is { IsVisible: false },
            "有数据时空态文案不可见（否则会浮在数据行上面）");

        // The "validate before persisting" boundary: pointing at an arbitrary empty directory must be rejected before any state is written.
        var empty = System.IO.Path.Combine(scratchRoot, "not-an-engine");
        System.IO.Directory.CreateDirectory(empty);
        Check(Throws<InvalidDataException>(() => StateStore.ValidateEngine(empty)),
            "导入空目录被 ValidateEngine 拒绝（导入路径不会先写坏状态）");

        // Empty data root → empty state visible, 0 rows. This is the only scenario where the empty-state copy is seen, worth asserting on its own.
        var blankRoot = System.IO.Path.Combine(scratchRoot, "blank-root");
        System.IO.Directory.CreateDirectory(blankRoot);
        var emptyShell = NewShell(blankRoot);
        emptyShell.Show();
        emptyShell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var emptyPage = (InstallsPage)emptyShell.NavigateTo("Installs");
        emptyPage.UpdateLayout();
        var emptyRows = (NamedDescendant<DataGrid>(emptyPage, "EnginesGrid")?.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
        Check(emptyRows == 0 && NamedDescendant<Control>(emptyPage, "EmptyEnginesPanel") is { IsVisible: true }
              && NamedDescendant<TextBlock>(emptyPage, "EmptyEngines") is { Text: { Length: > 0 } },
            "空数据根下达 0 行且空态文案可见");
        emptyShell.Close();

        // The default engine is shell state, so switching it has to reach the shell's own chrome and not only the
        // pages: the rail's bottom-left line is derived from DefaultEnginePath, and a shell that wrote it only
        // inside InitializeChrome kept saying the previous version until the next restart, data-root switch or
        // language change. The press goes through the real button, because "the event chain is wired" is the half
        // that calling the workspace directly would not prove.
        var railEngine = shell.Workspace.State.Engines
            .FirstOrDefault(engine => engine.Path == shell.Workspace.State.DefaultEnginePath);
        var otherEngine = railEngine is null
            ? null
            : shell.Workspace.State.Engines.FirstOrDefault(engine => engine.Path != railEngine.Path);
        // The press is per row now: "set default" lives in the row's Actions column and is disabled on the
        // default row itself. So the check presses the button a hand would press on that row, and reads the
        // row's own enabled state, rather than calling the workspace underneath it.
        bool PressSetDefault(EngineEntry engine)
        {
            if (grid is null) return false;
            installs.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            grid.SelectedItem = installs.RowsForCheck.FirstOrDefault(row => row.Engine.Path == engine.Path);
            Dispatcher.UIThread.RunJobs();
            var button = EngineRowButton(grid, engine, "SetDefault");
            if (button is not { IsEnabled: true }) return false;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return true;
        }

        if (railEngine is null || otherEngine is null || grid is null || !PressSetDefault(otherEngine))
        {
            // A failed assertion rather than an unguarded press: a change whose work delegate throws (no
            // selection) opens the typed error dialog, and under --verify-shell that modal has nobody to click it.
            Check(false, "切换默认引擎的断言要两行引擎，另一行带一个可按的「设为默认」（实际 "
                + shell.Workspace.State.Engines.Count + " 个引擎"
                + (grid is null ? "，没有表格" : "，按钮按不下去") + "）");
            return;
        }

        await WaitUntilAsync(() => shell.RailEngineLineForCheck == "AXMOL " + otherEngine.Version);
        Check(shell.Workspace.State.DefaultEnginePath == otherEngine.Path
              && shell.RailEngineLineForCheck == "AXMOL " + otherEngine.Version,
            "引擎页「设为默认」后侧栏左下角当场换成新版本（实际「" + shell.RailEngineLineForCheck + "」）");

        // A change that is still running refuses the next one, so the gate has to be empty before the second press.
        await WaitUntilAsync(() => !shell.Workspace.IsBusy);

        // Put the fixture back: the settings group reads the default engine, and every group after this one assumes
        // the fixture's own selection rather than this check's.
        var restoredDefault = PressSetDefault(railEngine);
        await WaitUntilAsync(() => shell.RailEngineLineForCheck == "AXMOL " + railEngine.Version);
        Check(restoredDefault
              && shell.Workspace.State.DefaultEnginePath == railEngine.Path
              && shell.RailEngineLineForCheck == "AXMOL " + railEngine.Version,
            "默认引擎与侧栏左下角都还给了 fixture（" + railEngine.Version + "）");

        await Task.CompletedTask;
    }

    internal async Task CheckSettingsPageAsync(string scratchRoot, MainWindow shell)
    {
        await PublicReleaseSourceSelfCheck.Run(Check, Path.Combine(scratchRoot, "public-updates"));
        var updateLog = new HubLog(Path.Combine(scratchRoot, "update-failure-logs"));
        var failedUpdates = new UpdateService
        {
            WriteLog = updateLog.Write,
            ManagerOverrideForCheck = () => throw new InvalidOperationException("Update manager fixture failure")
        };
        var failedCheck = await failedUpdates.CheckAsync();
        Check(failedCheck.Result == UpdateService.CheckResult.Failed && !failedUpdates.IsInstalled
            && File.ReadAllText(updateLog.FilePath).Contains("Update manager fixture failure", StringComparison.Ordinal),
            "更新管理器和安装识别失败不会抛出，且会写入 Hub 日志");
        var preferencesPath = PreferencesPathFor(scratchRoot);
        var settings = OpenSettingsOn(shell, "appearance");

        // --- 5.1 Readings when the page opens ---
        Check(settings.DeclaredLanguages.SequenceEqual(new[] { HubTexts.ChineseLanguage, HubTexts.EnglishLanguage }),
            "语言下拉项通过 Tag 声明的标记与受支持语言一致（实际 " + string.Join(", ", settings.DeclaredLanguages) + "）");

        Check(settings.SelectedLanguage == HubStrings.Language,
            "设置页打开时选中的就是当前语言（" + settings.SelectedLanguage + "）");

        // The card shows one group at a time, so a check that reads another group has to ask for it first.
        // Nothing below would notice — an IsVisible=false subtree is still in the tree and a walk still finds
        // it — which is precisely why the read has to name the group it is looking at.
        settings.SelectCategoryForCheck("updates");
        var expectedUpdateChannel = HubReleaseInfo.IsPrereleaseBuild ? UpdateChannels.Preview : UpdateChannels.Stable;
        Check(settings.DeclaredUpdateChannels.SequenceEqual(UpdateChannels.All)
              && settings.SelectedUpdateChannel == expectedUpdateChannel
              && settings.UpdateChannelLabelsAreLocalizedForCheck
              && settings.UpdateChannelPickerEnabledForCheck == !HubReleaseInfo.IsPrereleaseBuild,
            "更新设置声明 Stable / Preview、默认 Stable，标签已本地化且预发布构建锁定 Preview");
        if (HubReleaseInfo.IsPrereleaseBuild)
        {
            Check(UpdateChannels.IncludesPrereleases(UpdateService.Instance.UpdateChannel, HubReleaseInfo.IsPrereleaseBuild),
                "预发布构建即使偏好仍为 Stable 也强制纳入预发布更新");
        }
        else
        {
            settings.SelectUpdateChannel(UpdateChannels.Preview);
            Check(settings.SelectedUpdateChannel == UpdateChannels.Preview
                  && new PreferencesStore(preferencesPath).Load().UpdateChannel == UpdateChannels.Preview
                  && UpdateService.Instance.UpdateChannel == UpdateChannels.Preview,
                "选择 Preview 会持久化并更新客户端通道");
            settings.SelectUpdateChannel(UpdateChannels.Stable);
            Check(new PreferencesStore(preferencesPath).Load().UpdateChannel == UpdateChannels.Stable
                  && UpdateService.Instance.UpdateChannel == UpdateChannels.Stable,
                "切回 Stable 会持久化且保持自动化自检离线");
        }

        settings.SelectCategoryForCheck("storage");
        Check(NamedDescendant<TextBox>(settings, "DataLocation")?.Text == new StateStore(scratchRoot).Root,
            "数据目录显示的是实际数据根而不是默认目录");

        settings.SelectCategoryForCheck("permission");
        // Both switches ship on, and the pair still has to prove that they are independent and that this one
        // checkbox writes both stored keys — the checkbox is the product's single answer even though two legacy
        // preferences carry it.
        Check(NamedDescendant<CheckBox>(settings, "WebFetchCheck")?.IsChecked == true
              && NamedDescendant<CheckBox>(settings, "WebSearchCheck")?.IsChecked == true
              && settings.AllowWebSearchForCheck,
            "网页抓取与联网搜索默认开启，联网搜索由一个总开关控制");
        var webSearchCheck = NamedDescendant<CheckBox>(settings, "WebSearchCheck");
        if (webSearchCheck is not null) webSearchCheck.IsChecked = false;
        var optedOutPreferences = new PreferencesStore(preferencesPath).Load();
        Check(!settings.AllowWebSearchForCheck && !optedOutPreferences.AllowHostedWebSearch
              && !optedOutPreferences.AllowDeepSeekWebApi && optedOutPreferences.AllowOutboundWebFetch,
            "关掉联网搜索总开关会同时写入两个旧偏好，且不改变网页抓取权限");
        if (webSearchCheck is not null) webSearchCheck.IsChecked = true;
        var optedInPreferences = new PreferencesStore(preferencesPath).Load();
        Check(settings.AllowWebSearchForCheck && optedInPreferences.AllowHostedWebSearch
              && optedInPreferences.AllowDeepSeekWebApi && optedInPreferences.AllowOutboundWebFetch,
            "再打开联网搜索总开关会把两个旧偏好一起写回开启状态");

        var projectLocation = NamedDescendant<TextBox>(settings, "DefaultProjectLocation")?.Text;
        Check(projectLocation == HubTexts.Get("NotSelected", HubStrings.Language),
            "未设置默认项目目录时显示「尚未选择」（实际 " + projectLocation + "）");

        var defaultEngine = NamedDescendant<TextBlock>(settings, "DefaultEngine")?.Text;
        Check(defaultEngine is { Length: > 0 } && defaultEngine.Contains("2.11.5", StringComparison.Ordinal)
              && defaultEngine != HubTexts.Get("NoDefault", HubStrings.Language),
            "默认引擎显示的是 fixture 里那个引擎（实际 " + defaultEngine + "）");

        // The launch convention follows the *path*, never the product's name or its catalog row: VS Code is an
        // executable on Windows and a bundle on macOS, and a bundle started as a program is the crash that reached
        // the user ("FileName should not be a directory"). An id the catalog does not know must keep working too —
        // a browsed-in application becomes a custom preset instead of a binding nothing can launch.
        var bundlePath = "/Applications/Visual Studio Code.app";
        var executablePath = Path.Combine("Tools", "Code.exe");
        var customPreset = CodeEditors.Preset(new CodeEditor(CodeEditors.CustomId, bundlePath));
        Check(CodeEditors.LaunchFor(bundlePath) == EditorLaunch.MacBundle
              && CodeEditors.LaunchFor(executablePath) == EditorLaunch.Executable
              && CodeEditors.LaunchFor(Path.Combine("Tools", "code")) == EditorLaunch.Executable
              && customPreset.DisplayName == "Visual Studio Code",
            "启动方式由路径判定（.app 走 open -a，其余直接执行），未知 id 也能还原成可用编辑器（探针 " + bundlePath + "）");

        // The two old slots must fold into the one default exactly once, carrying the path over untouched: a
        // migration that re-validated would be a migration that can silently drop the user's choice.
        var legacyState = new HubState { CodeExecutable = @"C:\Tools\Microsoft VS Code\Code.exe" };
        Check(legacyState.MigrateEditors() && legacyState.CodeEditor is { Id: "vscode" } && legacyState.CodeExecutable is null
              && !legacyState.MigrateEditors()
              && legacyState.CodeEditor.Path == @"C:\Tools\Microsoft VS Code\Code.exe",
            "旧的两个编辑器槽位一次性折叠成默认编辑器，路径原样保留且重复迁移不再改写");

        // Which editor a press uses — the project's own, then the default, then what was detected — is the whole
        // reason one settings slot can serve someone who owns two IDEs, and the reason a deleted application falls
        // through instead of stranding the button on "not selected".
        var detectedPath = Path.Combine(scratchRoot, "detected-editor.exe");
        var globalPath = Path.Combine(scratchRoot, "global-editor.exe");
        var projectPath = Path.Combine(scratchRoot, "project-editor.exe");
        File.WriteAllText(detectedPath, "");
        File.WriteAllText(globalPath, "");
        File.WriteAllText(projectPath, "");
        var editorProject = new ProjectEntry { Name = "SampleProject", Path = Path.Combine(scratchRoot, "SampleProject"), Platform = "windows-x64" };
        var editorState = new HubState { CodeEditor = new CodeEditor(CodeEditors.CustomId, globalPath) };
        var editorDetected = new[] { new EditorBinding(new EditorPreset("vscode", "VS Code"), detectedPath) };
        var byDefault = CodeEditors.Resolve(editorProject, editorDetected, editorState);
        editorProject.CodeEditor = new CodeEditor(CodeEditors.CustomId, projectPath);
        var byProject = CodeEditors.Resolve(editorProject, editorDetected, editorState);
        editorProject.CodeEditor = new CodeEditor(CodeEditors.CustomId, Path.Combine(scratchRoot, "removed-editor.exe"));
        var byFallback = CodeEditors.Resolve(editorProject, editorDetected, editorState);
        editorState.CodeEditor = null;
        var byDetection = CodeEditors.Resolve(editorProject, editorDetected, editorState);
        Check(byDefault?.Path == globalPath && byProject?.Path == projectPath && byFallback?.Path == globalPath
              && byDetection?.Path == detectedPath,
            "编辑器解析顺序：项目自身 → 全局默认 → 探测结果，项目路径失效时回落（实际 "
            + byDefault?.Path + " / " + byProject?.Path + " / " + byFallback?.Path + " / " + byDetection?.Path + "）");

        // The download-speed tooltip rests on two pure functions that fail **silently** if wrong: a wrong
        // total still yields a plausible-looking rate, and an off-by-1024 still reads like a speed.
        var fullPackage = new VelopackAsset { FileName = "axmol-hub-1.0.0-win-x64-full.nupkg", Size = 100_000_000 };
        var smallDelta = new VelopackAsset { FileName = "axmol-hub-1.0.0-win-x64-delta.nupkg", Size = 2_000_000 };
        var oversizedDelta = new VelopackAsset { FileName = "axmol-hub-1.0.0-win-x64-delta.nupkg", Size = 200_000_000 };
        var noBase = UpdateService.DescribeDownload(new UpdateInfo(fullPackage, false));
        var withDelta = UpdateService.DescribeDownload(new UpdateInfo(fullPackage, false, fullPackage, [smallDelta]));
        var deltaBiggerThanPackage = UpdateService.DescribeDownload(new UpdateInfo(fullPackage, false, fullPackage, [oversizedDelta]));
        Check(noBase == (100_000_000L, 100)
              && withDelta == (2_000_000L, 70)
              && deltaBiggerThanPackage == (100_000_000L, 100),
            "下载速率换算的总量与阶段：无基线→整包(0-100%)，有增量→增量之和(0-70%)，增量大于整包→回落整包");

        Check(UpdateService.FormatSpeed(0) == "—"
              && UpdateService.FormatSpeed(512) == "512 B/s"
              && UpdateService.FormatSpeed(2048) == "2 KB/s"
              && UpdateService.FormatSpeed(3.5 * 1024 * 1024) == "3.5 MB/s",
            "下载速率文案按 B/KB/MB 分档，未知速率显示占位符，且与界面语言无关");

        // The version has to survive past the "found" moment. The card no longer renders the download's
        // phases — the strip's slot and the popover's rows do — so the rule is now the simpler one it was
        // always serving: the number stays where the user read it, and the restart tip names what it will
        // install. Assert the rendered text, not "the service knows a version".
        var pendingAsset = new VelopackAsset
        {
            FileName = "Axmol.Hub-9.9.9-win-x64-full.nupkg",
            Size = 1_000_000,
            Version = SemanticVersion.Parse("9.9.9"),
        };
        var pendingUpdate = new UpdateInfo(pendingAsset, false);

        // Back to the group the status line lives in: the storage reads above moved the card away from it.
        settings.SelectCategoryForCheck("updates");
        UpdateService.Instance.SetPendingForCheck(pendingUpdate);
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var foundLine = settings.UpdateStatusText;
        Check(foundLine == string.Format(HubTexts.Get("UpdateReady", HubStrings.Language), "9.9.9"),
            "检测到新版本时卡片的状态行说出版本号（实际 " + foundLine + "）");

        // The card's one button stays where it is once the check has found something. The old shape took it
        // away and left the user with a version number and nothing to press, while the fetch that number
        // pointed at lives on the sidebar's update button — so what the card owes instead is the sentence
        // saying where the bytes are going to move.
        Check(settings.UpdateCardButtonVisible && settings.UpdateCardButtonEnabled
              && settings.UpdateCardButtonLabel == HubTexts.Get("CheckForUpdates", HubStrings.Language)
              && settings.UpdateHintShown
              && settings.UpdateHintText == HubTexts.Get("UpdatePendingDownloadHint", HubStrings.Language),
            "发现新版本后卡片仍留着按钮，并说清去哪开始下载（按钮「" + settings.UpdateCardButtonLabel
            + "」，说明「" + settings.UpdateHintText + "」）");

        // The transfer moving is nobody's business on this page: the line must not change, and no
        // second progress bar may exist here to disagree with the row in the popover.
        UpdateService.Instance.SetPendingForCheck(pendingUpdate, UpdateService.DownloadState.Downloading);
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(settings.UpdateStatusText == foundLine && settings.ProgressBarCount == 0,
            "下载进行中卡片既不加进度条也不换掉那行版本号（页面内进度条 " + settings.ProgressBarCount + " 个）");
        // What it does say is which phase the transfer is in and which surface holds it — and the button goes
        // dark rather than inviting a second press at a transfer that is already running.
        Check(settings.UpdateCardButtonVisible && !settings.UpdateCardButtonEnabled
              && settings.UpdateCardButtonLabel == HubTexts.Get("CheckForUpdates", HubStrings.Language)
              && settings.UpdateHintText == HubTexts.Get("UpdateDownloadingHint", HubStrings.Language),
            "下载进行中卡片把按钮留在原处只是按不动，并说下载在底部任务栏里（按钮「"
            + settings.UpdateCardButtonLabel + "」，说明「" + settings.UpdateHintText + "」）");

        UpdateService.Instance.SetPendingForCheck(pendingUpdate, UpdateService.DownloadState.Ready);
        settings.UpdateLayout();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(settings.UpdateStatusText == foundLine
              && shell.UpdateActionTipForCheck.Contains("9.9.9", StringComparison.Ordinal)
              && shell.UpdateActionVisibleForCheck
              && shell.UpdateActionShapeForCheck == "pill"
              && shell.UpdateActionLabelForCheck == HubTexts.Get("TaskRestartAction", HubStrings.Language),
            "更新就绪后版本号仍在卡片上，并且进了侧栏更新按钮的重启提示（提示 " + shell.UpdateActionTipForCheck
            + "，形态 " + shell.UpdateActionShapeForCheck + "）");
        Check(settings.UpdateCardButtonVisible && settings.UpdateCardButtonEnabled
              && settings.UpdateCardButtonLabel == HubTexts.Get("TaskRestartAction", HubStrings.Language)
              && !settings.UpdateHintShown && settings.UpdateHintText.Length == 0,
            "下载完成后卡片上那枚按钮自己变成重启，说明行让位给它（实际「" + settings.UpdateCardButtonLabel + "」）");

        // And the card must come back down: a scripted update left behind would leak into every render
        // check that follows (the shell's update button included).
        UpdateService.Instance.ClearForCheck();
        settings.UpdateLayout();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(settings.UpdateStatusText == HubTexts.Get("UpdateCheckHint", HubStrings.Language)
              && settings.UpdateCardButtonVisible
              && settings.UpdateCardButtonLabel == HubTexts.Get("CheckForUpdates", HubStrings.Language)
              && !settings.UpdateHintShown
              && shell.TaskSlotTipForCheck == HubTexts.Get("TaskSlotTip", HubStrings.Language)
              && !shell.UpdateActionVisibleForCheck,
            "清掉脚本更新后卡片按钮回到检查、说明行收起，槽位提示与侧栏更新按钮都回到未检查的初始态（状态行 "
            + settings.UpdateStatusText + "，按钮「" + settings.UpdateCardButtonLabel + "」）");

        // --- 5.1b The task queue's own surface ---
        // The queue's contract with the shell, read off the shell. The label matrix goes through the pure
        // function rather than a live transfer because the branches are about *counting*, and counting can
        // be asserted without parking three real downloads behind it. What then runs live is the one thing
        // a pure function cannot prove: that the slot, the popover and the strip's geometry really move.
        Check(!shell.TaskSlotVisibleForCheck,
            "没有任何任务在跑时状态条的任务槽是暗的");

        static TaskQueue.Item Moving(string version, int? percent = null, long? bytes = null)
        {
            var item = new TaskQueue.Item($"engine:install:{version}", "TaskEnginePackage", version);
            item.BeginRunning();
            if (percent is { } p) item.ReportPercent(p);
            if (bytes is { } b) item.ReportBytes(new DownloadProgress(b, b * 2, 2_400_000));
            return item;
        }

        static TaskQueue.Item Waiting(string version)
            => new($"engine:install:{version}", "TaskEnginePackage", version);

        var twoMoving = new[] { Moving("9.9.9"), Moving("9.9.8"), Waiting("9.9.7") };
        var twoMovingState = MainWindow.TaskSlotStateFor(twoMoving);
        Check(twoMovingState.Visible && twoMovingState.Action == MainWindow.TaskSlotAction.OpenQueue
              && twoMovingState.Label == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                  HubTexts.Get("TaskSlotFormat", HubStrings.Language), 2, 3),
            "两路在跑、第三路等待时槽说 任务 2/3，并且它不是一个动作（实际 " + twoMovingState.Label + "）");

        // One number per download, never an average of two: with a single transfer moving its own
        // percentage is the honest aggregate, and with two the count stands alone.
        var oneMoving = new[] { Moving("9.9.9", percent: 37) };
        var oneMovingState = MainWindow.TaskSlotStateFor(oneMoving);
        Check(oneMovingState.Label == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                  HubTexts.Get("TaskSlotPercentFormat", HubStrings.Language), 1, 1, 37),
            "只有一路在跑时槽带上它自己的百分比（实际 " + oneMovingState.Label + "）");

        // A failure does not steal the label while something is still moving: the count is the live fact,
        // and the failed row is inside the popover where it can be read next to the one still running.
        var failedWhileMoving = new[] { Waiting("9.9.9"), Moving("9.9.8") };
        failedWhileMoving[0].Fail("fixture: the mirror answered 503");
        var mixedState = MainWindow.TaskSlotStateFor(failedWhileMoving);
        Check(mixedState.Visible && mixedState.Action == MainWindow.TaskSlotAction.OpenQueue
              && mixedState.Label == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                  HubTexts.Get("TaskSlotFormat", HubStrings.Language), 1, 1),
            "还有路在跑时，失败的那条不抢标签（实际 " + mixedState.Label + "）");

        var allFailed = new[] { Waiting("9.9.9") };
        allFailed[0].Fail("fixture: digest mismatch");
        Check(MainWindow.TaskSlotStateFor(allFailed).Visible,
            "没有任何任务在跑、只留下一次失败时槽仍然亮着");
        Check(!MainWindow.TaskSlotStateFor(Array.Empty<TaskQueue.Item>()).Visible,
            "队列空时槽保持沉默——更新的判定已经搬去侧栏按钮，槽不再替它发言");

        // The update's decision table, on the pure function that now owns it: found (the press fetches —
        // including after a cancel, which is exactly when auto-download will not fetch again by itself),
        // downloading (the button is hidden because the strip's percentage reports),
        // ready (the pill; the dot is back because the restart is owed), and silence with no update. The
        // arm is a label change on the pill, not a state of its own.
        var outcomeAvailable = new UpdateService.CheckOutcome(
            UpdateService.CheckResult.UpdateAvailable, new UpdateInfo(new VelopackAsset
            {
                FileName = "Axmol.Hub-9.9.9-win-x64-full.nupkg",
                Size = 1_000_000,
                Version = SemanticVersion.Parse("9.9.9"),
            }, false));
        var foundState = MainWindow.UpdateActionStateFor(outcomeAvailable, UpdateService.DownloadState.None, false);
        var downloadingState = MainWindow.UpdateActionStateFor(outcomeAvailable, UpdateService.DownloadState.Downloading, false);
        var readyState = MainWindow.UpdateActionStateFor(outcomeAvailable, UpdateService.DownloadState.Ready, false);
        var armedState = MainWindow.UpdateActionStateFor(outcomeAvailable, UpdateService.DownloadState.Ready, true);
        var silentState = MainWindow.UpdateActionStateFor(null, UpdateService.DownloadState.None, false);
        var upToDateState = MainWindow.UpdateActionStateFor(
            new UpdateService.CheckOutcome(UpdateService.CheckResult.UpToDate, null),
            UpdateService.DownloadState.None, false);
        Check(foundState.Kind == MainWindow.UpdateActionKind.Found
              && foundState.LabelKey is null && foundState.TipKey == "TaskDownloadSlotTip",
            "待下载状态 = 圆形 + 红点（无标签，提示带版本）：" + foundState.Kind + "/" + foundState.TipKey);
        Check(downloadingState.Kind == MainWindow.UpdateActionKind.Hidden
              && downloadingState.LabelKey is null && downloadingState.TipKey is null,
            "下载中状态 = 按钮整个不在屏上，进度归状态条：" + downloadingState.Kind);
        Check(readyState.Kind == MainWindow.UpdateActionKind.Ready
              && readyState.LabelKey == "TaskRestartAction" && readyState.TipKey == "UpdateRestartActionTip",
            "就绪状态 = 胶囊「重启完成更新」：" + readyState.Kind + "/" + readyState.LabelKey);
        Check(armedState.LabelKey == "TaskRestartArmed" && armedState.LabelKey != readyState.LabelKey,
            "忙时第一下只把胶囊换成再点一次（" + readyState.LabelKey + " / " + armedState.LabelKey + "）");
        Check(silentState.Kind == MainWindow.UpdateActionKind.Hidden
              && upToDateState.Kind == MainWindow.UpdateActionKind.Hidden
              && silentState.LabelKey is null && silentState.TipKey is null,
            "没有更新（未检查 / 已是最新）时按钮整个不在屏上");

        // The settings card's own matrix, through its pure functions for the same reason: the branch "the
        // button became the restart" needs a finished download to reach live, and what decides it is the
        // phase, not the switch. Both words are the rail's — one action, one sentence.
        Check(SettingsPage.UpdateCardLabelKey(outcomeAvailable, UpdateService.DownloadState.None, false) == "CheckForUpdates"
              && SettingsPage.UpdateCardLabelKey(outcomeAvailable, UpdateService.DownloadState.Downloading, false) == "CheckForUpdates"
              && SettingsPage.UpdateCardLabelKey(outcomeAvailable, UpdateService.DownloadState.Ready, false) == "TaskRestartAction"
              && SettingsPage.UpdateCardLabelKey(outcomeAvailable, UpdateService.DownloadState.Ready, true) == "TaskRestartArmed"
              && SettingsPage.UpdateCardLabelKey(null, UpdateService.DownloadState.None, false) == "CheckForUpdates",
            "卡片按钮的两态只看相位：没下载完是检查，下载完才是重启，忙时是再点一次（就绪 "
            + SettingsPage.UpdateCardLabelKey(outcomeAvailable, UpdateService.DownloadState.Ready, false)
            + "／忙 " + SettingsPage.UpdateCardLabelKey(outcomeAvailable, UpdateService.DownloadState.Ready, true) + "）");
        Check(SettingsPage.UpdateCardHintKey(outcomeAvailable, UpdateService.DownloadState.None) == "UpdatePendingDownloadHint"
              && SettingsPage.UpdateCardHintKey(outcomeAvailable, UpdateService.DownloadState.Downloading) == "UpdateDownloadingHint"
              && SettingsPage.UpdateCardHintKey(outcomeAvailable, UpdateService.DownloadState.Ready) is null
              && SettingsPage.UpdateCardHintKey(null, UpdateService.DownloadState.None) is null,
            "卡片的说明行只说相位：没开始说去哪按、下载中说在任务栏、就绪交给按钮说（就绪相位 "
            + (SettingsPage.UpdateCardHintKey(outcomeAvailable, UpdateService.DownloadState.Ready) ?? "不说话") + "）");

        // And live, on the real strip: one scripted transfer that never ends.
        TaskQueue.Instance.EnqueueForCheck("engine:install:9.9.6", "TaskEnginePackage", "9.9.6");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var platesBefore = shell.WindowControlsBoundsForCheck;
        Check(shell.TaskSlotVisibleForCheck
              && shell.TaskSlotTextForCheck == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                  HubTexts.Get("TaskSlotFormat", HubStrings.Language), 1, 1),
            "脚本下载在跑时状态条真的亮着并写着 任务 1/1（实际 " + shell.TaskSlotTextForCheck + "）");

        // Through the button, not the method behind it: the assertion says 点任务槽, so the routing is what
        // has to be proven — a slot wired to nothing would pass a direct ToggleTaskPopupForCheck.
        shell.ClickTaskSlotForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.TaskPopupOpenForCheck && shell.TaskRowCountForCheck == 1,
            "点任务槽弹出任务面板，跑着的那一项有一行（实际 " + shell.TaskRowCountForCheck + " 行）");
        // The frame, not just the tree: a Popup that opens with nothing in it passes every object-graph
        // check above, and it is exactly the failure the reader would see. It has to be captured as its own
        // visual because a popup presents in its own PopupRoot, outside the window's pixels.
        var popoverShot = System.IO.Path.Combine(ScratchDirectory.Resolve("task-popover"), "rows.png");
        var popoverStats = SmokeCapture.Capture(shell.TaskPopoverForCheck!, popoverShot);
        Check(System.IO.File.Exists(popoverShot) && !popoverStats.IsBlank(),
            "队列面板真的画出了内容（" + popoverStats.Width + "x" + popoverStats.Height
            + "，distinct=" + popoverStats.DistinctColors + "）");
        Check(shell.StatusStripHeightForCheck <= 28.5
              && SameRect(shell.WindowControlsBoundsForCheck, platesBefore),
            "面板展开时状态条仍是那 28 高，三枚窗口按钮一格没动");
        shell.CloseTaskPopupForCheck();
        Dispatcher.UIThread.RunJobs();

        // Three scripted transfers, so two hold the slots and one waits. Cancelling the waiting one has to
        // end it: a cancel that only reaches a running transfer would leave this one queued, and the moment
        // a slot freed it would start downloading something the user already said no to.
        TaskQueue.Instance.EnqueueForCheck("engine:install:9.9.5", "TaskEnginePackage", "9.9.5");
        TaskQueue.Instance.EnqueueForCheck("engine:install:9.9.4", "TaskEnginePackage", "9.9.4");
        var waiting = TaskQueue.Instance.EnqueueForCheck("engine:install:9.9.3", "TaskEnginePackage", "9.9.3")
            ?? throw new InvalidOperationException("fixture: the third transfer was refused, so nothing is waiting");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(TaskQueue.Instance.Items.Count(item => item.IsMoving) == 2
              && waiting is { State: TaskQueue.Phase.Queued },
            "第三路下载停在等待，闸只有两路（实际在跑 "
            + TaskQueue.Instance.Items.Count(item => item.IsMoving) + " 路）");
        TaskQueue.Instance.Cancel("engine:install:9.9.3");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(waiting.State == TaskQueue.Phase.Cancelled,
            "取消一个还在等待的下载，它就此结束，不会等空位补上（实际 " + waiting.State + "）");
        TaskQueue.Instance.ClearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        // The scripted transfer has to go before the restart can be read: while anything is moving the slot
        // is the queue, and that is the rule the two states are distinguished by.
        TaskQueue.Instance.ClearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.TaskSlotVisibleForCheck,
            "脚本下载结束后槽自己熄灭，不留下一个报过去的行");

        // The restart press must never reach the updater here: that would replace the process running the
        // assertions. The observer is the decision, recorded instead of executed.
        var applied = "";
        UpdateService.Instance.ApplyOverrideForCheck = update => applied = update.TargetFullRelease.Version.ToString();
        UpdateService.Instance.SetPendingForCheck(pendingUpdate, UpdateService.DownloadState.Ready);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.UpdateActionVisibleForCheck
              && shell.UpdateActionShapeForCheck == "pill"
              && shell.UpdateActionLabelForCheck == HubTexts.Get("TaskRestartAction", HubStrings.Language)
              && shell.UpdateActionDotVisibleForCheck
              && !shell.TaskSlotVisibleForCheck,
            "更新就绪且队列空时：侧栏按钮是带红点的重启胶囊，状态条的槽不再跟着说一遍（胶囊「"
            + shell.UpdateActionLabelForCheck + "」，槽亮着 " + shell.TaskSlotVisibleForCheck + "）");

        // The geometry that makes it findable, decided in window space so the pixel read below measures
        // exactly the rectangle this asserts on: the button left of the gear, the dot overhanging the
        // button's top-right corner (by a few pixels — it must sit on the corner, not float off it).
        var (readyButton, readyDot, readyGear) = shell.UpdateActionGeometryForCheck();
        Check(readyButton.Right <= readyGear.X + 0.5
              && readyButton.Width > 0 && readyGear.Width > 0
              && readyDot.Width >= 10
              && readyDot.Right - readyButton.Right is > 0 and < 6
              && readyButton.Top - readyDot.Top is > 0 and < 6,
            "重启按钮长在齿轮左侧，红点 ≥10px 压着它的右上角（按钮右缘 " + Fmt(readyButton.Right)
            + " ≤ 齿轮左缘 " + Fmt(readyGear.X) + "，红点 " + Fmt(readyDot.Width) + " 见方，外探 横 "
            + Fmt(readyDot.Right - readyButton.Right) + " / 纵 " + Fmt(readyButton.Top - readyDot.Top) + "）");
        Check(shell.UpdateActionDotAboveButtonForCheck,
            "红点画在按钮之上——同一张 Grid 里子序决定叠放，两个矩形断言看不出先后");
        // The pill's corners are the gear's, and that relation is the assertion rather than a number written
        // here: the two controls share a row, so "consistent with the one beside it" is what the style is for.

        // The frame, not the tree. Two captures: the dot has to be real red ink (a dot whose Fill went null
        // keeps every bounds assertion above green while the screen shows nothing), and the pill has to be
        // more than a flat plate — the word is on it. The dot is captured as its own visual: its frame has
        // no pixel-origin to disagree with, and a blank frame *is* the defect.
        var updateDir = ScratchDirectory.Resolve("update-button");
        var dotShot = System.IO.Path.Combine(updateDir, "dot.png");
        var dotStats = SmokeCapture.Capture(shell.UpdateActionDotForCheck, dotShot);
        var dotPixels = RegionPixels(dotShot, new Rect(0, 0, dotStats.Width, dotStats.Height),
            new Size(dotStats.Width, dotStats.Height));
        var redPixels = 0;
        for (var i = 0; i + 3 < dotPixels.Length; i += 4)
        {
            // BGRA. #FF0000 is the dot, and nothing else in this row can produce a saturated red pixel —
            // the accent plate is blue, the ring and the sidebar are grays.
            if (dotPixels[i + 3] > 0xF0 && dotPixels[i + 2] > 0xC0 && dotPixels[i + 1] < 0x60 && dotPixels[i] < 0x60)
            {
                redPixels++;
            }
        }
        Check(redPixels > 0,
            "红点真的画出了红色像素（" + dotStats.Width + "x" + dotStats.Height + " 的帧里命中 "
            + redPixels + " 个）");
        var pillShot = System.IO.Path.Combine(updateDir, "pill.png");
        var pillStats = SmokeCapture.Capture(shell.UpdateActionHostForCheck, pillShot);
        Check(!pillStats.IsBlank() && pillStats.DistinctColors > 2,
            "胶囊画得出来：强调底板与文字都在帧里（" + pillStats.Width + "x" + pillStats.Height
            + "，distinct=" + pillStats.DistinctColors + "）");

        // The pill's label and tip are key lookups rendered once per state — a language switch has to
        // re-resolve them or the button keeps last language's sentence forever. Round-tripped the way the
        // menu's own rows are: to English, read the button, back to Chinese, read it again.
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        BottomMenuSubmenuRows(shell, MainWindow.MenuRowTag.Language)
            .Single(item => item.Tag as string == HubTexts.EnglishLanguage)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.UpdateActionLabelForCheck == HubTexts.Get("TaskRestartAction", HubTexts.EnglishLanguage)
              && shell.UpdateActionTipForCheck == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                  HubTexts.Get("UpdateRestartActionTip", HubTexts.EnglishLanguage), "9.9.9")
              && shell.UpdateActionShapeForCheck == "pill",
            "切到英文后胶囊与提示都跟着换（label「" + shell.UpdateActionLabelForCheck + "」，tip「"
            + shell.UpdateActionTipForCheck + "」）");
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        BottomMenuSubmenuRows(shell, MainWindow.MenuRowTag.Language)
            .Single(item => item.Tag as string == HubTexts.ChineseLanguage)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.UpdateActionLabelForCheck == HubTexts.Get("TaskRestartAction", HubTexts.ChineseLanguage),
            "切回中文按钮也回来（实际「" + shell.UpdateActionLabelForCheck + "」）");

        // Busy: the first press arms, and nothing is applied yet. The two-tap rule moved here from the
        // strip verbatim — it is the rule, not the surface, that was being asserted.
        shell.RestartInterruptsWork = () => true;
        shell.ClickUpdateActionForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.RestartArmedForCheck && applied.Length == 0
              && shell.UpdateActionLabelForCheck == HubTexts.Get("TaskRestartArmed", HubStrings.Language),
            "有活在跑时第一下只把胶囊换成再点一次，不重启（实际 " + shell.UpdateActionLabelForCheck + "）");
        shell.ClickUpdateActionForCheck();
        Check(applied == "9.9.9", "第二下才真的走重启，并且带上那个版本（实际 " + applied + "）");

        // Idle: one press is the whole action, no confirmation in the way.
        shell.RestartInterruptsWork = () => false;
        applied = "";
        shell.ClickUpdateActionForCheck();
        Check(applied == "9.9.9" && !shell.RestartArmedForCheck,
            "没有在跑的东西时重启一下就走，不要求第二下（实际 " + applied + "）");

        // The settings card's button is that same gate, not a second one. Pressed from the card while other
        // work is running, the first press arms the **shared** state — which this reads back off the rail's
        // pill, so a per-button arm hiding behind the card would fail here — and only the second press applies.
        settings.SelectCategoryForCheck("updates");
        shell.RestartInterruptsWork = () => true;
        applied = "";
        settings.ClickUpdateCardButtonForCheck();
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(settings.UpdateCardButtonLabel == HubTexts.Get("TaskRestartArmed", HubStrings.Language)
              && shell.UpdateActionLabelForCheck == HubTexts.Get("TaskRestartArmed", HubStrings.Language)
              && shell.RestartArmedForCheck && applied.Length == 0,
            "设置卡片上的重启与侧栏共用一次武装：卡片第一下只改词，侧栏胶囊同时改口，谁都没重启（卡片「"
            + settings.UpdateCardButtonLabel + "」，胶囊「" + shell.UpdateActionLabelForCheck + "」）");
        settings.ClickUpdateCardButtonForCheck();
        Check(applied == "9.9.9" && !shell.RestartArmedForCheck,
            "卡片上第二下才真的走重启，并且带上那个版本（实际 " + applied + "）");
        shell.RestartInterruptsWork = () => false;
        UpdateService.Instance.ApplyOverrideForCheck = null;

        // Everything scripted goes back down, or every render check after this inherits a lit strip — and a
        // lit rail: the update button is chrome that must not survive its own update.
        TaskQueue.Instance.ClearForCheck();
        UpdateService.Instance.ClearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.TaskSlotVisibleForCheck && !shell.TaskPopupOpenForCheck
              && !shell.UpdateActionVisibleForCheck && !shell.UpdateActionDotVisibleForCheck,
            "清掉脚本队列与脚本更新后任务槽熄灭、面板收起、更新按钮连同红点消失");

        // --- 5.1c Retry, dismiss, and the download the settings card used to own ---
        // The three things this round added, asserted where they are decided: the pure function for which
        // branch wins, the live strip and popover for what the user can actually press.
        var retryWord = HubTexts.Get("TaskRetry", HubStrings.Language);
        var dismissWord = HubTexts.Get("TaskDismiss", HubStrings.Language);
        var cancelWord = HubTexts.Get("Cancel", HubStrings.Language);

        // A failure and a pending update are two different facts, so both surfaces may be lit at once: the
        // slot for the failed row (the queue's line), the button for the update (the app's). What may never
        // come back is a 「下载更新」 *in the strip* — that sentence moved with the action.
        var outrank = new[] { Waiting("9.9.9") };
        outrank[0].Fail("fixture: digest mismatch");
        Check(MainWindow.TaskSlotStateFor(outrank).Action == MainWindow.TaskSlotAction.OpenQueue
              && MainWindow.TaskSlotStateFor(outrank).Label != HubTexts.Get("TaskDownloadAction", HubStrings.Language),
            "有失败行时槽是队列的失败计数，下载动作不进这句话（实际 " + MainWindow.TaskSlotStateFor(outrank).Label + "）");

        // The offer itself, live, and with the real fetch refused: asking Velopack here would reach GitHub.
        var autoDownloadWas = UpdateService.Instance.AutoDownload;
        var downloaded = "";
        UpdateService.Instance.DownloadOverrideForCheck = update => downloaded = update.TargetFullRelease.Version.ToString();
        UpdateService.Instance.SetPendingForCheck(pendingUpdate);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.UpdateActionVisibleForCheck
              && shell.UpdateActionShapeForCheck == "icon"
              && shell.UpdateActionGlyphForCheck == "send"
              && shell.UpdateActionDotVisibleForCheck
              && shell.UpdateActionTipForCheck.Contains("9.9.9", StringComparison.Ordinal)
              && !shell.TaskSlotVisibleForCheck,
            "有待下载的更新时侧栏出现上箭头圆形 + 红点（提示 " + shell.UpdateActionTipForCheck
            + "），状态条不替它发言（槽亮着 " + shell.TaskSlotVisibleForCheck + "）");

        shell.ClickUpdateActionForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(downloaded == "9.9.9" && TaskQueue.Instance.Items.Count == 0 && !shell.RestartArmedForCheck,
            "点下载只发起那一次下载，不入队也不武装重启（实际版本 " + downloaded + "）");

        // The hole this button exists to close: auto-download **on** is the default, and a cancelled
        // transfer puts Download back to None — before this button that state had no entry point anywhere
        // in the app (the strip only offered with auto-download off, and the settings card's own button
        // never offers the fetch: it is the check until the bytes land). The button's condition is the
        // phase's, not the setting's: cancelled or never-started, the press fetches.
        UpdateService.Instance.AutoDownload = true;
        downloaded = "";
        UpdateService.Instance.SetPendingForCheck(pendingUpdate); // Download back to None, as a cancel leaves it
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.UpdateActionVisibleForCheck && shell.UpdateActionDotVisibleForCheck
              && !shell.TaskSlotVisibleForCheck,
            "自动下载开着、下载没在跑（刚被取消过的样子）时按钮照样带红点提供重试，槽保持安静");
        shell.ClickUpdateActionForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(downloaded == "9.9.9",
            "这次点击同样真的发起下载——取消后的重试入口找到了（实际版本 " + downloaded + "）");

        UpdateService.Instance.AutoDownload = autoDownloadWas;
        UpdateService.Instance.DownloadOverrideForCheck = null;
        UpdateService.Instance.ClearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // A failed row is the one settled state with answers left to give, and the retry has to be the same
        // row: a second row for one download would make 「取消这个」 ambiguous.
        var retryIdentity = "engine:install:9.9.2";
        var firstAttempt = TaskQueue.Instance.EnqueueForCheck(retryIdentity, "TaskEnginePackage", "9.9.2")
            ?? throw new InvalidOperationException("fixture: the retry transfer was refused");
        var firstCompletion = firstAttempt.Completion;
        TaskQueue.Instance.FinishTaskForCheck(retryIdentity, TaskQueue.Phase.Failed, "fixture: 503");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        shell.OpenTaskPopupForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.TaskRowCountForCheck == 1 && shell.TaskRowActionsForCheck == retryWord + "+" + dismissWord,
            "失败的那一行给的是重试和忽略，不是取消（实际 " + shell.TaskRowActionsForCheck + "）");

        // The trap the retry could walk into: a completion source is single-shot, so reusing it hands an
        // awaiting caller an already-settled task and 「还在下载」 reads as 「装完了」.
        shell.ClickTaskRowActionForCheck(retryIdentity, retryWord);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(firstCompletion.IsCompleted && !firstAttempt.Completion.IsCompleted
              && firstAttempt.State is TaskQueue.Phase.Queued or TaskQueue.Phase.Running
              && shell.TaskRowCountForCheck == 1 && shell.TaskRowActionsForCheck == cancelWord,
            "重试让同一行重新计时，旧的完成承诺不再算数（实际 " + firstAttempt.State + "）");

        TaskQueue.Instance.FinishTaskForCheck(retryIdentity, TaskQueue.Phase.Failed, "fixture: 503 again");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.TaskRowActionsForCheck == retryWord + "+" + dismissWord,
            "又失败一次，行仍然给那两条路（实际 " + shell.TaskRowActionsForCheck + "）");
        shell.ClickTaskRowActionForCheck(retryIdentity, dismissWord);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(TaskQueue.Instance.Items.Count == 0 && shell.TaskRowCountForCheck == 0
              && !shell.TaskSlotVisibleForCheck,
            "忽略之后这一行不再报，槽也跟着熄灭");

        // A cancelled row used to get *neither* button — no retry, no dismiss — so with auto-download on
        // (the default) a cancelled update download left no way to ask for it again anywhere in the app.
        // Two transfers on purpose: while the second one runs, the slot stays lit and keeps the popover's
        // rows alive, so the cancelled row's own buttons are what gets pressed here rather than a rebuild.
        var cancelA = "hub:update:7.7.7";
        var cancelB = "engine:install:7.7.6";
        TaskQueue.Instance.EnqueueForCheck(cancelA, "TaskHubUpdate", "7.7.7");
        TaskQueue.Instance.EnqueueForCheck(cancelB, "TaskEnginePackage", "7.7.6");
        TaskQueue.Instance.Cancel(cancelA);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        shell.OpenTaskPopupForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(TaskQueue.Instance.Items.FirstOrDefault(item => item.Identity == cancelA)?.State
              == TaskQueue.Phase.Cancelled
              && shell.TaskRowActionsForCheck == retryWord + "+" + dismissWord + " / " + cancelWord,
            "取消的那一行给重试和忽略，跑着的那一行给取消（实际 " + shell.TaskRowActionsForCheck + "）");
        shell.ClickTaskRowActionForCheck(cancelA, retryWord);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(TaskQueue.Instance.Items.FirstOrDefault(item => item.Identity == cancelA)?.State
              is TaskQueue.Phase.Queued or TaskQueue.Phase.Running,
            "点重试把取消的行原地拉回队列（实际 "
            + TaskQueue.Instance.Items.FirstOrDefault(item => item.Identity == cancelA)?.State + "）");
        TaskQueue.Instance.Cancel(cancelA);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        shell.ClickTaskRowActionForCheck(cancelA, dismissWord);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!TaskQueue.Instance.Items.Any(item => item.Identity == cancelA),
            "再取消一次后也能被忽略，行彻底离场");
        shell.CloseTaskPopupForCheck();
        TaskQueue.Instance.ClearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var dupIdentity = "engine:install:9.9.1";
        TaskQueue.Instance.EnqueueForCheck(dupIdentity, "TaskEnginePackage", "9.9.1");
        TaskQueue.Instance.FinishTaskForCheck(dupIdentity, TaskQueue.Phase.Failed, "fixture: first attempt");
        TaskQueue.Instance.EnqueueForCheck(dupIdentity, "TaskEnginePackage", "9.9.1");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(TaskQueue.Instance.Items.Count(item => item.Identity == dupIdentity) == 1,
            "同一次下载的旧失败行让位给新尝试，不并列成两行（实际 "
            + TaskQueue.Instance.Items.Count(item => item.Identity == dupIdentity) + " 行）");

        // An estimate is not a measurement. The row's measure column stays the transport's own number, and
        // what its owner guessed lives in the tooltip — the shape the settings card's bar used to have.
        var estimateIdentity = "hub:update:8.8.8";
        var estimateRow = TaskQueue.Instance.EnqueueForCheck(estimateIdentity, "TaskHubUpdate", "8.8.8")
            ?? throw new InvalidOperationException("fixture: the estimate transfer was refused");
        estimateRow.ReportPercent(42);
        estimateRow.ReportEstimate(1_900_000, 3.5 * 1024 * 1024);
        // The rows only exist while the popover is open, and dismissing the last one closed it: reopen so
        // this reads the live row rather than a stale one left over from the previous cell.
        shell.OpenTaskPopupForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var rowReading = shell.TaskRowMeasureForCheck(estimateIdentity);
        Check(rowReading.EndsWith("|42%", StringComparison.Ordinal)
              && rowReading.StartsWith(HubTexts.Get("TaskEstimateSize", HubStrings.Language), StringComparison.Ordinal)
              && rowReading.Contains("MB/s", StringComparison.Ordinal),
            "更新行只报百分比，估算的体积与速率留在 tooltip（实际 " + rowReading + "）");

        // The round's original defect was a settings-page button that restarted *around* the armed second tap.
        // The card now carries a restart word on purpose — through that same gate, asserted where the card is
        // ready — so what is left to read here is the other half of the rule: while nothing is ready, the page
        // holds no restart word at all (the card's button has gone back to 检查更新), so nothing on it can be
        // reached as a restart and bypass the confirmation.
        var restartWords = settings.GetVisualDescendants().OfType<Button>()
            .Select(button => button.Content as string ?? "")
            .Where(text => text.Contains("重启", StringComparison.Ordinal))
            .ToList();
        Check(restartWords.Count == 0
              && settings.UpdateCardButtonLabel == HubTexts.Get("CheckForUpdates", HubStrings.Language),
            "没有就绪的更新时设置页一个重启字样都没有，卡片按钮也已退回检查（实际 "
            + string.Join("/", restartWords) + "，按钮「" + settings.UpdateCardButtonLabel + "」）");

        // --- 5.1d The change on this surface, and the page it no longer freezes ---
        // A build is now a task row, so the gate that used to grey everything has to answer a refused press
        // with something the user can read. Three facts, all read off the shell rather than inferred: the
        // strip counts the change as its own row, the surfaces stay pressable, and the refusal is a sentence
        // that costs no dialog.
        // Started from an empty list: the cells above leave a scripted transfer behind on purpose, and a count
        // assertion measured against it would be a number about the fixture rather than about the build.
        TaskQueue.Instance.ClearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var changeIdentity = "project:build:fixture:windows-x64";
        if (TaskQueue.Instance.EnqueueForCheck(changeIdentity, "Build project", "fixture · windows-x64",
                TaskQueue.TaskKind.Mutating) is null)
        {
            throw new InvalidOperationException("fixture: the build change was refused");
        }

        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var changeLabel = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubTexts.Get("TaskSlotFormat", HubStrings.Language), 1, 1);
        Check(shell.Workspace.IsBusy && shell.TaskSlotVisibleForCheck && shell.TaskSlotTextForCheck == changeLabel,
            "构建作为任务行点亮同一个槽位（实际 " + shell.TaskSlotTextForCheck + "）");

        // The gate is held; nothing is disabled. This is the cell that used to be a grey rectangle.
        Check(shell.PageEnabledForCheck && shell.SettingsHostEnabledForCheck,
            "变更操作在跑时页面与设置卡都没有被冻住");

        var dialogsBeforeChange = HubDialogWindows().Length;
        await shell.Workspace.UninstallEngineAsync();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.Workspace.IsBusy
              && shell.StatusTextForCheck == HubTexts.Get("DeepLinkBusy", HubStrings.Language)
              && HubDialogWindows().Length == dialogsBeforeChange,
            "被闸挡住的动手给的是状态条一句话，不弹窗（实际 " + shell.StatusTextForCheck + "）");

        // The row's measure when the work can prove only that it is still going. The clock is what a compile
        // can honestly report, where 「—」 would read as a task that stopped saying anything.
        TaskQueue.Instance.AgeForCheck(changeIdentity, TimeSpan.FromMinutes(2));
        shell.OpenTaskPopupForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var changeMeasure = shell.TaskRowMeasureForCheck(changeIdentity);
        Check(changeMeasure.Contains("00:02:", StringComparison.Ordinal)
              && changeMeasure.Contains(HubTexts.Get("BuildElapsed", HubStrings.Language), StringComparison.Ordinal),
            "没有百分比的那一行给的是耗时，不是破折号（实际 " + changeMeasure + "）");
        shell.CloseTaskPopupForCheck();
        TaskQueue.Instance.FinishTaskForCheck(changeIdentity, TaskQueue.Phase.Done);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The other half of the same ruling: second-scale work holds this very gate, and takes no row. Were it
        // to show one, the popover would fill with flashes and 「is Hub busy」 would have two answers again.
        // Started through the real lifecycle rather than scripted into the queue, because the strip's cancel is
        // wired to the busy signal that lifecycle raises — a hand-made item would leave that wiring outside the
        // picture while still passing every word of this assertion.
        var quiet = shell.Workspace.ExecuteAsync("Open project folder",
            token => Task.Delay(int.MaxValue, token));
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.Workspace.IsBusy && shell.CanCancelForCheck
              && TaskQueue.Instance.Items.Count == 0 && !shell.TaskSlotVisibleForCheck,
            "秒级操作占着同一个闸、也取消得掉，但不占行也不点亮槽位");
        shell.Workspace.Cancel();
        await quiet;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.Workspace.IsBusy && !shell.CanCancelForCheck,
            "取消那条秒级操作后闸空出来，状态条的取消也跟着熄灭");

        // --- 5.1f The assistant holds this very gate ---
        // The defect this closes is two owners each believing the other's busy signal was somebody else's business:
        // an assistant `cmake --build` never consulted the queue, so a build button pressed beside it started a
        // second compile against the same output directory. Read off the shell, three facts — the hold lights the
        // one busy truth without taking a row, a refused press names the assistant instead of an operation nobody
        // can find, and the strip's ✕ stays dark because it cannot reach that process.
        var assistantHold = shell.TakeAssistantGateForCheck("cmake --build build");
        var secondHold = shell.TakeAssistantGateForCheck("cmake --build build");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(assistantHold is not null && secondHold is null && shell.Workspace.IsBusy
              && TaskQueue.Instance.Items.Count == 0 && !shell.TaskSlotVisibleForCheck && !shell.CanCancelForCheck,
            "助手占闸：忙真相只有一个，不占行不点亮槽位，第二条只会被告知在忙而不会插队，状态条的取消够不到它");

        var dialogsBeforeAssistant = HubDialogWindows().Length;
        await shell.Workspace.UninstallEngineAsync();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.StatusTextForCheck == HubTexts.Get("DeepLinkBusyAssistant", HubStrings.Language)
              && shell.PageEnabledForCheck && HubDialogWindows().Length == dialogsBeforeAssistant,
            "助手占闸时被挡住的动手给的是点名助手的一句话，不弹窗也不置灰（实际 " + shell.StatusTextForCheck + "）");

        // The road out the tool body owes the queue: the command's own finally hands the gate back. Left held, Hub
        // would report itself busy for the rest of the session with nothing running — which is the same silence
        // this round replaced, only pointing the other way.
        assistantHold?.Dispose();
        await WaitUntilAsync(() => !shell.Workspace.IsBusy);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.Workspace.IsBusy && TaskQueue.Instance.ActiveCount == 0,
            "助手还闸之后忙真相立刻熄灭，不留一个没人跑的占位");

        // The row reads its own process and nothing else. This is the defect the progress window carried: it
        // was fed from the global log, so a line printed by an unrelated tool moved its stage and its
        // percentage. Positive first (its own line lands), then the negative that is the whole point —
        // the same words through the log must not be able to touch it.
        TaskQueue.Instance.ClearForCheck();
        var ownIdentity = "project:build:ownstream:windows-x64";
        if (TaskQueue.Instance.EnqueueForCheck(ownIdentity, "Build project", "fixture · windows-x64",
                TaskQueue.TaskKind.Mutating) is null)
        {
            throw new InvalidOperationException("fixture: the own-stream build was refused");
        }

        shell.OpenTaskPopupForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        TaskQueue.Instance.ReportLineForCheck(ownIdentity, "Command: cmake --build build --config Release");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var ownStage = shell.TaskRowTitleForCheck(ownIdentity);
        Check(ownStage.Contains(HubTexts.Get("BuildCompiling", HubStrings.Language), StringComparison.Ordinal),
            "构建的那一行说的是它自己的阶段（行上 " + ownStage + "）");

        TaskQueue.Instance.ReportLineForCheck(ownIdentity, "[7/42] Building CXX object CMakeFiles/app.dir/main.cpp.obj");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.TaskRowMeasureForCheck(ownIdentity).Contains("7 / 42", StringComparison.Ordinal),
            "编译器报了自己的步数时，测量列就是那两步数（实际 "
            + shell.TaskRowMeasureForCheck(ownIdentity) + "）");

        shell.WriteLog("Command: org.gradle.launcher.GradleMain assembleRelease");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.TaskRowTitleForCheck(ownIdentity) == ownStage
              && !shell.TaskRowTitleForCheck(ownIdentity)
                  .Contains(HubTexts.Get("BuildPackaging", HubStrings.Language), StringComparison.Ordinal),
            "别人的日志推动不了这一行（行上仍是 " + shell.TaskRowTitleForCheck(ownIdentity) + "）");

        // The row's own answer for where the detail is: a change offers the press into the drawer, and the
        // drawer says what it holds now that the window that used to hold it is gone.
        Check(shell.TaskRowActionsForCheck.Contains(HubTexts.Get("TaskLogAction", HubStrings.Language), StringComparison.Ordinal)
              && shell.LogTitleForCheck == HubTexts.Get("LogTitle", HubStrings.Language),
            "变更的那一行给看日志，抽屉带着它自己的标题（实际 " + shell.LogTitleForCheck + "）");
        shell.CloseTaskPopupForCheck();

        // --- 5.1e Two gates over one list, and the one cancel with two entrances ---
        // Started clean again: the own-stream row above is Done and a Done row would be counted by the label
        // assertions below, which are about what is moving, not about what the list still remembers.
        TaskQueue.Instance.ClearForCheck();
        var pipeLeft = TaskQueue.Instance.EnqueueForCheck("engine:install:pipe-left", "TaskEnginePackage", "pipe-left");
        var pipeRight = TaskQueue.Instance.EnqueueForCheck("engine:install:pipe-right", "TaskEnginePackage", "pipe-right");
        var alongside = TaskQueue.Instance.EnqueueForCheck("project:build:alongside:windows-x64", "Build project",
            "alongside · windows-x64", TaskQueue.TaskKind.Mutating);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(pipeLeft?.IsMoving == true && pipeRight?.IsMoving == true
              && alongside?.State == TaskQueue.Phase.Running,
            "传输与变更互不等待：两路下载在跑时变更直接开跑（实际 " + alongside?.State + "）");

        // The other side of that line: a transfer that does not fit waits, and a change that does not fit is
        // refused. Both readings are the ruling, so the second request is driven through the lifecycle — the
        // refusal happens by not enqueuing, which a scripted Enqueue would paper over.
        var dialogsBeforeSecond = HubDialogWindows().Length;
        var second = shell.Workspace.ExecuteAsync("Set default engine", token => Task.Delay(int.MaxValue, token));
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var mutatingRows = TaskQueue.Instance.Items.Count(item => item.Kind == TaskQueue.TaskKind.Mutating);
        Check(mutatingRows == 1 && shell.StatusTextForCheck == HubTexts.Get("DeepLinkBusy", HubStrings.Language)
              && HubDialogWindows().Length == dialogsBeforeSecond,
            "第二条变更被拒绝而不是排队，拒绝只花状态条一句话（面板里 " + mutatingRows + " 条变更）");
        await second;

        // The strip's cancel and the row's cancel cannot be allowed to disagree: one item, and whichever
        // entrance is used leaves the other with nothing left to do.
        shell.OpenTaskPopupForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.CanCancelForCheck
              && shell.TaskRowActionsForCheck.Contains(cancelWord, StringComparison.Ordinal),
            "行上的取消与状态条的取消同时亮着，说的是同一件事");
        shell.ClickTaskRowActionForCheck("project:build:alongside:windows-x64", cancelWord);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(alongside?.State == TaskQueue.Phase.Cancelled && !shell.CanCancelForCheck,
            "从行上取消之后，状态条的取消跟着熄灭（实际 " + alongside?.State + "）");

        // The layer rule, stated for what a change now is: with the settings card up the strip's cancel must
        // still answer, because cancelling a compile is the one action nobody else offers. It is driven from
        // the strip this time rather than the row, and the item is a fresh one — the popover's own surface is
        // not what this cell is about.
        var underOverlay = TaskQueue.Instance.EnqueueForCheck("project:build:overlay:windows-x64", "Build project",
            "overlay · windows-x64", TaskQueue.TaskKind.Mutating);
        // Re-opened through the helper rather than the raw toggle, and the fresh instance put back into
        // `settings`: opening the card builds a new page, so a local still holding the previous instance would
        // leave every cell after this one reading a detached surface — which is a false red, not a false green,
        // but still a red nobody caused.
        settings = OpenSettingsOn(shell, "appearance");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.CanCancelForCheck && shell.Workspace.IsBusy,
            "设置浮层开着时状态条的取消仍然按得下去");
        shell.ClickCancelButtonForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(underOverlay?.State == TaskQueue.Phase.Cancelled,
            "从状态条取消之后，浮层底下那条变更真的停了（实际 " + underOverlay?.State + "）");
        TaskQueue.Instance.ClearForCheck();

        // 「运行不是任务」read as two facts: after Hub records the game it launched, the gate is still empty
        // and the list has no row for it; and when that game is holding its own output directory, a rebuild
        // answers with a sentence the user can act on instead of the compiler's sharing-violation text.
        // A fixture project rather than one out of state: the suite has moved to a scratch data root by now and
        // has no registered project to hand, and the rule under test needs only the path a game was started
        // for — the refusal happens before the target picker, so no artifact is touched either way.
        var heldProject = new ProjectEntry { Name = "fixture-running", Path = "fixture://running" };
        shell.Workspace.SelectedProject = heldProject;
        shell.Workspace.RememberGameForCheck(heldProject.Path, Environment.ProcessId);
        Check(!shell.Workspace.IsBusy && TaskQueue.Instance.ActiveCount == 0,
            "游戏跑着的时候闸是空的，运行也不占任务列表");

        var blocked = shell.Workspace.BuildAsync(configureOnly: false);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var blocker = HubDialogWindows().LastOrDefault();
        var blockerText = blocker is null ? "" : NamedDescendant<TextBlock>(blocker, "DialogMessage")?.Text ?? "";
        Check(blockerText.Contains(HubTexts.Get("ProjectStillRunning", HubStrings.Language), StringComparison.Ordinal),
            "游戏占着输出目录时，构建给的是能行动的一句话（弹窗 " + blockerText + "）");
        if (blocker is not null)
        {
            ActionButtons(blocker).FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        await blocked;
        shell.Workspace.ForgetGamesForCheck();
        shell.Workspace.SelectedProject = null;
        // The failure opened the drawer, as it should — and the geometry cells downstream measure the shell
        // with it closed. Leave the surface as it was found rather than as the last press left it.
        shell.ShowLog(false);

        TaskQueue.Instance.ClearForCheck();
        UpdateService.Instance.ClearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // --- 5.2 Really switch the language once ---
        // The shell-side probe uses a nav item that is still a RadioButton (NavToolchains): it is built at
        // shell construction time, so its text changing proves DynamicResource re-resolves in place. Settings
        // itself is no longer a nav item (it is the gear), so it can't serve as this probe any more.
        var navBefore = NavLabel(shell.NavToolchains);
        settings.SelectCategoryForCheck("storage");
        var noteBefore = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(navBefore == HubTexts.Get("Toolchains", HubTexts.ChineseLanguage)
              && noteBefore == HubTexts.Get("DataHint", HubTexts.ChineseLanguage),
            "切换前外壳与设置页的文字都是中文（" + navBefore + " / " + noteBefore + "）");

        settings.SelectLanguage(HubTexts.EnglishLanguage);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(settings.SelectedLanguage == HubTexts.EnglishLanguage,
            "选语言真的改变了设置页的当前语言（" + settings.SelectedLanguage + "）");

        // This is the core of the group: the nav item is a control built at **shell construction** time, long existing by the language switch.
        // Its text changing proves DynamicResource **re-resolves in place**, not just once at construction.
        var navAfter = NavLabel(shell.NavToolchains);
        Check(navAfter == HubTexts.Get("Toolchains", HubTexts.EnglishLanguage) && navAfter != navBefore,
            "外壳上早于切换就存在的控件（导航项）跟着换了文字：" + navBefore + " → " + navAfter);

        var noteAfter = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(noteAfter == HubTexts.Get("DataHint", HubTexts.EnglishLanguage) && noteAfter != noteBefore,
            "设置页内早于切换就存在的控件跟着换了文字（动态资源就地重解析）");

        // Persistence: if the UI changed language but the setting wasn't saved, the next launch reverts and it looks like a lost setting.
        Check(new PreferencesStore(preferencesPath).Load().Language == HubTexts.EnglishLanguage,
            "语言切换已写入设置文件（" + preferencesPath + "）");

        // The card is a layer, not a page, and a language switch repaints it in place rather than routing
        // through navigation — so it is still up, on the same group, after the switch. If it came back on the
        // first row every time, this is where the reader would notice.
        Check(shell.SettingsOverlayVisibleForCheck && settings.SelectedCategory == "storage",
            "换语言不关浮层也不把你送回第一栏（当前栏 " + settings.SelectedCategory + "）");

        // The theme labels are copy written in code rather than DynamicResource, so they only follow
        // if the language switch really reloads the page — the failure mode is "the settings page is
        // half English, half Chinese".
        settings.SelectCategoryForCheck("appearance");
        Check(ThemeLabels(settings) == string.Join("/", new[] { "ThemeSystem", "ThemeLight", "ThemeDark" }
                  .Select(key => HubTexts.Get(key, HubTexts.EnglishLanguage))),
            "切到英文后主题三项的文案也是英文（实际 " + ThemeLabels(settings) + "）");

        // --- 5.3 Another migrated page follows along too (cross-page, not just the settings page) ---
        var engines = (InstallsPage)shell.NavigateTo("Installs");
        shell.UpdateLayout();
        engines.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var englishHeaders = EngineHeaderTexts(NamedDescendant<DataGrid>(engines, "EnginesGrid"));
        Check(englishHeaders == EngineHeaderExpectation(HubTexts.EnglishLanguage),
            "切到英文后引擎页表头是英文（实际 " + englishHeaders + "）");

        // --- 5.5 Switch back to Chinese: prove it's reversible, not one-shot ---
        Check(!shell.SettingsOverlayVisibleForCheck,
            "导航到别的页时设置浮层一起收掉：不会留一层遮罩盖住刚要看的页面");
        OpenSettingsOn(shell, "storage");
        settings.SelectLanguage(HubTexts.ChineseLanguage);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(NavLabel(shell.NavToolchains) == HubTexts.Get("Toolchains", HubTexts.ChineseLanguage)
              && NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text == HubTexts.Get("DataHint", HubTexts.ChineseLanguage),
            "切回中文后文字跟着回来（不是单向生效）");

        Check(new PreferencesStore(preferencesPath).Load().Language == HubTexts.ChineseLanguage,
            "切回中文也落了盘（自检收尾不留英文设置，否则后面的渲染断言会拍成英文）");

        var restoredEngines = (InstallsPage)shell.NavigateTo("Installs");
        restoredEngines.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var restoredHeaders = EngineHeaderTexts(NamedDescendant<DataGrid>(restoredEngines, "EnginesGrid"));
        Check(restoredHeaders == EngineHeaderExpectation(HubTexts.ChineseLanguage),
            "切回中文后引擎页表头是中文（实际 " + restoredHeaders + "）");

        // --- 5.6 Three-state theme picker ---
        // Same three silent failure modes as 5.2, for the theme: the dropdown's Tag values drifting
        // from HubTheme, "saved but never applied", and "applied but the tokens didn't follow".
        settings.SelectCategoryForCheck("appearance");
        Check(settings.DeclaredThemes.SequenceEqual(HubTheme.All),
            "主题下拉项通过 Tag 声明的取值与 HubTheme 一致（实际 " + string.Join(", ", settings.DeclaredThemes) + "）");

        Check(settings.SelectedTheme == new PreferencesStore(preferencesPath).Load().Theme,
            "设置页打开时选中的就是设置文件里的主题（" + settings.SelectedTheme + "）");

        // The theme labels are written in code (a ComboBoxItem inside Items can't resolve
        // DynamicResource), so they probe the **other** half of the localization path: copy that only
        // changes through a reload. Reading them back here proves the language switch reached them.
        Check(ThemeLabels(settings) == string.Join("/", new[] { "ThemeSystem", "ThemeLight", "ThemeDark" }
                  .Select(key => HubTexts.Get(key, HubStrings.Language))),
            "切回中文后主题三项的文案也是中文（实际 " + ThemeLabels(settings) + "）");

        var darkToken = TokenColor("Hub.Background");
        shell.SetPreviewBadgeVisibleForCheck(true);
        var darkPreviewBadge = TokenColor("Hub.PreviewBadgeBackground");
        settings.SelectTheme(HubTheme.Light);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(Application.Current!.ActualThemeVariant == ThemeVariant.Light,
            "选浅色后实际主题变体跟着变（实际 " + Application.Current!.ActualThemeVariant + "）");

        // The variant name changing is not enough: what the user sees is the token **values**.
        var lightToken = TokenColor("Hub.Background");
        var lightPreviewBadge = TokenColor("Hub.PreviewBadgeBackground");
        Check(lightToken is not null && lightToken != darkToken,
            "切浅色后 Hub.Background 令牌换成另一组值（" + darkToken + " → " + lightToken + "）");
        Check(darkPreviewBadge is not null && lightPreviewBadge is not null && lightPreviewBadge != darkPreviewBadge
              && shell.PreviewBadgeUsesThemeTokensForCheck,
            "切浅色后 Preview 标记背景令牌也随主题变化（" + darkPreviewBadge + " → " + lightPreviewBadge + "）");

        Check(new PreferencesStore(preferencesPath).Load().Theme == HubTheme.Light,
            "主题切换已写入设置文件（" + preferencesPath + "）");

        // Switch back to dark: reversible, and it leaves the run dark so the render captures below
        // aren't shot in a light theme.
        settings.SelectTheme(HubTheme.Dark);
        Dispatcher.UIThread.RunJobs();
        Check(Application.Current!.ActualThemeVariant == ThemeVariant.Dark
              && new PreferencesStore(preferencesPath).Load().Theme == HubTheme.Dark,
            "切回深色也落了盘（自检收尾不留浅色设置，否则后面的渲染断言会拍成浅色）");
        shell.SetPreviewBadgeVisibleForCheck(HubReleaseInfo.IsPrereleaseBuild);

        await Task.CompletedTask;
    }

    internal void CheckSettingsOverlay(MainWindow shell)
    {
        var settings = OpenSettingsOn(shell, "appearance");
        var overlay = shell.SettingsOverlayBoundsForCheck;
        var card = shell.SettingsCardBoundsForCheck;
        var scrim = shell.SettingsScrimBoundsForCheck;
        var client = shell.ClientBoundsForCheck;
        var titleBar = shell.TitleBarBoundsForCheck;
        var strip = shell.StatusStripBoundsForCheck;

        // Stacking is document order — nothing in this app sets ZIndex — and a layer in the wrong slot covers
        // the shell just as well, so no geometry check below can see it. Same argument the expanded inspector
        // makes about itself.
        Check(shell.SettingsOverlayOrderForCheck,
            "设置浮层是壳、审阅撑开层、图片预览之上的最外层（层序写死在文档顺序里）");

        // The two facts that make the window still draggable and the strip still pressable, and the pair is the
        // point: a layer that merely *looks* clear of the title bar but still hit-tests across it would freeze
        // the window, and a layer that stops short of the strip but painted over it would hide the queue slot.
        Check(!overlay.Intersects(titleBar) && overlay.Bottom <= strip.Top + 0.5,
            "浮层不碰标题栏、也不越过状态条上沿：设置开着时窗口照样能拖、任务槽照样能点（浮层 "
            + Fmt(overlay.Bottom) + "，状态条上沿 " + Fmt(strip.Top) + "）");
        Check(overlay.Intersects(strip) == false && strip.Top - overlay.Bottom <= 28.5,
            "遮罩停在状态条之上而不是靠透明度糊过去，留出的那条正好是状态条自己的高度（差 "
            + Fmt(strip.Top - overlay.Bottom) + "）");
        Check(scrim.Width >= overlay.Width - 0.5 && scrim.Height >= overlay.Height - 0.5
              && shell.SettingsScrimBlocksForCheck,
            "遮罩铺满浮层并且真的画了东西：一块没有笔刷的 Border 不接受命中，壳会悄悄变得可点");
        Check(card.Width > 0 && overlay.Contains(card) && client.Contains(overlay),
            "卡片在浮层之内、浮层在客户区之内（卡片 " + Fmt(card.Width) + "x" + Fmt(card.Height) + "）");
        // The window is 1280x880 when the self-check builds it, so a card that merely sat at its own floor would
        // be 720x420 here. Measured against the parent is the whole point of the sizing rule.
        Check(card.Width >= 1100 && card.Height >= 700
              && card.Width <= overlay.Width - 40 && card.Height <= overlay.Height - 20,
            "卡片尺寸跟着父窗走：既不是塌回下限的小卡，也没有把遮罩铺满到不留边（"
            + Fmt(card.Width) + "x" + Fmt(card.Height) + "，浮层 "
            + Fmt(overlay.Width) + "x" + Fmt(overlay.Height) + "）");

        // One group on screen. The count, not "the right one is visible": a second pane left showing is the
        // bug, and it is the one an object-graph read of the intended pane cannot see.
        var panes = settings.GetLogicalDescendants().OfType<Panel>()
            .Where(node => node.Name is not null && node.Name.StartsWith("Pane", StringComparison.Ordinal))
            .ToList();
        Check(panes.Count == SettingsPage.CategoryKeys.Length
              && panes.Count(node => node.IsVisible) == 1
              && panes.Single(node => node.IsVisible).Name == "PaneAppearance",
            "七类容器都在，恰好一类可见且就是选中的那一类（可见 "
            + panes.Count(node => node.IsVisible) + " 个）");

        // A long group scrolled to the bottom, then a switch: the next group must open at its own top, not
        // somewhere in the middle of a list the reader has already left. The length is grown from the trust
        // entries rather than hoped for from the fixture's providers, so the overflow is the thing under test
        // and not a coincidence of how many models happen to be configured on this machine.
        OpenSettingsOn(shell, "permission");
        var scroll = NamedDescendant<ScrollViewer>(settings, "DetailScroll")
                     ?? throw new InvalidOperationException("自检夹具：详情区没有命名，滚动断言无从读起");
        for (var index = 1; index <= 30; index++)
        {
            settings.TrustToolForCheck($"fixture_tool_{index}");
        }
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(scroll.Extent.Height > scroll.Viewport.Height + 40,
            "信任清单长到详情区装不下，滚动这才真的在测（内容 " + Fmt(scroll.Extent.Height)
            + "，可视 " + Fmt(scroll.Viewport.Height) + "）");
        scroll.Offset = new Vector(0, 300);
        settings.SelectCategoryForCheck("editors");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(scroll.Offset.Y == 0,
            "换栏时详情滚动回到顶部，不把上一栏的位置带过来（实际偏移 " + Fmt(scroll.Offset.Y) + "）");
        Check(settings.RevokeAllTrustedToolsForCheck() && settings.TrustedToolsForCheck.Length == 0,
            "夹具留下的信任项收干净了：这一格不该把 30 个假工具留在用户的设置里");

        // Deep link and the answer to "where was I": a caller that knows which group answers its question gets
        // that group, and a caller that does not gets the one the reader left.
        OpenSettingsOn(shell, "updates");
        Check(settings.SelectedCategory == "updates" && shell.SettingsCategoryForCheck == "updates",
            "按类别深链落到那一栏，不是开完再跳回第一栏");
        shell.CloseSettings();
        OpenSettingsOn(shell, null);
        Check(settings.SelectedCategory == "updates",
            "再开时回到上次那一栏：浮层关掉不等于把人读到哪里忘了");

        // A dropdown is its own top-level, so Escape belongs to it first. Without this guard the card vanishes
        // from under a choice the reader was mid-way through making.
        shell.OpenSettingsForCheck("appearance");
        var picker = NamedDescendant<ComboBox>(settings, "LanguagePicker")
                     ?? throw new InvalidOperationException("自检夹具：外观栏里没有语言下拉");
        picker.IsDropDownOpen = true;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(picker.IsDropDownOpen && settings.HasOpenDropDown, "下拉展开时页面报得出「有下拉开着」");
        shell.PressSettingsKeyForCheck(Key.Escape);
        Check(picker.IsDropDownOpen && shell.SettingsOverlayVisibleForCheck,
            "下拉开着时 Escape 不收浮层：那一键是列表的，不是卡片的");
        picker.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();
        shell.PressSettingsKeyForCheck(Key.Escape);
        Check(!shell.SettingsOverlayVisibleForCheck, "没有下拉开着时 Escape 把浮层收掉");

        // The two other ways out, each through its own real input rather than the method behind it.
        OpenSettingsOn(shell, "appearance");
        shell.ClickSettingsCloseForCheck();
        Check(!shell.SettingsOverlayVisibleForCheck, "点卡片右上角的 × 关掉浮层（接线有效，不是只画了一枚按钮）");

        OpenSettingsOn(shell, "appearance");
        PressScrim(shell.SettingsScrimForCheck, shell);
        Check(!shell.SettingsOverlayVisibleForCheck,
            "点遮罩也关掉：卡里没有任何未提交状态，外面那一下不该没有反应");

        // The gear toggles: pressed twice it must undo itself, since it is the one entry always on screen.
        shell.ClickSettingsGearForCheck();
        Check(shell.SettingsOverlayVisibleForCheck, "齿轮第一下升起浮层");
        shell.ClickSettingsGearForCheck();
        Check(!shell.SettingsOverlayVisibleForCheck,
            "齿轮是开关不是只开：第二下把卡收掉（自检直接触发按钮事件，人在卡开着时点到的是遮罩而不是它）");

        // The charter's other half: a question does not get to be covered by an answer. The card holds the
        // whole chat column under its scrim, so an approval that lands while it is up would be a question
        // nobody can see, let alone answer.
        shell.OpenSettingsForCheck("appearance");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        shell.RaiseAttentionForCheck(ChatAttentionKind.ToolApproval);
        Check(!shell.SettingsOverlayVisibleForCheck,
            "会话需要决定时浮层自己让开：审批卡不能长在遮罩底下等人去发现");

        // Clipping: the detail column is the tall thing in a bounded card, and an overflow that paints past
        // the rounded corner is a bug here, not a style.
        shell.OpenSettingsForCheck("providers");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var detail = NamedDescendant<ScrollViewer>(settings, "DetailScroll");
        Check(detail is not null && detail.Bounds.Height <= shell.SettingsCardBoundsForCheck.Height + 0.5,
            "详情区高不过卡片：滚动条长在卡里，不是把卡撑破（详情 "
            + Fmt(detail?.Bounds.Height ?? 0) + "，卡片 " + Fmt(shell.SettingsCardBoundsForCheck.Height) + "）");

        // The × has to sit in the middle of the plate it is drawn on. The plate only appears under a pointer, so
        // an off-centre glyph is invisible to every screenshot and to the eye until somebody happens to hover —
        shell.CloseSettings();
    }

    internal async Task CheckDrawnDialogsAsync(MainWindow shell)
    {
        // ① The confirm box, on the path with no owner at all (the startup-failure route).
        var dialogsBefore = HubDialogWindows().Length;
        var pending = HubDialog.ShowAsync(null, "自绘头确认框", "这一张用右上角的盘关掉。", HubDialogButtons.OkCancel);
        var box = HubDialogWindows().LastOrDefault();
        Dispatcher.UIThread.RunJobs();
        Check(box is not null && HubDialogWindows().Length == dialogsBefore + 1, "没有 owner 的确认框也能起来，而且是带着自绘头起来的（弹窗数 " + dialogsBefore + " → " + HubDialogWindows().Length + "）");
        if (box is not null)
        {
            CheckOneDialogChrome(box, "确认框");
        }

        // What the one plate means, on the box that has to answer a question: closing it is Cancel, the same
        // answer Esc gives through the IsCancel button. This is the invariant the result enum is *ordered* for,
        // so it is asserted as behaviour and not left to default(T).
        var boxPlate = box is null ? null : NamedDescendant<Button>(box, "DialogCloseButton");
        if (boxPlate is null)
        {
            // The plate could not be found, so nothing will close this window and the await below would sit in
            // front of it until the suite's ceiling. Close it the blunt way and let the assertion say why.
            box?.Close();
            Check(false, "确认框的自绘头里没找到关闭盘，无法验证它的出口");
        }
        else
        {
            boxPlate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        var boxResult = await pending;
        Dispatcher.UIThread.RunJobs();
        Check(boxPlate is not null && boxResult == HubDialogResult.Cancel && HubDialogWindows().Length == dialogsBefore, "确认框的 × 就是它的取消出口：拿到 " + boxResult + "，窗口也真的关了（弹窗数回到 " + HubDialogWindows().Length + "）");
        // ② The picker, built the way --shot-provider-picker builds it. Its × must not be read as a choice.
        var picker = ProviderPickerWindow.ForCheck(shell.Chat.AvailablePresets());
        picker.Show();
        picker.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(picker.IsVisible, "添加提供商选择器能作为独立弹窗打开");
        NamedDescendant<Button>(picker, "DialogCloseButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(!picker.IsVisible && picker.SelectedId is null, "选择器的 × 与「取消」同义：关掉窗口，且没有把任何预设当成选中（SelectedId " + (picker.SelectedId ?? "null") + "）");
        // ③ The model catalog, taken through the settings card's own seam so it is the real thing.
        var settings = OpenSettingsOn(shell, "providers");
        var catalogId = shell.Chat.Providers.FirstOrDefault()?.Id ?? "";
        var catalog = settings.ModelCatalogForCheck(catalogId);
        Check(catalog is not null, "夹具里能构建出模型目录弹窗（provider " + catalogId + "）");
        if (catalog is not null)
        {
            catalog.Show();
            catalog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(catalog.IsVisible, "模型目录能作为独立弹窗打开");
            NamedDescendant<Button>(catalog, "DialogCloseButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(!catalog.IsVisible, "模型目录的 × 就是它自己那颗「关闭模型目录」的出口：窗口关掉，不留下一个开着的模态");
        }

        shell.CloseSettings();
        await new ShellDialogCancellationChecks(Check).RunAsync(shell);
    }

    internal void CheckOneDialogChrome(Window window, string label)
    {
        Check(window.ExtendClientAreaToDecorationsHint
              && window.WindowDecorations == WindowDecorations.BorderOnly
              && Math.Abs(window.ExtendClientAreaTitleBarHeightHint - HubDialogChrome.Height) <= 0.5,
            "「" + label + "」不再穿平台的标题栏：客户区扩展、装饰取 BorderOnly、高度提示跟着头的 32（实际 "
            + window.ExtendClientAreaToDecorationsHint + "、" + window.WindowDecorations + "、"
            + Fmt(window.ExtendClientAreaTitleBarHeightHint) + "）");

        var header = NamedDescendant<Border>(window, "DialogChrome");
        var drag = NamedDescendant<Border>(window, "DialogDragArea");
        var caption = NamedDescendant<TextBlock>(window, "DialogTitle");
        var plate = NamedDescendant<Button>(window, "DialogCloseButton");
        Check(header is not null && drag is not null && caption is not null && plate is not null,
            "「" + label + "」的自绘头四件都在：头、题字、拖动层、一枚关闭盘");
        if (header is null || drag is null || caption is null || plate is null)
        {
            return;
        }

        var client = window.Bounds;
        var headerRect = InWindow(window, header);
        Check(Math.Abs(headerRect.Y) <= 0.5 && Math.Abs(headerRect.Height - HubDialogChrome.Height) <= 0.5
              && Math.Abs(headerRect.X) <= 0.5 && Math.Abs(headerRect.Width - client.Width) <= 0.5,
            "「" + label + "」的头占住客户区最顶端、贴住窗口两缘且高 32（实际 " + Fmt(headerRect.Width) + "×"
            + Fmt(headerRect.Height) + " @ " + Fmt(headerRect.X) + "," + Fmt(headerRect.Y) + "，窗宽 "
            + Fmt(client.Width) + "）");

        // One plate, and it is the close one. The 46 is read against the pixels rather than trusted from the
        // style sheet because the styles moved to the app's dictionary this round — if the cascade stopped
        // reaching a dialog window, the plate would collapse to its content and only this would notice.
        var plateRect = InWindow(window, plate);
        var plates = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("window-control")).ToList();
        Check(plates.Count == 1 && ReferenceEquals(plates[0], plate) && plate.Classes.Contains("close")
              && Math.Abs(plateRect.Width - 46) <= 0.5 && Math.Abs(plateRect.Height - HubDialogChrome.Height) <= 0.5
              && Math.Abs(plateRect.Y) <= 0.5 && plateRect.Right >= client.Width - 0.5,
            "「" + label + "」的头上只有一枚 46×32 的关闭盘，落在右上角并铺到窗口右缘，没有最小化与最大化（实际 "
            + plates.Count + " 枚，" + Fmt(plateRect.Width) + "×" + Fmt(plateRect.Height) + "，右缘 "
            + Fmt(plateRect.Right) + " / 窗宽 " + Fmt(client.Width) + "）");

        // The bar is one press surface: the drag layer has to carry a brush — Avalonia hit-tests drawn geometry,
        // so a null background would measure, paint nothing, and let every press fall through to whatever is
        // behind it — and the caption must not answer hit tests, or it punches a dead hole in the middle of the
        // only region that moves the window.
        Check(drag.Background is not null && !caption.IsHitTestVisible,
            "「" + label + "」的头是一整块可压的拖动面，题字不吃命中（拖动层有笔刷 "
            + (drag.Background is not null) + "，题字可命中 " + caption.IsHitTestVisible + "）");

        // The title is said once. The caption is bound to Window.Title, so one write feeds the drawn bar and the
        // OS's window list; a body that still carried its own copy would be caught by the echo count.
        var echoes = window.GetVisualDescendants().OfType<TextBlock>()
            .Count(text => !ReferenceEquals(text, caption) && text.IsVisible && text.IsEffectivelyVisible
                           && text.Text == window.Title);
        Check(caption.Text == window.Title && echoes == 0,
            "「" + label + "」的标题只说一次：写在头上，正文里没有第二处（题字「" + caption.Text
            + "」，正文重复 " + echoes + " 处）");

    }
}
