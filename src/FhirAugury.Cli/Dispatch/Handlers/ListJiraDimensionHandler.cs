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
        bool? catalogJoinDegraded = ExtractCatalogJoinDegraded(request.Dimension, response);

        if (catalogJoinDegraded is not null)
        {
            return new
            {
                dimension = request.Dimension,
                catalogJoinDegraded = catalogJoinDegraded.Value,
                items,
            };
        }
        return new { dimension = request.Dimension, items };
    }

    /// <summary>
    /// Pure projection of the orchestrator's dimension response into the
    /// CLI-facing item shape. Extracted for testability. Tolerates both the
    /// legacy bare-array shape and the new <c>JiraWorkGroupListResponse</c>
    /// envelope (<c>{ catalogJoinDegraded, items }</c>) on the
    /// <c>workgroups</c> dimension. The <c>workgroups</c> dimension surfaces
    /// the additional canonical HL7 fields (<c>code</c>, <c>nameClean</c>,
    /// <c>definition</c>, <c>retired</c>) so callers can resolve work-group
    /// slugs without re-implementing <c>Hl7WorkGroupNameCleaner</c>; other
    /// dimensions keep the original two-field shape.
    /// </summary>
    internal static List<object> ProjectItems(string dimension, JsonElement response, int? limit)
    {
        List<object> items = [];

        // TODO(workgroup-envelope-cleanup): Drop legacy bare-array branch
        // once Phase 2 has been deployed for one cycle. Tracked in the
        // dual-shape rollout — see scratch/0518-15/plan.md Risks.
        JsonElement array = UnwrapItems(response);
        if (array.ValueKind != JsonValueKind.Array)
            return items;

        bool isWorkgroups = string.Equals(dimension, "workgroups", StringComparison.OrdinalIgnoreCase);
        int count = 0;
        foreach (JsonElement el in array.EnumerateArray())
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

    /// <summary>
    /// Extracts the <c>catalogJoinDegraded</c> flag from a
    /// <c>workgroups</c>-dimension envelope response. Returns <c>null</c>
    /// for non-workgroup dimensions or for the legacy bare-array shape so
    /// callers can decide whether to surface the field at all.
    /// </summary>
    internal static bool? ExtractCatalogJoinDegraded(string dimension, JsonElement response)
    {
        if (!string.Equals(dimension, "workgroups", StringComparison.OrdinalIgnoreCase))
            return null;
        if (response.ValueKind != JsonValueKind.Object) return null;

        if (response.TryGetProperty("catalogJoinDegraded", out JsonElement flag) ||
            response.TryGetProperty("CatalogJoinDegraded", out flag))
        {
            return flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        return null;
    }

    private static JsonElement UnwrapItems(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Array) return response;
        if (response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty("items", out JsonElement items) ||
                response.TryGetProperty("Items", out items))
            {
                return items;
            }
        }
        return default;
    }
}
