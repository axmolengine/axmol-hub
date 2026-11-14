using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>One parser shared by the approval gate and delivery. Model risk labels are never consulted.</summary>
internal static class ComputerInput
{
    internal static ComputerActionRequest? Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            string? Text(string key) => root.TryGetProperty(key, out var v) ? v.GetString() : null;
            int Number(string key, int fallback) => root.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
            var modifiers = root.TryGetProperty("modifiers", out var m)
                ? m.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];
            return new((Text("action") ?? "").Trim().ToLowerInvariant(), Number("x", -1), Number("y", -1),
                Text("text"), Text("key"), Number("scroll", 0))
            {
                EndX = Number("end_x", -1), EndY = Number("end_y", -1), DurationMs = Number("duration_ms", 400),
                Modifiers = modifiers, HorizontalScroll = Number("horizontal_scroll", 0),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        { return null; }
    }

    internal static string? Validate(ComputerActionRequest r)
    {
        if (r.Action is not ("screenshot" or "click" or "double_click" or "move" or "drag" or "type" or "press_key" or "scroll"))
            return "Refused: unsupported Computer Use action.";
        if (r.Action == "type" && (string.IsNullOrEmpty(r.Text) || r.Text.Length > 1000))
            return "Refused: type needs 1 to 1000 characters. Do not split and retry automatically.";
        if (r.Action == "press_key" && string.IsNullOrWhiteSpace(r.Key)) return "Refused: press_key needs one named key.";
        if (r.Modifiers.Length > 4 || r.Modifiers.Any(m => m is not ("command" or "control" or "alt" or "shift"))
            || r.Modifiers.Distinct().Count() != r.Modifiers.Length)
            return "Refused: modifiers must be distinct command, control, alt, or shift names.";
        if (r.Action != "press_key" && r.Modifiers.Length > 0) return "Refused: modifiers apply only to press_key.";
        if (r.Action == "drag" && r.DurationMs is < 100 or > 3000) return "Refused: drag duration_ms must be 100 to 3000.";
        if (r.Action == "scroll" && (r.Scroll is < -10 or > 10 || r.HorizontalScroll is < -10 or > 10
            || r.Scroll == 0 && r.HorizontalScroll == 0)) return "Refused: scroll needs nonzero vertical or horizontal steps, each from -10 to 10.";
        return null;
    }
}
