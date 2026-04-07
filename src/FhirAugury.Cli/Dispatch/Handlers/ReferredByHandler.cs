using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ReferredByHandler
{
    public static async Task<object> HandleAsync(ReferredByRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.ContentXRefAsync("referred-by", request.Value, request.SourceType, request.Limit, ct);
        return RefersToHandler.ParseXRefResponse(response);
    }
}
