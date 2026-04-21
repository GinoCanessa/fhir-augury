using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class JiraItemsTools
{
    [McpServerTool, Description("Get a Jira issue item by key.")]
    public static async Task<string> GetJiraItem(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key (e.g., FHIR-12345)")] string key,
        [Description("Include full content")] bool? includeContent = null,
        [Description("Include comments")] bool? includeComments = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(key)}");
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

    [McpServerTool, Description("List Jira items with optional pagination.")]
    public static async Task<string> ListJiraItems(
        IHttpClientFactory httpClientFactory,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/jira/items");
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

    [McpServerTool, Description("Get related items for a Jira issue.")]
    public static async Task<string> GetJiraItemRelated(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key")] string key,
        [Description("Seed source for relationship discovery")] string? seedSource = null,
        [Description("Maximum results")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(key)}/related");
            List<string> query = [];
            if (seedSource != null) query.Add($"seedSource={Uri.EscapeDataString(seedSource)}");
            if (limit != null) query.Add($"limit={limit.Value}");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a snapshot of a Jira issue with all related data.")]
    public static async Task<string> GetJiraItemSnapshot(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key")] string key,
        [Description("Include comments")] bool? includeComments = null,
        [Description("Include cross-references")] bool? includeRefs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(key)}/snapshot");
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

    [McpServerTool, Description("Get the content of a Jira issue in a specific format.")]
    public static async Task<string> GetJiraItemContent(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key")] string key,
        [Description("Content format (e.g., markdown, html, plain)")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/jira/items/{Uri.EscapeDataString(key)}/content");
            if (format != null) url.Append($"?format={Uri.EscapeDataString(format)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get links from a Jira issue to other issues, specs, or external resources.")]
    public static async Task<string> GetJiraItemLinks(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/jira/items/{Uri.EscapeDataString(key)}/links";

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
