using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SourcesHandler
{
    public static async Task<object> HandleAsync(SourcesRequest _, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.GetFromOrchestratorAsync(
            "/api/v1/source/orchestrator/list-sources", ct);
        return response;
    }
}
