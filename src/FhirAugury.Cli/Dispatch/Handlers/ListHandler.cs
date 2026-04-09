using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ListHandler
{
    public static async Task<object> HandleAsync(ListRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        JsonElement endpoints = await client.GetServiceEndpointsAsync(ct);
        string sourceLower = request.Source.ToLowerInvariant();
        string? sourceAddress = null;

        if (endpoints.TryGetProperty("endpoints", out JsonElement endpointsEl) && endpointsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement ep in endpointsEl.EnumerateArray())
            {
                bool enabled = ep.TryGetProperty("enabled", out JsonElement enabledEl) && enabledEl.GetBoolean();
                string? name = ep.GetStringOrNull("name");
                if (enabled && name is not null && name.Equals(sourceLower, StringComparison.OrdinalIgnoreCase))
                {
                    sourceAddress = ep.GetStringOrNull("httpAddress");
                    break;
                }
            }
        }

        if (sourceAddress is null)
        {
            List<string> available = [];
            if (endpoints.TryGetProperty("endpoints", out JsonElement eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement ep in eps.EnumerateArray())
                {
                    bool enabled = ep.TryGetProperty("enabled", out JsonElement enabledEl) && enabledEl.GetBoolean();
                    string? name = ep.GetStringOrNull("name");
                    if (enabled && name is not null)
                        available.Add(name);
                }
            }
            throw new ArgumentException(
                $"Unknown or disabled source: {request.Source}. Available: {string.Join(", ", available)}");
        }

        JsonElement response = await client.ListItemsAsync(
            sourceAddress, request.Limit, offset: null, request.SortBy, request.SortOrder, request.Filters, ct);

        List<object> items = [];
        if (response.TryGetProperty("items", out JsonElement itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in itemsEl.EnumerateArray())
            {
                items.Add(new
                {
                    id = item.GetStringOrNull("id"),
                    title = item.GetStringOrNull("title"),
                    url = item.GetStringOrNull("url"),
                    updatedAt = item.GetStringOrNull("updatedAt"),
                    metadata = item.GetStringDictionary("metadata"),
                });
            }
        }

        return new { items };
    }
}
