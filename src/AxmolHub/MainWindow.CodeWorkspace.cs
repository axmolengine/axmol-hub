using Avalonia.Controls;
using Avalonia.Threading;
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
            inspector.GitTitleResizeHandle = () => _workspaceView.VisibleTreeSplitter
                ?? (InspectorResizeGrip.IsEffectivelyVisible ? InspectorResizeGrip : null);
            _workspaceView.ToolRequested += tab => _inspectorTab = tab;
            _workspaceView.FileRequested += path =>
            {
                var id = _chat.ActiveConversation?.Id ?? "";
                var root = _chat.ActiveWorkspaceRoot;
                var error = CodeFileWindow.ShowEditor(this, path, () => _chat.EditorScopeFor(id, root), RefreshInspector);
                if (error is not null) _ = HubDialog.ShowAsync(this, HubStrings.Get("WorkspaceFiles"), HubStrings.Get(error));
            };
            _workspaceView.OpenRequested += ShowWorkspace;
            _workspaceView.OpenFolderRequested += () => _ = OpenWorkspaceFolderAsync();
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
        => _workspaceView?.SetBrowserObscured(SettingsOverlayHost.IsVisible || PictureViewerHost.IsVisible || NewWorkspaceOverlayHost.IsVisible || DeleteOverlayHost.IsVisible);

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

    internal string? OpenWorkspaceFile(string path)
    {
        var id = _chat.ActiveConversation?.Id ?? "";
        var root = _chat.ActiveWorkspaceRoot;
        return CodeFileWindow.ShowEditor(this, path, () => _chat.EditorScopeFor(id, root), RefreshInspector);
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

    internal BrowserBridge CreateWorkspaceBrowserBridge() => new(
        OutboundAllowed: () => _preferences.AllowOutboundWebFetch,
        PeekTabUrl: (workspaceRoot, tabId) => _workspaceView?.PeekBrowserUrl(workspaceRoot, tabId),
        ListTabs: (workspaceRoot, ct) => _workspaceView is { } view
            ? view.ListBrowsersAsync(workspaceRoot, ct)
            : Task.FromResult<IReadOnlyList<BrowserTabSummary>>([]),
        Open: (workspaceRoot, request, ct) =>
        {
            Task<BrowserOpenResult> Run()
            {
                BuildWorkspaceContent();
                if (_workspaceView is not { } view)
                    return Task.FromResult(new BrowserOpenResult(false, null, null, null,
                        "The workspace browser view is unavailable."));
                var conversationId = _chat.ActiveConversation?.Id ?? "";
                return view.OpenBrowserForAssistantAsync(
                    workspaceRoot,
                    request,
                    () => _chat.EditorScopeFor(conversationId, workspaceRoot),
                    ShowWorkspace,
                    ct);
            }
            return Dispatcher.UIThread.CheckAccess() ? Run() : Dispatcher.UIThread.InvokeAsync(Run);
        },
        Read: (workspaceRoot, request, ct) => _workspaceView is { } view
            ? view.ReadBrowserForAssistantAsync(workspaceRoot, request, ct)
            : Task.FromResult(new BrowserReadResult(false, null, null, null, false, null, null, false, null, null,
                "No embedded browser tab is open in this workspace. Call browser_open first.")),
        Act: (workspaceRoot, request, ct) => _workspaceView is { } view
            ? view.ActBrowserForAssistantAsync(workspaceRoot, request, ct)
            : Task.FromResult(new BrowserActResult(false, null, null, null, null,
                "No embedded browser tab is open in this workspace. Call browser_open first.")),
        Capture: (workspaceRoot, request, ct) => _workspaceView is { } view
            ? view.CaptureBrowserForAssistantAsync(workspaceRoot, request, ShowWorkspace, ct)
            : Task.FromResult(new BrowserScreenshotResult(false, null, null, null, null,
                "No embedded browser tab is open in this workspace. Call browser_open first.")));
}
