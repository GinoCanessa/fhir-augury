using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ConfluenceItemsHandler
{
    public static async Task<object> HandleAsync(ConfluenceItemsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (action == "list")
            return new { data = await client.GetFromOrchestratorAsync("/api/v1/confluence/items", ct) };
        if (action == "spaces")
            return new { data = await client.GetFromOrchestratorAsync("/api/v1/confluence/spaces", ct) };

        if (string.IsNullOrEmpty(request.Id))
            throw new ArgumentException($"Confluence items action '{action}' requires an id.");

        string id = Uri.EscapeDataString(request.Id);
        return action switch
        {
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/items/{id}", ct) },
            "related" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/items/{id}/related", ct) },
            "snapshot" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/items/{id}/snapshot", ct) },
            "content" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/items/{id}/content", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get, related, snapshot, content, spaces"),
        };
    }
}
