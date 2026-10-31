using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>Application grants. Window-title arguments only locate a window; they are never grant keys.</summary>
internal static class ComputerUseGrants
{
    public static bool IsComputerUseTool(string? name) => name is ToolNames.ComputerUse or ToolNames.CaptureScreen;

    public static string NormalizeTitle(string? value)
        => (value ?? "").Trim().Normalize(System.Text.NormalizationForm.FormC);

    public static string LocatorForTool(string? toolName, string? json)
    {
        if (!IsComputerUseTool(toolName) || string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(toolName == ToolNames.CaptureScreen ? "target" : "application", out var value)
                   && value.ValueKind == JsonValueKind.String ? NormalizeTitle(value.GetString()) : "";
        }
        catch (JsonException) { return ""; }
    }

    public static ComputerUseTarget? ResolveTarget(ScreenCaptureBridge? screen, string? toolName, string? json)
    {
        var title = LocatorForTool(toolName, json);
        if (screen is null || title.Length is 0 or > 256) return null;
        try
        {
            using var document = JsonDocument.Parse(json!);
            if (toolName == ToolNames.CaptureScreen && document.RootElement.TryGetProperty("fullscreen", out var full)
                && full.ValueKind != JsonValueKind.False) return null;
            var processId = 0;
            if (document.RootElement.TryGetProperty("process_id", out var pid)
                && (!pid.TryGetInt32(out processId) || processId < 0)) return null;
            var match = ScreenCapture.Find(screen.Windows(), title, processId);
            return match.Ok ? ComputerUseTarget.FromWindow(match.Window) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    public static List<ComputerApplication> Normalize(IEnumerable<ComputerApplication>? values)
        => (values ?? []).Select(value => ComputerApplication.FromExecutable(value?.ExecutablePath))
            .OfType<ComputerApplication>().DistinctBy(app => app.ExecutablePath, ComputerApplication.PathComparer).ToList();

    public static bool Contains(IEnumerable<ComputerApplication>? values, ComputerApplication? app)
        => ComputerApplication.FromExecutable(app?.ExecutablePath) is { } key
           && (values ?? []).Any(value => key.IsSameApplication(ComputerApplication.FromExecutable(value?.ExecutablePath)));

    public static bool Add(List<ComputerApplication> values, ComputerApplication? app)
    {
        if (ComputerApplication.FromExecutable(app?.ExecutablePath) is not { } key || Contains(values, key)) return false;
        values.Add(key);
        return true;
    }

    public static bool Remove(List<ComputerApplication> values, ComputerApplication app)
        => values.RemoveAll(value => app.IsSameApplication(ComputerApplication.FromExecutable(value?.ExecutablePath))) > 0;
}
