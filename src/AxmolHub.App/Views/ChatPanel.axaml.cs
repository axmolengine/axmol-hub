using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using AvaloniaEdit;
using Markdown.Avalonia;
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

    /// <summary>Raised when the active conversation or its title may have changed, so the shell can update
    /// its top-bar title (the panel no longer owns a title of its own).</summary>
    internal event Action? ConversationStateChanged;

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
        ModelPicker.SelectionChanged += (_, _) =>
        {
            if (!_ready || ModelPicker.SelectedItem is not ChatWorkspace.ChatModelOption choice) return;
            _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);
            AppendNotice(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ModelChangedFormat"),
                choice.Provider.Name + " · " + choice.ModelName), danger: false);
        };
        SendButton.Click += (_, _) => _ = SendAsync();
        ScrollToBottomButton.Click += (_, _) => ScrollToEnd();
        MessageScroller.ScrollChanged += (_, _) => UpdateScrollAffordance();
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UpdateSendState();
        };
        InputBox.GotFocus += (_, _) => SetComposerFocus(true);
        InputBox.LostFocus += (_, _) => SetComposerFocus(false);

        // KeyDown is registered for both tunnel and bubble. Subscribe on the tunnel: it runs before the
        // TextBox's own Enter handling, which (with AcceptsReturn=true) inserts a newline and marks the
        // bubble handled, silently swallowing a normal KeyDown subscriber.
        InputBox.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            // While an IME composition is open, Enter arrives as Key.ImeProcessed (confirm candidate), so it
            // falls through; only a bare Enter sends. Shift+Enter is left alone so it still inserts a newline.
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            _ = SendAsync();
        }, RoutingStrategies.Tunnel);

        Reload();
    }

    public void Reload()
    {
        if (_chat is null) return;
        _ready = false;

        InputBox.PlaceholderText = HubStrings.Get("InputPlaceholder");
        GreetingLabel.Text = HubStrings.Get("AssistantGreeting");
        GreetingSubtitle.Text = HubStrings.Get("AssistantGreetingSubtitle");
        BuildSuggestionChips();
        UpdateSendState();

        RefreshModelPicker();
        RenderMessages();
        _ready = true;
    }

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

    // ───────────────────────── Empty state ─────────────────────────

    private void BuildSuggestionChips()
    {
        SuggestionChips.Children.Clear();
        foreach (var key in new[]
                 {
                     "SuggestionCreateProject", "SuggestionCheckEnvironment",
                     "SuggestionMigrate", "SuggestionPhysics",
                 })
        {
            var text = HubStrings.Get(key);
            var chip = new Button
            {
                Content = text,
                Classes = { "suggestion" },
            };
            chip.Click += (_, _) => _ = SendSuggestionAsync(text);
            SuggestionChips.Children.Add(chip);
        }
    }

    private async Task SendSuggestionAsync(string text)
    {
        InputBox.Text = text;
        await SendAsync();
    }

    // ───────────────────────── Messages ─────────────────────────

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
            EmptyState.IsVisible = true;
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

        EmptyState.IsVisible = false;
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

        MessageFlow.Children.Add(BuildMessageRow(fromUser, body, index, turn.Role, turn.Text, isLast));
        _renderedCount++;

        // User text is plain by nature; only assistant turns carry Markdown worth rendering.
        if (!fromUser && markdown && turn.Text.Length > 0)
            MarkdownMessageRenderer.RenderInto(body, turn.Text);
    }

    private void AppendPlainBubble(string text, bool fromUser)
    {
        EmptyState.IsVisible = false;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        MessageFlow.Children.Add(BuildMessageRow(fromUser, body, null,
            fromUser ? ChatRoles.User : ChatRoles.Assistant, text, false));
        _renderedCount++;
        ScrollToEnd();
    }

    private void AppendStreamingBubble(StackPanel body)
    {
        EmptyState.IsVisible = false;
        MessageFlow.Children.Add(BuildMessageRow(false, body, null,
            ChatRoles.Assistant, "", isLast: true));
        _renderedCount++;
        ScrollToEnd();
    }

    /// <summary>
    /// Builds one message row. User rows: a Grid (full-width for hover) carrying a right-aligned pill.
    /// Assistant rows: a borderless Border carrying plain text. Both carry the message-row class and,
    /// when <paramref name="index"/> is non-null, a hover-revealed action row.
    /// A null <paramref name="index"/> (the live streaming bubble / just-sent user pill) carries no
    /// action bar: there is nothing stable to act on until the turn is persisted.
    /// </summary>
    private Control BuildMessageRow(bool fromUser, Control body, int? index, string role, string text, bool isLast)
    {
        var column = new StackPanel { Spacing = 6 };

        if (fromUser)
        {
            column.Children.Add(new Border
            {
                Classes = { "user-pill" },
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = body,
            });
        }
        else
        {
            column.Children.Add(body);
        }

        if (index is { } messageIndex)
        {
            var actions = BuildActionBar(messageIndex, role, text, isLast);
            actions.HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            column.Children.Add(actions);
        }

        if (fromUser)
        {
            var grid = new Grid();
            grid.Classes.Add("message-row");
            grid.Children.Add(column);
            return grid;
        }

        return new Border
        {
            Classes = { "assistant-msg", "message-row" },
            Child = column,
        };
    }

    private Control BuildActionBar(int index, string role, string text, bool isLast)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Classes.Add("message-actions");

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

    /// <summary>Appends a one-line notice: neutral (model changed, cancellation) or danger (errors).</summary>
    private void AppendNotice(string text, bool danger)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Classes.Add("notice");
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.MaxWidth = 820;
        if (danger) row.Classes.Add("danger");

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry("Hub.Icon.Notice"),
        };
        icon.Classes.Add("notice-icon");
        // The row declares "Auto,*", and a child without an explicit column lands in column 0 — both of
        // them there meant the text sat on top of the icon, which in a real run read like "the tip is
        // smudged over something". Icon owns column 0, text column 1.
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var label = new TextBlock { Text = text };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        MessageFlow.Children.Add(row);
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

    /// <summary>
    /// Keeps the round button in step with the composer: the same button sends or stops, and it stays
    /// greyed out while the box is empty so "there is nothing to send" is visible before the click rather than
    /// after it.
    /// </summary>
    private void UpdateSendState()
    {
        SendStateUpdates++;
        var streaming = _send is not null;
        SendButton.IsEnabled = streaming || (InputBox.Text ?? "").Trim().Length > 0;
        SendButton.Content = BuildSendIcon(streaming);
        ToolTip.SetTip(SendButton, HubStrings.Get(streaming ? "Stop" : "Send"));
    }

    /// <summary>Builds the arrow (send) or square (stop) glyph. Colours are bound as DynamicResource rather
    /// than looked up once because this runs from the constructor, before the control is attached — a brush
    /// captured there comes back null and the glyph would be invisible.</summary>
    private static Control BuildSendIcon(bool streaming)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
        };
        path.Data = ThemeGeometry(streaming ? "Hub.Icon.Stop" : "Hub.Icon.Send");

        if (streaming)
        {
            // The stop square is solid; the fill is what makes it read differently from the arrow.
            path.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new DynamicResourceExtension("Hub.TextOnAccent"));
            return path;
        }

        path.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextOnAccent"));
        path.StrokeThickness = 2;
        path.StrokeLineCap = PenLineCap.Round;
        path.StrokeJoin = PenLineJoin.Round;
        return path;
    }

    private static Geometry? ThemeGeometry(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) ? value as Geometry : null;

    /// <summary>Highlights the composer frame while the input has focus so the whole rounded box reads as the
    /// thing being typed into.</summary>
    private void SetComposerFocus(bool focused) => ComposerFrame.Classes.Set("focused", focused);

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
            AppendNotice(HubStrings.Get("NoAvailableChatModels"), danger: true);
            return;
        }

        if (_chat.ActiveConversation is null) _chat.StartConversation();
        InputBox.Text = "";

        AppendPlainBubble(text, fromUser: true);
        ConversationStateChanged?.Invoke();
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

        AppendPlainBubble(HubStrings.Get("ContinueInstruction"), fromUser: true);
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
        UpdateSendState();
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
            AppendNotice(HubStrings.Get("ChatCancelled"), danger: false);
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatFailed") + ex.Message, danger: true);
        }
        finally
        {
            _send.Dispose();
            _send = null;
            UpdateSendState();
            ForceRebuildMessages();
            ConversationStateChanged?.Invoke();
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

    /// <summary>Message rows are controls carrying the message-row class (user rows are Grids, assistant
    /// rows are Borders).</summary>
    private IEnumerable<Control> MessageRows
        => MessageFlow.Children.Where(child => child.Classes.Contains("message-row"));

    internal string FlowText
    {
        get
        {
            // Every text block (plain bodies plus the Markdown viewer's CTextBlock descendants) in order…
            var blocks = MessageRows
                .SelectMany(row => row.GetLogicalDescendants().OfType<TextBlock>())
                .Select(block => block.Text ?? "")
                .Where(text => text.Length > 0);

            // …plus the raw markdown carried as each viewer's Tag, so fenced code text is present too.
            var rawMarkdown = MessageRows
                .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
                .Select(viewer => viewer.Tag as string ?? "")
                .Where(text => text.Length > 0);

            return string.Join("\n", blocks.Concat(rawMarkdown));
        }
    }

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
        => MessageFlow.GetLogicalDescendants().OfType<TextEditor>()
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

    /// <summary>The selected model formatted as text — the model no longer has a dedicated label, but the
    /// composer chip and this value still expose it.</summary>
    internal string ActiveModelText => _chat.SelectedChatModel is { } choice
        ? string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("ActiveModelFormat"),
            choice.Provider.Name,
            choice.ModelName)
        : HubStrings.Get("NoAvailableChatModels");

    internal int ModelChoiceCount => ModelPicker.ItemCount;
    internal string SelectedModelText => ModelPicker.SelectedItem?.ToString() ?? "";

    internal int BubbleCount => MessageRows.Count();

    internal int MessageActionCount => MessageFlow.GetLogicalDescendants().OfType<Button>()
        .Count(button => button.Classes.Contains("message-action"));

    internal bool ScrollToBottomVisible => ScrollToBottomButton.IsVisible;

    /// <summary>The first rendered message row, so a check can prove a reload reuses it instead of rebuilding
    /// the whole flow (the incremental path's whole point).</summary>
    internal object? FirstBubbleForCheck => MessageRows.FirstOrDefault();

    /// <summary>Appends a notice row through the real path, so the row's layout can be read back off the
    /// controls instead of inferred from code.</summary>
    internal void AppendNoticeForCheck(string text, bool danger = false) => AppendNotice(text, danger);

    /// <summary>The last notice row's column layout — (icon column, text column, declared column count) —
    /// or null when the flow does not end with a notice row. Notice rows are not message rows, so this
    /// never disturbs the bubble assertions.</summary>
    internal (int IconColumn, int TextColumn, int Columns)? LastNoticeLayoutForCheck
    {
        get
        {
            if (MessageFlow.Children.Count == 0
                || MessageFlow.Children[^1] is not Grid row
                || !row.Classes.Contains("notice")
                || row.Children.Count < 2)
            {
                return null;
            }

            return (Grid.GetColumn(row.Children[0]), Grid.GetColumn(row.Children[1]), row.ColumnDefinitions.Count);
        }
    }

    // ── Composer (send button state, chip, focus highlight) ──
    internal void SetComposerFocusForCheck(bool focused) => SetComposerFocus(focused);
    internal bool ComposerFocusedForCheck => ComposerFrame.Classes.Contains("focused");
    internal IBrush? ComposerBorderBrushForCheck => ComposerFrame.BorderBrush;
    internal bool SendButtonEnabledForCheck => SendButton.IsEnabled;
    internal string InputTextForCheck => InputBox.Text ?? "";

    /// <summary>How many times the send button's state has been recomputed. A check asserts it grows when the
    /// input text changes, which is what proves the change notification is actually wired up.</summary>
    internal int SendStateUpdates { get; private set; }
    internal string SendButtonTooltipForCheck => ToolTip.GetTip(SendButton)?.ToString() ?? "";
    internal bool ModelPickerIsChipForCheck => ModelPicker.Classes.Contains("chip");

    /// <summary>True when the round button currently shows the stop square. Compared by geometry identity
    /// rather than by string, because the icons are resolved from the same cached resource.</summary>
    internal bool SendIconIsStopForCheck
        => SendButton.Content is Avalonia.Controls.Shapes.Path path
           && ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.Stop"));

    internal void SetInputForCheck(string text) => InputBox.Text = text;

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

    /// <summary>Starts a send without awaiting it, so a check can inspect the mid-stream state (the button
    /// becomes a stop icon before the first token arrives).</summary>
    internal Task BeginSendForCheckAsync(string text)
    {
        InputBox.Text = text;
        return SendAsync();
    }

    internal bool BubbleHasAction(int visibleIndex, string textKey)
    {
        var row = MessageRows.ElementAtOrDefault(visibleIndex);
        return row is not null && row.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action")
                           && string.Equals(button.Content?.ToString(), HubStrings.Get(textKey), StringComparison.Ordinal));
    }
}
