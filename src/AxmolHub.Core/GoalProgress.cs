using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>One result vocabulary for the local spin guard and the durable goal checkpoint.</summary>
public static class GoalProgress
{
    public const int WindowSize = 48;

    public static string NormalizeArguments(string argumentsJson)
    {
        try { return Canonical(JsonNode.Parse(argumentsJson))?.ToJsonString() ?? argumentsJson; }
        catch (JsonException) { return argumentsJson; }
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject table => new JsonObject(table.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };

    public static bool Failed(string result, bool failed = false)
        => failed || string.IsNullOrWhiteSpace(result)
           || result.StartsWith("Tool failed:", StringComparison.Ordinal)
           || result.StartsWith("Refused", StringComparison.Ordinal)
           || result.StartsWith("No change", StringComparison.Ordinal)
           || result.StartsWith("This session has no workspace directory", StringComparison.Ordinal)
           || result.StartsWith("This exact call", StringComparison.Ordinal)
           || result.StartsWith("This turn is stopping:", StringComparison.Ordinal)
           || result is ToolApprovalResults.Denied or ToolApprovalResults.Superseded
               or ToolApprovalResults.NotAnswered or ToolApprovalResults.Unavailable;

    /// <summary>Command exit status is part of the execution outcome even when the tool returned normally.</summary>
    public static bool Failed(string toolName, string result, bool failed = false)
        => Failed(result, failed) || toolName == ToolNames.RunCommand
           && Regex.IsMatch(result, @"exit: (?!0(?:\D|$))-?\d+");

    public static bool StateChanged(string toolName, string result)
    {
        if (Failed(result)) return false;
        return toolName switch
        {
            LegacyFileToolNames.FileWrite or ToolNames.FsWrite or ToolNames.FsReplace =>
                result.StartsWith("Created ", StringComparison.Ordinal) || result.StartsWith("Edited ", StringComparison.Ordinal),
            ToolNames.FsDelete => result.StartsWith("Deleted ", StringComparison.Ordinal),
            ToolNames.FsMove => result.StartsWith("Moved ", StringComparison.Ordinal),
            LegacyFileToolNames.RequestDirectory or LegacyFileToolNames.RequestFile or ToolNames.FsRequestAccess =>
                result.StartsWith("Granted", StringComparison.Ordinal),
            ToolNames.MemoryWrite => result.StartsWith("Saved ", StringComparison.Ordinal),
            ToolNames.ComputerUse => result.StartsWith("Input delivered (", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>New evidence counts once, even for read-only work. Failed and repeated evidence does not.</summary>
    public static bool Observe(Conversation conversation, string tool, string arguments, string result, bool failed)
    {
        var normalized = Regex.Replace(result, @"(?m)^shell:.*$|^\(the whole output.*$|^已用时间.*$|^Time Elapsed.*$|Change (?:ID|batch): \S+", "");
        var error = Failed(tool, result, failed);
        // Changing only the arguments is activity, not new evidence. Successful writes are different:
        // their arguments describe the state change even when the result only names the edited file.
        var evidence = normalized.Trim();
        var identity = StateChanged(tool, result) ? tool + ":" + NormalizeArguments(arguments) : tool;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (error ? "error:" : identity + ":") + evidence)));
        conversation.GoalProgressFingerprints ??= [];
        var repeated = conversation.GoalProgressFingerprints.Contains(fingerprint, StringComparer.Ordinal);
        conversation.GoalProgressFingerprints.Add(fingerprint);
        if (conversation.GoalProgressFingerprints.Count > WindowSize)
            conversation.GoalProgressFingerprints.RemoveRange(0, conversation.GoalProgressFingerprints.Count - WindowSize);
        var progress = !error && !repeated && evidence.Length > 0;
        if (progress)
        {
            conversation.GoalProgressVersion++;
            conversation.GoalNoProgressRequests = 0;
            conversation.GoalEmptyRunCount = 0;
            conversation.GoalCooldownCount = 0;
            conversation.GoalCheckpoint = $"{tool}: {normalized.Trim()[..Math.Min(normalized.Trim().Length, 1200)]}";
        }
        return progress;
    }
}
