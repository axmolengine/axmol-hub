using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>One workspace surface, lifted by the shell's existing inspector host. Files are documents;
/// the browser is independent navigation, never a tab. Runtime states outlive conversation switches.</summary>
internal sealed class WorkspaceView : Grid, IDisposable
{
    private sealed class Document(string path, string title, Control content, Func<bool> dirty)
    {
        internal string Path { get; } = path;
        internal string Title { get; } = title;
        internal Control Content { get; } = content;
        internal Func<bool> Dirty { get; } = dirty;
    }
    private sealed class State(WorkspaceFileTree tree)
    {
        internal WorkspaceFileTree Tree { get; } = tree;
        internal List<Document> Documents { get; } = [];
        internal HashSet<string> Tools { get; } = ["plan", "repo"];
        internal string? Selected { get; set; } = "plan";
        internal bool TreeVisible { get; set; }
        internal bool ManuallyHidden { get; set; }
        internal double TreeWidth { get; set; } = 230;
        internal Func<WorkspaceToolScope> ScopeFor { get; set; } = null!;
    }
    private readonly Dictionary<string, State> _states = new(WorkspaceFileTree.WorkspacePathComparer);
    private readonly InspectorPanel _inspector;
    private readonly Window _owner;
    private readonly Grid _body;
    private readonly ContentControl _content = new();
    private readonly ContentControl _treeHost = new();
    private readonly GridSplitter _splitter;
    private sealed record Tab(string Id, Border Plate, TextBlock Label, MenuItem Entry);
    private const double AddTabGap = 4;
    private readonly List<Tab> _tabs = [];
    private readonly StackPanel _tabRows = new() { Orientation = Orientation.Horizontal, Spacing = 3, ClipToBounds = true };
    private readonly Grid _tabGrid;
    private readonly Button _overflow;
    private readonly MenuFlyout _overflowMenu = new();
    private readonly Button _folder;
    private readonly StackPanel _empty;
    private State? _state;
    private string _key = "";
    private bool _drawer;
    private bool _sizing;
    private bool _closingTabs;
    private enum TabCloseScope { Single, All, Others, Right }
    internal Control TabStrip { get; }
    internal event Action<string>? FileRequested;
    internal event Action<string>? ToolRequested;
    internal event Action? OpenRequested;

    internal WorkspaceView(Window owner, InspectorPanel inspector)
    {
        _owner = owner;
        _inspector = inspector;
        _inspector.SelectToolForCheck = ClickToolForCheck;
        ClipToBounds = true;
        _body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,0,0"), ClipToBounds = true };
        _body.Children.Add(_content);
        _splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
        Grid.SetColumn(_splitter, 1);
        _body.Children.Add(_splitter);
        Grid.SetColumn(_treeHost, 2);
        _body.Children.Add(_treeHost);
        _treeHost.Bind(BackgroundProperty, new DynamicResourceExtension("Hub.Surface"));
        Children.Add(_body);
        _body.ColumnDefinitions[2].PropertyChanged += (_, args) =>
        {
            if (args.Property == ColumnDefinition.WidthProperty && !_sizing && !_drawer && _state is not null)
                _state.TreeWidth = Math.Clamp(_body.ColumnDefinitions[2].Width.Value, 180, 360);
        };
        SizeChanged += (_, _) => ApplyTreeLayout();
        _tabGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            Margin = new Thickness(12, 0, 0, 0), Height = 32, ClipToBounds = true };
        _tabGrid.Children.Add(_tabRows);
        _overflow = new Button { Name = "WorkspaceTabOverflow", Content = HubActionIcon.More(),
            Classes = { "titlebar" }, Margin = default, IsVisible = false, Flyout = _overflowMenu };
        _overflow.Bind(ToolTip.TipProperty, new DynamicResourceExtension("WorkspaceMoreTabs"));
        Grid.SetColumn(_overflow, 1);
        _tabGrid.Children.Add(_overflow);
        var plus = new Button { Name = "WorkspaceAddTab", Content = HubActionIcon.Create("Hub.Icon.Plus"),
            Classes = { "titlebar" }, Margin = new Thickness(AddTabGap, 0, 0, 0) };
        plus.Bind(ToolTip.TipProperty, new DynamicResourceExtension("WorkspaceAddTab"));
        var menu = new MenuFlyout();
        foreach (var (key, caption) in new[] { ("repo", "InspectorTabChanges"), ("plan", "InspectorTabPlan"), ("files", "WorkspaceFiles") })
        {
            var item = new MenuItem();
            item.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension(caption));
            item.Click += (_, _) => { if (key == "files") ShowFiles(); else OpenTool(key); };
            menu.Items.Add(item);
        }
        plus.Flyout = menu;
        Grid.SetColumn(plus, 2);
        _tabGrid.Children.Add(plus);
        _folder = new Button { Name = "WorkspaceFilesToggle", Content = HubActionIcon.Create("Hub.Icon.Folder"),
            Classes = { "viewer-action" }, Width = 28, Height = 28, Padding = default,
            Margin = new Thickness(4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        _folder.Click += (_, _) => { if (_state?.TreeVisible == true) HideFiles(); else ShowFiles(); };
        inspector.SetFileTreeToggle(_folder);
        _tabGrid.SizeChanged += (_, _) => LayoutTabs();
        TabStrip = _tabGrid;
        _empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Spacing = 8, MaxWidth = 240 };
        foreach (var (key, caption, icon) in new[] { ("repo", "InspectorTabChanges", "Hub.Icon.Changes"),
                     ("plan", "InspectorTabPlan", "Hub.Icon.Plan"), ("files", "WorkspaceFiles", "Hub.Icon.Folder") })
        {
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "workspace-entry" } };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(HubActionIcon.Create(icon));
            var label = new TextBlock();
            label.Bind(TextBlock.TextProperty, new DynamicResourceExtension(caption));
            row.Children.Add(label);
            button.Content = row;
            button.Click += (_, _) => { if (key == "files") ShowFiles(); else OpenTool(key); };
            _empty.Children.Add(button);
        }
    }

    internal void SetContext(string? root, Func<WorkspaceToolScope> scopeFor)
    {
        var key = WorkspacePaths.CanonicalRoot(root) ?? "";
        if (_state is not null && WorkspaceFileTree.WorkspacePathComparer.Equals(key, _key))
        {
            _state.ScopeFor = scopeFor;
            return;
        }
        _state?.Tree.SetActive(false);
        _content.Content = null;
        _treeHost.Content = null;
        _key = key;
        if (!_states.TryGetValue(key, out var state))
        {
            State? created = null;
            var tree = new WorkspaceFileTree(root, () => created!.ScopeFor());
            created = new State(tree) { ScopeFor = scopeFor };
            tree.CloseRequested += HideFiles;
            tree.FileRequested += path => FileRequested?.Invoke(path);
            _states[key] = state = created;
        }
        state.ScopeFor = scopeFor;
        _state = state;
        _treeHost.Content = state.Tree;
        Render();
    }

    internal void OpenTool(string tab)
    {
        if (_state is null) return;
        tab = tab == "plan" ? "plan" : "repo";
        _state.Tools.Add(tab);
        _state.Selected = tab;
        Render();
        ToolRequested?.Invoke(tab);
        OpenRequested?.Invoke();
    }

    internal void ShowFiles()
    {
        if (_state is null) return;
        _state.TreeVisible = true;
        _state.ManuallyHidden = false;
        ApplyTreeLayout();
        OpenRequested?.Invoke();
        Dispatcher.UIThread.Post(() => _state?.Tree.FocusFilter());
    }

    internal void HideFiles()
    {
        if (_state is null) return;
        _state.TreeVisible = false;
        _state.ManuallyHidden = true;
        ApplyTreeLayout();
    }

    internal void OpenDocument(string path, string title, Control content, Func<bool>? dirty = null)
    {
        if (_state is null) return;
        var existing = _state.Documents.Find(document => WorkspaceFileTree.WorkspacePathComparer.Equals(document.Path, path));
        if (existing is null)
        {
            existing = new Document(path, title, content, dirty ?? (() => false));
            _state.Documents.Add(existing);
            if (content is CodeEditorView editor) editor.StateChanged += RenderTabs;
        }
        _state.Selected = existing.Path;
        if (!_state.ManuallyHidden && Bounds.Width >= 640) _state.TreeVisible = true;
        _state.Tree.Reveal(path);
        Render();
        if (_drawer && _state.TreeVisible)
        {
            // Choosing a file dismisses a temporary narrow drawer, not the user's persistent close preference.
            _state.TreeVisible = false;
            ApplyTreeLayout();
        }
        OpenRequested?.Invoke();
    }

    internal bool SelectExisting(string path)
    {
        if (_state?.Documents.Find(document => WorkspaceFileTree.WorkspacePathComparer.Equals(document.Path, path)) is not { } document) return false;
        _state.Selected = document.Path;
        _state.Tree.Reveal(document.Path);
        Render();
        OpenRequested?.Invoke();
        return true;
    }

    internal void GoToPosition(string path, int? line, int? column)
    {
        if (_state?.Documents.Find(document => WorkspaceFileTree.WorkspacePathComparer.Equals(document.Path, path))?.Content
            is CodeEditorView editor)
            editor.GoToPosition(line, column);
    }

    private void Render()
    {
        if (_state is null) return;
        _content.Content = null;
        if (_state.Selected is "plan" or "repo")
        {
            _inspector.SelectWorkspaceTool(_state.Selected);
            _content.Content = _inspector;
        }
        else _content.Content = _state.Documents.Find(document => document.Path == _state.Selected)?.Content ?? _empty;
        RenderTabs();
        ApplyTreeLayout();
    }

    private void RenderTabs()
    {
        if (_state is null) return;
        _tabRows.Children.Clear();
        _tabs.Clear();
        var state = _state;
        foreach (var tool in new[] { "plan", "repo" })
        {
            if (!state.Tools.Contains(tool)) continue;
            AddTab(tool, null, tool == "plan" ? "InspectorTabPlan" : "InspectorTabChanges", () => OpenTool(tool),
                () => _ = CloseTabsAsync(state, tool));
        }
        foreach (var document in state.Documents)
            AddTab(document.Path, Path.GetFileName(document.Title), null,
                () => SelectExisting(document.Path), () => _ = CloseTabsAsync(state, document.Path), document.Dirty(), document.Content as CodeEditorView);
        LayoutTabs();
    }

    private void AddTab(string id, string? caption, string? resource, Action select, Action close, bool dirty = false, CodeEditorView? editor = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        var plate = new Border { Child = row, Classes = { "workspace-tab" }, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        plate.Classes.Set("selected", _state?.Selected == id);
        plate.Classes.Set("dirty", dirty);
        var label = new TextBlock { Text = caption, FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        if (resource is not null) label.Bind(TextBlock.TextProperty, new DynamicResourceExtension(resource));
        var button = new Button { Content = label, MaxWidth = 180, Height = 28,
            Classes = { "workspace-tab-select" }, Padding = new Thickness(9, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true };
        if (resource is null) ToolTip.SetTip(button, id);
        var state = _state!;
        ContextMenu TabMenu() => CreateTabMenu(state, id, editor);
        plate.ContextMenu = TabMenu();
        plate.Name = editor is not null ? "WorkspaceDocumentTab" : id == "plan" ? "WorkspacePlanTab" : "WorkspaceChangesTab";
        button.Click += (_, _) => select();
        row.Children.Add(button);
        var closeGlyph = HubActionIcon.Close();
        closeGlyph.Classes.Add("workspace-tab-close-glyph");
        Control closeContent = closeGlyph;
        if (dirty)
        {
            var dot = HubActionIcon.Unsaved();
            dot.Classes.Add("workspace-tab-dirty-dot");
            closeContent = new Grid { Width = 8, Height = 8, Children = { closeGlyph, dot } };
        }
        var x = new Button { Content = closeContent, Width = 20, Height = 20,
            Name = resource is null ? "WorkspaceCloseDocumentTab" : "WorkspaceClose" + (id == "plan" ? "Plan" : "Changes") + "Tab",
            Classes = { "workspace-tab-close" }, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center };
        x.Bind(ToolTip.TipProperty, new DynamicResourceExtension("WorkspaceCloseTab"));
        x.Click += (_, _) => close();
        Grid.SetColumn(x, 1);
        row.Children.Add(x);
        _tabRows.Children.Add(plate);
        var entry = new MenuItem { Tag = id };
        if (resource is not null) entry.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension(resource));
        else { entry.Header = caption; ToolTip.SetTip(entry, id); }
        entry.ContextMenu = TabMenu();
        entry.Click += (_, _) => select();
        _tabs.Add(new Tab(id, plate, label, entry));
        label.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty) Dispatcher.UIThread.Post(LayoutTabs);
        };
    }

    private void LayoutTabs()
    {
        // Measure captions independently of hidden plates. Resizing or changing language must be able to
        // restore a hidden tab without first showing every tab and causing an endless layout invalidation.
        var widths = _tabs.Select(tab =>
        {
            tab.Label.Measure(new Size(double.PositiveInfinity, 28));
            return Math.Min(180, Math.Ceiling(tab.Label.DesiredSize.Width) + 9) + 22;
        }).ToArray();
        var available = Math.Max(0, _tabGrid.Bounds.Width - 28 - AddTabGap);
        var total = widths.Sum() + Math.Max(0, _tabs.Count - 1) * _tabRows.Spacing;
        var overflow = total > available;
        var remaining = Math.Max(0, available - (overflow ? 28 : 0));
        var visible = new HashSet<int>();
        var selected = _tabs.FindIndex(tab => tab.Id == _state?.Selected);
        if (selected >= 0 && remaining >= 32)
        {
            visible.Add(selected);
            widths[selected] = Math.Min(widths[selected], remaining);
            remaining -= widths[selected] + _tabRows.Spacing;
        }
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (i == selected || widths[i] > remaining) continue;
            visible.Add(i);
            remaining -= widths[i] + _tabRows.Spacing;
        }
        _overflowMenu.Items.Clear();
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            tab.Plate.IsVisible = visible.Contains(i);
            tab.Plate.Width = widths[i];
            if (!tab.Plate.IsVisible) _overflowMenu.Items.Add(tab.Entry);
        }
        _overflow.IsVisible = _overflowMenu.Items.Count > 0;
    }

    private static string[] TabIds(State state)
        => new[] { "plan", "repo" }.Where(state.Tools.Contains)
            .Concat(state.Documents.Select(document => document.Path)).ToArray();

    private ContextMenu CreateTabMenu(State state, string id, CodeEditorView? editor)
    {
        var menu = new ContextMenu();
        if (editor is not null) editor.AddFileReveal(menu);
        else if (id == "plan") FileRevealMenu.Add(menu, _owner, () => _inspector.PlanFilePathFor?.Invoke());
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        foreach (var (scope, tag, caption) in new[]
                 {
                     (TabCloseScope.Single, "close", "WorkspaceCloseTab"),
                     (TabCloseScope.All, "closeAll", "WorkspaceCloseAllTabs"),
                     (TabCloseScope.Others, "closeOthers", "WorkspaceCloseOtherTabs"),
                     (TabCloseScope.Right, "closeRight", "WorkspaceCloseTabsToRight"),
                 })
        {
            var item = new MenuItem { Tag = tag };
            item.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension(caption));
            item.Click += (_, _) => _ = CloseTabsAsync(state, id, scope);
            menu.Items.Add(item);
            menu.Opened += (_, _) => item.IsEnabled = !_closingTabs && ReferenceEquals(state, _state)
                && CloseTargets(state, id, scope).Length > 0;
        }
        return menu;
    }

    private static string[] CloseTargets(State state, string id, TabCloseScope scope)
    {
        var ids = TabIds(state);
        var index = Array.IndexOf(ids, id);
        if (index < 0) return [];
        return scope switch
        {
            TabCloseScope.All => ids,
            TabCloseScope.Others => ids.Where(target => target != id).ToArray(),
            TabCloseScope.Right => ids.Skip(index + 1).ToArray(),
            _ => [id],
        };
    }

    private async Task CloseTabsAsync(State state, string id, TabCloseScope scope = TabCloseScope.Single, bool discard = false)
    {
        if (_closingTabs || !ReferenceEquals(state, _state)) return;
        // Snapshot both the workspace and document instances before the first confirmation can yield.
        var targets = CloseTargets(state, id, scope)
            .Select(target => (Id: target, Document: state.Documents.Find(document => document.Path == target))).ToArray();
        _closingTabs = true;
        try
        {
            foreach (var target in targets)
            {
                if (target.Document is { } document)
                {
                    if (!state.Documents.Contains(document)) continue;
                    if (!await CloseDocumentAsync(state, document, discard)) break;
                }
                else
                {
                    state.Tools.Remove(target.Id);
                    if (state.Selected == target.Id) ChooseRemaining(state);
                }
                if (ReferenceEquals(state, _state)) Render();
            }
        }
        finally { _closingTabs = false; }
    }

    private static void ChooseRemaining(State state, int documentIndex = 0)
    {
        state.Selected = state.Documents.Count > 0
            ? state.Documents[Math.Clamp(documentIndex, 0, state.Documents.Count - 1)].Path
            : state.Tools.Contains("repo") ? "repo" : state.Tools.Contains("plan") ? "plan" : null;
    }

    private async Task<bool> CloseDocumentAsync(State state, Document document, bool discard)
    {
        if (document.Dirty() && !discard)
        {
            var answer = await HubDialog.ShowAsync(_owner, HubStrings.Get("CodeUnsavedTitle"),
                document.Path + "\n\n" + HubStrings.Get("CodeUnsavedClose"), HubDialogButtons.SaveDiscardCancel);
            if (answer == HubDialogResult.Cancel) return false;
            if (answer == HubDialogResult.Save && (document.Content is not CodeEditorView editor || !editor.Save()))
            {
                if (ReferenceEquals(state, _state)) { state.Selected = document.Path; Render(); }
                return false;
            }
        }
        var index = state.Documents.IndexOf(document);
        if (index < 0) return true;
        state.Documents.RemoveAt(index);
        if (document.Content is CodeEditorView closedEditor) closedEditor.StateChanged -= RenderTabs;
        if (state.Selected == document.Path) ChooseRemaining(state, index);
        return true;
    }

    internal void CloseSelected(bool discard = false)
    {
        if (_state is { Selected: { } id } state && state.Documents.Any(document => document.Path == id))
            _ = CloseTabsAsync(state, id, discard: discard);
    }

    private void ApplyTreeLayout()
    {
        if (_state is null) return;
        _sizing = true;
        try
        {
            _drawer = Bounds.Width < 640;
            var visible = _state.TreeVisible;
            _folder.Classes.Set("selected", visible);
            _folder.Bind(ToolTip.TipProperty, new DynamicResourceExtension(visible ? "WorkspaceCloseFiles" : "WorkspaceShowFiles"));
            _treeHost.IsVisible = visible;
            _state.Tree.SetActive(visible && IsEffectivelyVisible);
            _splitter.IsVisible = visible && !_drawer;
            Grid.SetColumn(_treeHost, _drawer ? 0 : 2);
            Grid.SetColumnSpan(_treeHost, _drawer ? 3 : 1);
            _treeHost.HorizontalAlignment = _drawer ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            _treeHost.Width = _drawer ? Math.Min(300, Math.Max(0, Bounds.Width)) : double.NaN;
            _body.ColumnDefinitions[1].Width = new GridLength(visible && !_drawer ? 4 : 0);
            _body.ColumnDefinitions[2].Width = new GridLength(visible && !_drawer ? _state.TreeWidth : 0);
            _content.IsEnabled = !visible || !_drawer;
        }
        finally { _sizing = false; }
    }

    internal void ClickToolForCheck(string tool)
    {
        var index = tool == "plan" ? 0 : (_state?.Tools.Contains("plan") == true ? 1 : 0);
        if (_tabRows.Children.ElementAtOrDefault(index) is Border { Child: Grid row }
            && row.Children.FirstOrDefault() is Button button)
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
    internal ContextMenu? TabMenuForCheck(string id) => _tabs.Find(tab => tab.Id == id)?.Plate.ContextMenu;
    internal void RefreshLayout() => ApplyTreeLayout();
    internal void SetExpanded(bool expanded) => _inspector.SetExpanded(expanded);

    internal bool HasDirtyBuffers => _states.Values.Any(state => state.Documents.Any(document => document.Dirty()));
    internal bool SaveAll()
    {
        foreach (var document in _states.Values.SelectMany(state => state.Documents).Where(document => document.Dirty()))
            if (document.Content is not CodeEditorView editor || !editor.Save()) return false;
        return true;
    }
    internal void DiscardAll()
    {
        foreach (var state in _states.Values)
            state.Documents.RemoveAll(document => document.Dirty());
        if (_state is not null && !_state.Documents.Any(document => document.Path == _state.Selected)
            && _state.Selected is not ("plan" or "repo")) ChooseRemaining(_state);
        Render();
    }
    public void Dispose() { foreach (var state in _states.Values) state.Tree.Dispose(); }
    internal bool DocumentSelectedForCheck => _state?.Documents.Any(document => document.Path == _state.Selected) == true;
    internal Control? SelectedContentForCheck => _content.Content as Control;
    internal string SelectedTitleForCheck => _state?.Documents.Find(document => document.Path == _state.Selected)?.Title ?? "";
    internal WorkspaceFileTree TreeForCheck => _state!.Tree;
    internal Button FolderForCheck => _folder;
    internal bool TreeVisibleForCheck => _state?.TreeVisible == true;
    internal bool DrawerForCheck => _drawer;
    internal bool TabLayoutForCheck(MainWindow shell)
    {
        var slot = shell.InspectorTabSlotBoundsForCheck;
        var strip = shell.InspectorTabsBoundsForCheck;
        var edge = shell.InspectorBorderBoundsForCheck;
        var actions = shell.InspectorExpandToggleVisibleForCheck
            ? shell.InspectorExpandToggleBoundsForCheck : shell.InspectorToggleBoundsForCheck;
        var plus = _tabGrid.Children.OfType<Button>().Single(button => button.Name == "WorkspaceAddTab");
        var visible = _tabs.Where(tab => tab.Plate.IsVisible).ToArray();
        var hidden = _tabs.Where(tab => !tab.Plate.IsVisible).ToArray();
        var capacity = _tabGrid.Bounds.Width - 28 - AddTabGap - (_overflow.IsVisible ? 28 : 0);
        return Math.Abs(slot.X - (shell.InspectorExpandedForCheck ? shell.ClientBoundsForCheck.X : edge.X)) < 0.5
            && Math.Abs(strip.X - slot.X - 12) < 0.5 && strip.Right <= actions.Left + 0.5
            && Math.Abs(plus.Bounds.Width - plus.Bounds.Height) < 0.5
            && Math.Abs(_folder.Bounds.Width - _folder.Bounds.Height) < 0.5
            && (capacity < 32 || visible.Any(tab => tab.Id == _state?.Selected))
            && visible.All(tab => tab.Plate.Bounds.Right <= capacity + 0.5)
            && _overflow.IsVisible == (hidden.Length > 0)
            && _overflowMenu.Items.OfType<MenuItem>().Select(item => item.Tag as string)
                .SequenceEqual(hidden.Select(tab => tab.Id));
    }

    internal void SelectOverflowForCheck(string id)
        => _overflowMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == id)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    internal void CloseToolForCheck(string tool)
    {
        if (_state is { } state) _ = CloseTabsAsync(state, tool);
    }
}
