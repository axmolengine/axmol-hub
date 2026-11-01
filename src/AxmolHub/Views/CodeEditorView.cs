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

    internal ContextMenu CreateTabMenu(Action close, Action? reveal = null)
    {
        var menu = new ContextMenu();
        var save = new MenuItem { Tag = "save", InputGesture = new KeyGesture(Key.S,
            OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) };
        save.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension("CodeSave"));
        save.Click += (_, _) => { if (!Save()) reveal?.Invoke(); };
        var reload = new MenuItem { Tag = "reload" };
        reload.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension("CodeReload"));
        reload.Click += async (_, _) => { reveal?.Invoke(); await ReloadAsync(); };
        var closeItem = new MenuItem { Tag = "close" };
        closeItem.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension("WorkspaceCloseTab"));
        closeItem.Click += (_, _) => close();
        menu.Items.Add(save);
        menu.Items.Add(reload);
        menu.Items.Add(new Separator());
        FileRevealMenu.Add(menu, _owner, () => _canRead() ? _path : null);
        menu.Items.Add(closeItem);
        menu.Opening += (_, _) => save.IsEnabled = CanSave;
        return menu;
    }

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
    internal TextEditor EditorForCheck => _editor;
}
