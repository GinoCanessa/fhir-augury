using System.Text.Json;
using FhirAugury.Cli.Dispatch;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SearchHandler
{
    public static async Task<object> HandleAsync(Models.SearchRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string? sources = request.Sources is { Length: > 0 } ? string.Join(",", request.Sources) : null;

        JsonElement response = await client.UnifiedSearchAsync(request.Query, sources, request.Limit, ct);

        List<string> warnings = [];
        if (response.TryGetProperty("warnings", out JsonElement warningsEl) && warningsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement w in warningsEl.EnumerateArray())
            {
                string? val = w.GetString();
                if (val is not null)
                    warnings.Add(val);
            }
        }

        int totalResults = response.TryGetProperty("total", out JsonElement totalEl) ? totalEl.GetInt32() : 0;
        List<object> results = [];
        if (response.TryGetProperty("results", out JsonElement resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in resultsEl.EnumerateArray())
            {
                results.Add(new
                {
                    source = r.GetStringOrNull("source"),
                    contentType = r.GetStringOrNull("contentType"),
                    id = r.GetStringOrNull("id"),
                    title = r.GetStringOrNull("title"),
                    snippet = r.GetStringOrNull("snippet"),
                    score = r.TryGetProperty("score", out JsonElement scoreEl) ? scoreEl.GetDouble() : 0.0,
                    url = r.GetStringOrNull("url"),
                    updatedAt = r.GetStringOrNull("updatedAt"),
                    metadata = r.GetStringDictionary("metadata"),
                });
            }
        }

        return new SearchResultWithWarnings
        {
            Warnings = warnings,
            Result = new
            {
                query = request.Query,
                totalResults,
                results = results.ToArray(),
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
