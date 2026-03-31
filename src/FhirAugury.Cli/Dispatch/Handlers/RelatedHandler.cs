using Fhiraugury;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class RelatedHandler
{
    public static async Task<object> HandleAsync(RelatedRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        FindRelatedRequest grpcRequest = new()
        {
            Source = request.Source,
            Id = request.Id,
            Limit = request.Limit,
        };

        if (request.TargetSources is { Length: > 0 })
        {
            foreach (string source in request.TargetSources)
                grpcRequest.TargetSources.Add(source);
        }

        FindRelatedResponse response = await clients.Orchestrator.FindRelatedAsync(grpcRequest, cancellationToken: ct);

        return new
        {
            seedSource = response.SeedSource,
            seedId = response.SeedId,
            seedTitle = response.SeedTitle,
            items = response.Items.Select(i => new
            {
                source = i.Source,
                id = i.Id,
                title = i.Title,
                snippet = i.Snippet,
                url = i.Url,
                relevanceScore = i.RelevanceScore,
                relationship = i.Relationship,
                context = i.Context,
            }).ToArray(),
        };
    }
}
