using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

public partial class MainWindow
{
    private WorkspaceView? _workspaceView;

    internal bool WorkspaceHasDirtyBuffers => _workspaceView?.HasDirtyBuffers == true;
    internal bool WorkspaceDocumentVisibleForCheck => _workspaceView?.DocumentSelectedForCheck == true;
    internal string WorkspaceDocumentTitleForCheck => _workspaceView?.SelectedTitleForCheck ?? "";
    internal Control? WorkspaceDocumentContentForCheck => _workspaceView?.SelectedContentForCheck;
    internal WorkspaceView? WorkspaceViewForCheck => _workspaceView;

    private Control? BuildWorkspaceContent()
    {
        if (_chatPanel?.BuildInspectorContent(_inspectorTab, _inspectorTurn) is not InspectorPanel inspector) return null;
        if (_workspaceView is null)
        {
            _workspaceView = new WorkspaceView(this, inspector, WriteLog);
            _workspaceView.ToolRequested += tab => _inspectorTab = tab;
            _workspaceView.FileRequested += path =>
            {
                var id = _chat.ActiveConversation?.Id ?? "";
                var root = _chat.ActiveWorkspaceRoot;
                var error = CodeFileWindow.ShowEditor(this, path, () => _chat.EditorScopeFor(id, root), RefreshInspector);
                if (error is not null) _ = HubDialog.ShowAsync(this, HubStrings.Get("WorkspaceFiles"), HubStrings.Get(error));
            };
            _workspaceView.OpenRequested += ShowWorkspace;
            SettingsOverlayHost.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) SyncWorkspaceBrowserOverlay(); };
            PictureViewerHost.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) SyncWorkspaceBrowserOverlay(); };
            SyncWorkspaceBrowserOverlay();
            Closed += (_, _) => _workspaceView.Dispose();
            Closing += async (_, e) =>
            {
                var ready = _workspaceView.BrowsersReady;
                if (e.Cancel || ready.IsCompleted) return;
                e.Cancel = true;
                await ready;
                Close();
            };
        }
        var conversationId = _chat.ActiveConversation?.Id ?? "";
        var root = _chat.ActiveWorkspaceRoot;
        _workspaceView.SetContext(root, () => _chat.EditorScopeFor(conversationId, root));
        return _workspaceView;
    }

    private void SyncWorkspaceBrowserOverlay()
        => _workspaceView?.SetBrowserObscured(SettingsOverlayHost.IsVisible || PictureViewerHost.IsVisible);

    private void ShowWorkspace()
    {
        _inspectorOpen = true;
        SetInspectorPane(_workspaceView);
        SetInspectorColumnVisible();
        _workspaceView?.RefreshLayout();
        if (!_preferences.InspectorOpen)
        {
            _preferences.InspectorOpen = true;
            _preferencesStore.Save(_preferences);
        }
    }

    internal bool SelectWorkspaceDocument(string path)
    {
        BuildWorkspaceContent();
        return _workspaceView?.SelectExisting(path) == true;
    }

    internal void ShowWorkspaceDocument(string path, string title, Control content, Func<bool>? isDirty = null)
    {
        BuildWorkspaceContent();
        _workspaceView?.OpenDocument(path, title, content, isDirty);
    }

    internal void CloseWorkspaceDocument(bool discard = false) => _workspaceView?.CloseSelected(discard);
    internal bool SaveWorkspaceBuffers() => _workspaceView?.SaveAll() != false;
    internal void DiscardWorkspaceBuffers() => _workspaceView?.DiscardAll();
}
