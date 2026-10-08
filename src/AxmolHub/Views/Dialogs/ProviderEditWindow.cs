using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The add/edit form for a model provider, built in code like <see cref="EngineMirrorDialog"/>.
///
/// This is the **only** path to a custom provider (a user-supplied OpenAI-compatible endpoint — how a local
/// model is reached), so it also carries the validation: all three fields are required and the base URL must
/// be an absolute http/https URL. The check is live (the Save button stays disabled and the reason is shown),
/// and <see cref="ChatWorkspace.AddProvider"/>/<see cref="ChatWorkspace.UpdateProvider"/> re-run the same
/// validation, so a half-filled provider can never reach the file even if the UI is bypassed.
///
/// A built-in provider's base URL and name are shown **read-only**: they come from the shipped manifest, and
/// editing them would silently diverge from what OrcaRouter's partner review scans for. The user who wants a
/// different endpoint adds a custom provider — which is what the button is for.
/// </summary>
public sealed class ProviderEditWindow : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _model = new();
    private readonly TextBox _apiKey = new() { PasswordChar = '•' };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Classes = { "danger" } };
    private readonly Button _save;

    // Deliberately not named "Name": Avalonia's StyledElement already has a Name property, and hiding it
    // would be both a compiler warning and a trap for anyone reading the dialog.
    public string ProviderName => _name.Text ?? "";
    public string BaseUrl => _baseUrl.Text ?? "";
    public string Model => _model.Text ?? "";
    public string ApiKey => _apiKey.Text ?? "";

    private ProviderEditWindow(ModelProvider? existing)
    {
        var isBuiltIn = existing is { IsCustom: false };
        Title = existing is null ? HubStrings.Get("AddCustomProvider") : HubStrings.Get("EditProviderTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 18, Margin = new Thickness(0, 0, 0, 8) });

        if (isBuiltIn)
        {
            panel.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("ProviderBuiltInLockedHint"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        AddField(panel, HubStrings.Get("ProviderName"), _name, null, isBuiltIn);
        AddField(panel, HubStrings.Get("ProviderBaseUrl"), _baseUrl, HubStrings.Get("ProviderBaseUrlHint"), isBuiltIn);
        AddField(panel, HubStrings.Get("ProviderModel"), _model, HubStrings.Get("ProviderModelHint"), false);
        // The key row is always editable: a custom endpoint may or may not want one, and a built-in provider
        // exists precisely so the user pastes a key. Blank means "leave the stored key as it is".
        AddField(panel, HubStrings.Get("ApiKey"), _apiKey, HubStrings.Get("ApiKeyHint"), false);
        panel.Children.Add(_problem);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        _save = new Button { Content = HubStrings.Get("Save"), IsDefault = true, Classes = { "primary" } };
        _save.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _baseUrl.Text = existing.BaseUrl;
            _model.Text = existing.Model;
        }

        _name.TextChanged += (_, _) => Revalidate();
        _baseUrl.TextChanged += (_, _) => Revalidate();
        _model.TextChanged += (_, _) => Revalidate();
        Revalidate();
    }

    private static void AddField(StackPanel panel, string label, TextBox box, string? hint, bool readOnly)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = (IBrush?)Application.Current?.FindResource("Hub.TextSecondary"), Margin = new Thickness(0, 8, 0, 2) });
        box.IsReadOnly = readOnly;
        if (readOnly) box.Classes.Add("muted");
        panel.Children.Add(box);
        if (hint is not null)
        {
            panel.Children.Add(new TextBlock { Text = hint, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }
    }

    /// <summary>
    /// Live validation using the same rule the save path enforces (<see cref="ChatWorkspace.Validate"/>), so
    /// the button's enabled state and the actual gate can never disagree.
    /// </summary>
    private void Revalidate()
    {
        var key = ChatWorkspace.Validate(Build());
        _problem.Text = key is null ? "" : HubStrings.Get(key);
        _save.IsEnabled = key is null;
    }

    private ModelProvider Build() => new()
    {
        Name = ProviderName,
        BaseUrl = BaseUrl,
        Model = Model,
    };

    /// <summary>Shows the form. Returns the edited values on Save, or <c>null</c> on cancel.</summary>
    public static async Task<ProviderEditWindow?> ShowAsync(Window? owner, ModelProvider? existing)
    {
        var dialog = new ProviderEditWindow(existing);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog : null;
    }
}
