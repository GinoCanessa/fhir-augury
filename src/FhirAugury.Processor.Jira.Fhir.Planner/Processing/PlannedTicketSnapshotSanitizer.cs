using FhirAugury.Processing.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Processing;

public sealed class PlannedTicketSnapshotSanitizer(string runId)
    : AuthoringSnapshotSanitizer(CreateTables())
{
    public override async Task SanitizeAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        await PlannerDatabase.CreateCurrentSnapshotReceiptBackedTicketsAsync(
            connection,
            ct);
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
                    SELECT TicketKey
                    FROM {PlannerDatabase.CurrentSnapshotReceiptBackedTicketsTable})
                """,
                ct);
        }
        await ExecuteAsync(
            connection,
            $"""
            DELETE FROM planned_ticket_jira_content
            WHERE TicketKey NOT IN (
                SELECT TicketKey
                FROM {PlannerDatabase.CurrentSnapshotReceiptBackedTicketsTable});
            DELETE FROM planned_ticket_topic_members
            WHERE TicketKey NOT IN (
                SELECT TicketKey
                FROM {PlannerDatabase.CurrentSnapshotReceiptBackedTicketsTable});
            CREATE TEMP TABLE invalid_planned_snapshot_groups AS
            SELECT g.RowId
            FROM planned_ticket_topic_groups g
            LEFT JOIN planned_ticket_topic_members m ON m.TopicGroupRowId = g.RowId
            GROUP BY g.RowId, g.FirstTicketKey
            HAVING COUNT(m.RowId) < 2
                OR SUM(CASE WHEN m.TicketKey = g.FirstTicketKey THEN 1 ELSE 0 END) = 0
                OR g.FirstTicketKey NOT IN (
                    SELECT TicketKey
                    FROM {PlannerDatabase.CurrentSnapshotReceiptBackedTicketsTable});
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
                SELECT TicketKey
                FROM {PlannerDatabase.CurrentSnapshotReceiptBackedTicketsTable});
            """,
            ct);
        await ExecuteAsync(connection, "DELETE FROM planned_ticket_partition_receipts WHERE RunId <> @runId", ct, ("@runId", runId));
        await ExecuteAsync(
            connection,
            $"""
            CREATE TEMP TABLE retained_snapshot_receipts AS
            SELECT DISTINCT ReceiptId AS Id
            FROM {PlannerDatabase.CurrentSnapshotReceiptBackedTicketsTable}
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
