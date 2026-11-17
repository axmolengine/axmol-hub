using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

public partial class MainWindow
{
    private NewWorkspaceView? _newWorkspace;
    private Control? _workspacePickerFocus;
    private bool _pickingWorkspaceFolder;

    private async Task OpenWorkspaceFolderAsync()
    {
        if (_pickingWorkspaceFolder) return;
        _pickingWorkspaceFolder = true;
        CloseNewWorkspace();
        CloseSettings();
        var chat = _chat;
        try
        {
            var result = await Pickers.PickFolderAsync(this, HubStrings.Get("GroupNewWorkspaceTip"));
            // A data-root switch or window close can invalidate the destination while the native picker is up.
            if (!IsVisible || !ReferenceEquals(chat, _chat) || result.Outcome == PickOutcome.Cancelled) return;
            string? error = result.Outcome == PickOutcome.NotLocal ? HubStrings.Get("ChatFolderNotLocal")
                : result.Path is not { Length: > 0 } ? HubStrings.Get("LocalPathRequired")
                : OpenWorkspaceFolder(result.Path) is { } verdict ? WorkspaceRejectionMessage(verdict, result.Path)
                : null;
            if (error is not null) ShowFeedback(UserFeedback.Warning(error, "open-workspace"));
        }
        catch (Exception ex)
        {
            ShowFeedback(UserFeedback.Warning(ex.Message, "open-workspace"));
        }
        finally { _pickingWorkspaceFolder = false; }
    }

    internal WorkspacePathVerdict? OpenWorkspaceFolder(string path)
    {
        var (_, verdict) = _chat.OpenWorkspaceConversation(path);
        if (verdict is not null) return verdict;
        CloseNewWorkspace();
        CloseSettings();
        NavigateTo("Assistant");
        BuildWorkspaceContent();
        ShowWorkspace();
        _workspaceView?.ShowFiles();
        return null;
    }

    private void RegisterCreatedWorkspace(ProjectEntry project)
    {
        try
        {
            if (_chat.AddWorkspace(project.Path) is { } verdict)
            {
                ShowFeedback(UserFeedback.Warning(string.Format(HubStrings.Get("ProjectWorkspaceFailed"),
                    WorkspaceRejectionMessage(verdict, project.Path)), "project-workspace"));
                return;
            }
            SetStatus(HubStrings.Get("ProjectWorkspaceCreated"));
        }
        catch (Exception ex)
        {
            ShowFeedback(UserFeedback.Warning(string.Format(HubStrings.Get("ProjectWorkspaceFailed"), ex.Message),
                "project-workspace"));
        }
    }

    internal static string WorkspaceRejectionMessage(WorkspacePathVerdict verdict, string path) => verdict switch
    {
        WorkspacePathVerdict.ProtectedRoot => HubStrings.Get("ChatWorkspaceProtected"),
        WorkspacePathVerdict.OutsideRoots => HubStrings.Get("ChatWorkspaceOutside"),
        _ => HubStrings.Get("ChatWorkspaceRejected") + path,
    };

    private void InitializeNewWorkspace()
    {
        NewWorkspaceScrim.PointerPressed += (_, e) => { CloseNewWorkspace(); e.Handled = true; };
        NewWorkspaceOverlayHost.SizeChanged += (_, _) => SyncNewWorkspaceSize();
        AddHandler(KeyDownEvent, OnNewWorkspaceKeyDown, RoutingStrategies.Tunnel);
    }

    private void SyncNewWorkspaceSize()
    {
        NewWorkspaceCard.Width = Math.Min(600, Math.Max(0, NewWorkspaceOverlayHost.Bounds.Width - 48));
        NewWorkspaceCard.Height = Math.Min(_newWorkspace?.PreferredHeight ?? 244,
            Math.Max(0, NewWorkspaceOverlayHost.Bounds.Height - 28));
    }

    private void OpenNewWorkspace()
    {
        if (NewWorkspaceOverlayHost.IsVisible) return;
        CloseSettings();
        _workspacePickerFocus = FocusManager?.GetFocusedElement() as Control;
        _newWorkspace = new NewWorkspaceView(_chat, _workspace.State.Projects,
            OpenWorkspaceFolder, CloseNewWorkspace);
        _newWorkspace.LayoutChanged += SyncNewWorkspaceSize;
        NewWorkspaceHost.Content = _newWorkspace;
        NewWorkspaceOverlayHost.IsVisible = true;
        SyncNewWorkspaceSize();
        SyncWorkspaceBrowserOverlay();
        Dispatcher.UIThread.Post(() => _newWorkspace?.FocusInitialControl(), DispatcherPriority.Loaded);
    }

    private void CloseNewWorkspace()
    {
        if (!NewWorkspaceOverlayHost.IsVisible) return;
        NewWorkspaceOverlayHost.IsVisible = false;
        NewWorkspaceHost.Content = null;
        _newWorkspace = null;
        SyncWorkspaceBrowserOverlay();
        _workspacePickerFocus?.Focus();
        _workspacePickerFocus = null;
    }

    private void OnNewWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (!NewWorkspaceOverlayHost.IsVisible) return;
        if (e.Key == Key.Escape) { CloseNewWorkspace(); e.Handled = true; }
        else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() is not Button)
        {
            _newWorkspace?.SubmitSelection();
            e.Handled = true;
        }
    }
}
