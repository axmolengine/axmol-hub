using AxmolHub.Core;

internal static class AiWorkspaceChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // The sandbox is a pure function over strings plus one link check, so it is asserted here rather than
        // through a screen: a guardrail that only exists in the UI layer does not exist in `full` mode.
        var guardRoot = Path.Combine(root, "guard");
        // A junction left behind by an earlier failed run makes Directory.Delete(recursive: true) throw
        // UnauthorizedAccessException, so the link goes first — otherwise one red run poisons the next.
        var staleLink = Path.Combine(guardRoot, "ws", "link");
        if (Directory.Exists(staleLink))
            Directory.Delete(staleLink, recursive: false);
        if (Directory.Exists(guardRoot))
            Directory.Delete(guardRoot, recursive: true);
        var workspace = Path.Combine(guardRoot, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        Directory.CreateDirectory(Path.Combine(workspace, "hubdata"));
        Directory.CreateDirectory(Path.Combine(workspace, "engine-2.11.5", "core"));
        File.WriteAllText(Path.Combine(workspace, "src", "hello.cpp"), "int main() { return 0; }\r\n");
        var guards = new WorkspaceGuards(Path.Combine(workspace, "hubdata"), [Path.Combine(workspace, "engine-2.11.5")]);
        var allowed = WorkspacePaths.ResolveRead(workspace, "src/hello.cpp", guards);
        if (!allowed.IsAllowed || allowed.Full != Path.Combine(workspace, "src", "hello.cpp") || allowed.Relative != Path.Combine("src", "hello.cpp"))
            throw new Exception($"A plain relative path was not resolved inside the workspace ({allowed.Verdict}).");
        // Forward slashes are what a model emits; they must resolve without becoming a second code path.
        if (!WorkspacePaths.ResolveRead(workspace, "src/hello.cpp", guards).IsAllowed)
            throw new Exception("A forward-slash relative path was refused.");
        // Two refusals, not one. A relative spelling that climbs out is a malformed path; an absolute one is a
        // well-formed path in a place this session does not hold, and the way out differs — rewrite the path versus
        // ask for the directory (request_directory) — so the model has to be told which of the two it hit.
        foreach (var escaping in new[]
        {
            "../outside.cpp",
            "src/../../outside.cpp"
        }

        )
        {
            var verdict = WorkspacePaths.ResolveWrite(workspace, escaping, guards).Verdict;
            if (verdict != WorkspacePathVerdict.EscapesWorkspace)
                throw new Exception($"'{escaping}' was answered with {verdict} instead of EscapesWorkspace.");
        }

        // An empty path names nothing, which is not an escape: its sentence has to say "name the file", because
        // the escaping one sends a model off asking for a workspace it already has.
        if (WorkspacePaths.ResolveWrite(workspace, "", guards).Verdict != WorkspacePathVerdict.NoPath
            || !WorkspacePaths.ResultFor(WorkspacePathVerdict.NoPath, "").Contains("empty or missing", StringComparison.Ordinal))
            throw new Exception("An empty path was not answered with NoPath and the sentence that says so.");

        foreach (var outside in new[]
        {
            "/etc/passwd",
            "C:\\Windows\\win.ini"
        }

        )
        {
            var verdict = WorkspacePaths.ResolveWrite(workspace, outside, guards).Verdict;
            if (verdict != WorkspacePathVerdict.OutsideRoots)
                throw new Exception($"'{outside}' was answered with {verdict} instead of OutsideRoots.");
        }

        // Protected roots are refused identically by every verb that reads: the one rule no approval mode relaxes.
        foreach (var path in new[]
        {
            ".git/config",
            "hubdata/state.json",
            "engine-2.11.5/core/axmol.h"
        }

        )
        {
            if (WorkspacePaths.ResolveWrite(workspace, path, guards).Verdict != WorkspacePathVerdict.ProtectedRoot || WorkspacePaths.ResolveRead(workspace, path, guards).Verdict != WorkspacePathVerdict.ProtectedRoot)
                throw new Exception($"'{path}' was not refused as a protected root by both verbs.");
        }

        // …and that floor is a *writing* one now. A card that names the location itself opens it for reading — the
        // analysis the assistant is actually asked for — while a write and a command's directory stay refused, and
        // a grant made above the location opens nothing inside it: here the workspace contains the data root, and
        // the data root is still refused through the workspace's own spelling.
        var hubData = Path.Combine(workspace, "hubdata");
        var stateFile = Path.Combine(hubData, "state.json");
        File.WriteAllText(stateFile, "{}\n");
        var readOnly = new WorkspaceSandbox(workspace, [], [hubData]);
        if (WorkspacePaths.ResolveRead(readOnly, stateFile, guards).Verdict != WorkspacePathVerdict.Allowed
            || WorkspacePaths.ResolveWrite(readOnly, stateFile, guards).Verdict != WorkspacePathVerdict.ReadOnlyRoot
            || WorkspacePaths.VerifyCommandRoot(readOnly, hubData, guards) != WorkspacePathVerdict.ReadOnlyRoot
            || WorkspacePaths.ResolveRead(WorkspaceSandbox.For(workspace), stateFile, guards).Verdict != WorkspacePathVerdict.ProtectedRoot)
            throw new Exception("A read-only grant of a protected location did not answer reading yes and writing no.");
        // A directory verb exists for the read-only look-around tools, and it carries the same rule: listing is
        // reading. The workspace root itself being protected is refused too, because a search started there would
        // walk out of the sandbox one file at a time.
        if (WorkspacePaths.ResolveDirectory(workspace, "hubdata", guards).Verdict != WorkspacePathVerdict.ProtectedRoot || WorkspacePaths.ResolveDirectory(Path.Combine(workspace, "hubdata"), "sub", guards).Verdict != WorkspacePathVerdict.ProtectedRoot || WorkspacePaths.ResolveDirectory(Path.Combine(workspace, "engine-2.11.5"), "", guards).Verdict != WorkspacePathVerdict.ProtectedRoot)
            throw new Exception("A protected directory was walkable by the read-only tools.");
        foreach (var escaping in new[]
        {
            "../outside",
            "src/../../outside"
        }

        )
            if (WorkspacePaths.ResolveDirectory(workspace, escaping, guards).Verdict != WorkspacePathVerdict.EscapesWorkspace)
                throw new Exception($"'{escaping}' was not refused as escaping the workspace by the directory verb.");
        // …and an absolute path outside every root is its own answer for this verb too: the listing tools must say
        // "ask for that directory" rather than "your path is malformed".
        foreach (var outside in new[]
        {
            "/etc",
            "C:\\Windows"
        }

        )
            if (WorkspacePaths.ResolveDirectory(workspace, outside, guards).Verdict != WorkspacePathVerdict.OutsideRoots)
                throw new Exception($"'{outside}' was not refused as outside every root by the directory verb.");
        // An empty path is not a traversal bug, it is the question "what is in my project" — and both spellings of
        // the root have to answer it the same way, or the model learns to probe.
        if (!WorkspacePaths.ResolveDirectory(workspace, "", guards).IsAllowed || !WorkspacePaths.ResolveDirectory(workspace, ".", guards).IsAllowed || !WorkspacePaths.ResolveDirectory(workspace, "./", guards).IsAllowed)
            throw new Exception("The workspace root itself was refused as a directory to list.");
        if (WorkspacePaths.ResolveDirectory(workspace, "src/hello.cpp", guards).Verdict != WorkspacePathVerdict.NotADirectory || WorkspacePaths.ResolveDirectory(workspace, "nowhere", guards).Verdict != WorkspacePathVerdict.NotADirectory)
            throw new Exception("A file, or nothing at all, was accepted as a directory to search.");
        if (WorkspacePaths.ResolveDirectory(WorkspaceSandbox.For(null), "src", guards).Verdict != WorkspacePathVerdict.NoWorkspace || WorkspacePaths.ResolveDirectory(Path.Combine(guardRoot, "nope"), "src", guards).Verdict != WorkspacePathVerdict.MissingWorkspace)
            throw new Exception("The directory verb did not answer a missing workspace in words.");
        // The allowlist is a WRITE rule: reading a build log is legitimate, writing one is not.
        File.WriteAllText(Path.Combine(workspace, "src", "build.log"), "error: nope\n");
        if (WorkspacePaths.ResolveRead(workspace, "src/build.log", guards).Verdict != WorkspacePathVerdict.Allowed)
            throw new Exception("A build log inside the workspace could not be read.");
        foreach (var blocked in new[]
        {
            "src/build.log",
            "src/tool.exe",
            "src/notes.nitwit"
        }

        )
            if (WorkspacePaths.ResolveWrite(workspace, blocked, guards).Verdict != WorkspacePathVerdict.ExtensionNotAllowed)
                throw new Exception($"Writing '{blocked}' was not refused by the extension allowlist.");
        // A file that is absent is a write target, not a read target; a directory is neither.
        if (WorkspacePaths.ResolveWrite(workspace, "src/new.cpp", guards).Verdict != WorkspacePathVerdict.Allowed)
            throw new Exception("A new .cpp file was refused as a write target.");
        if (WorkspacePaths.ResolveRead(workspace, "src/new.cpp", guards).Verdict != WorkspacePathVerdict.NotAFile || WorkspacePaths.ResolveRead(workspace, "src", guards).Verdict != WorkspacePathVerdict.NotAFile || WorkspacePaths.ResolveWrite(workspace, "src", guards).Verdict != WorkspacePathVerdict.NotAFile)
            throw new Exception("A missing file or a directory was not reported as NotAFile.");
        // Names without a usable extension are still writable when the name itself is known build metadata.
        foreach (var name in new[]
        {
            "CMakeLists.txt",
            ".gitignore",
            "Makefile"
        }

        )
            if (WorkspacePaths.ResolveWrite(workspace, name, guards).Verdict != WorkspacePathVerdict.Allowed)
                throw new Exception($"{name} was refused as a write target.");
        // Every platform's project format is text, and the extension is what says so. An assistant that cannot write
        // a .pbxproj cannot help with the iOS build it is being shown, and a .axaml is Hub's own UI language. An .svg
        // is on the list because a vector document reaches a model as markup: no vision endpoint takes one as an image.
        foreach (var text in new[]
        {
            "src/ui.axaml",
            "src/Main.xaml",
            "ios/App.xcconfig",
            "ios/project.pbxproj",
            "ios/App.entitlements",
            "ios/LaunchScreen.storyboard",
            "ios/Icon.xib",
            "ios/en.strings",
            "ios/plural.stringsdict",
            "android/callback.aidl",
            "qt/app.pro",
            "server/app.cfg",
            "email/welcome.tmpl",
            "assets/icon.svg",
            "AxmolHub.slnx",
        }

        )
            if (WorkspacePaths.ResolveWrite(workspace, text, guards).Verdict != WorkspacePathVerdict.Allowed)
                throw new Exception($"{text} was refused as a write target even though it is a text project file " + $"({WorkspacePaths.ResolveWrite(workspace, text, guards).Verdict}).");
        // Exact match, not a prefix: a made-up extension that merely starts like a known one is still unknown, and a
        // prefix rule would let a binary masquerade as a project file.
        foreach (var invented in new[]
        {
            "src/bad.axamlx",
            "ios/bad.pbxproxx",
            "app.cfgg"
        }

        )
            if (WorkspacePaths.ResolveWrite(workspace, invented, guards).Verdict != WorkspacePathVerdict.ExtensionNotAllowed)
                throw new Exception($"{invented} was writable on a prefix match rather than on its extension.");
        // A directory the project has not grown yet is not a link. Asking a missing ancestor for its attributes answers
        // with an error value whose bits include the reparse flag, which refused every new file in a new folder — the
        // junction assertion below is the same rule with a link that really is there.
        if (WorkspacePaths.ResolveWrite(workspace, "platform/ios/App.entitlements", guards).Verdict != WorkspacePathVerdict.Allowed || WorkspacePaths.ResolveWrite(workspace, "android/new/api.aidl", guards).Verdict != WorkspacePathVerdict.Allowed)
            throw new Exception("A new file under a directory that does not exist yet was refused as a write target.");
        if (WorkspacePaths.ResolveRead(WorkspaceSandbox.For(null), "src/hello.cpp", guards).Verdict != WorkspacePathVerdict.NoWorkspace || WorkspacePaths.ResolveRead(Path.Combine(guardRoot, "nope"), "a.cpp", guards).Verdict != WorkspacePathVerdict.MissingWorkspace)
            throw new Exception("A missing workspace was not reported as such.");
        if (WorkspacePaths.VerifyCommandRoot(null, guards) != WorkspacePathVerdict.NoWorkspace || WorkspacePaths.VerifyCommandRoot(workspace, guards) != WorkspacePathVerdict.Allowed || WorkspacePaths.VerifyCommandRoot(Path.Combine(workspace, "hubdata"), guards) != WorkspacePathVerdict.ProtectedRoot)
            throw new Exception("The command root was not verified against the same guards.");
        // The refusal text is part of the contract: a sentence without "do not retry" buys nine more attempts.
        foreach (var verdict in new[]
        {
            WorkspacePathVerdict.NoWorkspace,
            WorkspacePathVerdict.EscapesWorkspace,
            WorkspacePathVerdict.OutsideRoots,
            WorkspacePathVerdict.ProtectedRoot,
            WorkspacePathVerdict.ReparsePoint,
            WorkspacePathVerdict.ExtensionNotAllowed,
            WorkspacePathVerdict.NotADirectory,
        }

        )
        {
            var sentence = WorkspacePaths.ResultFor(verdict, "x");
            if (sentence.Length == 0 || !sentence.Contains("retry", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"The refusal for {verdict} does not tell the model to stop retrying.");
        }

        // The two sentences that used to send the model at the tool this build deleted. The door is the person's own
        // menu now, and a refusal that names a call the model cannot make is exactly the one it retries.
        foreach (var verdict in new[]
        {
            WorkspacePathVerdict.NoWorkspace,
            WorkspacePathVerdict.MissingWorkspace
        }

        )
        {
            var sentence = WorkspacePaths.ResultFor(verdict, "x");
            if (sentence.Contains("set_workspace", StringComparison.Ordinal) || !sentence.Contains("workspace menu", StringComparison.Ordinal))
                throw new Exception($"The refusal for {verdict} still points at a tool the model no longer has.");
        }

        Console.WriteLine("PASS: workspace paths stay inside the sandbox and refuse protected roots.");
        if (OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(guardRoot, "outside");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(workspace, "link");
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true })!)
                mklink.WaitForExit();
            if (!Directory.Exists(link))
                throw new Exception("The junction fixture could not be created.");
            try
            {
                // SafePath only normalizes strings, so containment passes; the link walk is what catches this.
                if (WorkspacePaths.ResolveWrite(workspace, "link/evil.cpp", guards).Verdict != WorkspacePathVerdict.ReparsePoint || WorkspacePaths.ResolveRead(workspace, "link/evil.cpp", guards).Verdict != WorkspacePathVerdict.ReparsePoint)
                    throw new Exception("A write through a directory junction was not refused.");
                // The same rule with a missing directory under the link: skipping the attributes of ancestors that are
                // not there must not skip the ones above them, or a new file would escape through the junction.
                if (WorkspacePaths.ResolveWrite(workspace, "link/deeper/still-deeper/evil.cpp", guards).Verdict != WorkspacePathVerdict.ReparsePoint)
                    throw new Exception("A new file under a junction was not refused as a write through a link.");
            }
            finally
            {
                // Removed even when the assertion above fails: a dangling junction breaks the next run's cleanup.
                if (Directory.Exists(link))
                    Directory.Delete(link, recursive: false);
            }

            Console.WriteLine("PASS: a directory junction inside the workspace cannot be written through.");
        }

        // The anchored edit: every verdict is a different thing the model must do next, so each is asserted.
        if (FileEdit.Apply(null, "", "hello\n", false)is not { Verdict: FileEditVerdict.Created, Updated: "hello\n" })
            throw new Exception("Creating a file with an empty anchor did not report Created with LF endings.");
        if (FileEdit.Apply(null, "", "a\r\nb\r\n", false)is not { Verdict: FileEditVerdict.Created, Updated: "a\nb\n" })
            throw new Exception("Creating a file from a CRLF body kept the CRLF.");
        if (FileEdit.Apply(null, "anchor", "x", false).Verdict != FileEditVerdict.NotFound)
            throw new Exception("An anchor against a missing file was not NotFound.");
        if (FileEdit.Apply("existing", "", "x", false).Verdict != FileEditVerdict.AlreadyExists)
            throw new Exception("Creating over an existing file was not refused.");
        if (FileEdit.Apply("a\nb\nc\n", "zzz", "y", false).Verdict != FileEditVerdict.NotFound)
            throw new Exception("A missing anchor was not NotFound.");
        if (FileEdit.Apply("a\nb\na\n", "a", "z", false)is not { Verdict: FileEditVerdict.Ambiguous, Matches: 2 })
            throw new Exception("Two matches without replace_all was not Ambiguous.");
        if (FileEdit.Apply("a\nb\na\n", "a", "z", true)is not { Verdict: FileEditVerdict.Applied, Updated: "z\nb\nz\n", Matches: 2 })
            throw new Exception("replace_all did not replace every match.");
        if (FileEdit.Apply("a\nb\n", "b", "b", false).Verdict != FileEditVerdict.Unchanged)
            throw new Exception("An identical replacement was not Unchanged.");
        // Models emit \n; the file is CRLF. The edit must land, and what it writes must be LF end to end — the
        // lines the edit never touched are rewritten too, because a file split across two newlines is a file where
        // every later anchor has to guess.
        const string crlfFile = "int main() {\r\n    return 0;\r\n}\r\n";
        var crlfEdit = FileEdit.Apply(crlfFile, "    return 0;\n", "    return 1;\n", false);
        if (crlfEdit.Verdict != FileEditVerdict.Applied || crlfEdit.Updated != "int main() {\n    return 1;\n}\n")
            throw new Exception($"A CRLF file was not edited into LF ({crlfEdit.Verdict}: {crlfEdit.Updated.Replace("\r", "\\r")}).");
        if (crlfEdit.Updated.Contains('\r'))
            throw new Exception("An edit left a stray \r outside the replaced span.");
        // A refusal normalizes nothing, since the write tool refuses by handing back the text it was given and the
        // caller writes no bytes: a no-op edit against a CRLF file must find that file still CRLF.
        const string crlfBody = "a\r\nb\r\na\r\n";
        if (FileEdit.Apply(crlfBody, "zzz", "y", false).Updated != crlfBody || FileEdit.Apply(crlfBody, "a", "z", false).Updated != crlfBody || FileEdit.Apply(crlfBody, "b", "b", false).Updated != crlfBody || FileEdit.Apply(crlfBody, "", "y", false).Updated != crlfBody)
            throw new Exception("A refused or no-op edit rewrote the CRLF file it was not allowed to change.");
        if (!FileEdit.ResultFor(FileEditVerdict.Ambiguous, 3, "src/a.cpp").Contains("3 places"))
            throw new Exception("The ambiguous refusal does not say how many places matched.");
        Console.WriteLine("PASS: an anchored edit lands once, loudly, and what it writes is LF end to end.");
        // The check reports go out through the same policy: File.WriteAllLines joined them with the host's newline,
        // so three hosts produced three byte streams from one run. The trailing newline it added is kept.
        var reportEol = Path.Combine(guardRoot, "eol-report.txt");
        TextEol.WriteLines(reportEol, ["one\r\ntwo", "three"]);
        if (File.ReadAllText(reportEol) != "one\ntwo\nthree\n")
            throw new Exception($"A check report did not come out LF ('{File.ReadAllText(reportEol).Replace("\r", "\\r")}').");
        Console.WriteLine("PASS: a check report is LF, an embedded newline included, and still ends with one.");
        // Shell choice is per host and must be assertable on any host, so it is a pure function of two values.
        var windows = CommandShells.For("windows", pwshAvailable: false);
        // WindowsShell.PowerShell mixes separators on purpose (Path.Combine over a literal with '/'), so compare
        // normalized rather than asserting a spelling the Hub itself does not use.
        if (!windows.Executable.Replace('/', '\\').EndsWith("\\WindowsPowerShell\\v1.0\\powershell.exe", StringComparison.OrdinalIgnoreCase) || !windows.PrefixArguments.SequenceEqual(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"]))
            throw new Exception($"Windows did not get the system PowerShell with Bypass: {windows.Executable} {string.Join(' ', windows.PrefixArguments)}");
        var unixPwsh = CommandShells.For("linux", pwshAvailable: true);
        if (unixPwsh.Executable != "pwsh" || unixPwsh.PrefixArguments.Contains("-ExecutionPolicy") || !unixPwsh.PrefixArguments.SequenceEqual(["-NoProfile", "-NonInteractive", "-Command"]))
            throw new Exception("Unix pwsh was given Windows-only arguments.");
        var unixSh = CommandShells.For("macos", pwshAvailable: false);
        if (unixSh.Executable != "/bin/sh" || !unixSh.PrefixArguments.SequenceEqual(["-c"]))
            throw new Exception("Unix without pwsh did not fall back to /bin/sh -c.");
        // PowerShell 7 wins on Windows too when it is installed, and it keeps the same guard story as 5.1 —
        // Bypass included, because a script file is still a script file.
        var windowsPwsh = CommandShells.For("windows", pwshAvailable: true);
        if (windowsPwsh.Executable != "pwsh" || windowsPwsh.Label != "PowerShell 7" || !windowsPwsh.PrefixArguments.SequenceEqual(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"]) || windowsPwsh.Equals(windows))
            throw new Exception($"Windows with pwsh installed did not switch to PowerShell 7: {windowsPwsh.Label}.");
        if (!OperatingSystem.IsWindows() && CommandShells.PwshAvailable() != (CommandShells.Resolve("pwsh")is not null))
            throw new Exception("The pwsh probe and the selection disagree on this host.");
        // On Windows the name on PATH is `pwsh.exe`, so a bare-name probe that only matches the exact spelling finds
        // nothing on a machine that has PowerShell 7 installed — and the tool description then promises a shell the
        // session never gets. Only assertable where pwsh actually is, which is why it is written as a one-way rule.
        if (OperatingSystem.IsWindows() && CommandShells.Resolve("pwsh.exe")is not null && CommandShells.Resolve("pwsh")is null)
            throw new Exception("The pwsh probe did not match the pwsh.exe that is installed on this machine.");
        if (OperatingSystem.IsWindows() != windows.Executable.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            throw new Exception("The Windows tier is not the PowerShell that ships with Windows.");
        // ForCurrent is the same pure rule applied to the machine it runs on, and on Windows it must land on a
        // PowerShell tier — a host that falls through to /bin/sh would be handed a script it cannot parse.
        var current = CommandShells.ForCurrent();
        var expected = CommandShells.For(BuildTargets.Host, CommandShells.PwshAvailable());
        if (current.Executable != expected.Executable || current.Label != expected.Label)
            throw new Exception($"ForCurrent disagreed with the host it is running on: {current.Executable}.");
        if (OperatingSystem.IsWindows() && !current.IsPowerShell)
            throw new Exception("A Windows run_command got a shell that is not PowerShell.");
        // Both PowerShell tiers get told to write UTF-8, because their own default is the machine's OEM codepage
        // while the transcript is decoded as UTF-8. The command still runs — it is prefixed, not replaced — and
        // /bin/sh gets the command untouched, since that preamble is not shell syntax it understands.
        if (!windows.ArgumentsFor("Get-Location").SequenceEqual([..windows.PrefixArguments, CommandShell.Utf8Preamble + ";Get-Location"]) || !windowsPwsh.ArgumentsFor("ls").SequenceEqual([..windowsPwsh.PrefixArguments, CommandShell.Utf8Preamble + ";ls"]) || !unixPwsh.ArgumentsFor("ls").SequenceEqual([..unixPwsh.PrefixArguments, CommandShell.Utf8Preamble + ";ls"]) || !unixSh.ArgumentsFor("ls").SequenceEqual(["-c", "ls"]))
            throw new Exception($"The UTF-8 preamble is not applied exactly where it belongs:{Environment.NewLine}" + string.Join(Environment.NewLine, new[] { windows, windowsPwsh, unixPwsh, unixSh }.Select(shell => $"{shell.Label}: {string.Join(' ', shell.ArgumentsFor("ls"))}")));
        if (!windows.ArgumentsFor("dir")[^1].EndsWith("dir", StringComparison.Ordinal) || !unixSh.ArgumentsFor("dir").SequenceEqual(["-c", "dir"]))
            throw new Exception("A shell's own arguments swallowed the command it was given.");
        Console.WriteLine("PASS: one shell per host, with the encoding preamble only where the shell speaks it.");
        // The blank rule exists because a solid-colour frame still passes "the file was written". These buffers are
        // the three answers it must give, and they are made of bytes rather than of a rendered window: the criterion
        // is arithmetic, so asserting it does not need the renderer that produced the picture.
        const int side = 64;
        var rowBytes = side * 4;
        var solid = new byte[rowBytes * side];
        for (var index = 0; index < solid.Length; index += 4)
        {
            solid[index] = 30;
            solid[index + 1] = 30;
            solid[index + 2] = 30;
            solid[index + 3] = 255;
        }

        var solidFrame = FrameAnalysis.Analyze(solid, side, side, rowBytes);
        if (!solidFrame.IsBlank() || solidFrame.DistinctColors != 1)
            throw new Exception($"A solid-colour frame was not judged blank ({solidFrame}).");
        // Black background, white bars: exactly the two-colour UI that an earlier lower bound of "eight distinct
        // colours" called blank. The variance is the primary criterion; the colour count is only the weak fallback.
        var striped = new byte[rowBytes * side];
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var offset = (y * rowBytes) + (x * 4);
                var level = (byte)((x + y) % 8 < 4 ? 240 : 12);
                striped[offset] = level;
                striped[offset + 1] = level;
                striped[offset + 2] = level;
                striped[offset + 3] = 255;
            }

        var stripedFrame = FrameAnalysis.Analyze(striped, side, side, rowBytes);
        if (stripedFrame.IsBlank() || stripedFrame.DistinctColors != 2)
            throw new Exception($"A two-colour UI frame was misjudged as blank ({stripedFrame}).");
        if (!FrameAnalysis.Analyze([], 0, 0, 0).IsBlank() || !FrameAnalysis.Analyze(solid, 0, side, rowBytes).IsBlank())
            throw new Exception("A frame with no size was not reported as blank.");
        Console.WriteLine("PASS: a frame is blank by luminance variance, and a two-colour UI is not.");
        // Capture backends dispatch the same pure way the shells do, so the platform answer is assertable here even
        // though only one of these hosts is the machine running the check.
        var windowsCapture = CaptureBackends.For("windows", grimAvailable: false);
        var macCapture = CaptureBackends.For("macos", grimAvailable: false);
        var linuxGrim = CaptureBackends.For("linux", grimAvailable: true);
        var linuxBare = CaptureBackends.For("linux", grimAvailable: false);
        var unknownCapture = CaptureBackends.For("qnx", grimAvailable: false);
        if (windowsCapture.Id != CaptureBackends.GdiPrintWindow || !windowsCapture.Available || macCapture.Id != CaptureBackends.MacOsScreenshot || !macCapture.Available || linuxGrim.Id != CaptureBackends.LinuxGrim || !linuxGrim.Available)
            throw new Exception($"A host was not dispatched to its capture backend: {windowsCapture} · {macCapture} · {linuxGrim}");
        // A Linux session without grim, or an unknown host, answers with a sentence about the missing tool rather
        // than spawning a process that cannot start: the model has to be told what the user can actually install.
        if (linuxBare.Available || unknownCapture.Available)
            throw new Exception("A host with no capture tool offered a backend it cannot use.");
        foreach (var unusable in new[]
        {
            linuxBare,
            unknownCapture
        }

        )
            if (string.IsNullOrEmpty(unusable.Refusal) || !unusable.Refusal.Contains("retry", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"A capture refusal does not tell the model to stop retrying: {unusable.Refusal}");
        var currentCapture = CaptureBackends.ForCurrent();
        var expectedCapture = CaptureBackends.For(BuildTargets.Host, CommandShells.Resolve("grim")is not null);
        if (currentCapture.Id != expectedCapture.Id || currentCapture.Label != expectedCapture.Label)
            throw new Exception($"ForCurrent disagreed with the host it is running on: {currentCapture.Id}.");
        Console.WriteLine("PASS: each host picks one capture backend, and an unusable one says so in a refusal.");
        // ── the repository read: the parse first, on git's own bytes, then one live repository ──
        // The pane shows these facts to a person deciding whether to approve a checkout, so both halves are
        // asserted: the parse against records transcribed from measured `git status --porcelain=v1 -z -b` output
        // (including the two shapes that break a naive split — a rename whose origin is the next NUL field, and a
        // CJK path that stays whole only because -z never quotes), and one real repository read through the real
        // git, so the parse cannot drift from the format it claims to implement.
        // Six, not seven: the rename's origin is eaten, and an ignored file is not a change anybody is asked to
        // review — a user's status.showIgnored=true would otherwise put build output on the list. The origin is
        // spelled `A␠␠gone.txt` on purpose: a bare path would fail the record guard and be skipped by accident,
        // which is how this cell would pass while the rename's second field was being read as a change of its own.
        var statusReading = GitRepository.ParseStatus("## master...origin/master [ahead 2, behind 1]\0M  one.txt\0 M late.txt\0" + "R  renamed.txt\0A  gone.txt\0A  sub/deep.txt\0D  目录/说明.md\0?? untracked.txt\0!! ignored.txt\0");
        if (statusReading.BranchHeader is not { Length: > 0 } repoHeader || !repoHeader.Contains("ahead 2", StringComparison.Ordinal) || statusReading.Entries.Count != 6 || statusReading.Truncated)
            throw new Exception($"The porcelain parse read {statusReading.Entries.Count} entries from a known fixture.");
        var repoPaths = statusReading.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        if (!repoPaths.ContainsKey("renamed.txt") || repoPaths.ContainsKey("gone.txt") || repoPaths["renamed.txt"].Untracked || repoPaths["renamed.txt"].Mark != "R " || !repoPaths["untracked.txt"].Untracked || repoPaths["untracked.txt"].Mark != "??" || repoPaths["late.txt"].Mark != " M" || !repoPaths.ContainsKey("目录/说明.md") || repoPaths.ContainsKey("ignored.txt"))
            throw new Exception("A porcelain record shape was parsed into the wrong row: " + string.Join(",", statusReading.Entries.Select(entry => entry.Mark + entry.RelativePath)));
        var repoBranch = GitRepository.ParseBranch("## master...origin/master [ahead 2, behind 1]");
        var repoDetached = GitRepository.ParseBranch("## HEAD (no branch)");
        var repoNoUpstream = GitRepository.ParseBranch("## master");
        if (repoBranch.Name != "master" || repoBranch.Ahead != 2 || repoBranch.Behind != 1 || repoBranch.Detached || !repoDetached.Detached || repoDetached.Name != "HEAD (no branch)" || repoNoUpstream.Name != "master" || repoNoUpstream.Ahead != 0 || repoNoUpstream.Behind != 0)
            throw new Exception($"The branch line did not read as three facts: {repoBranch} / {repoDetached} / {repoNoUpstream}");
        // The bound is a product decision, not a constant nobody reaches: a repository with hundreds of dirty paths
        // has to say it was cut rather than show a page and imply it was the whole of it.
        var repoCrowded = GitRepository.ParseStatus(string.Concat(Enumerable.Range(0, 5).Select(i => $" M f{i}.txt\0")), maxEntries: 2);
        if (repoCrowded.Entries.Count != 2 || !repoCrowded.Truncated)
            throw new Exception("The entry bound was not enforced, or was enforced without saying so.");
        // Sections are paired with the name list by position. This fixture carries a path with a space — the case
        // where reading the `diff --git a/x b/y` header would be a guess — and a disagreement is reported as one.
        var patchFixture = "diff --git a/with space.txt b/with space.txt\nindex abc..def 100644\n" + "--- a/with space.txt\n+++ b/with space.txt\n@@ -1 +1 @@\n-q\n+q2\ndiff --git a/ok.txt b/ok.txt\n" + "--- a/ok.txt\n+++ b/ok.txt\n@@ -1 +1 @@\n-x\n+y\n";
        var repoSplit = GitRepository.SplitSections(patchFixture, ["with space.txt", "ok.txt"]);
        if (!repoSplit.Aligned || repoSplit.Sections.Count != 2 || !repoSplit.Sections["with space.txt"].Contains("+q2", StringComparison.Ordinal) || repoSplit.Sections["with space.txt"].Contains("diff --git a/ok.txt", StringComparison.Ordinal) || !repoSplit.Sections["ok.txt"].StartsWith("diff --git a/ok.txt", StringComparison.Ordinal))
            throw new Exception($"The patch was not cut at the file boundaries: {repoSplit.SectionCount} sections, " + $"{repoSplit.NameCount} names, [{string.Join(" | ", repoSplit.Sections.Values)}]");
        var repoSkewed = GitRepository.SplitSections(patchFixture, ["only-one.txt"]);
        if (repoSkewed.Aligned || repoSkewed.Sections.Count != 1 || !repoSkewed.Sections.ContainsKey("only-one.txt"))
            throw new Exception("A list and a patch that disagree were reported as aligned, or attributed at random.");
        Console.WriteLine("PASS: porcelain records, the branch line and a patch's file boundaries parse as git writes them.");
        // The same rules against the real thing. Guarded, because a host without git has to pass the suite with the
        // outcome it advertises rather than a repository nobody could look at.
        if (CommandShells.Resolve("git")is { } probeGit)
        {
            // git leaves objects read-only, and a recursive delete on Windows answers UnauthorizedAccessException to
            // that, so the fixture's own removal clears the attribute first. A red run must not poison the next one.
            static void WipeRepository(string directory)
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
            }

            var repoRoot = Path.Combine(root, "repo-probe");
            if (Directory.Exists(repoRoot))
                WipeRepository(repoRoot);
            Directory.CreateDirectory(repoRoot);
            File.WriteAllText(Path.Combine(repoRoot, "one.txt"), "one\n");
            File.WriteAllText(Path.Combine(repoRoot, "with space.txt"), "q\n");
            var probeRunner = new ProcessRunner(_ =>
            {
            });
            async Task Git(params string[] arguments)
            {
                var result = await probeRunner.RunAsync(probeGit, ["-c", "core.autocrlf=false", "-c", "core.quotepath=false", "--no-optional-locks", "--no-pager", ..arguments], repoRoot, timeout: TimeSpan.FromSeconds(20));
                if (result.ExitCode != 0)
                    throw new Exception($"git {string.Join(" ", arguments)} failed in the fixture repo: {result.Error}");
            }

            await Git("init", "-q");
            // Named identities rather than the machine's: a check that commits must not depend on whose laptop it ran,
            // and --allow-empty gives the baseline commit even though every file in here is still untracked.
            await Git("-c", "user.name=hub-check", "-c", "user.email=hub@example.invalid", "commit", "-q", "--allow-empty", "-m", "fixture: baseline");
            File.WriteAllText(Path.Combine(repoRoot, "one.txt"), "one\ntwo\n");
            File.WriteAllText(Path.Combine(repoRoot, "untracked 中文.txt"), "brand new\n");
            var repoState = await GitRepository.ReadAsync(repoRoot, probeRunner);
            if (repoState.Outcome != GitReadOutcome.Ok || repoState.Root is not { Length: > 0 } || !Directory.Exists(repoState.Root))
                throw new Exception($"A real repository did not read: {repoState.Outcome} · {repoState.Detail}");
            if (string.IsNullOrEmpty(repoState.Branch) || repoState.Truncated)
                throw new Exception($"A four-path repository read as branch 「{repoState.Branch}」 " + $"truncated={repoState.Truncated}.");
            var repoTracked = repoState.Entries.FirstOrDefault(entry => entry.RelativePath == "one.txt");
            var repoUntracked = repoState.Entries.FirstOrDefault(entry => entry.RelativePath == "untracked 中文.txt");
            if (repoTracked is null || repoUntracked is null || !repoUntracked.Untracked)
                throw new Exception("A real repository's dirty paths came back as: " + string.Join(",", repoState.Entries.Select(entry => entry.Mark + entry.RelativePath)));
            // A tracked change reads as a hunk and an untracked file as a whole file against nothing — the shape
            // ChatChanges already uses for a created file, so the two tabs cannot mean different things by it. The
            // untracked name carries a space and CJK, which is the encoding claim this whole read rests on.
            var repoTrackedDiff = GitRepository.DiffFor(repoState, repoTracked!);
            var repoUntrackedDiff = GitRepository.DiffFor(repoState, repoUntracked!);
            if (repoTrackedDiff is not { Length: > 0 } || !repoTrackedDiff.Contains("+two", StringComparison.Ordinal))
                throw new Exception($"The tracked file's diff was wrong: {repoTrackedDiff}");
            if (repoUntrackedDiff is not { Length: > 0 } || !repoUntrackedDiff.StartsWith("--- /dev/null", StringComparison.Ordinal) || !repoUntrackedDiff.Contains("brand new", StringComparison.Ordinal))
                throw new Exception($"The untracked file's diff was wrong: {repoUntrackedDiff}");
            // The two ways this read stops without anything to show are pane sentences, so they have to be outcomes.
            if ((await GitRepository.ReadAsync(null, probeRunner)).Outcome != GitReadOutcome.NoWorkspace)
                throw new Exception("A repository read with no workspace did not answer NoWorkspace.");
            // "Not a repository" is asserted outside this repository's own tree, because a folder under the check's
            // tmp root really is inside axmol-hub and git would answer with <i>its</i> status. Even there the
            // expectation is set by git rather than assumed: ask whether the directory has a repository, and claim
            // NotARepository only when it says it does not.
            var repoLoose = Path.Combine(Path.GetTempPath(), $"hub-not-a-repo-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repoLoose);
            var repoHasRepository = (await probeRunner.RunAsync(probeGit, ["rev-parse", "--show-toplevel"], repoLoose, timeout: TimeSpan.FromSeconds(10))).ExitCode == 0;
            var repoStopped = await GitRepository.ReadAsync(repoLoose, probeRunner);
            if (repoHasRepository && repoStopped.Outcome != GitReadOutcome.Ok)
                throw new Exception($"A directory that git says sits in a repository answered {repoStopped.Outcome}.");
            if (!repoHasRepository && (repoStopped.Outcome != GitReadOutcome.NotARepository || repoStopped.Detail is not { Length: > 0 }))
                throw new Exception($"A directory outside any repository answered {repoStopped.Outcome} · {repoStopped.Detail}");
            Directory.Delete(repoLoose);
            WipeRepository(repoRoot);
            Console.WriteLine("PASS: the repository tab's facts come from git itself, on a real repository.");
        }
        else
        {
            Console.WriteLine("PASS: no git on PATH — GitMissing is the outcome this host can show, and the parse still holds.");
        }

        Directory.Delete(guardRoot, recursive: true);
        return;
    }
}
