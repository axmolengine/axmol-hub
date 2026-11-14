using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;

namespace AxmolHub;

/// <summary>
/// The clipboard routes Hub's text surfaces carry — the right-click menu, and the key that has to answer a
/// selection at all: on a selection inside a message the menu offers 复制 and 引用, on the composer it carries the
/// three clipboard verbs a text box is expected to have, and <see cref="AttachCopyKey"/> is what gives a rendered
/// document its Ctrl+C.
///
/// <para>Why this file exists at all: nothing in the shell had a context menu. Avalonia's <see cref="TextBox"/>
/// ships none, and the rendered Markdown is a third-party control with no menu of its own, so a right-click
/// anywhere over a message or over the composer did nothing — the only ways at the text were the per-message copy
/// button and Ctrl+C, and Ctrl+C copies whatever the library can extract (see
/// <see cref="MarkdownMessageRenderer.SelectionText"/> for what its extractor leaves out).</para>
///
/// <para>Two routes on purpose, because the two surfaces want opposite things from a right-click. A menu on a
/// message is <i>earned by a selection</i>: it is opened from a context-requested handler and appears only when
/// there is something to act on. The composer's is assigned to <see cref="Control.ContextFlyout"/>, which the
/// framework opens itself — and that route is not cosmetic: it is what keeps the box's selection alive while the
/// menu is up (see <see cref="AttachTextBox"/>). Because the table below cannot answer for a menu the framework
/// owns, the lookups read <see cref="Control.ContextFlyout"/> first and fall back to it.</para>
///
/// <para>A menu opened by hand costs the selection unless something pays for it, and that is why
/// <see cref="Attach"/> looks the way it does. The popup takes the keyboard focus, and a
/// <see cref="SelectableTextBlock"/> — which is what the person's own message is drawn with — answers a lost
/// focus by clearing its selection, keeping it only while its own <c>ContextFlyout</c>/<c>ContextMenu</c> is the
/// open one. So the menu is handed to the control for as long as it is up, and its rows act on what was selected
/// when it opened: the first keeps the band on screen, the second makes the copy land even if a host clears the
/// selection anyway. A rendered document needs neither half — the library's selection lives in its blocks rather
/// than in the focus.</para>
///
/// <para>A rendered document does need one further route, and it is the one <see cref="AttachCopyKey"/> adds.
/// Living in the blocks rather than in the focus is what keeps its band on screen through a menu — and it is also
/// why no key ever reached it. Avalonia delivers a shortcut along the routed key route, which begins at the
/// focused element; the document never asked for the focus, so Ctrl+C went to whatever was focused last, usually
/// the composer, whose own selection is empty. That is the whole difference between the surfaces that worked and
/// the ones that did not: a <see cref="SelectableTextBlock"/> — the person's own message — and a
/// <see cref="TextBox"/> both take the focus when they are clicked, and this document did not.</para>
/// </summary>
internal static partial class SelectionMenu
{
    /// <summary>Target → the menu attached to it. Weak on the control on purpose: a transcript rebuilds its
    /// viewers as it scrolls, and a strong table would hold every document ever painted.</summary>
    private static readonly ConditionalWeakTable<Control, MenuFlyout> Attached = new();
    private static readonly ConditionalWeakTable<TopLevel, SelectionOwner> Owners = new();

    private sealed class SelectionOwner
    {
        internal WeakReference<Control>? Active;
    }

    private static void ShareSelectionOwner(Control target)
    {
        void Register()
        {
            if (TopLevel.GetTopLevel(target) is not { } root || Owners.TryGetValue(root, out _)) return;
            var owner = new SelectionOwner();
            Owners.Add(root, owner);
            root.AddHandler(InputElement.PointerPressedEvent, (sender, e) =>
            {
                if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
                var next = source.GetVisualAncestors().Prepend(source).OfType<Control>()
                    .FirstOrDefault(control => Attached.TryGetValue(control, out _));
                // Menus and action buttons must keep the selection they are about to copy or quote.
                if (next is null) return;
                if (owner.Active?.TryGetTarget(out var previous) == true && !ReferenceEquals(previous, next))
                {
                    if (previous is MarkdownScrollViewer markdown)
                        foreach (var block in markdown.GetVisualDescendants().OfType<CTextBlock>()) block.ClearSelection();
                    else if (previous is SelectableTextBlock text) text.SelectionStart = text.SelectionEnd = 0;
                }
                owner.Active = new(next);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        target.AttachedToVisualTree += (_, _) => Register();
        Register();
    }

    /// <summary>A menu for a surface whose selection the caller can read: 复制 always, 引用 when it has somewhere
    /// to put a quote.</summary>
    internal static void Attach(Control target, Func<string> readSelection, Action<string>? quote)
    {
        var menu = new MenuFlyout();

        // What the surface held when the menu opened. A row pressed after the popup took the focus has to act on
        // what the person saw when they asked for the menu, even if something cleared the selection in between —
        // which is what a selectable block does (see the class header). Live first, this as the fallback, so a
        // surface that keeps its selection reads as it always did.
        var opened = "";
        string Selection() => readSelection() is { Length: > 0 } live ? live : opened;

        var copy = new MenuItem { Header = HubStrings.Get("CopyMessage") };
        copy.Click += async (_, _) => await CopyAsync(target, Selection());
        menu.Items.Add(copy);

        if (quote is not null)
        {
            var quoted = new MenuItem { Header = HubStrings.Get("QuoteSelection") };
            quoted.Click += (_, _) =>
            {
                var text = Selection();
                if (text.Length > 0) quote(text);
            };
            menu.Items.Add(quoted);
        }

        // The band survives the menu, and the framework is what pays for it. A popup takes the keyboard focus, and
        // a selectable block answers a lost focus by clearing its selection — unless its own
        // ContextFlyout/ContextMenu is the open one (SelectableTextBlock.OnLostFocus carries the very guard a
        // TextBox does). Opening the menu by hand leaves that property null, which is why the band went the
        // instant the menu appeared; a rendered document never had the problem, because its selection lives in the
        // library's blocks and not in the focus. So the flyout is handed to the control for as long as it is up,
        // and taken back afterwards — long enough to hold the band, short enough that the next right-click is
        // still earned by a selection rather than opened by the framework for everyone.
        var held = target as SelectableTextBlock;
        var previousFlyout = held?.ContextFlyout;
        menu.Closed += (_, _) =>
        {
            if (held is not null && ReferenceEquals(held.ContextFlyout, menu)) held.ContextFlyout = previousFlyout;
        };

        Attached.AddOrUpdate(target, menu);
        ShareSelectionOwner(target);
        target.AddHandler(InputElement.ContextRequestedEvent, (_, e) =>
        {
            // Nothing selected, nothing asked: a menu whose only rows could not act on anything is worse than no
            // menu, and leaving the event alone keeps the library's own handling (if it ever grows one) intact.
            var text = readSelection();
            if (text.Length == 0) return;
            e.Handled = true;
            opened = text;
            if (held is not null)
            {
                previousFlyout = held.ContextFlyout;
                held.ContextFlyout = menu;
            }
            menu.ShowAt(target, showAtPointer: true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The keyboard half of a text surface: <c>Ctrl+C</c> (or <c>Cmd+C</c>) hands over what is selected in
    /// it, read the same way its menu reads it.
    ///
    /// <para>Needed because a shortcut travels the routed key route, which starts at the focused element, and a
    /// rendered document never asks for the focus (see the class header). A drag therefore leaves the band on
    /// screen with the keyboard still in the composer, whose selection is empty — so Ctrl+C copied nothing, and a
    /// surface a person could select in but not copy from reads as a broken window.</para>
    ///
    /// <para>The library takes the keyboard by itself — it calls Focus on the press — so what this adds is not the
    /// taking but the giving back: the keyboard is taken when the gesture <i>selected something</i> (a drag, or a
    /// second click that picks a word), and handed to whoever held it before when the gesture selected nothing. A
    /// click that only places a caret must leave the reader's keyboard where it was, or every click made on an
    /// answer while reading back would send the next keystroke nowhere. The previous holder is read on the tunnel
    /// of the press, the last moment it can be seen (measured 2026-10-18: still the composer on the tunnel, already
    /// this document on the bubble), and the keyboard is only handed back while this element is what holds it — a
    /// press inside the fenced code editor or on a copy button focuses something of its own, and that focus is not
    /// ours to move. The adorner is cleared and the surface kept out of the tab order, because this focus exists to
    /// route a key and must neither draw chrome nor grow a Tab stop per message.</para>
    ///
    /// <para>Pointer handlers observe handled gestures because the library consumes them. The copy key is
    /// intercepted on the tunnel route and marked handled before writing, so the library's bubble class handler
    /// cannot queue a second, paragraph-terminated clipboard value over the exact selection.</para>
    /// </summary>
    internal static void AttachCopyKey(Control target, Func<string> readSelection)
    {
        target.Focusable = true;
        target.FocusAdorner = null;
        KeyboardNavigation.SetIsTabStop(target, false);

        var previous = (IInputElement?)null;
        var pressedAt = (Point?)null;
        var repeated = false;
        target.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            previous = TopLevel.GetTopLevel(target)?.FocusManager?.GetFocusedElement();
            pressedAt = e.GetPosition(target);
            repeated = e.ClickCount > 1;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        target.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            var start = pressedAt;
            var multiClick = repeated;
            var holder = previous;
            pressedAt = null;
            repeated = false;
            previous = null;
            if (start is not { } from) return;
            var to = e.GetPosition(target);
            // A press released on the very pixel it started on is a caret; a drag is a hand that moved.
            var dragged = Math.Abs(to.X - from.X) > 0.5 || Math.Abs(to.Y - from.Y) > 0.5;
            if ((dragged || multiClick) && readSelection().Length > 0) return;
            if (holder is not { } back || ReferenceEquals(back, target)) return;
            // Only when this document is the one that took it: a child that wanted the keyboard for itself (the
            // code editor, a copy button) is not something to hand back out from under.
            if (!ReferenceEquals(TopLevel.GetTopLevel(target)?.FocusManager?.GetFocusedElement(), target)) return;
            if (back is InputElement element) element.Focus();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // Intercept before the Markdown viewer's bubble class handler: it posts a second clipboard write
        // using its paragraph extractor, which appends a newline and can overwrite our exact selection later.
        target.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.C) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            var text = readSelection();
            // Nothing selected here: the key belongs to whoever else can answer it, and swallowing it would leave
            // the composer unable to copy its own text.
            if (text.Length == 0) return;
            e.Handled = true;
            await CopyAsync(target, text);
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>The composer's menu: the three clipboard verbs, each enabled by what the box can actually do
    /// with it right now.
    ///
    /// <para>Assigned to <see cref="Control.ContextFlyout"/> rather than opened from a context-requested handler,
    /// and that is not a style choice — it is the one thing that keeps the selection alive. A popup takes keyboard
    /// focus, and Avalonia's <c>TextBox.OnLostFocus</c> clears the selection unless its own
    /// <c>ContextFlyout</c>/<c>ContextMenu</c> is the open one (measured: the guard is
    /// <c>ContextFlyout is { IsOpen: true } || ContextMenu is { IsOpen: true }</c>, and
    /// <c>ClearSelectionOnLostFocus</c> defaults to true). A menu opened by hand from an event handler leaves both
    /// properties null, so the box wiped the selection the moment the menu appeared and 复制/剪切 acted on
    /// nothing. The framework's own path also positions the flyout from the request position, so the menu still
    /// appears at the pointer. And unlike a message's menu this one always opens: a text box with nothing selected
    /// still has 粘贴 to offer, and the two verbs that cannot act are greyed instead of absent.</para>
    /// </summary>
    internal static void AttachTextBox(TextBox box)
    {
        var menu = new MenuFlyout();

        var cut = new MenuItem { Header = HubStrings.Get("CutText") };
        cut.Click += (_, _) => box.Cut();
        menu.Items.Add(cut);

        var copy = new MenuItem { Header = HubStrings.Get("CopyMessage") };
        copy.Click += (_, _) => box.Copy();
        menu.Items.Add(copy);

        var paste = new MenuItem { Header = HubStrings.Get("PasteText") };
        paste.Click += (_, _) => box.Paste();
        menu.Items.Add(paste);

        // Enabled as the menu opens, per box state: rows that come and go under the pointer are worse than rows
        // that are greyed, and an empty selection leaves 复制 as the answer to "why is this dead".
        menu.Opened += (_, _) =>
        {
            cut.IsEnabled = box.CanCut;
            copy.IsEnabled = box.CanCopy;
            paste.IsEnabled = box.CanPaste;
        };
        box.ContextFlyout = menu;
        Attached.AddOrUpdate(box, menu);
    }

    /// <summary>Where a copy lands when a check wants it, instead of the real clipboard — the same hook
    /// <c>ChatSidebar.TestClipboardWriter</c> gives the conversation-id copy, and for the same reason: the suite
    /// drives the row's own click handler, and reading the text back must not throw away what the person last
    /// copied. Left null in the shell, where a copy goes to the clipboard.</summary>
    internal static Func<string, Task>? TestClipboardWriter { get; set; }

    private static async Task CopyAsync(Control source, string text)
    {
        if (text.Length == 0) return;
        if (TestClipboardWriter is { } writer)
        {
            await writer(text);
            return;
        }
        if (TopLevel.GetTopLevel(source)?.Clipboard is not { } clipboard) return;

        // The same shape the code block's copy button uses: a DataTransfer with one text item, so the clipboard
        // carries plain text and nothing else.
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);
    }
}
