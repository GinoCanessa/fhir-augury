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
        [Description("Maximum results (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SearchRequest request = new SearchRequest { Query = query, Limit = limit };
            if (!string.IsNullOrEmpty(stream))
                request.Filters.Add("stream", stream);

            SearchResponse response = await zulipSource.SearchAsync(request, cancellationToken: cancellationToken);
            return UnifiedTools.FormatSearchResults(response, query);
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a full Zulip topic thread with all messages.")]
    public static async Task<string> GetZulipThread(
        ZulipService.ZulipServiceClient zulip,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic,
        [Description("Maximum messages (default 100)")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ZulipThread response = await zulip.GetThreadAsync(
                new ZulipGetThreadRequest { StreamName = stream, Topic = topic, Limit = limit },
                cancellationToken: cancellationToken);

            if (response.Messages.Count == 0)
                return $"No messages found in stream '{stream}', topic '{topic}'.";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"# {response.StreamName} > {response.Topic}");
            sb.AppendLine();
            sb.AppendLine($"**Messages:** {response.Messages.Count}");

            ZulipMessage first = response.Messages[0];
            ZulipMessage last = response.Messages[^1];
            sb.AppendLine($"**First message:** {first.Timestamp?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
            sb.AppendLine($"**Last message:** {last.Timestamp?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");

            List<string> participants = response.Messages.Select(m => m.SenderName).Distinct().ToList();
            sb.AppendLine($"**Participants:** {string.Join(", ", participants)}");
            sb.AppendLine();

            sb.AppendLine("## Messages");
            sb.AppendLine();
            foreach (ZulipMessage? msg in response.Messages)
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
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
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
        [Description("Maximum results (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ZulipQueryRequest request = new ZulipQueryRequest
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
                foreach (string s in streams.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    request.StreamNames.Add(s);
            }

            if (!string.IsNullOrWhiteSpace(senders))
            {
                foreach (string s in senders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    request.SenderNames.Add(s);
            }

            using AsyncServerStreamingCall<ZulipMessageSummary> call = zulip.QueryMessages(request, cancellationToken: cancellationToken);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("## Zulip Query Results");
            sb.AppendLine();

            int count = 0;
            await foreach (ZulipMessageSummary? msg in call.ResponseStream.ReadAllAsync(cancellationToken))
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
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List available Zulip streams.")]
    public static async Task<string> ListZulipStreams(
        ZulipService.ZulipServiceClient zulip,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AsyncServerStreamingCall<ZulipStream> call = zulip.ListStreams(new ZulipListStreamsRequest(),
                cancellationToken: cancellationToken);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("## Zulip Streams");
            sb.AppendLine();

            int count = 0;
            await foreach (ZulipStream? stream in call.ResponseStream.ReadAllAsync(cancellationToken))
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
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List topics in a Zulip stream.")]
    public static async Task<string> ListZulipTopics(
        ZulipService.ZulipServiceClient zulip,
        [Description("Stream name")] string stream,
        [Description("Maximum topics (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AsyncServerStreamingCall<ZulipTopic> call = zulip.ListTopics(
                new ZulipListTopicsRequest { StreamName = stream, Limit = limit },
                cancellationToken: cancellationToken);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"## Topics in {stream}");
            sb.AppendLine();

            int count = 0;
            await foreach (ZulipTopic? topic in call.ResponseStream.ReadAllAsync(cancellationToken))
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
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Zulip topic thread.")]
    public static async Task<string> SnapshotZulipThread(
        ZulipService.ZulipServiceClient zulip,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SnapshotResponse response = await zulip.GetThreadSnapshotAsync(
                new ZulipSnapshotRequest { StreamName = stream, Topic = topic, IncludeInternalRefs = true },
                cancellationToken: cancellationToken);
            return response.Markdown;
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }
}
