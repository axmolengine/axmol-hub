using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AxmolHub.Core;
using AvaloniaEdit;

namespace AxmolHub;

/// <summary>A persistent single-file buffer. Tab/layout changes never recreate its document or undo stack.
/// Authorization and atomic replacement remain with the session-aware caller.</summary>
internal sealed class CodeEditorView : Grid
{
    private readonly TextEditor _editor;
    private readonly string _path;
    private TextFileSnapshot _baseline;
    private readonly Func<TextFileSnapshot, string, TextReplaceResult> _save;
    private readonly Func<bool> _canRead;
    private readonly TextBlock _status;
    private readonly Window? _owner;
    private string? _lastObservedIdentity;
    private bool _saving;
    internal event Action? StateChanged;

    internal CodeEditorView(string path, TextFileSnapshot baseline,
        Func<TextFileSnapshot, string, TextReplaceResult> save, bool writable, Window? owner, Func<bool>? canRead = null)
    {
        _path = Path.GetFullPath(path);
        _owner = owner;
        _baseline = baseline;
        _lastObservedIdentity = baseline.Identity;
        _save = save;
        _canRead = canRead ?? (() => true);
        RowDefinitions = new RowDefinitions("*,Auto");
        _editor = CodeSurface.ForFile(path, TextEol.ToLf(baseline.Text));
        _editor.IsReadOnly = !writable;
        _editor.ShowLineNumbers = true;
        _editor.Name = "WorkspaceTextEditor";
        _editor.ContextMenu = CreateTextMenu();
        _editor.TextChanged += (_, _) => UpdateStatus();
        _editor.GotFocus += (_, _) => ObserveDisk();
        _editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key != Key.S || e.KeyModifiers != modifier) return;
            Save();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _status = new TextBlock { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Children.Add(_editor);
        Grid.SetRow(_status, 1);
        Children.Add(_status);
        UpdateStatus();
    }

    internal bool IsDirty => !string.Equals(_editor.Text, TextEol.ToLf(_baseline.Text), StringComparison.Ordinal);

    internal bool CanSave => IsDirty && !_editor.IsReadOnly && !_saving;

    private ContextMenu CreateTextMenu()
    {
        var menu = new ContextMenu();
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        MenuItem Add(string tag, string caption, Action action, KeyGesture? gesture = null)
        {
            var item = new MenuItem { Tag = tag, InputGesture = gesture };
            item.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension(caption));
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        var undo = Add("undo", "CodeUndo", () => _editor.Undo(), new KeyGesture(Key.Z, modifier));
        var redo = Add("redo", "CodeRedo", () => _editor.Redo(), OperatingSystem.IsMacOS()
            ? new KeyGesture(Key.Z, modifier | KeyModifiers.Shift) : new KeyGesture(Key.Y, modifier));
        menu.Items.Add(new Separator());
        var cut = Add("cut", "CutText", _editor.Cut, new KeyGesture(Key.X, modifier));
        var copy = Add("copy", "CopyMessage", _editor.Copy, new KeyGesture(Key.C, modifier));
        var paste = Add("paste", "PasteText", _editor.Paste, new KeyGesture(Key.V, modifier));
        var selectAll = Add("selectAll", "CodeSelectAll", _editor.SelectAll, new KeyGesture(Key.A, modifier));
        menu.Items.Add(new Separator());
        var save = Add("save", "CodeSave", () => Save(), new KeyGesture(Key.S, modifier));
        var reload = Add("reload", "CodeReload", () => _ = ReloadAsync());
        menu.Items.Add(new Separator());
        AddFileReveal(menu);
        menu.Opened += (_, _) =>
        {
            undo.IsEnabled = !_editor.IsReadOnly && _editor.CanUndo;
            redo.IsEnabled = !_editor.IsReadOnly && _editor.CanRedo;
            cut.IsEnabled = !_editor.TextArea.Selection.IsEmpty && _editor.CanCut;
            copy.IsEnabled = !_editor.TextArea.Selection.IsEmpty && _editor.CanCopy;
            paste.IsEnabled = !_editor.IsReadOnly && _editor.CanPaste;
            selectAll.IsEnabled = _editor.Document.TextLength > 0;
            save.IsEnabled = CanSave;
            reload.IsEnabled = !_saving && _canRead();
        };
        return menu;
    }

    internal void AddFileReveal(ContextMenu menu)
        => FileRevealMenu.Add(menu, _owner, () => _canRead() ? _path : null);

    internal bool Save()
    {
        if (!IsDirty) return true;
        if (_saving || _editor.IsReadOnly) return false;
        _saving = true;
        try
        {
            var result = _save(_baseline, _editor.Text);
            if (result.Verdict is TextReplaceVerdict.Written or TextReplaceVerdict.Unchanged)
            {
                var refreshed = TextFile.Read(_path, TextFile.MaxEditorBytes);
                if (refreshed.State == TextFileState.Text && TextEol.ToLf(refreshed.Text) == _editor.Text)
                {
                    _baseline = refreshed;
                    _lastObservedIdentity = refreshed.Identity;
                    UpdateStatus();
                    return true;
                }
                SetStatus(HubStrings.Get("CodeDiskConflict"));
            }
            else SetStatus(result.Verdict switch
            {
                TextReplaceVerdict.ChangedSince => HubStrings.Get("CodeDiskConflict"),
                TextReplaceVerdict.RefusedPath => HubStrings.Get("CodeWriteRefused"),
                _ => HubStrings.Get("CodeSaveFailed") + (result.Detail ?? result.Verdict.ToString()),
            });
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus(HubStrings.Get("CodeSaveFailed") + ex.Message);
            return false;
        }
        finally { _saving = false; }
    }

    internal async Task ReloadAsync()
    {
        if (IsDirty && await HubDialog.ShowAsync(_owner, HubStrings.Get("CodeReload"),
                HubStrings.Get("CodeReloadConfirm"), HubDialogButtons.OkCancel, danger: true) != HubDialogResult.Ok) return;
        if (!_canRead()) { SetStatus(HubStrings.Get("CodeUnreadable")); return; }
        var current = TextFile.Read(_path, TextFile.MaxEditorBytes);
        if (current.State != TextFileState.Text)
        {
            SetStatus(current.Detail ?? HubStrings.Get("CodeUnreadable"));
            return;
        }
        _baseline = current;
        _lastObservedIdentity = current.Identity;
        _editor.Text = TextEol.ToLf(current.Text);
        UpdateStatus();
    }

    private void ObserveDisk()
    {
        if (!_canRead()) { SetStatus(HubStrings.Get("CodeUnreadable")); return; }
        var current = TextFile.Read(_path, TextFile.MaxEditorBytes);
        if (current.State != TextFileState.Text || current.Identity == _baseline.Identity
            || current.Identity == _lastObservedIdentity) return;
        _lastObservedIdentity = current.Identity;
        if (IsDirty) { SetStatus(HubStrings.Get("CodeDiskConflict")); return; }
        _baseline = current;
        _editor.Text = TextEol.ToLf(current.Text);
        UpdateStatus();
        SetStatus(HubStrings.Get("CodeDiskReloaded"));
    }

    private void UpdateStatus()
    {
        if (_status is null) return;
        SetStatus(_editor.IsReadOnly ? HubStrings.Get("CodeReadOnly") : "");
        StateChanged?.Invoke();
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = !string.IsNullOrEmpty(text);
    }
    internal void GoToPosition(int? line, int? column)
    {
        if (line is not > 0) return;
        var lineNumber = Math.Min(line.Value, _editor.Document.LineCount);
        var documentLine = _editor.Document.GetLineByNumber(lineNumber);
        var columnOffset = Math.Clamp((column ?? 1) - 1, 0, documentLine.Length);
        _editor.CaretOffset = documentLine.Offset + columnOffset;
        _editor.TextArea.Caret.BringCaretToView();
        _editor.Focus();
    }

    internal TextEditor EditorForCheck => _editor;
}
