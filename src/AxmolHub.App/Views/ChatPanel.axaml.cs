using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub.App;

public partial class ChatPanel : UserControl
{
    private readonly ChatWorkspace _chat;
    private CancellationTokenSource? _send;
    private bool _ready;

    public ChatPanel()
    {
        _chat = null!;
        InitializeComponent();
    }

    public ChatPanel(ChatWorkspace chat)
    {
        _chat = chat;
        InitializeComponent();

        _chat.Changed += Reload;
        ConversationSearch.TextChanged += (_, _) => RefreshConversationList();
        ModelPicker.SelectionChanged += (_, _) =>
        {
            if (!_ready || ModelPicker.SelectedItem is not ChatWorkspace.ChatModelOption choice) return;
            _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);
        };
        NewConversationButton.Click += (_, _) => _chat.StartConversation();
        DeleteConversationButton.Click += (_, _) => DeleteActiveConversation();
        SendButton.Click += (_, _) => _ = SendAsync();

        InputBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            await SendAsync();
        };

        Reload();
    }

    public void Reload()
    {
        if (_chat is null) return;
        _ready = false;

        UpdateConversationTitle();
        NewConversationButton.Content = HubStrings.Get("NewConversation");
        DeleteConversationButton.Content = HubStrings.Get("DeleteConversation");
        DeleteConversationButton.IsEnabled = _chat.ActiveConversation is not null && _send is null;
        ConversationSearch.PlaceholderText = HubStrings.Get("SearchConversations");
        ConversationListLabel.Text = HubStrings.Get("Conversation");
        SendButton.Content = _send is null ? HubStrings.Get("Send") : HubStrings.Get("Stop");
        InputBox.PlaceholderText = HubStrings.Get("InputPlaceholder");
        EmptyHint.Text = HubStrings.Get("AssistantEmpty");

        RefreshModelPicker();
        UpdateActiveModel();
        RefreshConversationList();
        RenderMessages();
        _ready = true;
    }

    private void UpdateActiveModel()
    {
        ActiveModelLabel.Text = _chat.SelectedChatModel is { } choice
            ? string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ActiveModelFormat"),
                choice.Provider.Name,
                choice.ModelName)
            : HubStrings.Get("NoAvailableChatModels");
    }

    private void UpdateConversationTitle()
        => TitleLabel.Text = _chat.ActiveConversation is { Title.Length: > 0 } active
            ? active.Title
            : HubStrings.Get("Assistant");

    private void RefreshModelPicker()
    {
        var choices = _chat.AvailableChatModels.ToArray();
        ModelPicker.ItemsSource = choices;
        ModelPicker.SelectedItem = _chat.SelectedChatModel is { } selected
            ? choices.FirstOrDefault(choice =>
                choice.Provider.Id == selected.Provider.Id
                && string.Equals(choice.ModelName, selected.ModelName, StringComparison.OrdinalIgnoreCase))
            : null;
        ModelPicker.IsEnabled = choices.Length > 0;
    }

    private void RefreshConversationList()
    {
        if (_chat is null) return;
        var query = (ConversationSearch.Text ?? "").Trim();
        var activeId = _chat.ActiveConversation?.Id;
        var summaries = _chat.Conversations
            .Where(summary => query.Length == 0 || summary.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        ConversationList.Children.Clear();
        foreach (var summary in summaries)
        {
            var title = summary.Title.Length > 0 ? summary.Title : HubStrings.Get("NewConversation");
            var item = new Button
            {
                Content = title,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8),
            };
            ToolTip.SetTip(item, title);
            item.Classes.Add("session");
            if (summary.Id == activeId) item.Classes.Add("active");
            item.Tag = summary.Id;
            item.Click += (_, _) => _chat.OpenConversation(summary.Id);
            ConversationList.Children.Add(item);
        }

        if (summaries.Length == 0)
        {
            ConversationList.Children.Add(new TextBlock
            {
                Text = query.Length == 0 ? HubStrings.Get("NoConversations") : HubStrings.Get("NoSearchResults"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 6),
            });
        }
    }

    private void DeleteActiveConversation()
    {
        if (_chat.ActiveConversation is { } active) _chat.DeleteConversation(active.Id);
    }

    private async Task SendAsync()
    {
        if (_send is not null)
        {
            _send.Cancel();
            return;
        }

        var text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (_chat.SelectedChatModel is null)
        {
            AppendNotice(HubStrings.Get("NoAvailableChatModels"));
            return;
        }

        if (_chat.ActiveConversation is null) _chat.StartConversation();
        InputBox.Text = "";

        AppendBubble(HubStrings.Get("You"), text, fromUser: true);
        var assistantText = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "" };
        AppendBubble(HubStrings.Get("Assistant"), assistantText, fromUser: false);

        _send = new CancellationTokenSource();
        SendButton.Content = HubStrings.Get("Stop");
        DeleteConversationButton.IsEnabled = false;
        try
        {
            await foreach (var chunk in _chat.SendAsync(text, _send.Token).ConfigureAwait(true))
            {
                assistantText.Text += chunk;
                ScrollToEnd();
            }
        }
        catch (OperationCanceledException)
        {
            AppendNotice(HubStrings.Get("ChatCancelled"));
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatFailed") + ex.Message);
        }
        finally
        {
            _send.Dispose();
            _send = null;
            SendButton.Content = HubStrings.Get("Send");
            UpdateConversationTitle();
            DeleteConversationButton.IsEnabled = _chat.ActiveConversation is not null;
            RefreshConversationList();
        }
    }

    private void RenderMessages()
    {
        MessageFlow.Children.Clear();
        var conversation = _chat.ActiveConversation;
        if (conversation is null || conversation.Messages.Count == 0)
        {
            MessageFlow.Children.Add(EmptyHint);
            EmptyHint.IsVisible = true;
            return;
        }

        foreach (var turn in conversation.Messages)
        {
            if (turn.Role == ChatRoles.System) continue;
            AppendBubble(
                turn.Role == ChatRoles.User ? HubStrings.Get("You") : HubStrings.Get("Assistant"),
                turn.Text,
                fromUser: turn.Role == ChatRoles.User);
        }

        ScrollToEnd();
    }

    private void AppendBubble(string speaker, string text, bool fromUser)
        => AppendBubble(speaker, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, fromUser);

    private void AppendBubble(string speaker, TextBlock body, bool fromUser)
    {
        EmptyHint.IsVisible = false;
        var label = new TextBlock
        {
            Text = speaker,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrush(fromUser ? "Hub.AccentBorder" : "Hub.TextSecondary"),
        };
        var bubble = new Border
        {
            Background = ThemeBrush(fromUser ? "Hub.Surface" : "Hub.SurfaceRaised"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 9),
            MaxWidth = 780,
            HorizontalAlignment = fromUser
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 4, Children = { label, body } },
        };
        MessageFlow.Children.Add(bubble);
        ScrollToEnd();
    }

    private void AppendNotice(string text)
    {
        MessageFlow.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = ThemeBrush("Hub.DangerText"),
        });
        ScrollToEnd();
    }

    private static IBrush? ThemeBrush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) ? value as IBrush : null;

    private void ScrollToEnd() => Dispatcher.UIThread.Post(
        () => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);

    internal string FlowText => string.Join("\n", MessageFlow.Children
        .OfType<Border>()
        .SelectMany(bubble => (bubble.Child as StackPanel)?.Children.OfType<TextBlock>() ?? [])
        .Select(block => block.Text));

    internal string ActiveModelText => ActiveModelLabel.Text ?? "";
    internal int ConversationCount => _chat.Conversations.Count;
    internal int ModelChoiceCount => ModelPicker.ItemCount;
    internal string SelectedModelText => ModelPicker.SelectedItem?.ToString() ?? "";
    internal string ConversationListText => string.Join("\n", ConversationList.Children
        .OfType<Button>()
        .Select(button => button.Content?.ToString()));
    internal string ConversationTitleText => TitleLabel.Text ?? "";
    internal int BubbleCount => MessageFlow.Children.OfType<Border>().Count();

    internal void SearchForCheck(string query)
    {
        ConversationSearch.Text = query;
        RefreshConversationList();
    }

    internal bool OpenConversationForCheck(string id)
    {
        var item = ConversationList.Children.OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Tag?.ToString(), id, StringComparison.Ordinal));
        if (item is null) return false;
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return _chat.ActiveConversation?.Id == id;
    }

    internal bool SelectModelForCheck(string providerId, string modelName)
    {
        var choice = ModelPicker.Items?.Cast<ChatWorkspace.ChatModelOption>()
            .FirstOrDefault(option => option.Provider.Id == providerId && option.ModelName == modelName);
        if (choice is null) return false;
        ModelPicker.SelectedItem = choice;
        return true;
    }

    internal async Task SendForCheckAsync(string text)
    {
        InputBox.Text = text;
        await SendAsync();
    }
}
