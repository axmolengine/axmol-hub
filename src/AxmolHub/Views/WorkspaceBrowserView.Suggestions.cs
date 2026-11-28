using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed record BrowserOpenTabCandidate(string TabId, string Url, string? Title, IImage? Favicon);

internal sealed record WorkspaceBrowserHost(
    Func<BrowserHistoryStore> HistoryStore,
    Func<WorkspaceBrowserView, IReadOnlyList<BrowserOpenTabCandidate>> OtherTabs,
    Action<string> SwitchToTab);

internal sealed partial class WorkspaceBrowserView
{
    private readonly Grid _addressHost = new() { MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _suggestionsList = new() { Spacing = 2 };
    private readonly ScrollViewer _suggestionsScroll;
    private readonly Border _suggestionsBorder;
    private readonly Popup _suggestionsPopup;
    private readonly List<BrowserSuggestion> _suggestions = [];
    private readonly List<Border> _suggestionRows = [];
    private readonly Dictionary<string, IImage?> _openTabFavicons = new(StringComparer.Ordinal);
    private CancellationTokenSource? _suggestionFaviconCancellation;
    private Window? _observedWindow;
    private bool _updatingAddressText;
    private string? _programmaticAddressText;
    private bool _unfilteredOnFocus;
    private bool _popupInteracting;
    private bool _restoringFocusAfterPopupAction;
    private bool _suppressSuggestionsUntilIdle;
    private int _suppressSuggestionsGeneration;
    private int _selectedSuggestionIndex = -1;
    private int _suggestionsVersion;

    internal bool SuggestionsOpenForCheck => _suggestionsPopup.IsOpen;
    internal int SuggestionCountForCheck => _suggestions.Count;

    private void InitializeAddressSuggestions()
    {
        _address.Classes.Add("browser-address");
        _addressHost.Children.Add(_address);

        var searchIcon = HubActionIcon.Create("Hub.Icon.Search");
        searchIcon.Width = searchIcon.Height = 12;
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        searchIcon.Margin = new Thickness(9, 0, 0, 0);
        searchIcon.IsHitTestVisible = false;
        searchIcon.Opacity = 0.72;
        _addressHost.Children.Add(searchIcon);

        _suggestionsPopup.PlacementTarget = _addressHost;
        _suggestionsPopup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        _suggestionsPopup.VerticalOffset = 5;
        _suggestionsPopup.IsLightDismissEnabled = false;
        _suggestionsPopup.Child = _suggestionsBorder;
        Children.Add(_suggestionsPopup);

        _address.GotFocus += (_, _) =>
        {
            if (!_active || _disposed || _restoringFocusAfterPopupAction || _suppressSuggestionsUntilIdle) return;
            _programmaticAddressText = null;
            _unfilteredOnFocus = true;
            if (!string.IsNullOrEmpty(_address.Text))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_address.IsFocused && _unfilteredOnFocus && !_suppressSuggestionsUntilIdle) _address.SelectAll();
                }, DispatcherPriority.Input);
            }
            RefreshSuggestions(unfiltered: true);
        };

        _address.LostFocus += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_address.IsFocused && !_popupInteracting) CloseSuggestions();
            }, DispatcherPriority.Background);
        };

        _address.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!_active || _disposed || _suppressSuggestionsUntilIdle) return;
            if (_address.IsFocused && !_suggestionsPopup.IsOpen)
                RefreshSuggestions(unfiltered: _unfilteredOnFocus);
        }, RoutingStrategies.Tunnel);

        _address.TextChanged += (_, _) =>
        {
            if (_updatingAddressText)
            {
                _programmaticAddressText = null;
                return;
            }
            if (_disposed || _suppressSuggestionsUntilIdle) return;
            var current = _address.Text ?? "";
            if (_programmaticAddressText is not null && string.Equals(current, _programmaticAddressText, StringComparison.Ordinal))
            {
                _programmaticAddressText = null;
                return;
            }
            _programmaticAddressText = null;
            if (!_address.IsFocused) return;
            _unfilteredOnFocus = false;
            _selectedSuggestionIndex = -1;
            RefreshSuggestions(unfiltered: false);
        };

        _address.AddHandler(KeyDownEvent, OnAddressKeyDown, RoutingStrategies.Tunnel);
        _bar.SizeChanged += (_, _) => { if (_suggestionsPopup.IsOpen) SyncSuggestionsPopupGeometry(); };
        _addressHost.SizeChanged += (_, _) => { if (_suggestionsPopup.IsOpen) SyncSuggestionsPopupGeometry(); };
    }

    private void SetAddressText(string? value)
    {
        var next = value ?? "";
        if (string.Equals(_address.Text ?? "", next, StringComparison.Ordinal)) return;
        _programmaticAddressText = next;
        _updatingAddressText = true;
        try
        {
            _address.Text = next;
        }
        finally
        {
            _updatingAddressText = false;
        }
    }

    private void SuppressSuggestionsUntilIdle()
    {
        _suppressSuggestionsUntilIdle = true;
        var generation = ++_suppressSuggestionsGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _suppressSuggestionsGeneration)
                _suppressSuggestionsUntilIdle = false;
        }, DispatcherPriority.Background);
    }

    private void ClearAddressFocus()
    {
        if (_address.IsFocused)
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (!_suggestionsPopup.IsOpen)
                    RefreshSuggestions(unfiltered: _unfilteredOnFocus);
                if (_suggestions.Count > 0)
                {
                    _selectedSuggestionIndex = _selectedSuggestionIndex < 0
                        ? 0
                        : (_selectedSuggestionIndex + 1) % _suggestions.Count;
                    ApplySelectedSuggestionState();
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (_suggestionsPopup.IsOpen && _suggestions.Count > 0)
                {
                    _selectedSuggestionIndex = _selectedSuggestionIndex <= 0 ? -1 : _selectedSuggestionIndex - 1;
                    ApplySelectedSuggestionState();
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                if (_suggestionsPopup.IsOpen)
                {
                    CloseSuggestions();
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                e.Handled = true;
                if (_suggestionsPopup.IsOpen && _selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < _suggestions.Count)
                {
                    ActivateSuggestion(_suggestions[_selectedSuggestionIndex]);
                    return;
                }
                if (_suggestionsPopup.IsOpen && _suggestions.Count > 0
                    && !_unfilteredOnFocus && !BrowserAddressSuggestions.LooksLikeDirectAddress(_address.Text))
                {
                    ActivateSuggestion(_suggestions[0]);
                    return;
                }
                CloseSuggestions();
                NavigateAddress();
                break;
        }
    }

    private void RefreshSuggestions(bool unfiltered)
    {
        if (_disposed || !_active || _host is null)
        {
            CloseSuggestions();
            return;
        }

        var query = unfiltered ? "" : (_address.Text ?? "").Trim();
        var terms = BrowserAddressSuggestions.SplitQueryTerms(query);
        var openTabCandidates = _host.OtherTabs(this);
        _openTabFavicons.Clear();
        var openTabs = new List<BrowserOpenTabInfo>(openTabCandidates.Count);
        foreach (var tab in openTabCandidates)
        {
            if (string.IsNullOrWhiteSpace(tab.Url)) continue;
            _openTabFavicons[tab.TabId] = tab.Favicon;
            openTabs.Add(new BrowserOpenTabInfo(tab.TabId, tab.Url, tab.Title));
        }

        var history = _host.HistoryStore().Entries;
        var items = BrowserAddressSuggestions.Build(query, openTabs, history, BrowserAddressSuggestions.DefaultMaxResults);

        _suggestions.Clear();
        _suggestions.AddRange(items);
        if (_selectedSuggestionIndex >= _suggestions.Count) _selectedSuggestionIndex = -1;

        if (_suggestions.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        SyncSuggestionsPopupGeometry();
        RenderSuggestionRows(terms);
        AttachWindowObservers();
        if (!_suggestionsPopup.IsOpen) _suggestionsPopup.IsOpen = true;
    }

    private void SyncSuggestionsPopupGeometry()
    {
        var wideAddress = _addressHost.Bounds.Width >= 380;
        _suggestionsPopup.PlacementTarget = wideAddress ? _addressHost : _bar;
        var width = wideAddress ? _addressHost.Bounds.Width : _bar.Bounds.Width;
        if (width > 0) _suggestionsBorder.Width = Math.Max(220, width);
    }

    private void RenderSuggestionRows(IReadOnlyList<string> terms)
    {
        CancelSuggestionFavicons();
        var version = ++_suggestionsVersion;
        var cancellation = _suggestionFaviconCancellation = new CancellationTokenSource();
        var popupWidth = double.IsNaN(_suggestionsBorder.Width) ? Math.Max(240, _addressHost.Bounds.Width) : _suggestionsBorder.Width;
        var titleMaxWidth = Math.Max(90, (popupWidth - 88) * 0.54);

        _suggestionsList.Children.Clear();
        _suggestionRows.Clear();

        for (var i = 0; i < _suggestions.Count; i++)
        {
            var suggestion = _suggestions[i];
            var rowIndex = i;

            var iconHost = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var globe = HubActionIcon.Create("Hub.Icon.Globe");
            globe.Width = globe.Height = 13;
            globe.HorizontalAlignment = HorizontalAlignment.Center;
            globe.VerticalAlignment = VerticalAlignment.Center;
            var faviconImage = new Image { Width = 15, Height = 15, Stretch = Stretch.Uniform, IsVisible = false };
            iconHost.Children.Add(globe);
            iconHost.Children.Add(faviconImage);

            var immediateIcon = suggestion.Kind == BrowserSuggestionKind.SwitchToTab && suggestion.TabId is { } tabId
                && _openTabFavicons.TryGetValue(tabId, out var tabIcon) && tabIcon is not null
                ? tabIcon
                : BrowserFavicon.TryGetCached(suggestion.FaviconUrl, suggestion.Url);

            if (immediateIcon is not null)
            {
                faviconImage.Source = immediateIcon;
                faviconImage.IsVisible = true;
                globe.IsVisible = false;
            }
            else if (suggestion.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || suggestion.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _ = PopulateSuggestionFaviconAsync(suggestion, faviconImage, globe, version, cancellation.Token);
            }

            Control textContent;
            if (suggestion.Kind == BrowserSuggestionKind.SwitchToTab)
            {
                textContent = BuildHighlightedText(suggestion.DisplayTitle, terms, "Hub.TextPrimary", "Hub.TextPrimary");
            }
            else
            {
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
                var titleBlock = BuildHighlightedText(suggestion.DisplayTitle, terms, "Hub.TextPrimary", "Hub.TextPrimary");
                titleBlock.MaxWidth = titleMaxWidth;
                var separator = new TextBlock
                {
                    Text = " — ",
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                separator.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextTertiary"));
                var urlBlock = BuildHighlightedText(suggestion.DisplayUrl, terms, "Hub.TextSecondary", "Hub.TextPrimary");
                Grid.SetColumn(separator, 1);
                Grid.SetColumn(urlBlock, 2);
                line.Children.Add(titleBlock);
                line.Children.Add(separator);
                line.Children.Add(urlBlock);
                textContent = line;
            }

            Control trailing;
            if (suggestion.Kind == BrowserSuggestionKind.SwitchToTab)
            {
                var badge = new TextBlock
                {
                    FontSize = 11.5,
                    Margin = new Thickness(10, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                badge.Bind(TextBlock.TextProperty, new DynamicResourceExtension("WorkspaceBrowserSwitchToTab"));
                badge.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextTertiary"));
                trailing = badge;
            }
            else
            {
                var closeGlyph = HubActionIcon.Close(9, 1.3);
                if (closeGlyph is Panel closePanel && closePanel.Children.FirstOrDefault() is Avalonia.Controls.Shapes.Path closePath)
                {
                    closePath.ClearValue(Shape.StrokeProperty);
                    closePath.Classes.Add("browser-suggestion-remove-path");
                }
                var removeButton = new Button
                {
                    Name = "WorkspaceBrowserRemoveSuggestion",
                    Content = closeGlyph,
                    Focusable = false,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Classes = { "browser-suggestion-remove" },
                };
                removeButton.Bind(ToolTip.TipProperty, new DynamicResourceExtension("WorkspaceBrowserRemoveHistory"));
                removeButton.AddHandler(PointerPressedEvent, (_, e) =>
                {
                    if (!e.GetCurrentPoint(removeButton).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    RemoveHistorySuggestion(suggestion.Url);
                }, RoutingStrategies.Tunnel);
                trailing = removeButton;
            }

            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ClipToBounds = true };
            content.Children.Add(iconHost);
            Grid.SetColumn(textContent, 1);
            content.Children.Add(textContent);
            Grid.SetColumn(trailing, 2);
            content.Children.Add(trailing);

            var row = new Border
            {
                Name = "WorkspaceBrowserSuggestionRow",
                Tag = suggestion.Url,
                Child = content,
                Classes = { "browser-suggestion-row" },
            };
            row.Classes.Set("selected", rowIndex == _selectedSuggestionIndex);

            row.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _popupInteracting = true;
                try { ActivateSuggestion(suggestion); }
                finally { _popupInteracting = false; }
            };

            _suggestionRows.Add(row);
            _suggestionsList.Children.Add(row);
        }
    }

    private async Task PopulateSuggestionFaviconAsync(
        BrowserSuggestion suggestion, Image target, Control globe, int version, CancellationToken cancellation)
    {
        var image = await BrowserFavicon.LoadCachedAsync(suggestion.FaviconUrl, suggestion.Url, cancellation);
        if (image is null || _disposed || version != _suggestionsVersion || !_suggestionsPopup.IsOpen) return;
        target.Source = image;
        target.IsVisible = true;
        globe.IsVisible = false;
    }

    private void ApplySelectedSuggestionState()
    {
        for (var i = 0; i < _suggestionRows.Count; i++)
            _suggestionRows[i].Classes.Set("selected", i == _selectedSuggestionIndex);
        if (_selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < _suggestionRows.Count)
            _suggestionRows[_selectedSuggestionIndex].BringIntoView();
    }

    private void RemoveHistorySuggestion(string url)
    {
        _popupInteracting = true;
        _restoringFocusAfterPopupAction = true;
        try
        {
            _host?.HistoryStore().Remove(url);
            RefreshSuggestions(unfiltered: _unfilteredOnFocus);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && _active) _address.Focus();
                _restoringFocusAfterPopupAction = false;
                _popupInteracting = false;
            }, DispatcherPriority.Input);
        }
    }

    private void ActivateSuggestion(BrowserSuggestion suggestion)
    {
        SuppressSuggestionsUntilIdle();
        ClearAddressFocus();
        CloseSuggestions();
        if (suggestion.Kind == BrowserSuggestionKind.SwitchToTab && suggestion.TabId is { } tabId)
        {
            SetAddressText(_currentUri?.AbsoluteUri ?? "");
            _host?.SwitchToTab(tabId);
            return;
        }
        SetAddressText(suggestion.Url);
        NavigateAddress();
    }

    private void CloseSuggestions()
    {
        CancelSuggestionFavicons();
        DetachWindowObservers();
        _selectedSuggestionIndex = -1;
        if (_suggestionsPopup.IsOpen) _suggestionsPopup.IsOpen = false;
    }

    private void CancelSuggestionFavicons()
    {
        _suggestionFaviconCancellation?.Cancel();
        _suggestionFaviconCancellation?.Dispose();
        _suggestionFaviconCancellation = null;
    }

    private void AttachWindowObservers()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (ReferenceEquals(_observedWindow, window)) return;
        DetachWindowObservers();
        _observedWindow = window;
        if (_observedWindow is null) return;
        _observedWindow.Deactivated += OnObservedWindowDeactivated;
        _observedWindow.PositionChanged += OnObservedWindowMoved;
        _observedWindow.AddHandler(PointerPressedEvent, OnObservedWindowPointerPressed, RoutingStrategies.Tunnel);
    }

    private void DetachWindowObservers()
    {
        if (_observedWindow is null) return;
        _observedWindow.Deactivated -= OnObservedWindowDeactivated;
        _observedWindow.PositionChanged -= OnObservedWindowMoved;
        _observedWindow.RemoveHandler(PointerPressedEvent, OnObservedWindowPointerPressed);
        _observedWindow = null;
    }

    private void OnObservedWindowDeactivated(object? sender, EventArgs e) => CloseSuggestions();
    private void OnObservedWindowMoved(object? sender, PixelPointEventArgs e) => CloseSuggestions();

    private void OnObservedWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_suggestionsPopup.IsOpen) return;
        if (e.Source is Visual visual && (ReferenceEquals(visual, _addressHost) || _addressHost.IsVisualAncestorOf(visual)))
            return;
        CloseSuggestions();
    }

    private static TextBlock BuildHighlightedText(
        string text, IReadOnlyList<string> terms, string baseBrushKey, string matchBrushKey)
    {
        var block = new TextBlock
        {
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(baseBrushKey));

        var spans = BrowserAddressSuggestions.HighlightSpans(text, terms);
        if (spans.Count == 0)
        {
            block.Text = text;
            return block;
        }

        var cursor = 0;
        foreach (var span in spans)
        {
            if (span.Start > cursor)
                block.Inlines!.Add(new Run(text[cursor..span.Start]));
            var match = new Run(text.Substring(span.Start, span.Length)) { FontWeight = FontWeight.Bold };
            if (!string.Equals(matchBrushKey, baseBrushKey, StringComparison.Ordinal))
                match.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension(matchBrushKey));
            block.Inlines!.Add(match);
            cursor = span.Start + span.Length;
        }
        if (cursor < text.Length)
            block.Inlines!.Add(new Run(text[cursor..]));
        return block;
    }
}
