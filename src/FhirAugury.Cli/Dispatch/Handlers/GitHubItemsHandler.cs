using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class GitHubItemsHandler
{
    public static async Task<object> HandleAsync(GitHubItemsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (action == "list")
        {
            List<string> query = [];
            if (request.PullRequest.HasValue)
                query.Add($"pullRequest={(request.PullRequest.Value ? "true" : "false")}");
            if (request.Limit.HasValue) query.Add($"limit={request.Limit.Value}");
            if (request.Offset.HasValue) query.Add($"offset={request.Offset.Value}");
            string listPath = "/api/v1/github/items" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            return new { data = await client.GetFromOrchestratorAsync(listPath, ct) };
        }

        if (string.IsNullOrEmpty(request.Key))
            throw new ArgumentException($"GitHub items action '{action}' requires a key.");

        string encodedKey = request.Key.Replace("#", "%23");

        return action switch
        {
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/items/{encodedKey}", ct) },
            "related" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/items/related/{encodedKey}", ct) },
            "snapshot" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/items/snapshot/{encodedKey}", ct) },
            "content" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/items/content/{encodedKey}", ct) },
            "comments" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/items/comments/{encodedKey}", ct) },
            "commits" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/items/commits/{encodedKey}", ct) },
            "pr" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/items/pr/{encodedKey}", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid actions: list, get, related, snapshot, content, comments, commits, pr"),
        };
    }
}
