using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>The single authorized file-opening entry. The main shell hosts document tabs; a standalone owner
/// uses a window with the same editor and save pipeline. No unguarded read-only opening road remains.</summary>
public sealed class CodeFileWindow : Window
{
    private static readonly HashSet<CodeFileWindow> OpenWindows = [];
    private readonly CodeEditorView _editor;
    private bool _discardClose;
    private bool _confirmingClose;

    private CodeFileWindow(string path, CodeEditorView editor)
    {
        _editor = editor;
        OpenWindows.Add(this);
        Closed += (_, _) => OpenWindows.Remove(this);
        Title = Path.GetFileName(path);
        Width = 980;
        Height = 680;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new HubDialogChrome(this, "CloseWindow", Close).Wrap(editor);
        Closing += async (_, e) =>
        {
            if (!editor.IsDirty || _discardClose) return;
            e.Cancel = true;
            if (_confirmingClose) return;
            _confirmingClose = true;
            try
            {
                var answer = await HubDialog.ShowAsync(this, HubStrings.Get("CodeUnsavedTitle"),
                    path + "\n\n" + HubStrings.Get("CodeUnsavedClose"), HubDialogButtons.SaveDiscardCancel);
                if (answer == HubDialogResult.Cancel || answer == HubDialogResult.Save && !editor.Save()) return;
                _discardClose = true;
                Close();
            }
            finally { _confirmingClose = false; }
        };
    }

    internal static string? ShowEditor(Window? owner, string relativePath,
        Func<WorkspaceToolScope> scopeFor, Action? saved = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "InspectorOpenFileUnavailable";
        var initialScope = scopeFor();
        var read = WorkspacePaths.ResolveRead(initialScope.Sandbox, relativePath, initialScope.Guards);
        if (!read.IsAllowed) return "InspectorOpenFileUnavailable";
        var shell = owner as MainWindow;
        if (shell?.SelectWorkspaceDocument(read.Full) == true) return null;
        var baseline = TextFile.Read(read.Full, TextFile.MaxEditorBytes);
        if (baseline.State != TextFileState.Text) return "InspectorOpenFileUnavailable";
        var write = WorkspacePaths.ResolveWrite(initialScope.Sandbox, read.Full, initialScope.Guards);
        var writable = write.IsAllowed && WorkspacePaths.IsWritableText(Path.GetFileName(read.Full));
        var editor = new CodeEditorView(read.Full, baseline, (snapshot, text) =>
        {
            using var lease = TextFile.AcquireWrite(read.Full);
            var result = lease.Save(() =>
            {
                var currentScope = scopeFor();
                var currentPath = WorkspacePaths.ResolveWrite(currentScope.Sandbox, read.Full, currentScope.Guards);
                return currentPath.IsAllowed && WorkspaceFileTree.WorkspacePathComparer.Equals(currentPath.Full, read.Full)
                    ? currentPath
                    : WorkspacePath.Refuse(currentPath.IsAllowed ? WorkspacePathVerdict.ReparsePoint : currentPath.Verdict, read.Full);
            }, snapshot, text);
            if (result.Verdict == TextReplaceVerdict.Written) saved?.Invoke();
            return result;
        }, writable, owner, () =>
        {
            var current = scopeFor();
            return WorkspacePaths.ResolveRead(current.Sandbox, read.Full, current.Guards).IsAllowed;
        });
        if (shell is not null)
        {
            shell.ShowWorkspaceDocument(read.Full, Path.GetFileName(read.Full), editor, () => editor.IsDirty);
            return null;
        }
        var window = new CodeFileWindow(read.Full, editor);
        if (owner is null) window.Show(); else window.Show(owner);
        return null;
    }

    internal static async Task<bool> ConfirmCloseDirtyAsync(Window owner)
    {
        var dirty = OpenWindows.Where(window => window._editor.IsDirty).ToArray();
        var shell = owner as MainWindow;
        if (dirty.Length == 0 && shell?.WorkspaceHasDirtyBuffers != true && ChangesEditSession.DirtyCount == 0) return true;
        var answer = await HubDialog.ShowAsync(owner, HubStrings.Get("CodeUnsavedTitle"),
            HubStrings.Get("CodeUnsavedQuit"), HubDialogButtons.SaveDiscardCancel);
        if (answer == HubDialogResult.Cancel) return false;
        if (answer == HubDialogResult.Save)
        {
            foreach (var window in dirty) if (!window._editor.Save()) return false;
            if (shell?.SaveWorkspaceBuffers() == false) return false;
            if (ChangesEditSession.SaveAll() is { } error)
            {
                await HubDialog.ShowAsync(owner, HubStrings.Get("CodeUnsavedTitle"), HubStrings.Get("CodeSaveFailed") + error);
                return false;
            }
        }
        else
        {
            ChangesEditSession.DiscardAll();
            shell?.DiscardWorkspaceBuffers();
        }
        foreach (var window in dirty) { window._discardClose = true; window.Close(); }
        return true;
    }
}
