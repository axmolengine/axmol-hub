using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed class BuildCleanupChoice : StackPanel
{
    private readonly Func<BuildCleanup> _resolve;
    private readonly CheckBox _check = new() { Name = "ClearBuildDirectories", Content = HubStrings.Get("CleanBeforeBuild") };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    public BuildCleanup? Cleanup { get; private set; }
    public bool CanContinue => _check.IsChecked != true || Cleanup is not null;
    public event Action? Changed;

    public BuildCleanupChoice(Func<BuildCleanup> resolve)
    {
        _resolve = resolve;
        Margin = new Thickness(0, 8, 0, 8);
        Children.Add(_check);
        Children.Add(_details);
        _details.IsVisible = false;
        _check.IsCheckedChanged += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Cleanup = null;
        _details.IsVisible = _check.IsChecked == true;
        if (_details.IsVisible)
        {
            try
            {
                Cleanup = _resolve();
                _details.Text = HubStrings.Get("CleanBeforeBuildHint") + "\n" + string.Join("\n", Cleanup.Directories);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _details.Text = ex.Message;
            }
        }
        Changed?.Invoke();
    }
}
