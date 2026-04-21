using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class ZulipThreadsTools
{
    [McpServerTool, Description("Get a Zulip thread (stream + topic).")]
    public static async Task<string> GetZulipThread(
        IHttpClientFactory httpClientFactory,
        [Description("Stream name")] string streamName,
        [Description("Topic name")] string topic,
        [Description("Maximum messages")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/zulip/threads/{Uri.EscapeDataString(streamName)}/{Uri.EscapeDataString(topic)}");
            if (limit != null) url.Append($"?limit={limit.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a snapshot of a Zulip thread with all metadata.")]
    public static async Task<string> GetZulipThreadSnapshot(
        IHttpClientFactory httpClientFactory,
        [Description("Stream name")] string streamName,
        [Description("Topic name")] string topic,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/zulip/threads/{Uri.EscapeDataString(streamName)}/{Uri.EscapeDataString(topic)}/snapshot";

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
