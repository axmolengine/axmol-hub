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
    internal Button CloseOverlayForCheck => OverlayCloseButton;
    internal Button CancelOverlayForCheck => OverlayCancelButton;
    internal NewProjectDialog? CreateDialogForCheck => _createDialog;
    internal TextBlock TrashNameForCheck => TrashProjectName;
    internal TextBlock TrashPathForCheck => TrashProjectPath;

    internal void ShowTrashConfirmationForCheck(ProjectEntry project) => ShowTrashConfirmation(project);
    internal bool OverlayVisibleForCheck => ProjectOverlay.IsVisible;

    internal void SyncColumnsForCheck() => SyncColumns();
}
