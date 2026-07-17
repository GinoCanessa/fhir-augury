using System.Text;
using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ZulipStreamsHandler
{
    public static async Task<object> HandleAsync(ZulipStreamsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        return action switch
        {
            "list" => new { data = await client.GetFromOrchestratorAsync("/api/v1/zulip/streams", ct) },
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/streams/{request.ZulipStreamId}", ct) },
            "update" => new
            {
                data = await client.PutToOrchestratorAsync(
                    $"/api/v1/zulip/streams/{request.ZulipStreamId}",
                    request.Body.HasValue ? JsonSerializer.Serialize(request.Body.Value) : null,
                    ct),
            },
            "topics" => string.IsNullOrEmpty(request.StreamName)
                ? throw new ArgumentException("streamName is required for topics action")
                : new { data = await client.GetFromOrchestratorAsync(BuildTopicsUrl(request), ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get, update, topics"),
        };
    }

    private static string BuildTopicsUrl(ZulipStreamsRequest request)
    {
        StringBuilder url = new($"/api/v1/zulip/streams/topics?streamName={Uri.EscapeDataString(request.StreamName!)}");
        if (request.Limit != null) url.Append($"&limit={request.Limit.Value}");
        if (request.Offset != null) url.Append($"&offset={request.Offset.Value}");
        return url.ToString();
    }
}
