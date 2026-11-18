using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

internal readonly record struct PrebuiltChoiceState(PrebuiltAvailability? Availability, Func<Task<EngineBuildRequest?>>? ConfirmBuild);

internal sealed class PrebuiltChoice : StackPanel
{
    private readonly CheckBox _check = new() { Name = "UsePrebuiltLibraries", Content = HubStrings.Get("UsePrebuilt") };
    private readonly TextBlock _status = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _build = new() { Content = HubStrings.Get("PrebuiltBuildThisConfiguration"), Classes = { "compact", "quiet" } };
    private PrebuiltAvailability? _availability;
    private Func<Task<EngineBuildRequest?>>? _confirmBuild;

    public bool Enabled => _check.IsChecked == true;
    public bool CanContinue => !IsVisible || !Enabled || _availability?.Usable == true;
    public event Action? Changed;
    public event Action<EngineBuildRequest>? BuildRequested;

    public PrebuiltChoice(bool enabled)
    {
        Margin = new Thickness(0, 0, 0, 12);
        _check.IsChecked = enabled;
        Children.Add(_check);
        Children.Add(_status);
        Children.Add(_build);
        _check.IsCheckedChanged += (_, _) => Update();
        _build.Click += async (_, _) =>
        {
            if (_confirmBuild is not null && await _confirmBuild() is { } request)
                BuildRequested?.Invoke(request);
        };
    }

    public void Refresh(BuildTarget target, PrebuiltAvailability? availability, Func<Task<EngineBuildRequest?>>? confirmBuild = null)
    {
        IsVisible = EnginePrebuilt.Supported(target);
        _availability = availability;
        _confirmBuild = confirmBuild;
        Update();
    }

    private void Update()
    {
        _check.IsEnabled = _availability?.Usable == true || Enabled;
        _status.Text = _availability switch
        {
            { Usable: true } ready => string.Format(HubStrings.Get("PrebuiltReadyFormat"), ready.Label, ready.RelativeDirectory),
            { } unavailable => HubStrings.Get(unavailable.TextKey),
            null => HubStrings.Get("EngineMissing"),
        };
        _build.IsVisible = _availability?.Usable == false && _confirmBuild is not null;
        if (!CanContinue)
            _status.Text += " " + HubStrings.Get(_build.IsVisible ? "PrebuiltDisableOrBuild" : "PrebuiltDisableToContinue");
        Changed?.Invoke();
    }
}
