using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The card a finished turn leaves in the transcript: how much it changed, which files, and the two things a
/// person wants at that moment — read it properly, or put it back.
///
/// <para>It is part of the transcript rather than a bar somewhere else because that is where the question arises:
/// the reply that changed seven files ends here, and the answer belongs under it rather than in a column the
/// reader has to remember to open. Everything it says is derived from the turn itself (see
/// <see cref="TurnReview"/>), so a conversation reopened a week later shows the same card with the same numbers
/// for the same turn.</para>
///
/// <para>Undo is offered only when the turn is over: a reply still streaming has edits that are not finished, and
/// "put it back" against a half-written turn would restore files this run is about to write again.</para>
/// </summary>
public partial class ChatPanel
{
    /// <summary>How many files are listed before the card offers the rest. Seven files is what the reference
    /// client folds at; the number that matters is that the card stays shorter than the reply it belongs to.</summary>
    private const int ReviewFilesListed = 6;

    /// <summary>Whether the card's file list is showing everything. One card is on screen at a time, so one flag
    /// is enough — the last turn is the only turn a card hangs under.</summary>
    private bool _reviewFilesOpen;

    private Control? _reviewCard;

    /// <summary>The identity the cached card was built for: the turn, how many turns there are, whether its reply
    /// is still arriving, and whether the file list is open. Anything else changing leaves the same card standing —
    /// rebuilding it on every repaint would throw away the focus inside it and make the fold impossible to click.</summary>
    private string? _reviewCardKey;

    /// <summary>The card's own words, for the check that covers it: every line it renders, joined. Empty when no
    /// card is drawn — which is itself the answer for a turn that changed nothing, and for one still arriving.</summary>
    internal string ReviewCardTextForCheck
    {
        get
        {
            var card = ReviewCardFor(_chat?.ActiveConversation);
            return card is null
                ? ""
                : string.Join(" / ", card.GetLogicalDescendants().OfType<TextBlock>()
                    .Select(block => block.Text ?? "")
                    .Where(text => text.Length > 0));
        }
    }

    internal bool ClickReviewForCheck()
    {
        var button = ReviewCardFor(_chat?.ActiveConversation)?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(item => item.Tag as string == "ChatReviewReview");
        if (button is null) return false;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    internal bool ClickReviewFileForCheck(string path)
    {
        var button = ReviewCardFor(_chat?.ActiveConversation)?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(item => item.Tag as string == "ChatReviewFile:" + path);
        if (button is null) return false;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    internal bool ClickReviewToggleForCheck()
    {
        var button = ReviewCardFor(_chat?.ActiveConversation)?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(item => item.Tag as string == "ChatReviewToggle");
        if (button is null) return false;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>The colours the card's two numbers resolve to right now. Read off the rendered control, so a
    /// hard-coded colour would show up as a value that cannot follow the theme at all.</summary>
    internal (Color Plus, Color Minus) ReviewCountColorsForCheck
    {
        get
        {
            var card = ReviewCardFor(_chat?.ActiveConversation);
            Color Find(string prefix)
            {
                var block = card?.GetLogicalDescendants().OfType<TextBlock>()
                    .FirstOrDefault(text => (text.Text ?? "").StartsWith(prefix, StringComparison.Ordinal));
                return block?.Foreground is ISolidColorBrush brush ? brush.Color : default;
            }
            return (Find("+"), Find("−"));
        }
    }

    /// <summary>The card for the current turn, built at most once per identity.</summary>
    private Control? ReviewCardFor(Conversation? conversation)
    {
        if (conversation is null || _chat is null)
        {
            _reviewCard = null;
            _reviewCardKey = null;
            return null;
        }
        var active = _chat.RunFor(conversation.Id) is { Phase: not RunPhase.Completed };
        var key = string.Join(':', conversation.Id, conversation.Messages.Count,
            conversation.Messages.Count > 0 ? conversation.Messages[^1].ChangeId ?? "" : "",
            active ? "1" : "0", _reviewFilesOpen ? "1" : "0");
        if (!string.Equals(key, _reviewCardKey, StringComparison.Ordinal))
        {
            _reviewCardKey = key;
            _reviewCard = BuildReviewCard(conversation);
        }
        return _reviewCard;
    }

    /// <summary>
    /// The card for the turn the transcript ends on, or null when there is nothing to say: no landed file edits, a
    /// reply still arriving, or no session. A turn that only ran commands gets no card — its files carry no
    /// snapshot naming the turn, and a card that counted them would be crediting the assistant with whatever the
    /// command happened to touch.
    /// </summary>
    private Control? BuildReviewCard(Conversation? conversation)
    {
        if (conversation is null || _chat is null) return null;
        if (_chat.RunFor(conversation.Id) is { Phase: not RunPhase.Completed }) return null;
        var review = _chat.TurnReviewFor(conversation);
        if (review.IsEmpty) return null;

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 8),
            Margin = new Thickness(0, 8, 0, 4),
            Tag = "ChatReviewCard",
        };
        card.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("Hub.Surface"));
        card.Bind(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("Hub.BorderSubtle"));

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(ReviewHead(conversation, review));
        foreach (var file in _reviewFilesOpen ? review.Files : review.Files.Take(ReviewFilesListed))
            body.Children.Add(ReviewFileRow(file, selected => OpenTurnReviewFile(conversation, selected)));
        if (review.Files.Count > ReviewFilesListed) body.Children.Add(ReviewToggle(review.Files.Count));
        if (review.RanCommands)
        {
            var note = new TextBlock
            {
                Text = HubStrings.Get("InspectorTurnCommands"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 0, 0),
            };
            note.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextTertiary"));
            body.Children.Add(note);
        }
        card.Child = body;
        return card;
    }

    private Control ReviewHead(Conversation conversation, TurnReview review)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = review.Files.Count == 1
                ? HubStrings.Get("ChatReviewEditedOne")
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatReviewEditedFormat"), review.Files.Count),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextPrimary"));
        title.Children.Add(name);
        title.Children.Add(ReviewCounts(review.Added, review.Removed, review.CountsExact, review.CountsBudgetExceeded));
        grid.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(ReviewAction("ChatReviewUndo", () => _ = UndoTurnAsync(conversation.Id), tag: "ChatReviewUndo"));
        actions.Children.Add(ReviewAction("ChatReviewReview", () => OpenTurnReview(conversation),
            primary: true, tag: "ChatReviewReview"));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private void OpenTurnReview(Conversation? conversation)
    {
        if (conversation is null) return;
        BuildInspectorContent("repo", conversation.Messages.Count - 1, refreshChanges: true);
        _inspector?.SetScope(GitReviewScope.LastTurn);
        OpenInspector?.Invoke("repo", conversation.Messages.Count - 1);
    }

    private void OpenTurnReviewFile(Conversation? conversation, string path)
    {
        if (conversation is null) return;
        BuildInspectorContent("repo", conversation.Messages.Count - 1, refreshChanges: true);
        OpenInspector?.Invoke("repo", conversation.Messages.Count - 1);
        _inspector?.OpenLastTurnFile(path);
    }

    private void RefreshTurnChangesPill()
    {
        var chat = _chat;
        var conversation = chat?.ActiveConversation;
        var active = chat is not null && conversation is not null
            && chat.RunFor(conversation.Id) is { Phase: not RunPhase.Completed };
        var review = active ? chat!.TurnReviewFor(conversation) : TurnReview.Empty;
        TurnChangesPill.IsVisible = active && review.HasRows;
        Grid.SetColumn(ScrollToBottomButton, TurnChangesPill.IsVisible ? 2 : 1);
        ScrollToBottomButton.HorizontalAlignment = TurnChangesPill.IsVisible
            ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        ScrollToBottomButton.Margin = TurnChangesPill.IsVisible ? new Thickness(8, 0, 0, 0) : default;
        if (!TurnChangesPill.IsVisible) return;
        TurnChangesLabel.Text = review.Files.Count == 1 ? HubStrings.Get("ChatTurnFilesChangedOne")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatTurnFilesChangedFormat"), review.Files.Count);
        TurnChangesAdded.Text = review.CountsExact
            ? "+" + review.Added.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : HubStrings.Get(review.CountsBudgetExceeded ? "ChangeCountsBudgetExceeded" : "ChangeCountsUnavailable");
        TurnChangesRemoved.Text = review.CountsExact
            ? "−" + review.Removed.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        ToolTip.SetTip(TurnChangesPill, review.CountsExact ? null : HubStrings.Get(
            review.CountsBudgetExceeded ? "ChangeCountsBudgetDetail" : "InspectorCountsUnavailable"));
    }

    /// <summary>+N −M in the two colours the diff itself uses, so the header and the rows below it speak the same
    /// vocabulary. The minus is a real minus sign, not a hyphen: it is a count, and a hyphen reads as punctuation.</summary>
    private static Control ReviewCounts(int added, int removed, bool exact = true, bool budgetExceeded = true)
    {
        if (!exact) return UnavailableDiffCounts(budgetExceeded);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (added != 0)
        {
            var plus = new TextBlock { Text = "+" + added.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 12 };
            plus.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.SuccessText"));
            row.Children.Add(plus);
        }
        if (removed != 0)
        {
            var minus = new TextBlock { Text = "−" + removed.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 12 };
            minus.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
            row.Children.Add(minus);
        }
        return row;
    }

    private static Control ReviewFileRow(TurnReviewFile file, Action<string> openFile)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 2) };
        var path = new TextBlock
        {
            Text = file.RelativePath,
            FontSize = 12,
            FontFamily = ThemeFont("Hub.Font.Mono"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        ToolTip.SetTip(path, file.RelativePath);
        var fileButton = new Button
        {
            Content = path,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 5),
            Classes = { "chat-review-file" },
            Tag = "ChatReviewFile:" + file.RelativePath,
        };
        fileButton.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("Hub.Link"));
        ToolTip.SetTip(fileButton, file.RelativePath);
        fileButton.Click += (_, _) => openFile(file.RelativePath);
        row.Children.Add(fileButton);
        if (file.Lines is { } lines)
        {
            var counts = ReviewCounts(lines.Added, lines.Removed, lines.Exact);
            Grid.SetColumn(counts, 1);
            row.Children.Add(counts);
        }
        return row;
    }

    /// <summary>The fold itself: one line that says how many are still hidden, and says "fewer" once they are not.
    /// It carries the count because "show more" alone makes a reader open it to find out whether it was worth it.</summary>
    private Control ReviewToggle(int total)
    {
        var hidden = total - ReviewFilesListed;
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = _reviewFilesOpen
                    ? HubStrings.Get("ChatReviewFewer")
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("ChatReviewMoreFormat"), hidden),
                FontSize = 12,
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 2),
            Tag = "ChatReviewToggle",
        };
        button.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("Hub.Link"));
        button.Click += (_, _) =>
        {
            _reviewFilesOpen = !_reviewFilesOpen;
            var offset = MessageScroller.Offset;
            _reviewCardKey = null;
            ReviewCardFor(_chat?.ActiveConversation);
            SyncVirtualTails();
            MessageScroller.Offset = offset;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var updated = _reviewCard?.GetLogicalDescendants().OfType<Button>()
                    .FirstOrDefault(item => item.Tag as string == "ChatReviewToggle");
                updated?.Focus();
            });
        };
        return button;
    }

    private static Button ReviewAction(string textKey, Action click, bool primary = false, string? tag = null)
    {
        var button = new Button
        {
            Tag = tag,
            Content = new TextBlock { Text = HubStrings.Get(textKey), FontSize = 12 },
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Bind(TemplatedControl.ForegroundProperty,
            new DynamicResourceExtension(primary ? "Hub.TextPrimary" : "Hub.TextSecondary"));
        button.Bind(TemplatedControl.BackgroundProperty,
            new DynamicResourceExtension(primary ? "Hub.SurfaceRaised" : "Hub.SurfaceAlt"));
        button.Bind(TemplatedControl.BorderBrushProperty,
            new DynamicResourceExtension(primary ? "Hub.BorderStrong" : "Hub.BorderSubtle"));
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// Put the turn's files back. The workspace does the work off the UI thread and answers with a verdict per
    /// file; what lands here is the tick the transcript shows, in the same notice strip every other shell action
    /// uses — a card that silently changed the tree and said nothing would leave the reader trusting a diff that
    /// is no longer there.
    /// </summary>
    private async Task UndoTurnAsync(string conversationId)
    {
        if (_chat is null) return;
        // The answer is not painted here: a landed undo appends its own sentence in the user's voice, which is
        // both the confirmation and the thing the assistant has to read before it edits those files again. A
        // refusal leaves the card exactly as it was, because the turn is still there.
        await _chat.RevertTurnAsync(conversationId).ConfigureAwait(true);
    }

    /// <summary>A themed font family by resource key, for rows that are code rather than prose.</summary>
    private static FontFamily ThemeFont(string key)
        => Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            && value is FontFamily family
            ? family
            : FontFamily.Default;
}
