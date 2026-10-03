using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using static AxmolHub.App.ThemeProbe;

namespace AxmolHub.App;

/// <summary>
/// P4 foundation runtime self-check. What it asserts now: the MessageBox-replacing HubDialog, the
/// async picker wrappers, and <c>--smoke</c>'s "non-blank" criterion.
///
/// Why runtime assertions are necessary: a wrong Avalonia style or template **doesn't error**, it
/// just silently degrades (see Services/ThemeProbe.cs); and the "screenshot is blank" failure mode
/// is even more subtle — the process still exits 0 and the PNG still exists.
/// </summary>
public partial class FoundationCheckWindow : Window
{
    public FoundationCheckWindow() => InitializeComponent();

    public void Run(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath)
    {
        var finished = false;

        void Finish(List<string> lines, int passed, int failed)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            lines.Add(string.Format(CultureInfo.InvariantCulture, "RESULT: {0}/{1} passed", passed, passed + failed));

            try
            {
                System.IO.File.WriteAllLines(reportPath, lines);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }

            lifetime.Shutdown(failed == 0 ? 0 : 1);
        }

        Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            var lines = new List<string>();
            var passed = 0;
            var failed = 0;

            void Check(bool ok, string message)
            {
                if (ok)
                {
                    passed++;
                    lines.Add("PASS  " + message);
                }
                else
                {
                    failed++;
                    lines.Add("FAIL  " + message);
                }
            }

            try
            {
                UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await RunChecksAsync(lifetime, Check);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL  验证过程抛出异常: " + ex.GetType().Name + ": " + ex.Message);
            }

            Finish(lines, passed, failed);
        }, DispatcherPriority.Background);

        // Fallback: don't let the process hang forever if the window never shows (in automation, hanging is harder to diagnose than failing).
        DispatcherTimer.RunOnce(() =>
        {
            if (!finished)
            {
                Finish(new List<string> { "FAIL  窗口在 10 秒内没有触发 Opened，断言未执行" }, 0, 1);
            }
        }, TimeSpan.FromSeconds(10));
    }

    private async Task RunChecksAsync(IClassicDesktopStyleApplicationLifetime lifetime, Action<bool, string> check)
    {
        CheckPickerDecision(check);
        await CheckRealStorageProviderAsync(check);
        CheckBlankDetector(check);
        await CheckDialogAsync(lifetime, check);
        CheckBlankFrameIsRejected(check);
        CheckRealCapture(check);
    }

    /// <summary>
    /// Negative control: faces the "non-blank" criterion with a **real solid-color window capture**.
    ///
    /// The synthetic buffer above verified the criterion's arithmetic; this verifies the whole
    /// pipeline — the failure mode we really guard against is "captured too early / rendering not
    /// up → solid image → still exits 0". Proving the criterion lets good images through isn't
    /// enough; it must also **actually reject bad ones**, otherwise the defense is just decoration.
    /// </summary>
    private static void CheckBlankFrameIsRejected(Action<bool, string> check)
    {
        var directory = ScratchDirectory.Resolve("foundation-check");

        var blank = new Window
        {
            Width = 200,
            Height = 120,
            // In 12.1.3 SystemDecorations is obsolete, renamed WindowDecorations (same enum name).
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Black,
        };

        blank.Show();
        try
        {
            blank.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var stats = SmokeCapture.Capture(blank, System.IO.Path.Combine(directory, "blank-control.png"));
            check(stats.IsBlank() && stats.DistinctColors == 1,
                "负向对照：纯色窗口截图被判为空白（distinct=" + stats.DistinctColors
                + "，variance=" + stats.LuminanceVariance.ToString("F4", CultureInfo.InvariantCulture) + "）");
        }
        finally
        {
            blank.Close();
        }
    }

    /// <summary>
    /// The picker-result decision logic. "Picked a cloud/virtual location" must be distinguished
    /// from "user cancelled": the former treats null as a path and silently creates a relative-path
    /// project, while the latter is a normal give-up.
    /// </summary>
    private static void CheckPickerDecision(Action<bool, string> check)
    {
        var cancelled = Pickers.Resolve(itemPresent: false, localPath: null);
        check(cancelled.Outcome == PickOutcome.Cancelled && cancelled.Path is null,
            "没有任何项 → Cancelled");

        var notLocal = Pickers.Resolve(itemPresent: true, localPath: null);
        check(notLocal.Outcome == PickOutcome.NotLocal,
            "选了项但没有本地路径 → NotLocal（不当成取消）");

        var empty = Pickers.Resolve(itemPresent: true, localPath: string.Empty);
        check(empty.Outcome == PickOutcome.NotLocal, "空字符串路径 → NotLocal");

        var picked = Pickers.Resolve(itemPresent: true, localPath: @"C:\src\HelloAxmol");
        check(picked.Outcome == PickOutcome.Picked && picked.Path == @"C:\src\HelloAxmol",
            "有本地路径 → Picked 且原样带回（" + (picked.Path ?? "null") + "）");
    }

    /// <summary>
    /// Runs one round-trip with a real <see cref="Avalonia.Platform.Storage.IStorageProvider"/> to
    /// prove the <c>TryGetLocalPath()</c> extension really hooks up — the group above only verified
    /// the decision logic, and can't verify "does the platform implementation return a local path
    /// as promised".
    /// </summary>
    private async Task CheckRealStorageProviderAsync(Action<bool, string> check)
    {
        var probe = ScratchDirectory.FilePath("foundation-check", "picker-probe.txt");
        await System.IO.File.WriteAllTextAsync(probe, "probe");

        try
        {
            var item = await StorageProvider.TryGetFileFromPathAsync(new Uri(System.IO.Path.GetFullPath(probe)));
            var result = Pickers.Translate(item);
            check(result.Outcome == PickOutcome.Picked
                  && string.Equals(result.Path, System.IO.Path.GetFullPath(probe), StringComparison.OrdinalIgnoreCase),
                "真实 StorageProvider 往返：本地文件 → Picked（实际 "
                + result.Outcome + " · " + (result.Path ?? "null") + "）");
        }
        finally
        {
            try
            {
                System.IO.File.Delete(probe);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }
    }

    /// <summary>
    /// The "non-blank" criterion itself. A synthetic pixel buffer suffices: a solid color must be
    /// judged blank, a striped one must not. Otherwise the criterion isn't protecting evidence —
    /// it's manufacturing fake greens.
    /// </summary>
    private static void CheckBlankDetector(Action<bool, string> check)
    {
        const int side = 10;
        var stride = side * 4;

        var uniform = new byte[side * stride];
        Array.Fill(uniform, (byte)0xFF);
        var uniformStats = SmokeCapture.Analyze(uniform, side, side, stride);
        check(uniformStats.DistinctColors == 1 && Math.Abs(uniformStats.LuminanceVariance) < 0.0001
              && uniformStats.IsBlank(),
            "纯色缓冲被判为空白（distinct=" + uniformStats.DistinctColors
            + "，variance=" + uniformStats.LuminanceVariance.ToString("F4", CultureInfo.InvariantCulture) + "）");

        var striped = new byte[side * stride];
        for (var y = 0; y < side; y++)
        {
            var value = y % 2 == 0 ? (byte)0x00 : (byte)0xFF;
            for (var x = 0; x < side; x++)
            {
                var offset = (y * stride) + (x * 4);
                striped[offset] = value;
                striped[offset + 1] = value;
                striped[offset + 2] = value;
                striped[offset + 3] = 0xFF;
            }
        }

        var stripedStats = SmokeCapture.Analyze(striped, side, side, stride);
        check(!stripedStats.IsBlank() && stripedStats.DistinctColors == 2,
            "黑白条纹缓冲不被判为空白（distinct=" + stripedStats.DistinctColors
            + "，variance=" + stripedStats.LuminanceVariance.ToString("F2", CultureInfo.InvariantCulture) + "）");

        check(new FrameStats(0, 0, 0, 0).IsBlank(), "零尺寸帧被判为空白（渲染没起来时不能放行）");
    }

    private static async Task CheckDialogAsync(IClassicDesktopStyleApplicationLifetime lifetime, Action<bool, string> check)
    {
        // ---- OkCancel + danger: the tier of the WPF "uninstall" confirmation box ----
        var pending = HubDialog.ShowAsync(null, "卸载", "将删除安装目录与快捷方式。", HubDialogButtons.OkCancel, danger: true);
        var dialog = lifetime.Windows.OfType<HubDialog>().LastOrDefault();
        check(dialog is not null, "owner 为 null 时对话框也能显示（启动失败路径，WPF 的 MessageBox 同样支持）");

        if (dialog is null)
        {
            return;
        }

        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        check(dialog.Result == HubDialogResult.Cancel,
            "未点击时默认结果是 Cancel（点标题栏 X 会被读成 default(T)，所以 Cancel 必须是 0）");

        var card = dialog.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
        check(card is not null && IsToken(card.Background, "Hub.Surface"),
            "对话框主体复用了 Border.card（背景 = Hub.Surface，实际 " + Describe(card?.Background) + "）");

        check(NamedDescendant<TextBlock>(dialog, "DialogTitle")?.Text == "卸载",
            "标题写入 DialogTitle");
        check(NamedDescendant<TextBlock>(dialog, "DialogMessage")?.Text == "将删除安装目录与快捷方式。",
            "正文写入 DialogMessage");

        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        check(buttons.Count == 2, "OkCancel 生成 2 个按钮（实际 " + buttons.Count + "）");

        // The expected copy is **taken from the text table**, not written as literals. Previously this
        // hard-coded "取消/确定", tying the window's self-check to "the user happens to have the UI
        // in Chinese": `--verify-foundation` doesn't force a language, so the moment the user
        // switches to English, this assertion fails because of **correct** behavior.
        // Note the button order (cancel first, ok second) is still asserted — it's determined by the
        // HubDialogResult value order, independent of language, so it can't be dropped just because
        // the wording changed.
        var expectedLabels = string.Join("/", new[] { "Cancel", "Ok" }.Select(key => HubTexts.Get(key, HubStrings.Language)));
        var labels = string.Join("/", buttons.Select(b => b.Content?.ToString()));
        check(labels == expectedLabels, "按钮顺序为 取消、确定，文案来自 HubTexts（实际 " + labels + "）");

        var okCaption = HubTexts.Get("Ok", HubStrings.Language);
        var cancelCaption = HubTexts.Get("Cancel", HubStrings.Language);
        var primary = buttons.FirstOrDefault(b => b.Classes.Contains("primary"));
        var cancel = buttons.FirstOrDefault(b => b.IsCancel);
        check(primary?.Content?.ToString() == okCaption && primary.IsDefault,
            "确定键带 primary 类且 IsDefault（回车生效）");
        check(primary is not null && primary.Classes.Contains("danger"),
            "danger: true 时确定键带 danger 类（WPF 版没有这一档，MessageBoxImage 只换图标）");
        check(cancel?.Content?.ToString() == cancelCaption, "取消键带 IsCancel（Esc 生效）");

        // This guards against "writing it back as one language again": as long as the Chinese and English sides differ, the copy really goes through the text table.
        check(HubTexts.Get("Ok", HubTexts.EnglishLanguage) != HubTexts.Get("Ok", HubTexts.ChineseLanguage)
              && HubTexts.Get("Cancel", HubTexts.EnglishLanguage) != HubTexts.Get("Cancel", HubTexts.ChineseLanguage),
            "对话框按钮文案是本地化的，而不是写死一种语言"
            + "（写死中文的话，英文界面上点开任何弹窗按钮都是中文）");

        // Do a real click to verify the wiring from button to return value.
        primary?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var result = await pending;
        check(result == HubDialogResult.Ok, "点「确定」→ ShowAsync 返回 Ok（实际 " + result + "）");

        // ---- YesNo: choose No ----
        var yesNoPending = HubDialog.ShowAsync(null, "确认", "继续吗？", HubDialogButtons.YesNo);
        var yesNo = lifetime.Windows.OfType<HubDialog>().LastOrDefault();
        if (yesNo is not null)
        {
            yesNo.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var yesNoButtons = yesNo.GetVisualDescendants().OfType<Button>().ToList();
            var yesNoLabels = string.Join("/", yesNoButtons.Select(b => b.Content?.ToString()));
            check(yesNoLabels == string.Join("/", new[] { "No", "Yes" }.Select(key => HubTexts.Get(key, HubStrings.Language))),
                "YesNo 生成 否、是，文案来自 HubTexts（实际 " + yesNoLabels + "）");

            var yesCaption = HubTexts.Get("Yes", HubStrings.Language);
            var negative = yesNoButtons.FirstOrDefault(b => b.Classes.Contains("primary"));
            check(negative?.Content?.ToString() == yesCaption && negative.IsDefault, "肯定键带 primary 类");

            yesNoButtons.FirstOrDefault(b => b.Content?.ToString() == HubTexts.Get("No", HubStrings.Language))?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var yesNoResult = await yesNoPending;
            check(yesNoResult == HubDialogResult.No, "点「否」→ 返回 No（实际 " + yesNoResult + "）");
        }
        else
        {
            check(false, "YesNo 对话框未能显示");
        }
    }

    /// <summary>Really capture this window, facing the "non-blank" criterion with one real render.</summary>
    private void CheckRealCapture(Action<bool, string> check)
    {
        var directory = ScratchDirectory.Resolve("foundation-check");
        var path = System.IO.Path.Combine(directory, "foundation-check.png");

        var stats = SmokeCapture.Capture(this, path);
        check(System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 0,
            "真实渲染产出非空 PNG（" + (System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length + " 字节" : "文件不存在") + "）");
        check(!stats.IsBlank(),
            "真实窗口截图不是空白（" + stats.Width + "x" + stats.Height
            + "，distinct=" + stats.DistinctColors
            + "，variance=" + stats.LuminanceVariance.ToString("F2", CultureInfo.InvariantCulture) + "）");
    }
}
