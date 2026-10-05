using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using AvaloniaEdit;
using Markdown.Avalonia;
using MarkdownEngine = Markdown.Avalonia.Markdown;
using ColorTextBlock.Avalonia;

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
            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
            };
            row.Classes.Add("session-row");
            var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,42") };
            var item = new Button
            {
                Content = title,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0),
            };
            ToolTip.SetTip(item, title);
            item.Classes.Add("session");
            if (summary.Id == activeId) item.Classes.Add("active");
            item.Tag = summary.Id;
            item.Click += (_, _) => _chat.OpenConversation(summary.Id);
            var menuButton = new Button
            {
                Content = "⋯",
                Width = 40,
                Height = 38,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                FontSize = 18,
            };
            menuButton.Classes.Add("session-menu");
            ToolTip.SetTip(menuButton, HubStrings.Get("ConversationActions"));

            var menu = new MenuFlyout();
            var deleteItem = new MenuItem { Header = HubStrings.Get("DeleteConversation") };
            deleteItem.Click += (_, _) => DeleteConversation(summary.Id);
            menu.Items.Add(deleteItem);
            menuButton.Click += (_, _) => menu.ShowAt(menuButton);
            menuButton.Tag = menu;

            layout.Children.Add(item);
            Grid.SetColumn(menuButton, 1);
            layout.Children.Add(menuButton);
            row.Child = layout;
            row.Tag = summary.Id;
            ConversationList.Children.Add(row);
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

    private void DeleteConversation(string id) => _chat.DeleteConversation(id);

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
        var assistantBody = new StackPanel { Spacing = 8 };
        AppendBubble(HubStrings.Get("Assistant"), assistantBody, fromUser: false);
        var assistantMarkdown = new StringBuilder();
        var assistantPreview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        assistantBody.Children.Add(assistantPreview);

        _send = new CancellationTokenSource();
        SendButton.Content = HubStrings.Get("Stop");
        try
        {
            await foreach (var chunk in _chat.SendAsync(text, _send.Token).ConfigureAwait(true))
            {
                assistantMarkdown.Append(chunk);
                assistantPreview.Text = assistantMarkdown.ToString();
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
            MarkdownMessageRenderer.RenderInto(assistantBody, assistantMarkdown.ToString());
            _send.Dispose();
            _send = null;
            SendButton.Content = HubStrings.Get("Send");
            UpdateConversationTitle();
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
        => AppendBubble(speaker, MarkdownMessageRenderer.Render(text), fromUser);

    private void AppendBubble(string speaker, Control body, bool fromUser)
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
                : Avalonia.Layout.HorizontalAlignment.Stretch,
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
        .SelectMany(bubble => bubble.GetLogicalDescendants().OfType<TextBlock>())
        .Select(block => string.IsNullOrEmpty(block.Text)
            ? string.Concat(block.Inlines?.Select(inline => inline switch
            {
                Run run => run.Text,
                InlineUIContainer { Child: TextBlock { Tag: "markdown-link", Text: string linkText } } => linkText,
                _ => "",
            }) ?? [])
            : block.Text)
        .Concat(MessageFlow.Children
            .OfType<Border>()
            .SelectMany(bubble => bubble.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
            .Select(viewer => viewer.Tag as string ?? "")));

    internal bool HasVisibleMarkdownCodeBlock(string code)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => viewer.Markdown?.Contains(code, StringComparison.Ordinal) == true
                           && viewer.Bounds.Width > 0
                           && viewer.Bounds.Height > 0);

    internal bool HasRenderedMarkdownLink(string url)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => MarkdownMessageRenderer.HasLinkHandler(viewer, url));

    internal bool HasThemedMarkdownLink()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedLink);

    internal bool HasSyntaxHighlightedCode(string language)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .SelectMany(viewer => viewer.GetVisualDescendants().OfType<TextEditor>())
            .Any(editor => string.Equals(editor.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase)
                           && editor.SyntaxHighlighting is not null);

    internal bool HasMarkdownCopyToolbar()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasCopyToolbar);

    internal void ApplyMarkdownSyntaxHighlightingForCheck()
    {
        foreach (var viewer in MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
        {
            MarkdownMessageRenderer.ApplySyntaxHighlighting(viewer);
        }
    }

    internal string ActiveModelText => ActiveModelLabel.Text ?? "";
    internal int ConversationCount => _chat.Conversations.Count;
    internal int ModelChoiceCount => ModelPicker.ItemCount;
    internal string SelectedModelText => ModelPicker.SelectedItem?.ToString() ?? "";
    internal string ConversationListText => string.Join("\n", ConversationList.Children
        .OfType<Border>()
        .Select(row => (row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault()?.Content?.ToString()));
    internal string ConversationTitleText => TitleLabel.Text ?? "";
    internal int BubbleCount => MessageFlow.Children.OfType<Border>().Count();

    internal void SearchForCheck(string query)
    {
        ConversationSearch.Text = query;
        RefreshConversationList();
    }

    internal bool OpenConversationForCheck(string id)
    {
        var item = ConversationList.Children.OfType<Border>()
            .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal))
            .Select(row => (row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault())
            .FirstOrDefault(button => button is not null);
        if (item is null) return false;
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return _chat.ActiveConversation?.Id == id;
    }

    internal int SessionMenuCount => ConversationList.Children.OfType<Border>()
        .Select(row => row.Child as Grid)
        .Where(grid => grid is not null)
        .Sum(grid => grid!.Children.OfType<Button>().Count(button => button.Classes.Contains("session-menu")));

    internal bool SessionMenuHasAccessibleHitArea(string id)
        => ConversationList.Children.OfType<Border>()
            .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal))
            .SelectMany(row => (row.Child as Grid)?.Children.OfType<Button>() ?? [])
            .Any(button => button.Classes.Contains("session-menu")
                           && button.Width >= 36
                           && button.Height >= 36
                           && button.IsHitTestVisible);

    internal bool SessionHasDeleteMenu(string id)
        => ConversationList.Children.OfType<Border>()
            .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal))
            .SelectMany(row => (row.Child as Grid)?.Children.OfType<Button>() ?? [])
            .Any(button => button.Tag is MenuFlyout flyout
                           && flyout.Items.OfType<MenuItem>().Any(item =>
                               string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal)));

    internal bool DeleteConversationFromMenuForCheck(string id)
    {
        var deleteItem = ConversationList.Children.OfType<Border>()
            .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal))
            .SelectMany(row => (row.Child as Grid)?.Children.OfType<Button>() ?? [])
            .SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? [])
            .FirstOrDefault(item =>
                string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal));
        if (deleteItem is null) return false;

        deleteItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.Conversations.All(summary => summary.Id != id);
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
