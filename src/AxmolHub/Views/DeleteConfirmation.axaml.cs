using Avalonia.Controls;
using Avalonia.Input;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>One confirmation surface for destructive shell actions. A null answer always means cancel.</summary>
public partial class DeleteConfirmation : UserControl
{
    internal event Action<bool?>? Answered;

    public DeleteConfirmation()
    {
        InitializeComponent();
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        CloseButton.Click += (_, _) => Cancel();
        CancelButton.Click += (_, _) => Cancel();
        ConfirmButton.Click += (_, _) => Answered?.Invoke(UseRecycleBin.IsChecked == true);
        UseRecycleBin.IsCheckedChanged += (_, _) => UpdatePolicy();
        UpdatePolicy();
    }

    private void UpdatePolicy() => PolicyMessage.Text = HubStrings.Get(
        UseRecycleBin.IsChecked == true ? "RecycleBinPolicy" : "PermanentDeletePolicy");

    internal static string FailureReason(FileOperationResult result) => result.Kind == FileOperationResultKind.Unsupported
        ? HubStrings.Get("RecycleBinUnavailable") + "\n" + result.Error
        : result.Error ?? HubStrings.Get("OperationFailed");

    internal void Cancel() => Answered?.Invoke(null);
    internal void FocusCancel() => CancelButton.Focus();

    public static Task<bool?> ShowAsync(Window? owner, string title, string name, string? path, string message,
        string? actionCaption = null)
    {
        // Destructive actions need a visible shell. Losing the owner must never become consent.
        if (owner is not MainWindow { IsVisible: true } shell) return Task.FromResult<bool?>(null);
        var view = new DeleteConfirmation();
        view.Heading.Text = title;
        view.ConfirmButton.Content = actionCaption ?? title;
        view.ObjectName.Text = name;
        view.ObjectPath.Text = path;
        view.ObjectPath.IsVisible = !string.IsNullOrEmpty(path);
        view.Message.Text = message;
        return shell.ShowDeleteConfirmationAsync(view);
    }
}
