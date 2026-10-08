using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The "add a model provider" picker, built in code like <see cref="ProviderEditWindow"/> and
/// <see cref="EngineMirrorDialog"/>.
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
/// The escape hatch is preserved: the last row is always "Custom endpoint…", which opens
/// <see cref="ProviderEditWindow"/> for an endpoint nobody ships a preset for (a company gateway, a self-hosted
/// model, an Ollama box on another machine). Without it the search dialog would be a regression for exactly the
/// users the old form served.
/// </summary>
public sealed class ProviderPickerWindow : Window
{
    /// <summary>The sentinel appended after every preset: choosing it opens the free-form editor instead.</summary>
    private const string CustomId = "\u0000custom";

    private readonly List<ModelProvider> _presets;
    private readonly TextBox _search = new() { PlaceholderText = HubStrings.Get("ProviderSearchHint") };
    private readonly ListBox _list = new() { Margin = new Thickness(0, 10, 0, 0) };
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

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 0 };
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("AddProvider"), FontSize = 18 });
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("ProviderPickerHint"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        });
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        panel.Children.Add(_empty);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        var add = new Button { Content = HubStrings.Get("Add"), IsDefault = true, Classes = { "primary" } };
        add.Click += (_, _) => Commit();
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel };

        // A ListBox shows ToString() by default, which would flatten the card to a bare provider name. A
        // code-built DataTemplate lets each row own its own layout (see Row.Render) without a XAML file — the
        // same trade EngineMirrorDialog makes with its ToString-only choices, one step further because here the
        // description and endpoint genuinely need to be on screen.
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row.Render(), supportsRecycling: false);

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
            var stack = new StackPanel { Spacing = 2 };
            var title = new TextBlock { Text = Provider.Name, FontWeight = FontWeight.SemiBold };
            if (Provider.Affiliate)
            {
                // The affiliate flag rides on the card rather than only on the settings page: the disclosure has
                // to be visible at the moment of choosing, not after.
                title.Inlines?.Add(new Avalonia.Controls.Documents.Run("  ·  " + HubStrings.Get("Affiliate")) { FontSize = 11 });
            }

            stack.Children.Add(title);
            var description = Provider.Describe(Language);
            if (description.Length > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = description,
                    Classes = { "muted" },
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text = Provider.BaseUrl + "  ·  " + Provider.Model,
                Classes = { "muted" },
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return stack;
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

    // ── Verification hooks (used by --verify-shell) ──
    //
    // The dialog is modal, so the self-check cannot click it. Instead it constructs the window directly and
    // drives the pure parts — the query and what the list ends up holding — which is where a broken filter or a
    // missing custom row would show up. Rendering the rows is the part deliberately left to the eye.

    /// <summary>Builds the dialog without showing it, for the self-check.</summary>
    internal static ProviderPickerWindow ForCheck(IReadOnlyList<ModelProvider> presets) => new(presets);

    /// <summary>Types a query into the search box, exactly as a person would (the TextChanged handler is what
    /// re-filters, so writing the property directly would test nothing).</summary>
    internal void SearchForCheck(string query) => _search.Text = query;

    /// <summary>How many rows the list currently holds, custom row included.</summary>
    internal int RowCountForCheck => _list.ItemCount;

    /// <summary>The ids of the preset rows currently shown, in order (the custom row contributes nothing).</summary>
    internal string[] VisiblePresetIdsForCheck
        => _list.Items.OfType<Row>().OfType<PresetRow>().Select(row => row.Provider.Id).ToArray();

    /// <summary>Whether the "custom endpoint" row survived the current query.</summary>
    internal bool CustomRowVisibleForCheck => _list.Items.OfType<Row>().OfType<CustomRow>().Any();

    /// <summary>Whether the "no match" notice is on screen.</summary>
    internal bool EmptyNoticeVisibleForCheck => _empty.IsVisible;

    /// <summary>Selects a preset row by id and commits, as clicking Add would.</summary>
    internal bool ChoosePresetForCheck(string presetId)
    {
        _list.SelectedItem = _list.Items.OfType<Row>().OfType<PresetRow>()
            .FirstOrDefault(row => row.Provider.Id == presetId);
        if (_list.SelectedItem is null) return false;
        Commit();
        return true;
    }

    /// <summary>Selects the custom row and commits.</summary>
    internal bool ChooseCustomForCheck()
    {
        _list.SelectedItem = _list.Items.OfType<Row>().OfType<CustomRow>().FirstOrDefault();
        if (_list.SelectedItem is null) return false;
        Commit();
        return true;
    }
}
