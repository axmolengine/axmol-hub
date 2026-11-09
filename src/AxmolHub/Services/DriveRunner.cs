using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using AxmolHub.Core;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AxmolHub;

/// <summary>
/// <c>--drive &lt;script.json&gt;</c> (DEBUG builds only): runs a scripted sequence against the real product window
/// and screenshots it, so a developer or the assistant can reach a specific state and look at it.
/// <code>{ "out": "tmp/drive", "steps": [ {"do":"navigate","page":"Assistant"}, {"do":"click","text":"Settings"},
/// {"do":"type","name":"Composer","value":"hi"}, {"do":"wait","ms":300}, {"do":"resize","w":1100,"h":700},
/// {"do":"language","value":"en-US"}, {"do":"screenshot","file":"a.png"} ] }</code>
/// <c>click</c>/<c>type</c> (value to enter) locate a control by <c>name</c> (Control.Name) or <c>text</c> (visible text);
/// <c>workspace</c> selects a root in the active conversation; <c>inspector-width</c>/<c>inspector-drag</c>
/// resize the sidebar (value/delta). <c>flyout</c> opens a named button's menu and optionally selects an item
/// by tag or header. <c>menu</c> chooses an item by its stable <c>Tag</c> from a named control's context menu, and
/// <c>wait</c> also accepts <c>until</c> (a name or text) with an optional <c>ms</c> timeout. <c>wheel</c> raises a
/// real wheel gesture on the transcript's scroll bar (<c>dy</c> positive scrolls up), and <c>fold</c> toggles the first
/// tool group head (<c>open</c>) — the two gestures a transcript bug usually needs. The run exits 0 when
/// every step succeeded and no shot is blank, and leaves <c>drive.evidence.txt</c> in <c>out</c>. It uses the user's
/// real data root; settings are protected by the copy made in <see cref="HubHostOptions.Parse"/>.
/// </summary>
internal static class DriveRunner
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(300);

    public static void Attach(MainWindow window, IClassicDesktopStyleApplicationLifetime lifetime, string scriptPath)
    {
        var finished = false;
        var report = new StringBuilder();
        var outDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;

        void Finish(int code)
        {
            if (finished) return;
            finished = true;
            var line = (code == 0 ? "OK" : "FAIL") + " drive";
            report.Insert(0, line + Environment.NewLine);
            Console.WriteLine(report.ToString());
            try
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "drive.evidence.txt"), report.ToString().Replace("\r\n", "\n"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            lifetime.Shutdown(code);
        }

        window.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(scriptPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("out", out var o) && o.GetString() is { Length: > 0 } od)
                    outDir = Path.GetFullPath(od);
                Directory.CreateDirectory(outDir);
                report.AppendLine("data-root: " + App.Options.ResolveDataRoot(new PreferencesStore(App.Options.PreferencesPath).Load()));

                var failed = false;
                var digests = new HashSet<string>();
                var shots = 0;
                var original = window.PreferredLanguage;
                var index = 0;
                foreach (var step in root.GetProperty("steps").EnumerateArray())
                {
                    index++;
                    var kind = Str(step, "do");
                    string? error = null;
                    try
                    {
                        error = await RunStep(window, step, kind, outDir, digests, () => shots++);
                    }
                    catch (Exception ex)
                    {
                        error = ex.GetType().Name + ": " + ex.Message;
                    }

                    report.AppendLine($"{(error is null ? "ok  " : "FAIL")} #{index} {kind}" + (error is null ? "" : " - " + error));
                    if (error is not null)
                    {
                        failed = true;
                        break;
                    }
                }

                window.UseLanguage(original);
                report.AppendLine($"shots: {shots} (distinct {digests.Count})");
                Finish(failed ? 1 : 0);
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
                Finish(1);
            }
        }, DispatcherPriority.Loaded);

        DispatcherTimer.RunOnce(() =>
        {
            report.AppendLine("script exceeded " + Ceiling.TotalSeconds + "s");
            Finish(1);
        }, Ceiling);
    }

    private static async Task<string?> RunStep(MainWindow window, JsonElement step, string kind, string outDir,
        HashSet<string> digests, Action shotTaken)
    {
        switch (kind)
        {
            case "navigate":
                window.NavigateTo(Str(step, "page"));
                await Settle();
                return null;
            case "language":
                window.UseLanguage(Str(step, "value"));
                await Settle();
                return null;
            case "workspace":
            {
                var root = Path.GetFullPath(Str(step, "path"));
                if (!Directory.Exists(root)) return "workspace directory not found: " + root;
                var chat = window.Chat;
                var session = chat.ActiveConversation ?? chat.StartConversation();
                chat.SetWorkspaceRoot(session.Id, root);
                window.NavigateTo("Assistant");
                await Settle();
                return null;
            }
            case "inspector-drag":
                window.DragInspectorForCheck(step.GetProperty("delta").GetDouble());
                await Settle();
                return null;
            case "inspector-width":
                window.SetInspectorWidthForCheck(step.GetProperty("value").GetDouble());
                await Settle();
                return null;
            case "resize":
                window.Width = step.GetProperty("w").GetDouble();
                window.Height = step.GetProperty("h").GetDouble();
                await Settle();
                return null;
            case "wheel":
            {
                // A real wheel gesture over the transcript scroll bar: the panel's own tunnel handler owns the
                // offset, the stick-to-bottom flag and the repair request, so scrolling away behaves exactly
                // as it does under a hand. Positive dy scrolls up (away from the tail).
                var dy = step.TryGetProperty("dy", out var wheelDelta) ? wheelDelta.GetDouble() : 3;
                if (!RaiseWheel(window, dy)) return "transcript scroll bar not found";
                await Settle();
                return null;
            }
            case "fold":
            {
                // Toggles a tool group head by class (plus `text` to pick the group), because the caption changes
                // with the fold state: the repro recipe needs "expand", "collapse", "expand" exactly.
                var wanted = Opt(step, "text");
                ToggleButton? FindHead()
                {
                    foreach (var candidate in window.GetVisualDescendants().OfType<ToggleButton>()
                                 .Where(b => b.Classes.Contains("activity-group-head")))
                    {
                        if (wanted is null) return candidate;
                        var caption = string.Concat(candidate.GetVisualDescendants().OfType<TextBlock>()
                            .Select(t => t.Text));
                        if (caption.Contains(wanted, StringComparison.Ordinal)) return candidate;
                    }

                    return null;
                }

                var head = FindHead();
                // A fold changes the content height, so the head can end up outside the realized range: walk back
                // towards it (positive dy scrolls up, and a head sits above the rows it folds).
                for (var attempt = 0; head is null && attempt < 30; attempt++)
                {
                    if (!RaiseWheel(window, 2)) return "transcript scroll bar not found";
                    await Settle();
                    head = FindHead();
                }

                if (head is null) return "activity group head not found";
                head.IsChecked = step.TryGetProperty("open", out var open) ? open.GetBoolean() : true;
                await Settle();
                return null;
            }
            case "wait":
            {
                var ms = step.TryGetProperty("ms", out var m) ? m.GetInt32() : 5000;
                if (step.TryGetProperty("until", out var u))
                {
                    var target = u.GetString() ?? "";
                    var deadline = DateTime.UtcNow.AddMilliseconds(ms);
                    while (Find(window, target, target) is null)
                    {
                        if (DateTime.UtcNow > deadline) return "timeout waiting for '" + target + "'";
                        await Task.Delay(50);
                    }

                    await Settle();
                    return null;
                }

                await Task.Delay(ms);
                await Settle();
                return null;
            }
            case "flyout":
            {
                if (Find(window, Opt(step, "name"), null) is not Button { Flyout: MenuFlyout flyout } owner)
                    return "menu flyout not found: " + step;
                flyout.ShowAt(owner);
                await Settle();
                if (Opt(step, "item") is { } key)
                {
                    var item = flyout.Items.OfType<MenuItem>().FirstOrDefault(candidate =>
                        string.Equals(candidate.Tag as string, key, StringComparison.Ordinal)
                        || string.Equals(candidate.Header as string, key, StringComparison.Ordinal));
                    if (item is null) return "flyout item not found: " + key;
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    flyout.Hide();
                    await Settle();
                }
                return null;
            }
            case "menu":
            {
                var owner = Find(window, Opt(step, "name"), null);
                if (owner?.ContextMenu is not { } menu) return "context menu not found for: " + step;
                menu.Open(owner);
                await Settle();
                if (Opt(step, "item") is not { } key) return null;
                var item = menu.Items.OfType<MenuItem>().FirstOrDefault(candidate =>
                    string.Equals(candidate.Tag as string, key, StringComparison.Ordinal));
                if (item is null) return "menu item not found: " + key;
                if (!item.IsEnabled) return "menu item is disabled: " + key;
                menu.Close();
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Settle();
                return null;
            }
            case "popup":
            {
                if (FindPopup(window, Str(step, "name")) is not { } popup) return "popup not found: " + step;
                popup.IsOpen = !step.TryGetProperty("close", out var close) || !close.GetBoolean();
                await Settle();
                return null;
            }
            case "dialog":
            {
                // Shown and left open for a person to look at; pair it with a long wait. Not awaited on purpose.
                var buttons = Enum.Parse<HubDialogButtons>(Opt(step, "buttons") ?? "Ok");
                var danger = step.TryGetProperty("danger", out var d) && d.GetBoolean();
                var body = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get(Str(step, "bodyKey")), 1);
                _ = HubDialog.ShowAsync(window, HubStrings.Get(Str(step, "titleKey")), body, buttons, danger);
                await Settle();
                return null;
            }
            case "click":
            case "type":
            {
                var control = Find(window, Opt(step, "name"), Opt(step, "text"));
                if (control is null) return "control not found: " + step;
                if (kind == "type")
                {
                    if (control is not TextBox box) return "not a TextBox: " + control.GetType().Name;
                    box.Focus();
                    box.Text = Str(step, "value");
                }
                else if (control is ListBoxItem item) item.IsSelected = true;
                else if (control is Button button)
                {
                    if (button is ToggleButton toggle) toggle.IsChecked = !toggle.IsChecked;
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else return "not clickable: " + control.GetType().Name;
                await Settle();
                return null;
            }
            case "screenshot":
            {
                await Settle();
                var path = Path.Combine(outDir, Str(step, "file"));
                Visual capture = window;
                if (Opt(step, "menu") is { } menuOwner)
                {
                    var anchor = Find(window, menuOwner, null)?.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault();
                    if (anchor?.GetSelfAndVisualAncestors().OfType<TopLevel>().FirstOrDefault() is not { } popup)
                        return "context menu popup not open: " + menuOwner;
                    capture = popup;
                }
                else if (Opt(step, "popup") is { } popupName)
                {
                    // A Popup is its own top-level window, so the main-window capture never contains it.
                    if (FindPopup(window, popupName)?.Child is not Visual child
                        || TopLevel.GetTopLevel(child) is not Visual root)
                        return "popup not open: " + popupName;
                    capture = root;
                }
                var stats = SmokeCapture.Capture(capture, path);
                digests.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                shotTaken();
                return stats.IsBlank() ? "blank frame: " + path : null;
            }
            default:
                return "unknown step: " + kind;
        }
    }

    private static async Task Settle()
    {
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
    }

    /// <summary>Raises a wheel gesture on the transcript's scroll bar, so the panel's own handlers run.</summary>
    private static bool RaiseWheel(Window window, double dy)
    {
        var bar = window.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Name == "TranscriptScrollBar");
        if (bar is null) return false;
        var pointer = new Pointer(0, PointerType.Mouse, true);
        bar.RaiseEvent(new PointerWheelEventArgs(bar, pointer, bar, default, 0, new PointerPointProperties(),
            KeyModifiers.None, new Vector(0, dy)) { RoutedEvent = InputElement.PointerWheelChangedEvent });
        return true;
    }

    /// <summary>A Popup is not in the visual tree, so it is found through the logical tree.</summary>
    private static Popup? FindPopup(Control root, string name)
        => root.GetLogicalDescendants().OfType<Popup>().FirstOrDefault(popup => popup.Name == name);

    private static string Str(JsonElement e, string key) => e.GetProperty(key).GetString() ?? "";

    private static string? Opt(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetString() : null;

    /// <summary>First visible control whose Name equals <paramref name="name"/>, else whose visible text
    /// equals <paramref name="text"/>; a text match resolves up to its nearest interactive ancestor.</summary>
    private static Control? Find(Visual root, string? name, string? text)
    {
        foreach (var v in root.GetVisualDescendants())
        {
            if (v is not Control c || !c.IsVisible || c.Bounds.Width <= 0) continue;
            if (name is { Length: > 0 } && c.Name == name) return c;
        }

        if (text is not { Length: > 0 }) return null;
        foreach (var v in root.GetVisualDescendants())
        {
            if (v is not Control c || !c.IsVisible || c.Bounds.Width <= 0) continue;
            var shown = c switch
            {
                TextBlock tb => tb.Text,
                ContentControl cc when cc.Content is string s => s,
                _ => null,
            };
            if (shown != text) continue;
            for (var p = (Visual)c; p is not null; p = p.GetVisualParent())
            {
                if (p is ListBoxItem or ToggleButton or Button or TextBox) return (Control)p;
            }

            return c;
        }

        return null;
    }
}
