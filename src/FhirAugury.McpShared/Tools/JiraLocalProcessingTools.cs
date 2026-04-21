using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class JiraLocalProcessingTools
{
    [McpServerTool, Description("List tickets marked for local processing.")]
    public static async Task<string> ListLocalProcessingTickets(
        IHttpClientFactory httpClientFactory,
        [Description("Optional JSON filter body")] string? bodyJson = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await PostWithOptionalBody(client, "/api/v1/jira/local-processing/tickets", bodyJson, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a random ticket for local processing.")]
    public static async Task<string> GetRandomLocalProcessingTicket(
        IHttpClientFactory httpClientFactory,
        [Description("Optional JSON filter body")] string? bodyJson = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await PostWithOptionalBody(client, "/api/v1/jira/local-processing/random-ticket", bodyJson, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Mark a ticket as locally processed.")]
    public static async Task<string> SetLocalProcessingTicket(
        IHttpClientFactory httpClientFactory,
        [Description("JSON body with ticket key and processing metadata")] string? bodyJson = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await PostWithOptionalBody(client, "/api/v1/jira/local-processing/set-processed", bodyJson, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Clear all local processing markers.")]
    public static async Task<string> ClearAllLocalProcessing(
        IHttpClientFactory httpClientFactory,
        [Description("Optional JSON confirmation body")] string? bodyJson = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await PostWithOptionalBody(client, "/api/v1/jira/local-processing/clear-all-processed", bodyJson, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<JsonElement> PostWithOptionalBody(HttpClient client, string url, string? bodyJson, CancellationToken ct)
    {
        if (bodyJson == null)
            return await UnifiedTools.PostJsonAsync(client, url, ct);
        return await UnifiedTools.PutJsonBodyAsync(client, url, bodyJson, ct);
    }

    private static string FormatJson(JsonElement root) =>
        $"```json\n{JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true })}\n```";
}
