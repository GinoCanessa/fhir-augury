using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

internal static class RelatedByKeywordHandler
{
    public static async Task<object> HandleAsync(RelatedByKeywordRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement json = await client.ContentRelatedByKeywordAsync(
            request.Source, request.Id, request.MinScore, request.KeywordType, request.Limit, ct);

        string source = json.GetStringOrNull("source") ?? "";
        string sourceId = json.GetStringOrNull("sourceId") ?? "";

        List<object> relatedItems = [];
        if (json.TryGetProperty("relatedItems", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                List<string> sharedKeywords = [];
                if (item.TryGetProperty("sharedKeywords", out JsonElement skws) && skws.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement skw in skws.EnumerateArray())
                        sharedKeywords.Add(skw.GetString() ?? "");
                }

                relatedItems.Add(new
                {
                    source = item.GetStringOrNull("source") ?? "",
                    sourceId = item.GetStringOrNull("sourceId") ?? "",
                    contentType = item.GetStringOrNull("contentType") ?? "",
                    title = item.GetStringOrNull("title") ?? "",
                    score = item.TryGetProperty("score", out JsonElement sc) ? sc.GetDouble() : 0.0,
                    sharedKeywords,
                });
            }
        }

        return new { source, sourceId, total = relatedItems.Count, relatedItems };
    }
}
