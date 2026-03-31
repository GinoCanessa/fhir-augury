using Fhiraugury;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class XrefHandler
{
    public static async Task<object> HandleAsync(XrefRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        GetXRefResponse response = await clients.Orchestrator.GetCrossReferencesAsync(
            new GetXRefRequest
            {
                Source = request.Source,
                Id = request.Id,
                Direction = request.Direction,
            },
            cancellationToken: ct);

        return new
        {
            references = response.References.Select(x => new
            {
                sourceType = x.SourceType,
                sourceId = x.SourceId,
                sourceContentType = x.SourceContentType,
                targetType = x.TargetType,
                targetId = x.TargetId,
                linkType = x.LinkType,
                context = x.Context,
                targetTitle = x.TargetTitle,
                targetUrl = x.TargetUrl,
            }).ToArray(),
        };
    }
}
