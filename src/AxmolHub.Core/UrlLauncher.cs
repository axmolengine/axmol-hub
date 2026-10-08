using System.Diagnostics;

namespace AxmolHub.Core;

/// <summary>
/// Opens a URL in the user's browser, on the three platforms Hub builds for.
///
/// <para>Extracted out of <c>ChatWorkspace.TryOpenBrowser</c> because that method had a Windows branch, a macOS
/// branch, and a <c>return false</c> for everything else — which is correct as a last resort (the caller shows
/// the link so a sign-in never dead-ends), but it meant the one platform Hub ships an AppImage for never got a
/// browser.</para>
///
/// <para><b>Linux uses <c>UseShellExecute = false</c> on purpose.</b> Shell execute on Unix resolves the URL
/// through <c>xdg-open</c>, which is the right tool for "open this with its associated application" but the wrong
/// one the moment arguments have to reach a program of our choosing — the same distinction
/// <see cref="ProcessRunner.Launch"/> documents. Here we are naming an executable and handing it the URL as its
/// own argument, so we exec it.</para>
/// </summary>
public static class UrlLauncher
{
    /// <summary>
    /// Tries to open <paramref name="url"/>, returning <c>false</c> rather than throwing when there is no browser
    /// to open it with. Every caller already has a "show the link instead" branch, and that branch is what runs
    /// on a headless session — so a missing launcher is an expected condition, not an error to report.
    /// </summary>
    public static bool TryOpen(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                var open = new ProcessStartInfo("open") { UseShellExecute = false, CreateNoWindow = true };
                open.ArgumentList.Add(url);
                Process.Start(open)?.Dispose();
                return true;
            }

            return TryOpenLinux(url, LaunchLinux);
        }
        catch (Exception)
        {
            // Win32Exception (no such file), InvalidOperationException (nothing to hand the URL to), and an
            // ObjectDisposedException from a child that exited before we looked at it all mean the same thing:
            // no browser opened, use the fallback.
            return false;
        }
    }

    /// <summary>
    /// Runs the Linux attempts in <see cref="LinuxCommandOrder"/> order and returns on the first one that
    /// launches.
    ///
    /// <b>Each candidate is guarded on its own.</b> A stale <c>$BROWSER</c> entry is the common case on a real
    /// desktop, and if its <c>ENOENT</c> escaped this loop the two standard fallbacks would never be reached and
    /// the caller would be told there is no browser at all. That is why the variable's entries are kept here even
    /// when they cannot be found: this loop is where a failed exec is noticed, and it notices one without letting
    /// it end the search.
    /// </summary>
    public static bool TryOpenLinux(string url, Func<string[], bool> launch)
    {
        foreach (var argv in LinuxCommandOrder(Environment.GetEnvironmentVariable("BROWSER"), url))
        {
            try
            {
                if (launch(argv)) return true;
            }
            catch (Exception)
            {
                // Any launcher failure says only that *this* entry is not a browser; the next one still gets tried.
            }
        }

        return false;
    }

    /// <summary>
    /// Execs one argv with the URL appended, reporting whether a process came out of it.
    ///
    /// A browser that is on PATH but cannot run throws, and this does not wait to find out; one that starts and
    /// then fails is nobody's business here — the tab either opened or it did not, and the caller has already
    /// handed the user the link either way. <c>xdg-open</c> in particular forks and returns success as soon as it
    /// has passed the URL on, so an exit code would prove nothing even if we waited.
    /// </summary>
    private static bool LaunchLinux(string[] argv)
    {
        var start = new ProcessStartInfo(argv[0]) { UseShellExecute = false, CreateNoWindow = true };
        for (var i = 1; i < argv.Length; i++) start.ArgumentList.Add(argv[i]);
        using var process = Process.Start(start);
        return process is not null;
    }

    /// <summary>
    /// The Linux launch attempts, in precedence order: <c>$BROWSER</c> first because that is the variable the
    /// freedesktop convention says means "the user's browser" (a console user on a minimal system sets it because
    /// <c>xdg-open</c> is not installed and does not work), then <c>xdg-open</c>, then <c>gio open</c>.
    /// </summary>
    /// <remarks>
    /// <c>$BROWSER</c> is a colon-separated list; an entry may carry its own arguments and may name the URL slot
    /// with <c>%s</c> (the original Mozilla form, still honoured by the tools that read the variable — a wrapper
    /// script that takes the URL in the middle of a fixed argument list is a real thing people have installed).
    /// Entries are kept even when they cannot be found, because the caller's loop is where a failed exec is
    /// noticed; dropping them here would need a PATH lookup that an assertion cannot fake.
    ///
    /// <para>Public rather than internal for the same reason <c>CjkFontNotice.RunningOnLinux</c> is: it is the
    /// part of this class that can be asserted without a display server or a browser installed, and the Checks
    /// harness is a separate assembly.</para>
    /// </remarks>
    public static IReadOnlyList<string[]> LinuxCommandOrder(string? browserVariable, string url)
    {
        var attempts = new List<string[]>();

        foreach (var entry in (browserVariable ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var argv = FromBrowserEntry(entry, url);
            if (argv is not null) attempts.Add(argv);
        }

        attempts.Add(["xdg-open", url]);
        attempts.Add(["gio", "open", url]);
        return attempts;
    }

    /// <summary>
    /// One <c>$BROWSER</c> entry turned into an argv array, or <c>null</c> when the entry has nothing in it but
    /// whitespace. A trailing <c>%s</c> is dropped rather than passed to the browser as a stray argument.
    /// </summary>
    public static string[]? FromBrowserEntry(string entry, string url)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;

        var tokens = entry.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        var slot = Array.FindIndex(tokens, token => token == "%s");
        if (slot < 0)
        {
            // No slot named: the URL goes on the end, which is what every browser on Earth expects.
            return [.. tokens, url];
        }

        var argv = new List<string>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++) argv.Add(i == slot ? url : tokens[i]);
        // An entry that was only "%s" has no program in it; that is not a launch, it is a typo.
        return argv.Count <= 1 ? null : [.. argv];
    }
}
