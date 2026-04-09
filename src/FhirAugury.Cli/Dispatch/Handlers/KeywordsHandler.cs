using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

internal static class KeywordsHandler
{
    public static async Task<object> HandleAsync(KeywordsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement json = await client.ContentKeywordsAsync(
            request.Source, request.Id, request.KeywordType, request.Limit, ct);

        string source = json.GetStringOrNull("source") ?? "";
        string sourceId = json.GetStringOrNull("sourceId") ?? "";
        string contentType = json.GetStringOrNull("contentType") ?? "";

        List<object> keywords = [];
        if (json.TryGetProperty("keywords", out JsonElement kws) && kws.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement kw in kws.EnumerateArray())
            {
                keywords.Add(new
                {
                    keyword = kw.GetStringOrNull("keyword") ?? "",
                    keywordType = kw.GetStringOrNull("keywordType") ?? "",
                    count = kw.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 0,
                    bm25Score = kw.TryGetProperty("bm25Score", out JsonElement s) ? s.GetDouble() : 0.0,
                });
            }
        }

        return new { source, sourceId, contentType, total = keywords.Count, keywords };
    }
}
