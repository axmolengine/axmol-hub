using System.Text.Json;
using AxmolHub.Core;
using static CheckSupport;

internal static class CliJsonChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var cliIndex = Array.IndexOf(args, "--check-cli-json");
        if (cliIndex + 1 >= args.Length || args[cliIndex + 1].StartsWith("--"))
            throw new ArgumentException("--check-cli-json requires the path to AxmolHub.Cli.dll or to the self-contained host executable.");
        var cliPath = Path.GetFullPath(args[cliIndex + 1]);
        if (!File.Exists(cliPath))
            throw new FileNotFoundException("Build src/AxmolHub.Cli first: the CLI artifact does not exist.", cliPath);
        // A framework-dependent build is a dll and needs dotnet to start; a self-contained build is itself the host
        // executable.
        var executable = Path.GetExtension(cliPath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : cliPath;
        string[] leading = executable == "dotnet" ? [cliPath] : [];
        var workspace = Path.GetFullPath(".");
        var scratch = Path.Combine(root, "cli-contract");
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);
        var transcript = new List<string>();
        var cli = new ProcessRunner(line =>
        {
            lock (transcript)
                transcript.Add(line);
        });
        async Task<(int Code, string Output, string Error)> Invoke(string[] arguments)
        {
            var result = await cli.RunAsync(executable, [..leading, "--profile-root", Path.Combine(scratch, "profile"), ..arguments], workspace, timeout: TimeSpan.FromMinutes(2));
            return (result.ExitCode, result.Output, result.Error);
        }

        // When parsing fails, dump stdout verbatim before failing; otherwise nothing is left but a contentless FAILED.
        JsonDocument? Parse(string output)
        {
            try
            {
                return JsonDocument.Parse(output);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine("--- stdout was not exactly one JSON document ---");
                Console.Error.WriteLine(output.Length == 0 ? "(stdout was empty)" : output);
                return null;
            }
        }

        // On failure dump the child process's full transcript; stay quiet on success (otherwise the 14 JSON lines are
        // transcribed twice).
        void Judge(bool condition, string name)
        {
            if (condition)
            {
                Check(true, name);
                return;
            }

            Console.Error.WriteLine("--- CLI transcript ---");
            foreach (var line in transcript)
                Console.Error.WriteLine(line);
            throw new Exception("FAILED: " + name);
        }

        bool Has(JsonElement parent, params string[] names) => names.All(name => parent.TryGetProperty(name, out _));
        // Walk recursively to confirm no property name is PascalCase. If the naming policy ever falls back to
        // System.Text.Json's default, the whole contract stops matching the docs — and that blows up on the consumer
        // side only, never here.
        bool CamelCaseOnly(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Length > 0 && char.IsUpper(property.Name[0]))
                        return false;
                    if (!CamelCaseOnly(property.Value))
                        return false;
                }

                return true;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    if (!CamelCaseOnly(item))
                        return false;
                return true;
            }

            return true;
        }

        // ---- 1) success path ----
        var targets = await Invoke(["targets", "--json"]);
        var targetsDocument = Parse(targets.Output);
        Judge(targets.Code == 0, "targets --json exits 0");
        Judge(targetsDocument is not null, "targets --json writes exactly one parseable JSON document to stdout");
        if (targetsDocument is not null)
        {
            using (targetsDocument)
            {
                var envelope = targetsDocument.RootElement;
                Judge(envelope.GetProperty("schema").GetInt32() == CliContract.SchemaVersion, "the envelope declares the contract schema version");
                Judge(envelope.GetProperty("command").GetString() == "targets", "the envelope names the verb that ran");
                Judge(envelope.GetProperty("ok").GetBoolean(), "a successful command reports ok:true");
                Judge(envelope.GetProperty("exitCode").GetInt32() == 0, "the envelope exitCode equals the process exit code");
                Judge(!envelope.TryGetProperty("error", out _), "a successful envelope carries no error member");
                Judge(Has(envelope, "schema", "command", "ok", "exitCode", "data"), "the envelope is written with the documented member names");
                Judge(CamelCaseOnly(envelope), "no PascalCase member survives anywhere in the envelope");
                var list = envelope.GetProperty("data").GetProperty("targets");
                Judge(list.GetArrayLength() == BuildTargets.All.Count, "data.targets lists every build target");
                Judge(list.EnumerateArray().All(target => Has(target, "id", "name", "family", "architecture", "hosts", "simulator", "current")), "every target descriptor uses the documented field names");
                Judge(list.EnumerateArray().Any(target => target.GetProperty("id").GetString() == "windows-x64"), "target ids stay verbatim in JSON");
            }
        }

        // --json is a global flag: placed before the verb it must be byte-for-byte identical to placing it after.
        var flagFirst = await Invoke(["--json", "targets"]);
        Judge(flagFirst.Code == 0 && flagFirst.Output == targets.Output, "--json is position independent");
        // ---- 2) help: a bug really did slip through here once (help text first + the envelope after it,
        //         leaving stdout unparseable as a whole) ----
        var helpJson = await Invoke(["help", "--json"]);
        var helpDocument = Parse(helpJson.Output);
        Judge(helpJson.Code == 0 && helpDocument is not null, "help --json writes exactly one parseable JSON document to stdout");
        if (helpDocument is not null)
        {
            using (helpDocument)
            {
                var commands = helpDocument.RootElement.GetProperty("data").GetProperty("commands");
                Judge(commands.GetArrayLength() == 13 && commands.EnumerateArray().Any(command => command.GetString() == "install-tools"), "help enumerates every verb as data instead of printing prose to stdout");
            }
        }

        // ---- 3) without --json the human-readable output must stay exactly as it is ----
        var human = await Invoke(["targets"]);
        Judge(human.Code == 0 && !human.Output.TrimStart().StartsWith('{') && human.Output.Contains("current="), "without --json the human layout is untouched");
        // ---- 4) failure must also yield JSON: consumers should never be forced to parse stderr ----
        var failure = await Invoke(["select", Path.Combine(scratch, "data"), Path.Combine(scratch, "not-a-project"), "windows-x64", "--json"]);
        var failureDocument = Parse(failure.Output);
        Judge(failure.Code == 1, "a failing command keeps its process exit code");
        Judge(failureDocument is not null, "a failing command still writes exactly one parseable JSON document to stdout");
        if (failureDocument is not null)
        {
            using (failureDocument)
            {
                var envelope = failureDocument.RootElement;
                Judge(!envelope.GetProperty("ok").GetBoolean() && envelope.GetProperty("exitCode").GetInt32() == 1, "the failure envelope reports ok:false together with the real exit code");
                Judge(envelope.GetProperty("error").GetProperty("type").GetString() == nameof(InvalidDataException), "the failure envelope names the exception type");
                Judge(!string.IsNullOrWhiteSpace(envelope.GetProperty("error").GetProperty("message").GetString()), "the failure envelope carries the exception message");
                Judge(!envelope.TryGetProperty("data", out _), "an exception failure carries no data member");
            }
        }

        Judge(failure.Error.Length > 0, "the human diagnostic still goes to stderr rather than stdout");
        // ---- 5) verify is a deliberate exception: ok:false + exitCode:2, but data must still be there
        //         ("missing components" is data, not an exception) ----
        var verify = await Invoke(["verify", Path.Combine(scratch, "clean-root"), "windows-x64", "--json"]);
        var verifyDocument = Parse(verify.Output);
        Judge(verify.Code == 2, "verify exits 2 when components are missing");
        Judge(verifyDocument is not null, "verify with missing components still writes exactly one parseable JSON document");
        if (verifyDocument is not null)
        {
            using (verifyDocument)
            {
                var envelope = verifyDocument.RootElement;
                Judge(!envelope.GetProperty("ok").GetBoolean() && envelope.GetProperty("exitCode").GetInt32() == 2, "verify separates ok:false from exitCode 2 instead of throwing");
                var data = envelope.GetProperty("data");
                Judge(data.GetProperty("target").GetString() == "windows-x64" && data.GetProperty("components").GetArrayLength() > 0, "verify keeps its component inventory in data even when it fails");
                Judge(data.GetProperty("components").EnumerateArray().All(component => Has(component, "name", "status", "details", "executable")), "component descriptors use the documented field names");
                Judge(!envelope.TryGetProperty("error", out _), "missing components are data, not an error member");
            }
        }

        // ---- 6) unknown verb ----
        var unknown = await Invoke(["nonsense", "--json"]);
        var unknownDocument = Parse(unknown.Output);
        Judge(unknown.Code == 1 && unknownDocument is not null, "an unknown verb also fails in JSON");
        if (unknownDocument is not null)
        {
            using (unknownDocument)
            {
                Judge(!unknownDocument.RootElement.GetProperty("ok").GetBoolean() && unknownDocument.RootElement.GetProperty("error").GetProperty("type").GetString() == nameof(ArgumentException), "an unknown verb is reported as an ArgumentException");
            }
        }

        // ---- 7) the payload encoding itself (in-process, independent of every call above) ----
        using (var bare = JsonDocument.Parse(CliContract.Encode("noop", true, 0)))
        {
            var members = bare.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);
            Judge(members.SequenceEqual(["command", "exitCode", "ok", "schema"]), "an envelope without payload or error has exactly the four base members");
        }

        using (var child = JsonDocument.Parse(CliContract.Encode("run", true, 7, new ChildExitPayload(7))))
        {
            Judge(child.RootElement.GetProperty("data").GetProperty("exitCode").GetInt32() == 7, "run/serve/deploy report the child exit code inside data while ok stays true");
        }

        var described = CliContract.Describe(new InvalidDataException("boom"));
        Judge(described.Type == nameof(InvalidDataException) && described.Message == "boom", "Describe keeps the exception type and message and leaves the stack to stderr");
        Judge(!CliContract.Encode("noop", true, 0).Contains('\r'), "the envelope's internal newlines are LF, so the contract bytes do not depend on which host printed them");
        Console.WriteLine($"{count} CLI --json contract checks passed.");
        return;
    }
}
