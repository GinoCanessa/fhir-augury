using System.Text;
using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class JiraItemsHandler
{
    public static async Task<object> HandleAsync(JiraItemsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        return request.Action.ToLowerInvariant() switch
        {
            "get" => await HandleGetAsync(request, client, ct),
            "list" => await HandleListAsync(request, client, ct),
            "related" => await HandleRelatedAsync(request, client, ct),
            "snapshot" => await HandleSnapshotAsync(request, client, ct),
            "content" => await HandleContentAsync(request, client, ct),
            "links" => await HandleLinksAsync(request, client, ct),
            _ => throw new ArgumentException(
                $"Unknown action: {request.Action}. Valid: get, list, related, snapshot, content, links"),
        };
    }

    private static async Task<object> HandleGetAsync(JiraItemsRequest request, HttpServiceClient client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Key))
            throw new ArgumentException("key is required for get action");

        StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(request.Key)}");
        List<string> query = [];
        if (request.IncludeContent == true) query.Add("includeContent=true");
        if (request.IncludeComments == true) query.Add("includeComments=true");
        if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

        JsonElement result = await client.GetFromOrchestratorAsync(url.ToString(), ct);
        return new { data = result };
    }

    private static async Task<object> HandleListAsync(JiraItemsRequest request, HttpServiceClient client, CancellationToken ct)
    {
        StringBuilder url = new("/api/v1/jira/items");
        List<string> query = [];
        if (request.Limit != null) query.Add($"limit={request.Limit.Value}");
        if (request.Offset != null) query.Add($"offset={request.Offset.Value}");
        if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

        JsonElement result = await client.GetFromOrchestratorAsync(url.ToString(), ct);
        return new { data = result };
    }

    private static async Task<object> HandleRelatedAsync(JiraItemsRequest request, HttpServiceClient client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Key))
            throw new ArgumentException("key is required for related action");

        StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(request.Key)}/related");
        List<string> query = [];
        if (request.SeedSource != null) query.Add($"seedSource={Uri.EscapeDataString(request.SeedSource)}");
        if (request.Limit != null) query.Add($"limit={request.Limit.Value}");
        if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

        JsonElement result = await client.GetFromOrchestratorAsync(url.ToString(), ct);
        return new { data = result };
    }

    private static async Task<object> HandleSnapshotAsync(JiraItemsRequest request, HttpServiceClient client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Key))
            throw new ArgumentException("key is required for snapshot action");

        StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(request.Key)}/snapshot");
        List<string> query = [];
        if (request.IncludeComments == true) query.Add("includeComments=true");
        if (request.IncludeRefs == true) query.Add("includeRefs=true");
        if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

        JsonElement result = await client.GetFromOrchestratorAsync(url.ToString(), ct);
        return new { data = result };
    }

    private static async Task<object> HandleContentAsync(JiraItemsRequest request, HttpServiceClient client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Key))
            throw new ArgumentException("key is required for content action");

        StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(request.Key)}/content");
        if (request.Format != null) url.Append($"?format={Uri.EscapeDataString(request.Format)}");

        JsonElement result = await client.GetFromOrchestratorAsync(url.ToString(), ct);
        return new { data = result };
    }

    private static async Task<object> HandleLinksAsync(JiraItemsRequest request, HttpServiceClient client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Key))
            throw new ArgumentException("key is required for links action");

        string url = $"/api/v1/jira/items/{Uri.EscapeDataString(request.Key)}/links";
        JsonElement result = await client.GetFromOrchestratorAsync(url, ct);
        return new { data = result };
    }
}
