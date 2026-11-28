using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// <c>AxmolHub --check-macos-notifications</c> — the macOS notification channel asserted on the host that needs it.
///
/// <para><b>Why it exists.</b> The macOS path reaches UserNotifications through the Objective-C runtime by hand:
/// a global block, two dynamically added delegate methods, and three ABI constants that the compiler never sees.
/// A wrong offset or encoding builds cleanly and simply never paints a notification — the same symptom as "the
/// feature was never wired up". <c>--verify-shell</c> needs a display, and nothing in the build runs the macOS
/// half of the CI matrix through a real desktop session, so this check fills that cell: it runs before Avalonia,
/// needs no window, and stops short of requesting authorization.</para>
///
/// <para><b>It never asks for permission.</b> Requesting would put a system prompt in front of a CI runner and
/// could not be answered; the probe exercises the production delegate and block instead, and a person still
/// clicks one real banner. The bundle identity is printed rather than asserted: CI runs the bare
/// <c>AxmolHub.dll</c> (no .app), where delivery is impossible by design, and an installed/dev bundle prints its
/// identifier so the test can be told apart from a misconfiguration.</para>
///
/// <para>Output is <c>PASS:</c>/<c>FAIL:</c>/<c>SKIP:</c> lines in English — this is terminal text, not interface
/// text. Exit code is the number of failed assertions.</para>
/// </summary>
internal static class MacNotificationSelfCheck
{
    public static int Run()
    {
        var report = CheckReport.Console();
        bool Check(bool condition, string name) => report.Check(condition, name);

        if (!OperatingSystem.IsMacOS())
        {
            Check(true, "this host is not macOS, and the notification channel it would test is macOS-only");
            return report.ExitCode;
        }

        MacUserNotifications.Prepare();
        Console.WriteLine("bundle=" + (MacUserNotifications.BundleIdentifier ?? "none")
                          + " bundled=" + (MacUserNotifications.Bundled ? "yes" : "no"));
        if (!MacUserNotifications.Bundled)
        {
            report.Skip("this process is not inside an .app bundle; authorization and delivery need "
                        + "a bundle identity, so only the ABI below is asserted");
        }
        else
        {
            var statusTask = MacUserNotifications.GetPermissionStatusAsync();
            var completed = statusTask.Wait(TimeSpan.FromSeconds(3));
            var status = completed ? statusTask.Result : MacNotificationPermissionStatus.Unavailable;
            Check(completed && status != MacNotificationPermissionStatus.Unavailable,
                "getNotificationSettingsWithCompletionHandler: returned a live authorization status ("
                + (completed ? status.ToString() : "timed out") + ")");
        }

        Check(MacUserNotifications.TryProbe(out var step, out var failure),
            "the UserNotifications delegate, the global completion block and the presentation options answer on "
            + "this host (step=" + step + (failure is null ? "" : ", " + failure) + ")");

        Check(MacUserNotifications.TryGetRequestConversationId(Guid.NewGuid().ToString("N"), out _)
              && !MacUserNotifications.TryGetRequestConversationId(Guid.NewGuid().ToString("N")[..31], out _)
              && !MacUserNotifications.TryGetRequestConversationId("not-a-conversation", out _),
            "a notification request identifier only accepts a 32-character hex conversation id");

        Check(MacUserNotifications.PresentationBannerAndList == ((1 << 3) | (1 << 4))
              && MacUserNotifications.WillPresentTypeEncoding == "v@:@@@?"
              && MacUserNotifications.DidReceiveTypeEncoding == "v@:@@@?",
            "the presentation options and both delegate encodings match the UserNotifications headers");

        report.Skip("a visible banner and a click need a desktop session and a person; use "
                    + "--test-system-attention and the manual checklist in docs/building-from-source.md");
        return report.ExitCode;
    }
}
