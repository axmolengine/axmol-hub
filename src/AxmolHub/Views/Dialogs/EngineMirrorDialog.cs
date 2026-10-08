using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The "switch engine mirror" dialog, built in code like <see cref="EngineVersionDialog"/> and
/// <see cref="BuildTargetDialog"/>.
///
/// It shows **what the switch writes** for every option (<c>1k/.env · active_mirror=atomgit</c>,
/// <c>1k/.gitee (empty file)</c> …) rather than just a name: the choice lives in the engine tree and
/// survives reinstalling Hub, so the user is entitled to know which file changes.
///
/// The option list is passed in by the caller because it is **engine data**, not a constant: v3
/// engines declare their mirrors in <c>1k/sources.json</c> and v2 has exactly two. See
/// <see cref="EngineMirror"/>.
/// </summary>
public sealed class EngineMirrorDialog : Window
{
    private readonly ListBox _mirrors = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _storage = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16), Classes = { "muted" } };
    private readonly Button _apply;

    public MirrorOption? Selected => (_mirrors.SelectedItem as Choice)?.Option;

    private EngineMirrorDialog(EngineEntry engine, IReadOnlyList<MirrorOption> options, string current)
    {
        Title = HubStrings.Get("SwitchEngineMirror");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("SwitchEngineMirror"), FontSize = 18 });
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("MirrorHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = engine.ToString(),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        panel.Children.Add(_mirrors);
        panel.Children.Add(_storage);
        panel.Children.Add(_note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        _apply = new Button { Content = HubStrings.Get("Apply"), IsDefault = true, Classes = { "primary" } };
        _apply.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_apply);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };

        _note.Text = HubStrings.Get("MirrorFallbackNote");

        var choices = options.Select(option => new Choice(option)).ToArray();
        _mirrors.ItemsSource = choices;
        // Pre-select the mirror in effect right now, so opening the dialog and pressing Apply is a
        // no-op rather than a silent change.
        _mirrors.SelectedItem = choices.FirstOrDefault(choice => choice.Option.Id == current) ?? choices.FirstOrDefault();
        _mirrors.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (Selected is not { } option)
        {
            _storage.Text = "";
            _apply.IsEnabled = false;
            return;
        }

        _apply.IsEnabled = true;
        _storage.Text = HubStrings.Get("MirrorStoragePrefix") + "：" + option.Storage;
    }

    /// <summary>
    /// ListBox shows <c>ToString()</c> by default; wrapping the option localizes its label without
    /// pulling in an item template. A mirror Hub has no copy for (one the engine declares but Hub
    /// doesn't know) falls back to its raw id — mirroring <see cref="HubTexts.Get"/>.
    /// </summary>
    private sealed record Choice(MirrorOption Option)
    {
        // The note carries its own parentheses per language (Chinese full-width, English half-width
        // with a leading space), so appending it here needs no separator logic of its own.
        public override string ToString()
        {
            var label = Option.TextKey.Length == 0 ? Option.Id : HubStrings.Get(Option.TextKey);
            return Option.NoteKey.Length == 0 ? label : label + HubStrings.Get(Option.NoteKey);
        }
    }

    public static async Task<MirrorOption?> PickAsync(Window? owner, EngineEntry engine, IReadOnlyList<MirrorOption> options)
    {
        if (options.Count == 0) return null;
        var dialog = new EngineMirrorDialog(engine, options, EngineMirror.Current(engine));
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        return result == HubDialogResult.Ok ? dialog.Selected : null;
    }
}
