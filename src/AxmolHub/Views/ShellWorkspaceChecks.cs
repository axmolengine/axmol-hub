using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using static AxmolHub.ShellCheckSupport;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed partial class ShellDecisionsChecks
{
    /// <summary>Exercise the two contracts replacing the old single-file overlay checks, using real control
    /// events and persistent buffers. Rendered fixtures accompany the geometry assertions without a screen grab.</summary>
    private static async Task<(bool Documents, bool Layout, bool Commands)> CheckWorkspaceDocumentsAsync(MainWindow shell, string root)
    {
        var view = shell.WorkspaceViewForCheck!;
        // The host repository ignores tmp/. Give this isolated workspace its own Git view so browser fixtures
        // are visible under the same ignore policy as a real project, without changing the host's rules.
        if (CommandShells.Resolve("git") is { } git)
            await new ProcessRunner(_ => { }).RunAsync(git, ["init", "--quiet"], root);
        var folder = Path.Combine(root, "browser-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "buffer.cs");
        File.WriteAllText(path, "namespace Fixture;\n");
        var scope = new WorkspaceToolScope(root, new WorkspaceGuards(null, []), null, "workspace-check", [], null, null);
        var scopeFor = () => scope;
        var originalContent = shell.WorkspaceDocumentContentForCheck!;
        var original = Path.Combine(root, "src/main.cpp");
        var error = CodeFileWindow.ShowEditor(shell, path, scopeFor);
        var editor = shell.WorkspaceDocumentContentForCheck as CodeEditorView;
        if (error is not null || editor is null) return (false, false, false);
        editor.EditorForCheck.Text += "// Saved through text menu\n";
        var menu = editor.EditorForCheck.ContextMenu!;
        var saveItem = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "save"));
        saveItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var commands = !editor.IsDirty && File.ReadAllText(path) == editor.EditorForCheck.Text
            && ReferenceEquals(view.SelectedContentForCheck, editor);
        File.WriteAllText(path, "// Reloaded through text menu\n");
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "reload"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        commands &= !editor.IsDirty && editor.EditorForCheck.Text == File.ReadAllText(path)
            && ReferenceEquals(view.SelectedContentForCheck, editor);
        var tabMenu = view.TabMenuForCheck(path)!;
        commands &= menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "reveal"))
            && tabMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "reveal"))
            && !tabMenu.Items.OfType<MenuItem>().Any(item => item.Tag is "save" or "reload");
        editor.EditorForCheck.Text += "// Unsaved fixture\n";
        var buffer = editor.EditorForCheck.Text;
        var tabs = true;
        var initialWidth = shell.InspectorWidthForCheck;
        foreach (var width in new[] { 360.0, 300.0, 480.0, 700.0, 360.0 })
        {
            shell.DragInspectorForCheck(shell.InspectorWidthForCheck - width);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            tabs &= view.TabLayoutForCheck(shell);
        }
        view.SelectOverflowForCheck("repo");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        tabs &= view.SelectedContentForCheck is InspectorPanel panel && panel.FileTreeToggleAlignedForCheck
            && view.TabLayoutForCheck(shell);
        view.OpenTool("plan");
        tabs &= !shell.InspectorPaneForCheck!.FileTreeToggleVisibleForCheck;
        view.SelectExisting(path);
        shell.SetInspectorWidthForCheck(initialWidth);
        view.ShowFiles();
        await view.TreeForCheck.RefreshAsync();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var tree = view.TreeForCheck;
        tree.FilterForCheck.Text = "buffer";
        var expanded = tree.ExpandedForCheck.ToArray();
        tree.CloseButtonForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var closed = !view.TreeVisibleForCheck && editor.IsDirty && ReferenceEquals(view.SelectedContentForCheck, editor);
        view.SelectExisting(original);
        CodeFileWindow.ShowEditor(shell, path, scopeFor);
        var respectedClose = !view.TreeVisibleForCheck && ReferenceEquals(view.SelectedContentForCheck, editor);
        view.FolderForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await tree.RefreshAsync();
        var restoredState = view.TreeVisibleForCheck && tree.FilterForCheck.Text == "buffer"
            && tree.SelectedForCheck == path && expanded.All(tree.ExpandedForCheck.Contains);
        view.ShowFiles();
        var idempotentEntry = view.TreeVisibleForCheck;
        var before = tree.EntryCountForCheck;
        File.WriteAllText(Path.Combine(folder, "added.cs"), "// Added fixture\n");
        await WaitUntilAsync(() => tree.EntryCountForCheck != before);
        var dynamic = tree.EntryCountForCheck > before;
        var drawer = view.DrawerForCheck;
        SaveWorkspaceRender(shell, root, "workspace-drawer.png");
        tree.CloseButtonForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.ClickInspectorExpandToggleForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        view.ShowFiles();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var wide = !view.DrawerForCheck && shell.InspectorExpandedForCheck && view.TreeVisibleForCheck;
        tabs &= view.TabLayoutForCheck(shell);
        SaveWorkspaceRender(shell, root, "workspace-expanded.png");
        tree.CloseButtonForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.UpdateLayout();
        var fills = view.SelectedContentForCheck!.Bounds.Width >= view.Bounds.Width - 2;
        shell.ClickInspectorExpandToggleForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var restoredLayout = !shell.InspectorExpandedForCheck && ReferenceEquals(view.SelectedContentForCheck, editor)
            && editor.EditorForCheck.Text == buffer && editor.IsDirty;
        // A context switch cannot bind a document to another root or throw away its document and undo stack.
        var alternateRoot = Path.Combine(root, "other-workspace");
        Directory.CreateDirectory(alternateRoot);
        view.SetContext(alternateRoot, () => new WorkspaceToolScope(alternateRoot, new WorkspaceGuards(null, []), null, "workspace-check", [], null, null));
        view.SetContext(root, scopeFor);
        var retained = ReferenceEquals(view.SelectedContentForCheck, editor) && editor.EditorForCheck.Text == buffer
            && !view.TreeVisibleForCheck;
        File.WriteAllText(path, "// External conflicting change\n");
        var conflict = !editor.Save() && editor.IsDirty && editor.EditorForCheck.Text == buffer;
        view.CloseSelected(discard: true);
        view.ShowFiles();
        view.CloseToolForCheck("plan");
        view.CloseToolForCheck("repo");
        view.SelectExisting(original);
        view.CloseSelected(discard: true);
        shell.UpdateLayout();
        var independent = view.TreeVisibleForCheck && !view.DocumentSelectedForCheck;
        SaveWorkspaceRender(shell, root, "workspace-empty-tree.png");
        tree.CloseButtonForCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SaveWorkspaceRender(shell, root, "workspace-empty.png");
        var empty = !view.TreeVisibleForCheck && view.SelectedContentForCheck is StackPanel;
        // Restore the caller's original fixture, so subsequent shell geometry checks retain their preconditions.
        shell.ShowWorkspaceDocument(original, "src/main.cpp", originalContent);
        view.OpenTool("plan");
        view.OpenTool("repo");
        view.SelectExisting(original);
        Console.WriteLine($"workspace documents: close={closed}, respected={respectedClose}, state={restoredState}, entry={idempotentEntry}, dynamic={dynamic}, retained={retained}, conflict={conflict}");
        Console.WriteLine($"workspace layout: tabs={tabs}, drawer={drawer}, wide={wide}, fills={fills}, restored={restoredLayout}, independent={independent}, empty={empty}");
        return (closed && respectedClose && restoredState && idempotentEntry && dynamic && retained && conflict,
            tabs && drawer && wide && fills && restoredLayout && independent && empty, commands);
    }

    private static void SaveWorkspaceRender(MainWindow shell, string root, string name)
    {
        shell.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)shell.Bounds.Width, (int)shell.Bounds.Height));
        bitmap.Render(shell);
        using var stream = File.Create(Path.Combine(root, name));
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }
}
