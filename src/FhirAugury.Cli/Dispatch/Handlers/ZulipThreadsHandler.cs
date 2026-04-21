using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ZulipThreadsHandler
{
    public static async Task<object> HandleAsync(ZulipThreadsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (string.IsNullOrEmpty(request.StreamName) || string.IsNullOrEmpty(request.Topic))
            throw new ArgumentException("streamName and topic are required");

        string streamName = Uri.EscapeDataString(request.StreamName);
        string topic = Uri.EscapeDataString(request.Topic);
        return action switch
        {
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/threads/{streamName}/{topic}", ct) },
            "snapshot" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/threads/{streamName}/{topic}/snapshot", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: get, snapshot"),
        };
    }
}
