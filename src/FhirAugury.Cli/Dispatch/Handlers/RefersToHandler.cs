using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class RefersToHandler
{
    public static async Task<object> HandleAsync(RefersToRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.ContentXRefAsync("refers-to", request.Value, request.SourceType, request.Limit, ct);
        return ParseXRefResponse(response);
    }

    internal static object ParseXRefResponse(JsonElement response)
    {
        List<object> hits = [];
        if (response.TryGetProperty("hits", out JsonElement hitsEl) && hitsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement h in hitsEl.EnumerateArray())
            {
                hits.Add(new
                {
                    sourceType = h.GetStringOrNull("sourceType"),
                    sourceId = h.GetStringOrNull("sourceId"),
                    sourceTitle = h.GetStringOrNull("sourceTitle"),
                    targetType = h.GetStringOrNull("targetType"),
                    targetId = h.GetStringOrNull("targetId"),
                    targetTitle = h.GetStringOrNull("targetTitle"),
                    linkType = h.GetStringOrNull("linkType"),
                    context = h.GetStringOrNull("context"),
                    score = h.TryGetProperty("score", out JsonElement scoreEl) && scoreEl.ValueKind == JsonValueKind.Number
                        ? scoreEl.GetDouble() : 1.0,
                    updatedAt = h.GetStringOrNull("updatedAt"),
                });
            }
        }

        return new
        {
            value = response.GetStringOrNull("value"),
            direction = response.GetStringOrNull("direction"),
            total = response.TryGetProperty("total", out JsonElement totalEl) ? totalEl.GetInt32() : 0,
            hits = hits.ToArray(),
        };
    }
}
