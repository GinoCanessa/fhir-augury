using Fhiraugury;
using FhirAugury.Cli.Models;
using Grpc.Core;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ListHandler
{
    public static async Task<object> HandleAsync(ListRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);

        Dictionary<string, string> endpoints = await clients.GetServiceEndpointsAsync(ct);
        string sourceLower = request.Source.ToLowerInvariant();
        if (!endpoints.TryGetValue(sourceLower, out string? sourceAddress))
        {
            throw new ArgumentException(
                $"Unknown or disabled source: {request.Source}. Available: {string.Join(", ", endpoints.Keys)}");
        }

        SourceService.SourceServiceClient sourceClient = clients.GetSourceClient(sourceAddress);
        ListItemsRequest grpcRequest = new()
        {
            Limit = request.Limit,
            SortBy = request.SortBy,
            SortOrder = request.SortOrder,
        };

        if (request.Filters is not null)
        {
            foreach ((string key, string value) in request.Filters)
                grpcRequest.Filters.Add(key, value);
        }

        using AsyncServerStreamingCall<ItemSummary> call = sourceClient.ListItems(grpcRequest, cancellationToken: ct);

        List<object> items = [];
        await foreach (ItemSummary item in call.ResponseStream.ReadAllAsync(ct))
        {
            items.Add(new
            {
                id = item.Id,
                title = item.Title,
                url = item.Url,
                updatedAt = item.UpdatedAt?.ToDateTimeOffset().ToString("o"),
                metadata = new Dictionary<string, string>(item.Metadata),
            });
        }

        return new { items };
    }
}
