using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class ZulipMessagesTools
{
    [McpServerTool, Description("List Zulip messages with optional pagination.")]
    public static async Task<string> ListZulipMessages(
        IHttpClientFactory httpClientFactory,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/zulip/messages");
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

    [McpServerTool, Description("Get a specific Zulip message by ID.")]
    public static async Task<string> GetZulipMessage(
        IHttpClientFactory httpClientFactory,
        [Description("Message ID")] int id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/zulip/messages/{id}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get messages by a specific Zulip user.")]
    public static async Task<string> GetZulipMessagesByUser(
        IHttpClientFactory httpClientFactory,
        [Description("Username")] string user,
        [Description("Maximum results")] int? limit = null,
        [Description("Pagination offset")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/zulip/messages/by-user/{Uri.EscapeDataString(user)}");
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

    private static string FormatJson(JsonElement root) =>
        $"```json\n{JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true })}\n```";
}
