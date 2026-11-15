using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;
public partial class ProjectsPage : UserControl
{
    internal DataGrid GridForCheck => ProjectsGrid;
    internal ProjectListItem[] RowsForCheck => ProjectsGrid.ItemsSource as ProjectListItem[] ?? [];
    internal TextBox SearchForCheck => SearchBox;
    internal ComboBox EngineFilterForCheck => EngineFilter;
    internal ComboBox PlatformFilterForCheck => PlatformFilter;
    internal Button SortForCheck => SortButton;
    internal Button NewProjectForCheck => NewProjectButton;
    internal NewProjectDialog? CreateDialogForCheck => _createDialog;
    internal Task ConfirmDeleteForCheckAsync(ProjectEntry project) => ConfirmDeleteAsync(project);

    internal void SyncColumnsForCheck() => SyncColumns();
}
