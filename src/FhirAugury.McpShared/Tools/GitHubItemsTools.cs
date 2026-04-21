using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class GitHubItemsTools
{
    [McpServerTool, Description("List GitHub items with optional pagination.")]
    public static async Task<string> ListGitHubItems(
        IHttpClientFactory httpClientFactory,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/github/items");
            List<string> query = [];
            if (limit != null) query.Add($"limit={limit.Value}");
            if (offset != null) query.Add($"offset={offset.Value}");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a GitHub item by key (action-first routing).")]
    public static async Task<string> GetGitHubItem(
        IHttpClientFactory httpClientFactory,
        [Description("Item key (e.g., owner/repo#123)")] string key,
        [Description("Include full content")] bool? includeContent = null,
        [Description("Include comments")] bool? includeComments = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encoded = key.Replace("#", "%23");
            StringBuilder url = new($"/api/v1/github/items/{encoded}");
            List<string> query = [];
            if (includeContent == true) query.Add("includeContent=true");
            if (includeComments == true) query.Add("includeComments=true");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get related items for a GitHub item.")]
    public static async Task<string> GetGitHubItemRelated(
        IHttpClientFactory httpClientFactory,
        [Description("Item key")] string key,
        [Description("Maximum results")] int? limit = null,
        [Description("Seed source")] string? seedSource = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encoded = key.Replace("#", "%23");
            StringBuilder url = new($"/api/v1/github/items/related/{encoded}");
            List<string> query = [];
            if (limit != null) query.Add($"limit={limit.Value}");
            if (seedSource != null) query.Add($"seedSource={Uri.EscapeDataString(seedSource)}");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a snapshot of a GitHub item.")]
    public static async Task<string> GetGitHubItemSnapshot(
        IHttpClientFactory httpClientFactory,
        [Description("Item key")] string key,
        [Description("Include comments")] bool? includeComments = null,
        [Description("Include cross-references")] bool? includeRefs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encoded = key.Replace("#", "%23");
            StringBuilder url = new($"/api/v1/github/items/snapshot/{encoded}");
            List<string> query = [];
            if (includeComments == true) query.Add("includeComments=true");
            if (includeRefs == true) query.Add("includeRefs=true");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get the content of a GitHub item.")]
    public static async Task<string> GetGitHubItemContent(
        IHttpClientFactory httpClientFactory,
        [Description("Item key")] string key,
        [Description("Content format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encoded = key.Replace("#", "%23");
            StringBuilder url = new($"/api/v1/github/items/content/{encoded}");
            if (format != null) url.Append($"?format={Uri.EscapeDataString(format)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get comments on a GitHub item.")]
    public static async Task<string> GetGitHubItemComments(
        IHttpClientFactory httpClientFactory,
        [Description("Item key")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encoded = key.Replace("#", "%23");
            string url = $"/api/v1/github/items/comments/{encoded}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get commits for a GitHub item.")]
    public static async Task<string> GetGitHubItemCommits(
        IHttpClientFactory httpClientFactory,
        [Description("Item key")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encoded = key.Replace("#", "%23");
            string url = $"/api/v1/github/items/commits/{encoded}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get pull request details for a GitHub item.")]
    public static async Task<string> GetGitHubItemPullRequest(
        IHttpClientFactory httpClientFactory,
        [Description("Item key")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encoded = key.Replace("#", "%23");
            string url = $"/api/v1/github/items/pr/{encoded}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string FormatJson(JsonElement root) =>
        $"```json\n{JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true })}\n```";
}
