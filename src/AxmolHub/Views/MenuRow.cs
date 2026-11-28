using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AxmolHub;

/// <summary>One row of a menu: a glyph, a name, and a tick when this is the answer in force. A row inside a
/// submenu carries no glyph — the parent names the question, the rows under it are its answers — and its title
/// keeps the whole glyph slot as inset, so a child never starts left of its parent. The width is fixed across
/// rows so the ticks land in a column rather than wherever each name happens to end.
///
/// Shared by every MenuFlyout that offers a choice: the assistant's switchboard (<c>MainWindow.BuildBottomMenu</c>)
/// and the diff viewer's view menu draw the same row, so a tick means the same thing in both.</summary>
internal static class MenuRow
{
    internal static Control Build(string? iconKey, string title, bool current, string? badge = null,
        bool chevron = false)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinWidth = 200 };

        var hasGlyph = iconKey is { Length: > 0 };
        if (hasGlyph)
        {
            var icon = new Avalonia.Controls.Shapes.Path
            {
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                Fill = Brushes.Transparent,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension(iconKey!));
            icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
            Grid.SetColumn(icon, 0);
            header.Children.Add(icon);
        }

        // Named rather than positional: a row's parts are read back by the self-check, and "the first TextBlock
        // in the grid" is a description of where a label sits, not of what it is — the same reason the provider
        // groups carry role tags.
        var text = new TextBlock
        {
            Name = "MenuRowTitle",
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(hasGlyph ? 12 : 27, 0, 0, 0),
        };
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        // A badge is a count of what the row would show, so it sits where the eye looks for a number and the tick
        // keeps the column it has always had. A row that opens a submenu gets the chevron instead: it is not an
        // answer to this question, it is another question.
        if (badge is { Length: > 0 })
        {
            var count = new TextBlock
            {
                Name = "MenuRowBadge",
                Text = badge,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            count.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
            Grid.SetColumn(count, 2);
            header.Children.Add(count);
        }

        if (chevron)
        {
            var arrow = new Avalonia.Controls.Shapes.Path
            {
                Name = "MenuRowChevron",
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                Fill = Brushes.Transparent,
                StrokeThickness = 1.7,
                StrokeLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                IsVisible = true,
            };
            arrow.Bind(Avalonia.Controls.Shapes.Path.DataProperty,
                new DynamicResourceExtension("Hub.Icon.ChevronRight"));
            arrow.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty,
                new DynamicResourceExtension("Hub.TextSecondary"));
            Grid.SetColumn(arrow, 2);
            header.Children.Add(arrow);
        }

        var tick = Tick(current);
        tick.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(tick, 3);
        header.Children.Add(tick);
        return header;
    }

    internal static Avalonia.Controls.Shapes.Path Tick(bool current)
    {
        var tick = new Avalonia.Controls.Shapes.Path
        {
            Name = "MenuRowTick",
            Width = 11,
            // Match Hub.Icon.MenuTick's 16:11 aspect ratio (~7.6 DIP tall) so Stretch.Uniform leaves no bottom slack
            // that would push the checkmark above vertical center.
            Height = 8,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = current,
            // The key, not only the binding: a menu a check built has no resource host to resolve against, so
            // Data reads null there. The key says which glyph the mark asked for without the dictionaries.
            Tag = "Hub.Icon.MenuTick",
        };
        tick.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension("Hub.Icon.MenuTick"));
        tick.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        return tick;
    }
}
