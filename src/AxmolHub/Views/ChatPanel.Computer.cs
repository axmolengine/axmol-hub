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
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        void Button(string key, Action action)
        {
            var button = new Button { Content = HubStrings.Get(key), Classes = { "compact", "quiet" } };
            button.Click += (_, _) => action();
            actions.Children.Add(button);
        }
        Button(control.Paused ? "ComputerControlResume" : "ComputerControlPause",
            () => _chat?.PauseComputerControl(id!, !control.Paused));
        Button("ComputerControlStop", () => _chat?.RequestStop(id!));
        Button("ComputerControlRevoke", () => _chat?.RevokeComputerControl(id!));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        ComputerControlHost.Child = grid;
        ComputerControlHost.IsVisible = true;
    }
}
