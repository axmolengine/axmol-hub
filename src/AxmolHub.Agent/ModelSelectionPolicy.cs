using System.ClientModel.Primitives;

namespace AxmolHub.Agent;

/// <summary>Hands a stale request back to its owner without cancelling work already in flight.</summary>
public sealed class ModelSelectionChangedException() : Exception("The conversation's model selection changed before sending.");

/// <summary>Checks immediately before transport, so SDK retries cannot reuse a superseded selection.</summary>
internal sealed class ModelSelectionPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        RequestRetryScope.Current.Value?.ValidateSelectionAsync().GetAwaiter().GetResult();
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        if (RequestRetryScope.Current.Value is { } scope)
            await scope.ValidateSelectionAsync().ConfigureAwait(false);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }
}
