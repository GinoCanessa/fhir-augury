using Fhiraugury;
using FhirAugury.Cli.Models;
using Grpc.Core;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class GetHandler
{
    public static async Task<object> HandleAsync(GetRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        Metadata headers = new() { { "x-source", request.Source } };
        ItemResponse response = await clients.Orchestrator.GetItemAsync(
            new GetItemRequest
            {
                Id = request.Id,
                IncludeContent = true,
                IncludeComments = request.IncludeComments,
                SourceName = request.Source,
            },
            headers: headers,
            cancellationToken: ct);

        return new
        {
            source = response.Source,
            id = response.Id,
            title = response.Title,
            content = response.Content,
            url = response.Url,
            createdAt = response.CreatedAt?.ToDateTimeOffset().ToString("o"),
            updatedAt = response.UpdatedAt?.ToDateTimeOffset().ToString("o"),
            metadata = new Dictionary<string, string>(response.Metadata),
            comments = response.Comments.Select(c => new
            {
                id = c.Id,
                author = c.Author,
                body = c.Body,
                createdAt = c.CreatedAt?.ToDateTimeOffset().ToString("o"),
                url = c.Url,
            }).ToArray(),
        };
    }
}
