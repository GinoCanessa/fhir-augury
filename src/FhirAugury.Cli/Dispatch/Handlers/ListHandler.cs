using System.Text;
using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ListHandler
{
    public static async Task<object> HandleAsync(ListRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        string sourceLower = request.Source.ToLowerInvariant();
        if (sourceLower != "jira" && sourceLower != "zulip" && sourceLower != "confluence" && sourceLower != "github")
        {
            throw new ArgumentException(
                $"Unknown source: {request.Source}. Available: jira, zulip, confluence, github");
        }

        StringBuilder url = new($"/api/v1/{sourceLower}/items");
        List<string> queryParams = [];
        if (request.Limit > 0)
            queryParams.Add($"limit={request.Limit}");
        if (!string.IsNullOrEmpty(request.SortBy))
            queryParams.Add($"sort_by={Uri.EscapeDataString(request.SortBy)}");
        if (!string.IsNullOrEmpty(request.SortOrder))
            queryParams.Add($"sort_order={Uri.EscapeDataString(request.SortOrder)}");
        if (request.Filters is not null)
        {
            foreach ((string key, string value) in request.Filters)
                queryParams.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        if (queryParams.Count > 0)
            url.Append($"?{string.Join("&", queryParams)}");

        JsonElement response = await client.GetFromOrchestratorAsync(url.ToString(), ct);

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
