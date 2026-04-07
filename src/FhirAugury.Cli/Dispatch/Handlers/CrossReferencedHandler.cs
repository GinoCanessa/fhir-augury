using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class CrossReferencedHandler
{
    public static async Task<object> HandleAsync(CrossReferencedRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.ContentXRefAsync("cross-referenced", request.Value, request.SourceType, request.Limit, ct);
        return RefersToHandler.ParseXRefResponse(response);
    }
}
