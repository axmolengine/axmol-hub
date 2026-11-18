using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>The build dialog's draft; project metadata is unchanged until a build starts.</summary>
public sealed record BuildSelection(BuildTarget Target, string Configuration, bool AdvancedBuildEnabled, string AdditionalBuildParameters, BuildCleanup? Cleanup = null, bool? UsePrebuilt = null);

/// <summary>
/// Choose a target, configuration and optional extra engine arguments. Null means cancellation;
/// metadata writes are deferred until the build starts in <see cref="HubWorkspace.BuildAsync"/>.
/// </summary>
public sealed class BuildTargetDialog : Window
{
    private readonly ComboBox _picker = new() { Name = "BuildTargetPicker", Margin = new Thickness(0, 12, 0, 12) };
    private readonly ComboBox _configuration = new() { Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly Expander _advanced = new() { Name = "AdvancedBuild", Header = HubStrings.Get("AdvancedBuild") };
    private readonly TextBox _parameters = new()
    {
        Name = "AdditionalBuildParameters",
        PlaceholderText = "-xc \"-DAX_ENABLE_VR=ON,-DAX_ENABLE_OPENXR=ON\"",
        Margin = new Thickness(0, 6, 0, 8),
    };
    private readonly BuildCleanupChoice _cleanup;
    private readonly PrebuiltChoice _prebuilt;
    private readonly Func<Window, BuildTarget, string, PrebuiltChoiceState>? _prebuiltState;
    private EngineBuildRequest? _engineBuild;

    private BuildTargetDialog(ProjectEntry project, bool usePrebuilt,
        Func<Window, BuildTarget, string, PrebuiltChoiceState>? prebuiltState)
    {
        _prebuiltState = prebuiltState;
        _prebuilt = new PrebuiltChoice(usePrebuilt);
        _prebuilt.BuildRequested += request =>
        {
            _engineBuild = request;
            Close(HubDialogResult.Cancel);
        };
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
        body.Children.Add(_prebuilt);
        body.Children.Add(_hint);
        _cleanup = new BuildCleanupChoice(() => BuildCleanup.ForProject(project, (BuildTarget)_picker.SelectedItem!));
        body.Children.Add(_cleanup);
        body.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("BuildPlatformHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var advancedBody = new StackPanel { ClipToBounds = true };
        advancedBody.Children.Add(new TextBlock { Text = HubStrings.Get("AdditionalBuildParameters"), Margin = new Thickness(0, 8, 0, 0) });
        advancedBody.Children.Add(_parameters);
        advancedBody.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("AdditionalBuildParametersHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        _advanced.Content = advancedBody;
        _advanced.IsExpanded = project.AdvancedBuildEnabled;
        body.Children.Add(_advanced);
        _parameters.Text = project.AdditionalBuildParameters;

        bool CanContinue() => _picker.SelectedItem is BuildTarget && _configuration.SelectedItem is string
            && _cleanup.CanContinue && _prebuilt.CanContinue;
        var (footer, proceed, _) = HubDialogChrome.Footer(HubStrings.Get("Continue"), HubStrings.Get("Cancel"),
            () => { if (CanContinue()) Close(HubDialogResult.Ok); }, () => Close(HubDialogResult.Cancel));
        void RefreshFooter() => proceed.IsEnabled = CanContinue();
        _cleanup.Changed += RefreshFooter;
        _prebuilt.Changed += RefreshFooter;
        Content = chrome.Wrap(body, footer);

        var available = BuildTargets.ForHost(project.Version);
        _picker.ItemsSource = available;
        // The project's current target may not be buildable on this host (e.g. picked on another machine
        // or via the CLI); fall back to the first buildable target so the combo box never shows a selection
        // that isn't in the list.
        var current = BuildTargets.Get(project.Platform);
        _picker.SelectedItem = available.Contains(current) ? current : available.FirstOrDefault();
        _configuration.ItemsSource = BuildConfigurations.All;
        _configuration.SelectedItem = BuildConfigurations.All.Contains(project.Configuration) ? project.Configuration : BuildConfigurations.Default;
        _picker.SelectionChanged += (_, _) =>
        {
            RefreshTarget();
            _cleanup.Refresh();
        };
        _configuration.SelectionChanged += (_, _) => RefreshPrebuilt();
        RefreshTarget();
        RefreshFooter();
    }

    private void RefreshTarget()
    {
        var android = (_picker.SelectedItem as BuildTarget)?.Family == "android";
        _hint.Text = HubStrings.Get(android ? "AndroidReleasePending" : "ConfigurationHint");
        RefreshPrebuilt();
    }

    private void RefreshPrebuilt()
    {
        if (_picker.SelectedItem is not BuildTarget target || _configuration.SelectedItem is not string configuration)
        {
            _prebuilt.IsVisible = false;
            return;
        }
        var state = _prebuiltState?.Invoke(this, target, configuration);
        _prebuilt.Refresh(target, state?.Availability, state?.ConfirmBuild);
    }

    internal static async Task<(BuildSelection? Selection, EngineBuildRequest? EngineBuild)> PickAsync(Window? owner, ProjectEntry project,
        bool usePrebuilt = false, Func<Window, BuildTarget, string, PrebuiltChoiceState>? prebuiltState = null)
    {
        var dialog = new BuildTargetDialog(project, usePrebuilt, prebuiltState);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        BuildSelection? selection = result == HubDialogResult.Ok && dialog._picker.SelectedItem is BuildTarget target
            && dialog._configuration.SelectedItem is string configuration
            ? new BuildSelection(target, configuration, dialog._advanced.IsExpanded, TextEol.ToLf(dialog._parameters.Text ?? ""),
                dialog._cleanup.Cleanup, EnginePrebuilt.Supported(target) ? dialog._prebuilt.Enabled : null)
            : null;
        return (selection, dialog._engineBuild);
    }
}
