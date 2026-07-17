using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ZulipMessagesHandler
{
    public static async Task<object> HandleAsync(ZulipMessagesRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        return action switch
        {
            "list" => new { data = await client.GetFromOrchestratorAsync("/api/v1/zulip/messages", ct) },
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/messages/{request.Id}", ct) },
            "by-user" => string.IsNullOrEmpty(request.User)
                ? throw new ArgumentException("user is required for by-user action")
                : new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/messages/by-user/{Uri.EscapeDataString(request.User)}", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get, by-user"),
        };
    }
}
