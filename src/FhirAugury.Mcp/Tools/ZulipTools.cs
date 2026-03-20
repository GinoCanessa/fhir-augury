using System.ComponentModel;
using System.Text;
using Fhiraugury;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class ZulipTools
{
    [McpServerTool, Description("Search Zulip chat messages.")]
    public static async Task<string> SearchZulip(
        [FromKeyedServices("zulip")] SourceService.SourceServiceClient zulipSource,
        [Description("Search query")] string query,
        [Description("Filter to specific stream name")] string? stream = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    {
        var request = new SearchRequest { Query = query, Limit = limit };
        if (!string.IsNullOrEmpty(stream))
            request.Filters.Add("stream", stream);

        var response = await zulipSource.SearchAsync(request);
        return UnifiedTools.FormatSearchResults(response, query);
    }

    [McpServerTool, Description("Get a full Zulip topic thread with all messages.")]
    public static async Task<string> GetZulipThread(
        ZulipService.ZulipServiceClient zulip,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic,
        [Description("Maximum messages (default 100)")] int limit = 100)
    {
        var response = await zulip.GetThreadAsync(
            new ZulipGetThreadRequest { StreamName = stream, Topic = topic, Limit = limit });

        if (response.Messages.Count == 0)
            return $"No messages found in stream '{stream}', topic '{topic}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {response.StreamName} > {response.Topic}");
        sb.AppendLine();
        sb.AppendLine($"**Messages:** {response.Messages.Count}");

        var first = response.Messages[0];
        var last = response.Messages[^1];
        sb.AppendLine($"**First message:** {first.Timestamp?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
        sb.AppendLine($"**Last message:** {last.Timestamp?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");

        var participants = response.Messages.Select(m => m.SenderName).Distinct().ToList();
        sb.AppendLine($"**Participants:** {string.Join(", ", participants)}");
        sb.AppendLine();

        sb.AppendLine("## Messages");
        sb.AppendLine();
        foreach (var msg in response.Messages)
        {
            sb.AppendLine($"### {msg.SenderName} — {msg.Timestamp?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(response.Url))
        {
            sb.AppendLine("---");
            sb.AppendLine($"*URL: {response.Url}*");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Query Zulip messages with structured filters (streams, topics, senders, dates).")]
    public static async Task<string> QueryZulipMessages(
        ZulipService.ZulipServiceClient zulip,
        [Description("Filter by stream names (comma-separated)")] string? streams = null,
        [Description("Filter by topic name")] string? topic = null,
        [Description("Filter by topic keyword (partial match)")] string? topicKeyword = null,
        [Description("Filter by sender names (comma-separated)")] string? senders = null,
        [Description("Text query")] string? query = null,
        [Description("Sort by field (default timestamp)")] string sortBy = "timestamp",
        [Description("Sort order: asc or desc (default desc)")] string sortOrder = "desc",
        [Description("Maximum results (default 20)")] int limit = 20)
    {
        var request = new ZulipQueryRequest
        {
            Topic = topic ?? "",
            TopicKeyword = topicKeyword ?? "",
            Query = query ?? "",
            SortBy = sortBy,
            SortOrder = sortOrder,
            Limit = limit,
        };

        if (!string.IsNullOrWhiteSpace(streams))
        {
            foreach (var s in streams.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                request.StreamNames.Add(s);
        }

        if (!string.IsNullOrWhiteSpace(senders))
        {
            foreach (var s in senders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                request.SenderNames.Add(s);
        }

        using var call = zulip.QueryMessages(request);

        var sb = new StringBuilder();
        sb.AppendLine("## Zulip Query Results");
        sb.AppendLine();

        var count = 0;
        await foreach (var msg in call.ResponseStream.ReadAllAsync())
        {
            sb.AppendLine($"- **{msg.StreamName} > {msg.Topic}** [{msg.SenderName}]");
            if (!string.IsNullOrEmpty(msg.Snippet))
                sb.AppendLine($"  {msg.Snippet}");
            if (msg.Timestamp is not null)
                sb.AppendLine($"  {msg.Timestamp.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
            count++;
        }

        if (count == 0)
            return "No Zulip messages matched the query.";

        return sb.ToString();
    }

    [McpServerTool, Description("List available Zulip streams.")]
    public static async Task<string> ListZulipStreams(
        ZulipService.ZulipServiceClient zulip)
    {
        using var call = zulip.ListStreams(new ZulipListStreamsRequest());

        var sb = new StringBuilder();
        sb.AppendLine("## Zulip Streams");
        sb.AppendLine();

        var count = 0;
        await foreach (var stream in call.ResponseStream.ReadAllAsync())
        {
            sb.AppendLine($"- **{stream.Name}** ({stream.MessageCount} messages)");
            if (!string.IsNullOrEmpty(stream.Description))
                sb.AppendLine($"  {stream.Description}");
            count++;
        }

        if (count == 0)
            return "No Zulip streams found.";

        return sb.ToString();
    }

    [McpServerTool, Description("List topics in a Zulip stream.")]
    public static async Task<string> ListZulipTopics(
        ZulipService.ZulipServiceClient zulip,
        [Description("Stream name")] string stream,
        [Description("Maximum topics (default 50)")] int limit = 50)
    {
        using var call = zulip.ListTopics(
            new ZulipListTopicsRequest { StreamName = stream, Limit = limit });

        var sb = new StringBuilder();
        sb.AppendLine($"## Topics in {stream}");
        sb.AppendLine();

        var count = 0;
        await foreach (var topic in call.ResponseStream.ReadAllAsync())
        {
            sb.AppendLine($"- **{topic.Topic}** ({topic.MessageCount} messages)");
            if (topic.LastMessageAt is not null)
                sb.AppendLine($"  Last message: {topic.LastMessageAt.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
            count++;
        }

        if (count == 0)
            return $"No topics found in stream '{stream}'.";

        return sb.ToString();
    }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Zulip topic thread.")]
    public static async Task<string> SnapshotZulipThread(
        ZulipService.ZulipServiceClient zulip,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic)
    {
        var response = await zulip.GetThreadSnapshotAsync(
            new ZulipSnapshotRequest { StreamName = stream, Topic = topic, IncludeInternalRefs = true });
        return response.Markdown;
    }
}
