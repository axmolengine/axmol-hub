using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>The same browser is docked or drawn as a drawer. Hiding it stops observation, not its browse state.
/// Only the root and explicitly expanded directories are listed and watched.</summary>
internal sealed class WorkspaceFileTree : Grid, IDisposable
{
    private readonly Func<WorkspaceToolScope> _scopeFor;
    private readonly string? _root;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly Dictionary<string, WorkspaceDirectoryListing> _directories = new(WorkspacePathComparer);
    private readonly HashSet<string> _expanded = new(WorkspacePathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(WorkspacePathComparer);
    private readonly DispatcherTimer _refreshTimer;
    private readonly TextBox _filter;
    private readonly Button _close;
    private string? _selected;
    private bool _active;
    private int _generation;

    internal static StringComparer WorkspacePathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal event Action? CloseRequested;
    internal event Action<string>? FileRequested;

    internal WorkspaceFileTree(string? root, Func<WorkspaceToolScope> scopeFor)
    {
        _root = root;
        _scopeFor = scopeFor;
        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        ClipToBounds = true;
        Bind(BackgroundProperty, new DynamicResourceExtension("Hub.Surface"));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(8) };
        var name = new TextBlock
        {
            Text = root is { Length: > 0 } ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) : HubStrings.Get("WorkspaceFiles"),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(name, root);
        header.Children.Add(name);
        var refresh = ActionButton("WorkspaceRefreshFiles", "Hub.Icon.Refresh");
        refresh.Click += (_, _) => _ = RefreshAsync();
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        _close = ActionButton("WorkspaceCloseFiles", "Hub.Icon.Close");
        _close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(_close, 2);
        header.Children.Add(_close);
        Children.Add(header);
        _filter = new TextBox { Name = "WorkspaceFileFilter", Margin = default, Height = 28, MinHeight = 0,
            FontSize = 12, Padding = new Thickness(26, 3, 8, 3), VerticalContentAlignment = VerticalAlignment.Center };
        _filter.Bind(TextBox.PlaceholderTextProperty, new DynamicResourceExtension("WorkspaceFilterFiles"));
        _filter.TextChanged += (_, _) => RenderRows();
        var search = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        search.Children.Add(_filter);
        var searchIcon = HubActionIcon.Create("Hub.Icon.Search");
        searchIcon.Width = searchIcon.Height = 12;
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        searchIcon.Margin = new Thickness(8, 0, 0, 0);
        search.Children.Add(searchIcon);
        Grid.SetRow(search, 1);
        Children.Add(search);
        var scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 2);
        Children.Add(scroll);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); _ = RefreshAsync(); };
    }

    private static Button ActionButton(string tip, string icon)
    {
        var glyph = icon == "Hub.Icon.Close" ? HubActionIcon.Close() : HubActionIcon.Create(icon);
        // The close cross is inked in 8 units; a 13-unit ring beside it reads as the larger of the pair.
        if (glyph is Avalonia.Controls.Shapes.Path ring) ring.Width = ring.Height = 10;
        var button = new Button { Name = tip, Content = glyph,
            Width = 28, Height = 28, Margin = default, Classes = { "viewer-action" } };
        button.Bind(ToolTip.TipProperty, new DynamicResourceExtension(tip));
        return button;
    }

    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) _ = RefreshAsync();
        else
        {
            ++_generation;
            _refreshTimer.Stop();
            StopWatchers();
        }
    }

    internal void FocusFilter() => _filter.Focus();

    internal void Reveal(string path)
    {
        _selected = path;
        if (_root is { Length: > 0 })
        {
            var relative = Path.GetRelativePath(_root, path);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                var parent = Path.GetDirectoryName(path);
                while (parent is not null && !WorkspacePathComparer.Equals(parent, _root))
                {
                    _expanded.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
            }
        }
        RenderRows();
        if (_active) _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (!_active) return;
        var generation = ++_generation;
        var expanded = _expanded.ToHashSet(WorkspacePathComparer);
        var scope = _scopeFor();
        // Observe before enumerating. A cached listing can already contain the selected file on reopen,
        // and writes made while the new asynchronous read is in flight must not fall into a watch gap.
        SyncWatchers(_root is { Length: > 0 } ? expanded.Append(_root) : []);
        if (_directories.Count == 0) Notice("WorkspaceReadingFiles");
        var listings = await Task.Run(() =>
        {
            var result = new Dictionary<string, WorkspaceDirectoryListing>(WorkspacePathComparer);
            if (_root is not { Length: > 0 }) return result;
            var pending = new Stack<string>();
            pending.Push(_root);
            while (pending.TryPop(out var directory))
            {
                var listing = WorkspaceDirectoryReader.Read(scope, directory);
                result[directory] = listing;
                foreach (var child in listing.Entries)
                    if (child.IsDirectory && expanded.Contains(child.Path)) pending.Push(child.Path);
            }
            return result;
        });
        if (!_active || generation != _generation) return;
        _directories.Clear();
        foreach (var listing in listings) _directories.Add(listing.Key, listing.Value);
        RenderRows();
        SyncWatchers(listings.Where(item => item.Value.Error is null).Select(item => item.Key));
    }

    private void SyncWatchers(IEnumerable<string> directories)
    {
        var scope = _scopeFor();
        var allowed = directories.Where(directory =>
            WorkspacePaths.ResolveDirectory(scope.Sandbox, directory, scope.Guards).IsAllowed).ToHashSet(WorkspacePathComparer);
        foreach (var obsolete in _watchers.Keys.Where(directory => !allowed.Contains(directory)).ToArray())
        {
            _watchers[obsolete].Dispose();
            _watchers.Remove(obsolete);
        }
        foreach (var directory in allowed)
        {
            if (_watchers.ContainsKey(directory)) continue;
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += (_, _) => QueueRefresh();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(directory, watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => QueueRefresh();
    private void QueueRefresh() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    });

    private void RenderRows()
    {
        _rows.Children.Clear();
        if (_root is not { Length: > 0 }) { Notice("WorkspaceNoRoot"); return; }
        if (!_directories.ContainsKey(_root)) return;
        AddDirectory(_root, 0);
        if (_rows.Children.Count == 0) Notice("WorkspaceNoFiles");
    }

    private void AddDirectory(string directory, int depth)
    {
        if (!_directories.TryGetValue(directory, out var listing)) return;
        if (listing.Error is { } error)
        {
            _rows.Children.Add(new TextBlock { Text = HubStrings.Get("WorkspaceFilesUnavailable") + error,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8 + depth * 14, 4, 8, 4) });
            return;
        }
        var filter = _filter.Text?.Trim() ?? "";
        foreach (var entry in listing.Entries)
        {
            if (!entry.IsDirectory && filter.Length > 0 && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), ClipToBounds = true };
            if (entry.IsDirectory)
            {
                // Reuse the sidebar group's geometry, reduced to an eight-unit arrow. Matching its aspect
                // ratio keeps the ink centered before rotation rather than stretching it into a square.
                var arrow = new Avalonia.Controls.Shapes.Path
                {
                    Width = 40.0 / 7, Height = 8, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = new RotateTransform(_expanded.Contains(entry.Path) ? 90 : 0),
                    RenderTransformOrigin = RelativePoint.Center, IsHitTestVisible = false,
                };
                arrow.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension("Hub.Icon.ChevronRight"));
                arrow.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new DynamicResourceExtension("Hub.TextSecondary"));
                row.Children.Add(arrow);
            }
            var label = new TextBlock
            {
                Text = entry.Name, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var button = new Button
            {
                Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6 + depth * 14, 4, 6, 4), Classes = { "workspace-file-row" },
            };
            button.Classes.Set("selected", WorkspacePathComparer.Equals(_selected, entry.Path));
            ToolTip.SetTip(button, entry.Path);
            button.Click += (_, _) =>
            {
                if (entry.IsDirectory)
                {
                    if (!_expanded.Add(entry.Path)) _expanded.Remove(entry.Path);
                    _ = RefreshAsync();
                }
                else
                {
                    _selected = entry.Path;
                    RenderRows();
                    FileRequested?.Invoke(entry.Path);
                }
            };
            _rows.Children.Add(button);
            if (entry.IsDirectory && _expanded.Contains(entry.Path)) AddDirectory(entry.Path, depth + 1);
        }
    }

    private void Notice(string key)
    {
        _rows.Children.Clear();
        var text = new TextBlock { Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, new DynamicResourceExtension(key));
        _rows.Children.Add(text);
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
    public void Dispose() { SetActive(false); StopWatchers(); }
    internal Button CloseButtonForCheck => _close;
    internal TextBox FilterForCheck => _filter;
    internal string? SelectedForCheck => _selected;
    internal IReadOnlyCollection<string> ExpandedForCheck => _expanded;
    internal int EntryCountForCheck => _directories.Values.Sum(listing => listing.Entries.Count);
}
