using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketSnapshotSanitizer
    : AuthoringSnapshotSanitizer
{
    private readonly string _runId;
    private readonly bool _retainsInputProvenance;
    private readonly bool _retainsInPersonRequesters;
    private readonly bool _enforcesTrustedPeople;

    public PreparedTicketSnapshotSanitizer(
        string runId,
        int schemaVersion = PreparedTicketSnapshotSchemaV1.Version)
        : this(
            runId,
            PreparedTicketSnapshotSchemaResolver.Resolve(schemaVersion))
    {
    }

    internal PreparedTicketSnapshotSanitizer(
        string runId,
        AuthoringSnapshotSchemaCatalog catalog)
        : base(catalog)
    {
        _runId = runId;
        _retainsInputProvenance = catalog.Tables.Any(
            table => string.Equals(
                table.Name,
                "authoring_run_input_provenance",
                StringComparison.OrdinalIgnoreCase));
        _retainsInPersonRequesters = catalog.Tables.Any(
            table => string.Equals(
                table.Name,
                "prepared_ticket_in_person_requesters",
                StringComparison.OrdinalIgnoreCase));
        _enforcesTrustedPeople =
            catalog.Version == PreparedTicketSnapshotSchemaV3.Version;
    }

    public override async Task SanitizeAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        await PreparerDatabase.CreateCurrentSnapshotReceiptBackedTicketsAsync(
            connection,
            ct);
        List<string> ticketTables =
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
        if (_retainsInPersonRequesters)
        {
            ticketTables.Add("prepared_ticket_in_person_requesters");
        }
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
        if (_enforcesTrustedPeople)
        {
            await SanitizeTrustedPeopleAsync(connection, ct);
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
            ("@runId", _runId));
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
            """,
            ct,
            ("@runId", _runId));

        if (_retainsInputProvenance)
        {
            await ExecuteAsync(
                connection,
                $"""
                CREATE TEMP TABLE contributing_snapshot_runs AS
                SELECT DISTINCT RunId
                FROM {PreparerDatabase.CurrentSnapshotReceiptBackedTicketsTable};
                DELETE FROM authoring_run_input_provenance
                WHERE RunId NOT IN (SELECT RunId FROM contributing_snapshot_runs);
                """,
                ct);
        }

        await ExecuteAsync(
            connection,
            """
            DELETE FROM authoring_result_receipts
            WHERE Id NOT IN (SELECT Id FROM retained_snapshot_receipts);
            DELETE FROM authoring_run_items
            WHERE Id NOT IN (SELECT RunItemId FROM retained_snapshot_items);
            DELETE FROM authoring_runs
            WHERE Id NOT IN (SELECT RunId FROM retained_snapshot_runs);
            """,
            ct);

        await base.SanitizeAsync(connection, ct);
    }

    private static async Task SanitizeTrustedPeopleAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await SanitizePeopleValuesAsync(
            connection,
            "prepared_ticket_hydration",
            ct);
        await SanitizePeopleValuesAsync(
            connection,
            "prepared_jira_hydration",
            ct);
        await SanitizeInPersonRequestersAsync(connection, ct);
    }

    private static async Task SanitizePeopleValuesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken ct)
    {
        List<(
            long RowId,
            string? Reporter,
            string? Assignee,
            long? PolicyVersion)> rows = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT RowId, Reporter, Assignee,
                       PublicDisplayNamePolicyVersion
                FROM {table}
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }
        }

        foreach ((long rowId, string? reporter, string? assignee,
                  long? policyVersion) in rows)
        {
            await ExecuteAsync(
                connection,
                $"""
                UPDATE {table}
                SET Reporter = @reporter,
                    Assignee = @assignee
                WHERE RowId = @rowId
                """,
                ct,
                ("@reporter", NormalizeTrustedDisplayName(
                    reporter,
                    policyVersion)),
                ("@assignee", NormalizeTrustedDisplayName(
                    assignee,
                    policyVersion)),
                ("@rowId", rowId));
        }
    }

    private static async Task SanitizeInPersonRequestersAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<(
            long RowId,
            string TicketKey,
            string DisplayName,
            long? PolicyVersion)> rows = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT RowId, TicketKey, DisplayName,
                       PublicDisplayNamePolicyVersion
                FROM prepared_ticket_in_person_requesters
                ORDER BY RowId
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }
        }

        Dictionary<string, HashSet<string>> seenByTicket =
            new(StringComparer.OrdinalIgnoreCase);
        List<long> rowsToDelete = [];
        List<(long RowId, string DisplayName)> rowsToUpdate = [];
        foreach ((long rowId, string ticketKey, string displayName,
                  long? policyVersion) in rows)
        {
            string? normalized = NormalizeTrustedDisplayName(
                displayName,
                policyVersion);
            if (!seenByTicket.TryGetValue(
                    ticketKey,
                    out HashSet<string>? seen))
            {
                seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenByTicket[ticketKey] = seen;
            }
            if (normalized is null || !seen.Add(normalized))
            {
                rowsToDelete.Add(rowId);
                continue;
            }
            if (!string.Equals(
                    displayName,
                    normalized,
                    StringComparison.Ordinal))
            {
                rowsToUpdate.Add((rowId, normalized));
            }
        }

        foreach (long rowId in rowsToDelete)
        {
            await ExecuteAsync(
                connection,
                """
                DELETE FROM prepared_ticket_in_person_requesters
                WHERE RowId = @rowId
                """,
                ct,
                ("@rowId", rowId));
        }
        foreach ((long rowId, string displayName) in rowsToUpdate)
        {
            await ExecuteAsync(
                connection,
                """
                UPDATE prepared_ticket_in_person_requesters
                SET DisplayName = @displayName
                WHERE RowId = @rowId
                """,
                ct,
                ("@displayName", displayName),
                ("@rowId", rowId));
        }
    }

    private static string? NormalizeTrustedDisplayName(
        string? value,
        long? policyVersion)
        => policyVersion == PublicDisplayNamePolicy.CurrentVersion
            ? PublicDisplayNamePolicy.Normalize(value)
            : null;

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
