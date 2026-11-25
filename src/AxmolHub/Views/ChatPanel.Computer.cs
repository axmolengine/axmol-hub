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
        if (id is not null && _chat?.PortalControlFor(id) is { } portal)
        {
            var portalIdentity = id + ":portal:" + portal.Version + ":" + portal.Interaction + ":" + HubStrings.Get("PortalSessionTitle");
            if (_computerControlIdentity == portalIdentity) return;
            _computerControlIdentity = portalIdentity;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("PortalSessionTitle") + " · " + (portal.Paused ? HubStrings.Get("ComputerControlPause")
                    : portal.Interaction ? HubStrings.Get("ComputerControlInteraction") : HubStrings.Get("PortalOneAction")),
                Classes = { "muted" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            ToolTip.SetTip(row, HubStrings.Get("PortalInteractionHint"));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(CapsuleActionButton(portal.Paused ? "Hub.Icon.Play" : "Hub.Icon.Pause",
                portal.Paused ? "ComputerControlResume" : "ComputerControlPause", () => _chat.PauseComputerControl(id, !portal.Paused)));
            buttons.Children.Add(CapsuleActionButton("Hub.Icon.Stop", "ComputerControlStop", () => _chat.RequestStop(id)));
            buttons.Children.Add(CapsuleActionButton("Hub.Icon.Close", "ComputerControlRevoke", () => _chat.RevokeComputerControl(id)));
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            ComputerControlHost.Child = row;
            ComputerControlHost.IsVisible = true;
            return;
        }
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
        label.Text += " · " + HubStrings.Get(control.Interaction ? "ComputerControlInteraction" : "ComputerControlBrowsing");
        ToolTip.SetTip(label, control.Target.Application.ExecutablePath + "\n"
            + HubStrings.Get(control.Interaction ? "ComputerUseInteractionHint" : "ComputerUseGrantHint"));
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
