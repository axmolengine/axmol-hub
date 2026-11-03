using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Chooses a newer installed engine for an existing project.</summary>
public sealed class EngineUpgradeDialog : Window
{
    private readonly ListBox _engines = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly Button _apply;
    private readonly int _currentMajor;

    public EngineEntry? Selected => (_engines.SelectedItem as Row)?.Engine;

    private sealed record Row(EngineEntry Engine, bool IsCrossMajor)
    {
        public override string ToString() => Engine.Version + " · " + Engine.Channel;
    }

    private EngineUpgradeDialog(ProjectEntry project, IReadOnlyList<EngineEntry> candidates)
    {
        Title = HubStrings.Get("UpgradeEngineTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _currentMajor = BuildTargets.MajorVersion(project.Version);

        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = string.Format(HubStrings.Get("UpgradeEngineCurrent"), project.Version + " · " + project.Channel),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("UpgradeEngineHint"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        body.Children.Add(_engines);
        body.Children.Add(_detail);
        body.Children.Add(_warning);

        var (footer, apply, _) = HubDialogChrome.Footer(HubStrings.Get("UpgradeEngineApply"), HubStrings.Get("Cancel"),
            () => Close(HubDialogResult.Ok), () => Close(HubDialogResult.Cancel));
        _apply = apply;
        Content = chrome.Wrap(body, footer);

        _engines.ItemsSource = candidates
            .Select(engine => new Row(engine, BuildTargets.MajorVersion(engine.Version) != _currentMajor))
            .ToArray();
        _engines.SelectedIndex = 0;
        _engines.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if ((_engines.SelectedItem as Row) is not { } row)
        {
            _detail.Text = HubStrings.Get("UpgradeEngineNone");
            _warning.Text = "";
            _warning.IsVisible = false;
            _apply.IsEnabled = false;
            return;
        }

        _apply.IsEnabled = true;
        _detail.Text = string.Format(HubStrings.Get("UpgradeEngineTarget"), row.Engine.Version + " · " + row.Engine.Channel);
        _warning.IsVisible = row.IsCrossMajor;
        _warning.Text = row.IsCrossMajor ? HubStrings.Get("UpgradeEngineCrossMajor") : "";
    }

    public static async Task<EngineEntry?> PickAsync(Window? owner, ProjectEntry project, IReadOnlyList<EngineEntry> candidates)
    {
        if (candidates.Count == 0) return null;
        var dialog = new EngineUpgradeDialog(project, candidates);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Selected : null;
    }
}
