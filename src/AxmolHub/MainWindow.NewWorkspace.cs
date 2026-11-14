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
        _newWorkspace = new NewWorkspaceView(this, _chat, _workspace.State.Projects,
            NavigationViews.Normalize(_preferences.NavigationView) == NavigationViews.WorkbenchPlus, CloseNewWorkspace);
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
