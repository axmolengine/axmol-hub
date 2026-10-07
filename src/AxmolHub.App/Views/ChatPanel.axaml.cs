using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
    private readonly List<ContextAttachment> _contextAttachments = [];

    /// <summary>
    /// The pictures this composer is holding, in the order they were added, still in memory. A draft that is
    /// abandoned leaves nothing on disk, and the conversation the pictures would be stored into may not exist
    /// yet — Hub creates it when the message is sent, which is also when these bytes get a file name.
    /// </summary>
    private readonly List<PendingPicture> _pendingPictures = [];

    private string? _selectedComposerMode;
    private bool _stickToBottom = true;
    private Task? _contextCompressionTask;

    /// <summary>
    /// The bubble showing the reply arriving in the session on screen, or null while none is. Everything it
    /// displays is read off the run, so leaving and coming back rebuilds it from what actually arrived instead
    /// of from a copy this view kept — which is also what lets a session keep streaming while it is hidden.
    /// </summary>
    private LiveBubble? _live;

    /// <summary>One attached run's worth of chrome: the row, the pieces of it that change while text arrives,
    /// and the timer that animates them. Created on attach, thrown away on detach; the run outlives both.</summary>
    private sealed class LiveBubble
    {
        public required string ConversationId { get; init; }
        public required Control Row { get; init; }
        public required TextBlock Preview { get; init; }
        public required TextBlock Status { get; init; }
        public required TextBlock Elapsed { get; init; }
        public required Ellipse[] Dots { get; init; }
        public DispatcherTimer? Timer { get; set; }
        public Stopwatch Watch { get; } = new();
        public int Frame { get; set; }

        /// <summary>Whether this segment has produced any text yet — the status line says "preparing" until it
        /// has, and a steer starts a new segment, so the flag has to be able to go back.</summary>
        public bool ShowedText { get; set; }
    }

    /// <summary>The conversation whose turns <see cref="MessageFlow"/> currently shows, and how many of its
    /// visible turns are already rendered. Together they let <see cref="RenderMessages"/> append only what is
    /// new instead of tearing down and rebuilding the whole flow on every change.</summary>
    private string? _renderedConversationId;
    private int _renderedCount;

    /// <summary>The per-turn states as they were when the flow was laid down. An approval decision — and a spent
    /// undo copy — rewrite what a turn that is already on screen looks like without changing how many turns there
    /// are, which is precisely what counting cannot see.</summary>
    private string _renderedApprovalStamp = "";

    private static string ApprovalStamp(Conversation? conversation)
    {
        if (conversation is null) return "";
        var stamp = new StringBuilder();
        foreach (var turn in conversation.Messages)
        {
            if (turn.ApprovalState is null && turn.UndoName is null && turn.PlanApprovalState is null) continue;
            stamp.Append(turn.ToolCallId).Append(':').Append(turn.ApprovalState ?? "")
                .Append('/').Append(turn.UndoName ?? "")
                .Append('/').Append(turn.PlanApprovalState ?? "").Append(';');
        }

        return stamp.ToString();
    }

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
        // Tool activity already arrives on the UI thread, and it carries the session it happened in: a reply
        // running out of sight must not rewrite the status line of the one on screen.
        _chat.ToolActivityChanged += (conversationId, name, completed) =>
        {
            if (_live is { } live && live.ConversationId == conversationId)
                live.Status.Text = ToolActivityText(name, completed);
        };
        _chat.RunTextChanged += conversationId =>
        {
            if (_live is { } live && live.ConversationId == conversationId) RefreshLive(live);
        };
        _chat.RunsChanged += conversationId =>
        {
            if (conversationId == _chat.ViewedConversationId) RenderMessages();
        };
        _chat.RunCompleted += (conversationId, outcome) =>
        {
            if (conversationId != _chat.ViewedConversationId) return;
            RenderMessages();
            AppendRunNotice(outcome);
            UpdateSendState();
            ConversationStateChanged?.Invoke();
        };
        ModelPicker.Click += (_, _) => ShowModelMenu();
        ForkNotice.Click += (_, _) =>
        {
            if (ForkNotice.Tag is string sourceId) _chat.OpenConversation(sourceId);
        };
        ModeIndicatorButton.Click += (_, _) => ClearComposerMode();
        ContextButton.Click += (_, _) => ShowContextMenu();
        ContextPopup.PlacementTarget = ContextButton;
        ContextPopup.Placement = PlacementMode.TopEdgeAlignedRight;
        ContextProgressTrack.SizeChanged += (_, _) => UpdateContextProgressFill();
        CompressContextButton.Click += (_, _) => _contextCompressionTask = CompressContextAsync();
        ToolTip.SetTip(AddContextButton, HubStrings.Get("ChatAddContext"));
        AddContextButton.Click += (_, _) => ShowAddContextMenu();
        PermissionChip.Click += (_, _) => ShowPermissionMenu();
        WorkspaceChip.Click += (_, _) => ShowWorkspaceMenu();
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

        // Ctrl+V is claimed here rather than left to the box, and the text paste is then asked for by name: a
        // clipboard holding a screenshot has to become a chip, and a clipboard holding text has to keep pasting
        // text. Reading the clipboard is async, so the alternative — peek and decide — would either drop the
        // picture or paste the file path a copied image also carries.
        InputBox.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.V) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            e.Handled = true;
            if (!await TryPastePictureAsync()) InputBox.Paste();
        }, RoutingStrategies.Tunnel);

        // Dropping a capture onto the composer is how a picture arrives while the answer is still being written.
        // The frame is the target rather than the whole page, so a drop beside it does nothing at all.
        DragDrop.SetAllowDrop(ComposerFrame, true);
        DragDrop.AddDragOverHandler(ComposerFrame, (_, e) =>
        {
            if (DragCarriesFiles(e)) e.DragEffects = DragDropEffects.Copy;
        });
        DragDrop.AddDropHandler(ComposerFrame, OnComposerDrop);

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
        UpdateForkNotice();
        RenderMessages();
        UpdateContextRing();
    }

    /// <summary>
    /// Names the session the current one was forked from, above the transcript. Hidden for a normal session
    /// and for a fork whose source has since been deleted — a link to nothing would be worse than no link.
    /// </summary>
    private void UpdateForkNotice()
    {
        var source = _chat.ActiveConversation?.BranchSourceId is { Length: > 0 } sourceId
            ? _chat.Conversations.FirstOrDefault(summary => summary.Id == sourceId)
            : null;

        ForkNotice.IsVisible = source is not null;
        if (source is null) return;

        ForkNotice.Tag = source.Id;
        ForkNotice.Content = string.Format(
            System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get("ForkedFrom"), source.Title);
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
        // With routing on there is no single tier to name — Hub picks one per request — so the chip says who is
        // picking, and the tooltip carries the route this session last actually went out on. A setting that
        // spends money has to be readable without opening a log file.
        var routed = supportsReasoning && _chat.ActiveRouting == ChatRouting.Auto;
        SelectedReasoningLabel.Text = supportsReasoning
            ? routed ? HubStrings.Get("ChatRoutingAutoChip") : ReasoningChoiceLabel(_chat.ActiveReasoningEffort)
            : "";
        ToolTip.SetTip(SelectedReasoningLabel, routed && _chat.ActiveConversation is { } session
            && _chat.LastRouteFor(session.Id) is { } route
            ? $"{route.Model} · {route.Effort} — {route.Reason}"
            : null);
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
        // The chip names the *effective* mode, not the one someone clicked: a session that merely follows a
        // permissive default is in it, and reading back the override would call that mode safe. With no session
        // on screen yet it names the mode the first one starts under, because before the first message is the
        // moment to decide how much to allow.
        var permission = _chat.ActiveApprovalMode;
        PermissionChipIcon.Data = ThemeGeometry(ToolApprovalIconKey(permission));
        PermissionChipLabel.Text = HubStrings.Get(ToolApprovalModeKey(permission));
        PermissionChip.Classes.Set("danger", permission == ToolApprovalModes.Full);
        ToolTip.SetTip(PermissionChip, HubStrings.Get(ToolApprovalModeHintKey(permission)));
        // The picker reads out the directory itself rather than a label for it: "which folder may this assistant
        // write to" has one answer and the folder name is it. When nothing is bound it says so, because that is
        // the state where every file tool refuses and the reason is still one click away.
        var root = _chat.ActiveWorkspaceRoot;
        WorkspaceChipLabel.Text = root is { Length: > 0 } ? FolderNameOf(root) : HubStrings.Get("ChatWorkspaceNone");
        ToolTip.SetTip(WorkspaceChip, root is { Length: > 0 }
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWorkspaceChipHintFormat"), root)
            : HubStrings.Get("ChatWorkspaceChipHint"));
        // And it is on screen only while the choice is still open: no session, or one that has never received a
        // message. The directory is what a session is born into, so after the first turn it is history —
        // swapping it mid-conversation would leave every earlier reply about a different tree — and a control
        // that can no longer act is a claim that it still can.
        WorkspaceChip.IsVisible = _chat.ActiveConversation is not { Messages.Count: > 0 };
    }

    private static string FolderNameOf(string path)
        => System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    private void UpdateContextRing()
    {
        if (_chat is null) return;
        var (used, budget) = CurrentContextUsage();
        var ratio = budget > 0 ? (double)used / budget : 0;
        ContextRing.Usage = Math.Clamp(ratio, 0, 1);
        ContextPopoverPercent.Text = Math.Clamp((int)Math.Round(ratio * 100), 0, 100).ToString(
            System.Globalization.CultureInfo.CurrentCulture) + "%";
        UpdateContextProgressFill();
        RefreshContextCompressionButton();
        ToolTip.SetTip(ContextButton, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatContextEstimateFormat"),
            used.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            budget.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            Math.Clamp((int)Math.Round(ratio * 100), 0, 100)));
    }

    private (int Used, int Budget) CurrentContextUsage()
        => _chat.EstimateContextUsage(InputBox.Text ?? "");

    private void UpdateContextProgressFill()
    {
        if (ContextProgressTrack is null || ContextProgressFill is null) return;
        var (used, budget) = CurrentContextUsage();
        var ratio = budget > 0 ? Math.Clamp((double)used / budget, 0, 1) : 0;
        ContextProgressFill.Width = ContextProgressTrack.Bounds.Width * ratio;
    }

    private void RefreshContextCompressionButton()
    {
        if (_chat is null) return;
        var conversation = _chat.ActiveConversation;
        var compressing = conversation is not null && _chat.IsCompressingContext(conversation.Id);
        CompressContextButton.IsEnabled = conversation is not null
                                          && !compressing
                                          && _chat.CanCompressContext(conversation.Id);
        CompressContextButton.Content = HubStrings.Get(
            compressing ? "ChatCompressingContext" : "ChatCompressContext");
    }

    private void ShowContextMenu()
    {
        UpdateContextRing();
        ContextPopup.IsOpen = true;
    }

    private async Task CompressContextAsync()
    {
        if (_chat.ActiveConversation is not { } conversation) return;
        CompressContextButton.IsEnabled = false;
        CompressContextButton.Content = HubStrings.Get("ChatCompressingContext");
        try
        {
            if (!await _chat.CompressContextAsync(conversation.Id))
                AppendNotice(HubStrings.Get("ChatContextCompressUnavailable"), danger: false);
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatContextCompressFailed") + ex.Message, danger: true);
        }
        finally
        {
            UpdateContextRing();
        }
    }

    private void ShowAddContextMenu() => ShowMenu(BuildComposerMenu(), AddContextButton);

    private void ShowPermissionMenu() => ShowMenu(BuildPermissionMenu(), PermissionChip);

    private void ShowWorkspaceMenu() => ShowMenu(BuildWorkspaceMenu(), WorkspaceChip);

    /// <summary>
    /// Pick a directory, and — only while one is bound — clear it. The bound path is not repeated as a row: the
    /// chip reads it out and the tooltip gives it in full, so a third copy would be chrome saying a thing twice.
    /// </summary>
    private MenuFlyout BuildWorkspaceMenu()
    {
        var menu = new MenuFlyout();
        var choose = new MenuItem { Header = HubStrings.Get("ChatWorkspaceMenuChoose") };
        choose.Click += async (_, _) =>
        {
            menu.Hide();
            await PickWorkspaceFolderAsync();
        };
        menu.Items.Add(choose);

        if (_chat.ActiveWorkspaceRoot is { Length: > 0 })
        {
            var clear = new MenuItem { Header = HubStrings.Get("ChatWorkspaceMenuClear") };
            clear.Click += (_, _) =>
            {
                _chat.SelectWorkspaceRoot(null);
                menu.Hide();
            };
            menu.Items.Add(clear);
        }

        return menu;
    }

    /// <summary>Refusals come back as a verdict, not as text: the sentences in <c>WorkspacePaths</c> are written
    /// for the model, and the panel has its own words for the same fact.</summary>
    private async Task PickWorkspaceFolderAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatWorkspacePickTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFolderNotLocal"), danger: true);
            return;
        }

        switch (_chat.SelectWorkspaceRoot(result.Path))
        {
            case null:
                return;
            case WorkspacePathVerdict.ProtectedRoot:
                AppendNotice(HubStrings.Get("ChatWorkspaceProtected"), danger: true);
                return;
            default:
                AppendNotice(HubStrings.Get("ChatWorkspaceRejected") + result.Path, danger: true);
                return;
        }
    }

    /// <summary>
    /// The flyout this panel last opened. A popup is its own top-level, so a screenshot of one has to start
    /// from an item inside it — which is the only reason the instance is kept.
    /// </summary>
    private MenuFlyout? _openMenu;

    private void ShowMenu(MenuFlyout menu, Control anchor)
    {
        _openMenu = menu;
        menu.ShowAt(anchor);
    }

    /// <summary>
    /// The permission menu, hung off its own chip: three tiers, each stating what it lets through unasked, with
    /// the effective one ticked. A fourth row appears only when something overrides the app-wide default —
    /// otherwise it would be a second line on screen saying what the first already says. It works with no
    /// session on screen, where the pick lands on the one about to be started.
    /// </summary>
    private MenuFlyout BuildPermissionMenu()
    {
        var menu = new MenuFlyout();
        var effective = _chat.ActiveApprovalMode;
        foreach (var mode in new[] { ToolApprovalModes.Ask, ToolApprovalModes.Auto, ToolApprovalModes.Full })
        {
            var checkedItem = effective == mode;
            var item = new MenuItem
            {
                // The tick is drawn inside the row rather than by a toggle column: the column would sit to the
                // left of the tier's own icon and the two would read as one glyph doing a job twice. IsChecked
                // is still set, so the state stays on the item and not only in its pixels.
                Header = BuildPermissionHeader(mode, checkedItem),
                IsChecked = checkedItem,
            };
            item.Click += (_, _) =>
            {
                _chat.SelectApprovalMode(mode);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        if ((_chat.ActiveConversation?.ApprovalMode ?? _chat.SelectedApprovalMode) is { Length: > 0 })
        {
            var follow = new MenuItem
            {
                Header = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatToolPermissionFollowFormat"),
                    HubStrings.Get(ToolApprovalModeKey(_chat.DefaultApprovalMode))),
                FontSize = 12,
            };
            follow.Click += (_, _) =>
            {
                _chat.SelectApprovalMode(null);
                menu.Hide();
            };
            menu.Items.Add(follow);
        }

        return menu;
    }

    private static Control BuildPermissionHeader(string mode, bool checkedItem)
    {
        var danger = mode == ToolApprovalModes.Full;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Data = ThemeGeometry(ToolApprovalIconKey(mode)),
        };
        // Bound rather than read once: a brush taken by value here does not follow a theme switch.
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension(danger ? "Hub.DangerText" : "Hub.TextSecondary"));

        var title = new TextBlock
        {
            Text = HubStrings.Get(ToolApprovalModeKey(mode)),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        };
        var hint = new TextBlock
        {
            Text = HubStrings.Get(ToolApprovalModeHintKey(mode)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        if (danger)
        {
            title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
            hint.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
        }
        else
        {
            hint.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextTertiary"));
        }

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(hint);

        var tick = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry("Hub.Icon.Tick"),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsVisible = checkedItem,
        };
        tick.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        // One width for every row, so the ticks land in a column rather than wherever each description happens
        // to end.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinWidth = 300 };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(tick, 2);
        header.Children.Add(icon);
        header.Children.Add(text);
        header.Children.Add(tick);
        return header;
    }

    internal static string ToolApprovalIconKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "Hub.Icon.PermissionAuto",
        ToolApprovalModes.Full => "Hub.Icon.PermissionFull",
        _ => "Hub.Icon.PermissionAsk",
    };

    internal static string ToolApprovalModeKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "ToolApprovalAuto",
        ToolApprovalModes.Full => "ToolApprovalFull",
        _ => "ToolApprovalAsk",
    };

    internal static string ToolApprovalModeHintKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "ToolApprovalAutoHint",
        ToolApprovalModes.Full => "ToolApprovalFullHint",
        _ => "ToolApprovalAskHint",
    };

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

        var addPicture = new MenuItem { Header = HubStrings.Get("ChatAddImage") };
        addPicture.Click += async (_, _) => await AddLocalPictureAsync();
        menu.Items.Add(addPicture);

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
                var currentEffort = isSelected ? _chat.ActiveReasoningEffort : ChatReasoningEfforts.Default;
                foreach (var (effort, labelKey) in ReasoningChoices.Where(choice =>
                             choice.Value == ChatReasoningEfforts.Default
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

        menu.Items.Add(BuildRoutingMenuItem());
        return menu;
    }

    /// <summary>
    /// Who picks the model and the tier for this session. It is a switch under the model list rather than another
    /// row in it, because "自动" as a menu entry would read exactly like the gateway's model id
    /// <c>orcarouter/auto</c> — the same word, three meanings, and this is the one that changes what gets spent.
    /// </summary>
    private MenuItem BuildRoutingMenuItem()
    {
        var conversationId = _chat.ActiveConversation?.Id ?? "";
        var auto = conversationId.Length > 0 && _chat.RoutingFor(conversationId) == ChatRouting.Auto;
        var item = new MenuItem { Header = HubStrings.Get("ChatRoutingMenu") };
        foreach (var (value, labelKey, isAuto) in new (string, string, bool)[]
                 {
                     (ChatRouting.Manual, "ChatRoutingManual", false),
                     (ChatRouting.Auto, "ChatRoutingAuto", true),
                 })
        {
            var choice = new MenuItem
            {
                Header = HubStrings.Get(labelKey),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = auto == isAuto,
            };
            choice.Click += (_, _) =>
            {
                if (conversationId.Length > 0) _chat.SetRouting(conversationId, value);
                RefreshComposerChoices();
            };
            item.Items.Add(choice);
        }
        return item;
    }

    private static readonly (string Value, string LabelKey)[] ReasoningChoices =
    [
        (ChatReasoningEfforts.Default, "ChatReasoningDefault"),
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

        foreach (var picture in _pendingPictures)
        {
            var remove = new Button { Content = "×", Tag = picture };
            remove.Classes.Add("context-attachment-remove");
            remove.Click += (_, _) =>
            {
                if (remove.Tag is PendingPicture selected)
                {
                    _pendingPictures.Remove(selected);
                    RenderContextAttachments();
                    UpdateSendState();
                }
            };

            // The thumbnail is the label: a picture's file name says nothing about it, and a person deciding
            // whether this is the capture they meant has to see it. Bytes rather than a path because nothing of
            // this draft has reached the disk yet.
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var thumb = TryOpenBitmap(picture.Bytes);
            chip.Children.Add(thumb is null
                ? new TextBlock { Classes = { "muted" }, Text = picture.Name, MaxWidth = 120 }
                : new Image
                {
                    Source = thumb,
                    Width = 34,
                    Height = 34,
                    Stretch = Stretch.Uniform,
                });
            chip.Children.Add(remove);
            var frame = new Border { Child = chip };
            frame.Classes.Add("context-attachment");
            ToolTip.SetTip(frame, $"{picture.Name} · {picture.Bytes.LongLength} bytes");
            ContextAttachmentPanel.Children.Add(frame);
        }

        ContextAttachmentPanel.IsVisible = _contextAttachments.Count > 0 || _pendingPictures.Count > 0;
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

    // ───────────────────────── Pictures ─────────────────────────
    //
    // A picture is a second channel next to the text attachments: a folder becomes prompt text, a picture stays
    // bytes on the wire. They share the chip row and nothing else — reading one through the other would either
    // send a PNG as source code or quietly drop it.

    /// <summary>Offers one picture to the composer. The rules are Core's, the same four a captured frame is
    /// admitted by, and the notice names which one fired: "capture a smaller region" and "attach fewer at once"
    /// are different fixes, and a notice that only says "no" gets the same file picked again.</summary>
    private void AddPendingPicture(byte[] bytes, string name)
    {
        var verdict = ChatImageFormat.Admit(bytes, _pendingPictures.Count);
        if (verdict != ChatImageVerdict.Accepted)
        {
            AppendNotice(ImageNotice(verdict), danger: true);
            return;
        }

        _pendingPictures.Add(new PendingPicture(bytes, name));
        RenderContextAttachments();
        UpdateSendState();
    }

    /// <summary>Same admission for a picture that arrives as a file — picked, dropped, or copied from Explorer.
    /// The size is checked before the bytes are read, so dropping a folder of raw captures cannot freeze the
    /// window on a gigabyte Hub was going to refuse anyway.</summary>
    private void AddPictureFromPath(string path)
    {
        byte[]? bytes;
        ChatImageVerdict verdict;
        try
        {
            bytes = ConversationStore.ReadCandidateFile(path, out verdict);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
            return;
        }

        if (bytes is null)
        {
            AppendNotice(ImageNotice(verdict), danger: true);
            return;
        }

        AddPendingPicture(bytes, System.IO.Path.GetFileName(path));
    }

    private static string ImageNotice(ChatImageVerdict verdict) => verdict switch
    {
        ChatImageVerdict.Empty => HubStrings.Get("ChatImageEmpty"),
        ChatImageVerdict.Unrecognized => HubStrings.Get("ChatImageUnrecognized"),
        ChatImageVerdict.TooLarge => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatImageTooLarge"), ChatImageFormat.MaxImageBytes / (1024 * 1024)),
        _ => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatImageTooMany"), ChatImageFormat.MaxImagesPerMessage),
    };

    /// <summary>What a refused send should say. The picture refusals are worded with the limit in them, so they
    /// go through the same formatter the composer used when it refused the picture on the way in — a key shown
    /// raw would print the brace.</summary>
    private static string NoticeFor(string? refusalKey) => refusalKey switch
    {
        "ChatImageEmpty" => ImageNotice(ChatImageVerdict.Empty),
        "ChatImageUnrecognized" => ImageNotice(ChatImageVerdict.Unrecognized),
        "ChatImageTooLarge" => ImageNotice(ChatImageVerdict.TooLarge),
        "ChatImageTooMany" => ImageNotice(ChatImageVerdict.TooMany),
        _ => HubStrings.Get(refusalKey ?? "ChatFailed"),
    };

    private async Task AddLocalPictureAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFileAsync(owner, HubStrings.Get("ChatPickImageTitle"), PictureFileTypes);
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatImageNotLocal"), danger: true);
            return;
        }

        if (result.Outcome != PickOutcome.Picked || result.Path is not { } path) return;
        AddPictureFromPath(path);
    }

    /// <summary>The picker's filter is a courtesy, not a rule: what a picture *is* stays a decision about the
    /// bytes' header, because a pasted capture has no file name to match.</summary>
    private static IReadOnlyList<FilePickerFileType> PictureFileTypes { get; } =
    [
        new("PNG") { Patterns = ["*.png"] },
        new("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
        new("GIF") { Patterns = ["*.gif"] },
        new("WebP") { Patterns = ["*.webp"] },
    ];

    /// <summary>Ctrl+V in the composer: a picture on the clipboard becomes a chip, and everything else keeps
    /// being the text paste the box already does. Claiming the keystroke only once there is a picture to take is
    /// what keeps an ordinary paste working — a paste handler that eats text is a worse bug than no paste.</summary>
    private async Task<bool> TryPastePictureAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        IAsyncDataTransfer? transfer;
        try
        {
            transfer = await clipboard.TryGetDataAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return await TakeClipboardAsync(transfer);
    }

    /// <summary>The paste's one decision: did a picture get taken, or should the box paste text as it always
    /// has? Split out because it is the half a check can be handed a payload for.</summary>
    private async Task<bool> TakeClipboardAsync(IAsyncDataTransfer? transfer)
        => transfer is not null && await TakePicturesAsync(transfer) > 0;

    /// <summary>
    /// The paste's actual decision, separated from the clipboard so it can be handed a transfer of a known shape.
    /// A screenshot arrives as a bitmap with no file name, a copied file arrives as a storage item, and a copy
    /// from some applications arrives as both — the loop takes what is a picture and leaves the rest alone.
    /// </summary>
    private async Task<int> TakePicturesAsync(IAsyncDataTransfer transfer)
    {
        var taken = 0;
        foreach (var item in transfer.Items)
        {
            if (item.Formats.Contains(DataFormat.Bitmap)
                && await item.TryGetRawAsync(DataFormat.Bitmap) is Bitmap bitmap)
            {
                AddPendingPicture(EncodePng(bitmap), HubStrings.Get("ChatPastedImageName"));
                taken++;
            }
            else if (item.Formats.Contains(DataFormat.File)
                     && await item.TryGetRawAsync(DataFormat.File) is { } raw)
            {
                foreach (var path in LocalPathsOf(raw)) AddPictureFromPath(path);
                taken++;
            }
        }

        return taken;
    }

    /// <summary>A clipboard or drop payload hands back one item, a list of them, or an array — the shape is the
    /// platform's business, so the view accepts all three rather than betting on one.</summary>
    private static IEnumerable<string> LocalPathsOf(object raw)
    {
        IReadOnlyList<IStorageItem> items = raw switch
        {
            IStorageItem one => [one],
            IEnumerable<IStorageItem> many => many.ToArray(),
            _ => [],
        };
        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    /// <summary>A bitmap has to become the bytes Hub keeps, and PNG is the only honest answer: a capture loses
    /// nothing to it, and the header is what says so on the wire.</summary>
    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new System.IO.MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static bool DragCarriesFiles(DragEventArgs e)
        => e.DataTransfer.Items.Any(item => item.Formats.Contains(DataFormat.File));

    private void OnComposerDrop(object? sender, DragEventArgs e)
    {
        if (!DragCarriesFiles(e)) return;
        e.Handled = true;
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.File) is { } raw)
                foreach (var path in LocalPathsOf(raw)) AddPictureFromPath(path);
    }

    /// <summary>The bytes of every picture waiting in the composer, in chip order, or null when there are none —
    /// the shape the send and steer paths take so an empty draft stays exactly the request it was before.</summary>
    private IReadOnlyList<byte[]>? PendingPictureBytes()
        => _pendingPictures.Count == 0 ? null : _pendingPictures.Select(picture => picture.Bytes).ToArray();

    /// <summary>
    /// The pictures one sent message carries, painted from the file Hub kept. Bounded so a 5K capture cannot push
    /// the chat column open, and a file that is no longer there says so rather than drawing nothing at all — the
    /// transcript claims a picture was sent, and that claim has to stay checkable.
    /// </summary>
    private Control BuildTurnPictures(string conversationId, IReadOnlyList<ChatImage> images)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Classes.Add("turn-pictures");
        foreach (var image in images)
        {
            var path = _chat.StoredImagePath(conversationId, image.File);
            var bitmap = path is null ? null : TryOpenBitmap(path);
            Control cell = bitmap is not null
                ? new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 260,
                    MaxHeight = 200,
                }
                : new TextBlock
                {
                    Classes = { "muted" },
                    Text = HubStrings.Get("ChatImageGone"),
                };
            ToolTip.SetTip(cell, $"{image.File} · {image.MediaType} · {image.Bytes} bytes");
            var frame = new Border { Child = cell, Classes = { "turn-picture" } };
            row.Children.Add(frame);
        }

        return row;
    }

    private static Bitmap? TryOpenBitmap(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The same preview from bytes, because a draft has not reached the disk. A picture whose header
    /// Hub recognizes but whose pixels it cannot decode stays in the queue under its file name rather than
    /// vanishing — what the model is going to receive is the bytes, not this preview.</summary>
    private static Bitmap? TryOpenBitmap(byte[] bytes)
    {
        try
        {
            return new Bitmap(new System.IO.MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
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
        var conversation = _chat.ActiveConversation;
        var run = conversation is null ? null : _chat.RunFor(conversation.Id);

        // The live bubble is not a stored turn. It comes off the flow before the stored rows are laid down and
        // goes back on the end afterwards, so a repaint in the middle of a reply cannot bury it mid-transcript.
        // A bubble belonging to another session is closed for good: its run keeps going, and coming back to it
        // rebuilds the bubble from what arrived while it was out of sight.
        // A bubble that outlives its run would keep animating a reply that has already been written to the
        // transcript, where it now belongs — so the absence of a streaming run for this session closes it,
        // whoever happened to ask for a repaint.
        if (_live is { } attached && (run is not { IsStreaming: true } || attached.ConversationId != conversation?.Id))
            CloseLive();
        if (_live is { } held) MessageFlow.Children.Remove(held.Row);

        var visible = new List<(int Index, ChatTurn Turn)>();
        if (conversation is not null)
        {
            for (var i = 0; i < conversation.Messages.Count; i++)
            {
                if (conversation.Messages[i].Role != ChatRoles.System) visible.Add((i, conversation.Messages[i]));
            }
        }

        // A decision changes what an already-rendered turn looks like without changing how many turns there
        // are, which is precisely the case incremental rendering cannot see: the stamp is what lets a card
        // collapse into its one-line record instead of keeping buttons that no longer mean anything.
        var approvalStamp = ApprovalStamp(conversation);
        var approvalChanged = approvalStamp != _renderedApprovalStamp;
        _renderedApprovalStamp = approvalStamp;

        if (visible.Count == 0)
        {
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation?.Id;
            _renderedCount = 0;
            EmptyState.IsVisible = run is not { IsStreaming: true };
            if (run is { IsStreaming: true }) MessageFlow.Children.Add(AttachLive(run).Row);
            UpdateScrollAffordance();
            return;
        }

        if (_renderedConversationId != conversation!.Id || visible.Count < _renderedCount || approvalChanged)
        {
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation.Id;
            _renderedCount = 0;
        }

        var lastIndex = conversation.Messages.Count - 1;
        for (var i = _renderedCount; i < visible.Count; i++)
        {
            var (index, turn) = visible[i];
            AppendRenderedTurn(conversation.Id, index, turn, isLast: index == lastIndex,
                markdown: i >= visible.Count - EagerMarkdownLimit);
        }

        EmptyState.IsVisible = false;
        if (run is { IsStreaming: true }) MessageFlow.Children.Add(AttachLive(run).Row);
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

    private void AppendRenderedTurn(string conversationId, int index, ChatTurn turn, bool isLast, bool markdown)
    {
        var fromUser = turn.Role == ChatRoles.User;
        var body = new StackPanel { Spacing = 8 };

        // Whose words these are is not optional information: a peer session's message would otherwise read as
        // something the user typed. The stored turn carries the source's id, so the title is looked up here — and
        // a session deleted since still gets a line rather than no label at all.
        if (turn.InjectedFrom is { Length: > 0 } peer)
        {
            var name = _chat.SessionTitleFor(peer);
            var origin = new TextBlock
            {
                Classes = { "muted", "peer-origin" },
                Text = name is { Length: > 0 } titled
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("ChatPeerOriginFormat"), titled)
                    : HubStrings.Get("ChatPeerOriginGone"),
            };
            ToolTip.SetTip(origin, peer);
            body.Children.Add(origin);
        }

        body.Children.Add(new TextBlock { Text = turn.Text, TextWrapping = TextWrapping.Wrap });

        // What the person attached is shown from the file Hub kept, not from anything this view remembers: the
        // transcript is the only copy of "this message had a picture in it", and a row rebuilt after a restart
        // has to look the same as the one that was sent. A frame the assistant captured itself stays out of the
        // flow — its approval card already says which window was grabbed, and this row is about what the user said.
        if (fromUser && turn.Images.Count > 0) body.Children.Add(BuildTurnPictures(conversationId, turn.Images));

        Control? approvalSurface = null;

        // A call that needed permission carries its own record: the question with its buttons while it waits,
        // one quiet line once it does not. A call that never needed asking gets nothing drawn here, which is why
        // the approval state — not the presence of a tool call — is what decides. A write is the exception: it
        // changed a file whether or not anybody was asked, and the line is where its undo lives.
        if (turn.ToolCallId is { Length: > 0 } callId)
        {
            if (turn.ApprovalState == ChatApprovalStates.Pending)
                approvalSurface = BuildApprovalCard(conversationId, callId, turn);
            else if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 })
                approvalSurface = BuildCallRecord(conversationId, callId, turn);
        }
        if (turn.PlanApprovalState is { Length: > 0 })
        {
            approvalSurface = turn.PlanApprovalState == PlanApprovalStates.Pending
                ? BuildPlanApprovalCard(conversationId, index)
                : BuildPlanApprovalRecord(turn.PlanApprovalState);
        }

        // A function-call or tool-result turn gets no action bar: it is not a readable message, and acting on
        // half of a call/result pair orphans the other half (the pipeline sends them to the provider as one
        // exchange). `index` is the only thing that gates the bar, so nulling it here leaves stored indexes
        // untouched for every other turn.
        var actionableIndex = turn.ToolCallId is { Length: > 0 } ? (int?)null : index;
        MessageFlow.Children.Add(BuildMessageRow(fromUser, body, actionableIndex, turn.Role, turn.Text, isLast, turn.At));
        _renderedCount++;

        // User text is plain by nature; only assistant turns carry Markdown worth rendering.
        if (!fromUser && markdown && turn.Text.Length > 0)
            MarkdownMessageRenderer.RenderInto(body, turn.Text);
        if (approvalSurface is not null) body.Children.Add(approvalSurface);
    }

    /// <summary>The question stated once: which tool, with what, and the three answers it accepts. The arguments
    /// stay visible because "run file_write" is not a decision — what it writes is.</summary>
    private Control BuildApprovalCard(string conversationId, string callId, ChatTurn turn)
    {
        var card = new StackPanel { Spacing = 4 };
        card.Children.Add(new TextBlock
        {
            Classes = { "approval-question" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatApprovalQuestionFormat"), turn.ToolName ?? ""),
        });
        AddApprovalDetail(card, "ChatApprovalArguments", turn.ToolArguments);
        AddApprovalDetail(card, "ChatApprovalChangePreview", turn.ApprovalPreview);

        var actions = new StackPanel { Classes = { "approval-actions" } };
        actions.Children.Add(ApprovalButton("ApprovalAllow",
            () => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: false)));
        actions.Children.Add(ApprovalButton("ApprovalAllowAlways",
            () => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: true)));
        actions.Children.Add(ApprovalButton("ApprovalDeny",
            () => ResolveApproval(conversationId, callId, approved: false, alwaysAllow: false)));
        card.Children.Add(actions);

        return new Border { Classes = { "approval-card" }, ClipToBounds = true, Child = card };
    }

    private Control BuildPlanApprovalCard(string conversationId, int turnIndex)
    {
        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(new TextBlock
        {
            Classes = { "approval-question" },
            Text = HubStrings.Get("ChatPlanApprovalQuestion"),
        });

        var actions = new StackPanel { Classes = { "approval-actions" } };
        actions.Children.Add(ApprovalButton("ChatPlanApprove",
            () => ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Approved)));
        actions.Children.Add(ApprovalButton("ChatPlanRevise",
            () => ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.RevisionRequested)));
        actions.Children.Add(ApprovalButton("ChatPlanReject",
            () => ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Rejected)));
        card.Children.Add(actions);
        return new Border { Classes = { "approval-card", "plan-approval-card" }, ClipToBounds = true, Child = card };
    }

    private static Control BuildPlanApprovalRecord(string state)
    {
        var key = state switch
        {
            PlanApprovalStates.Approved => "ChatPlanApproved",
            PlanApprovalStates.RevisionRequested => "ChatPlanRevisionRequested",
            PlanApprovalStates.Rejected => "ChatPlanRejected",
            _ => "ChatPlanRejected",
        };
        return new Border
        {
            Classes = { "approval-record", "plan-approval-record" },
            Child = new TextBlock { Classes = { "approval-record-text" }, Text = HubStrings.Get(key) },
        };
    }

    private static void AddApprovalDetail(StackPanel card, string labelKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        card.Children.Add(new TextBlock { Classes = { "approval-label" }, Text = HubStrings.Get(labelKey) });
        card.Children.Add(new TextBlock
        {
            Classes = { "approval-detail" },
            Text = value,
            MaxHeight = 180,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private Button ApprovalButton(string textKey, Action onClick)
    {
        var button = new Button
        {
            Classes = { "approval-action" },
            Content = HubStrings.Get(textKey),
            Tag = textKey,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button CloseActionButton(Action onClick)
    {
        var glyph = new TextBlock
        {
            Text = "×",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            Width = 15,
            Height = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var button = new Button { Content = glyph, Tag = "Cancel" };
        button.Classes.Add("message-action");
        button.Classes.Add("message-action-icon");
        ToolTip.SetTip(button, HubStrings.Get("Cancel"));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The decided call, still in the record and no longer actionable: what became of it is the only
    /// thing worth the space — and, for a write, the one exit it has while a copy is left behind it.</summary>
    private Control BuildCallRecord(string conversationId, string callId, ChatTurn turn)
    {
        // A write that never had to ask still changed the file, so it gets a line of its own naming the file
        // rather than the tool: who was asked is a permission detail, what moved is the record.
        var line = turn.ApprovalState is { Length: > 0 } state
            ? $"{HubStrings.Get(state switch
                {
                    ChatApprovalStates.Approved => "ChatApprovalResolvedApproved",
                    ChatApprovalStates.Denied => "ChatApprovalResolvedDenied",
                    _ => "ChatApprovalResolvedSuperseded",
                })} · {turn.ToolName ?? ""}"
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWriteRecordFormat"), ChatUndoStore.WritePathOf(turn.ToolArguments));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Classes = { "approval-record-text" },
            Text = line,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (turn.UndoName is { Length: > 0 })
        {
            row.Children.Add(BuildUndoButton(conversationId, callId,
                ChatUndoStore.WritePathOf(turn.ToolArguments)));
        }

        return new Border { Classes = { "approval-record" }, Child = row };
    }

    private Button BuildUndoButton(string conversationId, string callId, string path)
    {
        var button = new Button
        {
            Classes = { "undo-action" },
            Content = HubStrings.Get("ChatUndoButton"),
            Tag = "ChatUndoButton",
        };
        ToolTip.SetTip(button, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatUndoTip"), path));
        button.Click += (_, _) => RevertWrite(conversationId, callId, button);
        return button;
    }

    /// <summary>One click, one file back. A refusal is a line in the notice row rather than a dialog: the reason
    /// nothing moved has to be readable while the transcript stays exactly where it was.</summary>
    private async void RevertWrite(string conversationId, string callId, Button source)
    {
        source.IsEnabled = false;
        var outcome = await _chat.RevertWriteAsync(conversationId, callId);
        // A revert that worked raises Changed on its way out, and the rebuild that drops the spent button comes
        // with it — so only a refusal has anything left to re-enable.
        if (outcome.Succeeded) return;
        source.IsEnabled = true;
        var key = outcome.RefusalKey ?? outcome.Verdict switch
        {
            UndoVerdict.CopyMissing => "ChatUndoCopyMissing",
            UndoVerdict.ChangedSince => "ChatUndoChangedSince",
            UndoVerdict.TargetMissing => "ChatUndoTargetMissing",
            UndoVerdict.NotText => "ChatUndoNotText",
            UndoVerdict.RefusedPath => "ChatUndoRefusedPath",
            _ => "ChatUndoWriteFailed",
        };
        AppendNotice(HubStrings.Get(key), danger: key is "ChatUndoWriteFailed" or "ChatUndoRefusedPath");
    }

    /// <summary>Hands one decision to the workspace. A refusal is spoken out loud rather than swallowed: the card
    /// would otherwise sit there having done nothing, which reads as a broken button.</summary>
    private void ResolveApproval(string conversationId, string callId, bool approved, bool alwaysAllow)
    {
        if (_chat.TryResolveApproval(conversationId, callId, approved, alwaysAllow, out var refusalKey)) return;
        AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
    }

    private void ResolvePlanApproval(string conversationId, int turnIndex, string decision)
    {
        if (!_chat.TryResolvePlanApproval(conversationId, turnIndex, decision, out var refusalKey))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
            return;
        }

        if (decision == PlanApprovalStates.RevisionRequested)
        {
            SetComposerMode(ChatModes.Plan);
            InputBox.Text = HubStrings.Get("ChatPlanRevisionPrompt") + "\n";
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }

        ConversationStateChanged?.Invoke();
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

    /// <summary>
    /// Builds one message row. User rows: a Grid (full-width for hover) carrying a right-aligned pill.
    /// Assistant rows: a borderless Border carrying plain text. Both carry the message-row class and,
    /// when <paramref name="index"/> is non-null, a hover-revealed action row.
    /// A null <paramref name="index"/> (the live streaming bubble / just-sent user pill) carries no
    /// action bar: there is nothing stable to act on until the turn is persisted.
    /// </summary>
    private Control BuildMessageRow(
        bool fromUser, StackPanel body, int? index, string role, string text, bool isLast, DateTimeOffset? at = null)
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
            var actions = BuildActionBar(messageIndex, role, text, isLast, at, body);
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

    private Control BuildActionBar(int index, string role, string text, bool isLast, DateTimeOffset? at, StackPanel body)
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
            bar.Children.Add(IconActionButton("EditMessage", "Hub.Icon.Edit", () => BeginInlineEdit(index, body, bar)));
        }
        else
        {
            bar.Children.Add(IconActionButton("CopyMessage", "Hub.Icon.Copy", () => CopyToClipboard(text)));
            if (isLast)
                bar.Children.Add(IconActionButton("RegenerateMessage", "Hub.Icon.Refresh", RegenerateAsync));
            bar.Children.Add(IconActionButton("BranchFromHere", "Hub.Icon.Branch", () => BranchFromHere(index)));
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

    /// <summary>One icon in a message's action row. The size and pen are per-glyph because a cross is two
    /// strokes meeting in the middle: at the weight an outline icon carries it, the crossing fills its plate
    /// and reads as one solid mark.</summary>
    private static Button IconActionButton(
        string textKey, string geometryKey, Action onClick, double size = 13, double thickness = 1.7)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = thickness,
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
        var streaming = IsViewedStreaming;
        var hasText = (InputBox.Text ?? "").Trim().Length > 0;
        var hasPicture = _pendingPictures.Count > 0;
        // A picture in the chip row is as much "there is something to send" as text is, and it says so in the
        // tooltip too: while a reply is streaming, sending this means steering it rather than stopping it.
        var saysSomething = hasText || hasPicture;
        // A steer already waiting is spent: the button must not offer a second one before the first is taken.
        SendButton.IsEnabled = ViewedRun?.HasQueuedSteer != true && (streaming || saysSomething);
        SendButton.Content = BuildSendIcon(streaming && !saysSomething);
        ToolTip.SetTip(SendButton, HubStrings.Get(streaming
            ? saysSomething ? "ChatSteer" : "Stop"
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
        if (ViewedRun is { IsStreaming: true } run)
        {
            if (run.HasQueuedSteer) return;
            var steerText = (InputBox.Text ?? "").Trim();
            var steerPictures = PendingPictureBytes();
            if (steerText.Length > 0 || steerPictures is { Count: > 0 })
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

                // The run does the rest: the current segment is cancelled, written as far as it got, and the
                // next one answers this text — none of which is this view's business any more. The composer is
                // only emptied once the steer is actually on the run; a reply that finished in the meantime
                // would otherwise eat the message the person just typed.
                if (!_chat.TrySteer(run.ConversationId, steerText, steerContext, steerPictures))
                {
                    UpdateSendState();
                    return;
                }

                InputBox.Text = "";
                _contextAttachments.Clear();
                _pendingPictures.Clear();
                RenderContextAttachments();
                if (_live is { } live) live.Status.Text = HubStrings.Get("ChatSteering");
                UpdateSendState();
                return;
            }

            _chat.RequestStop(run.ConversationId);
            return;
        }

        var text = (InputBox.Text ?? "").Trim();
        var pictures = PendingPictureBytes();
        // A picture with no question under it is a message: "look at this" is what people actually send, and a
        // send button that stays grey because the box is empty would say otherwise.
        if (text.Length == 0 && pictures is null) return;
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
        _pendingPictures.Clear();
        RenderContextAttachments();
        InputBox.Text = "";
        SendTextAsync(text, context, pictures);
    }

    /// <summary>Hands the message to the workspace and starts a run for it. The user's own turn is painted from
    /// the transcript like any other row, so there is no second copy of it here to keep in step.</summary>
    private void SendTextAsync(string text, string? attachedContext = null, IReadOnlyList<byte[]>? pictures = null)
    {
        if (text.Length == 0 && pictures is not { Count: > 0 }) return;
        if (_chat.ActiveConversation is null) _chat.StartConversation();
        if (_chat.ActiveConversation is not { } conversation) return;

        if (!_chat.TryEnqueueSend(conversation.Id, text, attachedContext, pictures, out var refusalKey))
        {
            AppendNotice(NoticeFor(refusalKey), danger: true);
            return;
        }

        ConversationStateChanged?.Invoke();
    }

    private void RegenerateAsync()
    {
        if (_chat.ActiveConversation is not { } conversation || IsViewedStreaming) return;
        if (!_chat.Regenerate(conversation.Id)) return;

        ForceRebuildMessages();
        if (!_chat.TryEnqueueContinuation(conversation.Id, out var refusalKey))
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatFailed"), danger: true);
    }

    /// <summary>Forks the conversation at this message into a fresh session and switches to it. Refused while
    /// a reply is streaming, because the history is still growing and the cut point would not be the one that
    /// was clicked. The message flow and the sidebar both repaint through the workspace's Changed event.</summary>
    private void BranchFromHere(int index)
    {
        if (_chat.ActiveConversation is not { } conversation || IsViewedStreaming) return;
        _chat.BranchFrom(conversation.Id, index);
    }

    /// <summary>
    /// Puts a user message back into an editable box where it stands, and swaps the row's hover actions for
    /// cancel and confirm so both exits are visible instead of something to remember. Enter commits,
    /// Shift+Enter adds a line, Escape or a click anywhere outside abandons the edit.
    ///
    /// Committing drops everything after this turn. That used to be gated by a confirmation dialog that only
    /// appeared once the retyping was already done; the edit icon's tooltip carries the consequence now, at
    /// the moment the edit is chosen instead.
    /// </summary>
    private void BeginInlineEdit(int index, StackPanel body, StackPanel bar)
    {
        if (IsViewedStreaming || body.Children.FirstOrDefault() is not TextBlock original) return;

        var untouched = original.Text ?? "";
        var normalActions = bar.Children.ToList();
        var closed = false;
        var editor = new TextBox
        {
            Text = untouched,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = original.FontSize,
        };
        editor.Classes.Add("message-edit");

        void Close()
        {
            if (closed) return;
            closed = true;
            body.Children.Remove(editor);
            body.Children.Insert(0, original);
            bar.Children.Clear();
            foreach (var action in normalActions) bar.Children.Add(action);
        }

        void Commit()
        {
            if (closed) return;
            var edited = editor.Text.Trim();
            var conversation = _chat.ActiveConversation;
            if (edited.Length == 0 || edited == untouched
                || IsViewedStreaming || conversation is null || !_chat.EditAndResend(conversation.Id, index, edited))
            {
                Close();
                return;
            }

            // The rebuild throws this whole subtree away, so there is nothing left to restore.
            closed = true;
            ForceRebuildMessages();
            if (!_chat.TryEnqueueContinuation(conversation.Id, out var refusalKey))
                AppendNotice(HubStrings.Get(refusalKey ?? "ChatFailed"), danger: true);
        }

        body.Children.Remove(original);
        body.Children.Insert(0, editor);
        bar.Children.Clear();
        bar.Children.Add(CloseActionButton(Close));
        bar.Children.Add(IconActionButton("ConfirmEdit", "Hub.Icon.Tick", Commit));
        editor.Focus();
        editor.CaretIndex = editor.Text.Length;

        // Registered on the tunnel for the same reason the composer is: a TextBox with AcceptsReturn marks
        // Enter handled in order to insert a newline, so a plain subscriber never sees the key.
        editor.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                Commit();
            }
        }, RoutingStrategies.Tunnel);

        // Clicking away abandons the edit — but not when focus only moved to this editor's own two buttons,
        // which have to be allowed to receive the click, and not when the window itself lost focus, which
        // would discard a draft nobody meant to drop.
        editor.LostFocus += (_, e) =>
        {
            if (e.NewFocusedElement is not Visual next || ReferenceEquals(next, editor)) return;
            if (next.GetLogicalAncestors().Any(ancestor
                    => ReferenceEquals(ancestor, bar) || ReferenceEquals(ancestor, body))) return;
            Close();
        };
    }

    /// <summary>
    /// The bubble for the reply now arriving in this session, created when the session was not on screen a
    /// moment ago. Its text is read off the run, which is what makes "switch away, switch back" show exactly
    /// what arrived while it was hidden — the view keeps no copy of a reply it is not painting.
    /// </summary>
    private LiveBubble AttachLive(ConversationRun run)
    {
        if (_live is { } existing && existing.ConversationId == run.ConversationId)
        {
            RefreshLive(existing);
            return existing;
        }

        CloseLive();
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(preview);
        var activity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        activity.Classes.Add("chat-activity");
        var dotsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        dotsPanel.Classes.Add("chat-activity-dots");
        var dots = Enumerable.Range(0, 3).Select(_ =>
        {
            var dot = new Ellipse();
            dot.Classes.Add("chat-activity-dot");
            return dot;
        }).ToArray();
        foreach (var dot in dots) dotsPanel.Children.Add(dot);
        activity.Children.Add(dotsPanel);
        var status = new TextBlock { Text = HubStrings.Get("ChatPreparing") };
        status.Classes.Add("chat-activity-label");
        activity.Children.Add(status);
        activity.Children.Add(new TextBlock
        {
            Text = "·",
            Classes = { "chat-activity-elapsed" },
        });
        var elapsed = new TextBlock
        {
            Text = "0s",
            Classes = { "chat-activity-elapsed" },
        };
        activity.Children.Add(elapsed);
        body.Children.Add(activity);

        var bubble = new LiveBubble
        {
            ConversationId = run.ConversationId,
            Row = BuildMessageRow(false, body, null, ChatRoles.Assistant, "", isLast: true),
            Preview = preview,
            Status = status,
            Elapsed = elapsed,
            Dots = dots,
        };
        bubble.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        bubble.Timer.Tick += (_, _) =>
        {
            if (_live is not { } current) return;
            current.Frame++;
            for (var i = 0; i < current.Dots.Length; i++)
                current.Dots[i].Opacity = (i + current.Frame) % current.Dots.Length == 0 ? 1 : 0.35;
            var seconds = current.Watch.Elapsed;
            current.Elapsed.Text = seconds.TotalMinutes >= 1
                ? $"{(int)seconds.TotalMinutes}m {seconds.Seconds:D2}s"
                : $"{Math.Max(0, (int)seconds.TotalSeconds)}s";
        };
        _live = bubble;
        bubble.Watch.Restart();
        bubble.Timer.Start();
        RefreshLive(bubble);
        UpdateSendState();
        ScrollToEnd();
        return bubble;
    }

    /// <summary>Throws the bubble away, not the reply: the run keeps streaming and a later attach reads the
    /// text back off it. The timer goes with the row, because nothing is being animated any more.</summary>
    private void CloseLive()
    {
        if (_live is not { } live) return;
        live.Timer?.Stop();
        live.Timer = null;
        live.Watch.Stop();
        MessageFlow.Children.Remove(live.Row);
        _live = null;
        UpdateSendState();
    }

    private void RefreshLive(LiveBubble bubble)
    {
        if (_chat.RunFor(bubble.ConversationId) is not { } run) return;
        bubble.Preview.Text = run.LiveText;
        if (run.LiveText.Length == 0) bubble.ShowedText = false;
        else if (!bubble.ShowedText)
        {
            bubble.ShowedText = true;
            bubble.Status.Text = HubStrings.Get("ChatGenerating");
        }

        ScrollToEndIfSticky();
    }

    /// <summary>Says how a run ended. The reason is recorded where it happened rather than inferred here from
    /// exception types, which is how "the user pressed stop" and "the stream stalled" used to blur together.</summary>
    private void AppendRunNotice(RunOutcome outcome)
    {
        if (outcome.NoticeKey is { } key)
        {
            AppendNotice(HubStrings.Get(key) + (outcome.Detail ?? ""), outcome.NoticeDanger);
            return;
        }

        if (!outcome.ReceivedText) AppendNotice(HubStrings.Get("ChatNoResponse"), danger: true);
    }

    private ConversationRun? ViewedRun
        => _chat.ActiveConversation is { } viewed ? _chat.RunFor(viewed.Id) : null;

    private bool IsViewedStreaming => ViewedRun is { IsStreaming: true };

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

    internal bool HasThemedMarkdownTable()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedTable);

    internal bool HasThemedMarkdownDocument()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedDocument);

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

    /// <summary>The floating button's shape: a square plate, not the pill it used to be. Read off the
    /// properties rather than <c>Bounds</c> because the button is collapsed until the flow scrolls.</summary>
    internal bool ScrollToBottomIsSquarePlateForCheck
        => ScrollToBottomButton is { Width: 28, Height: 28, CornerRadius: { TopLeft: 8 } };

    /// <summary>Whether the button carries the down-arrow glyph, matched against the resource itself so a
    /// different arrow cannot pass.</summary>
    internal bool ScrollToBottomShowsArrowForCheck
    {
        get
        {
            if (ScrollToBottomButton.GetLogicalDescendants()
                    .OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()?.Data is not { } data) return false;
            return Application.Current is { } app
                   && app.TryGetResource("Hub.Icon.ArrowDown", app.ActualThemeVariant, out var arrow)
                   && ReferenceEquals(data, arrow);
        }
    }

    /// <summary>The first rendered message row, so a check can prove a reload reuses it instead of rebuilding
    /// the whole flow (the incremental path's whole point).</summary>
    internal object? FirstBubbleForCheck => MessageRows.FirstOrDefault();

    // ── Approval card ──
    private IEnumerable<Border> ApprovalCards
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Border>())
            .Where(card => card.Classes.Contains("approval-card"));

    /// <summary>How many calls are asking. A request the user has to answer is never hover-gated — a hidden
    /// actionable request looks exactly like a reply that stopped working — so this reads the flow as painted.</summary>
    internal int PendingApprovalCardsForCheck => ApprovalCards.Count();

    internal Border? ApprovalCardForCheck => ApprovalCards.FirstOrDefault();

    internal bool ApprovalCardOnScreenForCheck
        => ApprovalCards.FirstOrDefault() is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };

    internal string ApprovalCardTextForCheck
        => ApprovalCards.FirstOrDefault() is { } card
            ? string.Join(" | ", card.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "").Where(text => text.Length > 0))
            : "";

    internal string[] ApprovalCardActionsForCheck
        => ApprovalCards.FirstOrDefault() is { } card
            ? card.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("approval-action"))
                .Select(button => button.Tag as string ?? "").ToArray()
            : [];

    /// <summary>Whether any of the card's buttons is drawn as a destructive act. Refusing a tool call is an
    /// ordinary answer to an ordinary question, and painting it as a detonation is how a permission UI ends up
    /// trained on saying yes.</summary>
    internal bool ApprovalCardActionsAreCalmForCheck
        => ApprovalCards.FirstOrDefault() is { } card
           && !card.GetLogicalDescendants().OfType<Button>().Any(button => button.Classes.Contains("destructive"));

    internal void ClickApprovalActionForCheck(string actionKey)
    {
        var button = ApprovalCards.FirstOrDefault()?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == actionKey);
        if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private IEnumerable<Border> PlanApprovalCards
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Border>())
            .Where(card => card.Classes.Contains("plan-approval-card"));

    internal int PendingPlanApprovalCardsForCheck => PlanApprovalCards.Count();

    internal bool PlanApprovalCardOnScreenForCheck
        => PlanApprovalCards.FirstOrDefault() is { IsVisible: true } card
           && card.Bounds is { Width: > 0, Height: > 0 };

    internal string PlanApprovalMessageTextForCheck
        => string.Join(" ", MessageFlow.Children
            .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
            .Select(viewer => viewer.Tag as string ?? ""));

    internal bool PlanApprovalMarkdownOnScreenForCheck
        => MessageFlow.Children
            .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
            .FirstOrDefault(viewer => (viewer.Tag as string)?.Contains("Reviewed plan", StringComparison.Ordinal) == true)
            is { IsVisible: true, Bounds: { Width: > 0, Height: > 0 } };

    internal string[] PlanApprovalActionsForCheck
        => PlanApprovalCards.FirstOrDefault() is { } card
            ? card.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("approval-action"))
                .Select(button => button.Tag as string ?? "").ToArray()
            : [];

    internal void ClickPlanApprovalActionForCheck(string actionKey)
    {
        var button = PlanApprovalCards.FirstOrDefault()?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == actionKey);
        if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The one-line records of calls already decided, in the order they appear.</summary>
    internal string[] ApprovalRecordsForCheck
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Border>())
            .Where(record => record.Classes.Contains("approval-record"))
            .Select(record => record.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault()?.Text ?? "")
            .ToArray();

    // ── the undo on a write's record line ──
    private IEnumerable<Button> UndoButtons
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Button>())
            .Where(button => button.Classes.Contains("undo-action"));

    /// <summary>How many writes still have a copy behind them. One per spent revert is the whole design: the
    /// button is the record that the pre-image exists.</summary>
    internal int UndoButtonsForCheck => UndoButtons.Count();

    internal string[] UndoButtonTipsForCheck
        => UndoButtons.Select(button => ToolTip.GetTip(button)?.ToString() ?? "").ToArray();

    /// <summary>Presses the <paramref name="index"/>th undo the way a person presses it. A missing button raises
    /// nothing rather than throwing, so the assertion that follows reports a failed expectation instead of a
    /// crashed suite.</summary>
    internal void ClickUndoForCheck(int index)
    {
        var button = UndoButtons.ElementAtOrDefault(index);
        if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

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
    internal bool PermissionChipEnabledForCheck => PermissionChip.IsEnabled;
    internal bool ChatActivityVisibleForCheck => _live is { Timer: not null };
    internal string ChatActivityTextForCheck => _live?.Status.Text ?? "";
    internal string ChatActivityElapsedForCheck => _live?.Elapsed.Text ?? "";
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
    internal string ReasoningChipTextForCheck => SelectedReasoningLabel.Text ?? "";
    internal bool ReasoningPickerEnabledForCheck => SelectedReasoningLabel.IsVisible;
    internal double ContextUsageForCheck => ContextRing.Usage;
    internal string ContextTooltipForCheck => ToolTip.GetTip(ContextButton)?.ToString() ?? "";
    internal string ContextPopoverTitleForCheck => ContextPopoverTitle.Text ?? "";
    internal string ContextPopoverPercentForCheck => ContextPopoverPercent.Text ?? "";
    internal bool ContextPopoverOpenForCheck => ContextPopup.IsOpen;
    internal bool ContextCompressButtonEnabledForCheck => CompressContextButton.IsEnabled;
    internal string ContextCompressButtonTextForCheck => CompressContextButton.Content?.ToString() ?? "";
    internal Visual ContextPopoverContentForCheck => (Visual)ContextPopup.Child!;
    internal Task? ContextCompressionTaskForCheck => _contextCompressionTask;
    internal void OpenContextPopoverForCheck() => ShowContextMenu();
    internal void CloseContextPopoverForCheck() => ContextPopup.IsOpen = false;
    internal void ClickContextCompressForCheck()
        => CompressContextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal int ContextAttachmentCountForCheck => _contextAttachments.Count;

    /// <summary>Pictures the composer is holding, in the order they would be sent.</summary>
    internal int PendingPictureCountForCheck => _pendingPictures.Count;

    /// <summary>Drives the same entrance the file picker hands back, so a check can prove the chip, the send and
    /// the stored file without a dialog nobody can automate (the picker's own decision is asserted in
    /// <c>Pickers.Resolve</c>).</summary>
    internal void AddImageForCheck(string path) => AddPictureFromPath(path);

    /// <summary>Feeds the composer a picture that never was a file — which is what a clipboard capture becomes
    /// once it is encoded. Every entrance ends in the same admission, so this is the one that proves the shared
    /// path and the one that a pasted screenshot goes through.</summary>
    internal void AddImageBytesForCheck(byte[] bytes, string name) => AddPendingPicture(bytes, name);

    /// <summary>The composer's drop target: what "you can drop a screenshot here" is decided by.</summary>
    internal bool ComposerAcceptsDropForCheck => DragDrop.GetAllowDrop(ComposerFrame);

    /// <summary>Pictures painted into the message flow, counted by the frame each one sits in.</summary>
    internal int RenderedPictureCountForCheck
        => MessageFlow.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("turn-picture"));

    /// <summary>Chips in the composer that carry a thumbnail rather than only a name.</summary>
    internal int PictureChipsForCheck
        => ContextAttachmentPanel.Children.OfType<Border>()
            .Count(border => border.Child is StackPanel panel && panel.Children.OfType<Image>().Any());

    /// <summary>Presses one chip's own × rather than removing the entry here: what a check has to prove is that
    /// the button takes the picture out of the message, not that the list can be edited.</summary>
    internal void RemovePictureChipForCheck(int index)
    {
        var buttons = ContextAttachmentPanel.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Tag is PendingPicture)
            .ToList();
        if (index < buttons.Count) buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Hands the paste path a clipboard payload of a known shape. The real Ctrl+V reads the system clipboard,
    /// which a self-check cannot fill without overwriting what the person had copied — and the part worth
    /// asserting is what the composer decides once it has the payload, not whether Windows answered.
    /// </summary>
    internal Task<bool> PasteTransferForCheck(IAsyncDataTransfer transfer) => TakeClipboardAsync(transfer);

    /// <summary>Raises the composer's own drop event, so the drop target and the file extraction run exactly as
    /// they do when a screenshot is dragged in from Explorer.</summary>
    internal void DropForCheck(IDataTransfer transfer)
        => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, ComposerFrame,
            default, KeyModifiers.None));

    /// <summary>Sends exactly what the composer holds, which is how a picture with no text under it is sent.</summary>
    internal Task SendComposerForCheck() => SendAsync();

    /// <summary>The session's attachment directory, as the store sees it. A check that claims a deleted session
    /// took its pictures with it has to look at the disk, not at a list this view keeps.</summary>
    internal string SessionImageDirectoryForCheck(string conversationId)
        => _chat.SessionImageDirectory(conversationId);
    internal bool HasComposerAddMenuForCheck => AddContextButton is not null;

    /// <summary>What the <c>+</c> menu offers right now, read from the menu that is actually built on click. A
    /// menu item that exists only in a screenshot is not a feature.</summary>
    internal string[] ComposerMenuHeadersForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>()
            .Select(item => item.Header?.ToString() ?? "").ToArray();
    internal bool ModeIndicatorVisibleForCheck => ModeIndicatorButton.IsVisible;
    internal bool ComposerPlusCenteredForCheck
        => AddContextButton.HorizontalContentAlignment == HorizontalAlignment.Center
           && AddContextButton.VerticalContentAlignment == VerticalAlignment.Center;
    internal bool ContextRingPrecedesModelForCheck
        => ContextButton.Parent is Panel panel
           && panel.Children.IndexOf(ContextButton) < panel.Children.IndexOf(ModelPicker);
    internal bool PermissionChipFollowsPlusForCheck
        => PermissionChip.Parent is Panel panel
           && panel.Children.IndexOf(PermissionChip) == panel.Children.IndexOf(AddContextButton) + 1;
    internal bool ModeIndicatorFollowsPermissionForCheck
        => ModeIndicatorButton.Parent is Panel panel
           && panel.Children.IndexOf(ModeIndicatorButton) == panel.Children.IndexOf(PermissionChip) + 1;
    internal bool ModeIndicatorKeepsLabelVisibleForCheck
        => ModeIndicatorLabel.IsVisible && ModeIndicatorLabel.Parent is Grid grid
           && grid.ColumnDefinitions.Count == 2
           && Grid.GetColumn(ModeIndicatorLabel) == 1;
    internal bool ModeIndicatorIconMatchesSelectedModeForCheck
        => ReferenceEquals(ModeIndicatorIcon.Data, ThemeGeometry(ComposerModeIconKey(_selectedComposerMode)));
    internal bool ModeIndicatorCloseIsRedForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
           && close.Foreground is ISolidColorBrush closeBrush
           && closeBrush.Color.R > closeBrush.Color.G
           && closeBrush.Color.R > closeBrush.Color.B;
    internal string ModeIndicatorCloseColorForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close"))?.Foreground?.ToString() ?? "unset";
    internal bool ModeIndicatorCloseIsLeftAndCenteredForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
           && close.VerticalAlignment == VerticalAlignment.Center
           && close.Parent is Grid grid
           && Grid.GetColumn(close) == 0;
    internal (string Glyph, double FontSize, string FontFamily) ModeIndicatorCloseGlyphForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
            ? (close.Text ?? "", close.FontSize, close.FontFamily?.ToString() ?? "")
            : ("", 0, "");
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

    // ── Permission chip and its menu ──
    // Each probe builds a fresh menu, which is the same object a user's click would have arrived on: the list
    // is composed from the session as it stands, not from state this view keeps.
    private static string MenuItemTitle(MenuItem item) => item.Header switch
    {
        Grid header => header.Children.OfType<StackPanel>().FirstOrDefault()?
            .Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
        string text => text,
        _ => "",
    };

    private static string MenuItemHint(MenuItem item)
        => item.Header is Grid header
            ? header.Children.OfType<StackPanel>().FirstOrDefault()?
                .Children.OfType<TextBlock>().ElementAtOrDefault(1)?.Text ?? ""
            : "";

    internal string PermissionChipLabelForCheck => PermissionChipLabel.Text ?? "";
    internal bool PermissionChipMarksDangerForCheck => PermissionChip.Classes.Contains("danger");
    internal bool PermissionChipIconIsDrawnForCheck => PermissionChipIcon.Data is not null;

    /// <summary>Both composer chips have to carry the same caret: on a chip that is otherwise plain text it is
    /// the only thing saying "this opens a menu", and a stroked triangle at this size collapses into a blob.
    /// </summary>
    internal bool ComposerChipsShareLineCaretForCheck => HasLineCaret(PermissionChip) && HasLineCaret(ModelPicker);

    private static bool HasLineCaret(Control anchor)
        => anchor.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("chip-caret")) is { } caret
           && ReferenceEquals(caret.Data, ThemeGeometry("Hub.Icon.Caret"))
           && caret.Fill is ISolidColorBrush { Color.A: 0 }
           && caret.Stroke is not null;

    /// <summary>Opens the permission menu through the same path the chip uses, and hands back an item of it:
    /// the flyout presents in its own top-level, so a screenshot has to be taken from inside.</summary>
    internal Control? OpenPermissionMenuForCheck()
    {
        ShowPermissionMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal Control? OpenComposerMenuForCheck()
    {
        ShowAddContextMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal string[] PermissionMenuTitlesForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Select(MenuItemTitle).ToArray();

    // ── Verification hooks for the workspace chip (used by --verify-shell) ──
    // The folder dialog is a modal and cannot be driven from a self-check, so these read the built state and
    // drive the workspace through ChatWorkspace instead — the same split AuthDialog's hooks use.

    internal string WorkspaceChipLabelForCheck => WorkspaceChipLabel.Text ?? "";
    internal bool WorkspaceChipVisibleForCheck => WorkspaceChip.IsVisible;
    internal string WorkspaceChipHintForCheck => ToolTip.GetTip(WorkspaceChip)?.ToString() ?? "";

    /// <summary>Where the picker sits, in the composer frame's own coordinates, and whether it is still inside
    /// that frame. The move is the change, so the placement is what gets pinned: (0, just under the frame) is
    /// "outside the chat box, bottom-left", and anything inside the frame is the old affordance back again.
    /// </summary>
    internal (double X, double Y, double FrameHeight, bool InsideFrame) WorkspaceChipPlateForCheck
    {
        get
        {
            var at = WorkspaceChip.TranslatePoint(new Point(0, 0), ComposerFrame) ?? default;
            return (at.X, at.Y, ComposerFrame.Bounds.Height, WorkspaceChip.GetLogicalAncestors()
                .Any(ancestor => ReferenceEquals(ancestor, ComposerFrame)));
        }
    }

    internal string[] WorkspaceMenuTitlesForCheck
        => BuildWorkspaceMenu().Items.OfType<MenuItem>().Select(MenuItemTitle).ToArray();

    internal Control? OpenWorkspaceMenuForCheck()
    {
        ShowWorkspaceMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    /// <summary>Clicks the "clear" row, which exists only while a directory is bound.</summary>
    internal bool ClickWorkspaceMenuClearForCheck()
    {
        var clear = BuildWorkspaceMenu().Items.OfType<MenuItem>()
            .FirstOrDefault(item => MenuItemTitle(item) == HubStrings.Get("ChatWorkspaceMenuClear"));
        if (clear is null) return false;
        clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    internal string[] PermissionMenuHintsForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Select(MenuItemHint).ToArray();

    internal int PermissionMenuCheckedCountForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Count(item => item.IsChecked);

    /// <summary>Which tier the menu marks as current, read off the tick's own visibility rather than a flag:
    /// the mark is what a person sees, so that is the thing that has to be right. -1 when nothing is marked.</summary>
    internal int PermissionMenuMarkedIndexForCheck
    {
        get
        {
            var items = BuildPermissionMenu().Items.OfType<MenuItem>().ToList();
            for (var i = 0; i < items.Count; i++)
            {
                // The row's two Paths are the tier's icon and then the tick; the text column is a panel.
                if (items[i].Header is Grid grid
                    && grid.Children.OfType<Avalonia.Controls.Shapes.Path>().LastOrDefault()
                        is { IsVisible: true } tick
                    && ReferenceEquals(tick.Data, ThemeGeometry("Hub.Icon.Tick"))) return i;
            }

            return -1;
        }
    }

    /// <summary>Whether the quiet "follow the default" row is offered — it exists only while this session has
    /// an override that could be dropped.</summary>
    internal bool PermissionMenuOffersFollowDefaultForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Any(item => item.Header is string);

    /// <summary>Clicks one entry of the permission menu, in the order the menu shows them.</summary>
    internal bool SelectPermissionMenuEntryForCheck(int index)
    {
        if (BuildPermissionMenu().Items.OfType<MenuItem>().ElementAtOrDefault(index) is not { } item) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

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

    /// <summary>Types and sends, then waits for that session's run to be over. Sending no longer blocks a
    /// caller until the reply lands — that is the whole point of a run — so a check that wants the finished
    /// answer has to say so, one pump at a time.</summary>
    internal async Task SendForCheckAsync(string text)
    {
        InputBox.Text = text;
        await SendAsync();
        await WaitForRunToFinishForCheck();
    }

    /// <summary>Pumps the dispatcher until the viewed session's run is over. A check that wants the finished
    /// answer has to ask for it now: a reply arrives independently of whoever pressed send.</summary>
    internal async Task WaitForRunToFinishForCheck()
    {
        for (var wait = 0; wait < 200 && IsStreamingForCheck; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    /// <summary>Starts a send without awaiting it, so a check can inspect the mid-stream state (the button
    /// becomes a stop icon before the first token arrives).</summary>
    internal Task BeginSendForCheckAsync(string text)
    {
        InputBox.Text = text;
        return SendAsync();
    }

    private sealed record ContextAttachment(string Kind, string Name, string Path, string? Details = null);

    /// <summary>
    /// One picture the composer is holding. Bytes rather than a path because two of the three entrances have no
    /// file to point at — a pasted capture arrives as a bitmap, and what all three have in common is the bytes
    /// Hub would store.
    /// </summary>
    private sealed record PendingPicture(byte[] Bytes, string Name);

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

    /// <summary>How many icon actions one bubble offers. Counted rather than sampled, because a button that
    /// quietly comes back is exactly the regression this row has already had once.</summary>
    internal int BubbleIconActionCount(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .Count(button => button.Classes.Contains("message-action-icon")) ?? -1;

    /// <summary>Rendered width of one icon action, so a plate that quietly grows back is caught.</summary>
    /// <summary>Raises one icon action by its string key, exactly as a click would.</summary>
    internal bool ClickBubbleAction(int visibleIndex, string textKey)
    {
        if (MessageRows.ElementAtOrDefault(visibleIndex)?
                .GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("message-action-icon")
                                          && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal))
            is not { } button) return false;

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>The in-place editor's cancel cross, measured the same way as the composer's: the two checks
    /// together are what keep one ✕ from being redrawn heavy while the other was softened.</summary>
    internal (string Glyph, double FontSize, string FontFamily) BubbleCancelGlyphForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("message-action-icon")
                                      && string.Equals(button.Tag?.ToString(), "Cancel", StringComparison.Ordinal))
            is { Content: TextBlock glyph }
            ? (glyph.Text ?? "", glyph.FontSize, glyph.FontFamily?.ToString() ?? "")
            : ("", 0, "");

    /// <summary>The in-place editor a bubble is hosting right now, or null while it shows plain text.</summary>
    internal TextBox? BubbleEditorForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Classes.Contains("message-edit"));

    internal bool IsStreamingForCheck => IsViewedStreaming;

    /// <summary>What the live bubble on screen says. It is read off the run, so this is also the proof that a
    /// reply kept arriving while its session was out of sight.</summary>
    internal string LivePreviewTextForCheck => _live?.Preview.Text ?? "";

    /// <summary>Presses the round button the way a click would, so stopping goes through the view instead of
    /// cancelling a run behind the panel's back.</summary>
    internal void ClickSendButtonForCheck() => SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal bool ForkNoticeVisibleForCheck => ForkNotice.IsVisible;
    internal string? ForkNoticeTextForCheck => ForkNotice.IsVisible ? ForkNotice.Content?.ToString() : null;
    internal void ClickForkNoticeForCheck() => ForkNotice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Moves focus out of an open inline editor, the way clicking anywhere else in the window would.</summary>
    internal void FocusComposerForCheck() => InputBox.Focus();

    /// <summary>Sends one key to the in-place editor, so the Enter/Escape bindings themselves are what a check
    /// exercises rather than the method they call.</summary>
    internal bool SendKeyToBubbleEditor(int visibleIndex, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (BubbleEditorForCheck(visibleIndex) is not { } editor) return false;
        editor.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = editor,
        });
        return true;
    }

    internal double BubbleIconActionWidth(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("message-action-icon"))?.Bounds.Width ?? -1;

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

    /// <summary>Who a bubble says wrote it — the peer label when the message came from another session, null when
    /// the user typed it. Asserted as text because an absent label is exactly the misreading it exists to stop.</summary>
    internal string? PeerOriginTextForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("peer-origin"))?.Text;

    /// <summary>How many bubbles on screen are labelled as another session's. Counted rather than sampled: a
    /// label on a turn nobody sent is the same bug as one that never appears.</summary>
    internal int PeerOriginCountForCheck => MessageFlow.GetLogicalDescendants()
        .OfType<TextBlock>().Count(block => block.Classes.Contains("peer-origin"));
}
