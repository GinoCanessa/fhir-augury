using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class RelatedHandler
{
    public static async Task<object> HandleAsync(RelatedRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string? targetSources = request.TargetSources is { Length: > 0 } ? string.Join(",", request.TargetSources) : null;

        JsonElement response = await client.FindRelatedAsync(request.Source, request.Id, request.Limit, targetSources, ct);

        List<object> items = [];
        if (response.TryGetProperty("items", out JsonElement itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement i in itemsEl.EnumerateArray())
            {
                items.Add(new
                {
                    source = i.GetStringOrNull("source"),
                    id = i.GetStringOrNull("id"),
                    title = i.GetStringOrNull("title"),
                    snippet = i.GetStringOrNull("snippet"),
                    url = i.GetStringOrNull("url"),
                    relevanceScore = i.TryGetProperty("relevanceScore", out JsonElement scoreEl) ? scoreEl.GetDouble() : 0.0,
                    relationship = i.GetStringOrNull("relationship"),
                    context = i.GetStringOrNull("context"),
                });
            }
        }

        return new
        {
            seedSource = response.GetStringOrNull("seedSource"),
            seedId = response.GetStringOrNull("seedId"),
            seedTitle = response.GetStringOrNull("seedTitle"),
            items = items.ToArray(),
        };
    }
}
