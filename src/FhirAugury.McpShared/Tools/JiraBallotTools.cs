using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

/// <summary>
/// MCP tools for the Jira Ballot vote (BALLOT-*) read model. Each tool forwards
/// to the Jira source through the orchestrator (<c>/api/v1/jira/ballot</c>).
/// </summary>
[McpServerToolType]
public static class JiraBallotTools
{
    [McpServerTool, Description(
        "List Ballot vote (BALLOT-*) Jira tickets (paged). " +
        "Filters: cycle (ballot cycle), specification, disposition (maps to status).")]
    public static async Task<string> ListJiraBallot(
        IHttpClientFactory httpClientFactory,
        [Description("Optional ballot cycle filter")] string? cycle = null,
        [Description("Optional specification filter")] string? specification = null,
        [Description("Optional disposition filter (maps to ticket status)")] string? disposition = null,
        [Description("Maximum number of records (default 50)")] int? limit = null,
        [Description("Number of records to skip")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            List<string> q = [];
            if (cycle != null) q.Add($"cycle={Uri.EscapeDataString(cycle)}");
            if (specification != null) q.Add($"specification={Uri.EscapeDataString(specification)}");
            if (disposition != null) q.Add($"disposition={Uri.EscapeDataString(disposition)}");
            if (limit != null) q.Add($"limit={limit.Value}");
            if (offset != null) q.Add($"offset={offset.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/jira/ballot" + Query(q), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a single Ballot vote (BALLOT-*) Jira ticket by key.")]
    public static async Task<string> GetJiraBallot(
        IHttpClientFactory httpClientFactory,
        [Description("BALLOT ticket key, e.g. BALLOT-1")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/jira/ballot/{Uri.EscapeDataString(key)}";
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
