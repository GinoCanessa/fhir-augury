using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class SiteBuildManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ticket-site-snapshot-{Guid.NewGuid():N}");

    public SiteBuildManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_root);

    [Fact]
    public async Task SnapshotModeWritesManifestAndIsIdempotent()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root, sequence: 7);
        string output = Path.Combine(_root, "site");

        (int first, string firstOut, string firstError) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output);
        Assert.True(first == 0, firstError);

        string manifestPath = Path.Combine(
            output,
            "discussion",
            SiteBuildManifest.FileName);
        SiteBuildManifest manifest = SiteBuildManifest.Read(manifestPath);
        Assert.Equal(snapshot.Descriptor.SnapshotId, manifest.SnapshotId);
        Assert.Equal(7, manifest.SnapshotSequence);
        Assert.Equal(1, manifest.IncludedItemCount);
        Assert.Contains(snapshot.Descriptor.SnapshotId, firstOut, StringComparison.Ordinal);
        DateTime firstWrite = File.GetLastWriteTimeUtc(manifestPath);

        await Task.Delay(50);
        (int second, string secondOut, _) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output);
        Assert.Equal(0, second);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(manifestPath));
        Assert.Contains(snapshot.Descriptor.SnapshotId, secondOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSnapshotWithDifferentTitleRebuildsWithNewBuildIdentity()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root, sequence: 8);
        string output = Path.Combine(_root, "title-rebuild");

        Assert.Equal(0, (await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output,
            "--title", "First title")).Exit);
        string manifestPath = Path.Combine(
            output,
            "discussion",
            SiteBuildManifest.FileName);
        SiteBuildManifest first = SiteBuildManifest.Read(manifestPath);

        Assert.Equal(0, (await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output,
            "--title", "Second title")).Exit);
        SiteBuildManifest second = SiteBuildManifest.Read(manifestPath);

        Assert.NotEqual(first.BuildIdentity, second.BuildIdentity);
        Assert.Equal("Second title", second.Title);
        Assert.Contains(
            "Second title",
            await File.ReadAllTextAsync(Path.Combine(output, "discussion", "index.html")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlderSequenceCannotReplaceNewerPublishedSite()
    {
        TicketSnapshotFixture newer =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 11,
                snapshotId: "snapshot-11");
        TicketSnapshotFixture older =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 10,
                snapshotId: "snapshot-10");
        string output = Path.Combine(_root, "stale-site");

        (int newerExit, _, string newerError) = await RunAsync(
            "--preparer-snapshot", newer.DatabasePath,
            "--snapshot-descriptor", newer.DescriptorPath,
            "--out", output);
        Assert.True(newerExit == 0, newerError);
        (int staleExit, _, string staleError) = await RunAsync(
            "--preparer-snapshot", older.DatabasePath,
            "--snapshot-descriptor", older.DescriptorPath,
            "--out", output);

        Assert.Equal(1, staleExit);
        Assert.Contains("older than", staleError, StringComparison.OrdinalIgnoreCase);
        SiteBuildManifest manifest = SiteBuildManifest.Read(Path.Combine(
            output,
            "discussion",
            SiteBuildManifest.FileName));
        Assert.Equal("snapshot-11", manifest.SnapshotId);
        Assert.Equal(11, manifest.SnapshotSequence);
    }

    [Fact]
    public async Task DescriptorRunMismatchIsRejected()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        AuthoringSnapshotDescriptor mismatched =
            snapshot.Descriptor with { RunId = "wrong-run" };
        await TicketSnapshotFixture.WriteDescriptorAsync(
            snapshot.DescriptorPath,
            mismatched);

        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", Path.Combine(_root, "run-mismatch"));

        Assert.Equal(1, exit);
        Assert.Contains("provenance", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DescriptorChecksumMismatchIsRejectedBeforeDatabaseUse()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        AuthoringSnapshotDescriptor mismatched =
            snapshot.Descriptor with { Sha256 = new string('0', 64) };
        await TicketSnapshotFixture.WriteDescriptorAsync(
            snapshot.DescriptorPath,
            mismatched);

        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", Path.Combine(_root, "checksum-mismatch"));

        Assert.Equal(1, exit);
        Assert.Contains("SHA-256 mismatch", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DescriptorTableCountMismatchIsRejected()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        Dictionary<string, long> counts =
            new(snapshot.Descriptor.TableCounts, StringComparer.Ordinal)
            {
                ["prepared_tickets"] = 2,
            };
        AuthoringSnapshotDescriptor mismatched =
            snapshot.Descriptor with { TableCounts = counts };
        await TicketSnapshotFixture.WriteDescriptorAsync(
            snapshot.DescriptorPath,
            mismatched);

        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", Path.Combine(_root, "count-mismatch"));

        Assert.Equal(1, exit);
        Assert.Contains("table counts", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotWithInternalTableIsRejected()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        await using (SqliteConnection connection = new(
            $"Data Source={snapshot.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE authoring_run_attempts(Id TEXT, OperationToken TEXT)";
            await command.ExecuteNonQueryAsync();
        }
        await snapshot.RefreshDescriptorHashAsync();

        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", Path.Combine(_root, "secret-table"));

        Assert.Equal(1, exit);
        Assert.Contains("forbidden", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotWithSchemaV1ColumnDriftIsRejected()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        await using (SqliteConnection connection = new(
            $"Data Source={snapshot.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE prepared_ticket_repos ADD COLUMN UnexpectedV2Column TEXT";
            await command.ExecuteNonQueryAsync();
        }
        await snapshot.RefreshDescriptorHashAsync();

        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", Path.Combine(_root, "schema-drift"));

        Assert.Equal(1, exit);
        Assert.Contains("schema v1", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnexpectedV2Column", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotRejectsSidecarsBeforeDatabaseValidation()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        await File.WriteAllTextAsync(snapshot.DatabasePath + "-wal", "not a snapshot");

        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", Path.Combine(_root, "sidecar"));

        Assert.Equal(1, exit);
        Assert.Contains("sidecar", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyAndSnapshotModesRequireForceAndPreserveSequenceState()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 12,
                snapshotId: "snapshot-12");
        TicketSnapshotFixture older =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 11,
                snapshotId: "snapshot-11-ownership");
        string legacyDatabase = Path.Combine(_root, "legacy-preparer.db");
        await PreparerTestDb.SeedAsync(
            legacyDatabase,
            [new PreparerTestDb.SourceTicketSeed("FHIR-1001")]);
        string output = Path.Combine(_root, "ownership");

        Assert.Equal(0, (await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output)).Exit);

        (int legacyExit, _, string legacyError) = await RunAsync(
            "--preparer-db", legacyDatabase,
            "--out", output);
        Assert.Equal(1, legacyExit);
        Assert.Contains("snapshot", legacyError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--force", legacyError, StringComparison.Ordinal);

        Assert.Equal(0, (await RunAsync(
            "--preparer-db", legacyDatabase,
            "--out", output,
            "--force")).Exit);

        (int staleExit, _, string staleError) = await RunAsync(
            "--preparer-snapshot", older.DatabasePath,
            "--snapshot-descriptor", older.DescriptorPath,
            "--out", output,
            "--force");
        Assert.Equal(1, staleExit);
        Assert.Contains("preserved sequence 12", staleError, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(
            output,
            "discussion",
            SiteBuildManifest.FileName)));
    }

    [Fact]
    public async Task SnapshotCannotTakeOverUnownedOutputWithoutForce()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        string output = Path.Combine(_root, "unowned");
        string discussion = Path.Combine(output, "discussion");
        Directory.CreateDirectory(discussion);
        await File.WriteAllTextAsync(Path.Combine(discussion, "index.html"), "unrelated");

        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output);
        Assert.Equal(1, exit);
        Assert.Contains("not owned", error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, (await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output,
            "--force")).Exit);
    }

    [Fact]
    public async Task CommittedSnapshotRemainsSuccessfulWhenFilteredDatabaseCleanupFails()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        string output = Path.Combine(_root, "cleanup-warning");
        string? deferredPath = null;
        TicketSiteCleanupHooks hooks = new(
            DeleteFilteredDatabaseAsync: path =>
            {
                deferredPath = path;
                throw new IOException("simulated filtered DB cleanup failure");
            });

        try
        {
            (int exit, string stdout, string stderr) =
                await RunWithCleanupHooksAsync(
                    hooks,
                    "--preparer-snapshot", snapshot.DatabasePath,
                    "--snapshot-descriptor", snapshot.DescriptorPath,
                    "--out", output,
                    "--spec", "FHIR");

            Assert.Equal(0, exit);
            Assert.Contains(snapshot.Descriptor.SnapshotId, stdout, StringComparison.Ordinal);
            Assert.Contains("Warning: deferred cleanup", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("publication failed", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(
                output,
                "discussion",
                SiteBuildManifest.FileName)));
        }
        finally
        {
            if (deferredPath is not null)
            {
                TestFileCleanup.SafeDeleteFile(deferredPath);
            }
        }
    }

    [Fact]
    public async Task SnapshotInputsInsideChooserOutputAreRejectedBeforePublication()
    {
        string output = Path.Combine(_root, "contained-output");
        Directory.CreateDirectory(output);
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(output);

        (int exit, _, string stderr) = await RunAsync(
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output);

        Assert.Equal(1, exit);
        Assert.Contains("publisher control", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(snapshot.DatabasePath));
        Assert.True(File.Exists(snapshot.DescriptorPath));
        Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        params string[] args)
        => await RunWithCleanupHooksAsync(null, args);

    private static async Task<(int Exit, string Stdout, string Stderr)>
        RunWithCleanupHooksAsync(
            TicketSiteCleanupHooks? cleanupHooks,
            params string[] args)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int exit = await Program.RunAsync(args, cleanupHooks);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

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
