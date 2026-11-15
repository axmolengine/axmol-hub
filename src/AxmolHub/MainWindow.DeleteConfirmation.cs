using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AxmolHub;

public partial class MainWindow
{
    internal Task<bool?> ShowDeleteConfirmationAsync(DeleteConfirmation view)
    {
        if (DeleteOverlayHost.IsVisible) return Task.FromResult<bool?>(null);
        CloseSettings();
        CloseNewWorkspace();
        var focus = FocusManager?.GetFocusedElement() as Control;
        var completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DeleteConfirmationHost.Content = view;
        DeleteOverlayHost.IsVisible = true;
        SyncWorkspaceBrowserOverlay();
        view.Answered += Finish;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closed += OnClosed;
        Dispatcher.UIThread.Post(view.FocusCancel, DispatcherPriority.Loaded);
        return completion.Task;

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Finish(null);
        }

        void OnClosed(object? sender, EventArgs e) => Finish(null);

        void Finish(bool? answer)
        {
            if (completion.Task.IsCompleted) return;
            view.Answered -= Finish;
            RemoveHandler(KeyDownEvent, OnKeyDown);
            Closed -= OnClosed;
            DeleteOverlayHost.IsVisible = false;
            DeleteConfirmationHost.Content = null;
            SyncWorkspaceBrowserOverlay();
            focus?.Focus();
            completion.TrySetResult(answer);
        }
    }
}
