using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class ZulipItemsTools
{
    [McpServerTool, Description("Get a Zulip item by ID.")]
    public static async Task<string> GetZulipItem(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        [Description("Include full content")] bool? includeContent = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/zulip/items/{Uri.EscapeDataString(id)}";
            if (includeContent == true) url += "?includeContent=true";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List Zulip items with optional pagination.")]
    public static async Task<string> ListZulipItems(
        IHttpClientFactory httpClientFactory,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/zulip/items");
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

    [McpServerTool, Description("Get related items for a Zulip item.")]
    public static async Task<string> GetZulipItemRelated(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        [Description("Maximum results")] int? limit = null,
        [Description("Seed source for relationship discovery")] string? seedSource = null,
        [Description("Seed ID for relationship discovery")] string? seedId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/zulip/items/{Uri.EscapeDataString(id)}/related");
            List<string> query = [];
            if (limit != null) query.Add($"limit={limit.Value}");
            if (seedSource != null) query.Add($"seedSource={Uri.EscapeDataString(seedSource)}");
            if (seedId != null) query.Add($"seedId={Uri.EscapeDataString(seedId)}");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a snapshot of a Zulip item with all related data.")]
    public static async Task<string> GetZulipItemSnapshot(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/zulip/items/{Uri.EscapeDataString(id)}/snapshot";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get the content of a Zulip item in a specific format.")]
    public static async Task<string> GetZulipItemContent(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        [Description("Content format (e.g., markdown, html, plain)")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/zulip/items/{Uri.EscapeDataString(id)}/content";
            if (format != null) url += $"?format={Uri.EscapeDataString(format)}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get comments on a Zulip item.")]
    public static async Task<string> GetZulipItemComments(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/zulip/items/{Uri.EscapeDataString(id)}/comments";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get links from a Zulip item.")]
    public static async Task<string> GetZulipItemLinks(
        IHttpClientFactory httpClientFactory,
        [Description("Item ID")] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/zulip/items/{Uri.EscapeDataString(id)}/links";

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
