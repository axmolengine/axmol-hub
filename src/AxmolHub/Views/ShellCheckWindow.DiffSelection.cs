using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Guards the diff's actual pointer-selection path. The old inspector checks read labels only, so a
/// derived text control with no selection theme passed while showing neither a text cursor nor a highlight.</summary>
internal sealed class ShellDiffSelectionChecks(Action<bool, string> check)
{
    internal async Task VerifyAsync(MainWindow shell)
    {
        var wasOpen = shell.InspectorVisibleForCheck;
        var savedTab = shell.InspectorTabForCheck;
        var savedWidth = shell.InspectorWidthForCheck;
        Func<GitRepositoryState, GitStatusEntry, GitFileRange, Task<ChatFileSnapshot?>>? savedRead = null;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        try
        {
            shell.Show();
            shell.OpenAssistant();
            shell.OpenInspector("changes");
            shell.SetInspectorWidthForCheck(360);
            if (shell.InspectorPaneForCheck is not { } pane)
            {
                check(false, "夹具：diff 拖选检查没有审阅面板");
                return;
            }

            var sample = "--- a/file\n+++ b/file\n@@ -4,2 +4,2 @@\n--- actual removed\n+++ actual added\n@@ -20 +20 @@\n-old\n+new\n";
            var counts = InspectorPanel.PatchCounts(sample);
            using var syntax = new ChangesDiffSyntax("file.cpp", sample, null);
            var compact = InspectorPanel.BuildUnifiedDiffBlock(sample, syntax, compact: true);
            var split = InspectorPanel.BuildSplitDiffBlock(sample, syntax);
            var compactText = string.Join("|", compact.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(InspectorPanel.TextForCheck));
            var splitText = string.Join("|", split.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(InspectorPanel.TextForCheck));
            check(counts == (2, 2) && compactText.Contains("-- actual removed") && compactText.Contains("++ actual added")
                  && !compactText.Contains("@@") && !compactText.Contains("a/file")
                  && splitText.Contains("-- actual removed") && splitText.Contains("++ actual added")
                  && !splitText.Contains("@@") && !splitText.Contains("a/file"),
                "精简 diff 正确隐藏元数据并保留多段变更及类似头部的代码行");

            var line = string.Concat(Enumerable.Repeat("0123456789 abcdefghij ", 20));
            var oldText = "old\n";
            var newText = "old\n" + line + "\n";
            // The Changes tab lists the repository, so its diff is opened through a repository row. A session
            // change row still exists in the panel but sits in a list the pane keeps hidden — nothing inside a
            // hidden list is measured or hit-tested, which is why this fixture could only ever see an empty pane.
            var readDirectory = ScratchDirectory.Resolve("diff-selection");
            var repository = new GitRepositoryState(GitReadOutcome.Ok,
                System.IO.Path.Combine(readDirectory, "workspace"), "main", 0, 0,
                [new GitStatusEntry("selection.cpp", "M", "M", false)],
                new Dictionary<string, string>(StringComparer.Ordinal), false, null);
            savedRead = pane.ReadComparisonRequested;
            pane.ReadComparisonRequested = (_, _, _) => Task.FromResult<ChatFileSnapshot?>(new(oldText, newText));
            // The pane is the shell's, and every repository read that lands repaints it from the shell's own
            // snapshot — which here knows no workspace, because this fixture has none. The injected reading is
            // therefore installed again until it survives the queued work, and the picker is named each time: a
            // scope that is not on show builds no rows, and the last fixture leaves the picker wherever it likes.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                pane.Reload(null, [],
                    (_, _) => (FileDiff.Unified(oldText, newText, "selection.cpp"), UndoCopyState.Read),
                    repository, "changes");
                pane.PickScopeForCheck(GitReviewScope.Uncommitted);
                Dispatcher.UIThread.RunJobs();
                if (!pane.RepoRowExpandedForCheck(0)) pane.ClickRepoRowForCheck(0);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (pane.RepoRowCountForCheck > 0 && pane.RepoRowExpandedForCheck(0)) break;
            }
            await Task.Delay(30);
            shell.UpdateLayout();
            var text = pane.GetVisualDescendants().OfType<SelectableTextBlock>()
                .FirstOrDefault(block => InspectorPanel.TextForCheck(block) == line);
            var viewer = text?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            if (text is null || viewer is null || viewer.Extent.Width <= viewer.Viewport.Width)
            {
                var observed = text is null
                    ? "正文块 0 个，仓库行 " + pane.GetVisualDescendants().OfType<Button>()
                        .Count(button => button.Classes.Contains("inspector-change-header")) + " 行"
                      + "，页 " + pane.SelectedTabKeyForCheck + "，git 列表 " + pane.RepoScopeListVisibleForCheck
                      + "，轮次列表 " + pane.LastTurnListVisibleForCheck
                      + "，行 " + pane.RepoRowCountForCheck
                      + "，说明「" + pane.RepoNoticeForCheck + "」，标题「" + pane.RepoHeadlineForCheck + "」"
                    : "文本宽 " + InspectorPanel.TextForCheck(text).Length + "，最近视口 "
                      + string.Join("/", text.GetVisualAncestors().OfType<ScrollViewer>()
                          .Select(ancestor => ancestor.Extent.Width.ToString("0.0") + " 对 "
                              + ancestor.Viewport.Width.ToString("0.0")));
                check(false, "夹具：diff 长行没有形成可横向滚动的文本视口（" + observed + "）");
                return;
            }

            // The revision picker this method used to size and theme lived on a session change row, and the
            // session-only Changes list is no longer shown at all. The claim that outlived it is the diff's own
            // selection colour, which has to follow the theme on the row that is on screen.
            SmokeCapture.Capture(pane, System.IO.Path.Combine(readDirectory, "repository-row.png"));
            if (Application.Current is { } app)
            {
                var previousTheme = app.RequestedThemeVariant;
                var themedSelection = true;
                var selectionColors = new List<string>();
                try
                {
                    foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                    {
                        app.RequestedThemeVariant = variant;
                        Dispatcher.UIThread.RunJobs();
                        shell.UpdateLayout();
                        selectionColors.Add(variant + ":" + text.SelectionBrush);
                        themedSelection &= MarkdownMessageRenderer.MatchesToken(text.SelectionBrush, "Hub.CodeSelection");
                        text.SelectionStart = 2;
                        text.SelectionEnd = 7;
                        SmokeCapture.Capture(viewer,
                            System.IO.Path.Combine(ScratchDirectory.Resolve("diff-selection"), "diff-selection-" + variant + ".png"));
                        text.ClearSelection();
                    }
                }
                finally
                {
                    app.RequestedThemeVariant = previousTheme;
                    Dispatcher.UIThread.RunJobs();
                    shell.UpdateLayout();
                }
                check(themedSelection, "diff 选区使用与聊天相同的 Hub.Selection，并随深浅主题切换（"
                    + string.Join("；", selectionColors) + "）");
            }

            Point At(int index)
            {
                var caret = text.TextLayout.HitTestTextPosition(index);
                return text.TranslatePoint(new Point(caret.X + 0.1, caret.Y + caret.Height / 2), shell) ?? default;
            }
            Point Edge(bool right) => viewer.TranslatePoint(
                new Point(right ? viewer.Viewport.Width - 2 : 2,
                    (text.TranslatePoint(new Point(0, text.Bounds.Height / 2), viewer) ?? default).Y), shell) ?? default;
            InputElement? Hit(Point point) => shell.InputHitTest(point) as InputElement;
            void Press(Point point)
            {
                var target = Hit(point);
                // Routed synthetic events bypass the input manager's native pointer-focus step.
                target?.Focus();
                target?.RaiseEvent(new PointerPressedEventArgs(target, pointer, shell, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None, 1));
            }
            void Move(Point point)
            {
                var target = pointer.Captured as InputElement ?? Hit(point);
                target?.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, target, pointer, shell, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            }
            void Release(Point point)
            {
                var target = pointer.Captured as InputElement ?? Hit(point);
                target?.RaiseEvent(new PointerReleasedEventArgs(target, pointer, shell, point, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                    KeyModifiers.None, MouseButton.Left));
            }

            var hitText = ReferenceEquals(Hit(At(2)), text);
            var before = System.IO.Path.Combine(readDirectory, "before.png");
            var after = System.IO.Path.Combine(readDirectory, "selected.png");
            SmokeCapture.Capture(text, before);
            Press(At(2));
            Move(At(7));
            Release(At(7));
            SmokeCapture.Capture(text, after);
            var original = SmokeCapture.ReadBgra(before, out _, out _, out _);
            var selected = SmokeCapture.ReadBgra(after, out _, out _, out _);
            check(hitText && text.SelectedText == "23456"
                  && text.Cursor?.ToString() == "Ibeam"
                  && text.SelectionBrush is ISolidColorBrush { Color.A: > 0 }
                  && !original.AsSpan().SequenceEqual(selected),
                "diff 正文可命中并拖出可见选区，光标为文本选择形态（命中 " + hitText
                + "，选中「" + text.SelectedText + "」，光标 " + text.Cursor
                + "，选区色 " + text.SelectionBrush + "，命中元素 " + Hit(At(2))?.GetType().Name + "）");

            // Hold the pointer still at each edge: scrolling only on PointerMoved would stop immediately.
            var observations = new List<string>();
            var followsSelection = true;
            foreach (var right in new[] { true, false })
            {
                viewer.Offset = new Vector(right ? 0 : 300, viewer.Offset.Y);
                shell.UpdateLayout();
                var start = viewer.TranslatePoint(new Point(viewer.Viewport.Width / 2,
                    (text.TranslatePoint(new Point(0, text.Bounds.Height / 2), viewer) ?? default).Y), shell) ?? default;
                Press(start);
                Move(Edge(right));
                var offset = viewer.Offset.X;
                var endpoint = text.SelectionEnd;
                // Wait for two scrolling ticks, not a fixed frame budget on a loaded UI thread.
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 500)
                {
                    await Task.Delay(20);
                    shell.UpdateLayout();
                    if (Math.Abs(viewer.Offset.X - offset) >= 24
                        && (right ? text.SelectionEnd > endpoint : text.SelectionEnd < endpoint)) break;
                }
                var delta = viewer.Offset.X - offset;
                var selectionDelta = text.SelectionEnd - endpoint;
                followsSelection &= right ? delta > 0 && selectionDelta > 0 : delta < 0 && selectionDelta < 0;
                observations.Add((right ? "右" : "左") + $": offset {offset:0.0}→{viewer.Offset.X:0.0}, end {endpoint}→{text.SelectionEnd}");
                Release(Edge(right));
            }
            check(followsSelection, "diff 拖选停在左右边缘时持续滚动并延伸选区（" + string.Join("；", observations) + "）");

            var stoppedOffset = viewer.Offset.X;
            var stoppedSelection = text.SelectedText;
            await Task.Delay(60);
            check(stoppedSelection.Length > 0 && viewer.Offset.X == stoppedOffset
                  && text.SelectedText == stoppedSelection && pointer.Captured is null,
                "diff 松开拖选后不再滚动，选区保留供复制（offset " + stoppedOffset
                + "→" + viewer.Offset.X + "，选区长度 " + stoppedSelection.Length + "→" + text.SelectedText.Length + "）");
        }
        catch (Exception ex)
        {
            check(false, "diff 拖选检查异常：" + ex);
        }
        finally
        {
            pointer.Capture(null);
            if (shell.InspectorPaneForCheck is { } paneAfter) paneAfter.ReadComparisonRequested = savedRead;
            shell.SetInspectorWidthForCheck(savedWidth);
            shell.OpenInspector(savedTab);
            if (!wasOpen) shell.CloseInspector();
        }
    }
}
