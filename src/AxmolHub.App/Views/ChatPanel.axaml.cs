using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
    private static readonly TimeSpan ChatRequestTimeout = TimeSpan.FromMinutes(2);

    private readonly ChatWorkspace _chat;
    private CancellationTokenSource? _send;
    private TextBlock? _streamStatusLabel;
    private TextBlock? _streamElapsedLabel;
    private Ellipse[] _activityDots = [];
    private DispatcherTimer? _activityTimer;
    private readonly Stopwatch _activityStopwatch = new();
    private readonly List<ContextAttachment> _contextAttachments = [];
    private string? _selectedComposerMode;
    private int _activityFrame;
    private bool _stopRequested;
    private bool _stickToBottom = true;
    private string? _pendingSteerText;
    private string? _pendingSteerContext;

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
        _chat.ToolActivityChanged += (name, completed) => Dispatcher.UIThread.Post(() =>
        {
            if (_streamStatusLabel is not null)
                _streamStatusLabel.Text = ToolActivityText(name, completed);
        });
        ModelPicker.Click += (_, _) => ShowModelMenu();
        ModeIndicatorButton.Click += (_, _) => ClearComposerMode();
        ContextButton.Click += (_, _) => ShowContextMenu();
        ToolTip.SetTip(AddContextButton, HubStrings.Get("ChatAddContext"));
        AddContextButton.Click += (_, _) => ShowAddContextMenu();
        SendButton.Click += (_, _) => _ = SendAsync();
        ScrollToBottomButton.Click += (_, _) => ScrollToEnd();
        MessageScroller.ScrollChanged += (_, _) => UpdateScrollAffordance();
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                UpdateSendState();
                UpdateContextRing();
            }
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
        InputBox.PlaceholderText = HubStrings.Get("InputPlaceholder");
        GreetingLabel.Text = HubStrings.Get("AssistantGreeting");
        GreetingSubtitle.Text = HubStrings.Get("AssistantGreetingSubtitle");
        UpdateSendState();

        RefreshModelPicker();
        RefreshComposerChoices();
        RenderMessages();
        UpdateContextRing();
    }

    private void RefreshModelPicker()
    {
        var choices = _chat.AvailableChatModels.ToArray();
        var selected = _chat.SelectedChatModel is { } active
            ? choices.FirstOrDefault(choice =>
                choice.Provider.Id == active.Provider.Id
                && string.Equals(choice.ModelName, active.ModelName, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedModelLabel.Text = selected?.ModelName ?? HubStrings.Get("NoAvailableChatModels");
        var supportsReasoning = selected is not null
            && ModelCatalog.SupportsReasoningEffort(selected.Provider, selected.ModelName);
        SelectedReasoningLabel.IsVisible = supportsReasoning;
        SelectedReasoningLabel.Text = supportsReasoning
            ? ReasoningChoiceLabel(_chat.ActiveReasoningEffort)
            : "";
        ModelPicker.IsEnabled = choices.Length > 0;
        ToolTip.SetTip(ModelPicker, selected is null
            ? HubStrings.Get("NoAvailableChatModels")
            : selected.Provider.Name + " · " + selected.ModelName);
    }

    private void RefreshComposerChoices()
    {
        RefreshModelPicker();
        ModeIndicatorButton.IsVisible = _selectedComposerMode is not null;
        ModeIndicatorIcon.Data = ThemeGeometry(ComposerModeIconKey(_selectedComposerMode));
        ModeIndicatorLabel.Text = HubStrings.Get(_selectedComposerMode switch
        {
            ChatModes.Ask => "ChatModeAsk",
            ChatModes.Plan => "ChatModePlan",
            _ => "ChatModeGoal",
        });
        ToolTip.SetTip(ModeIndicatorButton, HubStrings.Get("ChatModeResetHint"));
    }

    private void UpdateContextRing()
    {
        if (_chat is null) return;
        var (used, budget) = CurrentContextUsage();
        var ratio = budget > 0 ? (double)used / budget : 0;
        ContextRing.Usage = Math.Clamp(ratio, 0, 1);
        ToolTip.SetTip(ContextButton, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatContextEstimateFormat"),
            used.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            budget.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            Math.Clamp((int)Math.Round(ratio * 100), 0, 100)));
    }

    private (int Used, int Budget) CurrentContextUsage()
        => _chat.EstimateContextUsage(InputBox.Text ?? "");

    private void ShowContextMenu()
    {
        var (used, budget) = CurrentContextUsage();
        var ratio = budget > 0 ? (double)used / budget : 0;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem
        {
            Header = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextEstimateFormat"),
                used.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                budget.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                Math.Clamp((int)Math.Round(ratio * 100), 0, 100)),
            IsEnabled = false,
        });
        menu.Items.Add(new MenuItem
        {
            Header = HubStrings.Get("ChatContextEstimateHint"),
            IsEnabled = false,
        });
        menu.ShowAt(ContextButton);
    }

    private void ShowAddContextMenu()
    {
        BuildComposerMenu().ShowAt(AddContextButton);
    }

    private MenuFlyout BuildComposerMenu()
    {
        var menu = new MenuFlyout();
        foreach (var (mode, key) in new[]
                 {
                     (ChatModes.Ask, "ChatModeAsk"),
                     (ChatModes.Plan, "ChatModePlan"),
                     (ChatModes.Agent, "ChatModeGoal"),
                 })
        {
            var item = new MenuItem
            {
                Header = BuildComposerModeHeader(mode, key),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _selectedComposerMode == mode,
                Tag = mode,
            };
            item.Click += (_, _) =>
            {
                if (_selectedComposerMode == mode)
                    ClearComposerMode();
                else
                    SetComposerMode(mode);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        var addFolder = new MenuItem { Header = HubStrings.Get("ChatAddLocalFolder") };
        addFolder.Click += async (_, _) => await AddLocalFolderAsync();
        menu.Items.Add(addFolder);

        var projects = _chat.HubSnapshotProvider?.Invoke()?.Projects ?? [];
        var addProject = new MenuItem
        {
            Header = HubStrings.Get("ChatAddHubProject"),
            IsEnabled = projects.Count > 0,
        };
        foreach (var project in projects)
        {
            var projectItem = new MenuItem { Header = project.Name, Tag = project };
            projectItem.Click += (_, _) =>
            {
                if (projectItem.Tag is ChatWorkspace.HubProjectSummary selected)
                    AddProjectAttachment(selected);
            };
            addProject.Items.Add(projectItem);
        }

        if (projects.Count == 0)
        {
            addProject.Items.Add(new MenuItem
            {
                Header = HubStrings.Get("ChatNoHubProjects"),
                IsEnabled = false,
            });
        }
        menu.Items.Add(addProject);
        return menu;
    }

    private static Control BuildComposerModeHeader(string mode, string labelKey)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(BuildModeIcon(mode, 14));
        header.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(labelKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }

    private static Avalonia.Controls.Shapes.Path BuildModeIcon(string mode, double size)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry(ComposerModeIconKey(mode)),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(
            Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension("Hub.TextSecondary"));
        return icon;
    }

    private static string ComposerModeIconKey(string? mode) => mode switch
    {
        ChatModes.Ask => "Hub.Icon.ChatModeAsk",
        ChatModes.Plan => "Hub.Icon.ChatModePlan",
        _ => "Hub.Icon.ChatModeGoal",
    };

    private void SetComposerMode(string mode)
    {
        _selectedComposerMode = mode;
        _chat.SelectMode(mode);
        RefreshComposerChoices();
    }

    private void ClearComposerMode()
    {
        _selectedComposerMode = null;
        _chat.SelectMode(ChatModes.Agent);
        RefreshComposerChoices();
    }

    private void ShowModelMenu()
    {
        BuildModelMenu().ShowAt(ModelPicker);
    }

    private MenuFlyout BuildModelMenu()
    {
        var menu = new MenuFlyout();
        var choices = _chat.AvailableChatModels;
        var selected = _chat.SelectedChatModel;
        foreach (var choice in choices)
        {
            var isSelected = selected is not null
                && choice.Provider.Id == selected.Provider.Id
                && string.Equals(choice.ModelName, selected.ModelName, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = choice.Provider.Name + " · " + choice.ModelName,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = isSelected,
                Tag = choice,
            };
            item.Click += (_, _) => _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);

            if (ModelCatalog.ReasoningFor(choice.Provider, choice.ModelName) is { Efforts.Count: > 0 } reasoning)
            {
                var currentEffort = isSelected ? _chat.ActiveReasoningEffort : ChatReasoningEfforts.Auto;
                foreach (var (effort, labelKey) in ReasoningChoices.Where(choice =>
                             choice.Value == ChatReasoningEfforts.Auto
                             || reasoning.Efforts.Contains(choice.Value, StringComparer.OrdinalIgnoreCase)))
                {
                    var effortItem = new MenuItem
                    {
                        Header = HubStrings.Get(labelKey),
                        ToggleType = MenuItemToggleType.Radio,
                        IsChecked = currentEffort == effort,
                        Tag = effort,
                    };
                    effortItem.Click += (_, _) =>
                    {
                        if (!isSelected) _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);
                        _chat.SelectReasoningEffort(effort);
                    };
                    item.Items.Add(effortItem);
                }
            }

            menu.Items.Add(item);
        }

        return menu;
    }

    private static readonly (string Value, string LabelKey)[] ReasoningChoices =
    [
        (ChatReasoningEfforts.Auto, "ChatReasoningAuto"),
        (ChatReasoningEfforts.Low, "ChatReasoningLow"),
        (ChatReasoningEfforts.Medium, "ChatReasoningMedium"),
        (ChatReasoningEfforts.High, "ChatReasoningHigh"),
        (ChatReasoningEfforts.XHigh, "ChatReasoningXHigh"),
        (ChatReasoningEfforts.Max, "ChatReasoningMax"),
        (ChatReasoningEfforts.Ultra, "ChatReasoningUltra"),
    ];

    private static string ReasoningChoiceLabel(string effort)
    {
        var labelKey = ReasoningChoices.FirstOrDefault(choice => choice.Value == effort).LabelKey
                       ?? ReasoningChoices[0].LabelKey;
        var label = HubStrings.Get(labelKey);
        var separator = label.IndexOfAny(['：', ':']);
        return separator >= 0 ? label[(separator + 1)..].Trim() : label;
    }

    private async Task AddLocalFolderAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatPickFolderTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFolderNotLocal"), danger: true);
            return;
        }

        var path = result.Path!;
        AddContextAttachment(new ContextAttachment(
            "folder",
            System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
            path));
    }

    private void AddProjectAttachment(ChatWorkspace.HubProjectSummary project)
    {
        var details = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatProjectContextFormat"),
            project.EngineVersion,
            project.Platform,
            project.Configuration,
            project.BuildStatus);
        AddContextAttachment(new ContextAttachment("project", project.Name, project.Path, details));
    }

    private void AddContextAttachment(ContextAttachment attachment)
    {
        if (_contextAttachments.Any(existing =>
                string.Equals(existing.Path, attachment.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            return;
        _contextAttachments.Add(attachment);
        RenderContextAttachments();
        UpdateContextRing();
    }

    private void RenderContextAttachments()
    {
        ContextAttachmentPanel.Children.Clear();
        foreach (var attachment in _contextAttachments)
        {
            var label = new TextBlock
            {
                Text = (attachment.Kind == "project" ? HubStrings.Get("ChatProjectPrefix") : HubStrings.Get("ChatFolderPrefix"))
                       + attachment.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240,
            };
            label.Classes.Add("context-attachment-label");
            var remove = new Button { Content = "×", Tag = attachment };
            remove.Classes.Add("context-attachment-remove");
            ToolTip.SetTip(label, attachment.Path);
            remove.Click += (_, _) =>
            {
                if (remove.Tag is ContextAttachment selected)
                {
                    _contextAttachments.Remove(selected);
                    RenderContextAttachments();
                    UpdateContextRing();
                }
            };
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            chip.Children.Add(label);
            chip.Children.Add(remove);
            var border = new Border { Child = chip };
            border.Classes.Add("context-attachment");
            ContextAttachmentPanel.Children.Add(border);
        }

        ContextAttachmentPanel.IsVisible = _contextAttachments.Count > 0;
    }

    private Task<string?> ReadAttachmentContextAsync()
    {
        if (_contextAttachments.Count == 0) return Task.FromResult<string?>(null);
        var attachments = _contextAttachments.ToArray();
        return Task.Run<string?>(() =>
        {
            var sections = attachments.Select(attachment =>
            {
                var contents = ChatContextReader.ReadFolder(attachment.Path, attachment.Name);
                return attachment.Details is { Length: > 0 }
                    ? attachment.Details + "\n\n" + contents
                    : contents;
            });
            return string.Join("\n\n", sections);
        });
    }

    private static string ToolActivityText(string name, bool completed)
    {
        var tool = name switch
        {
            "get_projects" => HubStrings.Get("ChatToolProjects"),
            "get_engines" => HubStrings.Get("ChatToolEngines"),
            "get_toolchain_status" => HubStrings.Get("ChatToolchains"),
            _ => name,
        };
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(completed ? "ChatToolCompletedFormat" : "ChatToolRunningFormat"),
            tool);
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

        MessageFlow.Children.Add(BuildMessageRow(fromUser, body, index, turn.Role, turn.Text, isLast, turn.At));
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
            fromUser ? ChatRoles.User : ChatRoles.Assistant, text, false,
            fromUser ? DateTimeOffset.Now : null));
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
    private Control BuildMessageRow(
        bool fromUser, Control body, int? index, string role, string text, bool isLast, DateTimeOffset? at = null)
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
            var actions = BuildActionBar(messageIndex, role, text, isLast, at);
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

    private Control BuildActionBar(int index, string role, string text, bool isLast, DateTimeOffset? at)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Classes.Add("message-actions");

        if (role == ChatRoles.User && at is { } timestamp)
        {
            var localTimestamp = timestamp.ToLocalTime();
            var relative = new TextBlock { Text = FormatRelativeTime(DateTimeOffset.Now - localTimestamp) };
            relative.Classes.Add("message-timestamp");
            ToolTip.SetTip(relative, localTimestamp.ToString(
                "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture));
            bar.Children.Add(relative);
        }

        if (role == ChatRoles.User)
        {
            bar.Children.Add(IconActionButton("CopyMessage", "Hub.Icon.Copy", () => CopyToClipboard(text)));
            bar.Children.Add(IconActionButton("EditMessage", "Hub.Icon.Edit", () => _ = EditMessageAsync(index, text)));
        }
        else
        {
            bar.Children.Add(ActionButton("CopyMessage", () => CopyToClipboard(text)));
            if (isLast)
            {
                bar.Children.Add(ActionButton("RegenerateMessage", () => _ = RegenerateAsync()));
                bar.Children.Add(ActionButton("ContinueReply", () => _ = ContinueAsync()));
            }
            bar.Children.Add(ActionButton("DeleteMessage", () => _ = DeleteMessageAsync(index)));
        }

        return bar;
    }

    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        if (seconds < 60) return HubStrings.Get("MessageTimeJustNow");
        if (seconds < 3600)
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageTimeMinutesAgo"),
                (int)elapsed.TotalMinutes);
        if (seconds < 86400)
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageTimeHoursAgo"),
                (int)elapsed.TotalHours);

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("MessageTimeDaysAgo"),
            (int)elapsed.TotalDays);
    }

    private static Button ActionButton(string textKey, Action onClick)
    {
        var button = new Button { Content = HubStrings.Get(textKey), Tag = textKey };
        button.Classes.Add("message-action");
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button IconActionButton(string textKey, string geometryKey, Action onClick)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        var button = new Button { Content = icon, Tag = textKey };
        button.Classes.Add("message-action");
        button.Classes.Add("message-action-icon");
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
        var hasText = (InputBox.Text ?? "").Trim().Length > 0;
        SendButton.IsEnabled = _pendingSteerText is null && (streaming || hasText);
        SendButton.Content = BuildSendIcon(streaming && !hasText);
        ToolTip.SetTip(SendButton, HubStrings.Get(streaming
            ? hasText ? "ChatSteer" : "Stop"
            : "Send"));
        UpdateContextRing();
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
            if (_pendingSteerText is not null) return;
            var steerText = (InputBox.Text ?? "").Trim();
            if (steerText.Length > 0)
            {
                string? steerContext;
                try
                {
                    steerContext = await ReadAttachmentContextAsync();
                }
                catch (Exception ex)
                {
                    AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
                    return;
                }

                _pendingSteerText = steerText;
                _pendingSteerContext = steerContext;
                InputBox.Text = "";
                _contextAttachments.Clear();
                RenderContextAttachments();
                if (_streamStatusLabel is not null)
                    _streamStatusLabel.Text = HubStrings.Get("ChatSteering");
                UpdateSendState();
                _send.Cancel();
                return;
            }

            _stopRequested = true;
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

        string? context;
        try
        {
            context = await ReadAttachmentContextAsync();
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
            return;
        }

        _contextAttachments.Clear();
        RenderContextAttachments();
        InputBox.Text = "";
        await SendTextAsync(text, context);
    }

    private async Task SendTextAsync(string text, string? attachedContext = null)
    {
        if (text.Length == 0) return;
        if (_chat.SelectedChatModel is null)
        {
            AppendNotice(HubStrings.Get("NoAvailableChatModels"), danger: true);
            return;
        }

        if (_chat.ActiveConversation is null) _chat.StartConversation();

        AppendPlainBubble(text, fromUser: true);
        ConversationStateChanged?.Invoke();
        await StreamReplyAsync(token => _chat.SendAsync(text, attachedContext, token));
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
        _stopRequested = false;
        _send.CancelAfter(ChatRequestTimeout);
        var token = _send.Token;

        var body = new StackPanel { Spacing = 8 };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(preview);
        var activity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        activity.Classes.Add("chat-activity");
        var dots = new StackPanel { Orientation = Orientation.Horizontal };
        dots.Classes.Add("chat-activity-dots");
        _activityDots = Enumerable.Range(0, 3).Select(_ =>
        {
            var dot = new Ellipse();
            dot.Classes.Add("chat-activity-dot");
            return dot;
        }).ToArray();
        foreach (var dot in _activityDots) dots.Children.Add(dot);
        activity.Children.Add(dots);
        _streamStatusLabel = new TextBlock { Text = HubStrings.Get("ChatPreparing") };
        _streamStatusLabel.Classes.Add("chat-activity-label");
        activity.Children.Add(_streamStatusLabel);
        activity.Children.Add(new TextBlock
        {
            Text = "·",
            Classes = { "chat-activity-elapsed" },
        });
        _streamElapsedLabel = new TextBlock
        {
            Text = "0s",
            Classes = { "chat-activity-elapsed" },
        };
        activity.Children.Add(_streamElapsedLabel);
        body.Children.Add(activity);
        AppendStreamingBubble(body);

        var buffer = new StringBuilder();
        var receivedText = false;
        string? completionNotice = null;
        var completionNoticeIsDanger = false;
        _activityStopwatch.Restart();
        _activityFrame = 0;
        UpdateActivityIndicator();
        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _activityTimer.Tick += (_, _) =>
        {
            _activityFrame++;
            UpdateActivityIndicator();
            UpdateActivityElapsed();
        };
        _activityTimer.Start();
        UpdateSendState();
        try
        {
            await foreach (var chunk in start(token).ConfigureAwait(true))
            {
                if (!receivedText && chunk.Length > 0)
                {
                    receivedText = true;
                    if (_streamStatusLabel is not null)
                        _streamStatusLabel.Text = HubStrings.Get("ChatGenerating");
                }

                buffer.Append(chunk);
                preview.Text = buffer.ToString();
                ScrollToEndIfSticky();
            }
        }
        catch (OperationCanceledException)
        {
            if (_pendingSteerText is null)
            {
                completionNotice = _stopRequested
                    ? HubStrings.Get("ChatCancelled")
                    : HubStrings.Get("ChatTimedOut");
                completionNoticeIsDanger = !_stopRequested;
            }
        }
        catch (TimeoutException)
        {
            completionNotice = HubStrings.Get("ChatTimedOut");
            completionNoticeIsDanger = true;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            completionNotice = HubStrings.Get("ChatConnectionFailed") + ex.Message;
            completionNoticeIsDanger = true;
        }
        catch (Exception ex)
        {
            completionNotice = HubStrings.Get("ChatFailed") + ex.Message;
            completionNoticeIsDanger = true;
        }
        finally
        {
            _activityTimer?.Stop();
            _activityTimer = null;
            _activityStopwatch.Stop();
            _streamStatusLabel = null;
            _streamElapsedLabel = null;
            _activityDots = [];
            _send.Dispose();
            _send = null;
            UpdateSendState();
            ForceRebuildMessages();
            ConversationStateChanged?.Invoke();
            if (completionNotice is null && buffer.Length == 0)
            {
                completionNotice = HubStrings.Get("ChatNoResponse");
                completionNoticeIsDanger = true;
            }
            if (completionNotice is not null)
                AppendNotice(completionNotice, completionNoticeIsDanger);
            ScrollToEnd();
        }

        if (_pendingSteerText is { } steerText)
        {
            _pendingSteerText = null;
            var steerContext = _pendingSteerContext;
            _pendingSteerContext = null;
            UpdateSendState();
            await SendTextAsync(steerText, steerContext);
        }
    }

    private void UpdateActivityIndicator()
    {
        for (var i = 0; i < _activityDots.Length; i++)
            _activityDots[i].Opacity = (i + _activityFrame) % _activityDots.Length == 0 ? 1 : 0.35;
    }

    private void UpdateActivityElapsed()
    {
        if (_streamElapsedLabel is null) return;
        var elapsed = _activityStopwatch.Elapsed;
        _streamElapsedLabel.Text = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
            : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
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

    internal bool HasScrollableMarkdownTable()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasScrollableTable);

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

    internal int ModelChoiceCount => _chat.AvailableChatModels.Count;
    internal string SelectedModelText => SelectedModelLabel.Text ?? "";

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
    internal bool ChatActivityVisibleForCheck => _streamStatusLabel is { IsVisible: true };
    internal string ChatActivityTextForCheck => _streamStatusLabel?.Text ?? "";
    internal string ChatActivityElapsedForCheck => _streamElapsedLabel?.Text ?? "";
    internal string? LastNoticeTextForCheck
        => MessageFlow.Children.LastOrDefault() is Grid row
           && row.Classes.Contains("notice")
           && row.Children.OfType<TextBlock>().FirstOrDefault() is { } label
            ? label.Text
            : null;
    internal string InputTextForCheck => InputBox.Text ?? "";

    /// <summary>How many times the send button's state has been recomputed. A check asserts it grows when the
    /// input text changes, which is what proves the change notification is actually wired up.</summary>
    internal int SendStateUpdates { get; private set; }
    internal string SendButtonTooltipForCheck => ToolTip.GetTip(SendButton)?.ToString() ?? "";
    internal bool ModelPickerIsChipForCheck => ModelPicker.Classes.Contains("chip");
    internal bool ModelPickerUsesContentWidthForCheck => double.IsNaN(ModelPicker.Width);
    internal string SelectedModeForCheck => _chat.ActiveMode;
    internal string SelectedReasoningForCheck => _chat.ActiveReasoningEffort;
    internal bool ReasoningPickerEnabledForCheck => SelectedReasoningLabel.IsVisible;
    internal double ContextUsageForCheck => ContextRing.Usage;
    internal string ContextTooltipForCheck => ToolTip.GetTip(ContextButton)?.ToString() ?? "";
    internal int ContextAttachmentCountForCheck => _contextAttachments.Count;
    internal bool HasComposerAddMenuForCheck => AddContextButton is not null;
    internal bool ModeIndicatorVisibleForCheck => ModeIndicatorButton.IsVisible;
    internal bool ComposerPlusCenteredForCheck
        => AddContextButton.HorizontalContentAlignment == HorizontalAlignment.Center
           && AddContextButton.VerticalContentAlignment == VerticalAlignment.Center;
    internal bool ContextRingPrecedesModelForCheck
        => ContextButton.Parent is Panel panel
           && panel.Children.IndexOf(ContextButton) < panel.Children.IndexOf(ModelPicker);
    internal bool ModeIndicatorFollowsPlusForCheck
        => ModeIndicatorButton.Parent is Panel panel
           && panel.Children.IndexOf(ModeIndicatorButton) == panel.Children.IndexOf(AddContextButton) + 1;
    internal bool ModeIndicatorKeepsLabelVisibleForCheck
        => ModeIndicatorLabel.IsVisible && ModeIndicatorLabel.Parent is Grid grid
           && grid.ColumnDefinitions.Count == 2
           && Grid.GetColumn(ModeIndicatorLabel) == 1;
    internal bool ModeIndicatorIconMatchesSelectedModeForCheck
        => ReferenceEquals(ModeIndicatorIcon.Data, ThemeGeometry(ComposerModeIconKey(_selectedComposerMode)));
    internal bool ModeIndicatorCloseIsRedForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("mode-indicator-close")) is { } close
           && close.Stroke is ISolidColorBrush closeBrush
           && closeBrush.Color.R > closeBrush.Color.G
           && closeBrush.Color.R > closeBrush.Color.B;
    internal string ModeIndicatorCloseColorForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("mode-indicator-close"))?.Stroke?.ToString() ?? "unset";
    internal bool ModeIndicatorCloseIsLeftAndCenteredForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("mode-indicator-close")) is { } close
           && close.VerticalAlignment == VerticalAlignment.Center
           && close.Parent is Grid grid
           && Grid.GetColumn(close) == 0;
    internal bool ComposerMenuModesHaveIconsForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => item.Header is StackPanel header
                         && header.Children.OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault() is { } icon
                         && ReferenceEquals(icon.Data, ThemeGeometry(ComposerModeIconKey(item.Tag?.ToString())))
                         && header.Children.OfType<TextBlock>().Any());
    internal string[] ComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>()
        .Where(IsComposerModeItem)
        .Select(item => (string)item.Tag!)
        .ToArray();
    internal string[] CheckedComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>()
        .Where(item => IsComposerModeItem(item) && item.IsChecked)
        .Select(item => (string)item.Tag!)
        .ToArray();
    internal bool ComposerMenuModeItemsCloseOnClickForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => !item.StaysOpenOnClick);
    internal bool ComposerMenuModesAreCheckboxesForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => item.ToggleType == MenuItemToggleType.CheckBox);
    internal string? SelectedComposerModeForCheck => _selectedComposerMode;

    /// <summary>True when the round button currently shows the stop square. Compared by geometry identity
    /// rather than by string, because the icons are resolved from the same cached resource.</summary>
    internal bool SendIconIsStopForCheck
        => SendButton.Content is Avalonia.Controls.Shapes.Path path
           && ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.Stop"));

    /// <summary>The round button on its own, so a check can render just the circle and its glyph.</summary>
    internal Control SendButtonForCheck => SendButton;

    /// <summary>The colour the glyph is drawn in, read from its live brush. A pixel mask must not hardcode
    /// it: <c>Hub.Accent</c> is a different blue per theme, and a literal would fail on a theme switch.</summary>
    internal Color SendGlyphColorForCheck
        => SendButton.Content is Avalonia.Controls.Shapes.Path { Stroke: ISolidColorBrush brush }
            ? brush.Color
            : Colors.Transparent;

    /// <summary>
    /// Where the glyph's slot sits inside the button, in DIP, plus the width the hidden "…" affordance
    /// claims. Measured from layout bounds because it is the ink's placement *inside* the slot that goes
    /// wrong, not the slot's placement in the button — so the two together say whether a centring failure
    /// comes from layout or from Stretch. A negative affordance width means the template part was not
    /// found at all, which is a different failure from "it takes no space".
    /// </summary>
    internal (double Dx, double Dy, double SlotWidth, double AffordanceWidth) SendGlyphLayoutForCheck
    {
        get
        {
            var glyph = SendButton.Content as Visual;
            double x = 0, y = 0;
            for (var v = glyph; v is not null && v != SendButton; v = v.GetVisualParent())
            {
                x += v.Bounds.X;
                y += v.Bounds.Y;
            }
            var affordance = SendButton.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "PART_MoreAffordance");
            return (x + (glyph?.Bounds.Width ?? 0) / 2 - SendButton.Bounds.Width / 2,
                    y + (glyph?.Bounds.Height ?? 0) / 2 - SendButton.Bounds.Height / 2,
                    glyph?.Bounds.Width ?? 0,
                    affordance?.Bounds.Width ?? -1);
        }
    }

    internal void SetInputForCheck(string text) => InputBox.Text = text;

    internal bool SelectModeForCheck(string mode)
    {
        if (mode is not (ChatModes.Ask or ChatModes.Plan or ChatModes.Agent)) return false;
        SetComposerMode(mode);
        return true;
    }

    internal bool ClickComposerModeMenuForCheck(string mode)
    {
        var menu = BuildComposerMenu();
        var item = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), mode, StringComparison.Ordinal));
        if (item is null) return false;

        menu.ShowAt(AddContextButton);
        Dispatcher.UIThread.RunJobs();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return !menu.IsOpen;
    }

    internal void ResetModeForCheck()
        => ModeIndicatorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool IsComposerModeItem(MenuItem item)
        => item.Tag is string mode
           && (mode == ChatModes.Ask || mode == ChatModes.Plan || mode == ChatModes.Agent);

    internal bool SelectReasoningForCheck(string effort)
    {
        if (!ReasoningChoices.Any(choice => choice.Value == effort)) return false;
        return _chat.SelectReasoningEffort(effort);
    }

    internal bool SelectModelForCheck(string providerId, string modelName)
    {
        var choice = _chat.AvailableChatModels
            .FirstOrDefault(option => option.Provider.Id == providerId && option.ModelName == modelName);
        return choice is not null && _chat.SelectChatModel(providerId, modelName);
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

    private sealed record ContextAttachment(string Kind, string Name, string Path, string? Details = null);

    internal bool BubbleHasAction(int visibleIndex, string textKey)
    {
        var row = MessageRows.ElementAtOrDefault(visibleIndex);
        return row is not null && row.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action")
                           && (string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal)
                               || string.Equals(button.Content?.ToString(), HubStrings.Get(textKey), StringComparison.Ordinal)));
    }

    internal bool BubbleHasIconAction(int visibleIndex, string textKey)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action-icon")
                           && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal)) == true;

    internal double BubbleActionBarOpacity(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Classes.Contains("message-actions"))?.Opacity ?? -1;

    internal string? MessageTimestampTextForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("message-timestamp"))?.Text;

    internal string? MessageTimestampTooltipForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("message-timestamp")) is { } label
            ? ToolTip.GetTip(label)?.ToString()
            : null;
}
