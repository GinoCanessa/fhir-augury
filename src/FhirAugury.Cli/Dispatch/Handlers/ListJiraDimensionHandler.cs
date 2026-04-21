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

        List<object> items = ProjectItems(request.Dimension, response, request.Limit);

        return new { dimension = request.Dimension, items };
    }

    /// <summary>
    /// Pure projection of the orchestrator's dimension response into the
    /// CLI-facing item shape. Extracted for testability. The
    /// <c>workgroups</c> dimension surfaces the additional canonical HL7
    /// fields (<c>code</c>, <c>nameClean</c>, <c>definition</c>,
    /// <c>retired</c>) so callers can resolve work-group slugs without
    /// re-implementing <c>Hl7WorkGroupNameCleaner</c>; other dimensions
    /// keep the original two-field shape.
    /// </summary>
    internal static List<object> ProjectItems(string dimension, JsonElement response, int? limit)
    {
        List<object> items = [];
        if (response.ValueKind != JsonValueKind.Array)
            return items;

        bool isWorkgroups = string.Equals(dimension, "workgroups", StringComparison.OrdinalIgnoreCase);
        int count = 0;
        foreach (JsonElement el in response.EnumerateArray())
        {
            if (limit.HasValue && limit.Value > 0 && count >= limit.Value)
                break;

            if (isWorkgroups)
            {
                items.Add(new
                {
                    name = el.GetStringOrNull("name"),
                    code = el.GetStringOrNull("workGroupCode"),
                    nameClean = el.GetStringOrNull("workGroupNameClean"),
                    definition = el.GetStringOrNull("workGroupDefinition"),
                    retired = el.TryGetProperty("workGroupRetired", out JsonElement r)
                              && r.ValueKind == JsonValueKind.True,
                    issueCount = el.TryGetProperty("issueCount", out JsonElement c) ? c.GetInt32() : 0,
                });
            }
            else
            {
                items.Add(new
                {
                    name = el.GetStringOrNull("name"),
                    issueCount = el.TryGetProperty("issueCount", out JsonElement c) ? c.GetInt32() : 0,
                });
            }
            count++;
        }
        return items;
    }
}
