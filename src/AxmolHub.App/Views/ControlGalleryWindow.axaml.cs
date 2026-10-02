using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShapePath = Avalonia.Controls.Shapes.Path;
// 断言用的小工具（TokenColor / IsToken / Descendant / NamedDescendant …）已抽到
// Services/ThemeProbe.cs，与 P4 的基础件自检共用一份实现。
using static AxmolHub.App.ThemeProbe;

namespace AxmolHub.App;

/// <summary>
/// P3 的验收工具窗口：把 Theme/ 下的每个样式摆在一个界面上，用于三平台观感比对。
/// 只有中文说明，因为它是开发工件而不是产品界面（产品界面在 P5 才迁移）。
/// </summary>
public partial class ControlGalleryWindow : Window
{
    private static readonly GalleryProject[] SampleProjects =
    {
        new("HelloAxmol", "2.11.5", "已成功", "2026-10-02 15:04"),
        new("MoonRider", "2.11.5", "已失败", "2026-10-01 22:11"),
        new("TileForge", "2.11.5", "未构建", "2026-09-28 09:37"),
    };

    public ControlGalleryWindow()
    {
        InitializeComponent();
        GalleryGrid.ItemsSource = SampleProjects;
    }

    private void OnVariantChanged(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = VariantLight.IsChecked == true
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }

    /// <summary>
    /// 核对每个 ControlTheme / 类样式是否**真的**生效。存在的理由：样式表写错时 Avalonia
    /// 不会报错，只会静默地用默认外观渲染 —— 那种失败既编译不过不了也抛不出异常，只能靠断言。
    /// 结果写成文件而不是 stdout：App 是 WinExe，没有控制台可写。
    /// </summary>
    public void RunThemeVerification(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath)
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

        // 挂在 Opened 而不是直接 Post：窗口显示之后可视化树才存在，之前跑断言会得到一堆假失败。
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
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
                RunChecks(Check);
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

    private void RunChecks(Action<bool, string> check)
    {
        // ---- 先确认断言自己没坏：语义色必须能解析出来，否则下面每一条都会假失败 ----
        var probe = TokenColor("Hub.SurfaceRaised");
        check(probe is not null,
            "语义色可从 Application.Resources 解析（Hub.SurfaceRaised = " + (probe?.ToString() ?? "null") + "）");

        // ---- 按钮：ControlTheme 是否取代了 Fluent 默认 ----
        check(IsToken(BtnDefault.Background, "Hub.SurfaceRaised"),
            "Button 默认背景 = Hub.SurfaceRaised（实际 " + Describe(BtnDefault.Background) + "）");
        check(IsToken(BtnPrimary.Background, "Hub.Accent"),
            "Button.primary 背景 = Hub.Accent（实际 " + Describe(BtnPrimary.Background) + "）");
        check(ColorOf(BtnQuiet.Background) == Colors.Transparent,
            "Button.quiet 背景 = Transparent（实际 " + Describe(BtnQuiet.Background) + "）");
        var buttonFrame = NamedDescendant<Border>(BtnDefault, "Frame");
        check(buttonFrame is not null && Math.Abs(buttonFrame.CornerRadius.TopLeft - 5) < 0.001,
            "Button 模板为 Hub 版本（Border#Frame，圆角 = 5）");

        // ---- TextBox：Avalonia 12 的必需要件是 PART_TextPresenter，不是 WPF 的 PART_ContentHost ----
        check(NamedDescendant<TextPresenter>(TxtNormal, "PART_TextPresenter") is not null,
            "TextBox 模板包含 TextPresenter#PART_TextPresenter");
        check(IsToken(TxtNormal.Background, "Hub.SurfaceSunken"),
            "TextBox 背景 = Hub.SurfaceSunken（实际 " + Describe(TxtNormal.Background) + "）");
        check(IsToken(TxtPassword.Background, "Hub.SurfaceAlt"),
            "TextBox.password 背景 = Hub.SurfaceAlt（PasswordBox 的等价物）");
        check(TxtMono.FontFamily?.Name?.Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true
              || TxtMono.FontFamily?.ToString().Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true,
            "TextBox.mono 用了等宽字体回退链（实际 " + (TxtMono.FontFamily?.ToString() ?? "null") + "）");

        // ---- ComboBox：PART_Popup / PART_ItemsPresenter 都在 ----
        // 先展开：Popup 的内容在未打开时根本没有实例化，直接找会得到假失败。
        CmbEngine.IsDropDownOpen = true;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var comboPopup = NamedDescendant<Popup>(CmbEngine, "PART_Popup");
        check(comboPopup is not null, "ComboBox 模板包含 Popup#PART_Popup");
        // Popup 的内容不在 ComboBox 的可视化子树里（它有自己的 popup root），必须从 Popup 内部往下找。
        var popupContent = comboPopup?.Child as Visual
                           ?? comboPopup?.GetVisualDescendants().OfType<Visual>().FirstOrDefault();
        var comboItems = popupContent is null
            ? null
            : NamedDescendant<ItemsPresenter>(popupContent, "PART_ItemsPresenter") ?? Descendant<ItemsPresenter>(popupContent);
        check(comboItems is not null,
            "ComboBox 的 Popup 内含 ItemsPresenter#PART_ItemsPresenter（content="
            + (popupContent?.GetType().Name ?? "null") + "）");

        CmbEngine.IsDropDownOpen = false;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // ---- CheckBox ----
        var checkboxBox = NamedDescendant<Border>(ChkChecked, "Box");
        check(checkboxBox is not null, "CheckBox 模板包含 Border#Box");
        check(checkboxBox is not null && IsToken(checkboxBox.Background, "Hub.AccentSoft"),
            "CheckBox 勾选态底色 = Hub.AccentSoft（实际 " + Describe(checkboxBox?.Background) + "）");
        var tick = NamedDescendant<ShapePath>(ChkChecked, "Tick");
        check(tick is not null && tick.IsVisible,
            "CheckBox 勾选态显示矢量对勾（WPF 用的是「✓」字形）");

        // ---- RadioButton ----
        var radioFrame = NamedDescendant<Border>(RadChecked, "Frame");
        check(radioFrame is not null && IsToken(radioFrame.Background, "Hub.SurfaceSelected"),
            "RadioButton 选中态底色 = Hub.SurfaceSelected（实际 " + Describe(radioFrame?.Background) + "）");

        // ---- Expander：这条覆盖 ControlTheme 里的 ^:checked 选择器 ----
        var header = NamedDescendant<ToggleButton>(Exp, "ExpanderHeader");
        check(header is not null, "Expander 模板包含 ToggleButton#ExpanderHeader（复用 HubExpanderHeader 主题）");
        if (header is not null)
        {
            check(NamedDescendant<ShapePath>(header, "Arrow") is not null, "Expander 头部模板包含 Path#Arrow");
            Exp.IsExpanded = true;
            UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var arrowAfter = NamedDescendant<ShapePath>(header, "Arrow");
            // rotate(90deg) 解析出来的是 TransformOperations（矩阵），不是 RotateTransform 实例；
            // 矩阵挂在 .Value 上（12.1.3 源：public Matrix Value { get; }）。
            var expectedRotation = TransformOperations.Parse("rotate(90deg)");
            var actualTransform = arrowAfter?.RenderTransform;
            check(actualTransform is TransformOperations operations && operations.Value == expectedRotation.Value,
                "展开后箭头旋转 90°（IsChecked=" + header.IsChecked
                + "，RenderTransform=" + (actualTransform?.ToString() ?? "null") + "）");
            check(NamedDescendant<Border>(Exp, "ExpanderBody")?.IsVisible == true,
                "展开后内容区可见");
        }

        // ---- ScrollBar：换掉了 Fluent 的悬浮自动隐藏细条 ----
        // 按方向取：垂直条只设了 Width，水平条只设了 Height；拿错方向会得到一串假失败。
        var scrollBars = Scroller.GetVisualDescendants().OfType<ScrollBar>().ToList();
        check(scrollBars.Count > 0, "ScrollViewer 内已生成 ScrollBar（" + scrollBars.Count + " 条）");

        var verticalBar = scrollBars.FirstOrDefault(s => s.Orientation == Orientation.Vertical);
        check(verticalBar is not null && Math.Abs(verticalBar.Width - 10) < 0.001,
            "纵向 ScrollBar 宽度 = 10（实际 " + (verticalBar is null ? "无纵向条" : verticalBar.Width.ToString(CultureInfo.InvariantCulture)) + "）");
        if (verticalBar is not null)
        {
            var track = NamedDescendant<Track>(verticalBar, "PART_Track");
            check(track is not null, "ScrollBar 模板包含 Track#PART_Track");
            check(track?.Thumb is not null, "Track 挂了 Thumb（无翻页按钮，与 WPF 版一致）");
        }

        var horizontalBar = scrollBars.FirstOrDefault(s => s.Orientation == Orientation.Horizontal);
        check(horizontalBar is null || Math.Abs(horizontalBar.Height - 10) < 0.001,
            "横向 ScrollBar 高度 = 10（实际 " + (horizontalBar is null ? "无横向条" : horizontalBar.Height.ToString(CultureInfo.InvariantCulture)) + "）");

        // ---- DataGrid：只覆盖属性与选中色，不换模板 ----
        GalleryGrid.SelectedIndex = 0;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var row = Descendant<DataGridRow>(GalleryGrid);
        check(row is not null, "DataGrid 生成了数据行");
        if (row is not null)
        {
            check(row.IsSelected, "第一行处于选中态（SelectionMode=Single 生效）");
            var selectionRect = NamedDescendant<Rectangle>(row, "BackgroundRectangle");
            check(selectionRect is not null, "DataGridRow 模板包含 Rectangle#BackgroundRectangle");
            check(selectionRect is not null && IsToken(selectionRect.Fill, "Hub.RowSelected"),
                "选中行底色 = Hub.RowSelected（覆盖了 Fluent 的选中色，实际 " + Describe(selectionRect?.Fill) + "）");
        }
        check(Descendant<DataGridColumnHeader>(GalleryGrid) is not null, "DataGrid 生成了列头");

        // ---- 类样式：Border.card / TextBlock.muted ----
        var card = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
        check(card is not null && IsToken(card.Background, "Hub.Surface"),
            "Border.card 背景 = Hub.Surface（实际 " + Describe(card?.Background) + "）");
        check(card is not null && Math.Abs(card.CornerRadius.TopLeft - 7) < 0.001,
            "Border.card 圆角 = 7（实际 " + (card?.CornerRadius.TopLeft ?? double.NaN).ToString(CultureInfo.InvariantCulture) + "）");

        var muted = this.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("muted"));
        check(muted is not null && IsToken(muted.Foreground, "Hub.TextSecondary"),
            "TextBlock.muted 前景 = Hub.TextSecondary（实际 " + Describe(muted?.Foreground) + "）");
        check(muted is not null && Math.Abs(muted.FontSize - 12) < 0.001,
            "TextBlock.muted 字号 = 12");

        // ---- 矢量图标：StaticResource 是否真的解析到了 ----
        check(IconProjects.Data is not null && IconInstalls.Data is not null
              && IconToolchains.Data is not null && IconSettings.Data is not null,
            "四个导航图标几何解析成功（StaticResource 未失效）");
        check(IsToken(IconProjects.Fill, "Hub.TextPrimary"),
            "图标填充 = Hub.TextPrimary（实际 " + Describe(IconProjects.Fill) + "）");

        // ---- 主题变体切换 ----
        var darkBackground = ColorOf(Background);
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var lightBackground = ColorOf(Background);
            check(darkBackground != lightBackground,
                "切换 ThemeVariant 后窗口底色改变（深色 " + darkBackground + " → 浅色 " + lightBackground + "）");
            check(IsToken(Background, "Hub.Background"),
                "浅色变体下窗口底色 = Hub.Background 的浅色值（实际 " + Describe(Background) + "）");

            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            UpdateLayout();
        }
    }
}

internal sealed record GalleryProject(string Name, string Version, string Status, string LastOpened);
