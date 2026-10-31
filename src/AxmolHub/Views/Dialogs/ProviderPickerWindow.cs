using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The "add a model provider" picker, built in code like <see cref="EngineMirrorDialog"/>.
///
/// This replaces the old flow of "Add provider → an empty form". That form asked the user to already know an
/// OpenAI-compatible base URL and a model name before they had a provider to use — which is a configuration
/// screen for people who have already read the docs. GitHub Copilot's flow is the opposite shape and is the one
/// copied here: a **searchable list of known providers** (OrcaRouter, OpenAI, DeepSeek, Ollama …), each with a
/// one-line description, and picking one is the whole interaction.
///
/// The catalog is not hardcoded — it comes from <see cref="ChatWorkspace.AvailablePresets"/>, which reads the
/// same <c>manifests/ai-providers.json</c> that ships the endpoints. Adding a provider is a data change.
///
/// The escape hatch is preserved: the last row is always "Custom endpoint…", which closes the picker and opens
/// the settings page's own add draft (名称 / 接口地址 / 模型, in place in the provider list) for an endpoint
/// nobody ships a preset for — a company gateway, a self-hosted model, an Ollama box on another machine.
/// Without it the search dialog would be a regression for exactly the users the old form served.
/// </summary>
public sealed partial class ProviderPickerWindow : Window
{
    /// <summary>The sentinel appended after every preset: choosing it opens the free-form editor instead.</summary>
    private const string CustomId = "\u0000custom";

    private readonly List<ModelProvider> _presets;
    private readonly TextBox _search = new() { PlaceholderText = HubStrings.Get("ProviderSearchHint") };
    private readonly ListBox _list = new()
    {
        Margin = new Thickness(0, 10, 0, 0),
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _empty = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };

    /// <summary>The chosen preset, or the sentinel when the user picked "Custom endpoint…".</summary>
    public string? SelectedId { get; private set; }

    /// <summary>True when the user chose the custom row rather than a preset.</summary>
    public bool WantsCustom => SelectedId == CustomId;

    private ProviderPickerWindow(IReadOnlyList<ModelProvider> presets)
    {
        _presets = presets.ToList();
        Title = HubStrings.Get("AddProvider");
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // The bar this app draws. Its caption is Window.Title above, which is also why the body no longer opens
        // with a copy of "添加提供商" in 18px — the name is said once. The × means what the Cancel button means:
        // ShowAsync hands the caller a null, i.e. "the user did not pick one".
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        var body = new Grid
        {
            Margin = new Thickness(24, 12, 24, 24),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
        };

        var hint = new TextBlock
        {
            Text = HubStrings.Get("ProviderPickerHint"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        body.Children.Add(hint);
        Grid.SetRow(hint, 0);

        body.Children.Add(_search);
        Grid.SetRow(_search, 1);

        // The list sits in a star-sized row so it gets a bounded height and the ListBox's own scroller can
        // work. Inside a StackPanel it would receive infinite height and the scrollbar would never appear.
        body.Children.Add(_list);
        Grid.SetRow(_list, 2);

        body.Children.Add(_empty);
        Grid.SetRow(_empty, 3);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true, Classes = { "compact" }, Margin = new Thickness(0) };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        var add = new Button { Content = HubStrings.Get("Add"), IsDefault = true, Classes = { "compact", "primary" }, Margin = new Thickness(0) };
        add.Click += (_, _) => Commit();
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        body.Children.Add(buttons);
        Grid.SetRow(buttons, 4);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(chrome.Header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        // A ListBox shows ToString() by default, which would flatten the card to a bare provider name. A
        // code-built DataTemplate lets each row own its own layout (see Row.Render) without a XAML file — the
        // same trade EngineMirrorDialog makes with its ToString-only choices, one step further because here the
        // description and endpoint genuinely need to be on screen.
        //
        // The item handed to this template can be null, and when the list scrolls it is: the panels recycle a
        // container that left the viewport by clearing its Content, and the presenter runs the template anyway.
        // An unguarded row.Render() therefore threw NullReferenceException straight out of a layout pass the
        // first time this list was scrolled — nothing above it catches, so the process died with the dialog on
        // screen. Rendering nothing for the null item is the whole contract: the row that scrolled away is not
        // drawn again.
        _list.ItemTemplate = new FuncDataTemplate<Row>(
            (row, _) => row is null ? new Panel() : row.Render(),
            supportsRecycling: false);

        _search.TextChanged += (_, _) => Filter();
        // Double-clicking a row is the fast path; Enter is the careful one. Both end in Commit.
        _list.DoubleTapped += (_, _) => Commit();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is not null) Commit();
        };

        Filter();
        Opened += (_, _) => _search.Focus();
    }

    /// <summary>
    /// Rebuilds the visible rows for the current query. Matching is a case-insensitive substring over both the
    /// display name and the description: someone who only remembers "本地" should find Ollama, and someone who
    /// only remembers "anthropic" should not be told there is nothing (the descriptions name the vendor).
    /// </summary>
    private void Filter()
    {
        var query = (_search.Text ?? "").Trim();
        var language = HubStrings.Language;

        var matched = _presets
            .Where(preset => query.Length == 0
                             || preset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || preset.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || preset.Describe(language).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(preset => (Row)new PresetRow(preset, language))
            .ToList();

        // The custom row is appended to the (filtered) list only when it can match — it is always available
        // with an empty query, and matches on its label *and* on a stable ASCII alias. The alias matters: the
        // label is localized, so without it a user typing "custom" in a Chinese UI (or reading an English
        // screenshot of this dialog) would be told there is no such provider.
        var customLabel = HubStrings.Get("ProviderCustomRow");
        var customMatches = query.Length == 0
                            || customLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || "custom".Contains(query, StringComparison.OrdinalIgnoreCase)
                            || "endpoint".Contains(query, StringComparison.OrdinalIgnoreCase);
        if (customMatches)
        {
            matched.Add(new CustomRow(customLabel, HubStrings.Get("ProviderCustomRowHint")));
        }

        // Re-assign ItemsSource rather than mutate Items: the ComboBox lesson (a selection holding a
        // distinct-but-equal object silently clears) applies to ListBox too, and rebuilding avoids it entirely.
        _list.ItemsSource = matched;
        _list.SelectedItem = matched.FirstOrDefault();
        _empty.IsVisible = matched.Count == 0;
        _empty.Text = HubStrings.Get("ProviderSearchNoMatch");
    }

    private void Commit()
    {
        switch (_list.SelectedItem)
        {
            case PresetRow preset:
                SelectedId = preset.Provider.Id;
                Close(HubDialogResult.Ok);
                break;
            case CustomRow:
                SelectedId = CustomId;
                Close(HubDialogResult.Ok);
                break;
        }
    }

    /// <summary>A row in the picker. Two shapes only, so a plain abstract record beats a templated ListBox —
    /// the item template would need its own XAML and a display-member binding, and this is 40 lines of layout.</summary>
    private abstract record Row
    {
        public abstract Control Render();
    }

    private sealed record PresetRow(ModelProvider Provider, string Language) : Row
    {
        public override Control Render()
        {
            // Name + one quiet line. The picker is a scan-and-pick list, not a datasheet: the long manifest
            // description stays in the search index (Filter matches on it) and in the settings card after the
            // choice, but it does not get three wrapped lines per row here.
            var stack = new StackPanel { Spacing = 2 };
            var title = new TextBlock { Text = Provider.Name, FontWeight = FontWeight.SemiBold, FontSize = 13 };
            if (Provider.Affiliate)
            {
                // The affiliate flag rides on the card rather than only on the settings page: the disclosure has
                // to be visible at the moment of choosing, not after.
                title.Inlines?.Add(new Avalonia.Controls.Documents.Run("  ·  " + HubStrings.Get("Affiliate")) { FontSize = 11 });
            }

            stack.Children.Add(title);

            var oneLiner = ShortLine(Provider.Describe(Language));
            if (oneLiner.Length > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = oneLiner,
                    Classes = { "muted" },
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            return stack;
        }

        /// <summary>
        /// First sentence (or first clause) of a description, capped so a row stays one glance tall.
        /// Search still runs over the full text — this is presentation only.
        /// </summary>
        private static string ShortLine(string description)
        {
            if (description.Length == 0) return "";
            var stop = description.IndexOfAny(['。', '！', '？', '.', '!', '?']);
            var line = stop > 0 ? description[..stop] : description;
            const int cap = 42;
            return line.Length <= cap ? line : line[..(cap - 1)].TrimEnd() + "…";
        }

        public override string ToString() => Provider.Name;
    }

    private sealed record CustomRow(string Label, string Hint) : Row
    {
        public override Control Render()
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = Label, FontWeight = FontWeight.SemiBold });
            stack.Children.Add(new TextBlock
            {
                Text = Hint,
                Classes = { "muted" },
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return stack;
        }

        public override string ToString() => Label;
    }

    /// <summary>Shows the picker. Returns the chosen preset, the custom sentinel via
    /// <see cref="WantsCustom"/>, or <c>null</c> when the user cancelled.</summary>
    public static async Task<ProviderPickerWindow?> ShowAsync(Window? owner, IReadOnlyList<ModelProvider> presets)
    {
        var dialog = new ProviderPickerWindow(presets);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog : null;
    }
}
