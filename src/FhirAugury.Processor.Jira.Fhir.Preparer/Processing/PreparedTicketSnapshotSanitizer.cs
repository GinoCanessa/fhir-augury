using FhirAugury.Processing.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketSnapshotSanitizer(string runId)
    : AuthoringSnapshotSanitizer(CreateTables())
{
    public override async Task SanitizeAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        await PreparerDatabase.CreateCurrentSnapshotReceiptBackedTicketsAsync(
            connection,
            ct);
        string[] ticketTables =
        [
            "prepared_ticket_repos",
            "prepared_ticket_related_jira",
            "prepared_ticket_related_zulip",
            "prepared_ticket_related_github",
            "prepared_ticket_hydration",
            "prepared_jira_hydration",
            "prepared_zulip_hydration",
            "prepared_github_hydration",
            "prepared_repo_hydration",
            "prepared_ticket_jira_xref",
            "prepared_ticket_jira_content",
            "prepared_ticket_artifacts",
            "prepared_ticket_pages",
        ];
        foreach (string table in ticketTables)
        {
            await ExecuteAsync(
                connection,
                $"""
                DELETE FROM {table}
                WHERE TicketKey NOT IN (
                    SELECT TicketKey
                    FROM {PreparerDatabase.CurrentSnapshotReceiptBackedTicketsTable}
                )
                """,
                ct);
        }

        await ExecuteAsync(
            connection,
            $"""
            DELETE FROM prepared_ticket_topic_members
            WHERE TicketKey NOT IN (
                SELECT TicketKey
                FROM {PreparerDatabase.CurrentSnapshotReceiptBackedTicketsTable}
            );
            CREATE TEMP TABLE invalid_snapshot_groups AS
            SELECT g.RowId
            FROM prepared_ticket_topic_groups g
            LEFT JOIN prepared_ticket_topic_members m ON m.TopicGroupRowId = g.RowId
            GROUP BY g.RowId, g.FirstTicketKey
            HAVING COUNT(m.RowId) < 2
                OR SUM(CASE WHEN m.TicketKey = g.FirstTicketKey THEN 1 ELSE 0 END) = 0
                OR g.FirstTicketKey NOT IN (
                    SELECT TicketKey
                    FROM {PreparerDatabase.CurrentSnapshotReceiptBackedTicketsTable}
                );
            UPDATE prepared_ticket_topic_members
            SET TopicGroupRowId = NULL
            WHERE TopicGroupRowId IN (SELECT RowId FROM invalid_snapshot_groups);
            DELETE FROM prepared_ticket_topic_groups
            WHERE RowId IN (SELECT RowId FROM invalid_snapshot_groups)
               OR FirstTicketKey NOT IN (
                    SELECT TicketKey
                    FROM {PreparerDatabase.CurrentSnapshotReceiptBackedTicketsTable}
                );
            CREATE TEMP TABLE invalid_snapshot_topics AS
            SELECT t.RowId
            FROM prepared_ticket_topics t
            LEFT JOIN prepared_ticket_topic_members m ON m.TopicRowId = t.RowId
            GROUP BY t.RowId
            HAVING COUNT(m.RowId) < 2;
            DELETE FROM prepared_ticket_topic_members
            WHERE TopicRowId IN (SELECT RowId FROM invalid_snapshot_topics);
            DELETE FROM prepared_ticket_topic_groups
            WHERE TopicRowId IN (SELECT RowId FROM invalid_snapshot_topics);
            DELETE FROM prepared_ticket_topics
            WHERE RowId IN (SELECT RowId FROM invalid_snapshot_topics)
               OR RowId NOT IN (SELECT DISTINCT TopicRowId FROM prepared_ticket_topic_members);
            DELETE FROM prepared_tickets
            WHERE Key NOT IN (
                SELECT TicketKey
                FROM {PreparerDatabase.CurrentSnapshotReceiptBackedTicketsTable}
            );
            """,
            ct);
        await ExecuteAsync(
            connection,
            "DELETE FROM prepared_ticket_partition_receipts WHERE RunId <> @runId",
            ct,
            ("@runId", runId));
        await ExecuteAsync(
            connection,
            $"""
            CREATE TEMP TABLE retained_snapshot_receipts AS
            SELECT DISTINCT ReceiptId AS Id
            FROM {PreparerDatabase.CurrentSnapshotReceiptBackedTicketsTable}
            UNION
            SELECT Id
            FROM authoring_result_receipts
            WHERE RunId = @runId;
            CREATE TEMP TABLE retained_snapshot_items AS
            SELECT DISTINCT RunItemId
            FROM authoring_result_receipts
            WHERE Id IN (SELECT Id FROM retained_snapshot_receipts);
            INSERT INTO retained_snapshot_items(RunItemId)
            SELECT Id FROM authoring_run_items WHERE RunId = @runId;
            CREATE TEMP TABLE retained_snapshot_runs AS
            SELECT DISTINCT RunId
            FROM authoring_result_receipts
            WHERE Id IN (SELECT Id FROM retained_snapshot_receipts);
            INSERT OR IGNORE INTO retained_snapshot_runs(RunId) VALUES(@runId);
            DELETE FROM authoring_result_receipts
            WHERE Id NOT IN (SELECT Id FROM retained_snapshot_receipts);
            DELETE FROM authoring_run_items
            WHERE Id NOT IN (SELECT RunItemId FROM retained_snapshot_items);
            DELETE FROM authoring_runs
            WHERE Id NOT IN (SELECT RunId FROM retained_snapshot_runs);
            """,
            ct,
            ("@runId", runId));

        await base.SanitizeAsync(connection, ct);
    }

    private static IReadOnlyList<AuthoringSnapshotTable> CreateTables()
        =>
        [
            .. AuthoringSnapshotSanitizer.GetCoreTableDefinitions(),
            new("prepared_tickets"),
            new("prepared_ticket_repos"),
            new("prepared_ticket_related_jira"),
            new("prepared_ticket_related_zulip"),
            new("prepared_ticket_related_github"),
            new("prepared_ticket_hydration"),
            new("prepared_jira_hydration"),
            new("prepared_zulip_hydration"),
            new("prepared_github_hydration"),
            new("prepared_repo_hydration"),
            new("prepared_ticket_jira_xref"),
            new("prepared_ticket_jira_content"),
            new("prepared_ticket_artifacts"),
            new("prepared_ticket_pages"),
            new("prepared_ticket_topics"),
            new("prepared_ticket_topic_groups"),
            new("prepared_ticket_topic_members"),
            new("prepared_ticket_partition_receipts"),
            new("jira_review_workgroups"),
        ];

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(ct);
    }
}
