using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxmolHub.Agent;

/// <summary>
/// Removes the two fields a Responses body uses to ask the endpoint to continue from earlier state. Hub sends the
/// whole conversation on every round, so the chain is redundant — and gateways that only accept the stateless
/// Codex shape reject it outright (AgentRouter answers <c>previous_response_id expected null</c>).
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
            // A conversation body is large; the substring test keeps the parse off the requests that never chain.
            if (!json.Contains("\"previous_response_id\"", StringComparison.Ordinal)
                && !json.Contains("\"conversation\"", StringComparison.Ordinal)) return;
            if (JsonNode.Parse(json) is not JsonObject root) return;
            var removed = false;
            foreach (var field in ChainFields) removed |= root.Remove(field);
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
