using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using static AxmolHub.ThemeProbe;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellAboutChecks
{
    private readonly Action<bool, string> Check;
    internal ShellAboutChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal async Task CheckAboutMenuAsync()
    {
        // ① The name the menu bar and the About surface both belong to. The bundle's own names live in
        //    Platforms/macOS/DevelopmentInfo.plist and are not in the object graph; this is the one that is.
        Check(Application.Current?.Name == "Axmol Hub",
            "应用名称是 Axmol Hub（实际 " + Application.Current?.Name + "）");

        // ② The menu Hub exported, and the default entry it replaced. The About entry sits directly in the
        //    application menu — an item with no submenu of its own, because one row does not need a second level.
        //    Walking every header is how "the framework's About is gone" is read: it is not enough that ours
        //    exists, since both could be on the bar at once.
        var menu = NativeMenu.GetMenu(Application.Current!);
        var items = menu?.Items.OfType<NativeMenuItem>().ToArray() ?? [];
        var about = items.FirstOrDefault(item => item.Header == AboutMenuLabel);

        var headers = new List<string?>();
        void Collect(NativeMenu? source)
        {
            if (source is null) return;
            foreach (var item in source.Items.OfType<NativeMenuItem>())
            {
                headers.Add(item.Header);
                Collect(item.Menu);
            }
        }

        Collect(menu);

        Check(menu is not null && about is not null && about.Menu is null
              && items.Count(item => item.Header == AboutMenuLabel) == 1,
            "应用级原生菜单里直接有一条「" + AboutMenuLabel + "」，自己不再套一层子菜单（实际 "
            + (menu is null ? "没有导出菜单" : items.Length + " 个项：" + string.Join("；", items.Select(item => item.Header)))
            + "）");
        Check(headers.Count > 0
              && !headers.Any(header => header?.Contains("Avalonia", StringComparison.OrdinalIgnoreCase) == true),
            "框架自带的 About Avalonia 入口已被替换掉，菜单里没有残留（实际 "
            + (headers.Count == 0 ? "读到 0 个菜单项" : string.Join("；", headers)) + "）");

        // ③ The framework's own entries are still on the menu. The macOS exporter is what appends them, so that
        //    is the host this is read on — asserting them on Linux would be asserting a menu bar it never draws.
        if (OperatingSystem.IsMacOS())
        {
            Check(headers.Contains("Quit")
                  && headers.Any(header => header?.StartsWith("Hide", StringComparison.Ordinal) == true)
                  && headers.Contains("Show All"),
                "系统自带的 Hide / Hide Others / Show All / Quit 仍在，退出快捷键有落处（实际 "
                + string.Join("；", headers) + "）");
        }

        // ④ Open it the way the entry does. A null owner is HubDialog's non-modal path, which is what keeps the
        //    suite from blocking on a dialog nobody clicks — the same arrangement the foundation check uses.
        var windowsBefore = HubDialogWindows().Length;
        var pending = App.ShowAboutAsync(null);
        var dialog = HubDialogWindows().LastOrDefault();
        Check(dialog is not null && HubDialogWindows().Length == windowsBefore + 1,
            "点开关于项会弹出一个 Hub 自己的对话框（实际 " + windowsBefore + " → " + HubDialogWindows().Length + " 个）");
        if (dialog is null) return;

        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // ⑤ The title is said once: the menu label without its ellipsis, written into Window.Title and read back
        //    off the drawn bar's caption. Anything else would be two strings that can drift apart.
        var title = NamedDescendant<TextBlock>(dialog, "DialogTitle")?.Text;
        Check(about?.Header is not null && title == about.Header.TrimEnd('…') && dialog.Title == title,
            "关于对话框的标题就是菜单项的名字（菜单「" + about?.Header + "」，标题「" + title
            + "」，窗口「" + dialog.Title + "」）");

        // ⑥ Product name, the running assembly's version, then the one sentence. Built the same way the dialog
        //    builds it on purpose: what this catches is a body written as a literal, which is how a version goes
        //    stale, and a sentence dropped in a later edit.
        var body = NamedDescendant<TextBlock>(dialog, "DialogMessage")?.Text;
        var expectedBody = "Axmol Hub v" + HubReleaseInfo.DisplayVersion + "\n\n" + AboutTagline;
        var lines = body?.Split('\n') ?? [];
        Check(body == expectedBody && lines.Length == 3 && lines[0] == "Axmol Hub v" + HubReleaseInfo.DisplayVersion
              && lines[1].Length == 0 && lines[2] == AboutTagline,
            "关于信息是「产品名 + 当前版本」一行、空行、一句介绍（实际「" + body + "」）");

        // ⑦ The logo. Read off the control rather than off the call: a picture handed to the dialog and never
        //    shown is exactly the kind of silent failure this suite exists for.
        var picture = NamedDescendant<Image>(dialog, "DialogPicture");
        var logo = picture?.Source as Bitmap;
        Check(picture is { IsVisible: true } && logo is not null && picture.Width > 0 && picture.Height > 0,
            "关于对话框带 Axmol Hub 的图标（实际 "
            + (logo is null ? "没有图标" : logo.PixelSize.Width + "x" + logo.PixelSize.Height + " 像素") + "）");

        // ⑧ The repository link: its own row above the answers, captioned with the address, and wired — pressed
        //    through the same path a click takes, with the browser launch replaced so the suite opens nothing.
        var link = NamedDescendant<Button>(dialog, "DialogLink");
        var savedOpener = HubDialog.LinkOpener;
        string? opened = null;
        try
        {
            HubDialog.LinkOpener = url =>
            {
                opened = url;
                return true;
            };
            link?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            HubDialog.LinkOpener = savedOpener;
        }

        Check(link is not null && link.Content?.ToString() == AboutRepository && opened == AboutRepository
              && link.Classes.Contains("link"),
            "关于对话框带 Axmol Hub 的 GitHub 仓库链接，点下去交给打开器的是同一个地址（实际「"
            + link?.Content + "」→「" + opened + "」）");

        // ⑨ The bar is Hub's own: one close plate, drawn by HubDialogChrome — minimise and maximise belong to the
        //    main window, and this box is a dialog like every other one in the shell.
        var plates = dialog.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("window-control")).ToList();
        Check(plates.Count == 1 && plates[0].Classes.Contains("close"),
            "关于对话框沿用自绘标题栏：只有一枚关闭盘，没有最小化与最大化（实际 " + plates.Count + " 枚）");

        var buttons = ActionButtons(dialog);
        Check(buttons.Length == 0 && NamedDescendant<StackPanel>(dialog, "DialogButtons") is { IsVisible: false },
            "关于对话框没有确定键，按钮行完全收起（实际 "
            + string.Join("/", buttons.Select(button => button.Content?.ToString())) + "）");

        dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        var answer = await pending;
        Check(answer == HubDialogResult.Cancel && HubDialogWindows().Length == windowsBefore,
            "按 Esc 关掉关于对话框，结果是 Cancel、窗口没有留下（结果 " + answer + "，剩 "
            + HubDialogWindows().Length + " 个）");

        // ⑩ It opens again. A box that can only be shown once is the failure a "did it pop up" assertion cannot
        //    see, and the second time is also where the drawn bar's plate gets exercised as the exit.
        var again = App.ShowAboutAsync(null);
        var second = HubDialogWindows().LastOrDefault(candidate => !ReferenceEquals(candidate, dialog));
        Check(second is not null && second.Title == title,
            "关掉之后还能再打开一次，标题不变（实际「" + (second?.Title ?? "没有再弹出对话框") + "」）");
        if (second is not null)
        {
            second.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            NamedDescendant<Button>(second, "DialogCloseButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        var closed = await again;
        Check(closed == HubDialogResult.Cancel && HubDialogWindows().Length == windowsBefore,
            "用自绘头的关闭盘关掉关于对话框，得到 Cancel，窗口没有留下（结果 " + closed + "，剩 "
            + HubDialogWindows().Length + " 个）");

        // ⑪ The click a Mac user actually makes after closing the window: the close button hides the main window
        //    instead of ending the run (MainWindow.ShouldHideOnCloseForCheck), so the application menu stays there
        //    with nothing on screen — and that is exactly where the box failed. Asking ShowDialog for a modal owned
        //    by a window nobody can see throws "Cannot show window with non-visible owner"; raised inside the native
        //    menu callback, that ended the process instead of opening a box. The owner below is shown and then
        //    hidden on purpose: that is the state a close leaves, while a window that was never shown would not
        //    prove the platform path was taken at all.
        var hiddenOwner = new Window { Title = "hidden owner", Width = 320, Height = 200 };
        hiddenOwner.Show();
        hiddenOwner.Hide();
        var beforeHiddenOwner = HubDialogWindows().Length;
        var fromHiddenOwner = App.ShowAboutAsync(hiddenOwner);
        var alone = HubDialogWindows().LastOrDefault();
        Check(!hiddenOwner.IsVisible && alone is { IsVisible: true }
              && HubDialogWindows().Length == beforeHiddenOwner + 1,
            "主窗口藏起来后点关于项照样弹框，不再因为「所有者不可见」抛异常（实际主窗口可见 "
            + hiddenOwner.IsVisible + "，关于框 "
            + (alone is null ? "没有弹出" : alone.IsVisible ? "可见" : "不可见")
            + "，共 " + HubDialogWindows().Length + " 个）");

        // ⑫ …and it is a box, not a wedged process: it closes on its own plate and answers, with no window left over.
        if (alone is not null)
        {
            alone.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            NamedDescendant<Button>(alone, "DialogCloseButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        var aloneAnswer = await fromHiddenOwner;
        Check(aloneAnswer == HubDialogResult.Cancel && HubDialogWindows().Length == beforeHiddenOwner,
            "没有主窗口时弹出的关于框自己就能关掉，结果是 Cancel、窗口没有留下（结果 " + aloneAnswer + "，剩 "
            + HubDialogWindows().Length + " 个）");
        hiddenOwner.Close();
    }
}
