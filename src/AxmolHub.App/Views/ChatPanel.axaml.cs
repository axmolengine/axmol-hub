using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
    /// <summary>How many of the newest assistant turns are rendered as full Markdown. Older turns fall back
    /// to plain selectable text. Every Markdown turn carries a MarkdownScrollViewer plus an AvaloniaEdit
    /// highlighter, so an unbounded count is what makes a long session expensive; bounding it keeps the
    /// heavy controls proportional to what a user actually reads rather than to the session's total length.
    /// The limit is high enough that short sessions (all of the self-check fixtures) render everything.</summary>
    private const int EagerMarkdownLimit = 40;

    /// <summary>How close to the bottom counts as "the user is at the bottom". A few pixels of slack stop
    /// sub-pixel scroll rounding from flipping the state on every scroll event.</summary>
    private const double StickEpsilon = 6;

    private readonly ChatWorkspace _chat;
    private CancellationTokenSource? _send;
    private bool _ready;
    private bool _stickToBottom = true;

    /// <summary>The conversation whose turns <see cref="MessageFlow"/> currently shows, and how many of its
    /// visible turns are already rendered. Together they let <see cref="RenderMessages"/> append only what is
    /// new instead of tearing down and rebuilding the whole flow on every change.</summary>
    private string? _renderedConversationId;
    private int _renderedCount;

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
        ScrollToBottomButton.Click += (_, _) => ScrollToEnd();
        MessageScroller.ScrollChanged += (_, _) => UpdateScrollAffordance();

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

    // ───────────────────────── Session list ─────────────────────────

    private void RefreshConversationList()
    {
        if (_chat is null) return;
        var query = (ConversationSearch.Text ?? "").Trim();
        var activeId = _chat.ActiveConversation?.Id;
        var matches = _chat.Conversations
            .Where(summary => query.Length == 0 || summary.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        ConversationList.Children.Clear();

        // Time buckets are computed against the local date so "today" means the user's today. Pinned sessions
        // get their own bucket above the rest and are excluded from the time buckets.
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var yesterdayStart = todayStart.AddDays(-1);
        var weekStart = todayStart.AddDays(-7);

        AddGroup("PinConversation", matches.Where(summary => summary.Pinned));
        AddGroup("GroupToday", matches.Where(summary => !summary.Pinned && summary.UpdatedAt >= todayStart));
        AddGroup("GroupYesterday", matches.Where(summary => !summary.Pinned && summary.UpdatedAt >= yesterdayStart && summary.UpdatedAt < todayStart));
        AddGroup("GroupPrevious7Days", matches.Where(summary => !summary.Pinned && summary.UpdatedAt >= weekStart && summary.UpdatedAt < yesterdayStart));
        AddGroup("GroupOlder", matches.Where(summary => !summary.Pinned && summary.UpdatedAt < weekStart));

        void AddGroup(string headerKey, IEnumerable<ConversationSummary> items)
        {
            var list = items.ToArray();
            if (list.Length == 0) return;
            ConversationList.Children.Add(new TextBlock
            {
                Text = HubStrings.Get(headerKey),
                Classes = { "group-header" },
            });
            foreach (var summary in list) ConversationList.Children.Add(BuildSessionRow(summary, activeId));
        }

        if (matches.Length == 0)
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

    private Border BuildSessionRow(ConversationSummary summary, string? activeId)
    {
        var title = summary.Title.Length > 0 ? summary.Title : HubStrings.Get("NewConversation");
        var row = new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
        };
        row.Classes.Add("session-row");
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,42") };
        var item = new Button
        {
            Content = title,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0),
        };
        ToolTip.SetTip(item, title);
        item.Classes.Add("session");
        if (summary.Id == activeId) item.Classes.Add("active");
        item.Tag = summary.Id;
        item.Click += (_, _) =>
        {
            if (_send is not null) _send.Cancel();
            _chat.OpenConversation(summary.Id);
        };
        var menuButton = new Button
        {
            Content = "⋯",
            Width = 40,
            Height = 38,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 18,
        };
        menuButton.Classes.Add("session-menu");
        ToolTip.SetTip(menuButton, HubStrings.Get("ConversationActions"));

        var menu = new MenuFlyout();
        var renameItem = new MenuItem { Header = HubStrings.Get("RenameConversation") };
        renameItem.Click += (_, _) => _ = RenameConversationAsync(summary.Id, title);
        menu.Items.Add(renameItem);

        var pinItem = new MenuItem { Header = HubStrings.Get(summary.Pinned ? "UnpinConversation" : "PinConversation") };
        pinItem.Click += (_, _) => _chat.SetPinned(summary.Id, !summary.Pinned);
        menu.Items.Add(pinItem);

        var deleteItem = new MenuItem { Header = HubStrings.Get("DeleteConversation") };
        deleteItem.Click += (_, _) => _chat.DeleteConversation(summary.Id);
        menu.Items.Add(deleteItem);

        menuButton.Click += (_, _) => menu.ShowAt(menuButton);
        menuButton.Tag = menu;

        layout.Children.Add(item);
        Grid.SetColumn(menuButton, 1);
        layout.Children.Add(menuButton);
        row.Child = layout;
        row.Tag = summary.Id;
        return row;
    }

    private async Task RenameConversationAsync(string id, string currentTitle)
    {
        var title = await PromptWindow.ShowAsync(GetOwner(), HubStrings.Get("RenameConversationTitle"), currentTitle);
        if (title is null) return;
        _chat.RenameConversation(id, title);
    }

    // ───────────────────────── Message flow ─────────────────────────

    /// <summary>
    /// Repaints the message flow from the active conversation. Appends only the turns that are new since the
    /// last render and rebuilds only when the conversation changed or shrank (a delete, or an edit that
    /// truncated the tail) — rebuilding a long session on every unrelated change is what the incremental path
    /// exists to avoid.
    /// </summary>
    private void RenderMessages()
    {
        if (_chat is null) return;
        // While a reply is streaming the live bubbles own the flow; a reload here would throw them away.
        if (_send is not null) return;

        var conversation = _chat.ActiveConversation;
        var visible = new List<(int Index, ChatTurn Turn)>();
        if (conversation is not null)
        {
            for (var i = 0; i < conversation.Messages.Count; i++)
            {
                if (conversation.Messages[i].Role != ChatRoles.System) visible.Add((i, conversation.Messages[i]));
            }
        }

        if (visible.Count == 0)
        {
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation?.Id;
            _renderedCount = 0;
            EmptyHint.IsVisible = true;
            UpdateScrollAffordance();
            return;
        }

        if (_renderedConversationId != conversation!.Id || visible.Count < _renderedCount)
        {
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation.Id;
            _renderedCount = 0;
        }

        var lastIndex = conversation.Messages.Count - 1;
        for (var i = _renderedCount; i < visible.Count; i++)
        {
            var (index, turn) = visible[i];
            AppendRenderedTurn(index, turn, isLast: index == lastIndex, markdown: i >= visible.Count - EagerMarkdownLimit);
        }

        EmptyHint.IsVisible = false;
        UpdateScrollAffordance();
    }

    /// <summary>Forces a full rebuild on the next <see cref="RenderMessages"/> — used after an edit or a
    /// regenerate shortened the history, where the visible prefix no longer matches the stored turns.</summary>
    private void ForceRebuildMessages()
    {
        _renderedConversationId = null;
        _renderedCount = 0;
        RenderMessages();
    }

    private void AppendRenderedTurn(int index, ChatTurn turn, bool isLast, bool markdown)
    {
        var fromUser = turn.Role == ChatRoles.User;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = turn.Text, TextWrapping = TextWrapping.Wrap });

        var row = BuildBubble(
            fromUser ? HubStrings.Get("You") : HubStrings.Get("Assistant"),
            body, fromUser, index, turn.Role, turn.Text, isLast);
        MessageFlow.Children.Add(row);
        _renderedCount++;

        // User text is plain by nature; only assistant turns carry Markdown worth rendering.
        if (!fromUser && markdown && turn.Text.Length > 0)
            MarkdownMessageRenderer.RenderInto(body, turn.Text);
    }

    private void AppendPlainBubble(string speaker, string text, bool fromUser)
    {
        EmptyHint.IsVisible = false;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        MessageFlow.Children.Add(BuildBubble(speaker, body, fromUser, null, fromUser ? ChatRoles.User : ChatRoles.Assistant, text, false));
        _renderedCount++;
        ScrollToEnd();
    }

    private void AppendStreamingBubble(StackPanel body)
    {
        EmptyHint.IsVisible = false;
        MessageFlow.Children.Add(BuildBubble(
            HubStrings.Get("Assistant"), body, fromUser: false, index: null, role: ChatRoles.Assistant, text: "", isLast: true));
        _renderedCount++;
        ScrollToEnd();
    }

    /// <summary>Builds one message bubble. A null <paramref name="index"/> (the live streaming bubble) carries
    /// no action bar: there is nothing stable to act on until the turn is persisted.</summary>
    private Border BuildBubble(string speaker, Control body, bool fromUser, int? index, string role, string text, bool isLast)
    {
        var label = new TextBlock { Text = speaker, FontSize = 11, FontWeight = FontWeight.SemiBold };
        label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(fromUser ? "Hub.AccentBorder" : "Hub.TextSecondary"));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(label);
        if (index is { } messageIndex)
        {
            var actions = BuildActionBar(messageIndex, role, text, isLast);
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
        }

        var bubble = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 9),
            MaxWidth = 780,
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = new StackPanel { Spacing = 4, Children = { header, body } },
        };
        bubble.Bind(Border.BackgroundProperty, new DynamicResourceExtension(fromUser ? "Hub.Surface" : "Hub.SurfaceRaised"));
        bubble.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Hub.BorderSubtle"));
        bubble.Classes.Add("message-row");
        return bubble;
    }

    private Control BuildActionBar(int index, string role, string text, bool isLast)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        bar.Children.Add(ActionButton("CopyMessage", () => CopyToClipboard(text)));

        if (role == ChatRoles.User)
        {
            bar.Children.Add(ActionButton("EditMessage", () => _ = EditMessageAsync(index, text)));
        }
        else if (isLast)
        {
            bar.Children.Add(ActionButton("RegenerateMessage", () => _ = RegenerateAsync()));
            bar.Children.Add(ActionButton("ContinueReply", () => _ = ContinueAsync()));
        }

        bar.Children.Add(ActionButton("DeleteMessage", () => _ = DeleteMessageAsync(index)));
        return bar;
    }

    private static Button ActionButton(string textKey, Action onClick)
    {
        var button = new Button { Content = HubStrings.Get(textKey) };
        button.Classes.Add("message-action");
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        button.Click += (_, _) => onClick();
        return button;
    }

    private void AppendNotice(string text)
    {
        var notice = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        notice.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
        MessageFlow.Children.Add(notice);
        ScrollToEnd();
    }

    // ───────────────────────── Actions ─────────────────────────

    private Window? GetOwner() => TopLevel.GetTopLevel(this) as Window;

    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var data = new DataTransfer();
        data.Add(item);
        _ = clipboard.SetDataAsync(data);
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

        AppendPlainBubble(HubStrings.Get("You"), text, fromUser: true);
        await StreamReplyAsync(token => _chat.SendAsync(text, token));
    }

    private async Task RegenerateAsync()
    {
        if (_send is not null) return;
        if (_chat.ActiveConversation is null || _chat.SelectedChatModel is null) return;
        if (!_chat.Regenerate()) return;

        ForceRebuildMessages();
        await StreamReplyAsync(token => _chat.ResendAsync(token));
    }

    private async Task ContinueAsync()
    {
        if (_send is not null) return;
        if (_chat.ActiveConversation is null || _chat.SelectedChatModel is null) return;

        AppendPlainBubble(HubStrings.Get("You"), HubStrings.Get("ContinueInstruction"), fromUser: true);
        await StreamReplyAsync(token => _chat.ContinueAsync(HubStrings.Get("ContinueInstruction"), token));
    }

    private async Task EditMessageAsync(int index, string text)
    {
        if (_send is not null) return;

        var edited = await PromptWindow.ShowAsync(GetOwner(), HubStrings.Get("EditMessageTitle"), text);
        if (edited is null || edited.Trim().Length == 0) return;

        // Everything after the edited turn is dropped, so ask before doing something irreversible.
        var confirm = await HubDialog.ShowAsync(
            GetOwner(), HubStrings.Get("EditMessage"), HubStrings.Get("EditMessageConfirm"),
            HubDialogButtons.OkCancel, danger: true);
        if (confirm != HubDialogResult.Ok) return;

        if (!_chat.EditAndResend(index, edited)) return;
        ForceRebuildMessages();
        await StreamReplyAsync(token => _chat.ResendAsync(token));
    }

    private async Task DeleteMessageAsync(int index)
    {
        var conversation = _chat.ActiveConversation;
        if (conversation is null) return;

        var confirm = await HubDialog.ShowAsync(
            GetOwner(), HubStrings.Get("DeleteMessage"), HubStrings.Get("DeleteMessageConfirm"),
            HubDialogButtons.OkCancel, danger: true);
        if (confirm != HubDialogResult.Ok) return;

        _chat.RemoveTurn(conversation.Id, index);
    }

    /// <summary>Drives one streamed reply: appends a live assistant bubble, appends chunks as they arrive, and
    /// rebuilds from the persisted history when the stream ends so the finished turn gains its action bar.</summary>
    private async Task StreamReplyAsync(Func<CancellationToken, IAsyncEnumerable<string>> start)
    {
        _send = new CancellationTokenSource();
        var token = _send.Token;

        var body = new StackPanel { Spacing = 8 };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(preview);
        AppendStreamingBubble(body);

        var buffer = new StringBuilder();
        SendButton.Content = HubStrings.Get("Stop");
        try
        {
            await foreach (var chunk in start(token).ConfigureAwait(true))
            {
                buffer.Append(chunk);
                preview.Text = buffer.ToString();
                ScrollToEndIfSticky();
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
            ForceRebuildMessages();
            UpdateConversationTitle();
            RefreshConversationList();
            ScrollToEnd();
        }
    }

    // ───────────────────────── Scrolling ─────────────────────────

    private void ScrollToEnd()
    {
        _stickToBottom = true;
        Dispatcher.UIThread.Post(
            () => { MessageScroller.ScrollToEnd(); UpdateScrollAffordance(); },
            DispatcherPriority.Background);
    }

    private void ScrollToEndIfSticky()
    {
        if (!_stickToBottom) return;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void UpdateScrollAffordance()
    {
        var extent = MessageScroller.Extent.Height;
        var viewport = MessageScroller.Viewport.Height;
        var atBottom = MessageScroller.Offset.Y + viewport >= extent - StickEpsilon;
        _stickToBottom = atBottom;
        ScrollToBottomButton.IsVisible = extent > viewport + StickEpsilon && !atBottom;
    }

    // ───────────────────────── Self-check hooks ─────────────────────────

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
    internal string GroupHeaderText => string.Join("\n", ConversationList.Children
        .OfType<TextBlock>()
        .Where(block => block.Classes.Contains("group-header"))
        .Select(block => block.Text));
    internal string ConversationTitleText => TitleLabel.Text ?? "";
    internal int BubbleCount => MessageFlow.Children.OfType<Border>().Count();

    internal int MessageActionCount => MessageFlow.GetLogicalDescendants().OfType<Button>()
        .Count(button => button.Classes.Contains("message-action"));

    internal bool ScrollToBottomVisible => ScrollToBottomButton.IsVisible;

    /// <summary>The first rendered bubble, so a check can prove a reload reuses it instead of rebuilding the
    /// whole flow (the incremental path's whole point).</summary>
    internal object? FirstBubbleForCheck => MessageFlow.Children.OfType<Border>().FirstOrDefault();

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
        => SessionMenuButtons(id).Any(button => button.Width >= 36 && button.Height >= 36 && button.IsHitTestVisible);

    internal bool SessionHasDeleteMenu(string id)
        => SessionMenuItems(id).Any(item =>
            string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal));

    internal bool SessionHasRenameMenu(string id)
        => SessionMenuItems(id).Any(item =>
            string.Equals(item.Header?.ToString(), HubStrings.Get("RenameConversation"), StringComparison.Ordinal));

    internal bool SessionHasPinMenu(string id)
        => SessionMenuItems(id).Any(item =>
            string.Equals(item.Header?.ToString(), HubStrings.Get("PinConversation"), StringComparison.Ordinal)
            || string.Equals(item.Header?.ToString(), HubStrings.Get("UnpinConversation"), StringComparison.Ordinal));

    private IEnumerable<Button> SessionMenuButtons(string id) => ConversationList.Children.OfType<Border>()
        .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal))
        .SelectMany(row => (row.Child as Grid)?.Children.OfType<Button>() ?? [])
        .Where(button => button.Classes.Contains("session-menu"));

    private IEnumerable<MenuItem> SessionMenuItems(string id) => SessionMenuButtons(id)
        .SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? []);

    internal bool DeleteConversationFromMenuForCheck(string id)
    {
        var deleteItem = SessionMenuItems(id)
            .FirstOrDefault(item =>
                string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal));
        if (deleteItem is null) return false;

        deleteItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.Conversations.All(summary => summary.Id != id);
    }

    internal void RenameConversationForCheck(string id, string title)
        => _chat.RenameConversation(id, title);

    internal void TogglePinForCheck(string id)
    {
        var summary = _chat.Conversations.FirstOrDefault(candidate => candidate.Id == id);
        if (summary is not null) _chat.SetPinned(id, !summary.Pinned);
    }

    internal bool BubbleHasAction(int visibleIndex, string textKey)
    {
        var bubble = MessageFlow.Children.OfType<Border>().ElementAtOrDefault(visibleIndex);
        return bubble is not null && bubble.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action")
                           && string.Equals(button.Content?.ToString(), HubStrings.Get(textKey), StringComparison.Ordinal));
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
