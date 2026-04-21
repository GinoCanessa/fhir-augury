using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class JiraProjectTools
{
    [McpServerTool, Description("List all Jira projects.")]
    public static async Task<string> ListJiraProjects(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/jira/projects", cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get details of a specific Jira project.")]
    public static async Task<string> GetJiraProject(
        IHttpClientFactory httpClientFactory,
        [Description("Project key")] string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/jira/projects/{Uri.EscapeDataString(key)}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Update a Jira project's configuration or metadata.")]
    public static async Task<string> UpdateJiraProject(
        IHttpClientFactory httpClientFactory,
        [Description("Project key")] string key,
        [Description("JSON body with project updates")] string body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/jira/projects/{Uri.EscapeDataString(key)}";

            JsonElement root = await UnifiedTools.PutJsonBodyAsync(client, url, body, cancellationToken);
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
