using System.Text.Json;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets;

/// <summary>
/// Validates the public schema, provenance, integrity, and canonical hydration
/// required by the immutable discussion and applying source snapshots.
/// </summary>
internal static class HydrationAssertion
{
    public const string JiraProcessorKind = "jira-fhir";

    private static readonly string[] ForbiddenColumnFragments =
    [
        "token",
        "verifier",
        "callback",
        "secret",
        "bearer",
        "password",
        "credential",
        "diagnostic",
    ];

    public sealed record SnapshotValidationResult(
        AuthoringSnapshotDescriptor Descriptor,
        IReadOnlyDictionary<string, long> TableCounts);

    public static async Task<SnapshotValidationResult> ValidateSnapshotAsync(
        ImmutableFileSnapshot snapshot,
        AuthoringSnapshotDescriptor descriptor,
        TicketSiteKind siteKind,
        CancellationToken ct)
    {
        AuthoringSnapshotSchemaCatalog schema =
            GetSchema(siteKind, descriptor.SchemaVersion);
        ValidateDescriptorShape(descriptor, snapshot);
        ValidateWholeFile(snapshot, descriptor);

        await using SqliteConnection connection = OpenReadOnly(snapshot.Path);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ValidatePublicSchemaAsync(
            connection,
            schema,
            ct).ConfigureAwait(false);
        if (siteKind == TicketSiteKind.Applying)
        {
            await ValidateApplyingBrowserQueriesAsync(connection, ct)
                .ConfigureAwait(false);
        }
        await ValidateIntegrityAsync(connection, ct).ConfigureAwait(false);
        await ValidateProvenanceAsync(connection, descriptor, ct).ConfigureAwait(false);

        IReadOnlyList<string> countTables = schema.CountedTables;
        ValidateDescriptorCountKeys(descriptor.TableCounts, countTables);
        IReadOnlyDictionary<string, long> counts =
            await ReadTableCountsAsync(connection, countTables, ct).ConfigureAwait(false);
        foreach ((string table, long expected) in descriptor.TableCounts)
        {
            if (!counts.TryGetValue(table, out long actual) || actual != expected)
            {
                throw new InvalidOperationException(
                    $"Snapshot table count mismatch for '{table}': descriptor={expected}, database={actual}.");
            }
        }

        long itemCount = await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM authoring_run_items WHERE RunId = @runId",
            ct,
            ("@runId", descriptor.RunId)).ConfigureAwait(false);
        long receiptCount = await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM authoring_result_receipts",
            ct).ConfigureAwait(false);
        if (itemCount != descriptor.ItemCount)
        {
            throw new InvalidOperationException(
                $"Snapshot item count mismatch: descriptor={descriptor.ItemCount}, database={itemCount}.");
        }
        if (receiptCount != descriptor.ReceiptCount)
        {
            throw new InvalidOperationException(
                $"Snapshot receipt count mismatch: descriptor={descriptor.ReceiptCount}, database={receiptCount}.");
        }

        await ValidateTicketCompletenessAsync(connection, siteKind, ct)
            .ConfigureAwait(false);
        return new SnapshotValidationResult(descriptor, counts);
    }

    public static async Task<IReadOnlyDictionary<string, long>> ReadManifestCountsAsync(
        string dbPath,
        TicketSiteKind siteKind,
        CancellationToken ct)
    {
        await using SqliteConnection connection = OpenReadOnly(dbPath);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        int schemaVersion = await ReadSnapshotSchemaVersionAsync(
            connection,
            ct).ConfigureAwait(false);
        return await ReadTableCountsAsync(
            connection,
            GetSchema(siteKind, schemaVersion).CountedTables,
            ct).ConfigureAwait(false);
    }

    private static void ValidateDescriptorShape(
        AuthoringSnapshotDescriptor descriptor,
        ImmutableFileSnapshot snapshot)
    {
        if (!string.Equals(
            descriptor.ProcessorKind,
            JiraProcessorKind,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Descriptor processor kind '{descriptor.ProcessorKind}' is not '{JiraProcessorKind}'.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.RunId) ||
            string.IsNullOrWhiteSpace(descriptor.SnapshotId))
        {
            throw new InvalidOperationException(
                "Descriptor run ID and snapshot ID are required.");
        }
        if (descriptor.AuthoringEpoch < 1 || descriptor.Sequence < 1)
        {
            throw new InvalidOperationException(
                "Descriptor authoring epoch and snapshot sequence must be positive.");
        }
        if (descriptor.ItemCount < 0 || descriptor.ReceiptCount < 0)
        {
            throw new InvalidOperationException(
                "Descriptor item and receipt counts are inconsistent.");
        }
        if (descriptor.TableCounts is null)
        {
            throw new InvalidOperationException(
                "Descriptor table counts are required.");
        }
        if (descriptor.SizeBytes < 1 ||
            !string.Equals(
                snapshot.SourceFileName,
                descriptor.FileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Descriptor file name or size is inconsistent with the snapshot input.");
        }
        if (descriptor.Sha256 is not { Length: 64 } ||
            descriptor.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Descriptor SHA-256 must be a 64-character hexadecimal value.");
        }
    }

    private static void ValidateWholeFile(
        ImmutableFileSnapshot snapshot,
        AuthoringSnapshotDescriptor descriptor)
    {
        if (snapshot.SizeBytes != descriptor.SizeBytes)
        {
            throw new InvalidOperationException(
                $"Snapshot size mismatch: descriptor={descriptor.SizeBytes}, file={snapshot.SizeBytes}.");
        }
        if (!string.Equals(
            snapshot.Sha256,
            descriptor.Sha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot SHA-256 mismatch: descriptor={descriptor.Sha256}, file={snapshot.Sha256}.");
        }
    }

    private static async Task ValidatePublicSchemaAsync(
        SqliteConnection connection,
        AuthoringSnapshotSchemaCatalog schema,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> expected =
            schema.Tables.ToDictionary(
                table => table.Name,
                table => (IReadOnlyCollection<string>)table.Columns,
                StringComparer.OrdinalIgnoreCase);

        await using SqliteCommand objects = connection.CreateCommand();
        objects.CommandText =
            """
            SELECT type, name
            FROM sqlite_master
            WHERE type IN ('table', 'view', 'trigger')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """;
        List<string> tableNames = [];
        List<string> forbiddenObjects = [];
        await using (SqliteDataReader reader =
            await objects.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string type = reader.GetString(0);
                string name = reader.GetString(1);
                if (string.Equals(type, "table", StringComparison.Ordinal))
                {
                    tableNames.Add(name);
                }
                else
                {
                    forbiddenObjects.Add($"{type} {name}");
                }
            }
        }

        if (forbiddenObjects.Count > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot contains forbidden schema objects: {string.Join(", ", forbiddenObjects)}.");
        }

        string[] forbiddenTables = tableNames
            .Where(table => !expected.ContainsKey(table))
            .ToArray();
        if (forbiddenTables.Length > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot contains forbidden or internal tables: {string.Join(", ", forbiddenTables)}.");
        }

        string[] missingTables = expected.Keys
            .Where(table => !tableNames.Contains(table, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (missingTables.Length > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot is missing required public tables: {string.Join(", ", missingTables)}.");
        }

        foreach ((string table, IReadOnlyCollection<string> expectedColumns) in expected)
        {
            IReadOnlyList<string> actualColumns =
                await ReadColumnNamesAsync(connection, table, ct).ConfigureAwait(false);
            string[] missingColumns = expectedColumns
                .Where(column => !actualColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            string[] unexpectedColumns = actualColumns
                .Where(column => !expectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (missingColumns.Length > 0 || unexpectedColumns.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Snapshot table '{table}' does not match schema v{schema.Version}. " +
                    $"Missing columns: [{string.Join(", ", missingColumns)}]; " +
                    $"unexpected columns: [{string.Join(", ", unexpectedColumns)}].");
            }
            string? forbiddenColumn = actualColumns.FirstOrDefault(column =>
                ForbiddenColumnFragments.Any(fragment =>
                    column.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
            if (forbiddenColumn is not null)
            {
                throw new InvalidOperationException(
                    $"Snapshot contains forbidden column '{table}.{forbiddenColumn}'.");
            }
        }
    }

    private static async Task ValidateApplyingBrowserQueriesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        string[] queries =
        [
            "SELECT pt.*, jh.Title, jh.WorkGroup, jh.Status, jh.Type, jh.Url, COALESCE(th.Specification, jh.Specification), COALESCE(th.Priority, jh.Priority), COALESCE(th.Resolution, jh.Resolution), th.DescriptionPlain, COALESCE(th.ResolutionDescriptionPlain, jh.ResolutionDescriptionPlain) FROM planned_tickets pt LEFT JOIN planned_jira_hydration jh ON jh.IssueKey = pt.Key AND jh.JiraKey = pt.Key LEFT JOIN planned_ticket_hydration th ON th.IssueKey = pt.Key LIMIT 0",
            "SELECT RepoKey, RepoRevision, Justification FROM planned_ticket_repos LIMIT 0",
            "SELECT TicketRepoId, RepoKey, ChangeSequence, FilePath, ChangeTitle, ChangeDescription, ReplacementLines FROM planned_ticket_repo_changes LIMIT 0",
            "SELECT RepoKey, AffectedFilePath, HowAffected FROM planned_ticket_repo_impacts LIMIT 0",
            "SELECT RepoKey, ValidationSequence, Action FROM planned_ticket_change_validations LIMIT 0",
            "SELECT RepoKey, ConsiderationSequence, Consideration FROM planned_ticket_testing_considerations LIMIT 0",
            "SELECT RepoKey, QuestionSequence, Question FROM planned_ticket_open_questions LIMIT 0",
            "SELECT * FROM planned_ticket_related_jira LIMIT 0",
            "SELECT * FROM planned_ticket_related_zulip LIMIT 0",
            "SELECT * FROM planned_ticket_related_github LIMIT 0",
            "SELECT * FROM planned_ticket_hydration LIMIT 0",
            "SELECT * FROM planned_jira_hydration LIMIT 0",
            "SELECT * FROM planned_zulip_hydration LIMIT 0",
            "SELECT * FROM planned_github_hydration LIMIT 0",
            "SELECT * FROM planned_repo_hydration LIMIT 0",
            "SELECT * FROM planned_ticket_jira_xref LIMIT 0",
            "SELECT DescriptionHtml, ResolutionDescriptionHtml FROM planned_ticket_jira_content LIMIT 0",
            "SELECT RowId, Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint FROM planned_ticket_topics LIMIT 0",
            "SELECT RowId, Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic FROM planned_ticket_topic_groups LIMIT 0",
            "SELECT TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer FROM planned_ticket_topic_members LIMIT 0",
            "SELECT TopicRowId, RepoKey, OrderInTopic FROM planned_ticket_topic_repos LIMIT 0",
            "SELECT RunId, PartitionKey FROM planned_ticket_partition_receipts LIMIT 0",
            "SELECT Code, Name, NameClean FROM jira_review_workgroups LIMIT 0",
            "SELECT Id, ProcessorKind FROM authoring_runs LIMIT 0",
            "SELECT Id, RunId, BusinessKey, Status FROM authoring_run_items LIMIT 0",
            "SELECT Id, OperationId, BusinessKey FROM authoring_result_receipts LIMIT 0",
            "SELECT SnapshotId, ProcessorKind, RunId, Sequence FROM authoring_snapshot_provenance LIMIT 0",
        ];

        foreach (string query in queries)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = query;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<string> columns = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static async Task ValidateProvenanceAsync(
        SqliteConnection connection,
        AuthoringSnapshotDescriptor descriptor,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                   SchemaVersion, ItemCount, ReceiptCount, TableCountsJson, CreatedAt
            FROM authoring_snapshot_provenance
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Snapshot provenance row is missing.");
        }

        bool matches =
            string.Equals(reader.GetString(0), descriptor.SnapshotId, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), descriptor.ProcessorKind, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), descriptor.RunId, StringComparison.Ordinal) &&
            reader.GetInt64(3) == descriptor.AuthoringEpoch &&
            reader.GetInt64(4) == descriptor.Sequence &&
            reader.GetInt32(5) == descriptor.SchemaVersion &&
            reader.GetInt32(6) == descriptor.ItemCount &&
            reader.GetInt32(7) == descriptor.ReceiptCount &&
            DateTimeOffset.Parse(
                reader.GetString(9),
                System.Globalization.CultureInfo.InvariantCulture) ==
                descriptor.CreatedAt;
        if (!matches)
        {
            throw new InvalidOperationException(
                "Snapshot provenance does not match the trusted descriptor.");
        }

        Dictionary<string, long> provenanceCounts =
            JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(8))
            ?? throw new InvalidOperationException(
                "Snapshot provenance table counts are invalid.");
        if (!DictionaryEquals(provenanceCounts, descriptor.TableCounts))
        {
            throw new InvalidOperationException(
                "Snapshot provenance table counts do not match the trusted descriptor.");
        }
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Snapshot contains more than one provenance row.");
        }

        long matchingRun = await ScalarInt64Async(
            connection,
            """
            SELECT COUNT(*)
            FROM authoring_runs
            WHERE Id = @runId AND ProcessorKind = @processorKind
            """,
            ct,
            ("@runId", descriptor.RunId),
            ("@processorKind", descriptor.ProcessorKind)).ConfigureAwait(false);
        long invalidForeignItems = await ScalarInt64Async(
            connection,
            """
            SELECT COUNT(*)
            FROM authoring_run_items i
            LEFT JOIN authoring_result_receipts r
                ON r.RunItemId = i.Id
               AND r.Id = i.AcceptedReceiptId
               AND r.RunId = i.RunId
            LEFT JOIN authoring_runs run ON run.Id = i.RunId
            WHERE i.RunId <> @runId
              AND (
                  i.AcceptedReceiptId IS NULL
                  OR r.Id IS NULL
                  OR run.Id IS NULL
                  OR i.Status NOT IN ('complete', 'superseded')
              )
            """,
            ct,
            ("@runId", descriptor.RunId)).ConfigureAwait(false);
        if (matchingRun != 1 || invalidForeignItems != 0)
        {
            throw new InvalidOperationException(
                "Snapshot run and item coordinates do not match the trusted descriptor.");
        }
    }

    private static void ValidateDescriptorCountKeys(
        IReadOnlyDictionary<string, long> counts,
        IReadOnlyCollection<string> requiredTables)
    {
        string[] missing = requiredTables
            .Where(table => !counts.ContainsKey(table))
            .ToArray();
        string[] unexpected = counts.Keys
            .Where(table => !requiredTables.Contains(table, StringComparer.Ordinal))
            .ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw new InvalidOperationException(
                $"Descriptor table-count catalog mismatch. Missing: [{string.Join(", ", missing)}]; " +
                $"unexpected: [{string.Join(", ", unexpected)}].");
        }
    }

    private static async Task ValidateIntegrityAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        string result = Convert.ToString(
            await command.ExecuteScalarAsync(ct),
            System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot integrity check failed: {result}.");
        }
    }

    private static async Task ValidateTicketCompletenessAsync(
        SqliteConnection connection,
        TicketSiteKind siteKind,
        CancellationToken ct)
    {
        bool discussion = siteKind == TicketSiteKind.Discussion;
        string ticketTable = discussion
            ? "prepared_tickets"
            : "planned_tickets";
        string hydrationTable = discussion
            ? "prepared_ticket_hydration"
            : "planned_ticket_hydration";
        string hydrationKey = discussion
            ? "TicketKey"
            : "IssueKey";
        string jiraTable = discussion
            ? "prepared_jira_hydration"
            : "planned_jira_hydration";
        string jiraParentKey = discussion
            ? "TicketKey"
            : "IssueKey";
        string partitionTable = discussion
            ? "prepared_ticket_partition_receipts"
            : "planned_ticket_partition_receipts";

        long ticketCount = await ScalarInt64Async(
            connection,
            $"SELECT COUNT(*) FROM {ticketTable}",
            ct).ConfigureAwait(false);
        long incomplete = await ScalarInt64Async(
            connection,
            $"""
            SELECT COUNT(*)
            FROM {ticketTable} t
            LEFT JOIN {hydrationTable} h ON h.{hydrationKey} = t.Key
            LEFT JOIN {jiraTable} j ON j.{jiraParentKey} = t.Key AND j.JiraKey = t.Key
            WHERE h.{hydrationKey} IS NULL
               OR LOWER(h.HydrationStatus) <> 'resolved'
               OR j.JiraKey IS NULL
               OR LOWER(j.HydrationStatus) <> 'resolved'
            """,
            ct).ConfigureAwait(false);
        if (incomplete > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot has {incomplete} ticket(s) without complete canonical hydration.");
        }

        long workGroups = await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM jira_review_workgroups",
            ct).ConfigureAwait(false);
        long partitionReceipts = await ScalarInt64Async(
            connection,
            $"SELECT COUNT(*) FROM {partitionTable}",
            ct).ConfigureAwait(false);
        if (ticketCount > 0 && (workGroups == 0 || partitionReceipts == 0))
        {
            throw new InvalidOperationException(
                "Snapshot workgroup catalog or grouping receipts are incomplete.");
        }
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    private static AuthoringSnapshotSchemaCatalog GetSchema(
        TicketSiteKind siteKind,
        int schemaVersion)
        => (siteKind, schemaVersion) switch
        {
            (TicketSiteKind.Discussion, PreparedTicketSnapshotSchemaV1.Version) =>
                PreparedTicketSnapshotSchemaV1.Catalog,
            (TicketSiteKind.Discussion, PreparedTicketSnapshotSchemaV2.Version) =>
                PreparedTicketSnapshotSchemaV2.Catalog,
            (TicketSiteKind.Discussion, PreparedTicketSnapshotSchemaV3.Version) =>
                PreparedTicketSnapshotSchemaV3.Catalog,
            (TicketSiteKind.Applying, PlannedTicketSnapshotSchemaV1.Version) =>
                PlannedTicketSnapshotSchemaV1.Catalog,
            (TicketSiteKind.Discussion, _) =>
                throw new InvalidOperationException(
                    $"Unsupported Discussion snapshot schema version {schemaVersion}; " +
                    $"expected one of: {string.Join(", ", PreparedTicketSnapshotSchemaResolver.SupportedVersions)}."),
            (TicketSiteKind.Applying, _) =>
                throw new InvalidOperationException(
                    $"Unsupported Applying snapshot schema version {schemaVersion}; " +
                    $"expected {PlannedTicketSnapshotSchemaV1.Version}."),
            _ => throw new TicketSitePublishException(
                TicketSitePublishFailure.InvalidRequest,
                $"Unknown ticket site kind '{siteKind}'."),
        };

    private static async Task<int> ReadSnapshotSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SchemaVersion FROM authoring_snapshot_provenance";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Snapshot provenance row is missing.");
        }
        int schemaVersion = reader.GetInt32(0);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Snapshot contains more than one provenance row.");
        }
        return schemaVersion;
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadTableCountsAsync(
        SqliteConnection connection,
        IEnumerable<string> tables,
        CancellationToken ct)
    {
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            result[table] = await ScalarInt64Async(
                connection,
                $"SELECT COUNT(*) FROM \"{table}\"",
                ct).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
        => left.Count == right.Count &&
           left.All(pair =>
               right.TryGetValue(pair.Key, out long value) &&
               pair.Value == value);
}
