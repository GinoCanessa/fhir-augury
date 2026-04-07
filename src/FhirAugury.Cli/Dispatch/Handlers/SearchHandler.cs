using System.Text.Json;
using FhirAugury.Cli.Dispatch;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SearchHandler
{
    public static async Task<object> HandleAsync(Models.SearchRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        // Support both old "query" field and new "values" array
        List<string> values = request.Values is { Count: > 0 }
            ? request.Values
            : [request.Query];

        string? sources = request.Sources is { Length: > 0 } ? string.Join(",", request.Sources) : null;

        JsonElement response = await client.ContentSearchAsync(values, sources, request.Limit, ct);

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
        JsonElement hitsEl = response.TryGetProperty("hits", out JsonElement h) ? h
            : response.TryGetProperty("results", out JsonElement r) ? r
            : default;

        if (hitsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in hitsEl.EnumerateArray())
            {
                results.Add(new
                {
                    source = item.GetStringOrNull("source"),
                    contentType = item.GetStringOrNull("contentType"),
                    id = item.GetStringOrNull("id"),
                    title = item.GetStringOrNull("title"),
                    snippet = item.GetStringOrNull("snippet"),
                    score = item.TryGetProperty("score", out JsonElement scoreEl) ? scoreEl.GetDouble() : 0.0,
                    url = item.GetStringOrNull("url"),
                    updatedAt = item.GetStringOrNull("updatedAt"),
                    matchedValue = item.GetStringOrNull("matchedValue"),
                    metadata = item.GetStringDictionary("metadata"),
                });
            }
        }

        return new SearchResultWithWarnings
        {
            Warnings = warnings,
            Result = new
            {
                values,
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
