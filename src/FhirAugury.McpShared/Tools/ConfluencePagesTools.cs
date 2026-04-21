using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class ConfluencePagesTools
{
    [McpServerTool, Description("List Confluence pages with optional filtering.")]
    public static async Task<string> ListConfluencePages(
        IHttpClientFactory httpClientFactory,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        [Description("Filter by space key")] string? spaceKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/confluence/pages");
            List<string> query = [];
            if (limit != null) query.Add($"limit={limit.Value}");
            if (offset != null) query.Add($"offset={offset.Value}");
            if (spaceKey != null) query.Add($"spaceKey={Uri.EscapeDataString(spaceKey)}");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a specific Confluence page by ID.")]
    public static async Task<string> GetConfluencePage(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get related pages for a Confluence page.")]
    public static async Task<string> GetConfluencePageRelated(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        [Description("Maximum results")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}/related");
            if (limit != null) url.Append($"?limit={limit.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a snapshot of a Confluence page.")]
    public static async Task<string> GetConfluencePageSnapshot(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}/snapshot";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get the content of a Confluence page.")]
    public static async Task<string> GetConfluencePageContent(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        [Description("Content format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}/content");
            if (format != null) url.Append($"?format={Uri.EscapeDataString(format)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get comments on a Confluence page.")]
    public static async Task<string> GetConfluencePageComments(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}/comments";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get child pages of a Confluence page.")]
    public static async Task<string> GetConfluencePageChildren(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}/children";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get ancestor pages of a Confluence page.")]
    public static async Task<string> GetConfluencePageAncestors(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}/ancestors";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get pages linked to/from a Confluence page.")]
    public static async Task<string> GetConfluencePageLinked(
        IHttpClientFactory httpClientFactory,
        [Description("Page ID")] string pageId,
        [Description("Link direction: incoming, outgoing, or both")] string? direction = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/confluence/pages/{Uri.EscapeDataString(pageId)}/linked");
            if (direction != null) url.Append($"?direction={Uri.EscapeDataString(direction)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List Confluence pages by label.")]
    public static async Task<string> ListConfluencePagesByLabel(
        IHttpClientFactory httpClientFactory,
        [Description("Label name")] string label,
        [Description("Filter by space key")] string? spaceKey = null,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/confluence/pages/by-label/{Uri.EscapeDataString(label)}");
            List<string> query = [];
            if (spaceKey != null) query.Add($"spaceKey={Uri.EscapeDataString(spaceKey)}");
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

    private static string FormatJson(JsonElement root) =>
        $"```json\n{JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true })}\n```";
}
