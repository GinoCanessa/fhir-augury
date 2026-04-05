using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class XrefHandler
{
    public static async Task<object> HandleAsync(XrefRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.GetCrossReferencesAsync(request.Source, request.Id, request.Direction, ct);

        List<object> references = [];
        if (response.TryGetProperty("references", out JsonElement refsEl) && refsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement x in refsEl.EnumerateArray())
            {
                references.Add(new
                {
                    sourceType = x.GetStringOrNull("sourceType"),
                    sourceId = x.GetStringOrNull("sourceId"),
                    sourceContentType = x.GetStringOrNull("sourceContentType"),
                    targetType = x.GetStringOrNull("targetType"),
                    targetId = x.GetStringOrNull("targetId"),
                    linkType = x.GetStringOrNull("linkType"),
                    context = x.GetStringOrNull("context"),
                    targetTitle = x.GetStringOrNull("targetTitle"),
                    targetUrl = x.GetStringOrNull("targetUrl"),
                });
            }
        }

        return new
        {
            references = references.ToArray(),
        };
    }
}
