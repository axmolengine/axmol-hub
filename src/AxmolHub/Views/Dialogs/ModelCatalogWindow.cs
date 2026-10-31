using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Searches a provider's cached model catalog and enables selected models.</summary>
public sealed partial class ModelCatalogWindow : Window
{
    private readonly Func<IReadOnlyList<string>> _loadModels;
    private readonly Func<string, bool> _isEnabled;
    private readonly Func<string, bool> _enableModel;
    private readonly Func<Task<ModelFetchResult>> _refresh;
    private readonly TextBox _search = new() { PlaceholderText = HubStrings.Get("Search") };
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
    private readonly TextBlock _count = new()
    {
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
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

        // The bar this app draws, so the picker does not arrive wearing the platform's frame on top of the shell
        // it came out of. Its caption is Window.Title, set just above — the list window used to repeat that
        // string as a 18px TextBlock inside the body, and the header is what replaced it (one title, one place).
        // Closing here means the same thing as the dialog's own dismiss button, not the same thing as Cancel:
        // the caller of this window never branches on the result.
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Ok));

        var layout = new Grid
        {
            Margin = new Thickness(24, 12, 24, 24),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
        };

        var hint = new TextBlock
        {
            Text = HubStrings.Get("ModelCatalogHint"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        layout.Children.Add(hint);

        var searchFrame = BuildSearchField();
        Grid.SetRow(searchFrame, 1);
        layout.Children.Add(searchFrame);
        var listArea = new Grid { RowDefinitions = new RowDefinitions("*") };
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        Grid.SetRow(listArea, 2);
        layout.Children.Add(listArea);

        Grid.SetRow(_status, 3);
        layout.Children.Add(_status);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 16, 0, 0),
        };
        footer.Children.Add(_count);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        _refreshButton = new Button
        {
            Content = HubStrings.Get("RefreshModels"),
            Classes = { "compact" },
            Margin = new Thickness(0),
            IsEnabled = canRefresh,
            Tag = "model-catalog-refresh",
        };
        ToolTip.SetTip(_refreshButton, HubStrings.Get("RefreshModelsHint"));
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        var close = new Button { Content = HubStrings.Get("CloseModelCatalog"), IsDefault = true, IsCancel = true, Classes = { "compact" }, Margin = new Thickness(0) };
        close.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(_refreshButton);
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 4);
        layout.Children.Add(footer);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(chrome.Header);
        Grid.SetRow(layout, 1);
        root.Children.Add(layout);
        Content = root;

        _list.Background = Brushes.Transparent;
        // Overlay scrollbars consume the row's right edge. Reserve their own column instead so both
        // the rounded border and the model state remain visible while scrolling.
        ScrollViewer.SetAllowAutoHide(_list, false);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.Bind(ItemsControl.ItemContainerThemeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("HubModelCatalogItem"));
        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _list.ItemTemplate = new FuncDataTemplate<ModelRow>((_, _) => ModelRow.Render(), supportsRecycling: true);
        _search.TextChanged += (_, _) => Filter();
        _list.DoubleTapped += (_, _) => EnableSelected();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is not null) EnableSelected();
        };

        UpdateCount();
        Filter();
        if (!canRefresh && _models.Count == 0)
            _status.Text = HubStrings.Get("ModelCatalogRefreshUnavailable");
        Opened += (_, _) => _search.Focus();
    }

    private Border BuildSearchField()
    {
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Padding = new Thickness(0);
        _search.Margin = new Thickness(0);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.Bind(TextBox.PlaceholderForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));

        Avalonia.Controls.Shapes.Path Icon(string key, double size)
        {
            var icon = new Avalonia.Controls.Shapes.Path
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Fill = Brushes.Transparent,
                StrokeThickness = 1.7,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key));
            icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
            return icon;
        }

        var clearSearch = new Button
        {
            Classes = { "quiet" },
            Content = Icon("Hub.Icon.Close", 14),
            Padding = new Thickness(0),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        ToolTip.SetTip(clearSearch, HubStrings.Get("ClearSearch"));
        clearSearch.Click += (_, _) =>
        {
            _search.Text = "";
            _search.Focus();
        };
        _search.TextChanged += (_, _) => clearSearch.IsVisible = !string.IsNullOrEmpty(_search.Text);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,36") };
        content.Children.Add(Icon("Hub.Icon.Search", 18));
        Grid.SetColumn(_search, 1);
        content.Children.Add(_search);
        Grid.SetColumn(clearSearch, 2);
        content.Children.Add(clearSearch);
        var frame = new Border { Child = content };
        frame.Bind(StyledElement.ThemeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("HubModelCatalogSearch"));
        return frame;
    }

    private void UpdateCount()
        => _count.Text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("ModelCatalogCount"),
            _models.Count(_isEnabled),
            _models.Count);

    private void Filter()
    {
        var query = (_search.Text ?? "").Trim();
        var rows = _models
            .Where(name => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(name => new ModelRow(name, _isEnabled(name)))
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
            row.Enabled = true;
            UpdateCount();
            _status.Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ModelCatalogEnabled"),
                row.Name);
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
            UpdateCount();
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

    private sealed class ModelRow(string name, bool enabled) : INotifyPropertyChanged
    {
        private bool _enabled = enabled;

        public string Name { get; } = name;
        public string StateText => HubStrings.Get(Enabled
            ? "ModelCatalogEnabledState"
            : "ModelCatalogDisabledState");
        public bool IsEnabled => Enabled;
        public bool IsDisabled => !Enabled;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(IsDisabled));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static Control Render()
        {
            var text = new TextBlock
            {
                [!TextBlock.TextProperty] = new Binding(nameof(Name)),
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0),
            };

            var enabledState = new TextBlock
            {
                Text = HubStrings.Get("ModelCatalogEnabledState"),
                [!Visual.IsVisibleProperty] = new Binding(nameof(IsEnabled)),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            enabledState.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.Success"));
            var disabledState = new TextBlock
            {
                Text = HubStrings.Get("ModelCatalogDisabledState"),
                [!Visual.IsVisibleProperty] = new Binding(nameof(IsDisabled)),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            disabledState.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextTertiary"));
            var state = new Grid();
            state.Children.Add(enabledState);
            state.Children.Add(disabledState);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Tag = "model-catalog-row",
            };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(state, 1);
            grid.Children.Add(text);
            grid.Children.Add(state);
            return grid;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
}
