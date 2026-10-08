using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// A one-field prompt: a title, a single text box, and OK/Cancel. Built in code like the other dialogs.
///
/// It exists as a general dialog rather than an inline editor inside the account row because the row is
/// rebuilt from the credential list on every change — an inline <c>TextBox</c> would be thrown away mid-edit
/// the moment anything else repainted. A modal window is not rebuilt, so the text the user is typing survives.
/// </summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _input;

    public string Value => (_input.Text ?? "").Trim();

    private PromptWindow(string title, string initialValue, string? placeholder)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _input = new TextBox { Text = initialValue };
        if (placeholder is { Length: > 0 }) _input.PlaceholderText = placeholder;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18 });
        panel.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        var ok = new Button { Content = HubStrings.Get("Save"), IsDefault = true, Classes = { "primary" } };
        ok.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };

        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>Shows the prompt. Returns the trimmed text on Save, or <c>null</c> on cancel. An empty string
    /// is returned as-is: whether blank is acceptable is the caller's rule, not the dialog's.</summary>
    public static async Task<string?> ShowAsync(Window? owner, string title, string initialValue = "", string? placeholder = null)
    {
        var dialog = new PromptWindow(title, initialValue, placeholder);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Value : null;
    }
}
