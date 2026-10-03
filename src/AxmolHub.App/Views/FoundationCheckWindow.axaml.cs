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
/// P4 基础件的运行期自检。现在断言的是：替换 MessageBox 的 HubDialog、异步化的选择器包装、
/// 以及 <c>--smoke</c> 的"非空白"判据。
///
/// 为什么非要运行期断言：Avalonia 的样式与模板写错**不会报错**，只会静默退化
/// （见 Services/ThemeProbe.cs 的说明）；而"截图是空白的"这个失效模式更隐蔽 ——
/// 进程照样退出 0、PNG 照样存在。
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

        // 兜底：窗口没能显示时不要让进程一直挂着（自动化里挂住比失败更难查）。
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
    /// 负向对照：让"非空白"判据面对一张**真的纯色窗口截图**。
    ///
    /// 上面用合成缓冲验的是判据的算术，这里验的是整条管线 —— 真正要防的失效模式是
    /// "截早了/渲染没起来 → 纯色图 → 仍然退出 0"。只证明判据能放行好图是不够的，
    /// 还得证明它**真的会拦下坏图**，否则这条防线只是装饰。
    /// </summary>
    private static void CheckBlankFrameIsRejected(Action<bool, string> check)
    {
        var directory = ScratchDirectory.Resolve("foundation-check");

        var blank = new Window
        {
            Width = 200,
            Height = 120,
            // 12.1.3 里 SystemDecorations 已过时，改名成 WindowDecorations（枚举同名）。
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
    /// 选择器结果的判定逻辑。"选中的是云端/虚拟位置"必须与"用户取消"区分开：
    /// 前者把 null 当路径用会静默地创建一个相对路径工程，后者才是正常的放弃。
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
    /// 用真实 <see cref="Avalonia.Platform.Storage.IStorageProvider"/> 跑一次往返，
    /// 证明 <c>TryGetLocalPath()</c> 这条扩展真的接得上 —— 上面那组只验了判定逻辑，
    /// 验不到"平台实现是否按约定返回本地路径"。
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
    /// "非空白"判据本身。合成的像素缓冲足够说明问题：纯色必须被判为空白，
    /// 有条纹的必须不被判为空白。否则这条判据不是在保护证据，而是在制造假绿。
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
        // ---- OkCancel + danger：WPF 版"卸载"确认框的那一档 ----
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

        // 期望文案**从文案表取**，不写字面量。以前这里写死"取消/确定"，等于把这个窗口的自检
        // 绑在"用户恰好把界面语言设成中文"上：`--verify-foundation` 不强制语言，
        // 用户一切到英文，这条断言就会因为**正确**的行为而失败。
        // 注意按钮顺序（取消在前、确定在后）仍被断言 —— 它是由 HubDialogResult 的取值顺序决定的，
        // 与语言无关，不能因为换了种写法就漏掉。
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

        // 这条防的是"又把它写回成一种语言"：只要中英两侧不同，就说明文案真的走了文案表。
        check(HubTexts.Get("Ok", HubTexts.EnglishLanguage) != HubTexts.Get("Ok", HubTexts.ChineseLanguage)
              && HubTexts.Get("Cancel", HubTexts.EnglishLanguage) != HubTexts.Get("Cancel", HubTexts.ChineseLanguage),
            "对话框按钮文案是本地化的，而不是写死一种语言"
            + "（写死中文的话，英文界面上点开任何弹窗按钮都是中文）");

        // 走一次真实点击，验证按钮到返回值的接线。
        primary?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var result = await pending;
        check(result == HubDialogResult.Ok, "点「确定」→ ShowAsync 返回 Ok（实际 " + result + "）");

        // ---- YesNo：选否 ----
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

    /// <summary>拿本窗口真截一张图，让"非空白"判据面对一次真实渲染。</summary>
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
