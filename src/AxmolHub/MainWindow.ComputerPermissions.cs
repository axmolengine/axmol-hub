using System.Diagnostics;

namespace AxmolHub;

public partial class MainWindow
{
    private bool _macComputerPermissionDismissed;

    private void InitializeMacComputerPermissions()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacOsComputerPermissions.Changed += OnMacComputerPermissionNeeded;
        Closed += (_, _) => MacOsComputerPermissions.Changed -= OnMacComputerPermissionNeeded;
        MacComputerScreenSettings.Click += async (_, _) => await OpenMacComputerSettingsAsync(MacOsComputerPermission.ScreenRecording);
        MacComputerAccessibilitySettings.Click += async (_, _) => await OpenMacComputerSettingsAsync(MacOsComputerPermission.Accessibility);
        MacComputerPermissionRecheck.Click += (_, _) =>
        {
            // A press is a user action, so it asks the system again for whatever is still missing; the cooldown
            // collapses only the requests one call makes in a burst, not two deliberate presses.
            var missing = MacOsComputerPermissions.Missing;
            if (missing.HasFlag(MacOsComputerPermission.ScreenRecording)) MacOsComputerPermissions.Request(MacOsComputerPermission.ScreenRecording);
            if (missing.HasFlag(MacOsComputerPermission.Accessibility)) MacOsComputerPermissions.Request(MacOsComputerPermission.Accessibility);
            SyncMacComputerPermissions();
        };
        MacComputerPermissionDismiss.Click += (_, _) =>
        {
            _macComputerPermissionDismissed = true;
            MacComputerPermissionBanner.IsVisible = false;
        };
    }

    private void OnMacComputerPermissionNeeded()
    {
        _macComputerPermissionDismissed = false;
        SyncMacComputerPermissions();
    }

    private void SyncMacComputerPermissions()
    {
        if (!OperatingSystem.IsMacOS() || MacComputerPermissionBanner is null
            || MacOsComputerPermissions.Needed == MacOsComputerPermission.None) return;
        var missing = MacOsComputerPermissions.Missing;
        var screen = missing.HasFlag(MacOsComputerPermission.ScreenRecording);
        var accessibility = missing.HasFlag(MacOsComputerPermission.Accessibility);
        MacComputerPermissionText.Text = missing == MacOsComputerPermission.None
            ? HubStrings.Get("MacComputerPermissionsReady")
            : string.Join("\n", new[]
            {
                screen ? HubStrings.Get("MacComputerScreenMissing") : null,
                accessibility ? HubStrings.Get("MacComputerAccessibilityMissing") : null,
                HubStrings.Get("MacComputerPermissionRetry"),
            }.Where(line => line is not null));
        MacComputerScreenSettings.IsVisible = screen;
        MacComputerAccessibilitySettings.IsVisible = accessibility;
        MacComputerScreenSettings.Content = HubStrings.Get(accessibility ? "MacComputerOpenScreenSettings" : "MacComputerOpenSettings");
        MacComputerAccessibilitySettings.Content = HubStrings.Get(screen ? "MacComputerOpenAccessibilitySettings" : "MacComputerOpenSettings");
        MacComputerPermissionRecheck.IsVisible = missing != MacOsComputerPermission.None;
        MacComputerPermissionBanner.IsVisible = !_macComputerPermissionDismissed;
    }

    private async Task OpenMacComputerSettingsAsync(MacOsComputerPermission permission)
    {
        var pane = permission == MacOsComputerPermission.ScreenRecording ? "Privacy_ScreenCapture" : "Privacy_Accessibility";
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("x-apple.systempreferences:com.apple.preference.security?" + pane);
            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("System Settings did not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("System Settings did not open.");
            // The window's Activated event rechecks on return. It never resumes the tool call.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MacComputerPermissionText.Text = HubStrings.Get("MacComputerSettingsFailed");
        }
    }
}
