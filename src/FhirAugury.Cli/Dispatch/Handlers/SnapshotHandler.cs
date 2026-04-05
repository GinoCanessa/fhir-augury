using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SnapshotHandler
{
    public static async Task<object> HandleAsync(SnapshotRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.GetSnapshotAsync(request.Source, request.Id, ct);

        return new
        {
            id = response.GetStringOrNull("id") ?? request.Id,
            source = response.GetStringOrNull("source") ?? request.Source,
            markdown = response.GetStringOrNull("markdown"),
            url = response.GetStringOrNull("url"),
        };
    }
}
