using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ConfluencePagesHandler
{
    public static async Task<object> HandleAsync(ConfluencePagesRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (action == "list")
            return new { data = await client.GetFromOrchestratorAsync("/api/v1/confluence/pages", ct) };

        if (action == "by-label")
        {
            if (string.IsNullOrEmpty(request.Label))
                throw new ArgumentException("label is required for by-label action");
            string label = Uri.EscapeDataString(request.Label);
            return new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/by-label/{label}", ct) };
        }

        if (string.IsNullOrEmpty(request.PageId))
            throw new ArgumentException($"Confluence pages action '{action}' requires a pageId.");

        string pageId = Uri.EscapeDataString(request.PageId);
        return action switch
        {
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}", ct) },
            "related" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}/related", ct) },
            "snapshot" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}/snapshot", ct) },
            "content" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}/content", ct) },
            "comments" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}/comments", ct) },
            "children" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}/children", ct) },
            "ancestors" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}/ancestors", ct) },
            "linked" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/confluence/pages/{pageId}/linked", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get, related, snapshot, content, comments, children, ancestors, linked, by-label"),
        };
    }
}
