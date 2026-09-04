using FhirAugury.Processing.Common.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Processing;

public sealed class PlannedTicketSnapshotSanitizer(string runId)
    : AuthoringSnapshotSanitizer(CreateTables())
{
    public override async Task SanitizeAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        string[] issueKeyTables =
        [
            "planned_ticket_repos",
            "planned_ticket_repo_changes",
            "planned_ticket_repo_impacts",
            "planned_ticket_change_validations",
            "planned_ticket_testing_considerations",
            "planned_ticket_open_questions",
            "planned_ticket_related_jira",
            "planned_ticket_related_zulip",
            "planned_ticket_related_github",
            "planned_ticket_hydration",
            "planned_jira_hydration",
            "planned_zulip_hydration",
            "planned_github_hydration",
            "planned_repo_hydration",
            "planned_ticket_jira_xref",
        ];
        foreach (string table in issueKeyTables)
        {
            await ExecuteAsync(
                connection,
                $"""
                DELETE FROM {table}
                WHERE IssueKey NOT IN (
                    SELECT s.TicketKey
                    FROM planned_ticket_authoring_state s
                    INNER JOIN authoring_result_receipts r ON r.OperationId = s.OperationId
                    INNER JOIN authoring_run_items i
                        ON i.Id = s.RunItemId AND i.AcceptedReceiptId = r.Id
                    WHERE s.Classification = 'receipt-backed' AND i.Status = 'complete')
                """,
                ct);
        }
        await ExecuteAsync(
            connection,
            """
            DELETE FROM planned_ticket_jira_content
            WHERE TicketKey NOT IN (
                SELECT s.TicketKey
                FROM planned_ticket_authoring_state s
                INNER JOIN authoring_result_receipts r ON r.OperationId = s.OperationId
                INNER JOIN authoring_run_items i
                    ON i.Id = s.RunItemId AND i.AcceptedReceiptId = r.Id
                WHERE s.Classification = 'receipt-backed' AND i.Status = 'complete');
            DELETE FROM planned_ticket_topic_members
            WHERE TicketKey NOT IN (
                SELECT s.TicketKey
                FROM planned_ticket_authoring_state s
                INNER JOIN authoring_result_receipts r ON r.OperationId = s.OperationId
                INNER JOIN authoring_run_items i
                    ON i.Id = s.RunItemId AND i.AcceptedReceiptId = r.Id
                WHERE s.Classification = 'receipt-backed' AND i.Status = 'complete');
            CREATE TEMP TABLE invalid_planned_snapshot_groups AS
            SELECT g.RowId
            FROM planned_ticket_topic_groups g
            LEFT JOIN planned_ticket_topic_members m ON m.TopicGroupRowId = g.RowId
            GROUP BY g.RowId, g.FirstTicketKey
            HAVING COUNT(m.RowId) < 2
                OR SUM(CASE WHEN m.TicketKey = g.FirstTicketKey THEN 1 ELSE 0 END) = 0
                OR g.FirstTicketKey NOT IN (
                    SELECT s.TicketKey
                    FROM planned_ticket_authoring_state s
                    INNER JOIN authoring_result_receipts r ON r.OperationId = s.OperationId
                    INNER JOIN authoring_run_items i
                        ON i.Id = s.RunItemId AND i.AcceptedReceiptId = r.Id
                    WHERE s.Classification = 'receipt-backed' AND i.Status = 'complete');
            UPDATE planned_ticket_topic_members
            SET TopicGroupRowId = NULL
            WHERE TopicGroupRowId IN (SELECT RowId FROM invalid_planned_snapshot_groups);
            DELETE FROM planned_ticket_topic_groups
            WHERE RowId IN (SELECT RowId FROM invalid_planned_snapshot_groups);
            CREATE TEMP TABLE invalid_planned_snapshot_topics AS
            SELECT t.RowId
            FROM planned_ticket_topics t
            LEFT JOIN planned_ticket_topic_members m ON m.TopicRowId = t.RowId
            GROUP BY t.RowId
            HAVING COUNT(m.RowId) = 0;
            DELETE FROM planned_ticket_topic_repos
            WHERE TopicRowId IN (SELECT RowId FROM invalid_planned_snapshot_topics)
               OR TopicRowId NOT IN (SELECT DISTINCT TopicRowId FROM planned_ticket_topic_members);
            DELETE FROM planned_ticket_topic_members
            WHERE TopicRowId IN (SELECT RowId FROM invalid_planned_snapshot_topics);
            DELETE FROM planned_ticket_topic_groups
            WHERE TopicRowId IN (SELECT RowId FROM invalid_planned_snapshot_topics)
               OR TopicRowId NOT IN (SELECT DISTINCT TopicRowId FROM planned_ticket_topic_members)
               OR RowId NOT IN (
                   SELECT DISTINCT TopicGroupRowId FROM planned_ticket_topic_members
                   WHERE TopicGroupRowId IS NOT NULL);
            DELETE FROM planned_ticket_topics
            WHERE RowId IN (SELECT RowId FROM invalid_planned_snapshot_topics)
               OR RowId NOT IN (SELECT DISTINCT TopicRowId FROM planned_ticket_topic_members);
            DELETE FROM planned_tickets
            WHERE Key NOT IN (
                SELECT s.TicketKey
                FROM planned_ticket_authoring_state s
                INNER JOIN authoring_result_receipts r ON r.OperationId = s.OperationId
                INNER JOIN authoring_run_items i
                    ON i.Id = s.RunItemId AND i.AcceptedReceiptId = r.Id
                WHERE s.Classification = 'receipt-backed' AND i.Status = 'complete');
            """,
            ct);
        await ExecuteAsync(connection, "DELETE FROM planned_ticket_partition_receipts WHERE RunId <> @runId", ct, ("@runId", runId));
        await ExecuteAsync(connection, "DELETE FROM authoring_result_receipts WHERE RunId <> @runId", ct, ("@runId", runId));
        await ExecuteAsync(connection, "DELETE FROM authoring_run_items WHERE RunId <> @runId", ct, ("@runId", runId));
        await ExecuteAsync(connection, "DELETE FROM authoring_runs WHERE Id <> @runId", ct, ("@runId", runId));
        await base.SanitizeAsync(connection, ct);
    }

    private static IReadOnlyList<AuthoringSnapshotTable> CreateTables()
        =>
        [
            .. AuthoringSnapshotSanitizer.GetCoreTableDefinitions(),
            new("planned_tickets"),
            new("planned_ticket_repos"),
            new("planned_ticket_repo_changes"),
            new("planned_ticket_repo_impacts"),
            new("planned_ticket_change_validations"),
            new("planned_ticket_testing_considerations"),
            new("planned_ticket_open_questions"),
            new("planned_ticket_related_jira"),
            new("planned_ticket_related_zulip"),
            new("planned_ticket_related_github"),
            new("planned_ticket_hydration"),
            new("planned_jira_hydration"),
            new("planned_zulip_hydration"),
            new("planned_github_hydration"),
            new("planned_repo_hydration"),
            new("planned_ticket_jira_xref"),
            new("planned_ticket_jira_content"),
            new("planned_ticket_topics"),
            new("planned_ticket_topic_groups"),
            new("planned_ticket_topic_members"),
            new("planned_ticket_topic_repos"),
            new("planned_ticket_partition_receipts"),
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
