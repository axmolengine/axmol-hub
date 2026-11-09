using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellCodeBlocksChecks
{
    private readonly Action<bool, string> Check;
    internal ShellCodeBlocksChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal Task VerifyCodeBlockGeometryAsync()
    {
        const string single = "```text\nCompleted without a final answer for a very long model response, with finish_reason=tool_calls and tool_calls=1 after recording all the request metadata\n```";
        const string multiple = "```sh\ngrep -h \"Completed without a final answer\" ~/Library/Application\\ Support/AxmolHub/data/logs/*.log | tail -20\ngrep -h \"Model usage\" ~/Library/Application\\ Support/AxmolHub/data/logs/*.log | tail -20\n```";
        var column = new StackPanel { Width = 600 };
        column.Children.Add(MarkdownMessageRenderer.Render(single));
        column.Children.Add(MarkdownMessageRenderer.Render(multiple));
        var inspector = new InspectorPanel();
        inspector.Reload(single + "\n\n" + multiple, [], (_, _) => default, null, "plan");
        var surface = new Grid { ColumnDefinitions = new ColumnDefinitions("600,360") };
        surface.Children.Add(column);
        Grid.SetColumn(inspector, 1);
        surface.Children.Add(inspector);
        var window = new Window
        {
            Width = 960, Height = 600, Content = surface,
            WindowDecorations = WindowDecorations.None,
        };
        var previous = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.FirstOrDefault(candidate => candidate.IsActive);
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var chatEditors = column.GetVisualDescendants().OfType<TextEditor>().ToArray();
            var planEditors = inspector.GetVisualDescendants().OfType<TextEditor>().ToArray();
            Check(chatEditors.Length == 2 && planEditors.Length == 2,
                "会话历史与计划栏均渲染单行日志及多行命令（会话=" + chatEditors.Length
                + "，计划=" + planEditors.Length + "）");
            foreach (var (name, editors) in new[] { ("会话历史", chatEditors), ("计划栏", planEditors) })
            {
                for (var i = 0; i < editors.Length; i++)
                    CheckCodeBlockGeometry(editors[i], name + (i == 0 ? "单行长日志" : "多行长命令"));
            }
            CheckCodeBlockLinkInk(chatEditors.Concat(planEditors).ToArray(), "会话历史与计划栏");
            SmokeCapture.Capture(window, System.IO.Path.Combine(ScratchDirectory.Resolve("code-block-geometry"), "after.png"));
        }
        finally
        {
            window.Close();
            previous?.Activate();
            Dispatcher.UIThread.RunJobs();
        }
        return Task.CompletedTask;
    }

    internal async Task VerifyStreamingCodeBlockAsync(MainWindow shell, ChatPanel panel)
    {
        // Keep the command wider than the full 820px chat column, not just the narrower standalone fixture.
        const string command = "grep -h \"Completed without a final answer\" ~/Library/Application\\ Support/AxmolHub/data/logs/*.log | tail -20 | sort | uniq -c | sort -nr | head -20";
        var before = shell.Chat.ActiveConversation?.Id;
        var session = shell.Chat.StartConversation();
        shell.Chat.OpenConversation(session.Id);
        shell.OpenAssistant();
        panel.Reload();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(["```sh\n" + command + "\n```"],
            null, gate.Task, arrived);
        try
        {
            panel.SetInputForCheck("打印日志命令");
            _ = panel.SendComposerForCheck();
            await WaitForSignalAsync(arrived.Task);
            // The chunk has left the client; the block that draws it is built on the dispatcher pass after that,
            // and the scripted client is still gated — so waiting for the paint cannot outlive 「before it was
            // filed」, while reading before the paint is what made this line depend on the machine's load.
            await WaitUntilAsync(() => CodeEditor(panel, command) is not null, maxTicks: 120);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var live = CodeEditor(panel, command);
            Check(live is not null, "流式回复在尚未落盘时已经把长命令渲染为代码块" + Observed(panel, shell, session));
            if (live is not null) CheckCodeBlockGeometry(live, "流式回复长命令");
            gate.TrySetResult(true);
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var stored = CodeEditor(panel, command);
            Check(stored is not null, "完成后长命令回到历史消息的代码块" + Observed(panel, shell, session));
            if (stored is not null) CheckCodeBlockGeometry(stored, "流式落盘后长命令");
        }
        finally
        {
            gate.TrySetResult(true);
            await WaitForRunAsync(shell.Chat, session.Id);
            shell.Chat.ClientOverride = null;
            shell.Chat.DeleteConversation(session.Id);
            if (before is { } previous) shell.Chat.OpenConversation(previous);
            shell.OpenAssistant();
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The code block the panel drew for this command, or null when it has not been built yet.</summary>
    private static TextEditor? CodeEditor(ChatPanel panel, string command)
        => panel.GetVisualDescendants().OfType<TextEditor>()
            .FirstOrDefault(editor => editor.Text.Contains(command, StringComparison.Ordinal));

    /// <summary>What the panel actually held, so a failure names the frame it read instead of only the text that
    /// was missing from it: the code blocks on screen, the painted rows, the stored turns and whether the run is
    /// still going tell 「nothing was rendered」 from 「the wrong session is on screen」.</summary>
    private static string Observed(ChatPanel panel, MainWindow shell, Conversation session)
        => "（代码块 " + panel.GetVisualDescendants().OfType<TextEditor>().Count()
           + " 个，屏上 " + panel.PaintedRowsForCheck.Count + " 行，会话 " + session.Messages.Count
           + " 条，运行中 " + shell.Chat.IsRunning(session.Id) + "）";

    /// <summary>A bare URL in a code block is drawn by AvaloniaEdit's own link layer rather than by the syntax
    /// definition, so no palette rule in <see cref="DarkSyntax"/> reaches it: it shipped in the library's #0000FF, picked
    /// for a white page, about 2:1 on the dark well. Reading the layer rather than a screenshot is the only way this
    /// can fail in CI — the ink is a brush on the text view, and a wrong one renders without an error.</summary>
    internal void CheckCodeBlockLinkInk(TextEditor[] editors, string name)
    {
        var inks = editors.Select(editor => editor.TextArea.TextView.LinkTextForegroundBrush).ToArray();
        var well = DarkSyntax.Well();
        var contrast = inks.Select(ThemeProbe.ColorOf).OfType<Avalonia.Media.Color>()
            .Select(color => DarkSyntax.Contrast(color, well)).DefaultIfEmpty(0).Min();
        Check(inks.Length > 0 && inks.All(ink => ThemeProbe.IsToken(ink, "Hub.Link"))
              && contrast >= DarkSyntax.MinimumContrast,
            name + "：代码块里的裸 URL 用 Hub 链接色，不是编辑器自带的页面蓝（" + ThemeProbe.Describe(inks.FirstOrDefault())
            + "，在 " + well + " 上对比度 " + contrast.ToString("0.0") + "，下限 "
            + DarkSyntax.MinimumContrast.ToString("0.0") + "）");
    }

    internal void CheckCodeBlockGeometry(TextEditor editor, string name)
    {
        var border = editor.GetVisualAncestors().OfType<Border>()
            .First(candidate => candidate.Classes.Contains("CodeBlock"));
        var grid = (Grid)border.Child!;
        var view = editor.GetVisualDescendants().OfType<AvaloniaEdit.Rendering.TextView>().Single();
        var bar = editor.GetVisualDescendants().OfType<ScrollBar>()
            .Single(candidate => candidate.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var button = grid.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Classes.Contains("markdown-code-copy"));
        // Read the laid-out text metrics, not the default font estimate; the editor may select a fallback
        // font. Parent and child extents can round to adjacent physical pixels on fractional-DPI displays.
        view.EnsureVisualLines();
        var lines = view.VisualLines;
        var textHeight = lines.Sum(line => line.Height);
        var scale = TopLevel.GetTopLevel(editor)?.RenderScaling ?? 1;
        var tolerance = 1 / scale;
        var geometry = $"border={border.Bounds} grid={grid.Bounds} editor={editor.Bounds} "
            + $"view={view.Bounds} bar={bar.Bounds} copy={button.Bounds} visualLines={lines.Count} textHeight={textHeight} "
            + $"barVisible={bar.IsVisible} range={bar.Minimum}/{bar.Maximum} row={grid.RowDefinitions[1].ActualHeight}";
        Check(lines.Count > 0 && lines.Sum(line => line.LastDocumentLine.LineNumber - line.FirstDocumentLine.LineNumber + 1)
                  == editor.Document.LineCount && view.Bounds.Height >= textHeight - tolerance
              && bar.IsVisible && bar.Maximum > bar.Minimum
              && bar.Bounds.Top >= view.Bounds.Bottom - 0.5
              && editor.Bounds.Height >= view.Bounds.Height + bar.Bounds.Height - 0.5
              && grid.RowDefinitions[1].ActualHeight >= editor.Bounds.Height - tolerance
              && button.Bounds.Bottom <= editor.Bounds.Top + 0.5,
            name + "：文字完整、横向滚动条不盖最后一行、复制按钮占独立行（" + geometry + "）");
    }
}
