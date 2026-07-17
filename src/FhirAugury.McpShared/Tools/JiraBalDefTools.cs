using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

/// <summary>
/// MCP tools for the Jira Ballot Definition (BALDEF-*) read model. Each tool
/// forwards to the Jira source through the orchestrator
/// (<c>/api/v1/jira/baldef</c>).
/// </summary>
[McpServerToolType]
public static class JiraBalDefTools
{
    [McpServerTool, Description(
        "List Ballot Definition (BALDEF-*) Jira tickets (paged). " +
        "Filters: cycle (ballot cycle), level (ballot category, e.g. STU / Normative), workGroup.")]
    public static async Task<string> ListJiraBalDef(
        IHttpClientFactory httpClientFactory,
        [Description("Optional ballot cycle filter")] string? cycle = null,
        [Description("Optional ballot level/category filter (e.g. STU, Normative)")] string? level = null,
        [Description("Optional work group filter (accepted for parity; ignored upstream for BALDEF)")] string? workGroup = null,
        [Description("Maximum number of records (default 50)")] int? limit = null,
        [Description("Number of records to skip")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            List<string> q = [];
            if (cycle != null) q.Add($"cycle={Uri.EscapeDataString(cycle)}");
            if (level != null) q.Add($"level={Uri.EscapeDataString(level)}");
            if (workGroup != null) q.Add($"workGroup={Uri.EscapeDataString(workGroup)}");
            if (limit != null) q.Add($"limit={limit.Value}");
            if (offset != null) q.Add($"offset={offset.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/jira/baldef" + Query(q), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a single Ballot Definition (BALDEF-*) Jira ticket by key.")]
    public static async Task<string> GetJiraBalDef(
        IHttpClientFactory httpClientFactory,
        [Description("BALDEF ticket key, e.g. BALDEF-1")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/jira/baldef/{Uri.EscapeDataString(key)}";
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
