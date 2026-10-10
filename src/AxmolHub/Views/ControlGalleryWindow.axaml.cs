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
// The small assertion helpers (TokenColor / IsToken / Descendant / NamedDescendant …) have been
// extracted to Services/ThemeProbe.cs and shared with P4's foundation self-check.
using static AxmolHub.ThemeProbe;

namespace AxmolHub;

/// <summary>
/// P3's verification tool window: lays out every style under Theme/ on a single surface for
/// three-platform visual comparison.
/// This is a development artifact rather than a product UI (the product UI is migrated in P5), so
/// the captions are plain literals instead of localized copy. The assertion names stay Chinese:
/// they are what the verify-theme report prints.
/// </summary>
public partial class ControlGalleryWindow : Window
{
    private static readonly GalleryProject[] SampleProjects =
    {
        new("HelloAxmol", "2.11.5", "Succeeded", "2026-10-02 15:04"),
        new("MoonRider", "2.11.5", "Failed", "2026-10-01 22:11"),
        new("TileForge", "2.11.5", "Not built", "2026-09-28 09:37"),
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
    /// Verifies that each ControlTheme / class style **actually** takes effect. It exists because:
    /// when a stylesheet is wrong, Avalonia doesn't error — it just silently renders with the
    /// default appearance. That failure neither fails compilation nor throws, so it can only be
    /// caught by assertions.
    /// Results are written to a file rather than stdout: the app is a WinExe with no console.
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

        // Hook Opened rather than posting directly: the visual tree only exists after the window is shown; running assertions before that yields a pile of false failures.
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

        // Fallback: don't let the process hang forever if the window never shows (in automation, hanging is harder to diagnose than failing).
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
        // ---- First confirm the assertion itself isn't broken: the semantic color must resolve, otherwise every line below fails spuriously ----
        var probe = TokenColor("Hub.SurfaceRaised");
        check(probe is not null,
            "语义色可从 Application.Resources 解析（Hub.SurfaceRaised = " + (probe?.ToString() ?? "null") + "）");
        var darkProviderSurface = TokenColor("Hub.ProviderSurface");
        check(darkProviderSurface is not null && darkProviderSurface != TokenColor("Hub.Surface"),
            "深色主题 provider 卡片底色与模型项底色区分");

        // ---- Buttons: did the ControlTheme replace the Fluent default ----
        check(IsToken(BtnDefault.Background, "Hub.SurfaceRaised"),
            "Button 默认背景 = Hub.SurfaceRaised（实际 " + Describe(BtnDefault.Background) + "）");
        check(IsToken(BtnPrimary.Background, "Hub.Accent"),
            "Button.primary 背景 = Hub.Accent（实际 " + Describe(BtnPrimary.Background) + "）");
        check(ColorOf(BtnQuiet.Background) == Colors.Transparent,
            "Button.quiet 背景 = Transparent（实际 " + Describe(BtnQuiet.Background) + "）");
        var buttonFrame = NamedDescendant<Border>(BtnDefault, "Frame");
        check(buttonFrame is not null && Math.Abs(buttonFrame.CornerRadius.TopLeft - 5) < 0.001,
            "Button 模板为 Hub 版本（Border#Frame，圆角 = 5）");

        // ---- The "asks before acting" affordance: an ellipsis that only .more turns on ----
        // Read through the template rather than the label: the whole point of the marker is that it is
        // NOT part of the string (Build / Uninstall / PrebuiltSettings are reused as dialog titles, so
        // the ellipsis must never leak into them).
        var defaultAffordance = NamedDescendant<TextBlock>(BtnDefault, "PART_MoreAffordance");
        var moreAffordance = NamedDescendant<TextBlock>(BtnMore, "PART_MoreAffordance");
        var morePrimaryAffordance = NamedDescendant<TextBlock>(BtnMorePrimary, "PART_MoreAffordance");
        string Shown(TextBlock? part) => part is null ? "找不到角标" : part.IsVisible.ToString();
        check(defaultAffordance is { IsVisible: false },
            "普通按钮不显示省略号（实际 " + Shown(defaultAffordance) + "）");
        check(moreAffordance is { IsVisible: true },
            "Button.more 显示省略号（实际 " + Shown(moreAffordance) + "）");
        check(morePrimaryAffordance is { IsVisible: true }
              && IsToken(morePrimaryAffordance.Foreground, "Hub.TextOnAccent"),
            "primary + more 的省略号跟着按钮前景色走，不被全局 TextBlock 样式染成正文色（实际 "
            + Describe(morePrimaryAffordance?.Foreground) + "）");
        check(IsToken(BtnDestructive.Foreground, "Hub.DangerText")
              && ColorOf(BtnDestructive.Background) == Colors.Transparent,
            "Button.destructive = 透明底 + Hub.DangerText 文字（实际前景 "
            + Describe(BtnDestructive.Foreground) + " / 背景 " + Describe(BtnDestructive.Background) + "）");

        // ---- TextBox: Avalonia 12's required part is PART_TextPresenter, not WPF's PART_ContentHost ----
        check(NamedDescendant<TextPresenter>(TxtNormal, "PART_TextPresenter") is not null,
            "TextBox 模板包含 TextPresenter#PART_TextPresenter");
        check(IsToken(TxtNormal.Background, "Hub.SurfaceSunken"),
            "TextBox 背景 = Hub.SurfaceSunken（实际 " + Describe(TxtNormal.Background) + "）");
        check(IsToken(TxtPassword.Background, "Hub.SurfaceAlt"),
            "TextBox.password 背景 = Hub.SurfaceAlt（PasswordBox 的等价物）");
        check(TxtMono.FontFamily?.Name?.Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true
              || TxtMono.FontFamily?.ToString().Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true,
            "TextBox.mono 用了等宽字体回退链（实际 " + (TxtMono.FontFamily?.ToString() ?? "null") + "）");

        // ---- ComboBox: PART_Popup / PART_ItemsPresenter are both present ----
        // Expand first: the Popup's content isn't instantiated until opened; searching directly yields false failures.
        CmbEngine.IsDropDownOpen = true;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var comboPopup = NamedDescendant<Popup>(CmbEngine, "PART_Popup");
        check(comboPopup is not null, "ComboBox 模板包含 Popup#PART_Popup");
        // The Popup's content isn't in the ComboBox's visual subtree (it has its own popup root); search downward from inside the Popup.
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

        // ---- Expander: this covers the ^:checked selector in the ControlTheme ----
        var header = NamedDescendant<ToggleButton>(Exp, "ExpanderHeader");
        check(header is not null, "Expander 模板包含 ToggleButton#ExpanderHeader（复用 HubExpanderHeader 主题）");
        if (header is not null)
        {
            check(NamedDescendant<ShapePath>(header, "Arrow") is not null, "Expander 头部模板包含 Path#Arrow");
            Exp.IsExpanded = true;
            UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var arrowAfter = NamedDescendant<ShapePath>(header, "Arrow");
            // rotate(90deg) parses to TransformOperations (a matrix), not a RotateTransform instance;
            // the matrix lives on .Value (12.1.3 source: public Matrix Value { get; }).
            var expectedRotation = TransformOperations.Parse("rotate(90deg)");
            var actualTransform = arrowAfter?.RenderTransform;
            check(actualTransform is TransformOperations operations && operations.Value == expectedRotation.Value,
                "展开后箭头旋转 90°（IsChecked=" + header.IsChecked
                + "，RenderTransform=" + (actualTransform?.ToString() ?? "null") + "）");
            check(NamedDescendant<Border>(Exp, "ExpanderBody")?.IsVisible == true,
                "展开后内容区可见");
        }

        // ---- ScrollBar: replaced Fluent's hover auto-hide thin bar ----
        // Take by orientation: the vertical bar only sets Width, the horizontal only Height; taking the wrong orientation yields a string of false failures.
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

        // ---- DataGrid: only covers properties and selection color, no template swap ----
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

        // ---- Class styles: Border.card / TextBlock.muted ----
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

        // ---- Vector icons: did StaticResource actually resolve ----
        check(IconProjects.Data is not null && IconInstalls.Data is not null
              && IconToolchains.Data is not null && IconSettings.Data is not null,
            "四个导航图标几何解析成功（StaticResource 未失效）");
        check(IsToken(IconProjects.Fill, "Hub.TextPrimary"),
            "图标填充 = Hub.TextPrimary（实际 " + Describe(IconProjects.Fill) + "）");

        // ---- Theme variant switching ----
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
            var lightProviderSurface = TokenColor("Hub.ProviderSurface");
            check(lightProviderSurface is not null
                  && lightProviderSurface != TokenColor("Hub.Surface")
                  && lightProviderSurface != darkProviderSurface,
                "浅色主题 provider 卡片底色与模型项底色区分且适配主题切换");

            // The chat column's three surfaces, in the variant nothing else photographs: the pill and the composer
            // both sit on the page, and the chips on the composer sit on the composer. Each pair that actually
            // touches has to be more than a rounding error apart, or a message reads as floating on nothing.
            var lightSurface = TokenColor("Hub.Surface");
            var lightComposer = TokenColor("Hub.SurfaceAlt");
            var lightChip = TokenColor("Hub.SurfaceRaised");
            check(Separates(lightBackground, lightSurface),
                "浅色下用户气泡与页面底色分得开（" + lightBackground + " vs " + lightSurface + "）");
            check(Separates(lightBackground, lightComposer),
                "浅色下输入框与页面底色分得开（" + lightBackground + " vs " + lightComposer + "）");
            check(Separates(lightComposer, lightChip),
                "浅色下 composer 上的胶囊与框底分得开（" + lightComposer + " vs " + lightChip + "）");

            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            UpdateLayout();
        }
    }
}

internal sealed record GalleryProject(string Name, string Version, string Status, string LastOpened);
