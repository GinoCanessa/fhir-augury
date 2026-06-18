using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

/// <summary>
/// MCP tools for the Jira Project Scope Statement (PSS-*) read model. Each tool
/// forwards to the Jira source through the orchestrator (<c>/api/v1/jira/pss</c>).
/// </summary>
[McpServerToolType]
public static class JiraPssTools
{
    [McpServerTool, Description(
        "List Project Scope Statement (PSS-*) Jira tickets (paged). " +
        "Filters: workGroup (sponsoring work group), status.")]
    public static async Task<string> ListJiraPss(
        IHttpClientFactory httpClientFactory,
        [Description("Optional sponsoring work group filter")] string? workGroup = null,
        [Description("Optional status filter")] string? status = null,
        [Description("Maximum number of records (default 50)")] int? limit = null,
        [Description("Number of records to skip")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            List<string> q = [];
            if (workGroup != null) q.Add($"workGroup={Uri.EscapeDataString(workGroup)}");
            if (status != null) q.Add($"status={Uri.EscapeDataString(status)}");
            if (limit != null) q.Add($"limit={limit.Value}");
            if (offset != null) q.Add($"offset={offset.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/jira/pss" + Query(q), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a single Project Scope Statement (PSS-*) Jira ticket by key.")]
    public static async Task<string> GetJiraPss(
        IHttpClientFactory httpClientFactory,
        [Description("PSS ticket key, e.g. PSS-1")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/jira/pss/{Uri.EscapeDataString(key)}";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string Query(List<string> parts)
        => parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);

    private static string FormatJson(JsonElement root) =>
        $"```json\n{JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true })}\n```";
}
