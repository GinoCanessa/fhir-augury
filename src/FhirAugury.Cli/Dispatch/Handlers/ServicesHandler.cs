using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ServicesHandler
{
    public static async Task<object> HandleAsync(ServicesRequest request, string orchestratorAddr, CancellationToken ct)
    {
        return request.Action.ToLowerInvariant() switch
        {
            "status" => await HandleStatusAsync(orchestratorAddr, ct),
            "stats" => await HandleStatsAsync(orchestratorAddr, ct),
            _ => throw new ArgumentException(
                $"Unknown services action: {request.Action}. Valid actions: status, stats"),
        };
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
                List<object> indexes = [];
                if (s.TryGetProperty("indexes", out JsonElement indexesEl) && indexesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement i in indexesEl.EnumerateArray())
                    {
                        indexes.Add(new
                        {
                            name = i.GetStringOrNull("name"),
                            description = i.GetStringOrNull("description"),
                            isRebuilding = i.TryGetProperty("isRebuilding", out JsonElement irEl) && irEl.GetBoolean(),
                            lastRebuildStartedAt = i.GetStringOrNull("lastRebuildStartedAt"),
                            lastRebuildCompletedAt = i.GetStringOrNull("lastRebuildCompletedAt"),
                            recordCount = i.TryGetProperty("recordCount", out JsonElement rcEl) ? rcEl.GetInt32() : 0,
                            lastError = i.GetStringOrNull("lastError"),
                        });
                    }
                }

                services.Add(new
                {
                    name = s.GetStringOrNull("name"),
                    status = s.GetStringOrNull("status"),
                    httpAddress = s.GetStringOrNull("httpAddress"),
                    itemCount = s.TryGetProperty("itemCount", out JsonElement icEl) ? icEl.GetInt32() : 0,
                    dbSizeBytes = s.TryGetProperty("dbSizeBytes", out JsonElement dbEl) ? dbEl.GetInt64() : 0L,
                    lastSyncAt = s.GetStringOrNull("lastSyncAt"),
                    lastError = s.GetStringOrNull("lastError"),
                    indexes = indexes.ToArray(),
                });
            }
        }

        return new
        {
            lastXrefScanAt = response.GetStringOrNull("lastXrefScanAt"),
            services = services.ToArray(),
        };
    }

    private static async Task<object> HandleStatsAsync(string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement statusResponse = await client.GetServicesStatusAsync(ct);

        List<object> sources = [];
        if (statusResponse.TryGetProperty("services", out JsonElement servicesEl) && servicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement svc in servicesEl.EnumerateArray())
            {
                string? httpAddress = svc.GetStringOrNull("httpAddress");
                if (httpAddress is null)
                    continue;

                try
                {
                    JsonElement stats = await client.GetSourceStatsAsync(httpAddress, ct);
                    Dictionary<string, int> additionalCounts = [];
                    if (stats.TryGetProperty("additionalCounts", out JsonElement acEl) && acEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty prop in acEl.EnumerateObject())
                        {
                            if (prop.Value.TryGetInt32(out int val))
                                additionalCounts[prop.Name] = val;
                        }
                    }

                    sources.Add(new
                    {
                        source = stats.GetStringOrNull("source"),
                        totalItems = stats.TryGetProperty("totalItems", out JsonElement tiEl) ? tiEl.GetInt32() : 0,
                        totalComments = stats.TryGetProperty("totalComments", out JsonElement tcEl) ? tcEl.GetInt32() : 0,
                        databaseSizeBytes = stats.TryGetProperty("databaseSizeBytes", out JsonElement dsbEl) ? dsbEl.GetInt64() : 0L,
                        cacheSizeBytes = stats.TryGetProperty("cacheSizeBytes", out JsonElement csbEl) ? csbEl.GetInt64() : 0L,
                        lastSyncAt = stats.GetStringOrNull("lastSyncAt"),
                        oldestItem = stats.GetStringOrNull("oldestItem"),
                        newestItem = stats.GetStringOrNull("newestItem"),
                        additionalCounts,
                    });
                }
                catch
                {
                    // Service may be unreachable — skip it
                }
            }
        }

        return new
        {
            lastXrefScanAt = statusResponse.GetStringOrNull("lastXrefScanAt"),
            sources,
        };
    }
}
