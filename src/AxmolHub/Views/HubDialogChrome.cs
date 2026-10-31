using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AxmolHub;

/// <summary>
/// The header an internal Axmol Hub dialog draws for itself, so a dialog does not arrive wearing somebody
/// else's window frame while the shell it opened from drew its own (AGENTS.md: "Draw the chrome yourself").
///
/// It is deliberately **one close plate and nothing else**. Minimise and maximise belong to the main window:
/// a dialog has no place in the taskbar's window list to be minimised to (`ShowInTaskbar="False"`), and a
/// maximised confirm box or model picker is a worse screen than the one it covered. So the bar carries the
/// proportions of a caption — 32px tall, the platform's own 46×32 plate flush to the window's top-right
/// corner, `WindowDecorations` reduced to `BorderOnly` — and exactly one of its buttons.
///
/// The header owns three things a caller would otherwise re-implement slightly differently:
/// <list type="bullet">
///   <item>the window's drawn decorations, set here so every dialog asks for the same ones;</item>
///   <item>dragging the window by the bar, through the <em>same</em> threshold decision the main window
///   uses (<see cref="DragExceeded"/>) — one number, because the reason for it is one reason;</item>
///   <item>the close plate's action, which the caller supplies because "close" is a different answer in each
///   dialog: a confirmation's × is Cancel, a list window's × is its own dismiss button.</item>
/// </list>
///
/// Named parts are a contract, not an accident — the self-check finds the bar by them:
/// <c>DialogChrome</c> (the header itself), <c>DialogDragArea</c> (its press surface), <c>DialogTitle</c>
/// (the caption) and <c>DialogCloseButton</c> (the one plate).
/// </summary>
internal sealed class HubDialogChrome
{
    /// <summary>The header's height — the same 32 the main window's bar and its
    /// <c>ExtendClientAreaTitleBarHeightHint</c> carry, so a dialog's bar is the app's bar.</summary>
    internal const double Height = 32;

    /// <summary>How far the pointer has to travel from the press before the bar starts moving the window, in
    /// DIPs: the system's own caption threshold (<c>SM_CXDRAG</c>/<c>SM_CYDRAG</c> default to 4). It is not a
    /// preference — a platform move loop is modal, so beginning one on the press would swallow the second click
    /// of any later double-click gesture. One definition for the shell's title bar and every dialog's bar.</summary>
    internal const double DragThreshold = 4;

    /// <summary>The same decision the shell's bar uses: has the pointer moved far enough to be a drag rather
    /// than a click. Pure so a check can drive both arms — the arm that acts on it calls
    /// <c>BeginMoveDrag</c>, which does not return until a mouse moves.</summary>
    internal static bool DragExceeded(Point origin, Point current)
        => Math.Abs(current.X - origin.X) >= DragThreshold
           || Math.Abs(current.Y - origin.Y) >= DragThreshold;

    private readonly Window _window;
    private readonly Border _dragArea;
    private readonly Button _closeButton;
    private Point _pressOrigin;
    private PointerPressedEventArgs? _press;

    /// <summary>
    /// Builds the header and takes over the window's decorations. <paramref name="closeTipResourceKey"/> is a
    /// resource key rather than a string so the tooltip follows a language switch that happens while the dialog
    /// is open; <paramref name="close"/> is what pressing the plate means for this dialog.
    /// </summary>
    internal HubDialogChrome(Window window, string closeTipResourceKey, Action close)
    {
        _window = window;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = Height;
        window.WindowDecorations = WindowDecorations.BorderOnly;

        // The whole bar moves the window, and the plate sits above this layer and takes its own input first.
        // Background is a real brush on purpose: Avalonia hit-tests drawn geometry, so a null background here
        // would measure and fill but let every press fall through to whatever is behind the bar.
        _dragArea = new Border { Name = "DialogDragArea", Background = Brushes.Transparent };

        // The caption is not hit-test visible: the bar is one press surface, and a label that swallowed presses
        // would punch a hole in the middle of the only region that drags the window (same rule as the shell's
        // PageTitle). Its text is a one-way binding to Window.Title, so the dialog says its title once — the OS
        // window list and the drawn bar both read that one property, and neither can fall out of step.
        var title = new TextBlock
        {
            Name = "DialogTitle",
            Classes = { "dialog-title" },
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // Fill the caption column. A capped, stretched control is centered within a wide column,
            // which shifts even a short title away from the header's leading inset.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Left,
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(Window.Title))
        {
            Source = window,
            Mode = BindingMode.OneWay,
        });

        _closeButton = new Button
        {
            Name = "DialogCloseButton",
            Classes = { "window-control", "close" },
            VerticalAlignment = VerticalAlignment.Top,
            // The × is authored 1:1 in the 12×12 box it is drawn in, with Stretch="None" — the same reason the
            // shell's three caption glyphs are: scaling a 1-DIP stroke by the fit factor is what made the
            // minimise bar render as a dot, and the 0-24 grid icon of the same name is drawn scaled.
            Content = new Avalonia.Controls.Shapes.Path
            {
                Classes = { "glyph" },
                Data = Geometry.Parse("M2 2L10 10 M10 2L2 10"),
                Width = 12,
                Height = 12,
                Stretch = Stretch.None,
            },
        };
        _closeButton.Bind(ToolTip.TipProperty, new DynamicResourceExtension(closeTipResourceKey));
        _closeButton.Click += (_, _) => close();

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_closeButton, 1);
        layout.Children.Add(title);
        layout.Children.Add(_closeButton);

        // Drag layer first, controls after: document order is the stacking contract in this app.
        var content = new Grid();
        content.Children.Add(_dragArea);
        content.Children.Add(layout);

        Header = new Border
        {
            Name = "DialogChrome",
            Classes = { "dialog-chrome" },
            Height = Height,
            ClipToBounds = true,
            Child = content,
        };

        _dragArea.PointerPressed += OnDragPressed;
        _dragArea.PointerMoved += OnDragMoved;
        _dragArea.PointerReleased += OnDragReleased;

        // The system keeps a few pixels of frame between the client area and the window's outer edge, and the
        // plate has to reach it or its hover highlight stops short of the corner. A non-resizable dialog has
        // no such frame, so this is zero there and nothing moves. RenderScaling is only reliable once the
        // window has been shown — the shell's bar installs its own inset on Opened for the same reason — so the
        // inset is applied now and again then.
        ApplyCaptionInset();
        window.Opened += (_, _) => ApplyCaptionInset();
    }

    /// <summary>The header, for the caller to put in row 0 of its <c>RowDefinitions="Auto,*"</c> grid — the row
    /// is Auto because this is the one place the 32 lives. Nothing else is exposed: the self-check reads the bar
    /// by its named parts (<c>DialogChrome</c> / <c>DialogDragArea</c> / <c>DialogTitle</c> /
    /// <c>DialogCloseButton</c>), which is also how a person's click reaches it.</summary>
    internal Control Header { get; }

    /// <summary>Place the shared caption above a bounded body and optional action row.</summary>
    internal Control Wrap(Control body, Control? footer = null)
    {
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var content = new Border
        {
            Padding = new Thickness(24, 24, 24, footer is null ? 24 : 0),
            Child = body,
        };
        Grid.SetRow(content, 1);
        layout.Children.Add(Header);
        layout.Children.Add(content);
        if (footer is not null)
        {
            var actions = new Border { Padding = new Thickness(24, 0, 24, 24), Child = footer };
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);
        }
        return layout;
    }

    /// <summary>Pushes the plate out over the window's own frame (see the constructor).
    ///
    /// <para>The margin is always written, including where the frame is zero. The base <c>Button</c> theme
    /// carries a right gutter of 8 so a row of buttons does not touch, and a dialog holds exactly one plate —
    /// for it that gutter is pure error, eight pixels short of the corner the hover highlight is supposed to
    /// fill. Returning early on a host with no frame left the inherited value standing, which is why the plate
    /// reached the edge on Windows (where the negative inset overwrote it) and stopped short everywhere else.
    /// Writing zero here is also what makes the negative case below a replacement rather than an accident.</para>
    ///
    /// <para>Only this plate is touched: the main window's three buttons keep the gutter as their spacing, and
    /// that bar pulls its own negative inset on the whole panel instead.</para>
    /// </summary>
    private void ApplyCaptionInset()
    {
        var scaling = _window.RenderScaling <= 0 ? 1 : _window.RenderScaling;
        var frame = TitleBarSnapHook.ResizeFrameWidth;
        _closeButton.Margin = new Thickness(0, 0, frame > 0 ? -(frame / scaling) : 0, 0);
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Unlike the shell's bar, a dialog's bar has no second-click meaning: nothing here maximises, so the
        // first press can arm the drag and the threshold alone decides whether it becomes one.
        _pressOrigin = e.GetPosition(_window);
        _press = e;
        e.Pointer.Capture(_dragArea);
    }

    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press)
        {
            return;
        }

        if (!DragExceeded(_pressOrigin, e.GetPosition(_window)))
        {
            return;
        }

        // Let go of the press before handing the platform its move loop: that loop runs until the button comes
        // up, and nothing after this line in the handler may be relied on, so the state has to be settled first.
        _press = null;
        e.Pointer.Capture(null);
        _window.BeginMoveDrag(press);
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is null)
        {
            return;
        }

        _press = null;
        e.Pointer.Capture(null);
    }

    /// <summary>The row under a dialog's body: Cancel, then the confirm plate, right-aligned at the body's own
    /// margin. Every dialog that drew its own chrome also drew its own version of this row, and the copies had
    /// drifted apart in spacing and order — so the row lives here beside the header, with the same named-part
    /// contract for the self-check: <c>DialogFooter</c>, <c>DialogCancelButton</c>, <c>DialogConfirmButton</c>.
    ///
    /// <para>The two labels arrive as text rather than resource keys: a dialog's confirm wording ("Save",
    /// "Create", "Approve") is the caller's, and a language switch while the dialog is open is already not
    /// followed by the copies this replaces.</para>
    /// </summary>
    internal static (Control Root, Button Confirm, Button Cancel) Footer(
        string confirmText, string cancelText, Action confirm, Action cancel)
    {
        var row = new StackPanel
        {
            Name = "DialogFooter",
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var cancelButton = new Button
        {
            Name = "DialogCancelButton",
            Content = cancelText,
            IsCancel = true,
            Classes = { "compact" },
            Margin = new Thickness(0),
        };
        cancelButton.Click += (_, _) => cancel();

        var confirmButton = new Button
        {
            Name = "DialogConfirmButton",
            Content = confirmText,
            IsDefault = true,
            Classes = { "compact", "primary" },
            Margin = new Thickness(0),
        };
        confirmButton.Click += (_, _) => confirm();

        row.Children.Add(cancelButton);
        row.Children.Add(confirmButton);
        return (row, confirmButton, cancelButton);
    }
}
