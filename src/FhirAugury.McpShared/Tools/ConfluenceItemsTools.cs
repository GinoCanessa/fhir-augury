using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class ConfluenceItemsTools
{
    [McpServerTool, Description("List Confluence items with optional pagination.")]
    public static async Task<string> ListConfluenceItems(
        IHttpClientFactory httpClientFactory,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/confluence/items");
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

    [McpServerTool, Description("Get a Confluence item by ID.")]
    public static async Task<string> GetConfluenceItem(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        [Description("Include full content")] bool? includeContent = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/confluence/items/{Uri.EscapeDataString(id)}";
            if (includeContent == true) url += "?includeContent=true";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get related items for a Confluence item.")]
    public static async Task<string> GetConfluenceItemRelated(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        [Description("Maximum results")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/confluence/items/{Uri.EscapeDataString(id)}/related");
            if (limit != null) url.Append($"?limit={limit.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a snapshot of a Confluence item.")]
    public static async Task<string> GetConfluenceItemSnapshot(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/confluence/items/{Uri.EscapeDataString(id)}/snapshot";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get the content of a Confluence item.")]
    public static async Task<string> GetConfluenceItemContent(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        [Description("Content format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/confluence/items/{Uri.EscapeDataString(id)}/content");
            if (format != null) url.Append($"?format={Uri.EscapeDataString(format)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List all Confluence spaces.")]
    public static async Task<string> ListConfluenceSpaces(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/confluence/spaces", cancellationToken);
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
