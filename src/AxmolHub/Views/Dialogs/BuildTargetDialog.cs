using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>The build dialog's draft; project metadata is unchanged until a build starts.</summary>
public sealed record BuildSelection(BuildTarget Target, string Configuration, bool AdvancedBuildEnabled, string AdditionalBuildParameters);

/// <summary>
/// Choose a target, configuration and optional extra engine arguments. Null means cancellation;
/// metadata writes are deferred until the build starts in <see cref="HubWorkspace.BuildAsync"/>.
/// </summary>
public sealed class BuildTargetDialog : Window
{
    private readonly ComboBox _picker = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly ComboBox _configuration = new() { Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly CheckBox _advanced = new() { Name = "AdvancedBuild", Content = HubStrings.Get("AdvancedBuild") };
    private readonly TextBox _parameters = new()
    {
        Name = "AdditionalBuildParameters",
        PlaceholderText = "-xc \"-DAX_ENABLE_VR=ON,-DAX_ENABLE_OPENXR=ON\"",
        Margin = new Thickness(0, 6, 0, 8),
    };
    private readonly ProjectEntry _project;

    private BuildTargetDialog(ProjectEntry project)
    {
        _project = project;
        Title = HubStrings.Get("ChooseBuildPlatform");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        var body = new StackPanel();
        body.Children.Add(_picker);
        body.Children.Add(new TextBlock { Text = HubStrings.Get("BuildConfiguration") });
        body.Children.Add(_configuration);
        body.Children.Add(_hint);
        body.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("BuildPlatformHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        body.Children.Add(_advanced);
        var advancedBody = new StackPanel { IsVisible = false, ClipToBounds = true };
        advancedBody.Children.Add(new TextBlock { Text = HubStrings.Get("AdditionalBuildParameters"), Margin = new Thickness(0, 8, 0, 0) });
        advancedBody.Children.Add(_parameters);
        advancedBody.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("AdditionalBuildParametersHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        body.Children.Add(advancedBody);
        _parameters.Text = project.AdditionalBuildParameters;
        _advanced.IsChecked = project.AdvancedBuildEnabled;
        _advanced.IsCheckedChanged += (_, _) => advancedBody.IsVisible = _advanced.IsChecked == true;
        advancedBody.IsVisible = _advanced.IsChecked == true;

        var (footer, _, _) = HubDialogChrome.Footer(HubStrings.Get("Continue"), HubStrings.Get("Cancel"),
            () => Close(HubDialogResult.Ok), () => Close(HubDialogResult.Cancel));
        Content = chrome.Wrap(body, footer);

        var available = BuildTargets.ForHost(project.Version);
        _picker.ItemsSource = available;
        // The project's current target may not be buildable on this host (e.g. picked on another machine
        // or via the CLI); fall back to the first buildable target so the combo box never shows a selection
        // that isn't in the list.
        var current = BuildTargets.Get(project.Platform);
        _picker.SelectedItem = available.Contains(current) ? current : available[0];
        _picker.SelectionChanged += (_, _) => RefreshConfigurations();
        RefreshConfigurations();
    }

    private void RefreshConfigurations()
    {
        var android = (_picker.SelectedItem as BuildTarget)?.Family == "android";
        var previous = _configuration.SelectedItem as string ?? _project.Configuration;
        _configuration.ItemsSource = BuildConfigurations.All;
        _configuration.SelectedItem = BuildConfigurations.All.Contains(previous) ? previous : "Debug";
        _hint.Text = HubStrings.Get(android ? "AndroidReleasePending" : "ConfigurationHint");
    }

    public static async Task<BuildSelection?> PickAsync(Window? owner, ProjectEntry project)
    {
        var dialog = new BuildTargetDialog(project);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        return result == HubDialogResult.Ok && dialog._picker.SelectedItem is BuildTarget target
            && dialog._configuration.SelectedItem is string configuration
            ? new BuildSelection(target, configuration, dialog._advanced.IsChecked == true, TextEol.ToLf(dialog._parameters.Text ?? ""))
            : null;
    }
}
