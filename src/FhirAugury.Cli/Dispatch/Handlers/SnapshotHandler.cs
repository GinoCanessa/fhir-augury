using Fhiraugury;
using FhirAugury.Cli.Models;
using Grpc.Core;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SnapshotHandler
{
    public static async Task<object> HandleAsync(SnapshotRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        Metadata headers = new() { { "x-source", request.Source } };
        SnapshotResponse response = await clients.Orchestrator.GetSnapshotAsync(
            new GetSnapshotRequest
            {
                Id = request.Id,
                IncludeComments = request.IncludeComments,
                IncludeInternalRefs = true,
                SourceName = request.Source,
            },
            headers: headers,
            cancellationToken: ct);

        return new
        {
            id = request.Id,
            source = request.Source,
            markdown = response.Markdown,
            url = response.Url,
        };
    }
}
