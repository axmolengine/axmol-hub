using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Confirms the cost and scope before compiling the engine libraries.</summary>
public sealed class EngineBuildWindow : Window
{
    private readonly ComboBox _target;
    private readonly ComboBox _configuration;
    private readonly BuildCleanupChoice _cleanup;

    private EngineBuildWindow(EngineEntry engine, IReadOnlyList<BuildTarget> targets, string? requiredConfiguration)
    {
        _target = new ComboBox { ItemsSource = targets, SelectedIndex = 0, MinWidth = 200 };
        _configuration = new ComboBox
        {
            ItemsSource = BuildConfigurations.All,
            SelectedItem = requiredConfiguration ?? "Release",
            IsEnabled = requiredConfiguration is null,
            MinWidth = 200,
        };
        _cleanup = new BuildCleanupChoice(() => BuildCleanup.ForEngine(engine, (BuildTarget)_target.SelectedItem!));
        _target.SelectionChanged += (_, _) => _cleanup.Refresh();

        Title = HubStrings.Get("BuildEngineConfirm");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = engine.ToString(), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("EngineBuildCost"), TextWrapping = TextWrapping.Wrap });
        if (requiredConfiguration is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Format(HubStrings.Get("PrebuiltUnavailableFormat"), targets[0].Name, requiredConfiguration),
            });
            panel.Children.Add(new TextBlock { Text = HubStrings.Get("PrebuiltBuildOnlyHint"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            if (targets.Count == 1) panel.Children.Add(new TextBlock { Text = targets[0].Name });
            else panel.Children.Add(_target);
            panel.Children.Add(new TextBlock { Text = HubStrings.Get("BuildConfiguration") });
            panel.Children.Add(_configuration);
        }
        panel.Children.Add(_cleanup);
        var (footer, proceed, _) = HubDialogChrome.Footer(HubStrings.Get("Build"), HubStrings.Get("Cancel"),
            () => { if (_cleanup.CanContinue) Close(HubDialogResult.Ok); }, () => Close(HubDialogResult.Cancel));
        _cleanup.Changed += () => proceed.IsEnabled = _cleanup.CanContinue;
        Content = chrome.Wrap(panel, footer);
    }

    public static async Task<EngineBuildRequest?> PickAsync(Window? owner, EngineEntry engine,
        IReadOnlyList<BuildTarget> targets, string? requiredConfiguration = null)
    {
        if (targets.Count == 0) return null;
        var dialog = new EngineBuildWindow(engine, targets, requiredConfiguration);
        var result = await dialog.ShowDialog<HubDialogResult>(owner!);
        return result == HubDialogResult.Ok && dialog._target.SelectedItem is BuildTarget target
            && (requiredConfiguration ?? dialog._configuration.SelectedItem as string) is string configuration
            ? new(engine, target, configuration, dialog._cleanup.Cleanup) : null;
    }
}

public sealed record EngineBuildRequest(EngineEntry Engine, BuildTarget Target, string Configuration, BuildCleanup? Cleanup);
