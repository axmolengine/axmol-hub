using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

public sealed class PrebuiltWindow : Window
{
    private readonly PrebuiltChoice _choice;
    public bool Enabled => _choice.Enabled;
    public EngineBuildRequest? BuildRequest { get; private set; }

    public PrebuiltWindow(ProjectEntry project, EngineEntry engine, BuildTarget target,
        PrebuiltAvailability? availability, bool enabled, Func<Window, Task<EngineBuildRequest?>>? confirmBuild)
    {
        Title = HubStrings.Get("PrebuiltSettings");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = project.Name + "\n" + engine + " · " + target.Name + " · " + project.Configuration,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _choice = new PrebuiltChoice(enabled);
        _choice.Refresh(target, availability, confirmBuild is null ? null : () => confirmBuild(this));
        _choice.BuildRequested += request =>
        {
            BuildRequest = request;
            Close(HubDialogResult.Cancel);
        };
        panel.Children.Add(_choice);
        if (!_choice.IsVisible)
            panel.Children.Add(new TextBlock { Text = HubStrings.Get("PrebuiltPreferenceRetained"), TextWrapping = TextWrapping.Wrap });

        var (footer, ok, _) = HubDialogChrome.Footer(HubStrings.Get("Ok"), HubStrings.Get("Cancel"),
            () => { if (_choice.CanContinue) Close(HubDialogResult.Ok); }, () => Close(HubDialogResult.Cancel));
        ok.IsEnabled = _choice.CanContinue;
        _choice.Changed += () => ok.IsEnabled = _choice.CanContinue;
        Content = chrome.Wrap(panel, footer);
    }
}
