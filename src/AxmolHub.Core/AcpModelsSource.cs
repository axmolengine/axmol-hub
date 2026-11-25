namespace AxmolHub.Core;

/// <summary>
/// Model/connection capability source for an external ACP CLI agent. Delegates the actual process probe to
/// <see cref="ModelProvider.AcpCatalogProbe"/> so Core keeps zero dependency on Agent process hosting.
/// </summary>
public sealed class AcpModelsSource : IModelCapabilitySource
{
    public const string IdValue = "acp-agent";
    public const string DefaultModelName = "default";

    public string Id => IdValue;

    public async Task<ModelCapabilityBatch> FetchAsync(
        HttpClient client, ModelProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.IsAcp)
            return ModelCapabilityBatch.Unreachable("This provider is not configured as an ACP CLI agent.");

        if (provider.AcpCatalogProbe is not { } probe)
            return ModelCapabilityBatch.Unreachable("ACP agent probing is not wired for this host.");

        return await probe(provider, cancellationToken).ConfigureAwait(false);
    }
}
