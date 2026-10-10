using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// Command line and host directory. Counterpart of the argument parsing in WPF's App.xaml.cs,
/// with each default aligned one by one.
/// </summary>
internal sealed record HubHostOptions(
    string? DataRootArgument,
    string PreferencesPath,
    string? SmokeImagePath,
    string? SmokePagesDirectory,
    string? ShotProviderPickerPath,
    string? ShotSettingsAuthPath,
    string? ShotAuthDialogPath,
    string? ShotBottomMenuPath,
    string? ShotComposerMenuPath,
    string? VerifyThemeReport,
    string? VerifyFoundationReport,
    string? VerifyShellReport,
    string? VerifyOpsReport,
    string[] VerifyOpsEngines,
    bool Gallery,
    bool TestSystemAttention)
{
    /// <summary>
    /// Verification / screenshot modes. They run the **product window** (<c>--smoke</c>,
    /// <c>--smoke-pages</c>) but with no one around to click buttons — any modal dialog would turn
    /// "really running" into "hanging". So anything that exists only for a human (e.g. the missing
    /// font prompt) never fires here.
    ///
    /// Split of duties with <c>HubWorkspace.SuppressDialogs</c>: that one silences **operation
    /// failure** dialogs (inside a real operation), this one silences the **shell's own**
    /// informational dialogs (with no corresponding operation to fail).
    /// </summary>
    public bool IsAutomation =>
        SmokeImagePath is not null || SmokePagesDirectory is not null || ShotProviderPickerPath is not null
        || ShotSettingsAuthPath is not null || ShotAuthDialogPath is not null || ShotBottomMenuPath is not null
        || ShotComposerMenuPath is not null
        || VerifyThemeReport is not null || VerifyFoundationReport is not null
        || VerifyShellReport is not null || VerifyOpsReport is not null || Gallery;

    /// <summary>
    /// Settings and data root live in the per-user directory, **not** next to
    /// AppContext.BaseDirectory: Velopack replaces the whole current\ under the install directory
    /// on update, and deletes the entire install directory on uninstall.
    /// Engines and toolchains are gigabyte-scale, so use LocalApplicationData rather than roaming
    /// AppData.
    /// </summary>
    public static string UserDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "AxmolHub");

    public static string DefaultPreferencesPath => System.IO.Path.Combine(UserDirectory, "hub-settings.json");

    public static string DefaultDataRoot => System.IO.Path.Combine(UserDirectory, "data");

    public static string? DeepLinkArgument(string[] args)
        => args.FirstOrDefault(argument => argument.StartsWith(EngineInstallLink.Scheme + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Three-tier data-root priority: command line &gt; settings file &gt; default directory (same as
    /// the WPF version). Therefore this does **not** fall back here — once the default is filled in
    /// at parse time, we can no longer tell "the user explicitly passed --data-root" from "not
    /// passed", and the settings file value would be permanently overridden by the default.
    /// </summary>
    public string ResolveDataRoot(HubPreferences preferences)
        => System.IO.Path.GetFullPath(DataRootArgument ?? preferences.DataRoot ?? DefaultDataRoot);

    /// <summary>
    /// <c>--check-secrets [scratch-dir]</c>: the secret store's headless self-check. Handled in
    /// <c>Program.Main</c> before Avalonia starts rather than as a field on this record, because unlike every
    /// other verification mode it opens no window — which is what lets the Linux CI job run it with no display.
    /// </summary>
    public const string CheckSecretsFlag = "--check-secrets";

    public static bool IsSecretsSelfTest(string[] args)
        => args.Any(a => string.Equals(a, CheckSecretsFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The scratch directory after the flag, or <c>null</c> for the per-process temp default.</summary>
    public static string? SecretsScratchArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, CheckSecretsFlag, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    /// <summary>
    /// <c>--check-linux-integration [scratch-dir]</c>: the Linux desktop integration's headless self-check.
    /// Handled like <see cref="CheckSecretsFlag"/> — before Avalonia, no window — because the claim it proves
    /// ("the desktop environment can find our icon") is about a Linux session and nothing else in the build
    /// reaches one.
    /// </summary>
    public const string CheckLinuxIntegrationFlag = "--check-linux-integration";

    public static bool IsLinuxIntegrationSelfTest(string[] args)
        => args.Any(a => string.Equals(a, CheckLinuxIntegrationFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The scratch directory after the flag, or <c>null</c> for the per-process temp default.</summary>
    public static string? LinuxIntegrationScratchArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, CheckLinuxIntegrationFlag, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    /// <summary>
    /// <c>--check-webfetch &lt;url&gt;</c>: one real fetch through the shipped <c>web_fetch</c> body, headless.
    /// Handled like <see cref="CheckSecretsFlag"/> — before Avalonia, no window — because it is the only switch that
    /// can answer "did this build actually leave the machine, and what came back". Everything else about the tool is
    /// asserted on a stub transport, which proves the socket was constructed but not that a page arrives; the real hop
    /// needs real DNS, real TLS and a real server, and a human staring at a chat window is the weak version of that.
    /// It sends exactly one GET to the address on the command line and reads the outbound switch from the settings
    /// file first, so it also proves what the default is on this machine.
    /// </summary>
    public const string CheckWebFetchFlag = "--check-webfetch";

    public static bool IsWebFetchSelfTest(string[] args)
        => args.Any(a => string.Equals(a, CheckWebFetchFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address after the flag, or <c>null</c> when none was given.</summary>
    public static string? WebFetchArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, CheckWebFetchFlag, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    public static HubHostOptions Parse(string[] args)
    {
        return new HubHostOptions(
            Value(args, "--data-root"),
            Value(args, "--preferences") ?? DefaultPreferencesPath,
            Value(args, "--smoke"),
            Value(args, "--smoke-pages"),
            Value(args, "--shot-provider-picker"),
            Value(args, "--shot-settings-auth"),
            Value(args, "--shot-auth-dialog"),
            Value(args, "--shot-bottom-menu"),
            Value(args, "--shot-composer-menu"),
            Value(args, "--verify-theme"),
            Value(args, "--verify-foundation"),
            Value(args, "--verify-shell"),
            Value(args, "--verify-ops"),
            Trailing(args, "--verify-ops"),
            args.Any(a => string.Equals(a, "--gallery", StringComparison.OrdinalIgnoreCase)),
            args.Any(a => string.Equals(a, "--test-system-attention", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// <c>--verify-ops &lt;report&gt; &lt;engine dir&gt;...</c>: after the report path, any argument
    /// not starting with <c>--</c> counts as an engine directory. Positional arguments are used
    /// instead of repeated <c>--engine</c> flags because this command inherently takes several
    /// engines at once (a complete one and an incomplete one).
    /// </summary>
    private static string[] Trailing(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return [];
        }

        var values = new List<string>();
        for (var i = index + 2; i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            values.Add(args[i]);
        }

        return [.. values];
    }

    /// <summary>
    /// Reads the value of `--flag value`. Deliberately does not treat "a flag followed by another
    /// flag" as a value: `--smoke --gallery` should be treated as a missing argument, not use
    /// `--gallery` as a file name.
    /// </summary>
    private static string? Value(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var value = args[index + 1];
        return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }
}
