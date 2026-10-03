using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// The "choose engine version" dialog. Pure code-built, same approach as
/// <see cref="BuildTargetDialog"/> (both are one-off small windows, not worth a dedicated .axaml).
///
/// It exists because: previously the engines-page button installed the latest LTS in the manifest
/// directly, leaving no entry point for older versions. And "each project locks its own engine
/// version" is a premise of Hub — project A pinned to 2.11.3 and project B to 2.11.5 is the normal
/// state, so **which version can be installed** must be the user's choice, not an implementation
/// side effect.
///
/// The list comes from <see cref="EngineReleases"/>: the remote index pulled at startup, or the
/// built-in manifest when that fails. Versions aren't enumerated in code — where versions come from
/// is a data question, not a code question.
/// Returns <c>null</c> = user cancelled, in which case not a single byte should be downloaded.
/// </summary>
public sealed class EngineVersionDialog : Window
{
    private readonly ListBox _versions = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) };
    private readonly Button _install;

    public EngineRelease? Selected => _versions.SelectedItem as EngineRelease;

    private EngineVersionDialog(IReadOnlyList<EngineRelease> releases, string? preferredVersion, bool fromRemoteIndex)
    {
        Title = HubStrings.Get("ChooseEngineVersion");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = HubStrings.Get("ChooseEngineVersion"), FontSize = 18 });
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("EngineVersionHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        // Tell the user honestly where this list comes from. Falling back isn't a fault, but "where
        // the list comes from" must be visible — otherwise "there are 6 versions" and "only the
        // built-in few" look identical.
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(fromRemoteIndex ? "EngineIndexRemote" : "EngineIndexBuiltIn"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(_versions);
        panel.Children.Add(_detail);
        panel.Children.Add(_warning);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);
        _install = new Button { Content = HubStrings.Get("InstallSelected"), IsDefault = true, Classes = { "primary" } };
        _install.Click += (_, _) => Close(HubDialogResult.Ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_install);
        panel.Children.Add(buttons);

        Content = new Border { Child = panel, Padding = new Thickness(24) };

        _versions.ItemsSource = releases;
        // Pre-select: the version the caller specified, otherwise the newest in the manifest. Fall
        // back to the first item if not found — pre-selecting none would make the "continue" button
        // fail for no reason.
        var preferred = preferredVersion is null ? null : releases.FirstOrDefault(release => release.Version == preferredVersion);
        _versions.SelectedItem = preferred ?? releases.FirstOrDefault();
        _versions.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (Selected is not { } release)
        {
            _detail.Text = HubStrings.Get("EngineVersionNone");
            _warning.Text = "";
            _warning.IsVisible = false;
            _install.IsEnabled = false;
            return;
        }

        _install.IsEnabled = true;
        _detail.Text = string.Join("\n", new[]
        {
            HubStrings.Get("Channel") + "：" + release.Channel,
            HubStrings.Get("DownloadSize") + "：" + Size(release.Package.DownloadBytes),
            HubStrings.Get(release.Installed ? "Installed" : "Missing"),
        });

        // Versions with unverified recipes are still installable — the engine itself is
        // self-consistent, and the recipe only affects the Android packaging step. But it must be
        // stated here, otherwise the user only discovers after installing that packaging won't work.
        _warning.Text = HubStrings.Get("EngineRecipeUnverified");
        _warning.IsVisible = !release.RecipesVerified;
    }

    public static string Size(long? value) => value is null ? "—" : value >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{value / (1024d * 1024 * 1024):0.00} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{value / (1024d * 1024):0.00} MB");

    public static async Task<EngineRelease?> PickAsync(Window? owner, EngineReleases catalog, string? preferredVersion)
    {
        var releases = catalog.All();
        if (releases.Count == 0) return null;
        var dialog = new EngineVersionDialog(releases, preferredVersion, catalog.UsingRemoteIndex);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        return result == HubDialogResult.Ok ? dialog.Selected : null;
    }
}
