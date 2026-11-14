using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

public sealed record WorkspaceProjectChoice(ProjectListItem Item, bool IsAdded);

public partial class NewWorkspaceView : UserControl
{
    private readonly Window _owner = null!;
    private readonly ChatWorkspace _chat = null!;
    private readonly Action _close = null!;
    private readonly ProjectEntry[] _projects = [];
    private string? _selectedPath;
    private bool _refreshingProjects;
    private bool _pickingFolder;

    public NewWorkspaceView() => InitializeComponent();

    public NewWorkspaceView(Window owner, ChatWorkspace chat, IEnumerable<ProjectEntry> projects,
        bool showProjects, Action close)
    {
        InitializeComponent();
        _owner = owner;
        _chat = chat;
        _close = close;
        _projects = showProjects
            ? projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        ProjectSection.IsVisible = _projects.Length > 0;
        ProjectSearchField.IsVisible = _projects.Length > 5;
        ProjectSearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) RefreshProjects();
        };
        ProjectList.SelectionChanged += (_, _) =>
        {
            if (!_refreshingProjects && ProjectList.SelectedItem is WorkspaceProjectChoice selected)
                SelectFolder(selected.Item.Project.Path);
        };
        CloseButton.Click += (_, _) => close();
        CancelButton.Click += (_, _) => close();
        SubmitButton.Click += (_, _) => SubmitSelection();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        RefreshProjects();
    }

    internal event Action? LayoutChanged;

    // A short list should not inherit the empty height of a full picker. The list alone scrolls at the ceiling.
    internal double PreferredHeight => 244 + (ProjectSection.IsVisible
        ? 40 + Math.Min(4, _projects.Length) * 66 + (ProjectSearchField.IsVisible ? 46 : 0)
        : 0) + (ErrorLabel.IsVisible ? 72 : 0);

    internal void FocusInitialControl()
    {
        if (ProjectSearchBox.IsEffectivelyVisible) ProjectSearchBox.Focus();
        else BrowseButton.Focus();
    }

    private bool IsAdded(string path)
        => _chat.Workspaces.Any(root => SessionGroupKey.Workspace(root) == SessionGroupKey.Workspace(path));

    private void RefreshProjects()
    {
        var search = (ProjectSearchBox.Text ?? "").Trim();
        var rows = _projects.Where(project => project.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || project.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(project => new WorkspaceProjectChoice(new ProjectListItem(project, false, false), IsAdded(project.Path)))
            .ToArray();
        _refreshingProjects = true;
        try
        {
            ProjectList.ItemsSource = rows;
            ProjectList.SelectedItem = rows.FirstOrDefault(row => SameFolder(row.Item.Project.Path, _selectedPath));
        }
        finally { _refreshingProjects = false; }
        EmptyLabel.IsVisible = rows.Length == 0;
        RefreshSubmit();
    }

    private static bool SameFolder(string path, string? other)
        => other is not null && SessionGroupKey.Workspace(path) == SessionGroupKey.Workspace(other);

    private void SelectFolder(string path)
    {
        _selectedPath = path;
        var project = _projects.FirstOrDefault(project => SameFolder(project.Path, path))
            ?? new ProjectEntry { Path = path };
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        SelectedFolderName.Text = name.Length > 0 ? name : path;
        SelectedFolderPath.Text = new ProjectListItem(project, false, false).DisplayPath;
        ToolTip.SetTip(SelectedFolderPanel, path);
        FolderPlaceholder.IsVisible = false;
        SelectedFolderPanel.IsVisible = true;
        BrowseButton.Content = HubStrings.Get("WorkspaceChangeFolder");
        ErrorLabel.IsVisible = false;
        _refreshingProjects = true;
        try
        {
            ProjectList.SelectedItem = ProjectList.Items.OfType<WorkspaceProjectChoice>()
                .FirstOrDefault(row => SameFolder(row.Item.Project.Path, path));
        }
        finally { _refreshingProjects = false; }
        RefreshSubmit();
        LayoutChanged?.Invoke();
    }

    private void RefreshSubmit()
    {
        SubmitButton.IsEnabled = _selectedPath is not null && !_pickingFolder;
        SubmitButton.Content = HubStrings.Get(_selectedPath is not null && IsAdded(_selectedPath)
            ? "WorkspaceOpen" : "WorkspaceCreate");
    }

    internal void SubmitSelection()
    {
        if (_pickingFolder || _selectedPath is not { } path) return;
        try
        {
            var (_, verdict) = _chat.StartOrOpenEmptyConversationInWorkspace(path);
            if (verdict is { } refusal)
            {
                ShowError(MainWindow.WorkspaceRejectionMessage(refusal, path));
                return;
            }
            _close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task BrowseAsync()
    {
        if (_pickingFolder) return;
        _pickingFolder = true;
        BrowseButton.IsEnabled = false;
        RefreshSubmit();
        try
        {
            var result = await Pickers.PickFolderAsync(_owner, HubStrings.Get("ChatWorkspacePickTitle"));
            // Navigation or a data-root switch may have dismissed the layer while the native picker was open.
            if (TopLevel.GetTopLevel(this) is null) return;
            if (result.Outcome == PickOutcome.Cancelled) return;
            if (result.Outcome == PickOutcome.NotLocal)
                ShowError(HubStrings.Get("ChatFolderNotLocal"));
            else if (result.Path is { Length: > 0 } path) SelectFolder(path);
            else ShowError(HubStrings.Get("LocalPathRequired"));
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _pickingFolder = false;
            BrowseButton.IsEnabled = true;
            RefreshSubmit();
            if (TopLevel.GetTopLevel(this) is not null) BrowseButton.Focus();
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
        LayoutChanged?.Invoke();
    }
}
