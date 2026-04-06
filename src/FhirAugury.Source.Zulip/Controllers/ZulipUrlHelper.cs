using System.Globalization;
using System.Text;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Zulip.Controllers;

internal static class ZulipUrlHelper
{
    internal static string BuildMessageUrl(ZulipServiceOptions options, string streamName, string topic, int messageId) =>
        $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}/near/{messageId}";

    internal static string BuildThreadMarkdownSnapshot(SqliteConnection connection, string streamName, string topic)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# [{streamName}] > {topic}");
        sb.AppendLine();

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT SenderName, ContentPlain, Timestamp FROM zulip_messages WHERE StreamName = @streamName AND Topic = @topic ORDER BY Timestamp ASC",
            connection);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@topic", topic);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string sender = reader.GetString(0);
            string content = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string ts = reader.IsDBNull(2) ? "" : reader.GetString(2);

            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt))
                sb.AppendLine($"### {sender} ({dt:yyyy-MM-dd HH:mm})");
            else
                sb.AppendLine($"### {sender}");

            sb.AppendLine();
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? dt
            : null;
    }

    internal static FindRelatedResponse GetCrossSourceRelated(string seedSource, string seedId, int? limit, ZulipDatabase db, ZulipServiceOptions options)
    {
        int maxResults = Math.Min(limit ?? 10, 50);

        if (string.Equals(seedSource, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            using SqliteConnection connection = db.OpenConnection();

            string sql = """
                SELECT tt.StreamName, tt.Topic, tt.ReferenceCount,
                       (SELECT ZulipMessageId FROM zulip_messages
                        WHERE StreamName = tt.StreamName AND Topic = tt.Topic
                        ORDER BY Timestamp DESC LIMIT 1) AS LatestMsgId,
                       (SELECT Timestamp FROM zulip_messages
                        WHERE StreamName = tt.StreamName AND Topic = tt.Topic
                        ORDER BY Timestamp DESC LIMIT 1) AS LatestTimestamp,
                       COALESCE(zs.BaselineValue, 5) AS BaselineValue
                FROM zulip_thread_tickets tt
                LEFT JOIN zulip_streams zs ON zs.Name = tt.StreamName
                WHERE tt.JiraKey = @jiraKey
                ORDER BY tt.LastSeenAt DESC
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@jiraKey", seedId);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            List<RelatedItem> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string streamName = reader.GetString(0);
                string topic = reader.GetString(1);
                int latestMsgId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                int baselineValue = reader.IsDBNull(5) ? 5 : reader.GetInt32(5);

                if (latestMsgId == 0)
                    continue;

                results.Add(new RelatedItem
                {
                    Source = SourceSystems.Zulip,
                    ContentType = ContentTypes.Message,
                    Id = latestMsgId.ToString(),
                    Title = $"[{streamName}] {topic}",
                    RelevanceScore = 1.0 * (baselineValue / 5.0),
                    Url = BuildMessageUrl(options, streamName, topic, latestMsgId),
                    Relationship = "mentioned_in",
                });
            }

            return new FindRelatedResponse(seedSource, seedId, null, results);
        }

        // Unknown seed source: fall back to FTS with seedId as keyword, reduced score
        using SqliteConnection conn = db.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(seedId);
        if (string.IsNullOrEmpty(ftsQuery))
            return new FindRelatedResponse(seedSource, seedId, null, []);

        string ftsSql = """
            SELECT zm.ZulipMessageId, zm.StreamName, zm.Topic,
                   snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   zulip_messages_fts.rank, zm.Timestamp,
                   COALESCE(zs.BaselineValue, 5) as BaselineValue
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            LEFT JOIN zulip_streams zs ON zs.Id = zm.StreamId
            WHERE zulip_messages_fts MATCH @query
            ORDER BY zulip_messages_fts.rank
            LIMIT @limit
            """;

        using SqliteCommand ftsCmd = new SqliteCommand(ftsSql, conn);
        ftsCmd.Parameters.AddWithValue("@query", ftsQuery);
        ftsCmd.Parameters.AddWithValue("@limit", maxResults);

        List<RelatedItem> ftsResults = [];
        using SqliteDataReader ftsReader = ftsCmd.ExecuteReader();
        while (ftsReader.Read())
        {
            int msgId = ftsReader.GetInt32(0);
            string streamName = ftsReader.GetString(1);
            string topic = ftsReader.GetString(2);
            double rawRank = ftsReader.GetDouble(4);
            int baselineValue = ftsReader.GetInt32(6);

            ftsResults.Add(new RelatedItem
            {
                Source = SourceSystems.Zulip,
                ContentType = ContentTypes.Message,
                Id = msgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Snippet = ftsReader.IsDBNull(3) ? null : ftsReader.GetString(3),
                RelevanceScore = (-rawRank * 0.5) * (baselineValue / 5.0),
                Url = BuildMessageUrl(options, streamName, topic, msgId),
            });
        }

        return new FindRelatedResponse(seedSource, seedId, null, ftsResults);
    }
}