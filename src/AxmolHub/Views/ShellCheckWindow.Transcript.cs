using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ThemeProbe;
using static AxmolHub.ShellCheckSupport;
using static AxmolHub.ShellCheckFixture;

namespace AxmolHub;
internal sealed class ShellTranscriptChecks
{
    private readonly Action<bool, string> Check;
    private readonly ShellCheckFixture Fixture;
    internal ShellTranscriptChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
        Fixture = fixture;
    }

    internal async Task CheckEmptyStateAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        try
        {
            chat.OpenConversation(session.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var chips = panel.SuggestionChipCountForCheck;
            var first = panel.SuggestionChipTextForCheck(0);
            var tapped = panel.SuggestionChipTextForCheck(1);
            Check(chips == 4 && panel.EmptyStateVisibleForCheck && panel.SuggestionRowVisibleForCheck,
                "空会话的空状态给出四条建议（实际 " + chips + " 条）");

            // The chips must be four different offers, and each one has to be the language table's own words —
            // a duplicated chip, or a pill reading "AssistantSuggestFixBug" because a key went missing, is a
            // broken empty state that still renders and still gets clicked. The message quotes at most the four
            // real ones: a rebuild that forgot to clear leaves a thousand, and a FAIL line nobody can read is
            // worth as little as the cell it came from.
            var texts = Enumerable.Range(0, chips).Select(panel.SuggestionChipTextForCheck).ToArray();
            var table = panel.SuggestionKeysForCheck.Select(HubStrings.Get).ToArray();
            Check(chips == table.Length
                  && texts.All(text => text.Length > 0)
                  && texts.Distinct(StringComparer.Ordinal).Count() == chips
                  && texts.SequenceEqual(table, StringComparer.Ordinal),
                "四条建议各不相同，且逐字取自当前语言的文案表（实际 " + chips + " 条："
                + string.Join(" / ", texts.Take(4)) + "）");

            // Tap one. The promise is "fills the composer", so what is asserted is the composer's text, the
            // caret, and that nothing was sent — the last of these because a chip that sends on tap would pass
            // every other cell in this group. The chips are read through the safe accessor on purpose: with
            // none at all this has to be a FAIL, not an exception that ends every cell after it.
            panel.ClickSuggestionChipForCheck(1);
            Check(chips > 1 && panel.InputTextForCheck == tapped && panel.ComposerHasKeyboardFocusForCheck,
                "点建议只是把话填进输入框并聚焦，人还能改（实际「" + panel.InputTextForCheck + "」）");
            Check(!panel.IsStreamingForCheck && !panel.ChatActivityVisibleForCheck
                  && chat.StoredCopyForCheck(session.Id)?.Messages.Count == 0,
                "点建议不会替人按下发送：会话里还没有任何一条消息");

            // A message ends the empty state's business. The row has to go with it — a chip left under the first
            // reply would be a button that fills the composer with something the person has already moved past.
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["第一条回复"]);
            panel.SetInputForCheck("先不聊建议，问点别的");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            // The scripted gateway answers before the panel has had a chance to repaint, so the empty state is
            // still standing on the frame right after the run. Waiting for the repaint is what makes this the
            // same cell in CI and on a machine, and the wait is bounded — it cannot turn a hung view into a pass.
            await WaitUntilAsync(() => !panel.EmptyStateVisibleForCheck);
            Check(!panel.EmptyStateVisibleForCheck && !panel.SuggestionRowVisibleForCheck
                  && panel.InputTextForCheck.Length == 0,
                "发出第一条后空状态连同建议一起收起，输入框也清空（实际空状态"
                + (panel.EmptyStateVisibleForCheck ? "仍在" : "已收") + "，输入框「" + panel.InputTextForCheck + "」）");

            // And back to an untouched session it comes back, because it is rebuilt from the table on every
            // Reload rather than remembered by one conversation.
            var another = chat.StartConversation();
            try
            {
                chat.OpenConversation(another.Id);
                panel.Reload();
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Check(panel.EmptyStateVisibleForCheck
                      && panel.SuggestionChipCountForCheck == chips
                      && panel.SuggestionChipTextForCheck(0) == first,
                    "换到另一个空会话，建议 chip 原样回来（实际 " + panel.SuggestionChipCountForCheck + " 条）");
            }
            finally
            {
                chat.DeleteConversation(another.Id);
            }
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckPictureFeedbackAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var scratch = ScratchDirectory.Resolve("picture-feedback");
        var png = System.IO.Path.Combine(scratch, "shot.png");
        System.IO.File.WriteAllBytes(png, PictureFixture());
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        chat.ClientOverride = (_, _) => new ScriptedChatClient(["看过了"]);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // ── Something must answer when a drag comes in ──
            // Both directions are the cell: a page that never lights up is a target nobody finds, and one that
            // lights up for a drag it cannot use is a lie about what will happen on release.
            var droppedFile = await shell.StorageProvider.TryGetFileFromPathAsync(png);
            var files = new DataTransfer();
            if (droppedFile is not null) files.Add(DataTransferItem.CreateFile(droppedFile));
            var words = new DataTransfer();
            words.Add(DataTransferItem.CreateText("一段普通的文字"));

            panel.RaiseComposerDragOverForCheck(words);
            Check(!panel.ComposerDragOverForCheck,
                "拖进来的不是文件时输入框不亮环：接不住的东西不该先承诺");
            panel.RaiseComposerDragOverForCheck(files);
            Check(droppedFile is not null && panel.ComposerDragOverForCheck,
                "拖进来的是文件时输入框亮起环，松手前就知道该放在哪（实际"
                + (panel.ComposerDragOverForCheck ? "亮了" : "没亮") + "）");
            panel.RaiseComposerDragLeaveForCheck();
            Check(!panel.ComposerDragOverForCheck, "拖开之后环收回：亮着的环说的是一个正在发生的动作");
            panel.RaiseComposerDragOverForCheck(files);
            panel.DropForCheck(files);
            Check(!panel.ComposerDragOverForCheck && panel.PendingPictureCountForCheck == 1,
                "落下之后环也收回，图进了草稿（实际 " + panel.PendingPictureCountForCheck + " 张）");

            // ── The composer itself says where pictures come from ──
            // Asserted against the text table rather than a literal, and in both languages, because the hint is
            // worth nothing on the side the person is not reading.
            var hintChinese = HubTexts.Get("InputPlaceholder", HubTexts.ChineseLanguage);
            var hintEnglish = HubTexts.Get("InputPlaceholder", HubTexts.EnglishLanguage);
            Check(panel.ComposerPlaceholderForCheck == HubStrings.Get("InputPlaceholder")
                  && hintChinese.Contains("粘贴", StringComparison.Ordinal)
                  && hintChinese.Contains("拖", StringComparison.Ordinal)
                  && hintEnglish.Contains("Paste", StringComparison.OrdinalIgnoreCase)
                  && hintEnglish.Contains("drop", StringComparison.OrdinalIgnoreCase),
                "输入框提示在两种语言里都写明截图可以粘贴或拖进来（实际「"
                + panel.ComposerPlaceholderForCheck + "」）");

            // ── A sent picture must be zoomable ──
            panel.SetInputForCheck("看这张");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            await WaitUntilAsync(() => panel.RenderedPictureCountForCheck > 0);
            Check(panel.RenderedPictureCountForCheck == 1 && panel.PendingPictureCountForCheck == 0,
                "夹具：这条消息在气泡下画了一张缩略图（实际 " + panel.RenderedPictureCountForCheck + " 张）");

            panel.ClickRenderedPictureForCheck(0);
            var shown = shell.PictureViewerSizeForCheck;
            Check(shell.PictureViewerOpenForCheck && shown == (8, 8)
                  && shell.PictureViewerCaptionForCheck.Contains("image/png", StringComparison.Ordinal),
                "点缩略图打开窗口级查看器，显示的就是那张图本身（实际 " + shown.Width + "×" + shown.Height
                + "，说明「" + shell.PictureViewerCaptionForCheck + "」）");
            Check(shell.PictureViewerButtonCountForCheck == 2,
                "查看器上有复制与关闭两个动作，都只用图标说话（实际 "
                + shell.PictureViewerButtonCountForCheck + " 个）");

            // The payload, not the clipboard: a check that ran the real write would leave whatever the person had
            // copied gone, which is the one thing this suite may not do to the machine it runs on.
            Check(shell.PictureViewerCopyPayloadForCheck?.Items.FirstOrDefault()
                    ?.TryGetRaw(DataFormat.Bitmap) is Bitmap copied
                  && copied.PixelSize is { Width: 8, Height: 8 },
                "复制按钮交出的就是这张图的位图，而不是文件名或一段文字");

            shell.PressViewerKeyForCheck(Key.Escape);
            Check(!shell.PictureViewerOpenForCheck, "Esc 关掉查看器，键盘不必绕到那个 × 上");
            panel.ClickRenderedPictureForCheck(0);
            shell.ClickPictureViewerButtonForCheck(1);
            Check(!shell.PictureViewerOpenForCheck, "查看器上的 × 也关得住（与 Esc 是同一条路，不是同一个按钮）");

            // A Computer Use capture belongs to the tool result, not to a user message. It must still be visible
            // in the conversation and join the same picture viewer set as user attachments.
            var pictureConversation = panel.ActiveConversationForCheck!;
            var capturedFixture = pictureConversation.Messages
                .Last(turn => turn.Role == ChatRoles.User && turn.Images.Count > 0).Images[0];
            pictureConversation.Messages.Add(ChatTurn.FunctionCall("call_capture_picture", "computer_use",
                "{\"application\":\"Fixture App\",\"action\":\"screenshot\"}"));
            pictureConversation.Messages.Add(ChatTurn.FunctionResult("call_capture_picture",
                "Captured application window \"Fixture App\".", images: [capturedFixture]));
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.ExpandVirtualGroupsForCheck();
            Relayout(shell, panel, measure: true);
            await WaitUntilAsync(() => panel.RenderedPictureCountForCheck == 2);
            Check(panel.RenderedPictureCountForCheck == 2,
                "Computer Use 工具结果的截图直接显示在会话中（含原附件共 2 张缩略图，实际 "
                + panel.RenderedPictureCountForCheck + " 张）");
            panel.ClickRenderedPictureForCheck(1);
            Check(shell.PictureViewerOpenForCheck && shell.PictureViewerSizeForCheck == (8, 8),
                "点开工具结果中的截图会显示捕获图，而不是只显示文件名");
            shell.PressViewerKeyForCheck(Key.Escape);
            pictureConversation.Messages.RemoveRange(pictureConversation.Messages.Count - 2, 2);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // ── An unsent draft must be viewable too ──
            panel.AddImageForCheck(png);
            panel.ClickPendingPictureForCheck(0);
            var draft = shell.PictureViewerSizeForCheck;
            Check(panel.PendingPictureCountForCheck == 1 && shell.PictureViewerOpenForCheck
                  && draft == (8, 8),
                "草稿缩略图也点得开：发出去之前是最后能反悔的时刻（实际 " + draft.Width + "×" + draft.Height + "）");
            shell.PressViewerKeyForCheck(Key.Escape);

            // And the frame proves it painted. Measured as a difference against the same frame with the viewer
            // shut, because the thumbnails in the transcript carry the fixture's colours too — a count compared
            // against zero would pass on a page of thumbnails and fail on nothing.
            var shot = System.IO.Path.Combine(scratch, "preview.png");
            SmokeCapture.Capture(shell, shot);
            var thumbnails = FixturePixels(shot);
            panel.ClickRenderedPictureForCheck(0);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            SmokeCapture.Capture(shell, shot);
            var enlarged = FixturePixels(shot);
            Check(shell.PictureViewerOpenForCheck && enlarged > thumbnails + 50_000,
                "查看器把那张图放大画在屏幕上（缩略图 " + thumbnails + " 像素 → 打开后 " + enlarged + "）");
            shell.ClickPictureViewerButtonForCheck(1);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            SmokeCapture.Capture(shell, shot);
            var closed = FixturePixels(shot);
            Check(!shell.PictureViewerOpenForCheck && closed * 4 < enlarged,
                "关掉之后回到缩略图那一帧（放大时 " + enlarged + " → 关掉后 " + closed
                + "）：覆盖层是收起来的，不是盖在上面的");
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckComposerKeysAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var scratch = ScratchDirectory.Resolve("composer-keys");
        var png = System.IO.Path.Combine(scratch, "shot.png");
        System.IO.File.WriteAllBytes(png, PictureFixture());
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // ── What was sent can still be recalled with Up ──
            // The queue is the window's, not this conversation's — a draft lost to a steer has to be reachable
            // even when the conversation it belonged to was created by that very send — so the newest two are
            // read off the tail rather than counted, because the queue is capped.
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["第一条回答"]);
            panel.SetInputForCheck("第一句问话");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            panel.SetInputForCheck("第二句问话");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            Check(panel.RecentSentTextsForCheck(2).SequenceEqual(["第一句问话", "第二句问话"], StringComparer.Ordinal),
                "两句发出去的话都进了召回队列（实际「"
                + string.Join(" / ", panel.RecentSentTextsForCheck(2)) + "」）");

            panel.PressComposerKeyForCheck(Key.Up);
            Check(panel.InputTextForCheck == "第二句问话",
                "Up 先回到最近一句（实际「" + panel.InputTextForCheck + "」）");
            panel.PressComposerKeyForCheck(Key.Up);
            Check(panel.InputTextForCheck == "第一句问话",
                "再按 Up 继续往回走（实际「" + panel.InputTextForCheck + "」）");
            panel.PressComposerKeyForCheck(Key.Down);
            Check(panel.InputTextForCheck == "第二句问话", "Down 又回到近处");
            panel.PressComposerKeyForCheck(Key.Down);
            Check(panel.InputTextForCheck.Length == 0 && panel.RecallStepForCheck == 0,
                "走到头回到空框，而不是把最近一句钉在那里");

            // A half-written sentence wins the arrow over history — that is the case where replacing the box would
            // destroy work. And the walk restarts from the newest once the box is empty again, so typing did not
            // cost the history.
            panel.SetInputForCheck("自己写到一半");
            panel.PressComposerKeyForCheck(Key.Up);
            Check(panel.InputTextForCheck == "自己写到一半",
                "框里已经有字时 Up 让给光标，不覆盖正在写的话");
            panel.SetInputForCheck("");
            panel.PressComposerKeyForCheck(Key.Up);
            Check(panel.InputTextForCheck == "第二句问话",
                "清空之后 Up 重新从最近一句开始（实际「" + panel.InputTextForCheck + "」）");

            // ── After pressing send, the keyboard stays in the composer ──
            // Focus is moved away first, and the move is asserted: the box already holds the keyboard, so without
            // taking it away a handler that never gave it back would pass this cell standing still.
            panel.SetInputForCheck("发完这句");
            panel.MoveFocusOffComposerForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.ComposerHasKeyboardFocusForCheck,
                "夹具：焦点确实被挪走了，否则下一条断言什么都证明不了");
            panel.ClickSendButtonForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.ComposerHasKeyboardFocusForCheck,
                "按下发送后焦点回到输入框：接下来通常还要接着说");
            await panel.WaitForRunToFinishForCheck();

            // ── Esc: close the viewer first, then stop the reply ──
            panel.AddImageForCheck(png);
            panel.ClickPendingPictureForCheck(0);
            Check(shell.PictureViewerOpenForCheck, "夹具：查看器开着，图也还在草稿里");

            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["这条会被停住"], gate: hold.Task);
            panel.SetInputForCheck("先别答完");
            _ = panel.SendComposerForCheck();
            await WaitUntilAsync(() => panel.IsStreamingForCheck);
            panel.PressEscapeForCheck();
            Check(!shell.PictureViewerOpenForCheck && panel.IsStreamingForCheck,
                "查看器开着时 Esc 只收查看器，不停正在写的回复（覆盖层是屏幕上唯一正在被看的东西）");
            panel.PressEscapeForCheck();
            await WaitUntilAsync(() => !panel.IsStreamingForCheck);
            Check(!panel.IsStreamingForCheck, "再按一次 Esc 才停住这条回复");
            hold.SetResult(true);
            await panel.WaitForRunToFinishForCheck();

            // ── Esc while idle must do nothing ──
            var idleTurns = chat.StoredCopyForCheck(session.Id)?.Messages.Count ?? 0;
            panel.SetInputForCheck("留着别动");
            panel.PressEscapeForCheck();
            Check(panel.InputTextForCheck == "留着别动"
                  && chat.StoredCopyForCheck(session.Id)?.Messages.Count == idleTurns,
                "没有回复在跑时按 Esc 不吞字、也不动会话（实际「" + panel.InputTextForCheck + "」）");
            panel.SetInputForCheck("");

            // ── The sentence steered in mid-run is in the history too ──
            // Two chunks with a hold between them: the first is already on the record when the steer is tapped, so
            // the second one is what proves the reply was left to finish. Cancelled at the tap, the sentence stops
            // at its first half and never arrives.
            var steerHold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(
                ["引导之前写完的", "半路改的主意之后答的"], afterFirstChunkGate: steerHold.Task);
            panel.SetInputForCheck("把这条停住");
            _ = panel.SendComposerForCheck();
            await WaitUntilAsync(() => panel.FlowText.Contains("引导之前写完的", StringComparison.Ordinal));
            panel.SetInputForCheck("半路改的主意");
            await panel.SendComposerForCheck();
            Dispatcher.UIThread.RunJobs();
            // The first Enter while a reply is running only lifts the sentence onto the confirm strip: a steer is
            // the routing table's strongest signal, so it takes a second tap to say it.
            Check(panel.SteerConfirmVisibleForCheck
                  && panel.SteerConfirmTextForCheck == "半路改的主意"
                  && panel.IsStreamingForCheck,
                "回复进行中再按一次发送只是把这句摆到确认条上，还没有真的引导出去（实际「"
                + panel.SteerConfirmTextForCheck + "」）");
            panel.ClickSteerCommitForCheck();
            steerHold.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
            panel.SetInputForCheck("");
            panel.PressComposerKeyForCheck(Key.Up);
            Check(panel.InputTextForCheck == "半路改的主意",
                "被引导吞掉的那句同样能 Up 回来（实际「" + panel.InputTextForCheck + "」）");
            var steeredCopy = chat.StoredCopyForCheck(session.Id);
            Check(steeredCopy is not null
                  && steeredCopy.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                      && turn.Text == "引导之前写完的半路改的主意之后答的")
                  && steeredCopy.Messages.Any(turn => turn.Role == ChatRoles.User
                      && turn.Text == "半路改的主意"),
                "引导不打断正在写的那条：那半句照样写完，之后才接上新指令（实际 "
                + string.Join(" / ", (steeredCopy?.Messages ?? []).Select(turn => turn.Role + ":" + turn.Text)) + "）");

            // ── The steer rides the next request, not the end of the turn ──
            // One segment is a whole agent turn: the tool loop runs inside it, so waiting for the reply to end
            // would make the person sit through every call they have just said to stop making. The loop asks again
            // once a result is in hand, and that is the moment the queued sentence goes out.
            var boundary = chat.StartConversation();
            var loopHold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ToolCallingChatClient? loopClient = null;
            chat.ClientOverride = (_, _) => loopClient ??= new ToolCallingChatClient
            {
                HoldFirstResponse = loopHold.Task,
            };
            panel.SetInputForCheck("先读一个工具");
            _ = panel.SendComposerForCheck();
            await WaitUntilAsync(() => panel.IsStreamingForCheck);
            panel.SetInputForCheck("半路改的第二句");
            await panel.SendComposerForCheck();
            panel.ClickSteerCommitForCheck();
            loopHold.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
            var handed = loopClient?.LastMessages?.Select(message => message.Text).ToArray() ?? [];
            Check(handed.Any(text => text.Contains("半路改的第二句", StringComparison.Ordinal))
                  && chat.StoredCopyForCheck(boundary.Id) is { } loopCopy
                  && loopCopy.Messages.Any(turn => turn.Text == "先说的话")
                  && loopCopy.Messages.Any(turn => turn.Text == "后说的话"),
                "引导在下一次请求的边界上送到模型面前，工具循环不被掐断（第二次请求 "
                + handed.Length + " 条：" + string.Join(" | ", handed.Select(text =>
                    text.Length > 24 ? text[..24] : text))
                + "；转录 " + string.Join(" / ", (chat.StoredCopyForCheck(boundary.Id)?.Messages ?? [])
                    .Select(turn => turn.Role + ":" + turn.Text)) + "）");
            chat.DeleteConversation(boundary.Id);
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckTypographyAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        chat.ClientOverride = (_, _) => new ScriptedChatClient(
            ["# 标题\n\n正文一段，带 `行内代码`。\n\n```csharp\nint answer = 42;\n```"]);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            panel.SetInputForCheck("给我一段带 markdown 的回答");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            await WaitUntilAsync(() => panel.MarkdownBodyFontSizeForCheck > 0);
            var plain = panel.PlainTurnFontSizeForCheck;
            var markdown = panel.MarkdownBodyFontSizeForCheck;
            Check(Math.Abs(markdown - 12) < 0.001,
                "markdown 正文仍是主题里那档 12，而不是被拉回控件默认的 14（实际 "
                + markdown.ToString(CultureInfo.InvariantCulture) + "）");

            // The ink, for the same boundary the size pair above guards: only the last forty turns are Markdown,
            // so a message must not change brightness as it scrolls out of that window. Both readings come off the
            // control that paints the words and are pinned to the reading token, so a literal would not pass.

            // What the rung means, rather than which hex it happens to be: softer than the primary that headings
            // and chrome wear, still readable on every surface a body of prose or code can land on. The relation
            // holds in both variants, which is what lets it be asserted here without switching the theme first.
            var bodyInk = ThemeProbe.TokenColor("Hub.TextBody");
            var primaryInk = ThemeProbe.TokenColor("Hub.TextPrimary");
            var wells = new[] { "Hub.Background", "Hub.SurfaceRaised", "Hub.SurfaceSunken" }
                .Select(ThemeProbe.TokenColor).ToArray();
            var readable = bodyInk is { } ink
                           && wells.All(well => well is { } surface
                               && DarkSyntax.Contrast(ink, surface) >= DarkSyntax.MinimumContrast);
            var calmer = bodyInk is { } body && primaryInk is { } primary && wells[0] is { } page
                         && DarkSyntax.Contrast(body, page) < DarkSyntax.Contrast(primary, page);
            var ratios = string.Join(" / ", wells.Select(well => bodyInk is { } ink && well is { } surface
                ? DarkSyntax.Contrast(ink, surface).ToString("0.0", CultureInfo.InvariantCulture)
                : "null"));
            Check(readable && calmer,
                "正文墨色在页面/行内代码芯片/围栏代码底上都过 AA，且比标题与壳层的主墨色更柔（正文 "
                + ratios + "，主墨色在页面上 "
                + (primaryInk is { } primaryInkColor && wells[0] is { } pageColor
                    ? DarkSyntax.Contrast(primaryInkColor, pageColor).ToString("0.0", CultureInfo.InvariantCulture)
                    : "null") + "）");

            // The code face: the markdown theme used to name "Consolas, Menlo" itself, which is a second answer to
            // a question HubStyles has already answered, and the two answers drift.
            var codeFont = panel.MarkdownCodeFontFamilyForCheck;
            Check(codeFont.Contains("Cascadia Mono", StringComparison.Ordinal)
                  && codeFont.Contains("Noto Sans Mono CJK SC", StringComparison.Ordinal),
                "代码块用的是应用那一份等宽字体栈，而不是主题里另写的一份（实际 " + codeFont + "）");

            // The streaming bubble used to be a plain text block wearing no class of its own, so it answered at
            // the theme's default size and changed face the moment the turn was persisted. It now renders Markdown
            // the way the finished turn does, which retires that comparison and replaces it with the stronger
            // claim: a heading that arrives mid-stream is drawn as a heading, not shown as a pound sign. Parked
            // after the first chunk so the bubble is genuinely on screen while it is read.
            var park = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new[]
            {
                "# 流式中的标题\n\n停在屏幕上的第一段中文，长度要够把这一行撑到换行。",
                "\n\n第二段中文，同样撑满一行之后就该定型了。",
                "\n\n第三段中文，也撑满一行。",
                "\n\n最后一段，落在这里收尾。",
            };
            chat.ClientOverride = (_, _) => new ScriptedChatClient(stream, null, park.Task, arrived);
            panel.SetInputForCheck("边流边渲染给我看");
            await panel.SendComposerForCheck();
            await WaitForSignalAsync(arrived.Task);
            Dispatcher.UIThread.RunJobs();
            var blocksWhileParked = panel.LiveBlockCountForCheck;
            var headingRendered = panel.LiveHasRenderedHeadingForCheck;
            park.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
            Check(headingRendered && blocksWhileParked > 0,
                "流式期间到达的标题就按标题渲染，不必等落库（停在流上时已铺 " + blocksWhileParked
                + " 块，标题已渲染：" + (headingRendered ? "是" : "否") + "）");
            // The cost bound, and the answer to "what does this do to performance". Four chunks can be laid down
            // in a handful of documents, or in one per repaint — and the second is exactly how this path would
            // end up slower than the plain text block it replaced. Counting documents is the part that can be
            // asserted without a stopwatch and without a machine-dependent millisecond budget.
            var builds = panel.LiveRenderBuildsForCheck;
            Check(builds > 0 && builds <= stream.Length * 3,
                "流式渲染按块建文档，不随重画次数走（" + stream.Length + " 段共铺了 " + builds
                + " 个文档，上限 " + stream.Length * 3 + "）");
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckToolFloodAsync(MainWindow shell, ChatPanel panel)
    {
        const string firstLine = "flood-0-1";
        const string lastLine = "flood-39-30";
        // Forty blocks of thirty lines with a pause between them: the pause is what makes the window measurable.
        // A flood that lands in one scheduler tick would prove nothing about the frames inside it, and the whole
        // command still has to stay well inside the suite's own ceiling.
        var flood = CommandShells.ForCurrent().IsPowerShell
            ? "for($b=0;$b -lt 40;$b++){ 1..30 | ForEach-Object { \"flood-$b-$_\" }; Start-Sleep -Milliseconds 80 }"
            : "for b in $(seq 0 39); do for i in $(seq 1 30); do echo \"flood-$b-$i\"; done; sleep 0.08; done";

        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        var workspace = ScratchDirectory.Resolve("flood-workspace");
        System.IO.Directory.CreateDirectory(workspace);
        chat.SetWorkspaceRoot(session.Id, workspace);
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        chat.OpenConversation(session.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        var floodClient = new ApprovalChatClient
        {
            ToolName = "run_command",
            Arguments = new Dictionary<string, object?> { ["command"] = flood },
        };
        chat.ClientOverride = (_, _) => floodClient;
        shell.ShowLog(false);
        var writesBefore = shell.ActivityLogWritesForCheck;
        try
        {
            panel.SetInputForCheck("刷一轮输出，看看界面还动不动");
            _ = panel.SendComposerForCheck();
            await WaitUntilAsync(() => panel.LiveGlyphPhaseForCheck == ActivityPhase.RunningTool);
            Check(panel.LiveGlyphTickingForCheck,
                "命令在跑的时候思考指示器走针，不再是一排静止的节点（相位 " + panel.LiveGlyphPhaseForCheck
                + "，走针 " + panel.LiveGlyphTickingForCheck + "）");

            await WaitUntilAsync(() => shell.ActivityLogTextForCheck.Contains(firstLine, StringComparison.Ordinal));
            var framesBefore = panel.LiveFrameForCheck;
            await WaitUntilAsync(() => shell.ActivityLogTextForCheck.Contains(lastLine, StringComparison.Ordinal),
                1200);
            var frames = panel.LiveFrameForCheck - framesBefore;
            Check(frames >= 3,
                "输出刷屏的这几秒里界面照常走表（气泡跳了 " + frames + " 帧，1,200 行全部读完）");

            await panel.WaitForRunToFinishForCheck();
            var retained = shell.ActivityLogTextForCheck;
            Check(retained.Contains(firstLine, StringComparison.Ordinal)
                  && retained.Contains(lastLine, StringComparison.Ordinal),
                "刷屏的首尾两行都留在抽屉的保留尾部（长度 " + retained.Length + " 字符）");
            Check(shell.ActivityLogWritesForCheck == writesBefore,
                "抽屉关着时一次都没往文本框里写（写入 " + (shell.ActivityLogWritesForCheck - writesBefore)
                + " 次，而命令打了 1,200 行）");

            shell.ShowLog(true);
            var box = NamedDescendant<TextBox>(shell, "ActivityLog")?.Text ?? "";
            Check(shell.ActivityLogWritesForCheck > writesBefore
                  && box.Contains(lastLine, StringComparison.Ordinal),
                "打开抽屉才把保留尾部铺上屏，一次写完（写入 "
                + (shell.ActivityLogWritesForCheck - writesBefore) + " 次）");
        }
        finally
        {
            shell.ShowLog(false);
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckEmphasisAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        chat.ClientOverride = (_, _) => new ScriptedChatClient(
            ["**段首粗体**。\n\n那段话**不单独成轮**，而是搭在调用那一轮上。\n\n"
             + "探针证实 `MarkdownScrollViewer` **内含 `ScrollViewViewer`**（文档已构建）。\n\n"
             + "- 列表项里的**粗体**，后面还有字。\n\n1. 有序项里的**粗体**收尾。"]);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            panel.SetInputForCheck("把粗体的几种写法各来一遍");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            await WaitUntilAsync(() => panel.MarkdownBodyFontSizeForCheck > 0);
            var leaked = panel.MarkdownLeakedEmphasisForCheck();
            Check(leaked.Count == 0,
                "粗体真的解析了，分隔符没有当字面文字画出来（漏出 " + leaked.Count + " 处："
                + string.Join(" ∣ ", leaked) + "）");
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckThinkingAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        try
        {
            static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

            // ── While it is arriving ──
            // A reasoning gateway thinks first and switches to prose mid-stream, so this is the real ordering and
            // not a shape invented for the check.
            //
            // The stream is driven one chunk at a time rather than left to a clock. Task.Delay in 5ms steps rounds
            // up to ~15ms on Windows, so a check polling for "the next chunk landed" overshoots into the one after
            // it — which is how the assertions below first passed, then failed, then passed again on nothing but
            // scheduler luck. With the gate the check asks for a chunk, waits for its paint, then looks.
            var thoughtUnit = "这一段思考要长到能把那个二百像素的身体撑出滚动条，而且还得继续往下长，"
                + "才看得出它到底跟不跟得上最新一行，上滑之后又是不是真的停住了不拽。";
            var step = new StepGate();
            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ReasoningTrickleChatClient(
                30, TimeSpan.Zero, TimeSpan.Zero, "答案到了", thoughtUnit, hold.Task, step.Wait);
            panel.SetInputForCheck("先想清楚再答");
            await panel.SendComposerForCheck();

            // Releases a batch of chunks and then waits for the one condition the next assertion actually needs.
            // Waiting once per chunk was the mistake: each wait can burn its whole tick budget, and sixteen of
            // them in a row is what put this group back under the suite's 120s ceiling. The gate still decides
            // when anything arrives — batching the release just means the check asks once instead of sixteen
            // times, and the condition below is the thing under test's own precondition.
            async Task ReleaseAndAwaitAsync(int chunks, Func<bool> settled)
            {
                for (var round = 0; round < chunks; round++) step.Release();
                // Tight on purpose. The gate means the chunk is already on its way, so anything this long to
                // appear is the stream being stuck rather than the scheduler being slow — and a wide bound here
                // is paid for out of the suite's shared 120s ceiling, where it reads as an unrelated check
                // timing out three groups later.
                await WaitUntilAsync(settled, maxTicks: 60);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }

            await ReleaseAndAwaitAsync(14, () =>
            {
                var (_, at, seen) = panel.LiveThoughtScrollForCheck;
                return at > seen + 40;
            });
            var (offset, extent, viewport) = panel.LiveThoughtScrollForCheck;
            Check(panel.LiveThoughtExpandedForCheck && extent > viewport + 40
                  && offset + viewport >= extent - 6,
                "思考超过身体高度时读到的是最新一行，不是钉在开头（偏移 " + F(offset) + "，视口 " + F(viewport)
                + "，内容 " + F(extent) + "）");

            // A box that always follows is a box nobody can read the top of. Scrolling inside it is a position
            // somebody took, and thinking still arriving underneath must not drag them back down.
            panel.ScrollLiveThoughtForCheck(0);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var parkedAt = panel.LiveThoughtScrollForCheck.Extent;
            await ReleaseAndAwaitAsync(3, () => panel.LiveThoughtScrollForCheck.Extent > parkedAt);
            var (heldOffset, grownExtent, _) = panel.LiveThoughtScrollForCheck;
            Check(heldOffset <= 6 && grownExtent > parkedAt,
                "思考框内上滑之后，新到的思考不再把读数位置拽回底部（滑到顶后偏移停在 " + F(heldOffset)
                + "，内容已从 " + F(parkedAt) + " 长到 " + F(grownExtent) + "）");

            // And it picks the follow back up the moment the reader goes back down. The lock is a position they
            // are standing in, not a mode the box is caught in for the rest of the answer.
            panel.ScrollLiveThoughtForCheck(grownExtent);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var beforeResumed = panel.LiveThoughtScrollForCheck.Extent;
            await ReleaseAndAwaitAsync(1, () => panel.LiveThoughtScrollForCheck.Extent > beforeResumed);
            var (resumedOffset, resumedExtent, resumedViewport) = panel.LiveThoughtScrollForCheck;
            Check(resumedExtent > beforeResumed && resumedOffset + resumedViewport >= resumedExtent - 6,
                "滑回底部之后跟随自己重新上弦，不必重开这一行（内容 " + F(beforeResumed) + " → "
                + F(resumedExtent) + "）");

            // Let the rest through, then read the fold off the painted text rather than off the run's buffer: the
            // fold is a consequence of a paint, and the buffer is ahead of it.
            await ReleaseAndAwaitAsync(20,
                () => panel.LivePaintedTextForCheck.Contains("答案到了", StringComparison.Ordinal));
            // Read at the boundary: this is the last thing the live row will say, and the stored row has to say
            // the same number.
            var labelAtBoundary = panel.LiveThoughtLabelForCheck;
            // The row folds when the answer starts and stays folded — it does not leave. A card that hid itself
            // took the reasoning off screen at exactly the moment a reader might want to go back over it.
            Check(panel.LiveThoughtVisibleForCheck && !panel.LiveThoughtExpandedForCheck,
                "正文一到思考行折成一行但仍留在原处，不再整块消失（可见=" + panel.LiveThoughtVisibleForCheck
                + "，展开=" + panel.LiveThoughtExpandedForCheck + "）");
            hold.SetResult(true);
            await panel.WaitForRunToFinishForCheck();

            // The handoff is the whole reason the duration is stored: the number the reader watched has to still be
            // on the row once the bubble is thrown away and the real turn takes its place.
            var labels = panel.ThoughtRowLabelsForCheck();
            Check(labels.Contains(labelAtBoundary),
                "落盘之后同一行的头写着同一个数，交接看不出来（边界上「" + labelAtBoundary
                + "」，落盘读到 [" + string.Join(" | ", labels) + "]）。注意这条只证明两个面读的是同一个值，"
                + "不证明该值排除了正文打字时间——那需要一段跨过整秒的等待，自检的 120 秒预算换不起");

            // ── A hand that moved the row outranks both automatic changes to it ──
            // Its own send, because the two rules cannot share one run: collapsing the row by hand also suppresses
            // the auto-fold, so a single stream could show one rule working and hide the other failing.
            var step2 = new StepGate();
            var hold2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ReasoningTrickleChatClient(
                8, TimeSpan.Zero, TimeSpan.Zero, "第二段答案", thoughtUnit, hold2.Task, step2.Wait);
            panel.SetInputForCheck("再想一次，这次我自己收起来");
            await panel.SendComposerForCheck();
            for (var round = 0; round < 3; round++) step2.Release();
            await WaitUntilAsync(() => panel.LiveThoughtExpandedForCheck, maxTicks: 60);
            panel.ToggleLiveThoughtForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            // Two things have to survive this: more thinking arriving must not spring the row back open, and the
            // answer arriving must not fold it shut again. Without the "a hand moved this" rule the first of those
            // fails on every paint while the answer is absent, and the box reopens under whoever just shut it.
            for (var round = 0; round < 5; round++) step2.Release();
            await WaitUntilAsync(() => panel.LivePaintedTextForCheck.Contains("第二段答案", StringComparison.Ordinal));
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.LiveThoughtExpandedForCheck && panel.LiveThoughtVisibleForCheck,
                "人手动收起思考行之后，后续到达的思考和正文都不再改它的开合，行也仍在屏上（仍收起="
                + (!panel.LiveThoughtExpandedForCheck ? "是" : "否") + "，仍在屏上="
                + (panel.LiveThoughtVisibleForCheck ? "是" : "否") + "）");
            hold2.SetResult(true);
            await panel.WaitForRunToFinishForCheck();

            // ── Once it is on the record ──
            // Two shapes, because they reach the screen by different routes: prose with thinking behind a row of
            // its own, and a thinking-only turn that <i>is</i> the group's row rather than a fold inside it.
            var proseThought = "先想清楚要动哪几个文件";
            var onlyThought = "这一段只有思考，没有正文";
            session.Append(ChatTurn.Assistant("这是答案正文。", proseThought));
            session.Append(ChatTurn.Assistant("", onlyThought));
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.ExpandVirtualGroupsForCheck();
            Relayout(shell, panel, measure: true);
            var painted = panel.ReasoningTextsForCheck();
            Check(painted.Contains(proseThought) && painted.Contains(onlyThought),
                "落库之后的思考真的被画出来：带正文的挂在它自己那一行下，只有思考的填掉了原来那个空行（实际 "
                + painted.Count + " 行：" + string.Join(" ∣ ", painted) + "）");
            // One fold, not two. The thinking-only turn used to be a collapsed row inside a collapsed group, so
            // reading it cost two clicks and the group head had already said 已思考 once by then.
            Check(panel.GroupThoughtRowNestingForCheck() == 1,
                "只有思考的轮次展开组后一次点击就读到，组里不再套第二层折叠（实际折叠层数 "
                + panel.GroupThoughtRowNestingForCheck() + "）");
            // The two turns just appended were built by the factory with no duration, which is the same shape an
            // older session file loads into. A missing number has to read as no number: writing 已思考 0s would be
            // a claim about a session that never recorded one, and null versus zero is the only thing separating
            // them.
            Check(panel.ThoughtRowLabelsForCheck().Contains(HubStrings.Get("ActivityRowThought")),
                "没记时长的轮次（老会话就是这种）头上只写「已思考」，不谎报成 0s（实际 ["
                + string.Join(" | ", panel.ThoughtRowLabelsForCheck()) + "]）");

            // ── And the switch reaches what is already painted ──
            // Pressed through the checkbox rather than written to the preference: the link between the two is the
            // part that can be missing while both ends still look right. And read back off the file, because a
            // flip that only repainted without persisting would survive a restart as the opposite answer.
            var settings = OpenSettingsOn(shell, "appearance");
            settings.ToggleShowThinkingForCheck();
            Dispatcher.UIThread.RunJobs();
            var offPath = PreferencesPathFor(shell.Workspace.Store.Root);
            Check(!new PreferencesStore(offPath).Load().ShowThinking
                  && !chat.ShowThinking && panel.ReasoningTextsForCheck().Count == 0,
                "关掉思考既落进设置文件，也把已在屏上的那几块一起撤了下来——渲染器只往后画，看不见这个开关"
                + "（屏上还剩 " + panel.ReasoningTextsForCheck().Count + " 块）");
            settings.ToggleShowThinkingForCheck();
            Dispatcher.UIThread.RunJobs();
            var back = panel.ReasoningTextsForCheck();
            Check(new PreferencesStore(offPath).Load().ShowThinking
                  && back.Contains(proseThought) && back.Contains(onlyThought),
                "再开回来，两块都回到原处（实际 " + back.Count + " 块）");

            // ── A silent call's thinking ──
            // A read-only call asks for no card and leaves no undo copy, so the group rendered no row for it at
            // all and the thinking filed on that turn had nowhere on screen — filed, priced, replayed, and
            // invisible. It is content rather than a gap, and it is the one shape the two checks above cannot
            // catch, because they both go through a turn that draws a row anyway.
            var silentSession = chat.StartConversation();
            chat.SetApprovalMode(silentSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(silentSession.Id);
            const string silentThought = "先列一下目录，再决定读哪个文件";
            var silent = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = ToolNames.FsList,
                Arguments = new Dictionary<string, object?> { ["path"] = "." },
            };
            silent.Thinking = [silentThought];
            chat.ClientOverride = (_, _) => silent;
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            panel.SetInputForCheck("默默列一次目录，不用说为什么");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.ExpandVirtualGroupsForCheck();
            Relayout(shell, panel, measure: true);
            Check(panel.ReasoningTextsForCheck().Contains(silentThought),
                "不弹审批卡也不留撤销副本的静默调用，它那一轮的思考也有落点（屏上思考块 "
                + panel.ReasoningTextsForCheck().Count + " 个）");
            chat.DeleteConversation(silentSession.Id);
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckRunElapsedAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        try
        {
            // ── one answer, one line ──
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["这一轮回答完了。"]);
            panel.SetInputForCheck("答一次就好");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var answer = session.Messages.Last(turn => turn.Role == ChatRoles.Assistant);
            var wanted = answer.RunElapsed is { } one
                ? string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ChatRunElapsedFormat"),
                    ChatPanel.FormatDuration(one))
                : "";
            var lines = panel.RunElapsedLinesForCheck();
            Check(answer.RunElapsed is not null
                  && lines.Any(line => line.Contains(wanted, StringComparison.Ordinal)),
                "回答落盘时带上本次总耗时，切回来看还是同一个数（屏上 [" + string.Join(" | ", lines)
                + "]，期望「" + wanted + "」）");

            // ── a tool loop is one run, so it is one number on one turn ──
            var loopSession = StartAgentSession(chat);
            chat.SetApprovalMode(loopSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(loopSession.Id);
            var loop = new ApprovalChatClient
            {
                CallsRemaining = 2,
                ToolName = ToolNames.FsList,
                Arguments = new Dictionary<string, object?> { ["path"] = "." },
            };
            chat.ClientOverride = (_, _) => loop;
            panel.SetInputForCheck("列两次目录再回答");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var stamped = loopSession.Messages.Count(turn => turn.RunElapsed is not null);
            Check(stamped == 1 && panel.RunElapsedLinesForCheck().Count == 1,
                "跑了几轮工具的回答只盖一个总耗时，不会每轮都写、也不会滚回去看见同一个回答在变大（盖了 "
                + stamped + " 个轮次，屏上 " + panel.RunElapsedLinesForCheck().Count + " 行）");
            chat.DeleteConversation(loopSession.Id);

            // ── nothing recorded is nothing said ──
            // An older session loads its turns with no such field. The honest rendering of that is no line at
            // all, and this is the reason the field is nullable rather than a TimeSpan defaulting to zero.
            var oldSession = chat.StartConversation();
            chat.OpenConversation(oldSession.Id);
            oldSession.Append(ChatTurn.User("老问题"));
            oldSession.Append(ChatTurn.Assistant("老回答"));
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(oldSession.Messages.Last().RunElapsed is null && panel.RunElapsedLinesForCheck().Count == 0,
                "没记总耗时的轮次（老会话就是这种）不画那一行，也不谎报成本次共 0s（屏上 "
                + panel.RunElapsedLinesForCheck().Count + " 行）");
            chat.DeleteConversation(oldSession.Id);
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckNarratedCallHandoffAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        const string narration = "构建并跑修复后的完整自检：";
        try
        {
            var secondRound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new NarratedCallChatClient(secondRound.Task, narration);
            panel.SetInputForCheck("跑一下自检");
            await panel.SendComposerForCheck();

            // Waiting on the *filed* row, not on every painted row: the bubble is painting the same sentence while
            // the model says it, so a read that counted both would settle on the bubble itself and then report the
            // duplicate it was looking for. The filed row appears in the same hop that gave the bubble its chance
            // to let go, so by the time this settles the repaint under test has already run.
            await WaitUntilAsync(() => panel.FiledRowTextsForCheck.Any(
                row => row.Contains(narration, StringComparison.Ordinal)), maxTicks: 120);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var painted = panel.FiledRowTextsForCheck.Count(
                row => row.Contains(narration, StringComparison.Ordinal));
            Check(painted == 1 && panel.LiveBlockCountForCheck == 0,
                "带旁白的调用落盘后，这句旁白在屏上只有一份——实时气泡把它交棒给转录里那一行，而不是并排各留一份"
                + "（转录里 " + painted + " 行，气泡里还留着 " + panel.LiveBlockCountForCheck + " 块）");

            secondRound.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckThoughtDurationAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        try
        {
            var firstRoundReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstRound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new TwoRoundThinkingChatClient(firstRoundReady, releaseFirstRound.Task);
            panel.SetInputForCheck("想两轮再答");
            await panel.SendComposerForCheck();
            await firstRoundReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Hold the first response before onToolStarted resets the segment clock. The second response must
            // not inherit this interval; measure it instead of imposing a scheduler-sensitive upper time limit.
            var heldAt = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(1200);
            var heldFor = System.Diagnostics.Stopwatch.GetElapsedTime(heldAt);
            releaseFirstRound.SetResult(true);
            await panel.WaitForRunToFinishForCheck();

            var thought = session.Messages.Last(turn => turn.ReasoningElapsed is not null);
            var took = thought.RunElapsed ?? TimeSpan.Zero;
            Check(thought.ReasoningElapsed is { } reasoning && took - reasoning >= heldFor,
                "后一轮的「已思考」只算它自己那一段，不把前面几轮的工具时间也记在自己头上（这段思考 "
                + thought.ReasoningElapsed?.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + "s，整个运行 "
                + took.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s）");
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckScrollAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var scratch = ScratchDirectory.Resolve("scroll-check");
        var png = System.IO.Path.Combine(scratch, "shot.png");
        System.IO.File.WriteAllBytes(png, PictureFixture());
        var longAnswer = string.Join("\n\n", Enumerable.Range(1, 40)
            .Select(line => $"第 {line} 段：这一段的长度足够把消息列撑出一屏之外，剩下的要看才能读完。"));
        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        chat.ClientOverride = (_, _) => new ScriptedChatClient([longAnswer]);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // ── A long answer must be followed all the way to the end ──
            panel.SetInputForCheck("给我一屏以上");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.ViewAtBottomForCheck && !panel.ScrollToBottomVisible,
                "长回复写完之后视图停在最后一条，也不需要那个回底板的按钮");
            Check(panel.ComposerBandGapForCheck >= 0 && panel.ComposerBandGapForCheck <= 18,
                "没有任务或审批卡时，composer 上方横排紧邻输入框，不为隐藏卡片留空（"
                + panel.ComposerLayoutForCheck + "）");

            // ── A notice line survives one rebuild ──
            panel.AppendNoticeForCheck("这条提示要在重建之后还在");
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var paintedNotice = panel.NoticeInstanceTokenForCheck;
            Check(panel.NoticeVisibleForCheck && paintedNotice != 0
                  && panel.LastNoticeTextForCheck == "这条提示要在重建之后还在",
                "夹具：提示行已经落在会话末尾");
            Check(panel.ClickBubbleAction(panel.BubbleCount - 1, "RegenerateMessage"), "重新生成按钮在最后一次回复上");
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.NoticeVisibleForCheck
                  && panel.NoticeInstanceTokenForCheck != paintedNotice
                  && panel.NoticeInstanceTokenForCheck != 0
                  && panel.LastNoticeTextForCheck == "这条提示要在重建之后还在",
                "整段重画之后提示行被重新贴回原处：它不是重建的顺带牺牲品");

            // …and it is gone as soon as the person says something new, which is the other half of the rule.
            panel.SetInputForCheck("再说一句新的");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.NoticeVisibleForCheck,
                "人开口之后上一条提示就退场：旧警告回答不了新问题");

            // ── Switch away and back ──
            // The notice belongs to the session that produced it, and a session reopens on its newest line: the
            // reading position is the viewer's rather than the conversation's, so both have to be decided here
            // rather than inherited from wherever the last one happened to be left.
            panel.AppendNoticeForCheck("这条属于这个会话");
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var other = chat.StartConversation();
            chat.OpenConversation(other.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.NoticeVisibleForCheck,
                "换到另一个会话，上一条提示不跟过去：它说的是另一个会话的事");

            panel.SetInputForCheck("那边也要一屏以上");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            chat.OpenConversation(session.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.ScrollTranscriptForCheck(40);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.ViewAtBottomForCheck && panel.ScrollToBottomVisible,
                "夹具：这个会话被读到中间，回底板的按钮升起来了");
            chat.OpenConversation(other.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.ViewAtBottomForCheck,
                "切到一个长会话时落在最新一条，而不是继承上一处的读数");
            chat.DeleteConversation(other.Id);

            // ── An attachment row shrinking the viewport is not the person leaving the bottom ──
            chat.OpenConversation(session.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.AddImageForCheck(png);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.PendingPictureCountForCheck == 1 && panel.ViewAtBottomForCheck
                  && !panel.ScrollToBottomVisible,
                "贴上图片让输入框长高一截，视图仍然跟着底部，不冒出回底板的按钮");

        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckPictureAsync(MainWindow shell, ChatPanel panel)
    {        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var scratch = ScratchDirectory.Resolve("picture-check");
        var png = System.IO.Path.Combine(scratch, "capture.png");
        System.IO.File.WriteAllBytes(png, PictureFixture());
        var fake = System.IO.Path.Combine(scratch, "notes.png");
        System.IO.File.WriteAllText(fake, "这其实是一段文本，只是名字叫 png");
        var huge = System.IO.Path.Combine(scratch, "huge.txt");
        using (var stream = System.IO.File.Create(huge)) stream.SetLength(ChatImageFormat.MaxImageBytes + 1);

        var session = chat.StartConversation();
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        chat.ClientOverride = (_, _) => new ScriptedChatClient(["看懂了这张截图"]);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Check(panel.ComposerMenuHeadersForCheck.Contains(HubStrings.Get("ChatAddImage"))
                  && panel.ComposerAcceptsDropForCheck,
                "「+」菜单里有「添加图片…」，输入框也接得住拖进来的文件");

            panel.AddImageForCheck(png);
            Check(panel.PendingPictureCountForCheck == 1 && panel.PictureChipsForCheck == 1,
                "选中一张图片后 composer 上出现带缩略图的附件条（实际 "
                + panel.PendingPictureCountForCheck + " 张，其中缩略图 " + panel.PictureChipsForCheck + " 个）");

            panel.AddImageForCheck(fake);
            Check(panel.PendingPictureCountForCheck == 1
                  && panel.LastNoticeTextForCheck?.Contains("PNG", StringComparison.Ordinal) == true,
                "把文本改名成 .png 递进来按文件头拒掉，并说清能收哪几种（提示："
                + (panel.LastNoticeTextForCheck ?? "无") + "）");

            // Oversized is refused from the file's length, so this also proves the order: reading first would have
            // answered "not an image" to a text file of the same size.
            panel.AddImageForCheck(huge);
            Check(panel.PendingPictureCountForCheck == 1
                  && panel.LastNoticeTextForCheck?.Contains("MiB", StringComparison.Ordinal) == true,
                "超过单张上限的文件先按大小被拒，而不是读进内存以后才发现（提示："
                + (panel.LastNoticeTextForCheck ?? "无") + "）");

            for (var extra = 0; extra < 3; extra++) panel.AddImageForCheck(png);
            panel.AddImageForCheck(png);
            Check(panel.PendingPictureCountForCheck == ChatImageFormat.MaxImagesPerMessage
                  && panel.LastNoticeTextForCheck?.Contains(ChatImageFormat.MaxImagesPerMessage.ToString(),
                      StringComparison.Ordinal) == true,
                "一条消息最多带 " + ChatImageFormat.MaxImagesPerMessage + " 张，第六张被上限挡住并说了原因（提示："
                + (panel.LastNoticeTextForCheck ?? "无") + "）");

            panel.RemovePictureChipForCheck(0);
            Check(panel.PendingPictureCountForCheck == ChatImageFormat.MaxImagesPerMessage - 1,
                "点缩略图上的 × 真的把那张从这条消息里拿掉（实际 " + panel.PendingPictureCountForCheck + " 张）");

            var waiting = panel.PendingPictureCountForCheck;
            panel.SetInputForCheck("这三张截图报的是什么");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            var sentFile = chat.StoredCopyForCheck(session.Id);
            var lastUser = sentFile?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User);
            Check(lastUser is not null && lastUser.Images.Count == waiting
                  && StoredPictureCount(panel, session.Id) == waiting,
                "带着图发送后，图片落进会话目录、转录里带上了同名的附件（实际 "
                + (lastUser?.Images.Count ?? -1) + " 张，目录里 " + StoredPictureCount(panel, session.Id) + " 个文件）");
            Check(chat.PreparedImagePartsForCheck(session.Id) == waiting,
                "下一个请求仍能把转录里的每个附件名读回字节（实际 " + chat.PreparedImagePartsForCheck(session.Id)
                + "/" + waiting + "）");
            Check(panel.RenderedPictureCountForCheck == waiting,
                "用户气泡里画出了这一发的缩略图，而不只是一句话（实际 " + panel.RenderedPictureCountForCheck + " 张）");

            panel.AddImageForCheck(png);
            panel.SetInputForCheck("");
            Check(panel.PendingPictureCountForCheck == 1 && panel.SendButtonEnabledForCheck,
                "只有一张图、没有打字时发送按钮也是可发的：「看这个」本身就是一条消息");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            var pictureOnly = chat.StoredCopyForCheck(session.Id)?.Messages
                .LastOrDefault(turn => turn.Role == ChatRoles.User);
            Check(pictureOnly is not null && pictureOnly.Text.Length == 0 && pictureOnly.Images.Count == 1,
                "只发图的那条在转录里就是空文本加一张附件，没有被塞进任何代打的句子（实际文本长度 "
                + (pictureOnly?.Text.Length ?? -1) + "）");

            var directory = panel.SessionImageDirectoryForCheck(session.Id);
            await DeleteFixtureConversationAsync(chat, session.Id);
            Check(!System.IO.Directory.Exists(directory),
                "删掉会话时它的图片目录一起没了，不会留下谁也找不回的截图");

            // ── Paste it in, drop it in: both entrances really walked once ──
            // The system clipboard is the one thing a self-check must not overwrite, so the payload is handed to the
            // paste path directly, and the drop is raised as its real routed event on the real composer with a
            // storage item the platform itself produced. What is asserted is the decision made once the payload is
            // in hand — take a picture, ignore text — which is the part that would silently eat an ordinary
            // Ctrl+V or quietly drop a screenshot onto the floor.
            var entrance = chat.StartConversation();
            chat.SetApprovalMode(entrance.Id, ToolApprovalModes.Full);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();

            var pastedBitmap = new Bitmap(new System.IO.MemoryStream(PictureFixture()));
            var pasted = new DataTransfer();
            pasted.Add(DataTransferItem.Create(DataFormat.Bitmap, pastedBitmap));
            Check(await panel.PasteTransferForCheck(pasted) && panel.PendingPictureCountForCheck == 1
                  && panel.PictureChipsForCheck == 1,
                "剪贴板里是一张图时，粘贴把它变成带缩略图的附件并claim这次按键（实际 "
                + panel.PendingPictureCountForCheck + " 张）");

            var words = new DataTransfer();
            words.Add(DataTransferItem.CreateText("一段普通的文字"));
            Check(!await panel.PasteTransferForCheck(words) && panel.PendingPictureCountForCheck == 1,
                "剪贴板里只有文字时粘贴不收图、也不声称收过：Ctrl+V 仍然是大家预期的粘贴文本");

            var droppedFile = await shell.StorageProvider.TryGetFileFromPathAsync(png);
            var dropped = new DataTransfer();
            if (droppedFile is not null) dropped.Add(DataTransferItem.CreateFile(droppedFile));
            panel.DropForCheck(dropped);
            Check(droppedFile is not null && panel.PendingPictureCountForCheck == 2,
                "把图片文件拖到输入框上，落下的那张真的进了这条消息（实际 "
                + panel.PendingPictureCountForCheck + " 张）");

            panel.SetInputForCheck("这两张一起看");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            var entered = chat.StoredCopyForCheck(entrance.Id)?.Messages
                .LastOrDefault(turn => turn.Role == ChatRoles.User);
            Check(entered is { Images.Count: 2 } && entered.Images[0].MediaType == "image/png"
                  && entered.Images[1].MediaType == "image/png"
                  && StoredPictureCount(panel, entrance.Id) == 2,
                "粘来的与拖来的两张按到达顺序落进同一条消息，两边都存成 PNG（实际 "
                + (entered?.Images.Count ?? -1) + " 张，目录里 " + StoredPictureCount(panel, entrance.Id) + " 个文件）");

            var copyTransfer = panel.MessageClipboardForCheck(entrance.Id, entered!);
            var copyItem = copyTransfer.Items.First(item => item.Formats.Contains(ChatMessageClipboard.Format));
            Check(copyItem.Formats.Contains(DataFormat.Text)
                  && copyItem.Formats.Contains(ChatMessageClipboard.Format)
                  && copyTransfer.Items.Count(item => item.Formats.Contains(DataFormat.Bitmap)) == 2
                  && (await panel.PasteTransferForCheck(copyTransfer))
                  && panel.PendingPictureCountForCheck == 2
                  && panel.ReusedPendingPictureCountForCheck == 2
                  && panel.InputTextForCheck == entered!.Text,
                "Hub 内复制粘贴同时带回文字与两个原附件引用（实际复用 "
                + panel.ReusedPendingPictureCountForCheck + " 张）");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            var reused = chat.StoredCopyForCheck(entrance.Id)?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User);
            Check(reused is { Images.Count: 2 }
                  && reused.Images.Select(image => image.File).SequenceEqual(entered!.Images.Select(image => image.File))
                  && StoredPictureCount(panel, entrance.Id) == 2,
                "同一会话粘贴后继续发送仍引用原附件文件，没有重复存储（目录里 "
                + StoredPictureCount(panel, entrance.Id) + " 个）");

            var crossSession = chat.StartConversation();
            chat.SetApprovalMode(crossSession.Id, ToolApprovalModes.Full);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            var crossTransfer = panel.MessageClipboardForCheck(entrance.Id, entered!);
            Check(await panel.PasteTransferForCheck(crossTransfer)
                  && panel.PendingPictureCountForCheck == 2
                  && panel.ReusedPendingPictureCountForCheck == 0
                  && panel.InputTextForCheck == entered!.Text,
                "另一会话粘贴Hub剪贴板数据时从携带的图片字节导入，而不引用源会话路径");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            var imported = chat.StoredCopyForCheck(crossSession.Id)?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User);
            Check(imported is { Images.Count: 2 }
                  && imported.Images.All(image => image.MediaType == "image/png")
                  && StoredPictureCount(panel, crossSession.Id) == 2,
                "跨会话发送后图片写入目标会话的附件目录（实际 "
                + (imported?.Images.Count ?? -1) + " 张）");

            var unavailable = ChatTurn.User("文字仍可复制", images: [new ChatImage("gone.png", "image/png", 8)]);
            var unavailableTransfer = panel.MessageClipboardForCheck(entrance.Id, unavailable);
            Check(await panel.PasteTransferForCheck(unavailableTransfer)
                  && panel.PendingPictureCountForCheck == 0
                  && panel.InputTextForCheck == unavailable.Text,
                "源附件不可用时仍可复制并粘贴消息文字，不会伪造一张空图片");
            panel.SetInputForCheck("");
            chat.OpenConversation(entrance.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();

            // A reply already on screen is exactly when a picture gets added, and the steer is a second append:
            // a composer that empties its chips while the run refused the message would drop the picture the
            // person is looking at.
            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["这条是引导之后答的"], gate: hold.Task);
            panel.SetInputForCheck("先把这条回答停住");
            _ = panel.SendComposerForCheck();
            await WaitUntilAsync(() => panel.IsStreamingForCheck);
            var dragged = new DataTransfer();
            if (droppedFile is not null) dragged.Add(DataTransferItem.CreateFile(droppedFile));
            panel.DropForCheck(dragged);
            panel.SetInputForCheck("再看这张图");
            await panel.SendComposerForCheck();
            Dispatcher.UIThread.RunJobs();
            Check(panel.SteerConfirmVisibleForCheck && panel.SteerConfirmTextForCheck == "再看这张图"
                  && panel.SteerDraftPictureCountForCheck == 1,
                "回复进行中带图再发一次，先摆到确认条上，图跟着这句一起走（实际「"
                + panel.SteerConfirmTextForCheck + "」）");
            panel.ClickSteerCommitForCheck();
            hold.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
            var steered = chat.StoredCopyForCheck(entrance.Id)?.Messages
                .LastOrDefault(turn => turn.Role == ChatRoles.User && turn.Text == "再看这张图");
            Check(steered?.Images.Count == 1 && panel.PendingPictureCountForCheck == 0
                  && chat.RunningCount == 0,
                "回答进行到一半时拖进来的图，跟着那条引导一起进会话，composer 随后清空（实际 "
                + (steered?.Images.Count ?? -1) + " 张）");
            await DeleteFixtureConversationAsync(chat, entrance.Id);

            // ── One gesture, two channels: a picture becomes a picture, a text file becomes context ──
            // An .svg is the case that proves the split. A person wants to show what it draws, no picture admission
            // recognizes it by its header, and the markup is the only form of it a model can be sent — so it rides
            // as text while the chip draws the vector it is. A binary and an oversized file are the two refusals
            // that have to name their rule, because "not text" and "too large" are different fixes.
            var vector = System.IO.Path.Combine(scratch, "icon.svg");
            System.IO.File.WriteAllText(vector,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\">\n"
                + "  <rect width=\"8\" height=\"8\" fill=\"#3EC8F5\"/>\n</svg>\n");
            var binary = System.IO.Path.Combine(scratch, "payload.bin");
            System.IO.File.WriteAllBytes(binary, [0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01]);
            var oversized = System.IO.Path.Combine(scratch, "huge.svg");
            using (var stream = System.IO.File.Create(oversized))
                stream.SetLength(ChatContextReader.MaxAttachedFileBytes + 1);

            var files = chat.StartConversation();
            chat.SetApprovalMode(files.Id, ToolApprovalModes.Full);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["看过了"]);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();

            Check(panel.ComposerMenuHeadersForCheck.Contains(HubStrings.Get("ChatAddLocalFile")),
                "「+」菜单里有「添加本地文件…」：文件与文件夹是同一条上下文通道的两个入口");

            var vectorFile = await shell.StorageProvider.TryGetFileFromPathAsync(vector);
            var draggedVector = new DataTransfer();
            if (vectorFile is not null) draggedVector.Add(DataTransferItem.CreateFile(vectorFile));
            var picturesBefore = panel.PendingPictureCountForCheck;
            panel.DropForCheck(draggedVector);
            Dispatcher.UIThread.RunJobs();
            Check(vectorFile is not null && panel.PendingPictureCountForCheck == picturesBefore
                  && panel.ContextAttachmentLabelsForCheck.Contains(HubStrings.Get("ChatFilePrefix") + "icon.svg")
                  && panel.ContextChipHasFaceForCheck(panel.ContextAttachmentCountForCheck - 1),
                "把 .svg 拖到输入框上得到一张带矢量封面的文件附件，而不是被当成图片拒掉或塞进图片通道（实际 "
                + string.Join(" / ", panel.ContextAttachmentLabelsForCheck) + "）");

            panel.AddContextFileForCheck(binary);
            Check(panel.LastNoticeTextForCheck?.Contains("文本文件", StringComparison.Ordinal) == true
                  && panel.ContextAttachmentCountForCheck == 1,
                "二进制文件按「不是文本」被拒并说清规则是扩展名，附件条没有多出一张（提示："
                + (panel.LastNoticeTextForCheck ?? "无") + "）");

            panel.AddContextFileForCheck(oversized);
            Check(panel.LastNoticeTextForCheck?.Contains("MiB", StringComparison.Ordinal) == true
                  && panel.ContextAttachmentCountForCheck == 1,
                "超过单文件上限的文件按大小被拒，和图片一样先看大小再读字节（提示："
                + (panel.LastNoticeTextForCheck ?? "无") + "）");

            panel.SetInputForCheck("这个图标画的是什么");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            var carried = chat.StoredCopyForCheck(files.Id)?.Messages
                .LastOrDefault(turn => turn.Role == ChatRoles.User);
            Check(carried?.AttachedContext?.Contains("Attached file: icon.svg", StringComparison.Ordinal) == true
                  && carried.AttachedContext.Contains("#3EC8F5", StringComparison.Ordinal)
                  && carried.Images.Count == 0,
                "发出去的是这份 SVG 的标记文本、图片通道一字节没走（实际 AttachedContext "
                + (carried?.AttachedContext?.Length ?? -1) + " 字符，" + (carried?.Images.Count ?? -1) + " 张图）");
            Check(panel.ContextAttachmentCountForCheck == 0 && panel.ContextAttachmentLabelsForCheck.Length == 0,
                "发出去之后上下文附件条清空 —— 收到的文本在会话里，chip 不留在输入框上");

            chat.DeleteConversation(files.Id);
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            while (panel.PendingPictureCountForCheck > 0) panel.RemovePictureChipForCheck(0);
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
