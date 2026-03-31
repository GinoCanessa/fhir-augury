using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Zulip.Indexing;

/// <summary>
/// Scans Zulip messages for cross-references (Jira, GitHub, Confluence, FHIR elements)
/// using shared extractors, and populates xref tables and thread-ticket link tables.
/// </summary>
public class ZulipTicketIndexer(ZulipDatabase database, ILogger<ZulipTicketIndexer> logger)
{
    /// <summary>
    /// Rebuilds all cross-reference and ticket reference tables from scratch.
    /// Scans every message, runs all extractors, and rebuilds thread-ticket aggregation.
    /// </summary>
    public void RebuildFullIndex(CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        // Clear existing xref and ticket data
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM xref_jira;
                DELETE FROM xref_github;
                DELETE FROM xref_confluence;
                DELETE FROM xref_fhir_element;
                DELETE FROM zulip_thread_tickets;
                """;
            cmd.ExecuteNonQuery();
        }

        // Scan all messages and extract cross-references
        List<ZulipMessageRecord> messages = ZulipMessageRecord.SelectList(connection);
        int refCount = 0;

        foreach (ZulipMessageRecord message in messages)
        {
            ct.ThrowIfCancellationRequested();

            string messageText = GetMessageText(message);
            if (string.IsNullOrWhiteSpace(messageText)) continue;

            string sourceId = message.ZulipMessageId.ToString();
            refCount += ExtractAndInsertRefs(connection, sourceId, messageText);
        }

        // Aggregate thread-level ticket links from xref_jira
        int threadTicketCount = AggregateThreadTickets(connection);

        logger.LogInformation(
            "Cross-reference index rebuilt: {RefCount} refs from {MessageCount} messages, {ThreadTickets} thread-ticket links",
            refCount, messages.Count, threadTicketCount);
    }

    /// <summary>
    /// Incrementally indexes only messages that were ingested since the last run.
    /// Extracts cross-references and refreshes affected thread-ticket rows.
    /// </summary>
    public void IndexNewMessages(IReadOnlyList<int> newZulipMessageIds, CancellationToken ct = default)
    {
        if (newZulipMessageIds.Count == 0) return;

        using SqliteConnection connection = database.OpenConnection();

        HashSet<(string StreamName, string Topic)> affectedThreads = [];
        int refCount = 0;

        foreach (int zulipMessageId in newZulipMessageIds)
        {
            ct.ThrowIfCancellationRequested();

            ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: zulipMessageId);
            if (message is null) continue;

            string messageText = GetMessageText(message);
            if (string.IsNullOrWhiteSpace(messageText)) continue;

            string sourceId = message.ZulipMessageId.ToString();
            int extracted = ExtractAndInsertRefs(connection, sourceId, messageText);
            if (extracted > 0)
                affectedThreads.Add((message.StreamName, message.Topic));
            refCount += extracted;
        }

        // Refresh affected threads
        foreach ((string streamName, string topic) in affectedThreads)
        {
            ct.ThrowIfCancellationRequested();
            RefreshThreadTickets(connection, streamName, topic);
        }

        logger.LogInformation(
            "Incremental cross-reference index: {Messages} messages scanned, {Refs} refs, {Threads} threads refreshed",
            newZulipMessageIds.Count, refCount, affectedThreads.Count);
    }

    /// <summary>Combines plain text and HTML content from a message for extraction.</summary>
    private static string GetMessageText(ZulipMessageRecord message)
    {
        string plain = message.ContentPlain ?? "";
        string html = message.ContentHtml ?? "";
        if (string.IsNullOrWhiteSpace(html))
            return plain;
        return $"{plain} {html}";
    }

    /// <summary>Runs all shared extractors on the text and inserts records. Returns count of refs inserted.</summary>
    private static int ExtractAndInsertRefs(SqliteConnection connection, string sourceId, string messageText)
    {
        int count = 0;

        foreach (JiraXRefRecord r in JiraReferenceExtractor.GetReferences(ContentTypes.Message, sourceId, null, messageText))
        {
            r.Id = JiraXRefRecord.GetIndex();
            JiraXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (GitHubXRefRecord r in GitHubReferenceExtractor.GetReferences(ContentTypes.Message, sourceId, messageText))
        {
            r.Id = GitHubXRefRecord.GetIndex();
            GitHubXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (ConfluenceXRefRecord r in ConfluenceReferenceExtractor.GetReferences(ContentTypes.Message, sourceId, messageText))
        {
            r.Id = ConfluenceXRefRecord.GetIndex();
            ConfluenceXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences(ContentTypes.Message, sourceId, messageText))
        {
            r.Id = FhirElementXRefRecord.GetIndex();
            FhirElementXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        return count;
    }

    private static int AggregateThreadTickets(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO zulip_thread_tickets (Id, StreamName, Topic, JiraKey, ReferenceCount, FirstSeenAt, LastSeenAt)
            SELECT
                ROW_NUMBER() OVER () as Id,
                m.StreamName,
                m.Topic,
                xr.JiraKey,
                COUNT(*) as ReferenceCount,
                MIN(m.Timestamp) as FirstSeenAt,
                MAX(m.Timestamp) as LastSeenAt
            FROM xref_jira xr
            JOIN zulip_messages m ON CAST(m.ZulipMessageId AS TEXT) = xr.SourceId
            WHERE xr.ContentType = 'message'
            GROUP BY m.StreamName, m.Topic, xr.JiraKey
            """;
        return cmd.ExecuteNonQuery();
    }

    private static void RefreshThreadTickets(SqliteConnection connection, string streamName, string topic)
    {
        // Delete existing thread-ticket rows for this thread
        using (SqliteCommand deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM zulip_thread_tickets WHERE StreamName = @stream AND Topic = @topic";
            deleteCmd.Parameters.AddWithValue("@stream", streamName);
            deleteCmd.Parameters.AddWithValue("@topic", topic);
            deleteCmd.ExecuteNonQuery();
        }

        // Re-aggregate for this thread from xref_jira
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO zulip_thread_tickets (Id, StreamName, Topic, JiraKey, ReferenceCount, FirstSeenAt, LastSeenAt)
            SELECT
                ROW_NUMBER() OVER () + COALESCE((SELECT MAX(Id) FROM zulip_thread_tickets), 0) as Id,
                m.StreamName,
                m.Topic,
                xr.JiraKey,
                COUNT(*) as ReferenceCount,
                MIN(m.Timestamp) as FirstSeenAt,
                MAX(m.Timestamp) as LastSeenAt
            FROM xref_jira xr
            JOIN zulip_messages m ON CAST(m.ZulipMessageId AS TEXT) = xr.SourceId
            WHERE xr.ContentType = 'message'
              AND m.StreamName = @stream AND m.Topic = @topic
            GROUP BY m.StreamName, m.Topic, xr.JiraKey
            """;
        insertCmd.Parameters.AddWithValue("@stream", streamName);
        insertCmd.Parameters.AddWithValue("@topic", topic);
        insertCmd.ExecuteNonQuery();
    }
}
