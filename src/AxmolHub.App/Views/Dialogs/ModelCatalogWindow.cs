using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>Searches a provider's cached model catalog and enables selected models.</summary>
public sealed class ModelCatalogWindow : Window
{
    private readonly Func<IReadOnlyList<string>> _loadModels;
    private readonly Func<string, bool> _isEnabled;
    private readonly Func<string, bool> _enableModel;
    private readonly Func<Task<ModelFetchResult>> _refresh;
    private readonly TextBox _search = new() { PlaceholderText = HubStrings.Get("ModelCatalogSearchHint") };
    private readonly ListBox _list = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _empty = new()
    {
        Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
        Margin = new Thickness(0, 10, 0, 0),
    };
    private readonly TextBlock _status = new()
    {
        Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
    };
    private readonly Button _refreshButton;
    private readonly bool _canRefresh;
    private IReadOnlyList<string> _models;

    private ModelCatalogWindow(
        string providerName,
        Func<IReadOnlyList<string>> loadModels,
        Func<string, bool> isEnabled,
        Func<string, bool> enableModel,
        Func<Task<ModelFetchResult>> refresh,
        bool canRefresh)
    {
        _loadModels = loadModels;
        _isEnabled = isEnabled;
        _enableModel = enableModel;
        _refresh = refresh;
        _canRefresh = canRefresh;
        _models = _loadModels();

        Title = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("ModelCatalogTitle"), providerName);
        Width = 560;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
        };
        var title = new TextBlock
        {
            Text = Title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        };
        layout.Children.Add(title);

        var hint = new TextBlock
        {
            Text = HubStrings.Get("ModelCatalogHint"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        };
        Grid.SetRow(hint, 1);
        layout.Children.Add(hint);

        Grid.SetRow(_search, 2);
        layout.Children.Add(_search);
        var listArea = new Grid { RowDefinitions = new RowDefinitions("*") };
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        Grid.SetRow(listArea, 3);
        layout.Children.Add(listArea);

        Grid.SetRow(_status, 4);
        layout.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };
        _refreshButton = new Button
        {
            Content = HubStrings.Get("RefreshModels"),
            IsEnabled = canRefresh,
            Tag = "model-catalog-refresh",
        };
        ToolTip.SetTip(_refreshButton, HubStrings.Get("RefreshModelsHint"));
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        var close = new Button { Content = HubStrings.Get("CloseModelCatalog"), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(_refreshButton);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 5);
        layout.Children.Add(buttons);
        Content = new Border { Child = layout };

        _list.ItemTemplate = new FuncDataTemplate<ModelRow>((row, _) => row.Render(), supportsRecycling: false);
        _search.TextChanged += (_, _) => Filter();
        _list.DoubleTapped += (_, _) => EnableSelected();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is not null) EnableSelected();
        };

        Filter();
        if (!canRefresh && _models.Count == 0)
            _status.Text = HubStrings.Get("ModelCatalogRefreshUnavailable");
        Opened += (_, _) => _search.Focus();
    }

    private void Filter()
    {
        var query = (_search.Text ?? "").Trim();
        var rows = _models
            .Where(name => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(name => new ModelRow(name, _isEnabled(name), ModelCatalog.Describe(name)))
            .ToList();

        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault();
        _empty.IsVisible = rows.Count == 0;
        _empty.Text = query.Length == 0
            ? HubStrings.Get("ModelCatalogEmpty")
            : HubStrings.Get("ModelCatalogNoMatch");
    }

    private void EnableSelected()
    {
        if (_list.SelectedItem is not ModelRow row || row.Enabled) return;
        if (_enableModel(row.Name))
        {
            _status.Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ModelCatalogEnabled"),
                row.Name);
            Filter();
        }
        else
        {
            _status.Text = HubStrings.Get("ModelCatalogEnableFailed");
        }
    }

    private async Task RefreshAsync()
    {
        if (!_canRefresh) return;

        _refreshButton.IsEnabled = false;
        _status.Text = HubStrings.Get("ModelsRefreshing");
        try
        {
            var result = await _refresh();
            if (!result.Reachable)
            {
                _status.Text = HubStrings.Get("ModelsFetchFailed") + " " + result.Problem;
                return;
            }

            _models = _loadModels();
            _status.Text = _models.Count == 0
                ? HubStrings.Get("ModelsFetchedEmpty")
                : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("ModelsFetched"),
                    _models.Count);
            Filter();
        }
        finally
        {
            _refreshButton.IsEnabled = true;
        }
    }

    private sealed record ModelRow(string Name, bool Enabled, string Description)
    {
        public Control Render()
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = Name,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (Description.Length > 0)
            {
                text.Children.Add(new TextBlock
                {
                    Text = Description,
                    Classes = { "muted" },
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var state = new TextBlock
            {
                Text = HubStrings.Get(Enabled ? "ModelCatalogEnabledState" : "ModelCatalogDisabledState"),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            state.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                    Enabled ? "Hub.Success" : "Hub.TextTertiary"));

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(state, 1);
            grid.Children.Add(text);
            grid.Children.Add(state);
            return new Border
            {
                Padding = new Thickness(8, 6),
                Child = grid,
            };
        }

        public override string ToString() => Name;
    }

    internal async Task ShowAsync(Window? owner)
    {
        if (owner is null)
        {
            await ShowDialog<HubDialogResult>(null!);
        }
        else
        {
            await ShowDialog<HubDialogResult>(owner);
        }
    }

    internal static ModelCatalogWindow Create(
        ModelProvider provider,
        Func<IReadOnlyList<string>> loadModels,
        Func<string, bool> isEnabled,
        Func<string, bool> enableModel,
        Func<Task<ModelFetchResult>> refresh,
        bool canRefresh)
        => new(provider.Name, loadModels, isEnabled, enableModel, refresh, canRefresh);

    internal void SearchForCheck(string query) => _search.Text = query;

    internal string[] VisibleModelNamesForCheck
        => _list.Items.OfType<ModelRow>().Select(row => row.Name).ToArray();

    internal string[] EnabledModelNamesForCheck
        => _list.Items.OfType<ModelRow>().Where(row => row.Enabled).Select(row => row.Name).ToArray();

    internal bool HasRefreshButtonForCheck => _refreshButton.Tag as string == "model-catalog-refresh";

    internal bool ActivateForCheck(string name)
    {
        _list.SelectedItem = _list.Items.OfType<ModelRow>()
            .FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));
        if (_list.SelectedItem is null) return false;
        EnableSelected();
        return true;
    }

    internal Task RefreshForCheckAsync() => RefreshAsync();
}
