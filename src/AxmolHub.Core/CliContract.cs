using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>The failure description inside the envelope.</summary>
public sealed record CliError(string Type, string Message);

/// <summary>
/// The unified envelope for the CLI's <c>--json</c>. Contract doc: <c>docs/cli-json-contract.md</c>.
/// </summary>
public sealed class CliEnvelope
{
    /// <summary>Incremented on breaking changes; consumers should validate it before parsing <see cref="Data"/>.</summary>
    public int Schema { get; init; } = CliContract.SchemaVersion;

    public string Command { get; init; } = "";

    /// <summary>Whether this invocation completed normally. Note that under <c>run/serve/deploy</c> it can differ from ExitCode.</summary>
    public bool Ok { get; init; }

    /// <summary>The process exit code, exactly as it would be without <c>--json</c>.</summary>
    public int ExitCode { get; init; }

    /// <summary>The per-verb payload. Absent when <c>ok:false</c> and the failure was an exception.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliError? Error { get; init; }
}

/// <summary>
/// The encoding entry point for the contract. Placed in Core rather than Cli so that P1's MCP Server can
/// serialize **the same set of payload types** (see docs/ai-first-plan.md §1.1 "tools are defined once");
/// putting it in Cli would sooner or later produce two definitions that drift apart. Introduces no NuGet
/// dependency: System.Text.Json is in the framework, and Core's offline cold-build nature must be preserved.
/// </summary>
public static class CliContract
{
    /// <summary>
    /// Incremented on breaking changes; consumers should validate it before parsing <see cref="CliEnvelope.Data"/>.
    /// 2: <c>plan</c>'s payload changed from "a Hub-managed CMake command plan" to an engine cmdline invocation
    /// (<see cref="AxmolInvocation"/>) — the build has been delegated to <c>axmol build</c>.
    /// </summary>
    public const int SchemaVersion = 2;

    // camelCase: the conventional JSON casing, also consistent with the repo's existing output
    // (.axmol-hub.json uses lowercase keys like engine/version/platform). System.Text.Json defaults to
    // PascalCase, so without an explicit setting the envelope would grow Schema/Command/ExitCode — the
    // contract doc writes lowercase, so this must match the doc rather than making the doc conform to the
    // default.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Encode(string command, bool ok, int exitCode, object? data = null, CliError? error = null)
        => JsonSerializer.Serialize(
            new CliEnvelope { Command = command, Ok = ok, ExitCode = exitCode, Data = data, Error = error },
            Json);

    /// <summary>Translates an exception into the envelope's <c>error</c>. Takes only the type name and message, no stack — the stack still goes to stderr.</summary>
    public static CliError Describe(Exception exception) => new(exception.GetType().Name, exception.Message);
}

// ---------------------------------------------------------------------------
// Per-verb payloads. They are **part of the contract**, so they are typed and centralized; not anonymous
// objects assembled in place.
// ---------------------------------------------------------------------------

public sealed record TargetDescriptor(
    string Id,
    string Name,
    string Family,
    string Architecture,
    string[] Hosts,
    bool Simulator,
    bool Current,
    int MinimumEngineMajor = 2);

public sealed record TargetsPayload(IReadOnlyList<TargetDescriptor> Targets);

public sealed record ComponentDescriptor(string Name, string Status, string Details, string? Executable);

/// <summary>The <c>verify</c> payload. Note that it still exists when "components are missing" (ok:false + exitCode:2).</summary>
public sealed record VerifyPayload(string Target, IReadOnlyList<ComponentDescriptor> Components);

public sealed record ProjectPayload(ProjectEntry Project);

/// <summary>The <c>plan</c> payload: the engine command this build will execute (<c>axmol &lt;subcmd&gt; args</c>).</summary>
public sealed record PlanPayload(AxmolInvocation Plan);

public sealed record BuildPayload(ProjectEntry Project, string Status, string? Executable);

/// <summary>The <c>run</c>/<c>serve</c>/<c>deploy</c> payload: the exit code of the launched program.</summary>
public sealed record ChildExitPayload(int ExitCode);

public sealed record DeviceDescriptor(string Serial, string State, string Details);

public sealed record DevicesPayload(IReadOnlyList<DeviceDescriptor> Devices);

/// <summary>
/// The <c>install-tools</c> payload: the result of running the engine's <c>setup.ps1</c> once.
/// Note that <c>outcome</c> only takes outcomes that can be matched against the engine's raw output — with
/// Developer Mode off, setup does <c>exit 0</c> yet installs nothing, and that false success shows up as
/// <c>DeveloperModeBlocked</c>.
/// </summary>
public sealed record SetupPayload(string Engine, string Platform, string Outcome, int ExitCode);

/// <summary>
/// The <c>mirror</c> payload. <c>Available</c> is read out of the engine tree (v3: the mirror names
/// declared in <c>1k/sources.json</c>), so it is data rather than a constant list.
/// </summary>
public sealed record MirrorPayload(string Engine, string Path, string Mirror, IReadOnlyList<string> Available);

public sealed record CommandsPayload(IReadOnlyList<string> Commands);
