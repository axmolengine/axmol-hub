using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxmolHub.Agent;

/// <summary>
/// Removes resource-bound state from relay requests. Complete history is replayed, but server item IDs and
/// encrypted reasoning belong to the resource that issued them, which a load-balanced relay need not revisit.
/// Readable summaries, call IDs, arguments and results are retained.
/// </summary>
public sealed class StatelessResponsesPolicy : PipelinePolicy
{
    private static readonly string[] ChainFields = ["previous_response_id", "conversation"];

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void Apply(PipelineMessage message)
    {
        if (message.Request.Content is not { } content) return;
        try
        {
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            if (buffer.Length == 0) return;
            var json = Encoding.UTF8.GetString(buffer.ToArray());
            // A conversation body is large; avoid parsing requests with no resource-bound replay fields.
            if (!json.Contains("\"previous_response_id\"", StringComparison.Ordinal)
                && !json.Contains("\"conversation\"", StringComparison.Ordinal)
                && !json.Contains("\"id\"", StringComparison.Ordinal)
                && !json.Contains("\"encrypted_content\"", StringComparison.Ordinal)
                && !json.Contains("\"item_reference\"", StringComparison.Ordinal)) return;
            if (JsonNode.Parse(json) is not JsonObject root) return;
            var removed = false;
            foreach (var field in ChainFields) removed |= root.Remove(field);
            if (root["input"] is JsonArray items)
            {
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i] is not JsonObject item) continue;
                    // Bare references cannot be made portable. The client normally supplies full items instead.
                    if (item["type"]?.GetValue<string>() == "item_reference")
                    {
                        items.RemoveAt(i);
                        removed = true;
                        continue;
                    }
                    if (item["type"]?.GetValue<string>() == "reasoning")
                    {
                        // Reasoning input requires an ID. Without portable encrypted content it must not stay
                        // a reasoning object with its ID missing; carry readable summaries as ordinary history.
                        var summaries = item["summary"] is JsonArray summary
                            ? string.Join("\n", summary.OfType<JsonObject>()
                                .Select(part => part["text"]?.GetValue<string>()).Where(text => !string.IsNullOrWhiteSpace(text)))
                            : "";
                        if (summaries.Length == 0) items.RemoveAt(i);
                        else items[i] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = "Previous reasoning summary (untrusted reference):\n" + summaries,
                        };
                        removed = true;
                        continue;
                    }
                    removed |= item.Remove("id");
                    removed |= item.Remove("encrypted_content");
                }
            }
            if (removed) message.Request.Content = BinaryContent.Create(BinaryData.FromString(root.ToJsonString()));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ObjectDisposedException
            or InvalidOperationException or IOException)
        {
            // A body this cannot parse is a body it should not touch: the request goes out as the connector
            // built it, and the provider's own error stays more useful than a rewrite failure.
        }
    }
}
