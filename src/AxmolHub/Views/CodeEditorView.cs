using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;
using AvaloniaEdit;

namespace AxmolHub;

/// <summary>A single-file editing buffer. It owns the UI save point but delegates authorization and replacement
/// to the session-aware caller; a manual save never becomes an AI change or an undo-journal entry.</summary>
internal sealed class CodeEditorView : Grid
{
    private readonly TextEditor _editor;
    private readonly string _path;
    private TextFileSnapshot _baseline;
    private readonly Func<TextFileSnapshot, string, TextReplaceResult> _save;
    private readonly TextBlock _status;
    private readonly Button _discardButton;
    private readonly Window? _owner;
    private string? _lastObservedIdentity;
    private bool _saving;

    internal CodeEditorView(string path, TextFileSnapshot baseline,
        Func<TextFileSnapshot, string, TextReplaceResult> save, bool writable, Action discardAndClose, Window? owner)
    {
        _path = Path.GetFullPath(path);
        _owner = owner;
        _baseline = baseline;
        _lastObservedIdentity = baseline.Identity;
        _save = save;
        RowDefinitions = new RowDefinitions("Auto,*");
        _editor = CodeSurface.ForFile(path, baseline.Text ?? "");
        _editor.IsReadOnly = !writable;
        _editor.ShowLineNumbers = true;
        _editor.TextChanged += (_, _) => UpdateStatus();
        _editor.GotFocus += (_, _) => ObserveDisk();

        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(8) };
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(_status, Dock.Left);
        bar.Children.Add(_status);
        var saveButton = new Button { Content = "Save", IsEnabled = writable, Classes = { "compact" }, Margin = new Thickness(8, 0, 0, 0) };
        saveButton.Click += (_, _) => Save();
        DockPanel.SetDock(saveButton, Dock.Right);
        bar.Children.Add(saveButton);
        var reload = new Button { Content = "Reload", IsEnabled = writable, Classes = { "compact" }, Margin = new Thickness(8, 0, 0, 0) };
        reload.Click += async (_, _) => await ReloadAsync();
        DockPanel.SetDock(reload, Dock.Right);
        bar.Children.Add(reload);
        _discardButton = new Button { Content = "Discard and close", IsVisible = false, Classes = { "compact" }, Margin = new Thickness(8, 0, 0, 0) };
        _discardButton.Click += (_, _) => discardAndClose();
        DockPanel.SetDock(_discardButton, Dock.Right);
        bar.Children.Add(_discardButton);
        Children.Add(bar);
        Grid.SetRow(_editor, 1);
        Children.Add(_editor);
        UpdateStatus();
    }

    internal bool IsDirty => !string.Equals(_editor.Text, _baseline.Text, StringComparison.Ordinal);

    private void Save()
    {
        if (_saving || !IsDirty || _editor.IsReadOnly) return;
        _saving = true;
        try
        {
            TextReplaceResult result;
            try { result = _save(_baseline, _editor.Text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _status.Text = "Save failed; your buffer is preserved: " + ex.Message;
                return;
            }
            _status.Text = result.Verdict switch
            {
                TextReplaceVerdict.Written => "Saved. Reopen to continue editing from the new disk version.",
                TextReplaceVerdict.Unchanged => "No changes to save.",
                TextReplaceVerdict.ChangedSince => "The file changed on disk. Your buffer is preserved; compare or reload.",
                TextReplaceVerdict.RefusedPath => "Write permission or path changed. Your buffer is preserved.",
                _ => result.Detail ?? "Save failed. Your buffer is preserved.",
            };
            if (result.Verdict is TextReplaceVerdict.Written or TextReplaceVerdict.Unchanged)
            {
                var refreshed = TextFile.Read(_path, TextFile.MaxEditorBytes);
                if (refreshed.State == TextFileState.Text)
                {
                    _baseline = refreshed;
                    _lastObservedIdentity = refreshed.Identity;
                    UpdateStatus();
                }
            }
        }
        finally { _saving = false; }
    }

    private async Task ReloadAsync()
    {
        if (IsDirty && await HubDialog.ShowAsync(_owner, "Reload file",
                "Reloading discards the current unsaved buffer. Continue?", HubDialogButtons.OkCancel, danger: true)
            != HubDialogResult.Ok) return;
        var current = TextFile.Read(_path, TextFile.MaxEditorBytes);
        if (current.State != TextFileState.Text)
        {
            _status.Text = current.Detail ?? "The file is no longer readable as supported text.";
            return;
        }
        _baseline = current;
        _lastObservedIdentity = current.Identity;
        _editor.Text = current.Text;
        UpdateStatus();
    }

    private void ObserveDisk()
    {
        var current = TextFile.Read(_path, TextFile.MaxEditorBytes);
        if (current.State != TextFileState.Text || current.Identity == _baseline.Identity
            || current.Identity == _lastObservedIdentity) return;
        _lastObservedIdentity = current.Identity;
        if (IsDirty)
        {
            _status.Text = "File changed on disk; your unsaved buffer is preserved. Compare or reload explicitly.";
            return;
        }
        _baseline = current;
        _editor.Text = current.Text;
        UpdateStatus();
        _status.Text = "File changed on disk and was reloaded.";
    }

    private void UpdateStatus()
    {
        if (_status is null) return;
        _status.Text = _editor.IsReadOnly
            ? "Read-only"
            : IsDirty ? "Unsaved changes · closing is blocked until saved or discarded" : "No unsaved changes";
        _discardButton.IsVisible = IsDirty;
    }
}
