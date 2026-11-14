using Avalonia.Controls;
using Avalonia.Layout;
using AxmolHub.Core;

namespace AxmolHub;

public partial class ChatPanel
{
    private string _computerControlIdentity = "";

    private void RefreshComputerControlHost()
    {
        var id = _chat?.ActiveConversation?.Id;
        var control = id is null ? null : _chat?.ComputerControlFor(id);
        if (control is null)
        {
            ComputerControlHost.IsVisible = false;
            ComputerControlHost.Child = null;
            _computerControlIdentity = "";
            return;
        }
        var identity = id + ":" + control.Version + ":" + HubStrings.Get("ComputerControlPause");
        if (_computerControlIdentity == identity) return;
        _computerControlIdentity = identity;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var label = new TextBlock
        {
            Text = string.Format(HubStrings.Get(control.Paused ? "ComputerControlPaused" : "ComputerControlActive"),
                System.IO.Path.GetFileName(control.Target.Application.ExecutablePath)),
            Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(label, control.Target.Application.ExecutablePath);
        grid.Children.Add(label);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(CapsuleActionButton(control.Paused ? "Hub.Icon.Play" : "Hub.Icon.Pause",
            control.Paused ? "ComputerControlResume" : "ComputerControlPause",
            () => _chat?.PauseComputerControl(id!, !control.Paused)));
        actions.Children.Add(CapsuleActionButton("Hub.Icon.Stop", "ComputerControlStop",
            () => _chat?.RequestStop(id!)));
        actions.Children.Add(CapsuleActionButton("Hub.Icon.Close", "ComputerControlRevoke",
            () => _chat?.RevokeComputerControl(id!)));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        ComputerControlHost.Child = grid;
        ComputerControlHost.IsVisible = true;
    }
}
