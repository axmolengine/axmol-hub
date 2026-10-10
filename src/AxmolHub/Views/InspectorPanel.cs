using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Markdown.Avalonia;
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

    /// <summary>Raised by the expand arrow in the header. The same division as <see cref="CloseRequested"/>:
    /// the pane asks, the shell decides whether that means an overlay over the window or nothing on this page.</summary>
    public event Action? ExpandRequested;

    /// <summary>Raised when the repository tab is entered, and by its own ⟳. The panel never reads git itself:
    /// it has no session, no log and no thread to wait on, and a pane that goes and does I/O is a pane a check
    /// cannot drive from a fixture.</summary>
    public event Action? RepoRefreshRequested;

    private readonly Button _planTab;
    private readonly Button _changesTab;
    private readonly Button _repoTab;
    private readonly Button _expandButton;
    private readonly Path _expandIcon;
    private readonly ContentControl _planHost;
    private readonly ScrollViewer _changesScroll;
    private readonly StackPanel _changesList;
    private readonly ScrollViewer _repoScroll;
    private readonly StackPanel _repoBody;
    private readonly Grid _body;
    private bool _expanded;

    /// <summary>Paths a person had opened on the changes tab. A repaint clears the list to draw the newest
    /// snapshot, and a pane that folds back everything the reader opened on every streamed token is a pane that
    /// cannot be read while the run is still going — so the opened set outlives the rows it was drawn from.</summary>
    private readonly HashSet<string> _openedChanges = new(StringComparer.Ordinal);

    public InspectorPanel()
    {
        ClipToBounds = true;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        // ── Header: three tabs, and the two ways out. The tabs are the only navigation, so they read as text
        //    buttons whose selected one carries an underline plate rather than as a chrome-heavy TabControl. ──
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"), Margin = new Thickness(12, 10, 8, 8) };
        _planTab = TabButton("InspectorTabPlan");
        _changesTab = TabButton("InspectorTabChanges");
        _repoTab = TabButton("InspectorTabRepo");
        _planTab.Click += (_, _) => SelectTab("plan");
        _changesTab.Click += (_, _) => SelectTab("changes");
        // Asking for a read is the panel's business; doing one is not. The caller owns git, the session and the
        // thread this lands on, so the tab only says that the person wanted the repository looked at again.
        _repoTab.Click += (_, _) =>
        {
            SelectTab("repo");
            RepoRefreshRequested?.Invoke();
        };
        header.Children.Add(_planTab);
        Grid.SetColumn(_changesTab, 1);
        _changesTab.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(_changesTab);
        Grid.SetColumn(_repoTab, 2);
        _repoTab.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(_repoTab);

        // The column is clamped to 520 because the chat column has a floor to keep, which is the right room for a
        // diff and the wrong room for a plan a person has to read end to end. So the pane can be lifted out of the
        // column and given the window.
        _expandButton = ActionButton("InspectorExpand", "Hub.Icon.Expand");
        _expandButton.Name = "InspectorExpandButton";
        _expandIcon = (Path)_expandButton.Content!;
        _expandButton.Click += (_, _) => ExpandRequested?.Invoke();
        Grid.SetColumn(_expandButton, 4);
        header.Children.Add(_expandButton);

        var close = ActionButton("InspectorClose", "Hub.Icon.Close");
        close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(close, 5);
        header.Children.Add(close);
        root.Children.Add(header);

        // ── Body: the three tab surfaces stacked, only one visible. All live in the tree so switching tabs does
        //    not rebuild them, and a check can read any of them without selecting it first. ──
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

        _repoBody = new StackPanel { Spacing = 6 };
        _repoScroll = new ScrollViewer
        {
            Content = _repoBody,
            Margin = new Thickness(12, 0, 6, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _repoScroll.IsVisible = false;
        _body.Children.Add(_repoScroll);

        root.Children.Add(_body);
        Content = root;
        SelectTab("plan");
    }

    private static Button TabButton(string textKey)
    {
        var button = new Button { Classes = { "inspector-tab" }, Content = HubStrings.Get(textKey), Tag = textKey };
        return button;
    }

    /// <summary>The header's two glyph buttons. Stroked rather than filled like the picture viewer's row, so the
    /// × and the expand arrow weigh the same as each other and less than the tabs beside them.</summary>
    private static Button ActionButton(string textKey, string geometryKey)
    {
        var icon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
        };
        icon.Bind(Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        var button = new Button { Classes = { "viewer-action" }, Tag = textKey, Content = icon };
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        return button;
    }

    /// <summary>Told by the shell which of its two homes the pane is in, because the two are not the same
    /// reading. The arrow has to point back the way it came once it has been followed, and a plan laid across a
    /// whole window needs the chat column's line length rather than the window's — a document that runs 1200px
    /// per line is not faster to read, it is a scan for the end of each sentence. The diff keeps every pixel:
    /// side-by-side changes are exactly what the room is for.</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        var key = expanded ? "InspectorRestore" : "InspectorExpand";
        _expandIcon.Bind(Path.DataProperty, new DynamicResourceExtension(
            expanded ? "Hub.Icon.Restore" : "Hub.Icon.Expand"));
        _expandButton.Tag = key;
        ToolTip.SetTip(_expandButton, HubStrings.Get(key));
        _planHost.MaxWidth = expanded ? HubMetrics.ColumnMaxWidth : double.PositiveInfinity;
        _planHost.HorizontalAlignment = expanded ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    }

    /// <summary>Shows one tab surface and marks its button. All three stay in the tree, so painting a new plan
    /// does not rebuild a diff somebody had opened, and a check can read a tab it never selected. Anything that
    /// is not one of the two action tabs lands on the plan, which is the pane's rest state.</summary>
    private void SelectTab(string tab)
    {
        var changes = string.Equals(tab, "changes", StringComparison.Ordinal);
        var repo = string.Equals(tab, "repo", StringComparison.Ordinal);
        var plan = !changes && !repo;
        _planTab.Classes.Set("selected", plan);
        _changesTab.Classes.Set("selected", changes);
        _repoTab.Classes.Set("selected", repo);
        _planHost.IsVisible = plan;
        _changesScroll.IsVisible = changes;
        _repoScroll.IsVisible = repo;
    }

    /// <summary>Paints the three tabs from a snapshot and lands on the asked-for one. The diff resolver is lazy
    /// per file: a session of twelve writes must not read twelve files to show a list, only the one a person
    /// opens. The repository state arrives already read — it is the one input here that cost a process, so the
    /// caller owns when to take it and this only lays out what it got.</summary>
    public void Reload(
        string? planMarkdown,
        IReadOnlyList<ChangedFile> changes,
        Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff,
        GitRepositoryState? repository,
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

        ReloadRepository(repository, changes);
        SelectTab(tab);
    }

    private void ReloadRepository(GitRepositoryState? repository, IReadOnlyList<ChangedFile> changes)
    {
        _repoBody.Children.Clear();
        // The caveat is the tab's first line and it is never conditional. This list is the repository's, not the
        // session's, and a reader who assumes otherwise will mis-credit a week of their own work to the assistant
        // — which is exactly the confusion the separate tab exists to avoid, so it has to be said even when the
        // repository is empty and the sentence looks like decoration.
        _repoBody.Children.Add(Note("InspectorRepoCaveat"));

        if (repository is null)
        {
            _repoBody.Children.Add(EmptyLine("InspectorRepoReading"));
            _repoBody.Children.Add(RepositoryHead(null));
            return;
        }

        _repoBody.Children.Add(RepositoryHead(repository));
        if (!repository.IsReadable)
        {
            _repoBody.Children.Add(EmptyLine(RepositoryWhyKey(repository.Outcome)));
            return;
        }
        if (repository.Entries.Count == 0)
        {
            _repoBody.Children.Add(EmptyLine("InspectorRepoEmpty"));
            return;
        }
        foreach (var entry in repository.Entries)
            _repoBody.Children.Add(BuildRepositoryRow(entry, repository, TouchedBySession(repository, changes)));
        if (repository.Truncated)
        {
            _repoBody.Children.Add(EmptyLine(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorRepoTruncatedFormat"), repository.Entries.Count)));
        }
    }

    /// <summary>Why the tab cannot answer, in one of six sentences. Kept as a table rather than a default arm so
    /// a new outcome has to be given words here before it can show an empty list somewhere.</summary>
    private static string RepositoryWhyKey(GitReadOutcome outcome) => outcome switch
    {
        GitReadOutcome.NoWorkspace => "InspectorRepoNoWorkspace",
        GitReadOutcome.NotARepository => "InspectorRepoNotGit",
        GitReadOutcome.GitMissing => "InspectorRepoGitMissing",
        GitReadOutcome.UnsafeRepository => "InspectorRepoUnsafe",
        GitReadOutcome.Failed => "InspectorRepoFailed",
        _ => "InspectorRepoEmpty",
    };

    /// <summary>The branch line and the one way to ask for the read again. The glyph is git's own idea — a
    /// branch — and the count only appears when the session is off its upstream, because "0 ahead · 0 behind"
    /// would be a fact nobody needed telling.</summary>
    private Control RepositoryHead(GitRepositoryState? state)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StringBuilder();
        if (state is { IsReadable: true } readable)
        {
            text.Append(string.IsNullOrEmpty(readable.Branch) ? "—" : readable.Branch);
            if (readable.Ahead > 0 || readable.Behind > 0)
            {
                text.Append(" · ").Append(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("InspectorRepoAheadBehindFormat"), readable.Ahead, readable.Behind));
            }
        }
        else
        {
            text.Append("—");
        }
        var head = Themed(new TextBlock
        {
            Text = text.ToString(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-note" },
        }, "Hub.TextSecondary");
        ToolTip.SetTip(head, HubStrings.Get("InspectorTabRepo"));
        row.Children.Add(head);

        var icon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
        };
        icon.Bind(Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Refresh"));
        var refresh = new Button { Classes = { "viewer-action" }, Tag = "InspectorRepoRefresh", Content = icon };
        ToolTip.SetTip(refresh, HubStrings.Get("InspectorRepoRefresh"));
        refresh.Click += (_, _) => RepoRefreshRequested?.Invoke();
        Grid.SetColumn(refresh, 1);
        row.Children.Add(refresh);
        return row;
    }

    /// <summary>Which of these dirty paths this conversation also wrote. The two lists use different anchors —
    /// git speaks from the repository root, <see cref="ChatChanges"/> from the session's folder — so a workspace
    /// that is a subfolder matches by suffix rather than never matching at all. The mark is the one place the two
    /// tabs are allowed to talk to each other, and it adds no claim: it says a file is in both lists.</summary>
    private static HashSet<string> TouchedBySession(GitRepositoryState state, IReadOnlyList<ChangedFile> changes)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var relative = change.RelativePath.Replace('\\', '/');
            foreach (var entry in state.Entries)
            {
                if (string.Equals(entry.RelativePath, relative, StringComparison.Ordinal)
                    || entry.RelativePath.EndsWith("/" + relative, StringComparison.Ordinal))
                    touched.Add(entry.RelativePath);
            }
        }
        return touched;
    }

    private Control BuildRepositoryRow(GitStatusEntry entry, GitRepositoryState state, HashSet<string> touched)
    {
        var container = new StackPanel { Spacing = 0, Classes = { "inspector-change" } };
        var header = new Button { Classes = { "inspector-change-header" }, Tag = entry.RelativePath };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        // git's own two columns, not Hub's four verdicts: this is the repository's state and the letters are what
        // a developer reads a status line in. Monospace keeps the pair the width it is in `git status` itself.
        var mark = new TextBlock
        {
            Text = entry.Mark,
            Classes = { "inspector-verdict" },
            FontFamily = ThemedFont("Hub.Font.Mono"),
        };
        mark.Classes.Add(entry.Untracked ? "created" : "edited");
        grid.Children.Add(mark);

        var nameColumn = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        nameColumn.Children.Add(Themed(new TextBlock
        {
            Text = entry.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "inspector-path" },
        }, "Hub.TextPrimary"));
        if (touched.Contains(entry.RelativePath))
        {
            nameColumn.Children.Add(Themed(new TextBlock
            {
                Text = HubStrings.Get("InspectorRepoSessionAlsoTouched"),
                TextWrapping = TextWrapping.Wrap,
                Classes = { "inspector-note" },
            }, "Hub.TextTertiary"));
        }
        Grid.SetColumn(nameColumn, 1);
        nameColumn.Margin = new Thickness(6, 0, 6, 0);
        grid.Children.Add(nameColumn);

        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        header.Click += (_, _) =>
        {
            if (!detail.IsVisible && detail.Content is null)
            {
                var diff = GitRepository.DiffFor(state, entry);
                detail.Content = diff is { Length: > 0 }
                    ? BuildDiffBlock(diff)
                    : EmptyLine("InspectorRepoNoDiff");
            }
            detail.IsVisible = !detail.IsVisible;
            header.Classes.Set("expanded", detail.IsVisible);
        };

        container.Children.Add(header);
        container.Children.Add(detail);
        return container;
    }

    /// <summary>A muted line of prose in the body. Used twice (the caveat and the shared-path mark), so it is one
    /// style rather than a hand-placed size in two spots.</summary>
    private static Control Note(string textKey) => Themed(new TextBlock
    {
        Text = HubStrings.Get(textKey),
        TextWrapping = TextWrapping.Wrap,
        Classes = { "inspector-note" },
    }, "Hub.TextTertiary");

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
        // Re-opened rather than rebuilt: a repaint of the same path carries the reader's own expansion forward,
        // so a run that keeps writing does not fold away the file they are in the middle of looking at.
        if (_openedChanges.Contains(change.RelativePath))
        {
            detail.Content = BuildDiffDetail(change, resolveDiff);
            detail.IsVisible = true;
            header.Classes.Add("expanded");
        }
        header.Click += (_, _) =>
        {
            if (!detail.IsVisible && detail.Content is null)
                detail.Content = BuildDiffDetail(change, resolveDiff);
            detail.IsVisible = !detail.IsVisible;
            header.Classes.Set("expanded", detail.IsVisible);
            if (detail.IsVisible) _openedChanges.Add(change.RelativePath);
            else _openedChanges.Remove(change.RelativePath);
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

    /// <summary>The markdown the plan tab is holding, read off the rendered viewer's own <c>Tag</c>. A check can
    /// therefore prove *which* plan a card click landed on, not merely that some plan is on screen.</summary>
    internal string PlanMarkdownForCheck
        => _planHost.Content is MarkdownScrollViewer viewer ? viewer.Tag as string ?? "" : "";

    internal bool ExpandedForCheck => _expanded;
    internal string ExpandButtonTagForCheck => _expandButton.Tag as string ?? "";
    internal double PlanHostMaxWidthForCheck => _planHost.MaxWidth;

    /// <summary>Which arrow is actually painted. The tag says which tooltip is wired; this says whether the
    /// rebind that swaps the glyph took effect — a swap that reached only the text would leave a button pointing
    /// the wrong way out of a surface that has just covered the whole window.</summary>
    internal bool ExpandGlyphIsForCheck(string geometryKey)
        => Application.Current?.TryGetResource(geometryKey, null, out var value) == true
           && value is Geometry geometry
           && ReferenceEquals(_expandIcon.Data, geometry);

    /// <summary>Presses the header's own arrow, so a check drives the wiring rather than the shell method the
    /// arrow happens to end up calling.</summary>
    internal void ClickExpandForCheck()
        => _expandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal int ChangeRowCountForCheck => _changesList.Children.Count;
    internal string FirstChangePathForCheck
        => _changesList.Children.Count > 0
            && _changesList.Children[0] is StackPanel { Children.Count: > 0 } panel
            && panel.Children[0] is Button { Content: Grid grid }
            && grid.Children.Count > 1
            && grid.Children[1] is TextBlock name
            ? name.Text ?? ""
            : "";

    /// <summary>Which file rows the reader had opened, as the panel keeps them across repaints. A check drives
    /// this by clicking a row rather than by setting the field, so what it asserts is the wiring.</summary>
    internal int OpenChangeCountForCheck => _openedChanges.Count;

    /// <summary>Every string in one change row, joined: the verdict mark, the path, the counts, and once opened,
    /// the diff lines themselves — which is how a check tells the frozen preview from a resolved one.</summary>
    internal string ChangeRowTextForCheck(int index)
        => ChangeRows().ElementAtOrDefault(index) is { } row
            ? string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? ""))
            : "";

    internal bool ChangeRowExpandedForCheck(int index)
        => ChangeRows().ElementAtOrDefault(index) is { } row
           && row.GetLogicalDescendants().OfType<ContentControl>().FirstOrDefault()?.IsVisible == true;

    /// <summary>Presses a change row's header, so the lazy per-file diff is reached the way a reader reaches it
    /// and the resolver can be proven wired to the click rather than run for the whole list up front.</summary>
    internal void ClickChangeRowForCheck(int index)
    {
        if (ChangeRows().ElementAtOrDefault(index) is not { } row) return;
        row.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("inspector-change-header"))
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private List<Control> ChangeRows()
        => _changesList.Children.OfType<StackPanel>()
            .Where(panel => panel.Classes.Contains("inspector-change"))
            .Cast<Control>()
            .ToList();

    // ── the repository tab, as a check reads it ──

    internal bool RepoTabVisibleForCheck => _repoScroll.IsVisible;

    /// <summary>Every line in the body. The caveat and the head row are always there, so a list of N entries is
    /// N+2 — plus one more when the read said it was cut, and one more again when it said why it could not read.</summary>
    internal int RepoBodyCountForCheck => _repoBody.Children.Count;

    /// <summary>The head line's text: the branch, and the ahead/behind pair only when it is off its upstream.</summary>
    internal string RepoHeadlineForCheck => _repoBody.Children.OfType<Grid>()
        .SelectMany(grid => grid.Children).OfType<TextBlock>()
        .FirstOrDefault(block => block.Classes.Contains("inspector-note"))?.Text ?? "";

    /// <summary>The caveat, read apart from the why-sentences: it is the tab's standing first line, present in
    /// every state, while a why-sentence is what the tab ends on instead of a list.</summary>
    internal string RepoCaveatForCheck => _repoBody.Children.OfType<TextBlock>()
        .FirstOrDefault(block => block.Classes.Contains("inspector-note"))?.Text ?? "";

    /// <summary>The sentences the tab ends on instead of a list, joined. Each outcome owns its own string, and
    /// this is how that is proven rather than inferred from a row count.</summary>
    internal string RepoNoticeForCheck => string.Join(" ~ ", _repoBody.GetLogicalDescendants().OfType<TextBlock>()
        .Where(block => block.Classes.Contains("inspector-empty"))
        .Select(block => block.Text ?? ""));

    internal string RepoFirstPathForCheck => _repoBody.GetLogicalDescendants().OfType<TextBlock>()
        .FirstOrDefault(block => block.Classes.Contains("inspector-path"))?.Text ?? "";

    /// <summary>Every string in one row, joined: the two status letters, the path, and the mark when this
    /// conversation also wrote it. Read as text because that is what the reader sees, and a fixture state makes
    /// the whole tab assertable without a repository behind it.</summary>
    internal string RepoRowTextForCheck(int index)
        => RepositoryRows().ElementAtOrDefault(index) is { } row
            ? string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? ""))
            : "";

    internal int RepoRowCountForCheck => RepositoryRows().Count;

    internal bool RepoRowExpandedForCheck(int index)
        => RepositoryRows().ElementAtOrDefault(index) is { } row
           && row.GetLogicalDescendants().OfType<ContentControl>()
               .FirstOrDefault(control => control != row)?.IsVisible == true;

    /// <summary>Presses a row's own header button, which is the path a person takes and the only way to prove the
    /// lazy per-file diff is wired to the click rather than painted up front.</summary>
    internal void ClickRepoRowForCheck(int index)
    {
        if (RepositoryRows().ElementAtOrDefault(index) is not { } row) return;
        row.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("inspector-change-header"))
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Presses the ⟳ through its own button, so a check drives the event the panel raises rather than
    /// calling the shell method the button happens to end up in.</summary>
    internal void ClickRepoRefreshForCheck()
    {
        _repoBody.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag as string == "InspectorRepoRefresh")
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private List<Control> RepositoryRows()
        => _repoBody.Children.OfType<StackPanel>()
            .Where(panel => panel.Classes.Contains("inspector-change"))
            .Cast<Control>()
            .ToList();
}
