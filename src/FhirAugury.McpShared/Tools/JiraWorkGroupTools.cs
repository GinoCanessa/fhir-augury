using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class JiraWorkGroupTools
{
    [McpServerTool, Description(
        "List all Jira work groups joined with the canonical HL7 catalog. " +
        "Each entry includes name, code, nameClean (PascalCase slug suitable for URLs and folder names), " +
        "definition, retired, and issueCount. The response is an envelope object " +
        "with a top-level catalogJoinDegraded boolean (true when the HL7 catalog " +
        "join was empty or missing codes) and an items array. The shared " +
        "WorkGroupResolver accepts any of name, nameClean, or code; orchestrators " +
        "use catalogJoinDegraded as a proceed-with-warning signal.")]
    public static async Task<string> ListJiraWorkGroups(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/jira/work-groups", cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List issues for a specific Jira work group.")]
    public static async Task<string> ListJiraWorkGroupIssues(
        IHttpClientFactory httpClientFactory,
        [Description("Work group code")] string groupCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/jira/work-groups/{Uri.EscapeDataString(groupCode)}/issues";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List issues for all Jira work groups.")]
    public static async Task<string> ListAllJiraWorkGroupIssues(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/jira/work-groups/issues", cancellationToken);
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
