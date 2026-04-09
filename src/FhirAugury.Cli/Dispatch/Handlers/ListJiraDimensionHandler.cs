using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ListJiraDimensionHandler
{
    public static async Task<object> HandleAsync(
        ListJiraDimensionRequest request, string orchestratorAddr, CancellationToken ct)
    {
        string apiPath = request.Dimension.ToLowerInvariant() switch
        {
            "workgroups" => "/api/v1/jira/work-groups",
            "specifications" => "/api/v1/jira/specifications",
            "labels" => "/api/v1/jira/labels",
            "statuses" => "/api/v1/jira/statuses",
            _ => throw new ArgumentException($"Unknown dimension: {request.Dimension}"),
        };

        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.GetFromOrchestratorAsync(apiPath, ct);

        List<object> items = [];
        if (response.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (JsonElement el in response.EnumerateArray())
            {
                if (request.Limit.HasValue && request.Limit.Value > 0 && count >= request.Limit.Value)
                    break;
                items.Add(new
                {
                    name = el.GetStringOrNull("name"),
                    issueCount = el.TryGetProperty("issueCount", out JsonElement c) ? c.GetInt32() : 0,
                });
                count++;
            }
        }

        return new { dimension = request.Dimension, items };
    }
}
