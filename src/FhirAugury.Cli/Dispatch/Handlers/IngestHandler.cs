using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class IngestHandler
{
    public static async Task<object> HandleAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        return request.Action.ToLowerInvariant() switch
        {
            "trigger" => await HandleTriggerAsync(request, orchestratorAddr, ct),
            "status" => await HandleStatusAsync(orchestratorAddr, ct),
            "reingest" => await HandleReingestAsync(request, orchestratorAddr, ct),
            "reindex" => await HandleReindexAsync(request, orchestratorAddr, ct),
            _ => throw new ArgumentException(
                $"Unknown ingest action: {request.Action}. Valid actions: trigger, status, reingest, reindex"),
        };
    }

    private static async Task<object> HandleTriggerAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string? sources = request.Sources is { Length: > 0 } ? string.Join(",", request.Sources.Select(s => s.ToLowerInvariant())) : null;

        JsonElement response = await client.TriggerSyncAsync(request.Type, sources, request.JiraProject, ct);

        List<object> statuses = [];
        if (response.TryGetProperty("statuses", out JsonElement statusesEl) && statusesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in statusesEl.EnumerateArray())
            {
                statuses.Add(new
                {
                    source = s.GetStringOrNull("source"),
                    status = s.GetStringOrNull("status"),
                    message = s.GetStringOrNull("message"),
                });
            }
        }

        return new { statuses = statuses.ToArray() };
    }

    private static async Task<object> HandleStatusAsync(string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.GetServicesStatusAsync(ct);

        List<object> services = [];
        if (response.TryGetProperty("services", out JsonElement servicesEl) && servicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in servicesEl.EnumerateArray())
            {
                services.Add(new
                {
                    name = s.GetStringOrNull("name"),
                    status = s.GetStringOrNull("status"),
                    lastSyncAt = s.GetStringOrNull("lastSyncAt"),
                    itemCount = s.TryGetProperty("itemCount", out JsonElement icEl) ? icEl.GetInt32() : 0,
                });
            }
        }

        return new { services = services.ToArray() };
    }

    private static async Task<object> HandleReingestAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string? sources = request.Sources is { Length: > 0 } ? string.Join(",", request.Sources.Select(s => s.ToLowerInvariant())) : null;

        JsonElement response = await client.TriggerSyncAsync("rebuild", sources, request.JiraProject, ct);

        List<object> statuses = [];
        if (response.TryGetProperty("statuses", out JsonElement statusesEl) && statusesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in statusesEl.EnumerateArray())
            {
                statuses.Add(new
                {
                    source = s.GetStringOrNull("source"),
                    status = s.GetStringOrNull("status"),
                    message = s.GetStringOrNull("message"),
                });
            }
        }

        return new { statuses = statuses.ToArray() };
    }

    private static async Task<object> HandleReindexAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string? sources = request.Sources is { Length: > 0 } ? string.Join(",", request.Sources.Select(s => s.ToLowerInvariant())) : null;

        JsonElement response = await client.RebuildIndexAsync(request.IndexType, sources, ct);

        List<object> results = [];
        if (response.TryGetProperty("results", out JsonElement resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in resultsEl.EnumerateArray())
            {
                results.Add(new
                {
                    source = r.GetStringOrNull("source"),
                    success = r.TryGetProperty("success", out JsonElement succEl) && succEl.GetBoolean(),
                    actionTaken = r.GetStringOrNull("actionTaken"),
                    elapsedSeconds = r.TryGetProperty("elapsedSeconds", out JsonElement esEl) ? esEl.GetDouble() : (double?)null,
                    error = r.GetStringOrNull("error"),
                });
            }
        }

        return new { results = results.ToArray() };
    }
}
