using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets.Tests;

internal sealed class TicketSnapshotFixture
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private TicketSnapshotFixture(
        string databasePath,
        string descriptorPath,
        AuthoringSnapshotDescriptor descriptor)
    {
        DatabasePath = databasePath;
        DescriptorPath = descriptorPath;
        Descriptor = descriptor;
    }

    public string DatabasePath { get; }
    public string DescriptorPath { get; }
    public AuthoringSnapshotDescriptor Descriptor { get; private set; }

    public async Task<VerifiedAuthoringSnapshotPair> CreateVerifiedPairAsync(
        string serviceName)
    {
        string pairDirectory = Path.Combine(
            Path.GetDirectoryName(DatabasePath) ?? Environment.CurrentDirectory,
            $"pair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pairDirectory);
        string databaseFileName = Path.GetFileName(DatabasePath);
        string descriptorFileName = Path.GetFileName(DescriptorPath);
        string pairDatabasePath = Path.Combine(
            pairDirectory,
            databaseFileName);
        string pairDescriptorPath = Path.Combine(
            pairDirectory,
            descriptorFileName);
        File.Copy(DatabasePath, pairDatabasePath);
        File.Copy(DescriptorPath, pairDescriptorPath);
        byte[] descriptorBytes =
            await File.ReadAllBytesAsync(pairDescriptorPath);
        AuthoringSnapshotPairManifest manifest = new(
            AuthoringSnapshotPairManifest.CurrentFormatVersion,
            serviceName,
            Descriptor.RunId,
            Descriptor.SnapshotId,
            descriptorFileName,
            databaseFileName,
            Descriptor.SizeBytes,
            Convert.ToHexString(SHA256.HashData(descriptorBytes))
                .ToLowerInvariant(),
            Descriptor.Sha256);
        await File.WriteAllTextAsync(
            Path.Combine(
                pairDirectory,
                AuthoringSnapshotPairManifest.ReadyFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                serviceName,
                Descriptor.RunId,
                pairDirectory);
    }

    public async Task MoveSecondTicketToHistoricalLedgerAsync()
    {
        await using SqliteConnection connection = new(
            $"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly,
                TotalItems, CreatedAt, StartedAt, CompletedAt)
            VALUES(
                'historical-run', 'jira-fhir', 1, 'superseded', 0, 1,
                @createdAt, @createdAt, @createdAt);
            UPDATE authoring_run_items
            SET RunId = 'historical-run',
                Status = 'superseded'
            WHERE Id = 'item-2';
            UPDATE authoring_result_receipts
            SET RunId = 'historical-run'
            WHERE Id = 'receipt-2';
            UPDATE authoring_runs
            SET TotalItems = 1
            WHERE Id = @currentRunId;
            UPDATE authoring_snapshot_provenance
            SET ItemCount = 1,
                ReceiptCount = 2;
            """,
            ("@createdAt", Descriptor.CreatedAt.ToString("O")),
            ("@currentRunId", Descriptor.RunId));
        await connection.CloseAsync();

        Descriptor = await CreateDescriptorAsync(
            DatabasePath,
            Descriptor.RunId,
            Descriptor.SnapshotId,
            Descriptor.Sequence,
            Descriptor.TableCounts,
            Descriptor.CreatedAt,
            itemCount: 1,
            receiptCount: 2);
        await WriteDescriptorAsync(DescriptorPath, Descriptor);
    }

    public static async Task<TicketSnapshotFixture> CreatePreparerAsync(
        string root,
        long sequence = 1,
        string? snapshotId = null,
        bool includeSecondTicket = false)
    {
        string id = snapshotId ?? $"snapshot-{Guid.NewGuid():N}";
        string databasePath = Path.Combine(root, $"jira-fhir-{id}.db");
        string descriptorPath = databasePath + ".json";
        string runId = $"run-{id}";
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.PreparerDatabase
            .EnsureSchema(connection);
        await ExecuteAsync(connection,
            """
            CREATE TABLE authoring_snapshot_provenance(SnapshotId TEXT, ProcessorKind TEXT, RunId TEXT, AuthoringEpoch INTEGER, Sequence INTEGER, SchemaVersion INTEGER, ItemCount INTEGER, ReceiptCount INTEGER, TableCountsJson TEXT, CreatedAt TEXT);

            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                CreatedAt, StartedAt, CompletedAt, SnapshotId)
            VALUES(@runId, 'jira-fhir', 1, 'finalizing', 0, 1,
                @createdAt, @createdAt, NULL, @snapshotId);
            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
            VALUES('item-1', @runId, 'FHIR-1001', 'ticket', 'rev-1', 'complete',
                'receipt-1', 1, @createdAt, @createdAt, @createdAt);
            INSERT INTO authoring_result_receipts(
                Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            VALUES('receipt-1', 'operation-1', @runId, 'item-1', 'FHIR-1001',
                'hash', 'rev-1', 'rev-1', 1, @createdAt);
            INSERT INTO prepared_tickets(
                Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary,
                ExistingProposed, ProposalA, ProposalAJustification, ProposalAImpact,
                ProposalB, ProposalBJustification, ProposalBImpact, ProposalC,
                ProposalCJustification, Recommendation, RecommendationJustification, SavedAt)
            VALUES('prepared-1', 'FHIR-1001', 'Request', '', '', '', '', '', '',
                'A', 'A because', 'Non-substantive', 'B', 'B because',
                'Compatible, substantive', 'C', 'C because', 'A', 'Because', @createdAt);
            INSERT INTO prepared_ticket_hydration(
                Id, TicketKey, Specification, HydratedAt, HydrationStatus)
            VALUES('hydration-1', 'FHIR-1001', 'FHIR', @createdAt, 'resolved');
            INSERT INTO prepared_jira_hydration(
                Id, TicketKey, JiraKey, Title, Status, Type, WorkGroup, WorkGroupClean,
                Specification, HydratedAt, HydrationStatus)
            VALUES('jira-1', 'FHIR-1001', 'FHIR-1001', 'Snapshot title', 'Open',
                'Change Request', 'FHIR Infrastructure', 'FHIRInfrastructure',
                'FHIR', @createdAt, 'resolved');
            INSERT INTO prepared_ticket_jira_content(
                TicketKey, DescriptionHtml, ResolutionDescriptionHtml)
            VALUES('FHIR-1001', '<p>request html</p>', '<p>resolution html</p>');
            INSERT INTO prepared_ticket_artifacts(TicketKey, Value)
            VALUES('FHIR-1001', 'Observation');
            INSERT INTO prepared_ticket_pages(TicketKey, Value)
            VALUES('FHIR-1001', 'patient.html');
            INSERT INTO prepared_ticket_partition_receipts(
                RunId, StageId, PartitionKey, InputFingerprint, TopicRows,
                TopicGroupRows, MemberRows, PersistedAt)
            VALUES(@runId, 'grouping', 'FHIRInfrastructure|FHIR|Change Request',
                'fingerprint-1', 0, 0, 0, @createdAt);
            INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
            VALUES('fhir-i', 'FHIR Infrastructure', 'FHIRInfrastructure', @createdAt);
            """,
            ("@runId", runId),
            ("@snapshotId", id),
            ("@createdAt", createdAt.ToString("O")));

        int itemCount = 1;
        if (includeSecondTicket)
        {
            itemCount = 2;
            await ExecuteAsync(connection,
                """
                INSERT INTO authoring_run_items(
                    Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                    AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
                VALUES('item-2', @runId, 'CDS-2001', 'ticket', 'rev-2', 'complete',
                    'receipt-2', 1, @createdAt, @createdAt, @createdAt);
                INSERT INTO authoring_result_receipts(
                    Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                    ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
                VALUES('receipt-2', 'operation-2', @runId, 'item-2', 'CDS-2001',
                    'hash-2', 'rev-2', 'rev-2', 1, @createdAt);
                INSERT INTO prepared_tickets(
                    Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                    RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary,
                    ExistingProposed, ProposalA, ProposalAJustification, ProposalAImpact,
                    ProposalB, ProposalBJustification, ProposalBImpact, ProposalC,
                    ProposalCJustification, Recommendation, RecommendationJustification, SavedAt)
                VALUES('prepared-2', 'CDS-2001', 'CDS request', '', '', '', '', '', '',
                    'A', 'A because', 'Non-substantive', 'B', 'B because',
                    'Compatible, substantive', 'C', 'C because', 'A', 'Because', @createdAt);
                INSERT INTO prepared_ticket_hydration(
                    Id, TicketKey, Specification, HydratedAt, HydrationStatus)
                VALUES('hydration-2', 'CDS-2001', 'CDS Hooks', @createdAt, 'resolved');
                INSERT INTO prepared_jira_hydration(
                    Id, TicketKey, JiraKey, Title, Status, Type, WorkGroup, WorkGroupClean,
                    Specification, HydratedAt, HydrationStatus)
                VALUES('jira-2', 'CDS-2001', 'CDS-2001', 'CDS title', 'Open',
                    'Change Request', 'Clinical Decision Support', 'ClinicalDecisionSupport',
                    'CDS Hooks', @createdAt, 'resolved');
                INSERT INTO prepared_ticket_partition_receipts(
                    RunId, StageId, PartitionKey, InputFingerprint, TopicRows,
                    TopicGroupRows, MemberRows, PersistedAt)
                VALUES(@runId, 'grouping', 'ClinicalDecisionSupport|CDS Hooks|Change Request',
                    'fingerprint-2', 0, 0, 0, @createdAt);
                INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
                VALUES('cds', 'Clinical Decision Support', 'ClinicalDecisionSupport', @createdAt);
                """,
                ("@runId", runId),
                ("@createdAt", createdAt.ToString("O")));
        }

        string[] countTables =
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
        await SanitizeSnapshotSchemaAsync(
            connection,
            [
                "authoring_runs",
                "authoring_run_items",
                "authoring_result_receipts",
                "authoring_snapshot_provenance",
                .. countTables,
                "prepared_github_hydration",
                "prepared_repo_hydration",
                "prepared_ticket_jira_xref",
                "prepared_zulip_hydration",
            ]);
        Dictionary<string, long> counts =
            await ReadCountsAsync(connection, countTables);
        await ExecuteAsync(connection,
            """
            INSERT INTO authoring_snapshot_provenance
            VALUES(@snapshotId, 'jira-fhir', @runId, 1, @sequence, 1, @itemCount, @itemCount, @counts, @createdAt)
            """,
            ("@snapshotId", id),
            ("@runId", runId),
            ("@sequence", sequence),
            ("@itemCount", itemCount),
            ("@counts", JsonSerializer.Serialize(counts)),
            ("@createdAt", createdAt.ToString("O")));
        await connection.CloseAsync();

        AuthoringSnapshotDescriptor descriptor = await CreateDescriptorAsync(
            databasePath,
            runId,
            id,
            sequence,
            counts,
            createdAt,
            itemCount,
            itemCount);
        await WriteDescriptorAsync(descriptorPath, descriptor);
        return new TicketSnapshotFixture(databasePath, descriptorPath, descriptor);
    }

    public static async Task<TicketSnapshotFixture> CreatePlannerAsync(
        string root,
        long sequence = 1)
    {
        string id = $"planner-{Guid.NewGuid():N}";
        string databasePath = Path.Combine(root, $"jira-fhir-{id}.db");
        string descriptorPath = databasePath + ".json";
        string runId = $"run-{id}";
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.PlannerDatabase
            .EnsureSchema(connection);
        await ExecuteAsync(connection,
            """
            CREATE TABLE authoring_snapshot_provenance(SnapshotId TEXT, ProcessorKind TEXT, RunId TEXT, AuthoringEpoch INTEGER, Sequence INTEGER, SchemaVersion INTEGER, ItemCount INTEGER, ReceiptCount INTEGER, TableCountsJson TEXT, CreatedAt TEXT);

            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                CreatedAt, StartedAt, CompletedAt, SnapshotId)
            VALUES(@runId, 'jira-fhir', 1, 'finalizing', 0, 1,
                @createdAt, @createdAt, NULL, @snapshotId);
            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
            VALUES('item-1', @runId, 'FHIR-2001', 'ticket', 'rev-1', 'complete',
                'receipt-1', 1, @createdAt, @createdAt, @createdAt);
            INSERT INTO authoring_result_receipts(
                Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            VALUES('receipt-1', 'operation-1', @runId, 'item-1', 'FHIR-2001',
                'hash', 'rev-1', 'rev-1', 1, @createdAt);
            INSERT INTO planned_tickets(
                Id, Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt)
            VALUES('planned-1', 'FHIR-2001', 'Persuasive', 'Summary', 'Proposal',
                'Rationale', @createdAt);
            INSERT INTO planned_ticket_hydration(
                IssueKey, Specification, HydratedAt, HydrationStatus)
            VALUES('FHIR-2001', 'FHIR', @createdAt, 'resolved');
            INSERT INTO planned_jira_hydration(
                IssueKey, JiraKey, Title, Status, Type, WorkGroup, WorkGroupClean,
                Specification, HydratedAt, HydrationStatus)
            VALUES('FHIR-2001', 'FHIR-2001', 'Planner title', 'Open', 'Change Request',
                'FHIR Infrastructure', 'FHIRInfrastructure', 'FHIR', @createdAt, 'resolved');
            INSERT INTO planned_ticket_jira_content(
                TicketKey, DescriptionHtml, ResolutionDescriptionHtml)
            VALUES('FHIR-2001', '<p>planner request</p>', '<p>planner resolution</p>');
            INSERT INTO planned_ticket_partition_receipts(
                RunId, StageId, PartitionKey, InputFingerprint, TopicRows,
                TopicGroupRows, MemberRows, PersistedAt)
            VALUES(@runId, 'grouping', 'FHIRInfrastructure|FHIR|Change Request',
                'fingerprint-1', 0, 0, 0, @createdAt);
            INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
            VALUES('fhir-i', 'FHIR Infrastructure', 'FHIRInfrastructure', @createdAt);
            """,
            ("@runId", runId),
            ("@snapshotId", id),
            ("@createdAt", createdAt.ToString("O")));

        string[] countTables =
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
        await SanitizeSnapshotSchemaAsync(
            connection,
            [
                "authoring_runs",
                "authoring_run_items",
                "authoring_result_receipts",
                "authoring_snapshot_provenance",
                .. countTables,
                "planned_github_hydration",
                "planned_repo_hydration",
                "planned_ticket_jira_xref",
                "planned_ticket_related_github",
                "planned_ticket_related_jira",
                "planned_ticket_related_zulip",
                "planned_zulip_hydration",
            ]);
        Dictionary<string, long> counts =
            await ReadCountsAsync(connection, countTables);
        await ExecuteAsync(connection,
            """
            INSERT INTO authoring_snapshot_provenance
            VALUES(@snapshotId, 'jira-fhir', @runId, 1, @sequence, 1, 1, 1, @counts, @createdAt)
            """,
            ("@snapshotId", id),
            ("@runId", runId),
            ("@sequence", sequence),
            ("@counts", JsonSerializer.Serialize(counts)),
            ("@createdAt", createdAt.ToString("O")));
        await connection.CloseAsync();

        AuthoringSnapshotDescriptor descriptor = await CreateDescriptorAsync(
            databasePath,
            runId,
            id,
            sequence,
            counts,
            createdAt,
            1,
            1);
        await WriteDescriptorAsync(descriptorPath, descriptor);
        return new TicketSnapshotFixture(databasePath, descriptorPath, descriptor);
    }

    public async Task RefreshDescriptorHashAsync()
    {
        Descriptor = Descriptor with
        {
            Sha256 = await ComputeHashAsync(DatabasePath),
            SizeBytes = new FileInfo(DatabasePath).Length,
        };
        await WriteDescriptorAsync(DescriptorPath, Descriptor);
    }

    public static Task WriteDescriptorAsync(
        string path,
        AuthoringSnapshotDescriptor descriptor)
        => File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(descriptor, JsonOptions));

    private static async Task<AuthoringSnapshotDescriptor> CreateDescriptorAsync(
        string databasePath,
        string runId,
        string snapshotId,
        long sequence,
        IReadOnlyDictionary<string, long> counts,
        DateTimeOffset createdAt,
        int itemCount,
        int receiptCount)
        => new(
            "jira-fhir",
            runId,
            snapshotId,
            1,
            sequence,
            1,
            await ComputeHashAsync(databasePath),
            new FileInfo(databasePath).Length,
            itemCount,
            receiptCount,
            counts,
            Path.GetFileName(databasePath),
            createdAt);

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }

    private static async Task<Dictionary<string, long>> ReadCountsAsync(
        SqliteConnection connection,
        IEnumerable<string> tables)
    {
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            result[table] = Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        return result;
    }

    private static async Task SanitizeSnapshotSchemaAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> publicTables)
    {
        Dictionary<string, string[]> selectedColumns =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["authoring_runs"] =
                [
                    "Id", "ProcessorKind", "AuthoringEpoch", "Status", "DatabaseOnly",
                    "TotalItems", "CreatedAt", "StartedAt", "CompletedAt", "SnapshotId",
                ],
                ["authoring_run_items"] =
                [
                    "Id", "RunId", "BusinessKey", "ItemKind", "ExpectedSourceRevision",
                    "Status", "AcceptedReceiptId", "AttemptCount", "CreatedAt", "StartedAt",
                    "CompletedAt",
                ],
                ["authoring_result_receipts"] =
                [
                    "Id", "OperationId", "RunId", "RunItemId", "BusinessKey",
                    "ContentHash", "ExpectedSourceRevision", "ObservedSourceRevision",
                    "AuthoringEpoch", "PersistedAt",
                ],
            };

        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF");
        List<(string Type, string Name)> objects = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT type, name
                FROM sqlite_master
                WHERE type IN ('table', 'view', 'trigger')
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY type, name
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                objects.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach ((string type, string name) in objects.Where(item => item.Type != "table"))
        {
            await ExecuteAsync(
                connection,
                $"DROP {type.ToUpperInvariant()} IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        }
        foreach ((_, string name) in objects.Where(item => item.Type == "table"))
        {
            if (!publicTables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                await ExecuteAsync(
                    connection,
                    $"DROP TABLE IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
            }
        }

        foreach ((string table, string[] columns) in selectedColumns)
        {
            string replacement = $"__snapshot_{Guid.NewGuid():N}";
            string columnList = string.Join(
                ", ",
                columns.Select(column =>
                    $"\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
            await ExecuteAsync(
                connection,
                $"""
                CREATE TABLE "{replacement}" AS
                SELECT {columnList} FROM "{table}";
                DROP TABLE "{table}";
                ALTER TABLE "{replacement}" RENAME TO "{table}";
                """);
        }
        await ExecuteAsync(connection, "VACUUM; PRAGMA foreign_keys = ON");
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }
}
