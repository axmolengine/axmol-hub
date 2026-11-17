using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

public sealed record WorkspaceProjectChoice(ProjectListItem Item, bool IsAdded);

public partial class NewWorkspaceView : UserControl
{
    private readonly ChatWorkspace _chat = null!;
    private readonly Action _close = null!;
    private readonly Func<string, WorkspacePathVerdict?> _openWorkspace = null!;
    private readonly ProjectEntry[] _projects = [];

    public NewWorkspaceView() => InitializeComponent();

    public NewWorkspaceView(ChatWorkspace chat, IEnumerable<ProjectEntry> projects,
        Func<string, WorkspacePathVerdict?> openWorkspace, Action close)
    {
        InitializeComponent();
        _chat = chat;
        _close = close;
        _openWorkspace = openWorkspace;
        _projects = projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        ProjectSearchField.IsVisible = _projects.Length > 5;
        ProjectSearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) RefreshProjects();
        };
        ProjectList.SelectionChanged += (_, _) =>
        {
            SubmitButton.IsEnabled = ProjectList.SelectedItem is WorkspaceProjectChoice;
            ErrorLabel.IsVisible = false;
            LayoutChanged?.Invoke();
        };
        CloseButton.Click += (_, _) => close();
        CancelButton.Click += (_, _) => close();
        SubmitButton.Click += (_, _) => SubmitSelection();
        RefreshProjects();
    }

    internal event Action? LayoutChanged;

    // Only the project list scrolls; a short catalog keeps the picker compact.
    internal double PreferredHeight => 150 + Math.Max(1, Math.Min(4, _projects.Length)) * 66
        + (ProjectSearchField.IsVisible ? 46 : 0) + (ErrorLabel.IsVisible ? 72 : 0);

    internal void FocusInitialControl()
    {
        if (ProjectSearchBox.IsEffectivelyVisible) ProjectSearchBox.Focus();
        else ProjectList.Focus();
    }

    private void RefreshProjects()
    {
        var search = (ProjectSearchBox.Text ?? "").Trim();
        var selected = (ProjectList.SelectedItem as WorkspaceProjectChoice)?.Item.Project.Path;
        var rows = _projects.Where(project => project.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || project.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(project => new WorkspaceProjectChoice(new ProjectListItem(project, false),
                _chat.Workspaces.Any(root => SameFolder(root, project.Path))))
            .ToArray();
        ProjectList.ItemsSource = rows;
        ProjectList.SelectedItem = rows.FirstOrDefault(row => SameFolder(row.Item.Project.Path, selected));
        EmptyLabel.IsVisible = rows.Length == 0;
    }

    private static bool SameFolder(string path, string? other)
        => other is not null && SessionGroupKey.Workspace(path) == SessionGroupKey.Workspace(other);

    internal void SubmitSelection()
    {
        if (ProjectList.SelectedItem is not WorkspaceProjectChoice selected) return;
        var path = selected.Item.Project.Path;
        try
        {
            if (_openWorkspace(path) is { } refusal)
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

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
        LayoutChanged?.Invoke();
    }
}
