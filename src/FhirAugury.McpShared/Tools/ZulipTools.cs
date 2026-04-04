using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class ZulipTools
{
    [McpServerTool, Description("Search Zulip chat messages.")]
    public static async Task<string> SearchZulip(
        IHttpClientFactory httpClientFactory,
        [Description("Search query")] string query,
        [Description("Filter to specific stream name")] string? stream = null,
        [Description("Maximum results (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("zulip");
            StringBuilder url = new($"/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}");
            if (!string.IsNullOrEmpty(stream))
                url.Append($"&stream={Uri.EscapeDataString(stream)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return UnifiedTools.FormatSearchResults(root, query);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a full Zulip topic thread with all messages.")]
    public static async Task<string> GetZulipThread(
        IHttpClientFactory httpClientFactory,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic,
        [Description("Maximum messages (default 100)")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("zulip");
            string url = $"/api/v1/threads/{Uri.EscapeDataString(stream)}/{Uri.EscapeDataString(topic)}?limit={limit}";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);

            JsonElement messages = root.GetProperty("messages");
            if (messages.GetArrayLength() == 0)
                return $"No messages found in stream '{stream}', topic '{topic}'.";

            string streamName = UnifiedTools.GetNullableString(root, "stream") ?? stream;
            string topicName = UnifiedTools.GetNullableString(root, "topic") ?? topic;

            StringBuilder sb = new();
            sb.AppendLine($"# {streamName} > {topicName}");
            sb.AppendLine();
            sb.AppendLine($"**Messages:** {messages.GetArrayLength()}");

            // First and last message timestamps
            JsonElement first = messages[0];
            JsonElement last = messages[messages.GetArrayLength() - 1];
            string? firstTs = UnifiedTools.GetNullableString(first, "timestamp");
            string? lastTs = UnifiedTools.GetNullableString(last, "timestamp");
            if (firstTs is not null) sb.AppendLine($"**First message:** {firstTs}");
            if (lastTs is not null) sb.AppendLine($"**Last message:** {lastTs}");

            // Participants
            HashSet<string> participants = [];
            foreach (JsonElement msg in messages.EnumerateArray())
            {
                string? sender = UnifiedTools.GetNullableString(msg, "sender");
                if (sender is not null) participants.Add(sender);
            }
            if (participants.Count > 0)
                sb.AppendLine($"**Participants:** {string.Join(", ", participants)}");
            sb.AppendLine();

            sb.AppendLine("## Messages");
            sb.AppendLine();
            foreach (JsonElement msg in messages.EnumerateArray())
            {
                string sender = UnifiedTools.GetString(msg, "sender");
                string? ts = UnifiedTools.GetNullableString(msg, "timestamp");
                sb.AppendLine($"### {sender} — {ts}");
                sb.AppendLine(UnifiedTools.GetNullableString(msg, "content") ?? "");
                sb.AppendLine();
            }

            string? threadUrl = UnifiedTools.GetNullableString(root, "url");
            if (!string.IsNullOrEmpty(threadUrl))
            {
                sb.AppendLine("---");
                sb.AppendLine($"*URL: {threadUrl}*");
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Query Zulip messages with structured filters (streams, topics, senders, dates).")]
    public static async Task<string> QueryZulipMessages(
        IHttpClientFactory httpClientFactory,
        [Description("Filter by stream names (comma-separated)")] string? streams = null,
        [Description("Filter by topic name")] string? topic = null,
        [Description("Filter by topic keyword (partial match)")] string? topicKeyword = null,
        [Description("Filter by sender names (comma-separated)")] string? senders = null,
        [Description("Text query")] string? query = null,
        [Description("Sort by field (default timestamp)")] string sortBy = "timestamp",
        [Description("Sort order: asc or desc (default desc)")] string sortOrder = "desc",
        [Description("Maximum results (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("zulip");
            object body = new
            {
                streamNames = ParseCsv(streams),
                senderNames = ParseCsv(senders),
                topic = topic ?? "",
                topicKeyword = topicKeyword ?? "",
                query = query ?? "",
                sortBy,
                sortOrder,
                limit,
            };

            JsonElement root = await UnifiedTools.PostJsonBodyAsync(client, "/api/v1/query", body, cancellationToken);

            JsonElement results = root.TryGetProperty("results", out JsonElement rEl) ? rEl : root;
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return "No Zulip messages matched the query.";

            StringBuilder sb = new();
            sb.AppendLine("## Zulip Query Results");
            sb.AppendLine();

            foreach (JsonElement msg in results.EnumerateArray())
            {
                string streamName = UnifiedTools.GetNullableString(msg, "streamName")
                                    ?? UnifiedTools.GetString(msg, "stream");
                string msgTopic = UnifiedTools.GetString(msg, "topic");
                string sender = UnifiedTools.GetNullableString(msg, "senderName")
                                ?? UnifiedTools.GetString(msg, "sender");
                sb.AppendLine($"- **{streamName} > {msgTopic}** [{sender}]");

                string? snippet = UnifiedTools.GetNullableString(msg, "snippet");
                if (!string.IsNullOrEmpty(snippet))
                    sb.AppendLine($"  {snippet}");
                string? ts = UnifiedTools.GetNullableString(msg, "timestamp");
                if (!string.IsNullOrEmpty(ts))
                    sb.AppendLine($"  {ts}");
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List available Zulip streams.")]
    public static async Task<string> ListZulipStreams(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("zulip");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/streams", cancellationToken);

            JsonElement streams = root.TryGetProperty("streams", out JsonElement sEl) ? sEl : root;
            if (streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
                return "No Zulip streams found.";

            StringBuilder sb = new();
            sb.AppendLine("## Zulip Streams");
            sb.AppendLine();

            foreach (JsonElement s in streams.EnumerateArray())
            {
                string name = UnifiedTools.GetString(s, "name");
                int msgCount = s.TryGetProperty("messageCount", out JsonElement mcEl) ? mcEl.GetInt32() : 0;
                sb.AppendLine($"- **{name}** ({msgCount} messages)");
                string? desc = UnifiedTools.GetNullableString(s, "description");
                if (!string.IsNullOrEmpty(desc))
                    sb.AppendLine($"  {desc}");
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List topics in a Zulip stream.")]
    public static async Task<string> ListZulipTopics(
        IHttpClientFactory httpClientFactory,
        [Description("Stream name")] string stream,
        [Description("Maximum topics (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("zulip");
            string url = $"/api/v1/streams/{Uri.EscapeDataString(stream)}/topics?limit={limit}";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);

            JsonElement topics = root.TryGetProperty("topics", out JsonElement tEl) ? tEl : root;
            if (topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() == 0)
                return $"No topics found in stream '{stream}'.";

            StringBuilder sb = new();
            sb.AppendLine($"## Topics in {stream}");
            sb.AppendLine();

            foreach (JsonElement t in topics.EnumerateArray())
            {
                string topicName = UnifiedTools.GetString(t, "topic");
                int msgCount = t.TryGetProperty("messageCount", out JsonElement mcEl) ? mcEl.GetInt32() : 0;
                sb.AppendLine($"- **{topicName}** ({msgCount} messages)");
                string? lastMsg = UnifiedTools.GetNullableString(t, "lastMessageAt");
                if (!string.IsNullOrEmpty(lastMsg))
                    sb.AppendLine($"  Last message: {lastMsg}");
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Zulip topic thread.")]
    public static async Task<string> SnapshotZulipThread(
        IHttpClientFactory httpClientFactory,
        [Description("Item identifier (message ID or thread key)")] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("zulip");
            string url = $"/api/v1/items/{Uri.EscapeDataString(id)}/snapshot";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return UnifiedTools.GetString(root, "markdown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static List<string> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
