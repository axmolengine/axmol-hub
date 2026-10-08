using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The "prebuilt library settings" dialog (per project). Pure code-built, following the
/// <see cref="AndroidReleaseWindow"/> approach.
///
/// It answers three questions the user has:
/// <list type="number">
/// <item>whether this project currently links prebuilt libraries;</item>
/// <item>whether the engine's copy is usable under the **current target + configuration**, and in which directory;</item>
/// <item>what to do when it's unusable (build it from the engines page).</item>
/// </list>
/// Point 2 matters especially: when the directory is unusable the engine **silently falls back to
/// source build** (no error), so "I checked it but did it actually take effect" must be made clear
/// here, not discovered after the build runs absurdly slowly.
/// </summary>
public sealed class PrebuiltWindow : Window
{
    private readonly CheckBox _enabled;
    private readonly Action<string>? _navigate;

    public bool Enabled => _enabled.IsChecked == true;

    public PrebuiltWindow(ProjectEntry project, EngineEntry engine, BuildTarget target,
        PrebuiltAvailability availability, bool enabled, Action<string>? navigate)
    {
        _navigate = navigate;

        Title = HubStrings.Get("PrebuiltSettings");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("PrebuiltSettings"), FontSize = 18 });
        panel.Children.Add(new TextBlock
        {
            Text = project.Name + "\n" + engine + " · " + target.Name + " · " + project.Configuration,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _enabled = new CheckBox
        {
            Content = HubStrings.Get("UsePrebuilt"),
            IsChecked = enabled,
            Margin = new Thickness(0, 16, 0, 0),
            // Don't allow checking when the target platform is unsupported: checking would only make the build fail.
            IsEnabled = availability.Status != PrebuiltStatus.PlatformUnsupported,
        };
        panel.Children.Add(_enabled);

        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("PrebuiltHint"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        // The resolution conclusion: when ready, "platform architecture configuration → directory", otherwise the precise reason.
        var status = availability.Usable
            ? string.Format(HubStrings.Get("PrebuiltReadyFormat"), availability.Label, availability.RelativeDirectory)
            : HubStrings.Get(availability.TextKey);
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("PrebuiltStatus") + "：" + status,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = HubStrings.Get("Ok"), IsDefault = true, Classes = { "primary" } };
        ok.Click += (_, _) => Close(HubDialogResult.Ok);
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };

        // When unusable, give the "what to do" along with the entry point so the user doesn't have to hunt for it.
        if (!availability.Usable)
        {
            panel.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("PrebuiltUnavailableAction"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });

            var open = new Button { Content = HubStrings.Get("OpenEnginesPage"), Classes = { "quiet" }, Margin = new Thickness(0, 12, 0, 0) };
            open.Click += (_, _) =>
            {
                _navigate?.Invoke("Installs");
                Close(HubDialogResult.Cancel);
            };
            panel.Children.Add(open);
        }

        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };
    }
}
