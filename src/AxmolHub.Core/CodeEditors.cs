using System.Diagnostics;

namespace AxmolHub.Core;

/// <summary>
/// How an editor is started. This is a property of **what was found**, not of the product: VS Code is an
/// executable on Windows and Linux and an application bundle on macOS, so a catalog row that carried the
/// convention would be wrong on one host by construction — and a bundle started as a program is exactly the
/// <c>Win32Exception: The FileName property should not be a directory</c> that reached the user.
/// </summary>
public enum EditorLaunch
{
    /// <summary>A program started directly, with the folder as its positional argument.</summary>
    Executable,

    /// <summary>
    /// A macOS application bundle, started through <c>open -a</c>. A bundle is a **directory** on disk, so it
    /// cannot be executed at all: LaunchServices is the only thing that can start it.
    /// </summary>
    MacBundle,
}

/// <summary>How to look for one installation of a preset.</summary>
public enum EditorProbeKind
{
    /// <summary>A path, optionally carrying <c>%VARS%</c> or a leading <c>~/</c>.</summary>
    Path,

    /// <summary>A command name looked up on the PATH (a Linux install ships a launcher, not a fixed path).</summary>
    PathCommand,

    /// <summary>
    /// Ask <c>vswhere</c>. Visual Studio's directory carries the version, so writing the path down would rot
    /// with the next release; the installer knows where the current one put it.
    /// </summary>
    VisualStudio,
}

public sealed record EditorProbe(EditorProbeKind Kind, string Value);

/// <summary>An editor's identity. Where it was found and how that path is started are separate questions.</summary>
public sealed record EditorPreset(string Id, string DisplayName)
{
    /// <summary>
    /// A preset for an application chosen by hand, which is all a browsed-in editor offers: the display name comes
    /// from the path. Because this exists, a saved path stays launchable even when the catalog has never heard of
    /// the product — the other half of "selecting an editor on macOS never worked".
    /// </summary>
    public static EditorPreset Custom(string path)
        => new(CodeEditors.CustomId, Path.GetFileNameWithoutExtension(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))));

    public override string ToString() => DisplayName;
}

/// <summary>The persisted choice: a catalog id plus the path found (or browsed to) for it.</summary>
public sealed record CodeEditor(string Id, string Path);

/// <summary>A preset married to a path on this machine — what actually gets launched.</summary>
public sealed record EditorBinding(EditorPreset Preset, string Path)
{
    public override string ToString() => Preset.DisplayName;
}

/// <summary>A catalog row: the preset plus where each host keeps it.</summary>
public sealed record EditorSource(
    EditorPreset Preset,
    IReadOnlyList<EditorProbe> Windows,
    IReadOnlyList<EditorProbe> Mac,
    IReadOnlyList<EditorProbe> Linux);

/// <summary>
/// The editors Hub offers, and the only place that knows what one is: how to find it, whether a path can be
/// launched, and how to start it. Everything the settings page, the projects page and the launch path need is a
/// pure function here except <see cref="Detect"/> and the existence tests it uses.
/// </summary>
public static class CodeEditors
{
    /// <summary>The id a hand-picked application is saved under.</summary>
    public const string CustomId = "custom";

    private static readonly IReadOnlyList<EditorProbe> None = [];

    /// <summary>
    /// The catalog. Adding a product is one row: no state field, no UI slot, no launch branch.
    ///
    /// Visual Studio is entered version-free on purpose. Only Visual Studio 2026 exists (it replaced 2022 and
    /// there is no 2025), it is 64-bit only, and its directory name carries the version — so the row asks
    /// <c>vswhere</c> rather than naming a path that the next release would invalidate.
    /// </summary>
    public static IReadOnlyList<EditorSource> Sources { get; } =
    [
        new(new("vscode", "VS Code"),
            [At(@"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe"), At(@"%ProgramFiles%\Microsoft VS Code\Code.exe")],
            Bundle("Visual Studio Code"),
            [Command("code")]),
        new(new("vscode-insiders", "VS Code Insiders"),
            [At(@"%LOCALAPPDATA%\Programs\Microsoft VS Code Insiders\Code - Insiders.exe"), At(@"%ProgramFiles%\Microsoft VS Code Insiders\Code - Insiders.exe")],
            Bundle("Visual Studio Code - Insiders"),
            [Command("code-insiders")]),
        new(new("cursor", "Cursor"),
            [At(@"%LOCALAPPDATA%\Programs\cursor\Cursor.exe")],
            Bundle("Cursor"),
            [Command("cursor")]),
        new(new("visualstudio", "Visual Studio"),
            [new(EditorProbeKind.VisualStudio, "devenv.exe")],
            None,
            None),
        new(new("rider", "Rider"),
            [At(@"%LOCALAPPDATA%\Programs\Rider\bin\rider64.exe"), At(@"%ProgramFiles%\JetBrains\Rider\bin\rider64.exe")],
            Bundle("Rider"),
            [Command("rider")]),
        new(new("clion", "CLion"),
            [At(@"%LOCALAPPDATA%\Programs\CLion\bin\clion64.exe"), At(@"%ProgramFiles%\JetBrains\CLion\bin\clion64.exe")],
            Bundle("CLion"),
            [Command("clion")]),
        new(new("zed", "Zed"),
            [At(@"%LOCALAPPDATA%\Programs\Zed\Zed.exe")],
            Bundle("Zed"),
            [Command("zed")]),
        new(new("androidstudio", "Android Studio"),
            [At(@"%LOCALAPPDATA%\Programs\Android Studio\bin\studio64.exe"), At(@"%ProgramFiles%\Android\Android Studio\bin\studio64.exe")],
            Bundle("Android Studio"),
            [Command("studio.sh"), Command("studio")]),
        new(new("vscodium", "VSCodium"),
            None,
            None,
            [Command("codium")]),
    ];

    private static EditorProbe At(string path) => new(EditorProbeKind.Path, path);

    private static EditorProbe Command(string name) => new(EditorProbeKind.PathCommand, name);

    private static IReadOnlyList<EditorProbe> Bundle(string name)
        => [At($"/Applications/{name}.app"), At($"~/Applications/{name}.app")];

    /// <summary>
    /// Every editor Hub can offer on this host: the catalog's hits, then — on macOS — the applications sitting in
    /// the two folders applications actually live in.
    ///
    /// Listing them is what makes "choose an application" one click again. A folder dialog is the only panel that
    /// can hand back a macOS bundle, and it opens the bundle as a folder (the reader sees <c>Contents</c>); the
    /// alternative, a file dialog, returns nothing at all (AvaloniaUI/Avalonia#18303). So the list carries them,
    /// and 浏览… stays as the way out for a bundle somewhere else. Never throws: detection is an offer.
    /// </summary>
    public static IReadOnlyList<EditorBinding> Detect()
    {
        var found = new List<EditorBinding>();
        foreach (var source in Sources)
        {
            if (Probes(source).Select(Find).FirstOrDefault(path => path is not null) is { } path)
            {
                found.Add(new EditorBinding(source.Preset, path));
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (var bundle in MacApplications())
            {
                // A catalog hit already names this path (VS Code found at /Applications/… is the same file the
                // listing would offer); the catalog's row wins because it carries the product's name.
                if (found.All(binding => !string.Equals(binding.Path, bundle, StringComparison.Ordinal)))
                {
                    found.Add(new EditorBinding(EditorPreset.Custom(bundle), bundle));
                }
            }
        }

        return found;
    }

    /// <summary>Application bundles under <c>/Applications</c> and <c>~/Applications</c>, alphabetically.</summary>
    private static List<string> MacApplications()
    {
        string[] folders =
        [
            "/Applications",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        ];
        var bundles = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    bundles.AddRange(Directory.EnumerateDirectories(folder, "*.app"));
                }
            }
            catch (Exception)
            {
                // An unreadable Applications folder costs rows in a dropdown, not the settings page.
            }
        }

        bundles.Sort(StringComparer.OrdinalIgnoreCase);
        return bundles;
    }

    /// <summary>The preset behind a saved choice; an id the catalog does not know becomes a custom preset.</summary>
    public static EditorPreset Preset(CodeEditor editor)
        => Sources.Select(source => source.Preset).FirstOrDefault(preset => preset.Id == editor.Id)
            ?? EditorPreset.Custom(editor.Path);

    /// <summary>
    /// A saved choice made usable, or null when nothing launchable is at that path. A browsed-in application and a
    /// catalog row go through the same test: existence plus a shape the launch convention can actually start.
    ///
    /// The path is folded up to its enclosing bundle first, so a selection that landed inside
    /// <c>My Editor.app/Contents/…</c> — which is what a picker that treats bundles as folders returns — still
    /// resolves to the application that has to be started through LaunchServices.
    /// </summary>
    public static EditorBinding? Bind(CodeEditor? editor)
    {
        if (editor is null)
        {
            return null;
        }

        var path = ApplicationBundle(editor.Path) ?? editor.Path;
        return Accepts(path) ? new EditorBinding(Preset(editor), path) : null;
    }

    /// <summary>The outermost <c>.app</c> bundle containing <paramref name="path"/>, or null when there is none.</summary>
    public static string? ApplicationBundle(string path)
    {
        var candidate = new DirectoryInfo(Path.TrimEndingDirectorySeparator(path));
        while (candidate is not null && !candidate.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Parent;
        }

        return candidate?.FullName;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is something this host can start: an existing file, or an existing directory
    /// that is an application bundle. A plain directory — the project folder itself, caught by a stray drop — is not
    /// an editor, and starting one is the error this rule exists to prevent.
    /// </summary>
    public static bool Accepts(string? path)
        => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || (IsBundle(path) && Directory.Exists(path)));

    /// <summary>Whether the path names a macOS application bundle (<c>.app</c>), which decides the launch convention.</summary>
    public static bool IsBundle(string path)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    /// <summary>The convention <paramref name="path"/> demands. The path decides, never the product.</summary>
    public static EditorLaunch LaunchFor(string path) => IsBundle(path) ? EditorLaunch.MacBundle : EditorLaunch.Executable;

    /// <summary>
    /// What to start, and with what.
    ///
    /// The folder is a **positional** argument either way. devenv's own usage line is
    /// "devenv [solutionfile | projectfile | folder | anyfile.ext]" and it has no <c>/OpenFolder</c> switch —
    /// passing one aborts with "Invalid Command Line. Unknown Switch" before the IDE ever opens. A macOS bundle
    /// cannot be executed at all, so LaunchServices takes it (<c>-a &lt;bundle&gt;</c>) and the folder follows as
    /// the document to open.
    /// </summary>
    public static (string FileName, string[] Arguments) LaunchPlan(string path, string folder)
        => LaunchFor(path) == EditorLaunch.MacBundle
            ? ("/usr/bin/open", ["-a", path, folder])
            : (path, [folder]);


    /// <summary>
    /// Which editor a request uses: the project's own choice, then the reserved per-target-family slot, then the
    /// one global default, then the first editor detected.
    ///
    /// <paramref name="family"/> and <paramref name="familyOverrides"/> are wired without a UI. The chain is where
    /// "Android always opens in Android Studio" would live if it is ever asked for, and having the shape now is
    /// what keeps that request from turning into a second settings slot.
    ///
    /// A saved choice that no longer resolves (application deleted, path moved) falls through rather than sticking:
    /// answering "not selected" forever is what the old two-slot model did.
    /// </summary>
    public static EditorBinding? Resolve(ProjectEntry? project, IReadOnlyList<EditorBinding> detected, HubState state,
        string? family = null, IReadOnlyDictionary<string, CodeEditor>? familyOverrides = null)
    {
        if (Bind(project?.CodeEditor) is { } own)
        {
            return own;
        }

        var familyEditor = family is not null ? familyOverrides?.GetValueOrDefault(family) : null;
        if (Bind(familyEditor) is { } familyBinding)
        {
            return familyBinding;
        }

        if (Bind(state.CodeEditor) is { } global)
        {
            return global;
        }

        return detected.FirstOrDefault(binding => Accepts(binding.Path));
    }

    private static IReadOnlyList<EditorProbe> Probes(EditorSource source)
        => OperatingSystem.IsWindows() ? source.Windows : OperatingSystem.IsMacOS() ? source.Mac : source.Linux;

    private static string? Find(EditorProbe probe) => probe.Kind switch
    {
        EditorProbeKind.Path => Expand(probe.Value) is var path && Accepts(path) ? path : null,
        EditorProbeKind.PathCommand => OnPath(probe.Value),
        EditorProbeKind.VisualStudio => VisualStudioDevEnv(),
        _ => null,
    };

    private static string Expand(string path)
        => Environment.ExpandEnvironmentVariables(
            path.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
                : path);

    private static string? OnPath(string command) => CommandShells.Resolve(command);

    private static string? VisualStudioDevEnv()
    {
        // The installer is 32-bit on every host, which is the only reason ProgramFiles(x86) appears here; what it
        // reports is the devenv.exe of the installed version — a 64-bit binary, since 2026 has no 32-bit one.
        var installer = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(installer, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        try
        {
            using var query = Process.Start(new ProcessStartInfo(vswhere)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                ArgumentList = { "-latest", "-products", "*", "-property", "productPath" },
            });
            if (query is null)
            {
                return null;
            }

            var reported = query.StandardOutput.ReadToEnd().Trim();
            query.WaitForExit(4000);
            return Accepts(reported) ? reported : null;
        }
        catch (Exception)
        {
            // A machine without Visual Studio, a locked installer directory and a truncated vswhere all mean the same
            // thing to the user: not found here. Detection must never turn into an error dialog.
            return null;
        }
    }
}

/// <summary>
/// What an editor is pointed at — a separate question from which editor, and the reason "choose by build target"
/// is the wrong shape: every target edits the same sources, only the entry point differs. <c>proj.android</c> is
/// the Gradle project Android Studio can actually open; the project root is the CMake tree every other editor
/// takes, and stays the answer for a project that never shipped the Android template.
/// </summary>
public static class EditorTargets
{
    public static string Default(ProjectEntry project)
    {
        if (BuildTargets.Get(project.Platform).Family != "android")
        {
            return project.Path;
        }

        var gradle = Path.Combine(project.Path, "proj.android");
        return Directory.Exists(gradle) ? gradle : project.Path;
    }
}
