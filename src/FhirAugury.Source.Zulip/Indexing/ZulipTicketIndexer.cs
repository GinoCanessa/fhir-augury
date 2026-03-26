using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Zulip.Indexing;

/// <summary>
/// Scans Zulip messages for Jira ticket references and populates
/// message-ticket and thread-ticket link tables.
/// </summary>
public class ZulipTicketIndexer(ZulipDatabase database, ILogger<ZulipTicketIndexer> logger)
{
    /// <summary>
    /// Rebuilds all ticket reference tables from scratch.
    /// Scans every message and rebuilds both message-ticket and thread-ticket tables.
    /// </summary>
    public void RebuildFullIndex(CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        // Clear existing ticket data
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM zulip_thread_tickets; DELETE FROM zulip_message_tickets;";
            cmd.ExecuteNonQuery();
        }

        // Scan all messages and extract ticket references
        List<ZulipMessageRecord> messages = ZulipMessageRecord.SelectList(connection);
        int messageTicketCount = 0;

        foreach (ZulipMessageRecord message in messages)
        {
            ct.ThrowIfCancellationRequested();

            List<JiraTicketMatch> tickets = ExtractFromMessage(message);
            if (tickets.Count == 0) continue;

            HashSet<string> seenKeys = [];
            foreach (JiraTicketMatch ticket in tickets)
            {
                if (!seenKeys.Add(ticket.JiraKey)) continue;

                ZulipMessageTicketRecord.Insert(connection, new ZulipMessageTicketRecord
                {
                    Id = ZulipMessageTicketRecord.GetIndex(),
                    ZulipMessageId = message.ZulipMessageId,
                    JiraKey = ticket.JiraKey,
                    Context = ticket.Context,
                }, ignoreDuplicates: true);
                messageTicketCount++;
            }
        }

        // Aggregate thread-level ticket links
        int threadTicketCount = AggregateThreadTickets(connection);

        logger.LogInformation(
            "Ticket index rebuilt: {MessageTickets} message-ticket links, {ThreadTickets} thread-ticket links",
            messageTicketCount, threadTicketCount);
    }

    /// <summary>
    /// Incrementally indexes only messages that were ingested since the last run.
    /// Updates message-ticket links and refreshes affected thread-ticket rows.
    /// </summary>
    public void IndexNewMessages(IReadOnlyList<int> newZulipMessageIds, CancellationToken ct = default)
    {
        if (newZulipMessageIds.Count == 0) return;

        using SqliteConnection connection = database.OpenConnection();

        HashSet<(string StreamName, string Topic)> affectedThreads = [];

        foreach (int zulipMessageId in newZulipMessageIds)
        {
            ct.ThrowIfCancellationRequested();

            ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: zulipMessageId);
            if (message is null) continue;

            List<JiraTicketMatch> tickets = ExtractFromMessage(message);
            if (tickets.Count == 0) continue;

            affectedThreads.Add((message.StreamName, message.Topic));

            HashSet<string> seenKeys = [];
            foreach (JiraTicketMatch ticket in tickets)
            {
                if (!seenKeys.Add(ticket.JiraKey)) continue;

                ZulipMessageTicketRecord.Insert(connection, new ZulipMessageTicketRecord
                {
                    Id = ZulipMessageTicketRecord.GetIndex(),
                    ZulipMessageId = zulipMessageId,
                    JiraKey = ticket.JiraKey,
                    Context = ticket.Context,
                }, ignoreDuplicates: true);
            }
        }

        // Refresh affected threads
        foreach ((string streamName, string topic) in affectedThreads)
        {
            ct.ThrowIfCancellationRequested();
            RefreshThreadTickets(connection, streamName, topic);
        }

        logger.LogInformation(
            "Incremental ticket index: {Messages} messages scanned, {Threads} threads refreshed",
            newZulipMessageIds.Count, affectedThreads.Count);
    }

    private static List<JiraTicketMatch> ExtractFromMessage(ZulipMessageRecord message)
    {
        // Scan both plain text and HTML, merge and deduplicate
        List<JiraTicketMatch> tickets = JiraTicketExtractor.ExtractTickets(message.ContentPlain);

        if (!string.IsNullOrWhiteSpace(message.ContentHtml))
        {
            List<JiraTicketMatch> htmlTickets = JiraTicketExtractor.ExtractTickets(message.ContentHtml);
            HashSet<string> existingKeys = new HashSet<string>(tickets.Select(t => t.JiraKey));
            foreach (JiraTicketMatch htmlTicket in htmlTickets)
            {
                if (existingKeys.Add(htmlTicket.JiraKey))
                    tickets.Add(htmlTicket);
            }
        }

        return tickets;
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
                mt.JiraKey,
                COUNT(*) as ReferenceCount,
                MIN(m.Timestamp) as FirstSeenAt,
                MAX(m.Timestamp) as LastSeenAt
            FROM zulip_message_tickets mt
            JOIN zulip_messages m ON m.ZulipMessageId = mt.ZulipMessageId
            GROUP BY m.StreamName, m.Topic, mt.JiraKey
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

        // Re-aggregate for this thread
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO zulip_thread_tickets (Id, StreamName, Topic, JiraKey, ReferenceCount, FirstSeenAt, LastSeenAt)
            SELECT
                ROW_NUMBER() OVER () + COALESCE((SELECT MAX(Id) FROM zulip_thread_tickets), 0) as Id,
                m.StreamName,
                m.Topic,
                mt.JiraKey,
                COUNT(*) as ReferenceCount,
                MIN(m.Timestamp) as FirstSeenAt,
                MAX(m.Timestamp) as LastSeenAt
            FROM zulip_message_tickets mt
            JOIN zulip_messages m ON m.ZulipMessageId = mt.ZulipMessageId
            WHERE m.StreamName = @stream AND m.Topic = @topic
            GROUP BY m.StreamName, m.Topic, mt.JiraKey
            """;
        insertCmd.Parameters.AddWithValue("@stream", streamName);
        insertCmd.Parameters.AddWithValue("@topic", topic);
        insertCmd.ExecuteNonQuery();
    }
}
