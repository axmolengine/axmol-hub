using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// The visible half of the AI assistant: a full page hosting the conversation picker, the message flow, and
/// the input row. All state and all calls live in <see cref="ChatWorkspace"/>; this class turns events into
/// UI and UI into calls, and nothing else.
///
/// Provider **management** is not here — it belongs to the settings page (Settings ▸ Models). This page only
/// reports which model the next message will use; the read-only label is the whole of its provider surface.
///
/// Copy is written imperatively (via <see cref="HubStrings"/>) rather than with <c>{DynamicResource}</c>,
/// because most of it is composed with values (the active model, error text). <see cref="Reload"/>
/// recomputes every such label so it follows a language switch — the same discipline the settings page uses
/// for its code-written copy.
/// </summary>
public partial class ChatPanel : UserControl
{
    private readonly ChatWorkspace _chat;
    private CancellationTokenSource? _send;

    /// <summary>Guards the pickers against re-entrancy: setting ItemsSource/SelectedItem fires
    /// SelectionChanged, and acting on that would reload conversations in a loop.</summary>
    private bool _ready;

    /// <summary>For the XAML loader and design-time preview (missing it raises AVLN3001).</summary>
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

        ConversationPicker.SelectionChanged += (_, _) =>
        {
            if (!_ready) return;
            if (ConversationPicker.SelectedItem is ConversationSummary summary) _chat.OpenConversation(summary.Id);
        };

        NewConversationButton.Click += (_, _) => _chat.StartConversation();
        DeleteConversationButton.Click += (_, _) => DeleteSelectedConversation();
        SendButton.Click += (_, _) => _ = SendAsync();

        // Enter sends, Shift+Enter inserts a newline. Handling KeyDown before the TextBox consumes Enter is
        // what makes the single-line "type and send" flow work with AcceptsReturn=true.
        InputBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            await SendAsync();
        };

        Reload();
    }

    /// <summary>Recomputes all code-written copy and rebuilds the pickers. Safe to call repeatedly.</summary>
    public void Reload()
    {
        if (_chat is null) return;
        _ready = false;

        TitleLabel.Text = HubStrings.Get("Assistant");
        NewConversationButton.Content = HubStrings.Get("NewConversation");
        DeleteConversationButton.Content = HubStrings.Get("DeleteConversation");
        SendButton.Content = _send is null ? HubStrings.Get("Send") : HubStrings.Get("Stop");
        InputBox.PlaceholderText = HubStrings.Get("InputPlaceholder");
        EmptyHint.Text = HubStrings.Get("AssistantEmpty");
        UpdateActiveModel();

        RefreshConversationItems();

        RenderMessages();
        _ready = true;
    }

    /// <summary>
    /// Repaints the read-only "which model answers next" line. Provider configuration lives in Settings, so
    /// this label is the only place the user can see the current choice without leaving the conversation —
    /// and getting it wrong is silent (the wrong model would just answer), which is why the self-check reads
    /// it back.
    /// </summary>
    private void UpdateActiveModel()
    {
        ActiveModelLabel.Text = _chat.ActiveProvider is { } provider
            ? string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ActiveModelFormat"),
                provider.Name,
                provider.Model)
            : HubStrings.Get("NoProvider");
    }

    // ───────────────────────── Conversations ─────────────────────────

    private void DeleteSelectedConversation()
    {
        if (ConversationPicker.SelectedItem is not ConversationSummary summary) return;
        _chat.DeleteConversation(summary.Id);
    }

    // ───────────────────────── Sending ─────────────────────────

    private async Task SendAsync()
    {
        // While streaming, the same button is "Stop".
        if (_send is not null)
        {
            _send.Cancel();
            return;
        }

        var text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        if (_chat.ActiveProvider is null)
        {
            AppendNotice(HubStrings.Get("NoProvider"));
            return;
        }

        if (_chat.ActiveConversation is null) _chat.StartConversation();
        InputBox.Text = "";

        var userTurn = ChatTurn.User(text);
        AppendBubble(HubStrings.Get("You"), userTurn.Text, fromUser: true);

        // The assistant bubble is created empty and filled chunk by chunk; its TextBlock is captured so the
        // stream can append without rebuilding the flow (and losing the scroll position) on every token.
        var assistantText = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "" };
        AppendBubble(HubStrings.Get("Assistant"), assistantText, fromUser: false);

        _send = new CancellationTokenSource();
        SendButton.Content = HubStrings.Get("Stop");
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
            // The conversation list gained a turn (and possibly a title) — repaint the picker only, not the
            // whole flow, so the user's scroll position survives.
            RefreshConversationPicker();
        }
    }

    // ───────────────────────── Rendering ─────────────────────────

    /// <summary>Rebuilds the whole message flow from the active conversation.</summary>
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

    /// <summary>
    /// Adds one message bubble. The speaker label is prefixed rather than using left/right alignment only,
    /// so a screenshot makes "who said what" unambiguous (and the checks can read it back).
    /// </summary>
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
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(10, 8),
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

    /// <summary>
    /// Looks up a themed brush for code-built content. <c>FindResource</c> returns Avalonia's
    /// <c>UnsetValue</c> when the key is missing (a hard cast then throws), and there is no XAML here to
    /// resolve <c>{DynamicResource}</c>, so the lookup is guarded and a missing token degrades to the
    /// default brush rather than crashing the drawer.
    /// </summary>
    private static IBrush? ThemeBrush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) ? value as IBrush : null;

    private void RefreshConversationPicker()
    {
        var wasReady = _ready;
        _ready = false;
        RefreshConversationItems();
        _ready = wasReady;
    }

    /// <summary>
    /// Rebuilds the conversation picker and selects the active conversation.
    ///
    /// <see cref="ChatWorkspace.Conversations"/> mints a **fresh list of fresh summary objects** on every call,
    /// so the selected item must be taken from the very array assigned to <c>ItemsSource</c> — assigning an
    /// equivalent-but-distinct instance leaves the ComboBox with no match and it silently clears the selection.
    /// </summary>
    private void RefreshConversationItems()
    {
        var items = _chat.Conversations.ToArray();
        ConversationPicker.ItemsSource = items;
        ConversationPicker.SelectedItem = _chat.ActiveConversation is { } active
            ? Array.Find(items, summary => summary.Id == active.Id)
            : null;
    }

    /// <summary>Scrolls the message flow to the newest content. Avalonia's ScrollViewer has
    /// <c>ScrollToEnd</c>, but the extent only updates after a layout pass — posting at Background priority
    /// is what makes it land on the freshly appended bubble.</summary>
    private void ScrollToEnd() => Dispatcher.UIThread.Post(
        () => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);

    /// <summary>Reads the message flow back as text — used by the shell self-check to assert the streamed
    /// reply really landed in the UI.</summary>
    internal string FlowText => string.Join("\n", MessageFlow.Children
        .OfType<Border>()
        .SelectMany(bubble => (bubble.Child as StackPanel)?.Children.OfType<TextBlock>() ?? [])
        .Select(block => block.Text));

    // ── Verification hooks (used by the --verify-shell self-check; not part of the product surface) ──

    /// <summary>The read-only "which model answers next" line, as displayed. This page no longer owns a
    /// provider picker, so this is what proves the active provider actually reached the page.</summary>
    internal string ActiveModelText => ActiveModelLabel.Text ?? "";

    /// <summary>The conversation picker's selected item as displayed.</summary>
    internal string ConversationPickerText => ConversationPicker.SelectedItem?.ToString() ?? "";

    /// <summary>Number of conversations in the picker.</summary>
    internal int ConversationCount => ConversationPicker.ItemCount;

    /// <summary>Number of message bubbles currently shown (excludes the empty-state hint and notices).</summary>
    internal int BubbleCount => MessageFlow.Children.OfType<Border>().Count();

    /// <summary>Types into the input box and runs the real send path (button → SendAsync), so the check
    /// exercises the same code the user's click does.</summary>
    internal async Task SendForCheckAsync(string text)
    {
        InputBox.Text = text;
        await SendAsync();
    }
}
