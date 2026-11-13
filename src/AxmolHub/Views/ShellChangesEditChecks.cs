using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Real disk baselines and the shipped Changes controls, with no second diff implementation.</summary>
internal sealed class ShellChangesEditChecks(Action<bool, string> check)
{
    internal Task VerifyAsync(string scratchRoot)
    {
        var root = Path.Combine(scratchRoot, "changes-edit");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "probe.cpp");
        const string before = "/* start\nold */\nint value = 1;\n";
        const string after = "/* start\nnew */\nint value = 2;\n";
        File.WriteAllText(path, after);
        var scope = new WorkspaceToolScope(root, new WorkspaceGuards(null, []), null, "changes-edit", [], null, null);
        ChangesEditBuffer? Open() => ChangesEditBuffer.Open("probe.cpp", () => scope);
        var buffer = Open();
        if (buffer is null) { check(false, "差异编辑夹具：文本缓冲区可读取"); return Task.CompletedTask; }
        var session = new ChangesEditSession();
        session.Begin();
        session.Buffers.Add("probe.cpp", buffer);
        buffer.Text = after.Replace("2", "3", StringComparison.Ordinal);
        session.Cancel();
        check(File.ReadAllText(path) == after && !session.IsDirty && !session.Editing,
            "差异编辑：Cancel 只丢弃手动缓冲，保留进入编辑前的 AI 改动");
        buffer = Open()!;
        buffer.Text = after.Replace("2", "3", StringComparison.Ordinal);
        File.WriteAllText(path, "external\n");
        var conflict = buffer.Save();
        check(conflict.Verdict == TextReplaceVerdict.ChangedSince && File.ReadAllText(path) == "external\n" && buffer.IsDirty,
            $"差异编辑：磁盘冲突不覆盖外部内容，缓冲仍在（实际 {conflict.Verdict}）");
        File.WriteAllText(path, after);
        buffer = Open()!;
        buffer.Text = after.Replace("2", "4", StringComparison.Ordinal);
        scope = WorkspaceToolScope.Empty;
        var refused = buffer.Save();
        check(refused.Verdict != TextReplaceVerdict.Written && File.ReadAllText(path) == after && buffer.IsDirty,
            $"差异编辑：保存时重新校验权限（实际 {refused.Verdict}）");
        scope = new WorkspaceToolScope(root, new WorkspaceGuards(null, []), null, "changes-edit", [], null, null);
        session.Begin();
        session.Buffers.Add("probe.cpp", buffer);
        var saved = session.Save();
        check(saved is null && File.ReadAllText(path) == buffer.Text && !session.Editing,
            "差异编辑：Save 写入工作区，不创建 Git 提交");

        var secondPath = Path.Combine(root, "second.cpp");
        File.WriteAllText(secondPath, "int second = 1;\n");
        var firstBatch = Open()!;
        var secondBatch = ChangesEditBuffer.Open("second.cpp", () => scope)!;
        firstBatch.Text += "// first batch\n";
        secondBatch.Text += "// second batch\n";
        session.Begin();
        session.Buffers.Add("probe.cpp", firstBatch);
        session.Buffers.Add("second.cpp", secondBatch);
        File.WriteAllText(secondPath, "// external second\n");
        var partial = session.Save();
        check(partial is not null && !firstBatch.IsDirty && secondBatch.IsDirty && session.Editing
            && File.ReadAllText(secondPath) == "// external second\n" && ChangesEditSession.DirtyCount > 0,
            "差异编辑：批量保存部分失败时保留失败缓冲，不重复保存已成功的文件");
        session.Cancel();
        File.WriteAllText(path, after);

        var pane = new InspectorPanel();
        pane.OpenEditBuffer = (_, relative) => ChangesEditBuffer.Open(relative, () => scope);
        var change = new ChangedFile("probe.cpp", ChangeVerdict.Edited, "call", 0, null, null, null, "Edited", true);
        var repository = new GitRepositoryState(GitReadOutcome.Ok, root, "main", 0, 0,
            [new GitStatusEntry("probe.cpp", "M", "M", false)],
            new Dictionary<string, string>(StringComparer.Ordinal), false, null);
        pane.ReadComparisonRequested = (_, _, _) => Task.FromResult<ChatFileSnapshot?>(new(before, after));
        void Reload(string owner)
        {
            pane.Reload(null, [change], (_, _) =>
                (FileDiff.Unified(before, after, "probe.cpp"), UndoCopyState.Read), repository, "changes", null,
                _ => (new ChatFileSnapshot(before, after), UndoCopyState.Read), owner);
            Dispatcher.UIThread.RunJobs();
            if (!pane.RepoRowExpandedForCheck(0)) pane.ClickRepoRowForCheck(0);
            Dispatcher.UIThread.RunJobs();
        }
        Reload("one");
        var window = new Window { Width = 900, Height = 700, Content = pane };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Button? Button(string tag) => pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Tag, tag));
            void Click(string tag) => Button(tag)?.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            window.UpdateLayout();
            var editButton = Button("InspectorEditDiff");
            var settingsButton = Button("InspectorViewSettings");
            var editPosition = editButton?.TranslatePoint(default, pane);
            var settingsPosition = settingsButton?.TranslatePoint(default, pane);
            check(editButton is not null && settingsButton is not null
                  && editPosition is { } editPoint && settingsPosition is { } settingsPoint
                  && editPoint.X < settingsPoint.X
                  && Math.Abs(editPoint.Y + editButton.Bounds.Height / 2 - settingsPoint.Y - settingsButton.Bounds.Height / 2) <= 0.5
                  && editButton.Classes.Contains("viewer-action") && settingsButton.Classes.Contains("viewer-action"),
                "差异编辑与设置按钮共用悬停样式，编辑在左且两者垂直居中对齐");
            pane.PressViewSettingsForCheck();
            pane.ClickViewMenuRowForCheck("InspectorMenuSplit");
            Click("InspectorEditDiff");
            window.UpdateLayout();
            var editors = pane.GetVisualDescendants().OfType<TextEditor>().Where(e => Equals(e.Tag, "InspectorDiffEditor")).ToArray();
            check(editors.Length > 0 && editors.All(editor => !editor.IsReadOnly)
                && Button("InspectorCancelEdit")?.IsVisible == true && !pane.ViewSettingsVisibleForCheck,
                $"差异编辑：进入编辑显示 Cancel/Save 并隐藏设置（实际 {editors.Length} 个源片段）");
            var last = editors.LastOrDefault();
            if (last is null) return Task.CompletedTask;
            last.Text += "// manual\n";
            Reload("one");
            window.UpdateLayout();
            check(pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.Text.Contains("// manual", StringComparison.Ordinal))
                && Button("InspectorSaveEdit")?.IsEnabled == true,
                "差异编辑：同会话刷新保留未保存的手动缓冲");
            window.UpdateLayout();
            check(pane.GetVisualDescendants().OfType<TextEditor>().Any(e => !e.IsReadOnly && e.Text.Contains("// manual", StringComparison.Ordinal))
                && pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.IsReadOnly),
                "差异编辑：双栏刷新保留缓冲，仅新版可编辑");
            File.WriteAllText(path, "// external during edit\n");
            Click("InspectorSaveEdit");
            window.UpdateLayout();
            check(Button("InspectorCancelEdit")?.IsVisible == true && Button("InspectorSaveEdit")?.IsEnabled == true
                && pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.Text.Contains("// manual", StringComparison.Ordinal))
                && File.ReadAllText(path) == "// external during edit\n",
                "差异编辑：Save 冲突后保持编辑状态与可重试缓冲");
            File.WriteAllText(path, after);
            Reload("two");
            window.UpdateLayout();
            check(Button("InspectorEditDiff")?.IsVisible == true,
                "差异编辑：切换会话不把旧缓冲显示到新会话");
            Reload("one");
            window.UpdateLayout();
            check(pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.Text.Contains("// manual", StringComparison.Ordinal)),
                "差异编辑：返回原会话恢复未保存缓冲");
            Click("InspectorCancelEdit");
            check(!pane.GetVisualDescendants().OfType<TextEditor>().Any(e => Equals(e.Tag, "InspectorDiffEditor"))
                && File.ReadAllText(path) == after && pane.ViewSettingsVisibleForCheck,
                "差异编辑：Cancel 退出编辑、恢复设置按钮且不改磁盘");

            pane.ReadReviewRequested = request =>
            {
                if (request.Scope != GitReviewScope.Branch)
                    return Task.FromResult(new InspectorPanel.ReviewRead(null, null, null, null, GitDiffStats.Empty));
                if (request.Path is not null)
                {
                    var older = new GitFileVersion(true, TextFile.Decode(System.Text.Encoding.UTF8.GetBytes("branch baseline\n")), null);
                    var newer = new GitFileVersion(true, TextFile.Read(path), null);
                    return Task.FromResult(new InspectorPanel.ReviewRead(null, null, null,
                        new GitFileComparison(request.Path, null, GitFileRange.Range, older, newer)));
                }
                return Task.FromResult(request.Base is null
                    ? new InspectorPanel.ReviewRead(null, null, [new GitBranchInfo("main", "origin/main", true)], null)
                    : new InspectorPanel.ReviewRead(null, new GitChangedPaths(GitCommitListOutcome.Ok, null,
                        [new GitChangedPath("probe.cpp", "M")], "base"), null, null));
            };
            pane.SetScope(GitReviewScope.Unstaged);
            pane.ClickRepoRowForCheck(0);
            Click("InspectorEditDiff");
            window.UpdateLayout();
            var unstagedEditor = pane.GetVisualDescendants().OfType<TextEditor>()
                .LastOrDefault(e => Equals(e.Tag, "InspectorDiffEditor"));
            if (unstagedEditor is not null) unstagedEditor.Text += "// retained across scopes\n";
            check(unstagedEditor is { IsReadOnly: false } && Button("InspectorSaveEdit")?.IsEnabled == true,
                "Unstaged 差异的当前文件一侧可编辑，编辑后可以保存");
            pane.SetScope(GitReviewScope.Branch);
            pane.ClickRepoRowForCheck(0);
            window.UpdateLayout();
            var branchEditors = pane.GetVisualDescendants().OfType<TextEditor>().ToArray();
            check(branchEditors.Any(e => e.IsReadOnly && e.Text.Contains("branch baseline", StringComparison.Ordinal))
                  && branchEditors.Any(e => !e.IsReadOnly && e.Text.Contains("retained across scopes", StringComparison.Ordinal)),
                "切到 Branch 后保留工作区缓冲并使用该分支自己的只读比较基准");
            pane.SetScope(GitReviewScope.LastTurn);
            pane.ClickChangeRowForCheck(0);
            window.UpdateLayout();
            var lastTurnEditors = pane.GetVisualDescendants().OfType<TextEditor>().ToArray();
            check(pane.ChangeRowExpandedForCheck(0)
                  && !lastTurnEditors.Any(e => Equals(e.Tag, "InspectorDiffEditor")),
                "即使工作区有未保存编辑，Last turn 的原始快照仍以只读差异展示（行展开 "
                + pane.ChangeRowExpandedForCheck(0) + "，改动行 " + pane.ChangeRowCountForCheck
                + "，编辑框 " + string.Join("/", lastTurnEditors.Select(e => e.Tag + ":" + e.IsReadOnly)) + "）");
            pane.SetScope(GitReviewScope.Staged);
            pane.ClickRepoRowForCheck(0);
            window.UpdateLayout();
            check(!pane.GetVisualDescendants().OfType<TextEditor>().Any(e => Equals(e.Tag, "InspectorDiffEditor"))
                  && Button("InspectorSaveEdit")?.IsEnabled == true,
                "Staged 快照保持只读，其他范围的未保存编辑仍可保存");
            Click("InspectorCancelEdit");
        }
        finally { window.Close(); ChangesEditSession.DiscardAll(); }
        var diffAlongside = Open()!;
        var fullEditor = new CodeEditorView(path, TextFile.Read(path), (baseline, text) =>
        {
            using var lease = TextFile.AcquireWrite(path);
            return lease.Save(() => WorkspacePaths.ResolveWrite(scope.Sandbox, "probe.cpp", scope.Guards), baseline, text);
        }, writable: true, owner: null);
        var fullWindow = new Window { Width = 800, Height = 600, Content = fullEditor };
        try
        {
            fullWindow.Show();
            fullWindow.UpdateLayout();
            var source = fullEditor.GetVisualDescendants().OfType<TextEditor>().Single();
            // Resource lookup is deferred to editor creation and panel opening; a build cannot catch it.
            var search = source.SearchPanel;
            search.IsReplaceMode = true;
            search.Open();
            fullWindow.UpdateLayout();
            var fields = search.GetVisualDescendants().OfType<TextBox>().Select(box => box.Name).ToArray();
            check(search.IsOpened && fields.Contains("PART_searchTextBox") && fields.Contains("PART_replaceTextBox"),
                "完整文件编辑：查找/替换模板及全部操作图标可加载，不因静态资源缺失崩溃");
            search.Close();
            source.Document.Insert(source.Document.TextLength, "// full file\n");
            source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.S,
                KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control });
            check(!fullEditor.IsDirty && File.ReadAllText(path).Contains("// full file", StringComparison.Ordinal),
                "完整文件编辑：保存快捷键写入整文件，并更新独立缓冲的磁盘基准");
            diffAlongside.Text += "// diff edit\n";
            var overlap = diffAlongside.Save();
            check(overlap.Verdict == TextReplaceVerdict.ChangedSince && diffAlongside.IsDirty
                && File.ReadAllText(path).Contains("// full file", StringComparison.Ordinal),
                "两种编辑共存：整文件保存后差异缓冲检测冲突，不互相覆盖");
        }
        finally { fullWindow.Close(); }
        using var syntax = new ChangesDiffSyntax("probe.cpp", FileDiff.Unified(before, after, "probe.cpp"), new(before, after));
        check(syntax.Line(true, 2).Count > 0 && syntax.Line(false, 2).Count > 0,
            "差异高亮：两侧从完整原文计算跨行注释状态");
        return Task.CompletedTask;
    }
}
