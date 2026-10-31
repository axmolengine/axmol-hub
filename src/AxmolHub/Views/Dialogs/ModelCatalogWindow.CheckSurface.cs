using Avalonia.Controls;

namespace AxmolHub;
public sealed partial class ModelCatalogWindow : Window
{
    internal string CountTextForCheck => _count.Text ?? "";
    internal void SearchForCheck(string query) => _search.Text = query;
    internal string[] VisibleModelNamesForCheck => _list.Items.OfType<ModelRow>().Select(row => row.Name).ToArray();
    internal string[] EnabledModelNamesForCheck => _list.Items.OfType<ModelRow>().Where(row => row.Enabled).Select(row => row.Name).ToArray();
    internal bool HasRefreshButtonForCheck => _refreshButton.Tag as string == "model-catalog-refresh";
    internal bool UsesVirtualizingPanelForCheck => _list.ItemsPanel?.Build() is VirtualizingStackPanel;
    internal bool ActivationPreservedItemsSourceForCheck { get; private set; }

    internal bool ActivateForCheck(string name)
    {
        _list.SelectedItem = _list.Items.OfType<ModelRow>().FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));
        if (_list.SelectedItem is null)
            return false;
        var itemsSource = _list.ItemsSource;
        EnableSelected();
        ActivationPreservedItemsSourceForCheck = ReferenceEquals(itemsSource, _list.ItemsSource);
        return true;
    }

    internal Task RefreshForCheckAsync() => RefreshAsync();
}
