using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AxmolHub.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace AxmolHub;

/// <summary>
/// The right-hand review pane: the plan in full on one tab, the diff of every file this conversation touched on
/// the other. Built imperatively like the rest of the chat UI — no MVVM, style classes are the locator contract.
///
/// It is handed its content on every <see cref="Reload"/> rather than reaching into the workspace itself, so the
/// panel stays a pure view: the caller (ChatPanel) owns the conversation, the diff resolver and the markdown
/// renderer, and this only lays out what it is given. That is also what keeps it checkable — a check can Reload
/// it with a fixture and assert the frame without a live session behind it.
/// </summary>
internal sealed class InspectorPanel : UserControl
{
    /// <summary>Raised by the × in the header. The panel does not close itself: whether it collapses or the whole
    /// column hides is the shell's call, and only the shell knows the page it is on.</summary>
    public event Action? CloseRequested;

    private readonly Button _planTab;
    private readonly Button _changesTab;
    private readonly ContentControl _planHost;
    private readonly ScrollViewer _changesScroll;
    private readonly StackPanel _changesList;
    private readonly Grid _body;

    public InspectorPanel()
    {
        ClipToBounds = true;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        // ── Header: two tabs and a close. The tabs are the only navigation, so they read as text buttons whose
        //    selected one carries an underline plate rather than as a chrome-heavy TabControl. ──
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(12, 10, 8, 8) };
        _planTab = TabButton("InspectorTabPlan");
        _changesTab = TabButton("InspectorTabChanges");
        _planTab.Click += (_, _) => SelectTab(plan: true);
        _changesTab.Click += (_, _) => SelectTab(plan: false);
        header.Children.Add(_planTab);
        Grid.SetColumn(_changesTab, 1);
        _changesTab.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(_changesTab);

        var close = new Button { Classes = { "viewer-action" }, Tag = "InspectorClose" };
        var closeIcon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
        };
        closeIcon.Bind(Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Close"));
        closeIcon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        close.Content = closeIcon;
        ToolTip.SetTip(close, HubStrings.Get("InspectorClose"));
        close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(close, 3);
        header.Children.Add(close);
        root.Children.Add(header);

        // ── Body: the two tab surfaces stacked, only one visible. Both live in the tree so switching tabs does
        //    not rebuild them, and a check can read either without selecting it first. ──
        _body = new Grid();
        Grid.SetRow(_body, 1);

        _planHost = new ContentControl { Margin = new Thickness(12, 0, 12, 12) };
        _body.Children.Add(_planHost);

        _changesList = new StackPanel { Spacing = 6 };
        _changesScroll = new ScrollViewer
        {
            Content = _changesList,
            Margin = new Thickness(12, 0, 6, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _changesScroll.IsVisible = false;
        _body.Children.Add(_changesScroll);

        root.Children.Add(_body);
        Content = root;
        SelectTab(plan: true);
    }

    private static Button TabButton(string textKey)
    {
        var button = new Button { Classes = { "inspector-tab" }, Content = HubStrings.Get(textKey), Tag = textKey };
        return button;
    }

    private void SelectTab(bool plan)
    {
        _planTab.Classes.Set("selected", plan);
        _changesTab.Classes.Set("selected", !plan);
        _planHost.IsVisible = plan;
        _changesScroll.IsVisible = !plan;
    }

    /// <summary>Paints both tabs from a snapshot and lands on the asked-for one. The diff resolver is lazy per
    /// file: a session of twelve writes must not read twelve files to show a list, only the one a person opens.</summary>
    public void Reload(
        string? planMarkdown,
        IReadOnlyList<ChangedFile> changes,
        Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff,
        string tab)
    {
        _planHost.Content = string.IsNullOrWhiteSpace(planMarkdown)
            ? EmptyLine("InspectorPlanEmpty")
            : MarkdownMessageRenderer.Render(planMarkdown!);

        _changesList.Children.Clear();
        if (changes.Count == 0)
        {
            _changesList.Children.Add(EmptyLine("InspectorChangesEmpty"));
        }
        else
        {
            foreach (var change in changes)
                _changesList.Children.Add(BuildChangeRow(change, resolveDiff));
        }

        SelectTab(plan: !string.Equals(tab, "changes", StringComparison.Ordinal));
    }

    private static Control EmptyLine(string textKey)
        => Themed(new TextBlock
        {
            Text = HubStrings.Get(textKey),
            TextWrapping = TextWrapping.Wrap,
            Classes = { "inspector-empty" },
        }, "Hub.TextTertiary");

    private Control BuildChangeRow(ChangedFile change, Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff)
    {
        var container = new StackPanel { Spacing = 0, Classes = { "inspector-change" } };

        var (added, removed) = PreviewCounts(change, resolveDiff);

        var header = new Button { Classes = { "inspector-change-header" }, Tag = change.RelativePath };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var verdict = new TextBlock
        {
            Text = VerdictMark(change.Verdict),
            Classes = { "inspector-verdict", VerdictClass(change.Verdict) },
        };
        grid.Children.Add(verdict);

        var name = Themed(new TextBlock
        {
            Text = change.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-path" },
        }, "Hub.TextPrimary");
        Grid.SetColumn(name, 1);
        name.Margin = new Thickness(6, 0, 6, 0);
        grid.Children.Add(name);

        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (added > 0) counts.Children.Add(Themed(new TextBlock
        {
            Text = string.Format(HubStrings.Get("InspectorAddedFormat"), added),
            Classes = { "inspector-count-added" },
        }, "Hub.Success"));
        if (removed > 0) counts.Children.Add(Themed(new TextBlock
        {
            Text = string.Format(HubStrings.Get("InspectorRemovedFormat"), removed),
            Classes = { "inspector-count-removed" },
        }, "Hub.Danger"));
        Grid.SetColumn(counts, 2);
        grid.Children.Add(counts);

        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        header.Click += (_, _) =>
        {
            if (!detail.IsVisible && detail.Content is null)
                detail.Content = BuildDiffDetail(change, resolveDiff);
            detail.IsVisible = !detail.IsVisible;
            header.Classes.Set("expanded", detail.IsVisible);
        };

        container.Children.Add(header);
        container.Children.Add(detail);
        return container;
    }

    /// <summary>The +N −M chip counts a frozen preview without opening the file; when there is none the chip is
    /// left off rather than reading the disk on a list paint. The expanded body is the only place that resolves.</summary>
    private static (int Added, int Removed) PreviewCounts(
        ChangedFile change, Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff)
        => change.FrozenPreview is { Length: > 0 }
            ? ChatChanges.CountChanges(change.FrozenPreview)
            : (0, 0);

    private Control BuildDiffDetail(ChangedFile change, Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff)
    {
        var (diff, state) = resolveDiff(change);
        if (diff is null)
            return EmptyLine(DiffUnavailableKey(change, state));
        return BuildDiffBlock(diff);
    }

    private static string DiffUnavailableKey(ChangedFile change, UndoCopyState state) => state switch
    {
        UndoCopyState.CreatedFile => "InspectorDiffCreatedFile",
        UndoCopyState.EvictedOrSpent => change.Verdict == ChangeVerdict.Unchanged
            ? "InspectorDiffRefusedLine"
            : "InspectorDiffCopyGone",
        _ => "InspectorDiffUnreadable",
    };

    /// <summary>Renders a unified diff line by line, adds and deletes on their own tinted plates. A horizontal
    /// scroller rather than wrapping: code that wraps loses the column alignment a diff is read by.</summary>
    private static Control BuildDiffBlock(string diff)
    {
        var lines = new StackPanel { Spacing = 0 };
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var block = new Border();
            block.Classes.Add("inspector-diff-line");
            var lineClass = DiffLineClass(line);
            if (lineClass.Length > 0) block.Classes.Add(lineClass);
            var text = Themed(new TextBlock
            {
                Text = line.Length == 0 ? " " : line,
                FontFamily = ThemedFont("Hub.Font.Mono"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.NoWrap,
            }, "Hub.TextSecondary");
            block.Child = text;
            lines.Children.Add(block);
        }

        return new ScrollViewer
        {
            Content = new Border { Classes = { "inspector-diff" }, Child = lines },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
            ClipToBounds = true,
        };
    }

    private static string DiffLineClass(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            return "diff-header";
        if (line.StartsWith("@@", StringComparison.Ordinal)) return "diff-hunk";
        if (line.StartsWith('+')) return "diff-add";
        if (line.StartsWith('-')) return "diff-del";
        return "";
    }

    private static string VerdictMark(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Created => "＋",
        ChangeVerdict.Edited => "✎",
        ChangeVerdict.Unchanged => "·",
        _ => "⊘",
    };

    private static string VerdictClass(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Created => "created",
        ChangeVerdict.Edited => "edited",
        ChangeVerdict.Unchanged => "unchanged",
        _ => "refused",
    };

    // ── theming helpers: brushes and fonts bind through DynamicResourceExtension so they follow a theme switch
    //    that happens after this panel is already on screen. ──

    private static T Themed<T>(T control, string brushKey) where T : Control
    {
        if (control is TextBlock text)
            text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brushKey));
        return control;
    }

    private static FontFamily ThemedFont(string key)
        => Application.Current?.TryGetResource(key, null, out var value) == true && value is FontFamily family
            ? family
            : FontFamily.Default;

    // ── check accessors ──

    internal bool PlanTabVisibleForCheck => _planHost.IsVisible;
    internal bool ChangesTabVisibleForCheck => _changesScroll.IsVisible;
    internal int ChangeRowCountForCheck => _changesList.Children.Count;
    internal string FirstChangePathForCheck
        => _changesList.Children.Count > 0
            && _changesList.Children[0] is StackPanel { Children.Count: > 0 } panel
            && panel.Children[0] is Button { Content: Grid grid }
            && grid.Children.Count > 1
            && grid.Children[1] is TextBlock name
            ? name.Text ?? ""
            : "";
}
