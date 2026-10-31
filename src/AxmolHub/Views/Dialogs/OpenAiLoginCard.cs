using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Agent;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>A settings-layer card; closing it cancels the pending attempt.</summary>
public sealed partial class OpenAiLoginCard : UserControl
{
    private readonly TextBox _callback;
    private readonly TextBlock _error;
    public OpenAiLoginCard(IOpenAiLoginAttempt attempt, Action cancel)
    {
        ClipToBounds = true;
        MaxWidth = 560;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var open = new Button { Content = HubStrings.Get("AuthOpenAiOpen"), Classes = { "compact" }, Margin = new Thickness(0) };
        open.Click += (_, _) => UrlLauncher.TryOpen(attempt.AuthorizationUrl);
        var copy = new Button { Content = HubStrings.Get("AuthOpenAiCopy"), Classes = { "compact" }, Margin = new Thickness(0) };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(attempt.AuthorizationUrl);
            }
            catch (Exception)
            {
                _error.Text = attempt.AuthorizationUrl;
                _error.IsVisible = true;
            }
        };
        actions.Children.Add(open);
        actions.Children.Add(copy);
        _callback = new TextBox { PlaceholderText = HubStrings.Get("AuthOpenAiCallback"), AcceptsReturn = false };
        var submit = new Button { Content = HubStrings.Get("AuthOpenAiSubmit"), Classes = { "compact" }, Margin = new Thickness(0) };
        submit.Click += (_, _) =>
        {
            if (!attempt.Submit(_callback.Text?.Trim() ?? ""))
            {
                _error.Text = HubStrings.Get("AuthOpenAiInvalid");
                _error.IsVisible = true;
            }
            else _callback.Clear();
        };
        var close = new Button { Content = HubStrings.Get("Cancel"), Classes = { "compact" }, Margin = new Thickness(0) };
        close.Click += (_, _) => cancel();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(submit);
        footer.Children.Add(close);
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = HubStrings.Get("AuthOpenAiTitle"), FontSize = 18, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = HubStrings.Get("AuthOpenAiHint"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(actions);
        content.Children.Add(_callback);
        content.Children.Add(_error);
        content.Children.Add(footer);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }
}
