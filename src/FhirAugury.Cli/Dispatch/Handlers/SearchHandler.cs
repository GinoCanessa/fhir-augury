using Fhiraugury;
using FhirAugury.Cli.Dispatch;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SearchHandler
{
    public static async Task<object> HandleAsync(Models.SearchRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        UnifiedSearchRequest grpcRequest = new() { Query = request.Query, Limit = request.Limit };

        if (request.Sources is { Length: > 0 })
        {
            foreach (string source in request.Sources)
                grpcRequest.Sources.Add(source);
        }

        SearchResponse response = await clients.Orchestrator.UnifiedSearchAsync(grpcRequest, cancellationToken: ct);

        List<string> warnings = [.. response.Warnings];

        return new SearchResultWithWarnings
        {
            Warnings = warnings,
            Result = new
            {
                query = request.Query,
                totalResults = response.TotalResults,
                results = response.Results.Select(r => new
                {
                    source = r.Source,
                    contentType = r.ContentType,
                    id = r.Id,
                    title = r.Title,
                    snippet = r.Snippet,
                    score = r.Score,
                    url = r.Url,
                    updatedAt = r.UpdatedAt?.ToDateTimeOffset().ToString("o"),
                    metadata = new Dictionary<string, string>(r.Metadata),
                }).ToArray(),
            },
        };
    }

    private sealed class SearchResultWithWarnings : IHasWarnings
    {
        public List<string>? Warnings { get; set; }
        public object Result { get; set; } = null!;
        public List<string>? TakeWarnings() => Warnings is { Count: > 0 } ? Warnings : null;
        public object GetData() => Result;
    }
}
