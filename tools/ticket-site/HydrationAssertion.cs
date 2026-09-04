using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

/// <summary>
/// Fail-fast assertion that the preparer DB has already been hydrated.
/// <c>ticket-site</c> discussion sub-site is a pure consumer of a hydrated DB now; the
/// preparer service owns the hydration sweep (startup + admin
/// endpoint). If the DB does not carry a <c>prepared_ticket_hydration</c>
/// table with at least one row, the tool aborts with an actionable
/// error pointing operators at the service.
/// </summary>
internal static class HydrationAssertion
{
    public const int SupportedSnapshotSchemaVersion = 1;
    public const string JiraProcessorKind = "jira-fhir";

    private static readonly string[] CorePublicTables =
    [
        "authoring_runs",
        "authoring_run_items",
        "authoring_result_receipts",
        "authoring_snapshot_provenance",
    ];

    private static readonly string[] PreparerCountTables =
    [
        "prepared_tickets",
        "prepared_ticket_repos",
        "prepared_ticket_related_jira",
        "prepared_ticket_related_zulip",
        "prepared_ticket_related_github",
        "prepared_ticket_hydration",
        "prepared_jira_hydration",
        "prepared_ticket_jira_content",
        "prepared_ticket_artifacts",
        "prepared_ticket_pages",
        "prepared_ticket_topics",
        "prepared_ticket_topic_groups",
        "prepared_ticket_topic_members",
        "prepared_ticket_partition_receipts",
        "jira_review_workgroups",
    ];

    private static readonly string[] PreparerPublicTables =
    [
        .. CorePublicTables,
        .. PreparerCountTables,
        "prepared_github_hydration",
        "prepared_repo_hydration",
        "prepared_ticket_jira_xref",
        "prepared_zulip_hydration",
    ];

    private static readonly string[] PlannerCountTables =
    [
        "planned_tickets",
        "planned_ticket_repos",
        "planned_ticket_repo_changes",
        "planned_ticket_repo_impacts",
        "planned_ticket_change_validations",
        "planned_ticket_testing_considerations",
        "planned_ticket_open_questions",
        "planned_ticket_hydration",
        "planned_jira_hydration",
        "planned_ticket_jira_content",
        "planned_ticket_topics",
        "planned_ticket_topic_groups",
        "planned_ticket_topic_members",
        "planned_ticket_topic_repos",
        "planned_ticket_partition_receipts",
        "jira_review_workgroups",
    ];

    private static readonly string[] PlannerPublicTables =
    [
        .. CorePublicTables,
        .. PlannerCountTables,
        "planned_github_hydration",
        "planned_repo_hydration",
        "planned_ticket_jira_xref",
        "planned_ticket_related_github",
        "planned_ticket_related_jira",
        "planned_ticket_related_zulip",
        "planned_zulip_hydration",
    ];

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

    private static readonly string[] SanitizedAuthoringRunColumns =
    [
        "Id",
        "ProcessorKind",
        "AuthoringEpoch",
        "Status",
        "DatabaseOnly",
        "TotalItems",
        "CreatedAt",
        "StartedAt",
        "CompletedAt",
        "SnapshotId",
    ];

    private static readonly string[] SanitizedAuthoringRunItemColumns =
    [
        "Id",
        "RunId",
        "BusinessKey",
        "ItemKind",
        "ExpectedSourceRevision",
        "Status",
        "AcceptedReceiptId",
        "AttemptCount",
        "CreatedAt",
        "StartedAt",
        "CompletedAt",
    ];

    private static readonly string[] SanitizedReceiptColumns =
    [
        "Id",
        "OperationId",
        "RunId",
        "RunItemId",
        "BusinessKey",
        "ContentHash",
        "ExpectedSourceRevision",
        "ObservedSourceRevision",
        "AuthoringEpoch",
        "PersistedAt",
    ];

    private static readonly string[] SnapshotProvenanceColumns =
    [
        "SnapshotId",
        "ProcessorKind",
        "RunId",
        "AuthoringEpoch",
        "Sequence",
        "SchemaVersion",
        "ItemCount",
        "ReceiptCount",
        "TableCountsJson",
        "CreatedAt",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyCollection<string>>>
        PreparerSchema = new(() => CreateExpectedSchema(PreparerSubSiteEmitter.Kind));

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyCollection<string>>>
        PlannerSchema = new(() => CreateExpectedSchema(PlannerSubSiteEmitter.Kind));

    public sealed record SnapshotValidationResult(
        AuthoringSnapshotDescriptor Descriptor,
        IReadOnlyDictionary<string, long> TableCounts);

    public static async Task<bool> AssertHydratedAsync(string dbPath, TextWriter stderr, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        ArgumentNullException.ThrowIfNull(stderr);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (SqliteCommand probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'prepared_ticket_hydration'";
            object? exists = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (exists is null)
            {
                await WriteActionableErrorAsync(stderr, dbPath).ConfigureAwait(false);
                return false;
            }
        }

        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM prepared_ticket_hydration";
        object? rowCount = await count.ExecuteScalarAsync(ct).ConfigureAwait(false);
        long n = rowCount is long l ? l : Convert.ToInt64(rowCount);
        if (n > 0)
        {
            return true;
        }

        await WriteActionableErrorAsync(stderr, dbPath).ConfigureAwait(false);
        return false;
    }

    private static Task WriteActionableErrorAsync(TextWriter stderr, string dbPath)
        => stderr.WriteLineAsync(
            $"Database '{dbPath}' is not hydrated. Run FhirAugury.Processor.Jira.Fhir.Preparer against it first "
            + "(the service hydrates on startup, or POST /api/v1/admin/hydration/backfill on a running service).");

    public static async Task<SnapshotValidationResult?> ValidateSnapshotAsync(
        string dbPath,
        string descriptorPath,
        string siteKind,
        TextWriter stderr,
        CancellationToken ct)
    {
        await using ImmutableFileSnapshot snapshot =
            await ImmutableFileSnapshot.CreateAsync(dbPath, ct).ConfigureAwait(false);
        return await ValidateSnapshotAsync(
            snapshot,
            descriptorPath,
            siteKind,
            stderr,
            ct).ConfigureAwait(false);
    }

    public static async Task<SnapshotValidationResult?> ValidateSnapshotAsync(
        ImmutableFileSnapshot snapshot,
        string descriptorPath,
        string siteKind,
        TextWriter stderr,
        CancellationToken ct)
    {
        try
        {
            AuthoringSnapshotDescriptor descriptor =
                await ReadDescriptorAsync(descriptorPath, ct).ConfigureAwait(false);
            ValidateDescriptorShape(descriptor, snapshot);
            ValidateWholeFile(snapshot, descriptor);

            await using SqliteConnection connection = OpenReadOnly(snapshot.Path);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ValidatePublicSchemaAsync(connection, siteKind, ct).ConfigureAwait(false);
            await ValidateBrowserQueriesAsync(connection, siteKind, ct).ConfigureAwait(false);
            await ValidateIntegrityAsync(connection, ct).ConfigureAwait(false);
            await ValidateProvenanceAsync(connection, descriptor, ct).ConfigureAwait(false);

            string[] countTables = siteKind == PreparerSubSiteEmitter.Kind
                ? PreparerCountTables
                : PlannerCountTables;
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
                "SELECT COUNT(*) FROM authoring_run_items",
                ct).ConfigureAwait(false);
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
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or SqliteException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"Snapshot validation failed: {ex.Message}")
                .ConfigureAwait(false);
            return null;
        }
    }

    public static async Task<IReadOnlyDictionary<string, long>> ReadManifestCountsAsync(
        string dbPath,
        string siteKind,
        CancellationToken ct)
    {
        await using SqliteConnection connection = OpenReadOnly(dbPath);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        string[] tables = siteKind == PreparerSubSiteEmitter.Kind
            ? PreparerCountTables
            : PlannerCountTables;
        return await ReadTableCountsAsync(connection, tables, ct).ConfigureAwait(false);
    }

    private static async Task<AuthoringSnapshotDescriptor> ReadDescriptorAsync(
        string descriptorPath,
        CancellationToken ct)
    {
        if (!File.Exists(descriptorPath))
        {
            throw new IOException($"Descriptor file not found: {descriptorPath}");
        }
        await using FileStream stream = File.OpenRead(descriptorPath);
        return await JsonSerializer.DeserializeAsync<AuthoringSnapshotDescriptor>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Descriptor file '{descriptorPath}' is empty.");
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
        if (descriptor.SchemaVersion != SupportedSnapshotSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported snapshot schema version {descriptor.SchemaVersion}; " +
                $"expected {SupportedSnapshotSchemaVersion}.");
        }
        if (descriptor.ItemCount < 0 || descriptor.ReceiptCount < 0 ||
            descriptor.ReceiptCount > descriptor.ItemCount)
        {
            throw new InvalidOperationException(
                "Descriptor item and receipt counts are inconsistent.");
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
        if (descriptor.Sha256.Length != 64 ||
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
        string siteKind,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> expected =
            siteKind == PreparerSubSiteEmitter.Kind
                ? PreparerSchema.Value
                : PlannerSchema.Value;

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
                    $"Snapshot table '{table}' does not match schema v1. " +
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

    private static async Task ValidateBrowserQueriesAsync(
        SqliteConnection connection,
        string siteKind,
        CancellationToken ct)
    {
        string[] queries;
        if (siteKind == PreparerSubSiteEmitter.Kind)
        {
            await ExecuteAsync(
                connection,
                """
                CREATE TEMP VIEW jira_processing_source_tickets AS
                SELECT jh.JiraKey AS Key,
                       jh.Title,
                       CASE WHEN instr(jh.JiraKey, '-') > 1
                            THEN substr(jh.JiraKey, 1, instr(jh.JiraKey, '-') - 1)
                            ELSE '' END AS Project,
                       jh.Status,
                       jh.WorkGroup,
                       jh.Type,
                       jh.Specification
                FROM prepared_jira_hydration jh
                WHERE jh.TicketKey = jh.JiraKey
                """,
                ct).ConfigureAwait(false);
            queries =
            [
                "SELECT pt.*, jst.Title, jst.WorkGroup, jst.Status, jst.Type FROM prepared_tickets pt LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key LIMIT 0",
                "SELECT Repo, RepoCategory, Justification FROM prepared_ticket_repos LIMIT 0",
                "SELECT AssociatedTicketKey, LinkType, Justification FROM prepared_ticket_related_jira LIMIT 0",
                "SELECT ZulipThreadId, Justification FROM prepared_ticket_related_zulip LIMIT 0",
                "SELECT GitHubItemId, Justification FROM prepared_ticket_related_github LIMIT 0",
                "SELECT * FROM prepared_ticket_hydration LIMIT 0",
                "SELECT * FROM prepared_jira_hydration LIMIT 0",
                "SELECT * FROM prepared_zulip_hydration LIMIT 0",
                "SELECT * FROM prepared_github_hydration LIMIT 0",
                "SELECT * FROM prepared_repo_hydration LIMIT 0",
                "SELECT * FROM prepared_ticket_jira_xref LIMIT 0",
                "SELECT DescriptionHtml, ResolutionDescriptionHtml FROM prepared_ticket_jira_content LIMIT 0",
                "SELECT TicketKey, Value FROM prepared_ticket_artifacts LIMIT 0",
                "SELECT TicketKey, Value FROM prepared_ticket_pages LIMIT 0",
                "SELECT RowId, Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint FROM prepared_ticket_topics LIMIT 0",
                "SELECT RowId, Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic FROM prepared_ticket_topic_groups LIMIT 0",
                "SELECT TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer FROM prepared_ticket_topic_members LIMIT 0",
                "SELECT RunId, PartitionKey FROM prepared_ticket_partition_receipts LIMIT 0",
                "SELECT Code, Name, NameClean FROM jira_review_workgroups LIMIT 0",
                "SELECT Id, ProcessorKind FROM authoring_runs LIMIT 0",
                "SELECT Id, RunId, BusinessKey, Status FROM authoring_run_items LIMIT 0",
                "SELECT Id, OperationId, BusinessKey FROM authoring_result_receipts LIMIT 0",
                "SELECT SnapshotId, ProcessorKind, RunId, Sequence FROM authoring_snapshot_provenance LIMIT 0",
            ];
        }
        else
        {
            queries =
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
        }

        foreach (string query in queries)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = query;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>>
        CreateExpectedSchema(string siteKind)
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        string[] publicTables;
        if (siteKind == PreparerSubSiteEmitter.Kind)
        {
            FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.PreparerDatabase
                .EnsureSchema(connection);
            publicTables = PreparerPublicTables;
        }
        else
        {
            FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.PlannerDatabase
                .EnsureSchema(connection);
            publicTables = PlannerPublicTables;
        }

        Dictionary<string, IReadOnlyCollection<string>> schema =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string table in publicTables)
        {
            schema[table] = table switch
            {
                "authoring_runs" => SanitizedAuthoringRunColumns,
                "authoring_run_items" => SanitizedAuthoringRunItemColumns,
                "authoring_result_receipts" => SanitizedReceiptColumns,
                "authoring_snapshot_provenance" => SnapshotProvenanceColumns,
                _ => ReadColumnNames(connection, table),
            };
        }
        return schema;
    }

    private static IReadOnlyList<string> ReadColumnNames(
        SqliteConnection connection,
        string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> columns = [];
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Producer schema did not create required public table '{table}'.");
        }
        return columns;
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

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        long foreignItems = await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM authoring_run_items WHERE RunId <> @runId",
            ct,
            ("@runId", descriptor.RunId)).ConfigureAwait(false);
        if (matchingRun != 1 || foreignItems != 0)
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
        string siteKind,
        CancellationToken ct)
    {
        string ticketTable = siteKind == PreparerSubSiteEmitter.Kind
            ? "prepared_tickets"
            : "planned_tickets";
        string hydrationTable = siteKind == PreparerSubSiteEmitter.Kind
            ? "prepared_ticket_hydration"
            : "planned_ticket_hydration";
        string hydrationKey = siteKind == PreparerSubSiteEmitter.Kind
            ? "TicketKey"
            : "IssueKey";
        string jiraTable = siteKind == PreparerSubSiteEmitter.Kind
            ? "prepared_jira_hydration"
            : "planned_jira_hydration";
        string jiraParentKey = siteKind == PreparerSubSiteEmitter.Kind
            ? "TicketKey"
            : "IssueKey";
        string partitionTable = siteKind == PreparerSubSiteEmitter.Kind
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
