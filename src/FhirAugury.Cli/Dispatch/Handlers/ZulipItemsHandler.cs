using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ZulipItemsHandler
{
    public static async Task<object> HandleAsync(ZulipItemsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (action == "list")
            return new { data = await client.GetFromOrchestratorAsync("/api/v1/zulip/items", ct) };

        if (string.IsNullOrEmpty(request.Id))
            throw new ArgumentException($"Zulip items action '{action}' requires an id.");

        string id = Uri.EscapeDataString(request.Id);
        return action switch
        {
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/items/{id}", ct) },
            "related" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/items/{id}/related", ct) },
            "snapshot" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/items/{id}/snapshot", ct) },
            "content" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/items/{id}/content", ct) },
            "comments" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/items/{id}/comments", ct) },
            "links" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/zulip/items/{id}/links", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get, related, snapshot, content, comments, links"),
        };
    }
}
