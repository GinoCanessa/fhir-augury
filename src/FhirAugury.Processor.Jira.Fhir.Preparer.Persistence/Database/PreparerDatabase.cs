using System.Globalization;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Common.Text;
using FhirAugury.Common.WorkGroups;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

public sealed class PreparerDatabase(string dbPath, ILogger<PreparerDatabase> logger, bool readOnly = false)
    : FhirAugury.Processing.Common.Database.ProcessingDatabase(dbPath, logger, readOnly),
      IHydrationTargetDatabase,
      IAuthoringCutoverParticipant
{
    private const string AuthoringProcessorKind = "jira-fhir";
    public const string PublicationMetadataStageName =
        "publication-metadata";
    public const string GroupingCertificationStageName =
        "grouping-certification";
    public const string PublicationReconciliationGroupingStageName =
        "publication-reconciliation-grouping";
    private const string PublicationReconciliationSnapshotReservationKind =
        "snapshot-reservation-v1";
    public const string CurrentSnapshotReceiptBackedTicketsTable =
        "current_prepared_receipt_backed_tickets";
    private static readonly string MaintenanceOwnerGeneration = Guid.NewGuid().ToString("N");
    private FileStream? _startupOwnerLock;

    public string DatabasePath { get; } = dbPath;

    public void AcquireStartupOwnership()
    {
        if (_startupOwnerLock is not null)
        {
            return;
        }
        string ownerPath = $"{Path.GetFullPath(DatabasePath)}.owner.lock";
        Directory.CreateDirectory(
            Path.GetDirectoryName(ownerPath)
            ?? throw new InvalidOperationException(
                $"Database path '{DatabasePath}' has no parent directory."));
        try
        {
            _startupOwnerLock = new FileStream(
                ownerPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another Preparer service generation owns database '{DatabasePath}'.",
                ex);
        }
    }

    /// <summary>
    /// Idempotent. Creates every preparer table via <c>CREATE TABLE IF NOT EXISTS</c>
    /// and follows up with the <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> passes required
    /// by CsLightDbGen's lack of composite-unique support. Safe to call against a
    /// connection the preparer does not own (e.g., <c>ticket-site</c> discussion sub-site's trim-step
    /// temp copy).
    /// </summary>
    public static void EnsureSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        EnsureSchema(connection, logger: null);
    }

    /// <summary>
    /// Logger-aware overload used by the instance bootstrap path; <see cref="EnsureSchema(SqliteConnection)"/>
    /// remains available for ad-hoc callers (e.g. <c>ticket-site</c> discussion sub-site's trim-step temp copy).
    /// </summary>
    internal static void EnsureSchema(SqliteConnection connection, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        JiraProcessingSourceTicketStore.EnsureSchema(connection);
        AuthoringRunStore.EnsureSchema(connection);

        // Legacy migration before CreateTable: the generated
        // PreparedJiraHydrationRecord.CreateTable builds an index over
        // WorkGroupClean, which errors on a pre-feature schema under DQS-off
        // SQLite when the column is absent. No-ops on a fresh DB (table absent).
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "prepared_jira_hydration", "WorkGroupClean", "TEXT NULL");
        foreach (string column in new[]
        {
            "DescriptionHtml",
            "ResolutionDescriptionHtml",
            "Reporter",
            "CreatedAt",
            "RelatedArtifactsRaw",
            "RelatedPagesRaw",
        })
        {
            SqliteSchemaHelpers.AddColumnIfMissing(connection, "prepared_ticket_hydration", column, "TEXT NULL");
            SqliteSchemaHelpers.AddColumnIfMissing(connection, "prepared_jira_hydration", column, "TEXT NULL");
        }
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_hydration",
            "Assignee",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_hydration",
            "SourceProject",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_hydration",
            "SourceLastSuccessfulRefreshAt",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_hydration",
            "SourceContentRevision",
            "INTEGER NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_jira_hydration",
            "Assignee",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_hydration",
            "PublicDisplayNamePolicyVersion",
            "INTEGER NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_jira_hydration",
            "PublicDisplayNamePolicyVersion",
            "INTEGER NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_in_person_requesters",
            "PublicDisplayNamePolicyVersion",
            "INTEGER NULL");

        PreparedTicketRecord.CreateTable(connection);
        PreparedTicketJiraContentRecord.CreateTable(connection);
        PreparedTicketArtifactRecord.CreateTable(connection);
        PreparedTicketPageRecord.CreateTable(connection);
        PreparedTicketRepoRecord.CreateTable(connection);
        PreparedTicketRelatedJiraRecord.CreateTable(connection);
        PreparedTicketRelatedZulipRecord.CreateTable(connection);
        PreparedTicketRelatedGitHubRecord.CreateTable(connection);
        PreparedTicketHydrationRecord.CreateTable(connection);
        PreparedJiraHydrationRecord.CreateTable(connection);
        PreparedZulipHydrationRecord.CreateTable(connection);
        PreparedGitHubHydrationRecord.CreateTable(connection);
        PreparedRepoHydrationRecord.CreateTable(connection);
        PreparedTicketJiraXrefRecord.CreateTable(connection);
        PreparedTicketInPersonRequesterRecord.CreateTable(connection);
        PreparedTicketTopicRecord.CreateTable(connection);
        PreparedTicketTopicGroupRecord.CreateTable(connection);
        PreparedTicketTopicMemberRecord.CreateTable(connection);
        PreparedTicketPublicationRefreshReceiptRecord.CreateTable(connection);
        PreparedTicketPartitionCertificationRecord.CreateTable(connection);
        PreparedTicketPublicationReconciliationRecord.CreateTable(connection);
        PreparedTicketPublicationReconciliationJournalRecord.CreateTable(connection);
        JiraReviewWorkGroupRecord.CreateTable(connection);
        EnsureMigrationsTable(connection);
        EnsureAuthoringStateTables(connection);
        EnsurePublicationReconciliationTables(connection);
        EnsurePublicationCompositeIndexes(connection);
        EnsureCanonicalCompositeIndexes(connection);
        EnsureHydrationWorkGroupCleanColumn(connection);
        EnsureHydrationCompositeUniqueIndexes(connection);
        EnsureGroupingCompositeUniqueIndexes(connection);
        RunMigrationsIfNeeded(connection, logger);
        SeedLegacyRunInputProvenance(connection);
    }

    private static void EnsureAuthoringStateTables(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS prepared_ticket_authoring_state(
                TicketKey TEXT PRIMARY KEY,
                Classification TEXT NOT NULL,
                GraphHash TEXT NOT NULL,
                ReceiptContentHash TEXT NULL,
                RunId TEXT NULL,
                RunItemId TEXT NULL,
                OperationId TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_prepared_ticket_authoring_state_classification
            ON prepared_ticket_authoring_state(Classification);

            CREATE TABLE IF NOT EXISTS prepared_ticket_partition_receipts(
                RunId TEXT NOT NULL,
                StageId TEXT NOT NULL,
                PartitionKey TEXT NOT NULL,
                InputFingerprint TEXT NOT NULL,
                OutputFingerprint TEXT NULL,
                TopicRows INTEGER NOT NULL,
                TopicGroupRows INTEGER NOT NULL,
                MemberRows INTEGER NOT NULL,
                PersistedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, PartitionKey)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_run_item_partitions(
                RunItemId TEXT PRIMARY KEY,
                TicketKey TEXT NOT NULL,
                WorkGroupClean TEXT NULL,
                WorkGroupDisplay TEXT NULL,
                Specification TEXT NULL,
                Type TEXT NULL,
                CapturedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_authoring_state",
            "ReceiptContentHash",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_partition_receipts",
            "OutputFingerprint",
            "TEXT NULL");
    }

    private static void EnsurePublicationReconciliationTables(
        SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_reconciliation_items(
                RunId TEXT NOT NULL,
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                Disposition TEXT NOT NULL,
                BaselineSourceRevision TEXT NOT NULL,
                CurrentSourceRevision TEXT NOT NULL,
                BaselineReceiptId TEXT NOT NULL,
                BaselineRunItemId TEXT NOT NULL,
                BaselineContributingRunId TEXT NOT NULL,
                ItemKind TEXT NULL,
                ExpectedSourceRevision TEXT NULL,
                BaselineAuthoredFingerprint TEXT NOT NULL,
                BaselineGroupingFingerprint TEXT NOT NULL,
                PRIMARY KEY(RunId, TicketKey)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_staged_graphs(
                RunId TEXT NOT NULL,
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                RunItemId TEXT NOT NULL,
                OperationId TEXT NOT NULL,
                SourceRevision TEXT NOT NULL,
                AuthoredFingerprint TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                StagedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, TicketKey)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_staged_hydration(
                RunId TEXT NOT NULL,
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                HydrationFingerprint TEXT NOT NULL,
                HydrationJson TEXT NOT NULL,
                StagedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, TicketKey)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_staged_receipts(
                RunId TEXT NOT NULL,
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                ReceiptId TEXT NOT NULL,
                RunItemId TEXT NOT NULL,
                OperationId TEXT NOT NULL,
                AuthoredFingerprint TEXT NOT NULL,
                PersistedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, TicketKey),
                UNIQUE(ReceiptId)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_grouping_impacts(
                RunId TEXT NOT NULL,
                PartitionKey TEXT NOT NULL,
                ImpactJson TEXT NOT NULL,
                Complete INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, PartitionKey)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_staged_grouping(
                RunId TEXT NOT NULL,
                PartitionKey TEXT NOT NULL,
                ReplacementJson TEXT NOT NULL,
                CorpusFingerprint TEXT NOT NULL,
                OutputFingerprint TEXT NOT NULL,
                ProtectedRowsFingerprint TEXT NOT NULL,
                StagedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, PartitionKey)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_grouping_stage_receipts(
                RunId TEXT NOT NULL,
                StageId TEXT NOT NULL,
                StageLeaseId TEXT NOT NULL,
                PartitionKey TEXT NOT NULL,
                InputFingerprint TEXT NOT NULL,
                OutputFingerprint TEXT NOT NULL,
                TopicRows INTEGER NOT NULL,
                TopicGroupRows INTEGER NOT NULL,
                MemberRows INTEGER NOT NULL,
                PersistedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, PartitionKey)
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_unaffected_fingerprints(
                RunId TEXT PRIMARY KEY,
                FingerprintJson TEXT NOT NULL,
                CapturedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_reconciliation_proofs(
                RunId TEXT PRIMARY KEY,
                ProofJson TEXT NOT NULL,
                CapturedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_reconciliation_fences(
                RunId TEXT PRIMARY KEY,
                LeaseId TEXT NOT NULL,
                AcquiredAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS prepared_ticket_publication_snapshot_descriptors(
                RunId TEXT PRIMARY KEY,
                DescriptorJson TEXT NOT NULL,
                Sha256 TEXT NOT NULL,
                PersistedAt TEXT NOT NULL
            );

            CREATE TRIGGER IF NOT EXISTS prevent_snapshot_after_unpublished_canonical
            BEFORE INSERT ON authoring_runs
            WHEN NEW.DatabaseOnly = 0
             AND EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                WHERE PromotionState = 'canonical-unpublished')
            BEGIN
                SELECT RAISE(
                    ABORT,
                    'canonical-unpublished-restriction');
            END;
            """;
        command.ExecuteNonQuery();
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_publication_reconciliation_items",
            "ItemKind",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_publication_reconciliation_items",
            "ExpectedSourceRevision",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_publication_reconciliations",
            "CancelledAt",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_ticket_publication_reconciliations",
            "CancellationReason",
            "TEXT NULL");
    }

    public async Task<AuthoringRunRecord> CreatePublicationReconciliationAsync(
        PreparedTicketPublicationReconciliationComparison comparison,
        DateTimeOffset? now = null,
        CancellationToken ct = default,
        Func<SqliteConnection, CancellationToken, Task>? validateFrozenObservation = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.ContractVersion !=
            PreparedTicketPublicationReconciliationContract.CurrentVersion)
        {
            throw new ArgumentException(
                $"Unsupported reconciliation contract version '{comparison.ContractVersion}'.",
                nameof(comparison));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(comparison.SourceRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparison.SourceSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparison.SourceSnapshotSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparison.StableJiraGeneration);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparison.CorpusFingerprint);
        if (comparison.Items.Count == 0)
        {
            throw new ArgumentException(
                "A publication reconciliation requires at least one item.",
                nameof(comparison));
        }
        string[] duplicateKeys = comparison.Items
            .GroupBy(item => item.TicketKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateKeys.Length != 0)
        {
            throw new ArgumentException(
                $"The reconciliation comparison contains duplicate ticket keys: {string.Join(", ", duplicateKeys)}.",
                nameof(comparison));
        }
        foreach (PreparedTicketPublicationReconciliationItemDecision item in
                 comparison.Items)
        {
            PreparedTicketPublicationReconciliationDispositionValues
                .EnsureValid(item.Disposition);
            if (string.IsNullOrWhiteSpace(item.ItemKind) ||
                string.IsNullOrWhiteSpace(item.ExpectedSourceRevision))
            {
                throw new ArgumentException(
                    $"Reconciliation item '{item.TicketKey}' is missing its publication corpus coordinate.",
                    nameof(comparison));
            }
            string selectedSourceRevision =
                item.Disposition ==
                    PreparedTicketPublicationReconciliationDispositionValues
                        .CarryForward
                    ? item.BaselineSourceRevision
                    : item.CurrentSourceRevision;
            if (!string.Equals(
                    item.ExpectedSourceRevision,
                    selectedSourceRevision,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Reconciliation item '{item.TicketKey}' does not retain its selected source revision.",
                    nameof(comparison));
            }
        }

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            AuthoringRunStore store = new(this);
            string runId = Guid.NewGuid().ToString("N");
            AuthoringRunRecord run = await store.CreateMixedRunAsync(
                connection,
                transaction,
                AuthoringProcessorKind,
                PreparedTicketPublicationReconciliationContract.Purpose,
                comparison.SourceRunId,
                comparison.Items.Select(item => new AuthoringMixedRunItem(
                    item.TicketKey,
                    item.Disposition ==
                        PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward
                        ? $"reconciliation:{runId}:{item.ItemKind}"
                        : item.ItemKind!,
                    item.Disposition ==
                        PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward
                        ? item.CurrentSourceRevision
                        : item.ExpectedSourceRevision!,
                    item.Disposition ==
                        PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward
                        ? AuthoringStatusValues.Items.Complete
                        : AuthoringStatusValues.Items.Pending,
                    item.Disposition ==
                        PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward
                        ? item.BaselineReceiptId
                        : null)).ToArray(),
                runId: runId,
                now: timestamp,
                requestJson: JsonSerializer.Serialize(comparison),
                ct: ct);
            if (validateFrozenObservation is not null)
            {
                await validateFrozenObservation(connection, ct);
            }

            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO prepared_ticket_publication_reconciliations(
                    RunId, SourceRunId, SourceSnapshotId, SourceSnapshotSha256,
                    StableJiraGeneration, CorpusFingerprint, ComparisonJson,
                    PromotionState, CapturedAt)
                VALUES(
                    @runId, @sourceRunId, @sourceSnapshotId, @sourceSnapshotSha256,
                    @stableJiraGeneration, @corpusFingerprint, @comparisonJson,
                    @promotionState, @capturedAt)
                """,
                ct,
                ("@runId", run.Id),
                ("@sourceRunId", comparison.SourceRunId),
                ("@sourceSnapshotId", comparison.SourceSnapshotId),
                ("@sourceSnapshotSha256", comparison.SourceSnapshotSha256),
                ("@stableJiraGeneration", comparison.StableJiraGeneration),
                ("@corpusFingerprint", comparison.CorpusFingerprint),
                ("@comparisonJson", JsonSerializer.Serialize(comparison)),
                ("@promotionState",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged),
                ("@capturedAt", Format(comparison.CapturedAt)));
            foreach (PreparedTicketPublicationReconciliationItemDecision item in
                     comparison.Items)
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO prepared_ticket_publication_reconciliation_items(
                        RunId, TicketKey, Disposition, BaselineSourceRevision,
                        CurrentSourceRevision, BaselineReceiptId,
                        BaselineRunItemId, BaselineContributingRunId,
                        ItemKind, ExpectedSourceRevision,
                        BaselineAuthoredFingerprint, BaselineGroupingFingerprint)
                    VALUES(
                        @runId, @ticketKey, @disposition, @baselineRevision,
                        @currentRevision, @receiptId, @runItemId,
                        @contributingRunId, @itemKind,
                        @expectedSourceRevision, @authoredFingerprint,
                        @groupingFingerprint)
                    """,
                    ct,
                    ("@runId", run.Id),
                    ("@ticketKey", item.TicketKey),
                    ("@disposition", item.Disposition),
                    ("@baselineRevision", item.BaselineSourceRevision),
                    ("@currentRevision", item.CurrentSourceRevision),
                    ("@receiptId", item.BaselineReceiptId),
                    ("@runItemId", item.BaselineRunItemId),
                    ("@contributingRunId", item.BaselineContributingRunId),
                    ("@itemKind", item.ItemKind),
                    ("@expectedSourceRevision",
                        item.ExpectedSourceRevision),
                    ("@authoredFingerprint", item.BaselineAuthoredFingerprint),
                    ("@groupingFingerprint", item.BaselineGroupingFingerprint));
            }
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO prepared_ticket_publication_reconciliation_journal(
                    RunId, State, UpdatedAt)
                VALUES(@runId, @state, @updatedAt)
                """,
                ct,
                ("@runId", run.Id),
                ("@state",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged),
                ("@updatedAt", Format(timestamp)));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO prepared_ticket_publication_reconciliation_fences(
                    RunId, LeaseId, AcquiredAt)
                VALUES(@runId, @leaseId, @acquiredAt)
                """,
                ct,
                ("@runId", run.Id),
                ("@leaseId", Guid.NewGuid().ToString("N")),
                ("@acquiredAt", Format(timestamp)));
            await transaction.CommitAsync(ct);
            return run;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void EnsurePublicationCompositeIndexes(
        SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_publication_refresh_receipts_run_stage
            ON prepared_ticket_publication_refresh_receipts(RunId, StageId);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_partition_certifications_run_partition
            ON prepared_ticket_partition_certifications(RunId, PartitionKey);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureCanonicalCompositeIndexes(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_artifacts_ticket_value
            ON prepared_ticket_artifacts(TicketKey, Value);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_pages_ticket_value
            ON prepared_ticket_pages(TicketKey, Value);
            """;
        command.ExecuteNonQuery();
    }

    protected override void InitializeSchema(SqliteConnection connection)
        => EnsureSchema(connection, Logger);

    /// <summary>
    /// CsLightDbGen does not currently expose a way to declare a composite UNIQUE index
    /// (the <c>[LdgSQLiteIndex]</c> attribute has no Unique property), so the per-ticket
    /// uniqueness contract for each hydration table is enforced via follow-on
    /// <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> statements, mirroring the
    /// <see cref="JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex"/> pattern.
    /// </summary>
    private static void EnsureHydrationCompositeUniqueIndexes(SqliteConnection connection)
    {
        string[] statements =
        [
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_jira_hydration_ticket_jira ON prepared_jira_hydration(TicketKey, JiraKey);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_zulip_hydration_ticket_thread ON prepared_zulip_hydration(TicketKey, ZulipThreadId);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_github_hydration_ticket_item ON prepared_github_hydration(TicketKey, GitHubItemId);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_repo_hydration_ticket_repo ON prepared_repo_hydration(TicketKey, Repo);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_jira_xref_ticket_jira_source ON prepared_ticket_jira_xref(TicketKey, JiraKey, Source);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_in_person_requesters_ticket_name ON prepared_ticket_in_person_requesters(TicketKey COLLATE NOCASE, DisplayName COLLATE NOCASE);",
        ];
        foreach (string sql in statements)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static void SeedLegacyRunInputProvenance(
        SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO authoring_run_input_provenance(
                RunId, Source, LatestSuccessfulRefreshAt,
                ContentRevision, CapturedAt)
            SELECT run.Id, 'jira', NULL, NULL, run.CreatedAt
            FROM authoring_runs run
            WHERE run.ProcessorKind = @processorKind COLLATE NOCASE
              AND NOT EXISTS (
                  SELECT 1
                  FROM authoring_run_input_provenance provenance
                  WHERE provenance.RunId = run.Id
                    AND provenance.Source = 'jira' COLLATE NOCASE
              )
            """;
        command.Parameters.AddWithValue(
            "@processorKind",
            AuthoringProcessorKind);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Per-partition composite-unique indexes for the grouping tables. As with
    /// <see cref="EnsureHydrationCompositeUniqueIndexes"/>, CsLightDbGen's
    /// <c>[LdgSQLiteIndex]</c> attribute does not support <c>Unique</c>, so the
    /// uniqueness contract is enforced here via follow-on
    /// <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> statements. The
    /// "each ticket appears in at most one Topic within a partition" invariant
    /// cannot be a single SQLite UNIQUE (the partition triple lives on
    /// <c>prepared_ticket_topics</c>, members live on <c>prepared_ticket_topic_members</c>);
    /// it is enforced in C# inside <c>SaveGroupingAsync</c> + the payload validator.
    /// </summary>
    private static void EnsureGroupingCompositeUniqueIndexes(SqliteConnection connection)
    {
        string[] statements =
        [
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_topics_partition_short ON prepared_ticket_topics(WorkGroupClean, Specification, Type, ShortDescription);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_topic_groups_topic_first ON prepared_ticket_topic_groups(TopicRowId, FirstTicketKey);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_topic_members_topic_ticket ON prepared_ticket_topic_members(TopicRowId, TicketKey);",
        ];
        foreach (string sql in statements)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Ensures the <c>schema_migrations</c> sentinel table exists. Sentinel
    /// rows are used by <see cref="RunMigrationsIfNeeded"/> to make
    /// one-shot data migrations (e.g. the stored-WorkGroupClean backfill)
    /// idempotent across restarts.
    /// </summary>
    private static void EnsureMigrationsTable(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations(
                Name TEXT PRIMARY KEY,
                AppliedAt TEXT NOT NULL)
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the stored <c>WorkGroupClean</c> column to
    /// <c>prepared_jira_hydration</c> on legacy DBs that predate this
    /// column (CsLightDbGen emits it for fresh DBs). Pairs with the
    /// generator-emitted <c>IDX_prepared_jira_hydration_WorkGroupClean</c>
    /// non-unique index so SELECTs filtered by the slug stay fast.
    /// </summary>
    private static void EnsureHydrationWorkGroupCleanColumn(SqliteConnection connection)
    {
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "prepared_jira_hydration",
            "WorkGroupClean",
            "TEXT NULL");
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "CREATE INDEX IF NOT EXISTS IDX_prepared_jira_hydration_WorkGroupClean " +
            "ON prepared_jira_hydration(WorkGroupClean)";
        command.ExecuteNonQuery();
    }

    private const string HydrationCleanMigrationName = "prepared-jira-hydration-clean-v1";
    private const string TicketTopicsCleanMigrationName = "ticket-topics-clean-v1";
    private const string StructuredPeopleMigrationName =
        "prepared-hydration-structured-people-v2";

    /// <summary>
    /// Runs every one-shot data migration whose sentinel is not yet present.
    /// Each migration is wrapped in a single <c>BEGIN IMMEDIATE</c> /
    /// <c>COMMIT</c> transaction so partial work cannot leak; the sentinel
    /// is only written after the transaction commits.
    /// </summary>
    private static void RunMigrationsIfNeeded(SqliteConnection connection, ILogger? logger)
    {
        if (!MigrationHasRun(connection, StructuredPeopleMigrationName))
        {
            InvalidateUnverifiedLegacyHydration(connection, logger);
            MarkMigrationApplied(connection, StructuredPeopleMigrationName);
        }

        if (!MigrationHasRun(connection, HydrationCleanMigrationName))
        {
            BackfillJiraHydrationWorkGroupClean(connection, logger);
            MarkMigrationApplied(connection, HydrationCleanMigrationName);
        }

        if (!MigrationHasRun(connection, TicketTopicsCleanMigrationName))
        {
            BackfillTicketTopicsWorkGroupClean(connection, logger);
            MarkMigrationApplied(connection, TicketTopicsCleanMigrationName);
        }
    }

    private static void InvalidateUnverifiedLegacyHydration(
        SqliteConnection connection,
        ILogger? logger)
    {
        using SqliteTransaction transaction =
            connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        int parentRows;
        int jiraRows;
        using (SqliteCommand parent = connection.CreateCommand())
        {
            parent.Transaction = transaction;
            parent.CommandText =
                """
                UPDATE prepared_ticket_hydration
                SET Reporter = NULL,
                    HydrationStatus = 'unresolved',
                    HydrationReason = 'structured people and provenance refresh required'
                """;
            parentRows = parent.ExecuteNonQuery();
        }
        using (SqliteCommand jira = connection.CreateCommand())
        {
            jira.Transaction = transaction;
            jira.CommandText =
                """
                UPDATE prepared_jira_hydration
                SET Reporter = NULL
                WHERE Reporter IS NOT NULL
                """;
            jiraRows = jira.ExecuteNonQuery();
        }
        transaction.Commit();
        logger?.LogInformation(
            "Invalidated unverified legacy hydration: parentRows={ParentRows} jiraReporterRows={JiraRows}",
            parentRows,
            jiraRows);
    }

    private static bool MigrationHasRun(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM schema_migrations WHERE Name = @name";
        command.Parameters.AddWithValue("@name", name);
        return command.ExecuteScalar() is not null;
    }

    private static void MarkMigrationApplied(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR IGNORE INTO schema_migrations(Name, AppliedAt) VALUES (@name, @appliedAt)";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue(
            "@appliedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Re-derives <c>WorkGroupClean</c> from <c>WorkGroup</c> via
    /// <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/> for every
    /// row whose stored slug is stale or absent. Runs as a single
    /// transaction; on the hydration table (bounded by hydrated-ticket
    /// count, on the order of 10⁴–10⁵ rows) a single transaction is
    /// acceptable and keeps the migration atomic.
    /// </summary>
    private static void BackfillJiraHydrationWorkGroupClean(SqliteConnection connection, ILogger? logger)
    {
        List<(int RowId, string? WorkGroup, string? Existing)> rows = [];
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText = "SELECT RowId, WorkGroup, WorkGroupClean FROM prepared_jira_hydration";
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read())
            {
                int rowId = reader.GetInt32(0);
                string? wg = reader.IsDBNull(1) ? null : reader.GetString(1);
                string? existing = reader.IsDBNull(2) ? null : reader.GetString(2);
                rows.Add((rowId, wg, existing));
            }
        }

        if (rows.Count == 0)
        {
            logger?.LogInformation(
                "Backfilled WorkGroupClean on prepared_jira_hydration: rowsScanned=0 rowsUpdated=0");
            return;
        }

        using SqliteTransaction tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        int updated = 0;
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE prepared_jira_hydration SET WorkGroupClean = @clean WHERE RowId = @rowId";
            SqliteParameter cleanParam = update.Parameters.Add("@clean", SqliteType.Text);
            SqliteParameter rowIdParam = update.Parameters.Add("@rowId", SqliteType.Integer);
            foreach ((int rowId, string? wg, string? existing) in rows)
            {
                string newCleanRaw = Hl7WorkGroupNameCleaner.Clean(wg);
                string? newClean = string.IsNullOrEmpty(newCleanRaw) ? null : newCleanRaw;
                if (string.Equals(newClean, existing, StringComparison.Ordinal)) continue;
                cleanParam.Value = (object?)newClean ?? DBNull.Value;
                rowIdParam.Value = rowId;
                update.ExecuteNonQuery();
                updated++;
            }
        }
        tx.Commit();
        logger?.LogInformation(
            "Backfilled WorkGroupClean on prepared_jira_hydration: rowsScanned={Scanned} rowsUpdated={Updated}",
            rows.Count, updated);
    }

    /// <summary>
    /// Re-derives <c>WorkGroupClean</c> for every row of
    /// <c>prepared_ticket_topics</c> from <c>WorkGroupDisplay</c> via
    /// <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/>. Aborts with
    /// <see cref="WorkGroupCleanReslugAbortedException"/> (and leaves the
    /// sentinel un-written) if the reslug would violate the
    /// <c>idx_prepared_ticket_topics_partition_short</c> UNIQUE index by
    /// collapsing two distinct rows onto a single
    /// <c>(newClean, Specification, Type, ShortDescription)</c> tuple.
    /// </summary>
    private static void BackfillTicketTopicsWorkGroupClean(SqliteConnection connection, ILogger? logger)
    {
        List<(int RowId, string WorkGroupDisplay, string ExistingClean, string Specification, string Type, string ShortDescription)> rows = [];
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText =
                "SELECT RowId, WorkGroupDisplay, WorkGroupClean, Specification, Type, ShortDescription " +
                "FROM prepared_ticket_topics";
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }

        if (rows.Count == 0)
        {
            logger?.LogInformation(
                "Reslugged WorkGroupClean on prepared_ticket_topics: rowsScanned=0 rowsUpdated=0 collisionsAborted=0");
            return;
        }

        // Pre-flight collision check. The destination uniqueness key is
        // (WorkGroupClean, Specification, Type, ShortDescription). If two
        // rows with different RowIds would map to the same destination
        // tuple after the reslug — or a row would land on top of an
        // unchanged row that already occupies the destination — abort
        // without writing.
        Dictionary<(string Clean, string Spec, string Type, string Short), int> targets = new(64);
        Dictionary<(string Clean, string Spec, string Type, string Short), int> unchanged = new(64);
        List<(int RowId, string? NewClean)> plan = [];
        foreach (var row in rows)
        {
            string newCleanRaw = Hl7WorkGroupNameCleaner.Clean(row.WorkGroupDisplay);
            string newClean = string.IsNullOrEmpty(newCleanRaw) ? row.ExistingClean : newCleanRaw;
            (string Clean, string Spec, string Type, string Short) key = (newClean, row.Specification, row.Type, row.ShortDescription);
            if (string.Equals(newClean, row.ExistingClean, StringComparison.Ordinal))
            {
                unchanged[key] = row.RowId;
            }
            else if (targets.TryGetValue(key, out int otherRowId))
            {
                logger?.LogError(
                    "Reslug collision: RowIds {A} and {B} would both map to ({Clean}, {Spec}, {Type}, {Short})",
                    otherRowId, row.RowId, key.Clean, key.Spec, key.Type, key.Short);
                throw new WorkGroupCleanReslugAbortedException(
                    $"Reslug collision on prepared_ticket_topics: RowIds {otherRowId} and {row.RowId} would both map to ({key.Clean}, {key.Spec}, {key.Type}, {key.Short})");
            }
            else
            {
                targets[key] = row.RowId;
                plan.Add((row.RowId, newClean));
            }
        }
        foreach (var kvp in targets)
        {
            if (unchanged.TryGetValue(kvp.Key, out int unchangedRowId) && unchangedRowId != kvp.Value)
            {
                logger?.LogError(
                    "Reslug collision: RowId {A} would land on RowId {B} at ({Clean}, {Spec}, {Type}, {Short})",
                    kvp.Value, unchangedRowId, kvp.Key.Clean, kvp.Key.Spec, kvp.Key.Type, kvp.Key.Short);
                throw new WorkGroupCleanReslugAbortedException(
                    $"Reslug collision on prepared_ticket_topics: RowId {kvp.Value} would land on existing RowId {unchangedRowId} at ({kvp.Key.Clean}, {kvp.Key.Spec}, {kvp.Key.Type}, {kvp.Key.Short})");
            }
        }

        using SqliteTransaction tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        int updated = 0;
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE prepared_ticket_topics SET WorkGroupClean = @clean WHERE RowId = @rowId";
            SqliteParameter cleanParam = update.Parameters.Add("@clean", SqliteType.Text);
            SqliteParameter rowIdParam = update.Parameters.Add("@rowId", SqliteType.Integer);
            foreach ((int rowId, _) in plan)
            {
                cleanParam.Value = $"__reslug_{rowId}_{Guid.NewGuid():N}";
                rowIdParam.Value = rowId;
                update.ExecuteNonQuery();
            }

            foreach ((int rowId, string? newClean) in plan)
            {
                cleanParam.Value = (object?)newClean ?? DBNull.Value;
                rowIdParam.Value = rowId;
                update.ExecuteNonQuery();
                updated++;
            }
        }

        // Final integrity check before committing.
        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.Transaction = tx;
            integrity.CommandText = "PRAGMA integrity_check";
            object? scalar = integrity.ExecuteScalar();
            string result = scalar?.ToString() ?? string.Empty;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkGroupCleanReslugAbortedException(
                    $"PRAGMA integrity_check failed mid-migration: {result}");
            }
        }

        tx.Commit();
        logger?.LogInformation(
            "Reslugged WorkGroupClean on prepared_ticket_topics: rowsScanned={Scanned} rowsUpdated={Updated} collisionsAborted=0",
            rows.Count, updated);
    }

    public async Task<PreparedTicketSaveResult> SavePreparedTicketAsync(PreparedTicketPayload payload, CancellationToken ct = default)
    {
        PreparedTicketPayloadValidator.ThrowIfInvalid(payload);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync(ct);
        try
        {
            await EnsureLegacyAuthoringModeAsync(connection, ct);
            await EnsureGeneralMutationAllowedAsync(connection, ct);
            await SavePreparedTicketCoreAsync(connection, payload, savedAt, ct);
            string graphHash = await ComputePreparedGraphHashAsync(connection, payload.Key, ct);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_authoring_state(
                    TicketKey, Classification, GraphHash, ReceiptContentHash,
                    RunId, RunItemId, OperationId, UpdatedAt)
                VALUES(
                    @ticketKey, 'legacy-unverified', @graphHash, NULL,
                    NULL, NULL, NULL, @updatedAt)
                ON CONFLICT(TicketKey) DO UPDATE SET
                    Classification = excluded.Classification,
                    GraphHash = excluded.GraphHash,
                    ReceiptContentHash = NULL,
                    RunId = NULL,
                    RunItemId = NULL,
                    OperationId = NULL,
                    UpdatedAt = excluded.UpdatedAt
                """,
                ct,
                ("@ticketKey", payload.Key),
                ("@graphHash", graphHash),
                ("@updatedAt", Format(savedAt)));

            await ExecuteRawAsync(connection, "COMMIT", ct);
            return new PreparedTicketSaveResult(payload.Key, 1, payload.Repos.Count, payload.RelatedJiraTickets.Count, payload.RelatedZulipThreads.Count, payload.RelatedGitHubItems.Count);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task SavePreparedTicketForAuthoringAsync(
        SqliteConnection connection,
        PreparedTicketPayload payload,
        string graphHash,
        string runId,
        string runItemId,
        string operationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        PreparedTicketPayloadValidator.ThrowIfInvalid(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphHash);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
        await CapturePreChangePartitionAsync(
            connection,
            payload.Key,
            runItemId,
            savedAt,
            ct);
        await SavePreparedTicketCoreAsync(connection, payload, savedAt, ct);
        string currentGraphHash = await ComputePreparedGraphHashAsync(
            connection,
            payload.Key,
            ct);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_authoring_state(
                TicketKey, Classification, GraphHash, ReceiptContentHash,
                RunId, RunItemId, OperationId, UpdatedAt)
            VALUES(
                @ticketKey, 'receipt-backed', @graphHash, @receiptContentHash,
                @runId, @runItemId, @operationId, @updatedAt)
            ON CONFLICT(TicketKey) DO UPDATE SET
                Classification = excluded.Classification,
                GraphHash = excluded.GraphHash,
                ReceiptContentHash = excluded.ReceiptContentHash,
                RunId = excluded.RunId,
                RunItemId = excluded.RunItemId,
                OperationId = excluded.OperationId,
                UpdatedAt = excluded.UpdatedAt
            """,
            ct,
            ("@ticketKey", payload.Key),
            ("@graphHash", currentGraphHash),
            ("@receiptContentHash", graphHash),
            ("@runId", runId),
            ("@runItemId", runItemId),
            ("@operationId", operationId),
            ("@updatedAt", Format(savedAt)));
    }

    private static async Task CapturePreChangePartitionAsync(
        SqliteConnection connection,
        string ticketKey,
        string runItemId,
        DateTimeOffset capturedAt,
        CancellationToken ct)
    {
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT
                j.WorkGroupClean,
                j.WorkGroup,
                IFNULL(j.Specification, 'Unspecified'),
                IFNULL(j.Type, '')
            FROM prepared_jira_hydration j
            WHERE j.TicketKey = @ticketKey AND j.JiraKey = @ticketKey
            LIMIT 1
            """;
        select.Parameters.AddWithValue("@ticketKey", ticketKey);
        string? workGroupClean = null;
        string? workGroupDisplay = null;
        string? specification = null;
        string? type = null;
        await using (SqliteDataReader reader = await select.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                workGroupClean = ReadNullableString(reader, 0);
                workGroupDisplay = ReadNullableString(reader, 1);
                specification = ReadNullableString(reader, 2);
                type = ReadNullableString(reader, 3);
            }
        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_run_item_partitions(
                RunItemId, TicketKey, WorkGroupClean, WorkGroupDisplay, Specification, Type, CapturedAt)
            VALUES(@runItemId, @ticketKey, @workGroupClean, @workGroupDisplay, @specification, @type, @capturedAt)
            ON CONFLICT(RunItemId) DO UPDATE SET
                TicketKey = excluded.TicketKey,
                WorkGroupClean = excluded.WorkGroupClean,
                WorkGroupDisplay = excluded.WorkGroupDisplay,
                Specification = excluded.Specification,
                Type = excluded.Type,
                CapturedAt = excluded.CapturedAt
            """,
            ct,
            ("@runItemId", runItemId),
            ("@ticketKey", ticketKey),
            ("@workGroupClean", workGroupClean),
            ("@workGroupDisplay", workGroupDisplay),
            ("@specification", specification),
            ("@type", type),
            ("@capturedAt", Format(capturedAt)));
    }

    public async Task<int> ClassifyLegacyPreparedTicketsAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            int count = await ClassifyLegacyPreparedTicketsCoreAsync(
                connection,
                ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return count;
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<AuthoringCutoverPreparation> PrepareCutoverAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await ClassifyLegacyPreparedTicketsCoreAsync(connection, ct);
        List<AuthoringRunItemDefinition> items = [];
        List<(DateTimeOffset? LastSuccessfulRefreshAt, long? ContentRevision)>
            provenanceCoordinates = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.Key, s.SourceTicketShape, s.LastUpdated, s.Title,
                   s.Status, s.WorkGroup, s.Type, s.Specification,
                   s.SourceProjectLastSuccessfulRefreshAt,
                   s.SourceContentRevision
            FROM prepared_tickets p
            INNER JOIN prepared_ticket_authoring_state a
                ON a.TicketKey = p.Key
            LEFT JOIN jira_processing_source_tickets s
                ON s.Key = p.Key COLLATE NOCASE
               AND s.SourceTicketShape = 'fhir' COLLATE NOCASE
            WHERE a.Classification = 'legacy-unverified'
            ORDER BY p.Key
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1))
            {
                throw new InvalidOperationException(
                    $"Prepared ticket '{reader.GetString(0)}' has no current Jira source row for revalidation.");
            }
            string key = reader.GetString(0);
            string revision = reader.IsDBNull(2)
                ? AuthoringResultHasher.HashNormalizedUtf8(
                    string.Join(
                        "\n",
                        key,
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7)))
                : ParseDate(reader.GetString(2)).ToString(
                    "O",
                    CultureInfo.InvariantCulture);
            items.Add(new AuthoringRunItemDefinition(
                key,
                reader.GetString(1),
                revision));
            provenanceCoordinates.Add((
                reader.IsDBNull(8)
                    ? null
                    : ParseDate(reader.GetString(8)),
                reader.IsDBNull(9)
                    ? null
                    : reader.GetInt64(9)));
        }

        DateTimeOffset? latestSuccessfulRefreshAt =
            provenanceCoordinates.Count > 0 &&
            provenanceCoordinates.All(coordinate =>
                coordinate.LastSuccessfulRefreshAt is not null)
                ? provenanceCoordinates.Max(coordinate =>
                    coordinate.LastSuccessfulRefreshAt!.Value)
                : null;
        long? contentRevision = null;
        if (provenanceCoordinates.Count > 0 &&
            provenanceCoordinates.All(coordinate =>
                coordinate.ContentRevision is not null))
        {
            long candidate = provenanceCoordinates[0].ContentRevision!.Value;
            if (provenanceCoordinates.All(coordinate =>
                    coordinate.ContentRevision == candidate))
            {
                contentRevision = candidate;
            }
        }

        return new AuthoringCutoverPreparation(
            items,
            items.Count == 0
                ? null
                :
                [
                    new AuthoringRunInputProvenanceDefinition(
                        "jira",
                        latestSuccessfulRefreshAt,
                        contentRevision),
                ]);
    }

    private async Task<int> ClassifyLegacyPreparedTicketsCoreAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<string> keys = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT p.Key
                FROM prepared_tickets p
                ORDER BY p.Key
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                keys.Add(reader.GetString(0));
            }
        }

        int classified = 0;
        foreach (string key in keys)
        {
            string graphHash = await ComputePreparedGraphHashAsync(
                connection,
                key,
                ct);
            if (await IsCurrentPreparedReceiptBackedAsync(
                    connection,
                    key,
                    graphHash,
                    ct))
            {
                continue;
            }
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_authoring_state(
                    TicketKey, Classification, GraphHash, ReceiptContentHash,
                    UpdatedAt)
                VALUES(
                    @ticketKey, 'legacy-unverified', @graphHash, NULL,
                    @updatedAt)
                ON CONFLICT(TicketKey) DO UPDATE SET
                    Classification = excluded.Classification,
                    GraphHash = excluded.GraphHash,
                    ReceiptContentHash = NULL,
                    RunId = NULL,
                    RunItemId = NULL,
                    OperationId = NULL,
                    UpdatedAt = excluded.UpdatedAt
                """,
                ct,
                ("@ticketKey", key),
                ("@graphHash", graphHash),
                ("@updatedAt", Format(DateTimeOffset.UtcNow)));
            classified++;
        }
        return classified;
    }

    private static async Task<bool> IsCurrentPreparedReceiptBackedAsync(
        SqliteConnection connection,
        string ticketKey,
        string graphHash,
        CancellationToken ct)
        => await ReadCurrentPreparedReceiptCoordinatesAsync(
            connection,
            ticketKey,
            graphHash,
            ct) is not null;

    public static async Task CreateCurrentSnapshotReceiptBackedTicketsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await ExecuteAsync(
            connection,
            $"""
            DROP TABLE IF EXISTS temp.{CurrentSnapshotReceiptBackedTicketsTable};
            CREATE TEMP TABLE {CurrentSnapshotReceiptBackedTicketsTable}(
                TicketKey TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                ReceiptId TEXT NOT NULL,
                RunItemId TEXT NOT NULL,
                RunId TEXT NOT NULL
            );
            """,
            ct);

        List<string> keys = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Key FROM prepared_tickets ORDER BY Key";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                keys.Add(reader.GetString(0));
            }
        }

        foreach (string key in keys)
        {
            string graphHash = await ComputePreparedGraphHashAsync(
                connection,
                key,
                ct);
            (string ReceiptId, string RunItemId, string RunId)? coordinates =
                await ReadCurrentPreparedReceiptCoordinatesAsync(
                    connection,
                    key,
                    graphHash,
                    ct);
            if (coordinates is null)
            {
                continue;
            }
            await ExecuteAsync(
                connection,
                $"""
                INSERT INTO {CurrentSnapshotReceiptBackedTicketsTable}(
                    TicketKey, ReceiptId, RunItemId, RunId)
                VALUES(@ticketKey, @receiptId, @runItemId, @runId)
                """,
                ct,
                ("@ticketKey", key),
                ("@receiptId", coordinates.Value.ReceiptId),
                ("@runItemId", coordinates.Value.RunItemId),
                ("@runId", coordinates.Value.RunId));
        }

    }

    public async Task<PreparedTicketPublicationRefreshInventory>
        GetPublicationRefreshInventoryAsync(
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ReadPublicationRefreshInventoryAsync(connection, ct);
    }

    public static Task<PreparedTicketPublicationRefreshInventory>
        GetPublicationRefreshInventoryAsync(
            SqliteConnection connection,
            CancellationToken ct = default)
        => ReadPublicationRefreshInventoryAsync(connection, ct);

    public async Task<IReadOnlyList<PreparedTicketPublicationRefreshCandidate>>
        ListPublicationRefreshCandidatesAsync(
            CancellationToken ct = default)
        => (await GetPublicationRefreshInventoryAsync(ct)).Candidates;

    public static async Task<IReadOnlyList<AuthoringMaintenanceRunItem>>
        GetPublicationRefreshMaintenanceItemsAsync(
            SqliteConnection connection,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationRefreshInventory inventory =
            await ReadPublicationRefreshInventoryAsync(connection, ct);
        return inventory.Candidates
            .Select(candidate => new AuthoringMaintenanceRunItem(
                candidate.TicketKey,
                candidate.ItemKind,
                candidate.ExpectedSourceRevision,
                candidate.ReceiptId))
            .ToArray();
    }

    public static async Task<PreparedTicketPublicationRefreshInventory>
        ReadPublicationRefreshInventoryAsync(
            SqliteConnection connection,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await CreateCurrentSnapshotReceiptBackedTicketsAsync(connection, ct);
        List<PreparedTicketPublicationRefreshCandidate> candidates = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT current.TicketKey, current.ReceiptId,
                   current.RunItemId, current.RunId,
                   item.ItemKind, item.ExpectedSourceRevision
            FROM {CurrentSnapshotReceiptBackedTicketsTable} current
            INNER JOIN authoring_run_items item
                ON item.Id = current.RunItemId
               AND item.RunId = current.RunId
               AND item.BusinessKey = current.TicketKey COLLATE NOCASE
               AND item.AcceptedReceiptId = current.ReceiptId
            ORDER BY current.TicketKey COLLATE NOCASE,
                     current.TicketKey, current.ReceiptId,
                     current.RunItemId, current.RunId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candidates.Add(
                new PreparedTicketPublicationRefreshCandidate(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
        }

        string corpusFingerprint =
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                candidates.Select(candidate =>
                    candidate.ToPublicationCorpusItem()));
        return new PreparedTicketPublicationRefreshInventory(
            candidates.AsReadOnly(),
            corpusFingerprint);
    }

    public Task<PreparedTicketPublicationRefreshReceiptRecord>
        ApplyPublicationMetadataAsync(
            string runId,
            AuthoringRunStageLease lease,
            string inputFingerprint,
            PreparedTicketPublicationRefreshInventory inventory,
            IReadOnlyList<PreparedTicketPublicationMetadata> metadata,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(inventory);
        return ApplyPublicationMetadataAsync(
            runId,
            lease.StageId,
            lease.LeaseId,
            inputFingerprint,
            inventory.CorpusFingerprint,
            inventory.Candidates,
            metadata,
            ct);
    }

    public static PreparedTicketPublicationEnrichmentInput? ReadPublicationEnrichmentInput(
        AuthoringRunRecord run)
        => ReadPublicationEnrichmentInput(run.RequestJson, run.SourceRunId);

    private static PreparedTicketPublicationEnrichmentInput? ReadPublicationEnrichmentInput(
        string? requestJson,
        string? sourceRunId)
    {
        try
        {
            AuthoringMaintenanceRunRequest? request = AuthoringMaintenanceRunRequest.Parse(requestJson);
            if (request is null)
            {
                return null;
            }
            request.EnsureRecipe(
                PreparedTicketPublicationEnrichmentContract.RecipeName,
                PreparedTicketPublicationEnrichmentContract.CurrentVersion);
            PreparedTicketPublicationEnrichmentInput input =
                PreparedTicketPublicationEnrichmentContract.ParseInput(request.RecipeInputJson);
            if (input.Source.RunId != sourceRunId ||
                request.CorpusComparison != new AuthoringRunCorpusComparison(
                    input.Source.SnapshotId, input.Source.ExportedTicketCount,
                    input.Corpus.Count, input.AdditionalTicketKeys.Count))
            {
                throw new ArgumentException("The stored recipe does not match its run and corpus comparison.");
            }
            return input;
        }
        catch (NotSupportedException)
        {
            throw new PreparedTicketPublicationProtectionException(
                PreparedTicketPublicationRefreshFailureCodes.UnsupportedRecipe,
                "The stored publication recipe or its version is unsupported.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new PreparedTicketPublicationProtectionException(
                PreparedTicketPublicationRefreshFailureCodes.InvalidRecipe,
                "The stored publication recipe is malformed or has inconsistent coordinates.");
        }
    }

    public async Task ValidatePublicationEnrichmentAsync(
        string runId,
        PreparedTicketPublicationEnrichmentInput input,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN", ct);
        try
        {
            await ValidatePublicationEnrichmentAsync(connection, runId, input, ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public static async Task ValidatePublicationEnrichmentAsync(
        SqliteConnection connection,
        string runId,
        PreparedTicketPublicationEnrichmentInput input,
        CancellationToken ct = default)
    {
        string sourceRunId = await ReadPublicationRefreshSourceRunIdAsync(connection, runId, ct);
        PreparedTicketPublicationEnrichmentInput stored;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT RequestJson FROM authoring_runs WHERE Id = @runId";
            command.Parameters.AddWithValue("@runId", runId);
            object? requestJson = await command.ExecuteScalarAsync(ct);
            stored = ReadPublicationEnrichmentInput(
                requestJson is null or DBNull ? null : (string)requestJson, sourceRunId)
                ?? throw new PreparedTicketPublicationProtectionException(
                    PreparedTicketPublicationRefreshFailureCodes.InvalidRecipe,
                    "The enrichment run no longer has its durable recipe.");
        }
        if (PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(stored) !=
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input))
        {
            throw new PreparedTicketPublicationProtectionException(
                PreparedTicketPublicationRefreshFailureCodes.InvalidRecipe,
                "The stored enrichment recipe changed after selection.");
        }

        Dictionary<string, PreparedTicketPublicationCorpusItem> candidates =
            stored.Corpus.ToDictionary(item => item.TicketKey, StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT BusinessKey, ItemKind, ExpectedSourceRevision, AcceptedReceiptId, Status
                FROM authoring_run_items WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", runId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string ticketKey = reader.GetString(0);
                if (!seen.Add(ticketKey) ||
                    !candidates.TryGetValue(ticketKey, out PreparedTicketPublicationCorpusItem? candidate) ||
                    reader.GetString(1) != $"maintenance:{runId}:{candidate.ItemKind}" ||
                    reader.GetString(2) != candidate.ExpectedSourceRevision ||
                    ReadNullableString(reader, 3) != candidate.ReceiptId ||
                    reader.GetString(4) != AuthoringStatusValues.Items.Complete)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageFingerprintMismatch,
                        "The publication maintenance items no longer retain the frozen receipt coordinates.");
                }
            }
        }
        if (seen.Count != candidates.Count)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "The publication maintenance items no longer cover the frozen corpus.");
        }
        PreparedTicketPublicationProtectionReader.ValidateFrozen(
            stored, await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(connection, ct), runId);
    }

    public async Task<PreparedTicketPublicationRefreshReceiptRecord?>
        GetCommittedPublicationEnrichmentReceiptAsync(
            string runId,
            AuthoringRunStageLease lease,
            string inputFingerprint,
            PreparedTicketPublicationEnrichmentInput input,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN", ct);
        try
        {
            await EnsurePublicationEnrichmentStageAsync(connection, runId, lease, inputFingerprint, input, ct);
            PreparedTicketPublicationRefreshReceiptRecord? receipt =
                await ReadPublicationRefreshReceiptAsync(connection, runId, lease.StageId, ct);
            if (receipt is not null)
            {
                EnsureMatchingPublicationRefreshReceipt(receipt, inputFingerprint, input.CorpusFingerprint);
            }
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return receipt;
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsurePublicationEnrichmentStageAsync(
        SqliteConnection connection,
        string runId,
        AuthoringRunStageLease lease,
        string inputFingerprint,
        PreparedTicketPublicationEnrichmentInput input,
        CancellationToken ct)
    {
        await EnsureStageLeaseAsync(
            connection, runId, lease.StageId, lease.LeaseId, inputFingerprint,
            PreparedTicketPublicationEnrichmentContract.StageName, string.Empty, ct);
        if (PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input) != inputFingerprint)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "The enrichment stage does not match its frozen recipe fingerprint.");
        }
        await ValidatePublicationEnrichmentAsync(connection, runId, input, ct);
    }

    public async Task<PreparedTicketPublicationRefreshReceiptRecord> ApplyPublicationEnrichmentAsync(
        string runId,
        AuthoringRunStageLease lease,
        string inputFingerprint,
        PreparedTicketPublicationEnrichmentInput input,
        PreparedTicketPublicationEnrichmentBatch batch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.JiraMetadata);
        ArgumentNullException.ThrowIfNull(batch.ZulipOutcomes);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsurePublicationEnrichmentStageAsync(connection, runId, lease, inputFingerprint, input, ct);
            PreparedTicketPublicationRefreshReceiptRecord? existing =
                await ReadPublicationRefreshReceiptAsync(connection, runId, lease.StageId, ct);
            if (existing is not null)
            {
                EnsureMatchingPublicationRefreshReceipt(existing, inputFingerprint, input.CorpusFingerprint);
                await ExecuteRawAsync(connection, "COMMIT", ct);
                return existing;
            }

            PreparedTicketPublicationMetadata[] metadata = NormalizeAndValidatePublicationMetadata(
                PreparedTicketPublicationRefreshInventory.FromEnrichmentInput(input).Candidates,
                batch.JiraMetadata);
            ValidatePublicationZulipOutcomes(input.ZulipReferences, batch.ZulipOutcomes);
            foreach (PreparedTicketPublicationMetadata row in metadata)
            {
                int parentRows = await ExecuteAsyncWithCount(
                    connection,
                    """
                    UPDATE prepared_ticket_hydration
                    SET Reporter = @reporter, Assignee = @assignee,
                        PublicDisplayNamePolicyVersion = @policy,
                        SourceProject = @project, SourceLastSuccessfulRefreshAt = @sourceRefresh,
                        SourceContentRevision = @revision, HydratedAt = @hydratedAt
                    WHERE TicketKey = @ticketKey COLLATE NOCASE
                    """,
                    ct,
                    ("@reporter", row.Reporter), ("@assignee", row.Assignee),
                    ("@policy", row.PublicDisplayNamePolicyVersion), ("@project", row.SourceProject),
                    ("@sourceRefresh", Format(row.SourceLastSuccessfulRefreshAt)),
                    ("@revision", row.SourceContentRevision), ("@hydratedAt", Format(row.HydratedAt)),
                    ("@ticketKey", row.TicketKey));
                int selfRows = await ExecuteAsyncWithCount(
                    connection,
                    """
                    UPDATE prepared_jira_hydration
                    SET Reporter = @reporter, Assignee = @assignee,
                        PublicDisplayNamePolicyVersion = @policy,
                        UpdatedAt = COALESCE(@updatedAt, UpdatedAt)
                    WHERE TicketKey = @ticketKey COLLATE NOCASE
                      AND JiraKey = TicketKey COLLATE NOCASE
                    """,
                    ct,
                    ("@reporter", row.Reporter), ("@assignee", row.Assignee),
                    ("@policy", row.PublicDisplayNamePolicyVersion),
                    ("@updatedAt", row.UpdatedAt is { } updatedAt ? Format(updatedAt.ToUniversalTime()) : null),
                    ("@ticketKey", row.TicketKey));
                if (parentRows != 1 || selfRows != 1)
                {
                    throw new PreparedTicketPublicationProtectionException(
                        PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph,
                        $"Prepared ticket '{row.TicketKey}' has no unique parent/self Jira hydration to enrich.");
                }

                await ExecuteAsync(
                    connection,
                    "DELETE FROM prepared_ticket_in_person_requesters WHERE TicketKey = @ticketKey COLLATE NOCASE",
                    ct, ("@ticketKey", row.TicketKey));
                foreach (string requester in row.InPersonRequesters)
                {
                    int inserted = await ExecuteAsyncWithCount(
                        connection,
                        """
                        INSERT INTO prepared_ticket_in_person_requesters(
                            TicketKey, DisplayName, PublicDisplayNamePolicyVersion)
                        VALUES(@ticketKey, @displayName, @policy)
                        """,
                        ct, ("@ticketKey", row.TicketKey), ("@displayName", requester),
                        ("@policy", row.PublicDisplayNamePolicyVersion));
                    if (inserted != 1)
                    {
                        throw new PreparedTicketPublicationProtectionException(
                            PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph,
                            "A publication requester write did not persist exactly one display row.");
                    }
                }
            }
            foreach (PreparedTicketPublicationZulipOutcome outcome in batch.ZulipOutcomes)
            {
                await ApplyPublicationZulipOutcomeAsync(connection, outcome.Hydration, ct);
            }

            await ValidatePublicationEnrichmentAsync(connection, runId, input, ct);
            PreparedTicketPublicationRefreshReceiptRecord receipt = await WritePublicationRefreshReceiptAsync(
                connection, runId, lease.StageId, inputFingerprint, input.CorpusFingerprint,
                metadata.Max(row => row.SourceLastSuccessfulRefreshAt),
                metadata[0].SourceContentRevision, DateTimeOffset.UtcNow, ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return receipt;
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static void ValidatePublicationZulipOutcomes(
        IReadOnlyList<PreparedTicketPublicationZulipReference> references,
        IReadOnlyList<PreparedTicketPublicationZulipOutcome> outcomes)
    {
        Dictionary<string, PreparedTicketPublicationZulipReference> accepted =
            references.ToDictionary(reference => reference.AssociationId, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (outcomes.Count != references.Count)
        {
            throw InvalidBatch("Zulip outcomes must cover every frozen accepted association exactly once.");
        }
        foreach (PreparedTicketPublicationZulipOutcome outcome in outcomes)
        {
            if (outcome is null || outcome.Hydration is null ||
                !seen.Add(outcome.AssociationId) ||
                !accepted.TryGetValue(outcome.AssociationId, out PreparedTicketPublicationZulipReference? reference) ||
                outcome.Hydration.TicketKey != reference.TicketKey ||
                outcome.Hydration.ZulipThreadId != reference.Reference)
            {
                throw InvalidBatch("Zulip outcomes contain missing, duplicate, or extra accepted coordinates.");
            }
            HydrationZulipRow row = outcome.Hydration;
            ZulipReferenceHydrationReasonReadResult reason = ZulipReferenceHydrationReason.Read(row.HydrationReason);
            if (reason.MetadataFailure is not null || reason.Metadata is not { } metadata)
            {
                throw InvalidBatch("A Zulip outcome has no valid typed lookup metadata.");
            }
            if (metadata.LatestOutcome == ZulipReferenceLookupOutcome.Resolved)
            {
                bool message = ZulipReferenceContract.TryGetMessageId(reference.Reference, out int messageId);
                if (row.HydrationStatus != "resolved" || metadata.Backing != ZulipReferenceBacking.TypedResolver ||
                    !ZulipReferenceContract.IsCoherentResolution(new()
                    {
                        Reference = reference.Reference, Outcome = metadata.LatestOutcome,
                        Kind = message ? ZulipReferenceKind.Message : ZulipReferenceKind.Thread,
                        MessageId = message ? messageId : null, StreamId = row.StreamId,
                        StreamName = row.StreamName, Topic = row.Topic, MessageCount = row.MessageCount,
                        FirstMessageAt = row.FirstMessageAt, LastMessageAt = row.LastMessageAt,
                        FirstMessageExcerpt = row.FirstMessageExcerpt, Url = row.Url,
                        Diagnostics = metadata.Diagnostics,
                    }, reference.Reference))
                {
                    throw InvalidBatch("A successful Zulip outcome has no coherent source-backed link.");
                }
            }
            else if (row.HydrationStatus != "unresolved" || metadata.Backing != ZulipReferenceBacking.None ||
                row.StreamId is not null || row.StreamName is not null || row.Topic is not null ||
                row.MessageCount is not null || row.FirstMessageAt is not null || row.LastMessageAt is not null ||
                row.FirstMessageExcerpt is not null || row.Url is not null)
            {
                throw InvalidBatch("A failed Zulip lookup cannot supply newly observed source context.");
            }
        }

        static PreparedTicketPublicationProtectionException InvalidBatch(string detail)
            => new(PreparedTicketPublicationRefreshFailureCodes.InvalidEnrichmentBatch, detail);
    }

    private static async Task ApplyPublicationZulipOutcomeAsync(
        SqliteConnection connection,
        HydrationZulipRow row,
        CancellationToken ct)
    {
        string? id = null;
        long? rowId = null;
        string? storedUrl = null;
        string? storedReason = null;
        bool legacyIndexedContext = false;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT RowId, Id, StreamName, Topic, MessageCount, Url, HydrationReason
                FROM prepared_zulip_hydration
                WHERE TicketKey = @ticketKey COLLATE NOCASE AND ZulipThreadId = @reference COLLATE BINARY
                """;
            command.Parameters.AddWithValue("@ticketKey", row.TicketKey);
            command.Parameters.AddWithValue("@reference", row.ZulipThreadId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                rowId = reader.GetInt64(0);
                id = reader.GetString(1);
                legacyIndexedContext = !string.IsNullOrWhiteSpace(ReadNullableString(reader, 2)) &&
                    !string.IsNullOrWhiteSpace(ReadNullableString(reader, 3)) &&
                    !reader.IsDBNull(4) && reader.GetInt64(4) > 0;
                storedUrl = ReadNullableString(reader, 5);
                storedReason = ReadNullableString(reader, 6);
                if (await reader.ReadAsync(ct))
                {
                    throw new PreparedTicketPublicationProtectionException(
                        PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph,
                        "An accepted Zulip reference has ambiguous hydration rows.");
                }
            }
        }
        int changed;
        if (id is not null && row.HydrationStatus == "unresolved")
        {
            ZulipReferenceHydrationReasonReadResult prior = ZulipReferenceHydrationReason.Read(storedReason);
            ZulipReferenceHydrationOutcome latest = ZulipReferenceHydrationReason.Read(row.HydrationReason).Metadata
                ?? throw new InvalidOperationException("The validated lookup outcome has no metadata.");
            bool safeUrl = ZulipReferenceContract.IsSafeUrl(storedUrl);
            ZulipReferenceBacking backing = !safeUrl ? ZulipReferenceBacking.None
                : prior is { HasSourceBacking: true, Metadata: { } backed } ? backed.Backing
                : prior.Metadata is null && prior.MetadataFailure is null && legacyIndexedContext
                    ? ZulipReferenceBacking.LegacyIndexedContext
                    : ZulipReferenceBacking.Unverified;
            ZulipReferenceDiagnosticCode[] diagnostics = latest.Diagnostics
                .Concat(prior.Metadata?.Diagnostics ?? [])
                .Concat(prior.MetadataFailure is { } failure ? [failure] : [])
                .Distinct().Order().ToArray();
            string reason = ZulipReferenceHydrationReason.Serialize(latest with
            {
                Backing = backing,
                Diagnostics = diagnostics,
            });
            // Old context and its observation time are not a successful observation of this failed attempt.
            changed = await ExecuteAsyncWithCount(
                connection,
                """
                UPDATE prepared_zulip_hydration
                SET Url = @url, HydrationStatus = 'unresolved', HydrationReason = @reason
                WHERE RowId = @rowId AND Id = @id
                """,
                ct, ("@url", safeUrl ? storedUrl : null), ("@reason", reason),
                ("@rowId", rowId), ("@id", id));
        }
        else
        {
            changed = await ExecuteAsyncWithCount(
                connection,
                id is null
                    ? """
                      INSERT INTO prepared_zulip_hydration(
                          Id, TicketKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount,
                          FirstMessageAt, LastMessageAt, FirstMessageExcerpt, Url,
                          HydratedAt, HydrationStatus, HydrationReason)
                      VALUES(
                          @id, @ticketKey, @reference, @streamId, @streamName, @topic, @count,
                          @first, @last, @excerpt, @url, @hydratedAt, @status, @reason)
                      """
                    : """
                      UPDATE prepared_zulip_hydration
                      SET StreamId = @streamId, StreamName = @streamName, Topic = @topic,
                          MessageCount = @count, FirstMessageAt = @first, LastMessageAt = @last,
                          FirstMessageExcerpt = @excerpt, Url = @url,
                          HydratedAt = @hydratedAt, HydrationStatus = @status, HydrationReason = @reason
                      WHERE RowId = @rowId AND Id = @id
                      """,
                ct, ("@id", id ?? Guid.NewGuid().ToString("N")), ("@rowId", rowId),
                ("@ticketKey", row.TicketKey), ("@reference", row.ZulipThreadId),
                ("@streamId", row.StreamId), ("@streamName", row.StreamName), ("@topic", row.Topic),
                ("@count", row.MessageCount),
                ("@first", row.FirstMessageAt is { } first ? Format(first.ToUniversalTime()) : null),
                ("@last", row.LastMessageAt is { } last ? Format(last.ToUniversalTime()) : null),
                ("@excerpt", row.FirstMessageExcerpt), ("@url", row.Url),
                ("@hydratedAt", Format(row.HydratedAt)), ("@status", row.HydrationStatus),
                ("@reason", row.HydrationReason));
        }
        if (changed != 1)
        {
            throw new PreparedTicketPublicationProtectionException(
                PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph,
                "A publication Zulip write did not persist exactly one accepted hydration row.");
        }
    }

    public async Task<PreparedTicketPublicationRefreshReceiptRecord>
        ApplyPublicationMetadataAsync(
            string runId,
            string stageId,
            string stageLeaseId,
            string inputFingerprint,
            string corpusFingerprint,
            IReadOnlyList<PreparedTicketPublicationRefreshCandidate>
                expectedCandidates,
            IReadOnlyList<PreparedTicketPublicationMetadata> metadata,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageLeaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusFingerprint);
        ArgumentNullException.ThrowIfNull(expectedCandidates);
        ArgumentNullException.ThrowIfNull(metadata);

        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureStageLeaseAsync(
                connection,
                runId,
                stageId,
                stageLeaseId,
                inputFingerprint,
                PublicationMetadataStageName,
                string.Empty,
                ct);

            PreparedTicketPublicationRefreshReceiptRecord? existing =
                await ReadPublicationRefreshReceiptAsync(
                    connection,
                    runId,
                    stageId,
                    ct);
            if (existing is not null)
            {
                EnsureMatchingPublicationRefreshReceipt(
                    existing,
                    inputFingerprint,
                    corpusFingerprint);
                await ExecuteRawAsync(connection, "COMMIT", ct);
                return existing;
            }

            string sourceRunId = await ReadPublicationRefreshSourceRunIdAsync(
                connection,
                runId,
                ct);
            PreparedTicketPublicationRefreshInventory current =
                await ReadPublicationRefreshInventoryAsync(connection, ct);
            EnsureMatchingPublicationInventory(
                current,
                expectedCandidates,
                corpusFingerprint);

            string canonicalInputFingerprint =
                PreparedTicketPublicationContract
                    .ComputePublicationRefreshInputFingerprint(
                        sourceRunId,
                        current.Candidates.Select(candidate =>
                            candidate.ToPublicationCorpusItem()));
            if (!string.Equals(
                    canonicalInputFingerprint,
                    inputFingerprint,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    "The publication refresh stage input no longer matches its source run and accepted corpus.");
            }

            PreparedTicketPublicationMetadata[] normalizedMetadata =
                NormalizeAndValidatePublicationMetadata(
                    current.Candidates,
                    metadata);
            long sourceContentRevision =
                normalizedMetadata[0].SourceContentRevision;
            DateTimeOffset sourceLastSuccessfulRefreshAt =
                normalizedMetadata.Max(row =>
                    row.SourceLastSuccessfulRefreshAt);
            DateTimeOffset appliedAt = DateTimeOffset.UtcNow;

            foreach (PreparedTicketPublicationMetadata row in
                     normalizedMetadata)
            {
                int parentRows = await ExecuteAsyncWithCount(
                    connection,
                    """
                    UPDATE prepared_ticket_hydration
                    SET Reporter = @reporter,
                        Assignee = @assignee,
                        PublicDisplayNamePolicyVersion = @policyVersion,
                        SourceProject = @sourceProject,
                        SourceLastSuccessfulRefreshAt = @sourceRefresh,
                        SourceContentRevision = @sourceContentRevision,
                        HydratedAt = @hydratedAt,
                        HydrationStatus = 'resolved',
                        HydrationReason = NULL
                    WHERE TicketKey = @ticketKey COLLATE NOCASE
                    """,
                    ct,
                    ("@reporter", row.Reporter),
                    ("@assignee", row.Assignee),
                    ("@policyVersion",
                        row.PublicDisplayNamePolicyVersion),
                    ("@sourceProject", row.SourceProject),
                    ("@sourceRefresh",
                        Format(row.SourceLastSuccessfulRefreshAt)),
                    ("@sourceContentRevision",
                        row.SourceContentRevision),
                    ("@hydratedAt", Format(row.HydratedAt)),
                    ("@ticketKey", row.TicketKey));
                if (parentRows != 1)
                {
                    throw new InvalidOperationException(
                        $"Prepared ticket '{row.TicketKey}' has no unique parent hydration row to refresh.");
                }

                int selfRows = await ExecuteAsyncWithCount(
                    connection,
                    """
                    UPDATE prepared_jira_hydration
                    SET Reporter = @reporter,
                        Assignee = @assignee,
                        PublicDisplayNamePolicyVersion = @policyVersion
                    WHERE TicketKey = @ticketKey COLLATE NOCASE
                      AND JiraKey = TicketKey COLLATE NOCASE
                    """,
                    ct,
                    ("@reporter", row.Reporter),
                    ("@assignee", row.Assignee),
                    ("@policyVersion",
                        row.PublicDisplayNamePolicyVersion),
                    ("@ticketKey", row.TicketKey));
                if (selfRows != 1)
                {
                    throw new InvalidOperationException(
                        $"Prepared ticket '{row.TicketKey}' has no unique canonical Jira hydration row to refresh.");
                }

                await ExecuteAsync(
                    connection,
                    """
                    DELETE FROM prepared_ticket_in_person_requesters
                    WHERE TicketKey = @ticketKey COLLATE NOCASE
                    """,
                    ct,
                    ("@ticketKey", row.TicketKey));
                foreach (string displayName in row.InPersonRequesters)
                {
                    await ExecuteAsync(
                        connection,
                        """
                        INSERT INTO prepared_ticket_in_person_requesters(
                            TicketKey, DisplayName,
                            PublicDisplayNamePolicyVersion)
                        VALUES(@ticketKey, @displayName, @policyVersion)
                        """,
                        ct,
                        ("@ticketKey", row.TicketKey),
                        ("@displayName", displayName),
                        ("@policyVersion",
                            row.PublicDisplayNamePolicyVersion));
                }
            }

            await ExecuteAsync(
                connection,
                """
                DELETE FROM authoring_run_input_provenance
                WHERE RunId = @runId
                  AND Source = @source COLLATE NOCASE
                """,
                ct,
                ("@runId", runId),
                ("@source",
                    PreparedTicketPublicationContract.JiraSourceName));
            PreparedTicketPublicationRefreshReceiptRecord receipt =
                await WritePublicationRefreshReceiptAsync(
                    connection, runId, stageId, inputFingerprint, corpusFingerprint,
                    sourceLastSuccessfulRefreshAt, sourceContentRevision, appliedAt, ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return receipt;
        }
        catch
        {
            await ExecuteRawAsync(
                connection,
                "ROLLBACK",
                CancellationToken.None);
            throw;
        }
    }

    public async Task<PreparedTicketPublicationRefreshReceiptRecord?>
        GetPublicationRefreshReceiptAsync(
            string runId,
            string stageId,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ReadPublicationRefreshReceiptAsync(
            connection,
            runId,
            stageId,
            ct);
    }

    public async Task<PreparedTicketPublicationRefreshReceiptRecord?>
        GetMatchingPublicationRefreshReceiptAsync(
            string runId,
            string stageId,
            string inputFingerprint,
            string corpusFingerprint,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationRefreshReceiptRecord? receipt =
            await GetPublicationRefreshReceiptAsync(runId, stageId, ct);
        if (receipt is not null)
        {
            EnsureMatchingPublicationRefreshReceipt(
                receipt,
                inputFingerprint,
                corpusFingerprint);
        }
        return receipt;
    }

    private static async Task<string> ReadPublicationRefreshSourceRunIdAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT refresh.SourceRunId
            FROM authoring_runs refresh
            INNER JOIN authoring_runs source
                ON source.Id = refresh.SourceRunId
               AND source.ProcessorKind = refresh.ProcessorKind
            WHERE refresh.Id = @runId
              AND refresh.SourceRunId <> refresh.Id
              AND refresh.ProcessorKind = @processorKind
              AND refresh.Purpose = @purpose
              AND refresh.DatabaseOnly = 0
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@processorKind",
            AuthoringProcessorKind);
        command.Parameters.AddWithValue(
            "@purpose",
            AuthoringRunPurposeValues.PublicationRefresh);
        string? sourceRunId =
            Convert.ToString(await command.ExecuteScalarAsync(ct));
        if (string.IsNullOrWhiteSpace(sourceRunId))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.RunNotActive,
                $"Run '{runId}' is not a snapshot-producing publication refresh run.");
        }
        return sourceRunId;
    }

    private static PreparedTicketPublicationMetadata[]
        NormalizeAndValidatePublicationMetadata(
            IReadOnlyList<PreparedTicketPublicationRefreshCandidate>
                candidates,
            IReadOnlyList<PreparedTicketPublicationMetadata> metadata)
    {
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "A publication refresh requires at least one receipt-backed ticket.");
        }
        if (metadata.Count != candidates.Count)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "Publication metadata does not cover the accepted corpus exactly once.");
        }

        Dictionary<string, PreparedTicketPublicationMetadata> byTicket =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (PreparedTicketPublicationMetadata row in metadata)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (!byTicket.TryAdd(row.TicketKey, row))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Publication metadata contains duplicate ticket '{row.TicketKey}'.");
            }
        }

        List<PreparedTicketPublicationMetadata> normalized = [];
        long? sourceContentRevision = null;
        foreach (PreparedTicketPublicationRefreshCandidate candidate in
                 candidates)
        {
            if (!byTicket.TryGetValue(
                    candidate.TicketKey,
                    out PreparedTicketPublicationMetadata? row))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Publication metadata is missing ticket '{candidate.TicketKey}'.");
            }
            if (!row.SourceIsStable || row.SourceContentRevision < 0)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Publication metadata for '{candidate.TicketKey}' was not read from a stable Jira generation.");
            }
            if (row.PublicDisplayNamePolicyVersion !=
                PublicDisplayNamePolicy.CurrentVersion)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Publication metadata for '{candidate.TicketKey}' does not carry the current public display-name policy.");
            }

            string expectedRevision =
                AuthoringSourceRevision.CanonicalizeTimestamp(
                    candidate.ExpectedSourceRevision);
            string observedRevision =
                AuthoringSourceRevision.CanonicalizeTimestamp(
                    row.ObservedSourceRevision);
            if (!JiraSourceRevision.AreEquivalent(
                    expectedRevision,
                    observedRevision))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"Jira source revision for '{candidate.TicketKey}' changed from '{expectedRevision}' to '{observedRevision}'.");
            }
            if (row.UpdatedAt is { } updatedAt &&
                !JiraSourceRevision.AreEquivalent(Format(updatedAt), observedRevision))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"The explicit Jira update time for '{candidate.TicketKey}' does not match its observed source revision.");
            }

            int separator = candidate.TicketKey.IndexOf(
                '-',
                StringComparison.Ordinal);
            if (separator <= 0 ||
                !string.Equals(
                    candidate.TicketKey[..separator],
                    row.SourceProject,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Publication metadata for '{candidate.TicketKey}' has mismatched project provenance.");
            }

            if (sourceContentRevision is null)
            {
                sourceContentRevision = row.SourceContentRevision;
            }
            else if (sourceContentRevision.Value !=
                     row.SourceContentRevision)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    "Publication metadata spans more than one Jira content revision.");
            }

            IReadOnlyList<string> requesters =
                (row.InPersonRequesters ?? [])
                .Select(value =>
                    PublicDisplayNamePolicy.Normalize(value))
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();
            normalized.Add(row with
            {
                Reporter = PublicDisplayNamePolicy.Normalize(row.Reporter),
                Assignee = PublicDisplayNamePolicy.Normalize(row.Assignee),
                InPersonRequesters = requesters,
                SourceProject = candidate.TicketKey[..separator],
                PublicDisplayNamePolicyVersion =
                    PublicDisplayNamePolicy.CurrentVersion,
            });
        }
        return normalized.ToArray();
    }

    private static async Task<PreparedTicketPublicationRefreshReceiptRecord> WritePublicationRefreshReceiptAsync(
        SqliteConnection connection,
        string runId,
        string stageId,
        string inputFingerprint,
        string corpusFingerprint,
        DateTimeOffset sourceLastSuccessfulRefreshAt,
        long sourceContentRevision,
        DateTimeOffset appliedAt,
        CancellationToken ct)
    {
        int provenanceRows = await ExecuteAsyncWithCount(
            connection,
            """
            INSERT INTO authoring_run_input_provenance(
                RunId, Source, LatestSuccessfulRefreshAt, ContentRevision, CapturedAt)
            VALUES(@runId, @source, @sourceRefresh, @sourceContentRevision, @capturedAt)
            """,
            ct, ("@runId", runId), ("@source", PreparedTicketPublicationContract.JiraSourceName),
            ("@sourceRefresh", Format(sourceLastSuccessfulRefreshAt)),
            ("@sourceContentRevision", sourceContentRevision), ("@capturedAt", Format(appliedAt)));
        if (provenanceRows != 1)
        {
            throw new InvalidOperationException("The publication provenance was not persisted exactly once.");
        }
        int receiptRows = await ExecuteAsyncWithCount(
            connection,
            """
            INSERT INTO prepared_ticket_publication_refresh_receipts(
                RunId, StageId, InputFingerprint, CorpusFingerprint,
                SourceLastSuccessfulRefreshAt, SourceContentRevision, PublicDisplayNamePolicyVersion, AppliedAt)
            VALUES(@runId, @stageId, @inputFingerprint, @corpusFingerprint,
                @sourceRefresh, @sourceContentRevision, @policyVersion, @appliedAt)
            """,
            ct, ("@runId", runId), ("@stageId", stageId), ("@inputFingerprint", inputFingerprint),
            ("@corpusFingerprint", corpusFingerprint), ("@sourceRefresh", Format(sourceLastSuccessfulRefreshAt)),
            ("@sourceContentRevision", sourceContentRevision), ("@policyVersion", PublicDisplayNamePolicy.CurrentVersion),
            ("@appliedAt", Format(appliedAt)));
        if (receiptRows != 1)
        {
            throw new InvalidOperationException("The publication apply receipt was not persisted exactly once.");
        }
        return await ReadPublicationRefreshReceiptAsync(connection, runId, stageId, ct)
            ?? throw new InvalidOperationException("The publication refresh receipt was not persisted.");
    }

    private static void EnsureMatchingPublicationInventory(
        PreparedTicketPublicationRefreshInventory current,
        IReadOnlyList<PreparedTicketPublicationRefreshCandidate> expected,
        string expectedCorpusFingerprint)
    {
        string suppliedFingerprint =
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                expected.Select(candidate =>
                    candidate.ToPublicationCorpusItem()));
        if (!string.Equals(
                suppliedFingerprint,
                expectedCorpusFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.CorpusFingerprint,
                expectedCorpusFingerprint,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "The accepted publication corpus changed before metadata could be applied.");
        }

        PreparedTicketPublicationRefreshCandidate[] expectedOrdered =
            expected
                .OrderBy(
                    candidate => candidate.TicketKey,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    candidate => candidate.TicketKey,
                    StringComparer.Ordinal)
                .ToArray();
        if (!current.Candidates.SequenceEqual(expectedOrdered))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "The accepted publication receipt coordinates changed before metadata could be applied.");
        }
    }

    private static void EnsureMatchingPublicationRefreshReceipt(
        PreparedTicketPublicationRefreshReceiptRecord receipt,
        string inputFingerprint,
        string corpusFingerprint)
    {
        if (!string.Equals(
                receipt.InputFingerprint,
                inputFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.CorpusFingerprint,
                corpusFingerprint,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Publication refresh receipt for stage '{receipt.StageId}' does not match the requested input.");
        }
    }

    private static async Task<
        PreparedTicketPublicationRefreshReceiptRecord?>
        ReadPublicationRefreshReceiptAsync(
            SqliteConnection connection,
            string runId,
            string stageId,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, RunId, StageId, InputFingerprint,
                   CorpusFingerprint, SourceLastSuccessfulRefreshAt,
                   SourceContentRevision, PublicDisplayNamePolicyVersion,
                   AppliedAt
            FROM prepared_ticket_publication_refresh_receipts
            WHERE RunId = @runId AND StageId = @stageId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@stageId", stageId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new PreparedTicketPublicationRefreshReceiptRecord
            {
                RowId = reader.GetInt32(0),
                RunId = reader.GetString(1),
                StageId = reader.GetString(2),
                InputFingerprint = reader.GetString(3),
                CorpusFingerprint = reader.GetString(4),
                SourceLastSuccessfulRefreshAt =
                    ParseDate(reader.GetString(5)),
                SourceContentRevision = reader.GetInt64(6),
                PublicDisplayNamePolicyVersion = reader.GetInt32(7),
                AppliedAt = ParseDate(reader.GetString(8)),
            }
            : null;
    }

    private static async Task<(string ReceiptId, string RunItemId, string RunId)?>
        ReadCurrentPreparedReceiptCoordinatesAsync(
            SqliteConnection connection,
            string ticketKey,
            string graphHash,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.Id, i.Id, i.RunId
            FROM prepared_ticket_authoring_state s
            INNER JOIN authoring_result_receipts r
                ON r.OperationId = s.OperationId
               AND r.RunId = s.RunId
               AND r.RunItemId = s.RunItemId
               AND r.BusinessKey = s.TicketKey COLLATE NOCASE
               AND r.ContentHash = s.ReceiptContentHash
               AND r.ObservedSourceRevision = r.ExpectedSourceRevision
            INNER JOIN authoring_run_items i
                ON i.Id = s.RunItemId
               AND i.RunId = s.RunId
               AND i.BusinessKey = s.TicketKey COLLATE NOCASE
               AND i.ItemKind = 'fhir' COLLATE NOCASE
               AND i.AcceptedReceiptId = r.Id
               AND i.ExpectedSourceRevision = r.ExpectedSourceRevision
               AND i.Status IN (@itemComplete, @itemSuperseded)
            INNER JOIN authoring_runs run
                ON run.Id = s.RunId
               AND run.ProcessorKind = @processorKind
               AND run.AuthoringEpoch = r.AuthoringEpoch
            WHERE s.TicketKey = @ticketKey COLLATE NOCASE
              AND s.Classification = 'receipt-backed'
              AND s.GraphHash = @graphHash
            """;
        command.Parameters.AddWithValue("@itemComplete", AuthoringStatusValues.Items.Complete);
        command.Parameters.AddWithValue(
            "@itemSuperseded",
            AuthoringStatusValues.Items.Superseded);
        command.Parameters.AddWithValue("@processorKind", AuthoringProcessorKind);
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
        command.Parameters.AddWithValue("@graphHash", graphHash);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<int> CountLegacyUnverifiedAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM prepared_ticket_authoring_state WHERE Classification = 'legacy-unverified'";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<int> CountLegacyUnverifiedNotSupersededByRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE run_chain(Id) AS (
                SELECT @runId
                UNION ALL
                SELECT lineage.PreviousRunId
                FROM authoring_revalidation_lineage lineage
                INNER JOIN run_chain chain ON lineage.RunId = chain.Id
            )
            SELECT COUNT(*)
            FROM prepared_ticket_authoring_state s
            WHERE s.Classification = 'legacy-unverified'
              AND NOT EXISTS (
                  SELECT 1
                  FROM authoring_run_items i
                  INNER JOIN run_chain chain ON chain.Id = i.RunId
                  WHERE i.BusinessKey = s.TicketKey COLLATE NOCASE
                    AND i.Status = @superseded
                    AND i.AcceptedReceiptId IS NULL
              )
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@superseded",
            AuthoringStatusValues.Items.Superseded);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    public async Task RetireSupersededLegacyRowsAsync(
        string runId,
        AuthoringRunStageLease lease,
        string inputFingerprint,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureStageLeaseAsync(
                connection,
                runId,
                lease.StageId,
                lease.LeaseId,
                inputFingerprint,
                "revalidation-retirement",
                "",
                ct);
            await ExecuteAsync(
                connection,
                """
                WITH RECURSIVE run_chain(Id, Depth) AS (
                    SELECT @runId, 0
                    UNION ALL
                    SELECT lineage.PreviousRunId, chain.Depth + 1
                    FROM authoring_revalidation_lineage lineage
                    INNER JOIN run_chain chain ON lineage.RunId = chain.Id
                )
                UPDATE prepared_ticket_authoring_state
                SET Classification = 'superseded',
                    ReceiptContentHash = NULL,
                    RunId = (
                        SELECT i.RunId
                        FROM authoring_run_items i
                        INNER JOIN run_chain chain ON chain.Id = i.RunId
                        WHERE i.BusinessKey =
                              prepared_ticket_authoring_state.TicketKey COLLATE NOCASE
                          AND i.Status = @superseded
                          AND i.AcceptedReceiptId IS NULL
                        ORDER BY chain.Depth, i.RowId
                        LIMIT 1
                    ),
                    RunItemId = (
                        SELECT i.Id
                        FROM authoring_run_items i
                        INNER JOIN run_chain chain ON chain.Id = i.RunId
                        WHERE i.BusinessKey =
                              prepared_ticket_authoring_state.TicketKey COLLATE NOCASE
                          AND i.Status = @superseded
                          AND i.AcceptedReceiptId IS NULL
                        ORDER BY chain.Depth, i.RowId
                        LIMIT 1
                    ),
                    OperationId = NULL,
                    UpdatedAt = @updatedAt
                WHERE Classification = 'legacy-unverified'
                  AND EXISTS (
                      SELECT 1
                      FROM authoring_run_items i
                      INNER JOIN run_chain chain ON chain.Id = i.RunId
                      WHERE i.BusinessKey =
                            prepared_ticket_authoring_state.TicketKey COLLATE NOCASE
                        AND i.Status = @superseded
                        AND i.AcceptedReceiptId IS NULL
                  )
                """,
                ct,
                ("@runId", runId),
                ("@superseded", AuthoringStatusValues.Items.Superseded),
                ("@updatedAt", Format(DateTimeOffset.UtcNow)));
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<AuthoringMaintenanceRunItem>>
        GetGroupingMaintenanceItemsAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await GetGroupingMaintenanceItemsAsync(connection, ct);
    }

    public async Task<IReadOnlyList<AuthoringMaintenanceRunItem>>
        GetGroupingMaintenanceItemsAsync(
            SqliteConnection connection,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.TicketKey, i.ItemKind, r.ExpectedSourceRevision, r.Id
            FROM prepared_ticket_authoring_state s
            INNER JOIN authoring_result_receipts r
                ON r.OperationId = s.OperationId
            INNER JOIN authoring_run_items i
                ON i.Id = s.RunItemId
               AND i.AcceptedReceiptId = r.Id
            WHERE s.Classification = 'receipt-backed'
            ORDER BY s.TicketKey
            """;
        List<AuthoringMaintenanceRunItem> items = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new AuthoringMaintenanceRunItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return items;
    }

    public async Task<bool> HasActiveAuthoringFenceAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM authoring_mutation_fences)";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0;
    }

    public async Task<PreparerMaintenanceLease?> TryAcquireMaintenanceLeaseAsync(
        string operation,
        CancellationToken ct = default)
    {
        string runId =
            $"maintenance:{MaintenanceOwnerGeneration}:{operation}:{Guid.NewGuid():N}";
        string leaseId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            int inserted = await ExecuteAsyncWithCount(
                connection,
                """
                INSERT OR IGNORE INTO authoring_mutation_fences(ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(@processorKind, @runId, @leaseId, @acquiredAt)
                """,
                ct,
                ("@processorKind", AuthoringProcessorKind),
                ("@runId", runId),
                ("@leaseId", leaseId),
                ("@acquiredAt", Format(DateTimeOffset.UtcNow)));
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return inserted == 1 ? new PreparerMaintenanceLease(runId, leaseId) : null;
        }

        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<int> RecoverInterruptedMaintenanceLeasesAsync(
        CancellationToken ct = default)
    {
        if (_startupOwnerLock is null)
        {
            throw new InvalidOperationException(
                "Preparer startup ownership must be acquired before recovering maintenance leases.");
        }
        await using SqliteConnection connection = OpenConnection();
        return await ExecuteAsyncWithCount(
            connection,
            """
            DELETE FROM authoring_mutation_fences
            WHERE ProcessorKind = @processorKind
              AND RunId LIKE 'maintenance:%'
              AND RunId NOT LIKE @ownerPrefix
            """,
            ct,
            ("@processorKind", AuthoringProcessorKind),
            ("@ownerPrefix", $"maintenance:{MaintenanceOwnerGeneration}:%"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _startupOwnerLock?.Dispose();
            _startupOwnerLock = null;
        }
        base.Dispose(disposing);
    }

    public async Task ReleaseMaintenanceLeaseAsync(
        PreparerMaintenanceLease lease,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(
            connection,
            """
            DELETE FROM authoring_mutation_fences
            WHERE ProcessorKind = @processorKind AND RunId = @runId AND LeaseId = @leaseId
            """,
            ct,
            ("@processorKind", AuthoringProcessorKind),
            ("@runId", lease.RunId),
            ("@leaseId", lease.LeaseId));
    }

    public async Task<IReadOnlyList<PreparedTicketRunPartition>> GetRunPartitionsAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await CreateCurrentSnapshotReceiptBackedTicketsAsync(connection, ct);
        await EnsureRunCorpusReceiptProvenanceAsync(connection, runId, ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE run_chain(Id) AS (
                SELECT @runId
                UNION
                SELECT lineage.PreviousRunId
                FROM authoring_revalidation_lineage lineage
                INNER JOIN run_chain chain ON lineage.RunId = chain.Id
            ),
            completed AS (
                SELECT i.Id, i.BusinessKey
                FROM authoring_run_items i
                INNER JOIN run_chain chain ON chain.Id = i.RunId
                WHERE i.Status = @complete
                  AND i.AcceptedReceiptId IS NOT NULL
            )
            SELECT
                j.WorkGroupClean,
                j.WorkGroup,
                IFNULL(j.Specification, 'Unspecified'),
                IFNULL(j.Type, '')
            FROM completed i
            INNER JOIN prepared_jira_hydration j
                ON j.TicketKey = i.BusinessKey AND j.JiraKey = i.BusinessKey
            WHERE j.WorkGroupClean IS NOT NULL
              AND j.WorkGroupClean <> ''
              AND j.Type IS NOT NULL
              AND j.Type <> ''
            UNION ALL
            SELECT
                p.WorkGroupClean,
                p.WorkGroupDisplay,
                p.Specification,
                p.Type
            FROM completed i
            INNER JOIN prepared_ticket_run_item_partitions p ON p.RunItemId = i.Id
            WHERE p.WorkGroupClean IS NOT NULL
              AND p.WorkGroupClean <> ''
              AND p.Type IS NOT NULL
              AND p.Type <> ''
            UNION ALL
            SELECT
                t.WorkGroupClean,
                t.WorkGroupDisplay,
                t.Specification,
                t.Type
            FROM prepared_ticket_topics t
            WHERE EXISTS(
                SELECT 1
                FROM authoring_run_items i
                WHERE i.RunId = @runId
                  AND i.ItemKind LIKE 'maintenance:%')
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
        List<(string? WorkGroupClean, string? WorkGroupDisplay, string? Specification, string? Type)> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((
                ReadNullableString(reader, 0),
                ReadNullableString(reader, 1),
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3)));
        }
        await reader.DisposeAsync();

        var affected = rows
            .Select(row =>
            {
                string workGroupClean = NormalizePartitionValue(
                    row.WorkGroupClean,
                    "unattributed");
                return (
                    WorkGroupClean: workGroupClean,
                    WorkGroupDisplay: string.IsNullOrWhiteSpace(row.WorkGroupDisplay)
                        ? workGroupClean
                        : row.WorkGroupDisplay.Trim(),
                    Specification: NormalizePartitionValue(row.Specification, "Unspecified"),
                    Type: NormalizePartitionValue(row.Type, string.Empty));
            })
            .GroupBy(row => (row.WorkGroupClean, row.Specification, row.Type))
            .Select(group => (
                group.Key.WorkGroupClean,
                WorkGroupDisplay: group.Select(row => row.WorkGroupDisplay)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? group.Key.WorkGroupClean,
                group.Key.Specification,
                group.Key.Type))
            .OrderBy(
                partition => GetPartitionKey(
                    partition.WorkGroupClean,
                    partition.Specification,
                    partition.Type),
                StringComparer.Ordinal)
            .ToArray();

        List<PreparedTicketRunPartition> partitions = [];
        foreach (var partition in affected)
        {
            IReadOnlyList<(string Key, string ReceiptId)> members =
                await ReadCurrentPartitionMembersAsync(
                    connection,
                    partition.WorkGroupClean,
                    partition.Specification,
                    partition.Type,
                    ct);
            string fingerprint = AuthoringResultHasher.HashNormalizedUtf8(
                string.Join(
                    "\n",
                    members.Select(member => $"{member.Key}:{member.ReceiptId}")));
            partitions.Add(new PreparedTicketRunPartition(
                partition.WorkGroupClean,
                partition.WorkGroupDisplay,
                partition.Specification,
                partition.Type,
                GetPartitionKey(
                    partition.WorkGroupClean,
                    partition.Specification,
                    partition.Type),
                fingerprint,
                members.Select(member => member.Key).ToArray()));
        }
        return partitions;
    }

    private static async Task EnsureRunCorpusReceiptProvenanceAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH RECURSIVE run_chain(Id) AS (
                SELECT @runId
                UNION
                SELECT lineage.PreviousRunId
                FROM authoring_revalidation_lineage lineage
                INNER JOIN run_chain chain ON lineage.RunId = chain.Id
            )
            SELECT COUNT(*)
            FROM authoring_run_items i
            INNER JOIN run_chain chain ON chain.Id = i.RunId
            WHERE i.Status = @complete
              AND i.AcceptedReceiptId IS NOT NULL
              AND i.ItemKind NOT LIKE 'maintenance:%'
              AND NOT EXISTS (
                  SELECT 1
                  FROM {CurrentSnapshotReceiptBackedTicketsTable} valid
                  WHERE valid.RunItemId = i.Id
              )
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@complete",
            AuthoringStatusValues.Items.Complete);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidOperationException(
                "One or more prepared tickets lack valid current receipt provenance.");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> GetSnapshotTableCountsAsync(
        int schemaVersion,
        CancellationToken ct = default)
    {
        AuthoringSnapshotSchemaCatalog schema =
            PreparedTicketSnapshotSchemaResolver.Resolve(schemaVersion);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        await using SqliteConnection connection = OpenConnection();
        foreach (string table in schema.CountedTables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            string quoted =
                $"\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            command.CommandText = $"SELECT COUNT(*) FROM {quoted}";
            counts[table] = Convert.ToInt64(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture);
        }
        return counts;
    }

    public Task<IReadOnlyDictionary<string, long>> GetSnapshotTableCountsAsync(
        CancellationToken ct = default)
        => GetSnapshotTableCountsAsync(
            PreparedTicketSnapshotSchemaV1.Version,
            ct);

    public async Task<int> GetSnapshotReceiptCountAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await CreateCurrentSnapshotReceiptBackedTicketsAsync(connection, ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM (
                SELECT r.Id
                FROM authoring_result_receipts r
                WHERE r.RunId = @runId
                UNION
                SELECT ReceiptId
                FROM {CurrentSnapshotReceiptBackedTicketsTable}
            )
            """;
        command.Parameters.AddWithValue("@runId", runId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Atomically replaces the grouping rows for the
    /// <c>(WorkGroupClean, Specification, Type)</c> partition described by
    /// <paramref name="payload"/>. Validates the payload first, then verifies
    /// that every referenced ticket key exists in <c>prepared_tickets</c>; if
    /// any are missing, throws <see cref="ArgumentException"/> before any
    /// mutation. Inside a single <c>BEGIN IMMEDIATE</c> transaction the
    /// partition's existing topic / group / member rows are deleted and the
    /// new ones inserted.
    /// </summary>
    public async Task<PreparedTicketGroupingSaveResult> SaveGroupingAsync(PreparedTicketGroupingPayload payload, CancellationToken ct = default)
    {
        payload = CanonicalizeGroupingPayload(payload);
        PreparedTicketGroupingPayloadValidator.ThrowIfInvalid(payload);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;

        await using SqliteConnection connection = OpenConnection();

        IReadOnlyList<string> referencedKeys = CollectReferencedTicketKeys(payload);
        if (referencedKeys.Count > 0)
        {
            IReadOnlyList<string> missing = await FindMissingPreparedTicketKeysAsync(connection, referencedKeys, ct);
            if (missing.Count > 0)
            {
                throw new ArgumentException($"Unknown prepared ticket keys: {string.Join(", ", missing)}.", nameof(payload));
            }
        }

        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureLegacyAuthoringModeAsync(connection, ct);
            await EnsureGeneralMutationAllowedAsync(connection, ct);
            PreparedTicketGroupingSaveResult result = await SaveGroupingCoreAsync(
                connection,
                payload,
                savedAt,
                ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return result;
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<PreparedTicketGroupingSaveResult> SaveGroupingForRunAsync(
        PreparedTicketGroupingPayload payload,
        string runId,
        string stageId,
        string stageLeaseId,
        string inputFingerprint,
        CancellationToken ct = default)
    {
        payload = CanonicalizeGroupingPayload(payload);
        PreparedTicketGroupingPayloadValidator.ThrowIfInvalid(payload);
        string outputFingerprint =
            PreparedTicketPublicationContract
                .ComputeGroupingOutputFingerprint(payload);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        IReadOnlyList<string> referencedKeys = CollectReferencedTicketKeys(payload);
        if (referencedKeys.Count > 0)
        {
            IReadOnlyList<string> missing =
                await FindMissingPreparedTicketKeysAsync(connection, referencedKeys, ct);
            if (missing.Count > 0)
            {
                throw new ArgumentException(
                    $"Unknown prepared ticket keys: {string.Join(", ", missing)}.",
                    nameof(payload));
            }
        }

        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureStageLeaseAsync(
                connection,
                runId,
                stageId,
                stageLeaseId,
                inputFingerprint,
                "grouping",
                GetPartitionKey(payload.WorkGroupClean, payload.Specification, payload.Type),
                ct);
            await EnsureGroupingTicketsBelongToPartitionAsync(
                connection,
                runId,
                payload,
                referencedKeys,
                ct);
            PreparedTicketGroupingSaveResult result = await SaveGroupingCoreAsync(
                connection,
                payload,
                savedAt,
                ct);
            await SavePartitionReceiptAsync(
                connection,
                runId,
                stageId,
                GetPartitionKey(payload.WorkGroupClean, payload.Specification, payload.Type),
                inputFingerprint,
                outputFingerprint,
                result,
                savedAt,
                ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return result;
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<AuthoringRunStageReceipt?> GetGroupingReceiptAsync(
        string runId,
        string stageId,
        string partitionKey,
        string inputFingerprint,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TopicRows, TopicGroupRows, MemberRows, PersistedAt
            FROM prepared_ticket_partition_receipts
            WHERE RunId = @runId
              AND StageId = @stageId
              AND PartitionKey = @partitionKey
              AND InputFingerprint = @inputFingerprint
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@stageId", stageId);
        command.Parameters.AddWithValue("@partitionKey", partitionKey);
        command.Parameters.AddWithValue("@inputFingerprint", inputFingerprint);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AuthoringRunStageReceipt(
                runId,
                stageId,
                partitionKey,
                inputFingerprint,
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                ParseDate(reader.GetString(3)))
            : null;
    }

    public async Task<PreparedTicketGroupingReceiptCoordinate?>
        GetLatestGroupingReceiptAsync(
            string partitionKey,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ReadLatestGroupingReceiptAsync(
            connection,
            partitionKey,
            ct);
    }

    public Task<PreparedTicketGroupingCertificationEvidence>
        CertifyGroupingPartitionAsync(
            string runId,
            AuthoringRunStageLease lease,
            PreparedTicketRunPartition partition,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return CertifyGroupingForPublicationAsync(
            runId,
            lease.StageId,
            lease.LeaseId,
            partition,
            ct);
    }

    public async Task<PreparedTicketGroupingCertificationEvidence>
        CertifyGroupingForPublicationAsync(
            string runId,
            string stageId,
            string stageLeaseId,
            PreparedTicketRunPartition partition,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageLeaseId);
        ArgumentNullException.ThrowIfNull(partition);

        string expectedPartitionKey = GetPartitionKey(
            partition.WorkGroupClean,
            partition.Specification,
            partition.Type);
        if (!string.Equals(
                expectedPartitionKey,
                partition.PartitionKey,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The grouping partition key does not match its coordinates.",
                nameof(partition));
        }

        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureStageLeaseAsync(
                connection,
                runId,
                stageId,
                stageLeaseId,
                partition.InputFingerprint,
                GroupingCertificationStageName,
                partition.PartitionKey,
                ct);
            _ = await ReadPublicationRefreshSourceRunIdAsync(
                connection,
                runId,
                ct);

            await CreateCurrentSnapshotReceiptBackedTicketsAsync(
                connection,
                ct);
            IReadOnlyList<(string Key, string ReceiptId)> currentMembers =
                await ReadCurrentPartitionMembersAsync(
                    connection,
                    partition.WorkGroupClean,
                    partition.Specification,
                    partition.Type,
                    ct);
            string currentInputFingerprint =
                AuthoringResultHasher.HashNormalizedUtf8(
                    string.Join(
                        "\n",
                        currentMembers.Select(member =>
                            $"{member.Key}:{member.ReceiptId}")));
            if (!string.Equals(
                    currentInputFingerprint,
                    partition.InputFingerprint,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Grouping partition '{partition.PartitionKey}' no longer has input '{partition.InputFingerprint}'.");
            }

            PreparedTicketGroupingReceiptCoordinate sourceReceipt =
                await ReadLatestGroupingReceiptAsync(
                    connection,
                    partition.PartitionKey,
                    ct)
                ?? throw new InvalidOperationException(
                    $"Grouping partition '{partition.PartitionKey}' has no retained source receipt.");
            if (!string.Equals(
                    sourceReceipt.InputFingerprint,
                    partition.InputFingerprint,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Latest grouping receipt for partition '{partition.PartitionKey}' does not match the current accepted corpus.");
            }

            string outputFingerprint =
                await ComputeGroupingOutputFingerprintAsync(
                    connection,
                    partition.PartitionKey,
                    ct);
            DateTimeOffset certifiedAt = DateTimeOffset.UtcNow;
            bool legacyCertification =
                sourceReceipt.OutputFingerprint is null;
            if (!legacyCertification)
            {
                if (!string.Equals(
                        sourceReceipt.OutputFingerprint,
                        outputFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageFingerprintMismatch,
                        $"Grouping output for partition '{partition.PartitionKey}' no longer matches its source receipt.");
                }
            }
            else
            {
                PreparedTicketPartitionCertificationRecord? existing =
                    await ReadPartitionCertificationAsync(
                        connection,
                        runId,
                        partition.PartitionKey,
                        ct);
                if (existing is null)
                {
                    await ExecuteAsync(
                        connection,
                        """
                        INSERT INTO prepared_ticket_partition_certifications(
                            RunId, StageId, PartitionKey, InputFingerprint,
                            SourceRunId, SourceStageId,
                            SourceInputFingerprint, OutputFingerprint,
                            CertifiedAt)
                        VALUES(
                            @runId, @stageId, @partitionKey,
                            @inputFingerprint, @sourceRunId,
                            @sourceStageId, @sourceInputFingerprint,
                            @outputFingerprint, @certifiedAt)
                        """,
                        ct,
                        ("@runId", runId),
                        ("@stageId", stageId),
                        ("@partitionKey", partition.PartitionKey),
                        ("@inputFingerprint",
                            partition.InputFingerprint),
                        ("@sourceRunId", sourceReceipt.RunId),
                        ("@sourceStageId", sourceReceipt.StageId),
                        ("@sourceInputFingerprint",
                            sourceReceipt.InputFingerprint),
                        ("@outputFingerprint", outputFingerprint),
                        ("@certifiedAt", Format(certifiedAt)));
                }
                else
                {
                    EnsureMatchingPartitionCertification(
                        existing,
                        stageId,
                        partition,
                        sourceReceipt,
                        outputFingerprint);
                    certifiedAt = existing.CertifiedAt;
                }
            }

            await ExecuteRawAsync(connection, "COMMIT", ct);
            return new PreparedTicketGroupingCertificationEvidence(
                runId,
                partition.PartitionKey,
                partition.InputFingerprint,
                outputFingerprint,
                sourceReceipt,
                legacyCertification,
                certifiedAt);
        }
        catch
        {
            await ExecuteRawAsync(
                connection,
                "ROLLBACK",
                CancellationToken.None);
            throw;
        }
    }

    public async Task<PreparedTicketPartitionCertificationRecord?>
        GetPartitionCertificationAsync(
            string runId,
            string partitionKey,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ReadPartitionCertificationAsync(
            connection,
            runId,
            partitionKey,
            ct);
    }

    public async Task<
        IReadOnlyList<PreparedTicketGroupingCertificationEvidence>>
        GetPublicationGroupingCertificationsAsync(
            string runId,
            IReadOnlyList<PreparedTicketRunPartition> partitions,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(partitions);
        if (partitions
            .Select(partition => partition.PartitionKey)
            .Distinct(StringComparer.Ordinal)
            .Count() != partitions.Count)
        {
            throw new ArgumentException(
                "Publication grouping partitions must be unique.",
                nameof(partitions));
        }

        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            _ = await ReadPublicationRefreshSourceRunIdAsync(
                connection,
                runId,
                ct);
            await CreateCurrentSnapshotReceiptBackedTicketsAsync(
                connection,
                ct);

            List<PreparedTicketGroupingCertificationEvidence> evidence =
                new(partitions.Count);
            foreach (PreparedTicketRunPartition partition in partitions
                         .OrderBy(
                             value => value.PartitionKey,
                             StringComparer.Ordinal))
            {
                string expectedPartitionKey = GetPartitionKey(
                    partition.WorkGroupClean,
                    partition.Specification,
                    partition.Type);
                if (!string.Equals(
                        expectedPartitionKey,
                        partition.PartitionKey,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The grouping partition key does not match its coordinates.",
                        nameof(partitions));
                }

                string? stageId;
                DateTimeOffset stageCompletedAt;
                await using (SqliteCommand stageCommand =
                             connection.CreateCommand())
                {
                    stageCommand.CommandText =
                        """
                        SELECT Id, CompletedAt
                        FROM authoring_run_stages
                        WHERE RunId = @runId
                          AND StageName = @stageName
                          AND PartitionKey = @partitionKey
                          AND InputFingerprint = @inputFingerprint
                          AND Status = @complete
                        """;
                    stageCommand.Parameters.AddWithValue("@runId", runId);
                    stageCommand.Parameters.AddWithValue(
                        "@stageName",
                        GroupingCertificationStageName);
                    stageCommand.Parameters.AddWithValue(
                        "@partitionKey",
                        partition.PartitionKey);
                    stageCommand.Parameters.AddWithValue(
                        "@inputFingerprint",
                        partition.InputFingerprint);
                    stageCommand.Parameters.AddWithValue(
                        "@complete",
                        AuthoringStatusValues.Stages.Complete);
                    await using SqliteDataReader stageReader =
                        await stageCommand.ExecuteReaderAsync(ct);
                    if (!await stageReader.ReadAsync(ct) ||
                        stageReader.IsDBNull(1))
                    {
                        throw new InvalidOperationException(
                            $"Grouping partition '{partition.PartitionKey}' has no completed publication certification stage.");
                    }
                    stageId = stageReader.GetString(0);
                    stageCompletedAt =
                        ParseDate(stageReader.GetString(1));
                    if (await stageReader.ReadAsync(ct))
                    {
                        throw new InvalidOperationException(
                            $"Grouping partition '{partition.PartitionKey}' has ambiguous publication certification stages.");
                    }
                }

                IReadOnlyList<(string Key, string ReceiptId)> currentMembers =
                    await ReadCurrentPartitionMembersAsync(
                        connection,
                        partition.WorkGroupClean,
                        partition.Specification,
                        partition.Type,
                        ct);
                string currentInputFingerprint =
                    AuthoringResultHasher.HashNormalizedUtf8(
                        string.Join(
                            "\n",
                            currentMembers.Select(member =>
                                $"{member.Key}:{member.ReceiptId}")));
                if (!string.Equals(
                        currentInputFingerprint,
                        partition.InputFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageFingerprintMismatch,
                        $"Grouping partition '{partition.PartitionKey}' changed after publication certification.");
                }

                PreparedTicketGroupingReceiptCoordinate sourceReceipt =
                    await ReadLatestGroupingReceiptAsync(
                        connection,
                        partition.PartitionKey,
                        ct)
                    ?? throw new InvalidOperationException(
                        $"Grouping partition '{partition.PartitionKey}' has no retained source receipt.");
                if (!string.Equals(
                        sourceReceipt.InputFingerprint,
                        partition.InputFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageFingerprintMismatch,
                        $"Grouping receipt for partition '{partition.PartitionKey}' does not match the current accepted corpus.");
                }

                string outputFingerprint =
                    await ComputeGroupingOutputFingerprintAsync(
                        connection,
                        partition.PartitionKey,
                        ct);
                bool legacyCertification =
                    sourceReceipt.OutputFingerprint is null;
                DateTimeOffset certifiedAt = stageCompletedAt;
                if (legacyCertification)
                {
                    PreparedTicketPartitionCertificationRecord
                        certification =
                            await ReadPartitionCertificationAsync(
                                connection,
                                runId,
                                partition.PartitionKey,
                                ct)
                            ?? throw new InvalidOperationException(
                                $"Legacy grouping partition '{partition.PartitionKey}' has no durable publication certification.");
                    EnsureMatchingPartitionCertification(
                        certification,
                        stageId,
                        partition,
                        sourceReceipt,
                        outputFingerprint);
                    certifiedAt = certification.CertifiedAt;
                }
                else if (!string.Equals(
                             sourceReceipt.OutputFingerprint,
                             outputFingerprint,
                             StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageFingerprintMismatch,
                        $"Grouping output for partition '{partition.PartitionKey}' no longer matches its source receipt.");
                }

                evidence.Add(
                    new PreparedTicketGroupingCertificationEvidence(
                        runId,
                        partition.PartitionKey,
                        partition.InputFingerprint,
                        outputFingerprint,
                        sourceReceipt,
                        legacyCertification,
                        certifiedAt));
            }

            await ExecuteRawAsync(connection, "COMMIT", ct);
            return evidence.AsReadOnly();
        }
        catch
        {
            await ExecuteRawAsync(
                connection,
                "ROLLBACK",
                CancellationToken.None);
            throw;
        }
    }

    public async Task CertifyExistingGroupingAsync(
        string runId,
        string stageId,
        string stageLeaseId,
        PreparedTicketRunPartition partition,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureStageLeaseAsync(
                connection,
                runId,
                stageId,
                stageLeaseId,
                partition.InputFingerprint,
                "grouping",
                partition.PartitionKey,
                ct);
            PreparedTicketGroupingSaveResult counts = await ReadGroupingCountsAsync(
                connection,
                partition,
                ct);
            string outputFingerprint =
                await ComputeGroupingOutputFingerprintAsync(
                    connection,
                    partition.PartitionKey,
                    ct);
            await SavePartitionReceiptAsync(
                connection,
                runId,
                stageId,
                partition.PartitionKey,
                partition.InputFingerprint,
                outputFingerprint,
                counts,
                DateTimeOffset.UtcNow,
                ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static async Task<PreparedTicketGroupingSaveResult> SaveGroupingCoreAsync(
        SqliteConnection connection,
        PreparedTicketGroupingPayload payload,
        DateTimeOffset savedAt,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            DELETE FROM prepared_ticket_topic_members
            WHERE TopicRowId IN (
                SELECT RowId FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
            )
            """,
            ct,
            ("@wg", payload.WorkGroupClean),
            ("@spec", payload.Specification),
            ("@type", payload.Type));
        await ExecuteAsync(
            connection,
            """
            DELETE FROM prepared_ticket_topic_groups
            WHERE TopicRowId IN (
                SELECT RowId FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
            )
            """,
            ct,
            ("@wg", payload.WorkGroupClean),
            ("@spec", payload.Specification),
            ("@type", payload.Type));
        await ExecuteAsync(
            connection,
            "DELETE FROM prepared_ticket_topics WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type",
            ct,
            ("@wg", payload.WorkGroupClean),
            ("@spec", payload.Specification),
            ("@type", payload.Type));

        int topicRows = 0;
        int topicGroupRows = 0;
        int memberRows = 0;
        foreach (PreparedTicketTopicPayload topic in payload.Topics)
        {
            int topicRowId = await InsertTopicAsync(connection, payload, topic, savedAt, ct);
            topicRows++;
            for (int groupIndex = 0; groupIndex < topic.LinkedTicketGroups.Count; groupIndex++)
            {
                PreparedTicketTopicGroupPayload group = topic.LinkedTicketGroups[groupIndex];
                int groupRowId = await InsertTopicGroupAsync(
                    connection,
                    topicRowId,
                    groupIndex,
                    group,
                    savedAt,
                    ct);
                topicGroupRows++;
                foreach (PreparedTicketTopicGroupMemberPayload member in group.Members)
                {
                    await InsertTopicMemberAsync(
                        connection,
                        topicRowId,
                        groupRowId,
                        member.TicketKey,
                        member.Order,
                        ct);
                    memberRows++;
                }
            }

            for (int remainingIndex = 0; remainingIndex < topic.RemainingTicketKeys.Count; remainingIndex++)
            {
                await InsertTopicMemberAsync(
                    connection,
                    topicRowId,
                    topicGroupRowId: null,
                    topic.RemainingTicketKeys[remainingIndex],
                    remainingIndex,
                    ct);
                memberRows++;
            }
        }

        return new PreparedTicketGroupingSaveResult(
            payload.WorkGroupClean,
            payload.Specification,
            payload.Type,
            topicRows,
            topicGroupRows,
            memberRows);
    }

    private static PreparedTicketGroupingPayload CanonicalizeGroupingPayload(
        PreparedTicketGroupingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string cleaned = Hl7WorkGroupNameCleaner.Clean(payload.WorkGroupClean);
        return new PreparedTicketGroupingPayload
        {
            WorkGroupClean = string.IsNullOrWhiteSpace(cleaned)
                ? payload.WorkGroupClean.Trim()
                : cleaned,
            WorkGroupDisplay = payload.WorkGroupDisplay.Trim(),
            Specification = NormalizePartitionValue(
                payload.Specification,
                "Unspecified"),
            Type = NormalizePartitionValue(payload.Type, string.Empty),
            SavedAt = payload.SavedAt,
            Topics = payload.Topics,
        };
    }

    private static async Task EnsureGroupingTicketsBelongToPartitionAsync(
        SqliteConnection connection,
        string runId,
        PreparedTicketGroupingPayload payload,
        IReadOnlyList<string> ticketKeys,
        CancellationToken ct)
    {
        foreach (string ticketKey in ticketKeys)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM prepared_ticket_authoring_state s
                    INNER JOIN authoring_result_receipts r ON r.OperationId = s.OperationId
                    INNER JOIN authoring_run_items i
                        ON i.Id = s.RunItemId
                       AND i.AcceptedReceiptId = r.Id
                       AND i.Status IN (@complete, @superseded)
                    INNER JOIN prepared_jira_hydration j
                        ON j.TicketKey = s.TicketKey AND j.JiraKey = s.TicketKey
                    WHERE s.Classification = 'receipt-backed'
                      AND s.TicketKey = @ticketKey
                      AND j.WorkGroupClean = @workGroupClean
                      AND IFNULL(j.Specification, 'Unspecified') = @specification
                      AND IFNULL(j.Type, '') = @type
                )
                """;
            command.Parameters.AddWithValue("@ticketKey", ticketKey);
            command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
            command.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
            command.Parameters.AddWithValue("@workGroupClean", payload.WorkGroupClean);
            command.Parameters.AddWithValue("@specification", payload.Specification);
            command.Parameters.AddWithValue("@type", payload.Type);
            bool valid = Convert.ToInt32(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture) != 0;
            if (!valid)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Ticket '{ticketKey}' does not belong to grouping partition '{GetPartitionKey(payload.WorkGroupClean, payload.Specification, payload.Type)}' for run '{runId}'.");
            }
        }
    }

    private static async Task<IReadOnlyList<(string Key, string ReceiptId)>>
        ReadCurrentPartitionMembersAsync(
            SqliteConnection connection,
            string workGroupClean,
            string specification,
            string type,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT v.TicketKey, v.ReceiptId
            FROM {CurrentSnapshotReceiptBackedTicketsTable} v
            INNER JOIN prepared_jira_hydration j
                ON j.TicketKey = v.TicketKey AND j.JiraKey = v.TicketKey
            WHERE j.WorkGroupClean = @workGroupClean
              AND IFNULL(j.Specification, 'Unspecified') = @specification
              AND IFNULL(j.Type, '') = @type
            ORDER BY v.TicketKey
            """;
        command.Parameters.AddWithValue("@workGroupClean", workGroupClean);
        command.Parameters.AddWithValue("@specification", specification);
        command.Parameters.AddWithValue("@type", type);
        List<(string Key, string ReceiptId)> members = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            members.Add((reader.GetString(0), reader.GetString(1)));
        }
        return members;
    }

    /// <summary>
    /// Reads the full grouping shape for a single
    /// <c>(WorkGroupClean, Specification, Type)</c> partition. Returns
    /// <c>null</c> only when the partition has no topic rows <em>and</em> no
    /// prep tickets attributable to it via the
    /// <c>prepared_jira_hydration</c> self-join. Topics are sorted in C#:
    /// hinted topics first (ascending by hint); unhinted topics second,
    /// sorted descending by total member count and then ascending by
    /// short description (ordinal-ignore-case).
    /// </summary>
    /// <remarks>
    /// The <c>WorkGroupClean</c> → <c>WorkGroup</c> display-name mapping is
    /// served by the stored <c>prepared_jira_hydration.WorkGroupClean</c>
    /// column (populated on insert via
    /// <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/> and backfilled
    /// on schema migration); SELECTs match on
    /// <c>j.WorkGroupClean = @wg</c>. Tickets with no self-row in
    /// <c>prepared_jira_hydration</c> (<c>JiraKey = TicketKey</c>) are
    /// counted in <c>UnattributedTicketCount</c> but are intentionally
    /// excluded from <c>IndividualTicketKeys</c>.
    /// </remarks>
    public async Task<PreparedTicketGroupingPartition?> GetGroupingAsync(string workGroupClean, string specification, string type, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PreparedTicketGroupingPartition? partition = await BuildPartitionAsync(connection, workGroupClean, workGroupDisplay: null, specification, type, ct);
        if (partition is null)
        {
            return null;
        }

        return partition;
    }

    /// <summary>
    /// Workgroup-wide aggregate. Discovers all <c>(Specification, Type)</c>
    /// partitions that either have topic rows or have at least one
    /// hydrated prep ticket attributable to the workgroup. Returns
    /// <c>null</c> when neither source produces a row.
    /// </summary>
    public async Task<PreparedTicketGroupingWorkGroupView?> GetWorkGroupGroupingsAsync(string workGroupClean, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        IReadOnlyList<(string Specification, string Type)> partitions = await DiscoverWorkGroupPartitionsAsync(connection, workGroupClean, ct);
        string? workGroupDisplay = await ResolveWorkGroupDisplayAsync(connection, workGroupClean, ct);
        if (partitions.Count == 0 && workGroupDisplay is null)
        {
            return null;
        }

        string displayName = workGroupDisplay ?? workGroupClean;
        List<PreparedTicketGroupingPartition> built = [];
        foreach ((string specification, string type) in partitions)
        {
            PreparedTicketGroupingPartition? partition = await BuildPartitionAsync(connection, workGroupClean, displayName, specification, type, ct);
            if (partition is not null)
            {
                built.Add(partition);
            }
        }

        return new PreparedTicketGroupingWorkGroupView(workGroupClean, displayName, built);
    }

    /// <summary>
    /// Returns the workgroup display name resolved from
    /// <c>prepared_ticket_topics.WorkGroupDisplay</c> first, falling
    /// through to the most-recent <c>prepared_jira_hydration.WorkGroup</c>
    /// self-row, mirroring the heading logic used by
    /// <see cref="GetWorkGroupGroupingsAsync"/>. Returns <c>null</c> when
    /// neither source carries a non-empty display string for
    /// <paramref name="workGroupClean"/>. Consumed by
    /// <c>PreparedTicketHydrationController</c>.
    /// </summary>
    public async Task<string?> ResolveWorkGroupDisplayNameAsync(string workGroupClean, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ResolveWorkGroupDisplayAsync(connection, workGroupClean, ct);
    }

    /// <summary>
    /// Returns the per-ticket analytic / clustering projection used by
    /// the <c>topic-groupings</c> skill. For every <c>prepared_jira_hydration</c>
    /// self-row (<c>JiraKey = TicketKey</c>) whose <c>WorkGroup</c>
    /// matches <paramref name="workGroupClean"/> under the
    /// stored <c>WorkGroupClean</c> column populated via
    /// <c>Hl7WorkGroupNameCleaner.Clean</c> on insert
    /// by the rest of the preparer, this method:
    /// <list type="bullet">
    ///   <item>Pulls the partition / display fields
    ///   (<c>Title</c>, <c>Status</c>, <c>Specification</c>, <c>Type</c>)
    ///   from the hydration row.</item>
    ///   <item>Left-joins <c>prepared_tickets</c> to pull the
    ///   <c>RequestSummary</c> / <c>CommentSummary</c> /
    ///   <c>LinkedTicketSummary</c> / <c>RelatedTicketSummary</c> /
    ///   <c>RelatedZulipSummary</c> / <c>RelatedGitHubSummary</c>
    ///   analytic text fields. Tickets without a
    ///   <c>prepared_tickets</c> row are still emitted with empty
    ///   summaries and <c>HasPreparedTicket = false</c> so the
    ///   clustering skill can drop them before building any payload.</item>
    ///   <item>Pulls every <c>prepared_ticket_related_jira</c> row for
    ///   those keys to populate the per-ticket <c>Links</c> list.</item>
    /// </list>
    /// Returns <c>null</c> when the workgroup has zero hydration self-rows.
    /// Read-only — does not touch the hydration sweeper or the
    /// grouping tables.
    /// </summary>
    public async Task<PreparedTicketClusteringSignals?> GetClusteringSignalsAsync(string workGroupClean, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();

        List<PreparedTicketClusteringSignal> tickets = [];
        Dictionary<string, List<PreparedTicketClusteringLink>> linksByTicket = new(StringComparer.Ordinal);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT j.TicketKey,
                       j.Title,
                       j.Status,
                       j.Specification,
                       j.Type,
                       t.RequestSummary,
                       t.CommentSummary,
                       t.LinkedTicketSummary,
                       t.RelatedTicketSummary,
                       t.RelatedZulipSummary,
                       t.RelatedGitHubSummary,
                       CASE WHEN t.Key IS NULL THEN 0 ELSE 1 END AS HasPreparedTicket
                FROM prepared_jira_hydration j
                LEFT JOIN prepared_tickets t ON t.Key = j.TicketKey
                WHERE j.JiraKey = j.TicketKey
                  AND j.WorkGroupClean = @wg
                ORDER BY j.TicketKey ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string ticketKey = reader.GetString(0);
                List<PreparedTicketClusteringLink> ticketLinks = [];
                linksByTicket[ticketKey] = ticketLinks;
                tickets.Add(new PreparedTicketClusteringSignal(
                    TicketKey: ticketKey,
                    Title: ReadNullableString(reader, 1),
                    Status: ReadNullableString(reader, 2),
                    Specification: ReadNullableString(reader, 3),
                    Type: ReadNullableString(reader, 4),
                    RequestSummary: ReadNullableString(reader, 5) ?? string.Empty,
                    CommentSummary: ReadNullableString(reader, 6) ?? string.Empty,
                    LinkedTicketSummary: ReadNullableString(reader, 7) ?? string.Empty,
                    RelatedTicketSummary: ReadNullableString(reader, 8) ?? string.Empty,
                    RelatedZulipSummary: ReadNullableString(reader, 9) ?? string.Empty,
                    RelatedGitHubSummary: ReadNullableString(reader, 10) ?? string.Empty,
                    HasPreparedTicket: reader.GetInt32(11) == 1,
                    Links: ticketLinks));
            }
        }

        if (tickets.Count == 0)
        {
            return null;
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.TicketKey, r.AssociatedTicketKey, r.LinkType, r.Justification
                FROM prepared_ticket_related_jira r
                INNER JOIN prepared_jira_hydration j
                  ON j.TicketKey = r.TicketKey AND j.JiraKey = j.TicketKey
                WHERE j.WorkGroupClean = @wg
                ORDER BY r.TicketKey ASC, r.AssociatedTicketKey ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string ticketKey = reader.GetString(0);
                if (!linksByTicket.TryGetValue(ticketKey, out List<PreparedTicketClusteringLink>? bucket))
                {
                    continue;
                }

                bucket.Add(new PreparedTicketClusteringLink(
                    AssociatedTicketKey: reader.GetString(1),
                    LinkType: reader.GetString(2),
                    Justification: reader.GetString(3)));
            }
        }

        string? workGroupDisplay = await ResolveWorkGroupDisplayAsync(connection, workGroupClean, ct);
        return new PreparedTicketClusteringSignals(workGroupClean, workGroupDisplay, tickets);
    }

    /// <summary>
    /// Lists the per-ticket display projection over
    /// <c>prepared_jira_hydration</c> self-rows
    /// (<c>JiraKey = TicketKey</c>) whose <c>WorkGroup</c> matches
    /// <paramref name="workGroupClean"/> under the
    /// stored <c>WorkGroupClean</c> column (populated via
    /// <c>Hl7WorkGroupNameCleaner.Clean</c> on insert) used by
    /// the grouping query. Rows are ordered by <c>TicketKey</c> ascending.
    /// Returns an empty list (never throws) when no rows match.
    /// Used by the reviewer-facing hydration API.
    /// </summary>
    public async Task<IReadOnlyList<PreparedJiraHydrationRow>> ListJiraHydrationDisplayForWorkGroupAsync(
        string workGroupClean,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain,
                   WorkGroup, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason,
                   Reporter, Assignee, PublicDisplayNamePolicyVersion
            FROM prepared_jira_hydration
            WHERE JiraKey = TicketKey
              AND WorkGroupClean = @wg
            ORDER BY TicketKey ASC
            """;
        command.Parameters.AddWithValue("@wg", workGroupClean);
        List<PreparedJiraHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int? policyVersion =
                reader.IsDBNull(17) ? null : reader.GetInt32(17);
            rows.Add(new PreparedJiraHydrationRow(
                TicketKey: reader.GetString(0),
                JiraKey: reader.GetString(1),
                Title: ReadNullableString(reader, 2),
                Status: ReadNullableString(reader, 3),
                Type: ReadNullableString(reader, 4),
                Priority: ReadNullableString(reader, 5),
                Resolution: ReadNullableString(reader, 6),
                ResolutionDescriptionPlain: ReadNullableString(reader, 7),
                WorkGroup: ReadNullableString(reader, 8),
                Specification: ReadNullableString(reader, 9),
                UpdatedAt: reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
                Url: ReadNullableString(reader, 11),
                HydratedAt: ParseDate(reader.GetString(12)),
                HydrationStatus: reader.GetString(13),
                HydrationReason: ReadNullableString(reader, 14),
                Reporter: NormalizeTrustedDisplayName(
                    ReadNullableString(reader, 15),
                    policyVersion),
                Assignee: NormalizeTrustedDisplayName(
                    ReadNullableString(reader, 16),
                    policyVersion),
                PublicDisplayNamePolicyVersion:
                    NormalizePolicyVersion(policyVersion)));
        }

        return rows;
    }

    /// <summary>
    /// Deletes the partition's topic / group / member rows in a single
    /// transaction. Always succeeds (deleting an empty partition is a
    /// no-op) — matches the source's "regenerate, do not update" stance.
    /// </summary>
    public async Task DeleteGroupingAsync(string workGroupClean, string specification, string type, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureLegacyAuthoringModeAsync(connection, ct);
            await EnsureGeneralMutationAllowedAsync(connection, ct);
            await ExecuteAsync(
                connection,
                """
                DELETE FROM prepared_ticket_topic_members
                WHERE TopicRowId IN (
                    SELECT RowId FROM prepared_ticket_topics
                    WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                )
                """,
                ct,
                ("@wg", workGroupClean),
                ("@spec", specification),
                ("@type", type));

            await ExecuteAsync(
                connection,
                """
                DELETE FROM prepared_ticket_topic_groups
                WHERE TopicRowId IN (
                    SELECT RowId FROM prepared_ticket_topics
                    WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                )
                """,
                ct,
                ("@wg", workGroupClean),
                ("@spec", specification),
                ("@type", type));

            await ExecuteAsync(
                connection,
                "DELETE FROM prepared_ticket_topics WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type",
                ct,
                ("@wg", workGroupClean),
                ("@spec", specification),
                ("@type", type));

            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static IReadOnlyList<string> CollectReferencedTicketKeys(PreparedTicketGroupingPayload payload)
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        foreach (PreparedTicketTopicPayload topic in payload.Topics)
        {
            foreach (PreparedTicketTopicGroupPayload group in topic.LinkedTicketGroups)
            {
                foreach (PreparedTicketTopicGroupMemberPayload member in group.Members)
                {
                    set.Add(member.TicketKey);
                }
            }

            foreach (string remaining in topic.RemainingTicketKeys)
            {
                set.Add(remaining);
            }
        }

        return [.. set];
    }

    private static async Task<IReadOnlyList<string>> FindMissingPreparedTicketKeysAsync(SqliteConnection connection, IReadOnlyList<string> keys, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        List<string> parameterNames = new(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            string name = $"@k{i}";
            parameterNames.Add(name);
            command.Parameters.AddWithValue(name, keys[i]);
        }

        command.CommandText = $"SELECT Key FROM prepared_tickets WHERE Key IN ({string.Join(", ", parameterNames)})";
        HashSet<string> found = new(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(reader.GetString(0));
        }

        List<string> missing = [];
        foreach (string key in keys)
        {
            if (!found.Contains(key))
            {
                missing.Add(key);
            }
        }

        missing.Sort(StringComparer.Ordinal);
        return missing;
    }

    private static async Task<int> InsertTopicAsync(SqliteConnection connection, PreparedTicketGroupingPayload payload, PreparedTicketTopicPayload topic, DateTimeOffset savedAt, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_topics
            (Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
            VALUES
            (@Id, @WorkGroupClean, @WorkGroupDisplay, @Specification, @Type, @ShortDescription, @LongerDescription, @RenderOrderHint, @SavedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@WorkGroupClean", payload.WorkGroupClean);
        command.Parameters.AddWithValue("@WorkGroupDisplay", payload.WorkGroupDisplay);
        command.Parameters.AddWithValue("@Specification", payload.Specification);
        command.Parameters.AddWithValue("@Type", payload.Type);
        command.Parameters.AddWithValue("@ShortDescription", topic.ShortDescription);
        command.Parameters.AddWithValue("@LongerDescription", topic.LongerDescription);
        command.Parameters.AddWithValue("@RenderOrderHint", (object?)topic.RenderOrderHint ?? DBNull.Value);
        command.Parameters.AddWithValue("@SavedAt", Format(savedAt));
        object? scalar = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertTopicGroupAsync(SqliteConnection connection, int topicRowId, int orderInTopic, PreparedTicketTopicGroupPayload group, DateTimeOffset savedAt, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_topic_groups
            (Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic, SavedAt)
            VALUES
            (@Id, @TopicRowId, @FirstTicketKey, @Rationale, @OrderInTopic, @SavedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TopicRowId", topicRowId);
        command.Parameters.AddWithValue("@FirstTicketKey", group.FirstTicketKey);
        command.Parameters.AddWithValue("@Rationale", group.Rationale);
        command.Parameters.AddWithValue("@OrderInTopic", orderInTopic);
        command.Parameters.AddWithValue("@SavedAt", Format(savedAt));
        object? scalar = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task InsertTopicMemberAsync(SqliteConnection connection, int topicRowId, int? topicGroupRowId, string ticketKey, int orderInContainer, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_topic_members
            (Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer)
            VALUES
            (@Id, @TopicRowId, @TopicGroupRowId, @TicketKey, @OrderInContainer)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TopicRowId", topicRowId);
        command.Parameters.AddWithValue("@TopicGroupRowId", (object?)topicGroupRowId ?? DBNull.Value);
        command.Parameters.AddWithValue("@TicketKey", ticketKey);
        command.Parameters.AddWithValue("@OrderInContainer", orderInContainer);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PreparedTicketGroupingPartition?> BuildPartitionAsync(SqliteConnection connection, string workGroupClean, string? workGroupDisplay, string specification, string type, CancellationToken ct)
    {
        List<TopicRow> topicRows = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT RowId, Id, WorkGroupDisplay, ShortDescription, LongerDescription, RenderOrderHint, SavedAt
                FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            command.Parameters.AddWithValue("@spec", specification);
            command.Parameters.AddWithValue("@type", type);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                topicRows.Add(new TopicRow(
                    RowId: reader.GetInt32(0),
                    Id: reader.GetString(1),
                    WorkGroupDisplay: reader.GetString(2),
                    ShortDescription: reader.GetString(3),
                    LongerDescription: reader.GetString(4),
                    RenderOrderHint: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    SavedAt: DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        List<string> individualKeys = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.Key
                FROM prepared_tickets t
                INNER JOIN prepared_jira_hydration j
                  ON j.TicketKey = t.Key AND j.JiraKey = t.Key
                WHERE j.WorkGroupClean = @wg
                  AND IFNULL(j.Type, '') = @type
                  AND IFNULL(j.Specification, 'Unspecified') = @spec
                  AND NOT EXISTS (
                      SELECT 1
                      FROM prepared_ticket_topic_members m
                      INNER JOIN prepared_ticket_topics topic ON topic.RowId = m.TopicRowId
                      WHERE m.TicketKey = t.Key
                        AND topic.WorkGroupClean = @wg
                        AND topic.Specification = @spec
                        AND topic.Type = @type
                  )
                ORDER BY t.Key
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            command.Parameters.AddWithValue("@spec", specification);
            command.Parameters.AddWithValue("@type", type);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                individualKeys.Add(reader.GetString(0));
            }
        }

        int unattributedCount;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM prepared_tickets t
                WHERE NOT EXISTS (
                    SELECT 1 FROM prepared_jira_hydration j
                    WHERE j.TicketKey = t.Key AND j.JiraKey = t.Key
                )
                """;
            object? scalar = await command.ExecuteScalarAsync(ct);
            unattributedCount = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        }

        if (topicRows.Count == 0 && individualKeys.Count == 0)
        {
            return null;
        }

        List<PreparedTicketTopic> topics = [];
        DateTimeOffset? lastSavedAt = null;
        foreach (TopicRow topicRow in topicRows)
        {
            (IReadOnlyList<PreparedTicketTopicGroup> groups, IReadOnlyList<string> remaining) = await LoadTopicContentsAsync(connection, topicRow.RowId, ct);
            topics.Add(new PreparedTicketTopic(
                topicRow.Id,
                topicRow.ShortDescription,
                topicRow.LongerDescription,
                topicRow.RenderOrderHint,
                topicRow.SavedAt,
                groups,
                remaining));
            if (lastSavedAt is null || topicRow.SavedAt > lastSavedAt.Value)
            {
                lastSavedAt = topicRow.SavedAt;
            }
        }

        topics.Sort((a, b) =>
        {
            bool aHinted = a.RenderOrderHint.HasValue;
            bool bHinted = b.RenderOrderHint.HasValue;
            if (aHinted && bHinted)
            {
                int byHint = a.RenderOrderHint!.Value.CompareTo(b.RenderOrderHint!.Value);
                if (byHint != 0)
                {
                    return byHint;
                }

                return string.Compare(a.ShortDescription, b.ShortDescription, StringComparison.OrdinalIgnoreCase);
            }

            if (aHinted != bHinted)
            {
                return aHinted ? -1 : 1;
            }

            int aTotal = TopicTotalCount(a);
            int bTotal = TopicTotalCount(b);
            int byCount = bTotal.CompareTo(aTotal);
            if (byCount != 0)
            {
                return byCount;
            }

            return string.Compare(a.ShortDescription, b.ShortDescription, StringComparison.OrdinalIgnoreCase);
        });

        string resolvedDisplay = workGroupDisplay
            ?? (topicRows.Count > 0 ? topicRows[0].WorkGroupDisplay : workGroupClean);

        return new PreparedTicketGroupingPartition(
            workGroupClean,
            resolvedDisplay,
            specification,
            type,
            topics,
            individualKeys,
            unattributedCount,
            lastSavedAt);
    }

    private static int TopicTotalCount(PreparedTicketTopic topic)
    {
        int total = topic.RemainingTicketKeys.Count;
        foreach (PreparedTicketTopicGroup group in topic.LinkedTicketGroups)
        {
            total += group.Members.Count;
        }

        return total;
    }

    private static async Task<(IReadOnlyList<PreparedTicketTopicGroup> Groups, IReadOnlyList<string> Remaining)> LoadTopicContentsAsync(SqliteConnection connection, int topicRowId, CancellationToken ct)
    {
        List<TopicGroupRow> groupRows = [];
        Dictionary<int, List<PreparedTicketTopicGroupMember>> membersByGroupRowId = [];

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT RowId, Id, FirstTicketKey, Rationale, OrderInTopic, SavedAt
                FROM prepared_ticket_topic_groups
                WHERE TopicRowId = @topic
                ORDER BY OrderInTopic
                """;
            command.Parameters.AddWithValue("@topic", topicRowId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                int rowId = reader.GetInt32(0);
                List<PreparedTicketTopicGroupMember> members = [];
                membersByGroupRowId[rowId] = members;
                groupRows.Add(new TopicGroupRow(
                    RowId: rowId,
                    Id: reader.GetString(1),
                    FirstTicketKey: reader.GetString(2),
                    Rationale: reader.GetString(3),
                    OrderInTopic: reader.GetInt32(4),
                    SavedAt: DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Members: members));
            }
        }

        List<string> remaining = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT TopicGroupRowId, TicketKey, OrderInContainer
                FROM prepared_ticket_topic_members
                WHERE TopicRowId = @topic
                ORDER BY (TopicGroupRowId IS NULL), TopicGroupRowId, OrderInContainer
                """;
            command.Parameters.AddWithValue("@topic", topicRowId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string ticketKey = reader.GetString(1);
                int order = reader.GetInt32(2);
                if (reader.IsDBNull(0))
                {
                    remaining.Add(ticketKey);
                    continue;
                }

                int groupRowId = reader.GetInt32(0);
                if (membersByGroupRowId.TryGetValue(groupRowId, out List<PreparedTicketTopicGroupMember>? bucket))
                {
                    bucket.Add(new PreparedTicketTopicGroupMember(ticketKey, order));
                }
            }
        }

        List<PreparedTicketTopicGroup> groups = new(groupRows.Count);
        foreach (TopicGroupRow row in groupRows)
        {
            groups.Add(new PreparedTicketTopicGroup(
                row.Id,
                row.FirstTicketKey,
                row.Rationale,
                row.OrderInTopic,
                row.SavedAt,
                row.Members));
        }

        return (groups, remaining);
    }

    private static async Task<IReadOnlyList<(string Specification, string Type)>> DiscoverWorkGroupPartitionsAsync(SqliteConnection connection, string workGroupClean, CancellationToken ct)
    {
        HashSet<(string, string)> seen = [];
        List<(string, string)> ordered = [];

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT Specification, Type
                FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                (string, string) tuple = (reader.GetString(0), reader.GetString(1));
                if (seen.Add(tuple))
                {
                    ordered.Add(tuple);
                }
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT IFNULL(j.Specification, 'Unspecified'), IFNULL(j.Type, '')
                FROM prepared_jira_hydration j
                WHERE j.JiraKey = j.TicketKey
                  AND j.WorkGroupClean = @wg
                  AND j.Type IS NOT NULL AND j.Type <> ''
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                (string, string) tuple = (reader.GetString(0), reader.GetString(1));
                if (seen.Add(tuple))
                {
                    ordered.Add(tuple);
                }
            }
        }

        ordered.Sort((a, b) =>
        {
            int bySpec = string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
            if (bySpec != 0)
            {
                return bySpec;
            }

            return string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        });
        return ordered;
    }

    private static async Task<string?> ResolveWorkGroupDisplayAsync(SqliteConnection connection, string workGroupClean, CancellationToken ct)
    {
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT WorkGroupDisplay FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg
                ORDER BY SavedAt DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            object? scalar = await command.ExecuteScalarAsync(ct);
            if (scalar is string display && !string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT j.WorkGroup FROM prepared_jira_hydration j
                WHERE j.JiraKey = j.TicketKey
                  AND j.WorkGroupClean = @wg
                ORDER BY j.HydratedAt DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            object? scalar = await command.ExecuteScalarAsync(ct);
            if (scalar is string display && !string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }

        return null;
    }

    private readonly record struct TopicRow(
        int RowId,
        string Id,
        string WorkGroupDisplay,
        string ShortDescription,
        string LongerDescription,
        int? RenderOrderHint,
        DateTimeOffset SavedAt);

    private sealed record TopicGroupRow(
        int RowId,
        string Id,
        string FirstTicketKey,
        string Rationale,
        int OrderInTopic,
        DateTimeOffset SavedAt,
        List<PreparedTicketTopicGroupMember> Members);


    public async Task<bool> PreparedTicketExistsAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM prepared_tickets WHERE Key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        object? value = await command.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task SaveHydrationAsync(PreparedTicketHydrationBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        PreparedTicketHydrationRow parent =
            NormalizeTrustedPeople(batch.Parent);
        PreparedJiraHydrationRow[] jiraRows = batch.JiraRows
            .Select(NormalizeTrustedPeople)
            .ToArray();
        IReadOnlyList<PreparedTicketInPersonRequesterRow> requesters =
            NormalizeInPersonRequesters(
                batch.TicketKey,
                batch.InPersonRequesters);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync(ct);
        try
        {
            await EnsureTicketMutationAllowedAsync(connection, batch.TicketKey, ct);
            await DeleteHydrationRowsAsync(connection, batch.TicketKey, ct);
            await InsertHydrationParentAsync(connection, parent, ct);
            await ReplaceCanonicalJiraFieldsAsync(connection, parent, ct);
            foreach (PreparedTicketInPersonRequesterRow row in requesters)
            {
                await InsertInPersonRequesterAsync(connection, row, ct);
            }
            foreach (PreparedJiraHydrationRow row in jiraRows)
            {
                await InsertJiraHydrationAsync(connection, row, ct);
            }

            foreach (PreparedZulipHydrationRow row in batch.ZulipRows)
            {
                await InsertZulipHydrationAsync(connection, row, ct);
            }

            foreach (PreparedGitHubHydrationRow row in batch.GitHubRows)
            {
                await InsertGitHubHydrationAsync(connection, row, ct);
            }

            foreach (PreparedRepoHydrationRow row in batch.RepoRows)
            {
                await InsertRepoHydrationAsync(connection, row, ct);
            }

            foreach (PreparedTicketJiraXrefRow row in batch.JiraXrefRows)
            {
                await InsertJiraXrefAsync(connection, row, ct);
            }

            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Explicit-interface mapping from the shared neutral
    /// <see cref="HydrationBatch"/> shape onto the preparer's concrete
    /// <see cref="PreparedTicketHydrationBatch"/>. Reporter persistence
    /// deliberately uses only the authenticated structured channel, while
    /// the remaining fields map directly before delegating to the existing
    /// <see cref="SaveHydrationAsync(PreparedTicketHydrationBatch, CancellationToken)"/>.
    /// </summary>
    Task IHydrationTargetDatabase.SaveHydrationAsync(HydrationBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        PreparedTicketHydrationRow parent = new(
            TicketKey: batch.Parent.TicketKey,
            Priority: batch.Parent.Priority,
            Resolution: batch.Parent.Resolution,
            ResolutionDescriptionPlain: batch.Parent.ResolutionDescriptionPlain,
            Specification: batch.Parent.Specification,
            RaisedInVersion: batch.Parent.RaisedInVersion,
            SelectedBallot: batch.Parent.SelectedBallot,
            ChangeCategory: batch.Parent.ChangeCategory,
            Impact: batch.Parent.Impact,
            Labels: batch.Parent.Labels,
            CommentCount: batch.Parent.CommentCount,
            DescriptionPlain: batch.Parent.DescriptionPlain,
            HydratedAt: batch.Parent.HydratedAt,
            HydrationStatus: batch.Parent.HydrationStatus,
            HydrationReason: batch.Parent.HydrationReason,
            DescriptionHtml: batch.Parent.DescriptionHtml,
            ResolutionDescriptionHtml: batch.Parent.ResolutionDescriptionHtml,
            Reporter: batch.Parent.StructuredReporter,
            CreatedAt: batch.Parent.CreatedAt,
            RelatedArtifactsRaw: batch.Parent.RelatedArtifactsRaw,
            RelatedPagesRaw: batch.Parent.RelatedPagesRaw,
            Assignee: batch.Parent.Assignee,
            SourceProject: batch.Parent.SourceIsStable == true
                ? batch.Parent.SourceProject
                : null,
            SourceLastSuccessfulRefreshAt:
                batch.Parent.SourceIsStable == true
                    ? batch.Parent.SourceLastSuccessfulRefreshAt
                    : null,
            SourceContentRevision: batch.Parent.SourceIsStable == true
                ? batch.Parent.SourceContentRevision
                : null,
            PublicDisplayNamePolicyVersion:
                batch.Parent.PublicDisplayNamePolicyVersion);

        List<PreparedJiraHydrationRow> jiraRows = new(batch.JiraRows.Count);
        foreach (HydrationJiraRow r in batch.JiraRows)
        {
            jiraRows.Add(new PreparedJiraHydrationRow(
                r.TicketKey, r.JiraKey, r.Title, r.Status, r.Type, r.Priority,
                r.Resolution, r.ResolutionDescriptionPlain, r.WorkGroup, r.Specification,
                r.UpdatedAt, r.Url, r.HydratedAt, r.HydrationStatus, r.HydrationReason,
                r.DescriptionHtml, r.ResolutionDescriptionHtml, r.StructuredReporter, r.CreatedAt,
                r.RelatedArtifactsRaw, r.RelatedPagesRaw, r.Assignee,
                r.PublicDisplayNamePolicyVersion));
        }

        List<PreparedZulipHydrationRow> zulipRows = new(batch.ZulipRows.Count);
        foreach (HydrationZulipRow r in batch.ZulipRows)
        {
            zulipRows.Add(new PreparedZulipHydrationRow(
                r.TicketKey, r.ZulipThreadId, r.StreamId, r.StreamName, r.Topic,
                r.MessageCount, r.FirstMessageAt, r.LastMessageAt, r.FirstMessageExcerpt,
                r.Url, r.HydratedAt, r.HydrationStatus, r.HydrationReason));
        }

        List<PreparedGitHubHydrationRow> githubRows = new(batch.GitHubRows.Count);
        foreach (HydrationGitHubRow r in batch.GitHubRows)
        {
            githubRows.Add(new PreparedGitHubHydrationRow(
                r.TicketKey, r.GitHubItemId, r.Owner, r.Repo, r.Number, r.Path,
                r.Title, r.State, r.IsPullRequest, r.Labels, r.UpdatedAt, r.Url,
                r.HydratedAt, r.HydrationStatus, r.HydrationReason));
        }

        List<PreparedRepoHydrationRow> repoRows = new(batch.RepoRows.Count);
        foreach (HydrationRepoRow r in batch.RepoRows)
        {
            repoRows.Add(new PreparedRepoHydrationRow(
                r.TicketKey, r.Repo, r.Description, r.WorkGroup, r.Specification,
                r.CategoryDetail, r.Url, r.HydratedAt, r.HydrationStatus, r.HydrationReason));
        }

        List<PreparedTicketJiraXrefRow> xrefRows = new(batch.JiraXrefRows.Count);
        foreach (HydrationJiraXrefRow r in batch.JiraXrefRows)
        {
            xrefRows.Add(new PreparedTicketJiraXrefRow(r.TicketKey, r.JiraKey, r.Source));
        }

        PreparedTicketHydrationBatch concrete = new(
            TicketKey: batch.TicketKey,
            Parent: parent,
            JiraRows: jiraRows,
            ZulipRows: zulipRows,
            GitHubRows: githubRows,
            RepoRows: repoRows,
            JiraXrefRows: xrefRows,
            InPersonRequesters:
                (batch.Parent.InPersonRequesters ?? [])
                .Select(displayName =>
                    new PreparedTicketInPersonRequesterRow(
                        batch.TicketKey,
                        displayName,
                        batch.Parent.PublicDisplayNamePolicyVersion))
                .ToArray());

        return SaveHydrationAsync(concrete, ct);
    }

    public async Task SaveWorkGroupCatalogAsync(
        IReadOnlyList<HydrationWorkGroupRow> workGroups,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workGroups);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureLegacyAuthoringModeAsync(connection, ct);
            await EnsureGeneralMutationAllowedAsync(connection, ct);
            await ExecuteRawAsync(connection, "DELETE FROM jira_review_workgroups", ct);
            foreach (HydrationWorkGroupRow workGroup in workGroups)
            {
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
                    VALUES(@code, @name, @nameClean, @updatedAt)
                    """,
                    ct,
                    ("@code", workGroup.Code),
                    ("@name", workGroup.Name),
                    ("@nameClean", workGroup.NameClean),
                    ("@updatedAt", Format(workGroup.UpdatedAt)));
            }
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task SaveWorkGroupCatalogForRunAsync(
        IReadOnlyList<HydrationWorkGroupRow> workGroups,
        string runId,
        string stageId,
        string stageLeaseId,
        string inputFingerprint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workGroups);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureStageLeaseAsync(
                connection,
                runId,
                stageId,
                stageLeaseId,
                inputFingerprint,
                "workgroup-catalog",
                string.Empty,
                ct);
            await ExecuteRawAsync(connection, "DELETE FROM jira_review_workgroups", ct);
            foreach (HydrationWorkGroupRow workGroup in workGroups)
            {
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
                    VALUES(@code, @name, @nameClean, @updatedAt)
                    """,
                    ct,
                    ("@code", workGroup.Code),
                    ("@name", workGroup.Name),
                    ("@nameClean", workGroup.NameClean),
                    ("@updatedAt", Format(workGroup.UpdatedAt)));
            }
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<PreparedTicketHydrationReadModel?> GetHydrationAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PreparedTicketHydrationRow? parent = await ReadHydrationParentAsync(connection, key, ct);
        IReadOnlyList<PreparedJiraHydrationRow> jira = await ReadJiraHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedZulipHydrationRow> zulip = await ReadZulipHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedGitHubHydrationRow> github = await ReadGitHubHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedRepoHydrationRow> repos = await ReadRepoHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedTicketJiraXrefRow> xref = await ReadJiraXrefAsync(connection, key, ct);
        IReadOnlyList<PreparedTicketInPersonRequesterRow> requesters =
            await ReadInPersonRequestersAsync(connection, key, ct);
        if (parent is null && jira.Count == 0 && zulip.Count == 0 && github.Count == 0 && repos.Count == 0 && xref.Count == 0 && requesters.Count == 0)
        {
            return null;
        }

        return new PreparedTicketHydrationReadModel(
            parent,
            jira,
            zulip,
            github,
            repos,
            xref,
            requesters);
    }

    /// <summary>
    /// Returns every <c>Key</c> in <c>prepared_tickets</c> in ascending order.
    /// Used by the hydration sweeper as the inventory pass.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListPreparedTicketKeysAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Key FROM prepared_tickets ORDER BY Key";
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>
    /// Returns the set of <c>prepared_tickets.Key</c> values whose
    /// <c>prepared_ticket_hydration</c> row is either missing or has
    /// <c>HydrationStatus = 'unresolved'</c>. These are the keys the
    /// hydration sweeper should re-hydrate.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListUnresolvedOrMissingHydrationKeysAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Key FROM prepared_tickets t
            LEFT JOIN prepared_ticket_hydration h ON h.TicketKey = t.Key
            WHERE h.TicketKey IS NULL OR h.HydrationStatus = 'unresolved'
            ORDER BY t.Key
            """;
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    public async Task<IReadOnlyList<string>> ListRelatedJiraKeysForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT AssociatedTicketKey FROM prepared_ticket_related_jira WHERE TicketKey = @key ORDER BY AssociatedTicketKey",
            key, ct);

    public async Task<IReadOnlyList<string>> ListRelatedZulipThreadIdsForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT ZulipThreadId FROM prepared_ticket_related_zulip WHERE TicketKey = @key ORDER BY ZulipThreadId",
            key, ct);

    public async Task<IReadOnlyList<string>> ListRelatedGitHubItemIdsForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT GitHubItemId FROM prepared_ticket_related_github WHERE TicketKey = @key ORDER BY GitHubItemId",
            key, ct);

    public async Task<IReadOnlyList<string>> ListReposForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT Repo FROM prepared_ticket_repos WHERE TicketKey = @key ORDER BY Repo",
            key, ct);

    private async Task<IReadOnlyList<string>> ReadStringColumnAsync(string sql, string key, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task DeleteHydrationRowsAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        foreach (string table in new[]
        {
            "prepared_ticket_hydration",
            "prepared_jira_hydration",
            "prepared_zulip_hydration",
            "prepared_github_hydration",
            "prepared_repo_hydration",
            "prepared_ticket_jira_xref",
            "prepared_ticket_in_person_requesters",
        })
        {
            await ExecuteAsync(connection, $"DELETE FROM {table} WHERE TicketKey = @key", ct, ("@key", key));
        }
    }

    private static IReadOnlyList<PreparedTicketInPersonRequesterRow>
        NormalizeInPersonRequesters(
            string ticketKey,
            IReadOnlyList<PreparedTicketInPersonRequesterRow>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return [];
        }
        if (rows.Any(row =>
                !string.Equals(
                    row.TicketKey,
                    ticketKey,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Every in-person requester must belong to the hydration batch ticket.",
                nameof(rows));
        }

        return rows
            .Where(row =>
                row.PublicDisplayNamePolicyVersion ==
                    PublicDisplayNamePolicy.CurrentVersion)
            .Select(row =>
                PublicDisplayNamePolicy.Normalize(row.DisplayName))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .Select(displayName =>
                new PreparedTicketInPersonRequesterRow(
                    ticketKey,
                    displayName,
                    PublicDisplayNamePolicy.CurrentVersion))
            .ToArray();
    }

    private static PreparedTicketHydrationRow NormalizeTrustedPeople(
        PreparedTicketHydrationRow row)
    {
        int? policyVersion = NormalizePolicyVersion(
            row.PublicDisplayNamePolicyVersion);
        return row with
        {
            Reporter = NormalizeTrustedDisplayName(
                row.Reporter,
                policyVersion),
            Assignee = NormalizeTrustedDisplayName(
                row.Assignee,
                policyVersion),
            PublicDisplayNamePolicyVersion = policyVersion,
        };
    }

    private static PreparedJiraHydrationRow NormalizeTrustedPeople(
        PreparedJiraHydrationRow row)
    {
        int? policyVersion = NormalizePolicyVersion(
            row.PublicDisplayNamePolicyVersion);
        return row with
        {
            Reporter = NormalizeTrustedDisplayName(
                row.Reporter,
                policyVersion),
            Assignee = NormalizeTrustedDisplayName(
                row.Assignee,
                policyVersion),
            PublicDisplayNamePolicyVersion = policyVersion,
        };
    }

    private static string? NormalizeTrustedDisplayName(
        string? value,
        int? policyVersion)
        => policyVersion == PublicDisplayNamePolicy.CurrentVersion
            ? PublicDisplayNamePolicy.Normalize(value)
            : null;

    private static int? NormalizePolicyVersion(int? policyVersion)
        => policyVersion == PublicDisplayNamePolicy.CurrentVersion
            ? PublicDisplayNamePolicy.CurrentVersion
            : null;

    private static async Task InsertInPersonRequesterAsync(
        SqliteConnection connection,
        PreparedTicketInPersonRequesterRow row,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_in_person_requesters(
                TicketKey, DisplayName, PublicDisplayNamePolicyVersion)
            VALUES(
                @ticketKey, @displayName, @publicDisplayNamePolicyVersion)
            """,
            ct,
            ("@ticketKey", row.TicketKey),
            ("@displayName", row.DisplayName),
            ("@publicDisplayNamePolicyVersion",
                row.PublicDisplayNamePolicyVersion));
    }

    private static async Task InsertHydrationParentAsync(SqliteConnection connection, PreparedTicketHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_hydration
            (Id, TicketKey, Priority, Resolution, ResolutionDescriptionPlain, Specification, RaisedInVersion, SelectedBallot,
             ChangeCategory, Impact, Labels, CommentCount, DescriptionPlain, DescriptionHtml, ResolutionDescriptionHtml,
             Reporter, Assignee, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw, SourceProject,
             SourceLastSuccessfulRefreshAt, SourceContentRevision, HydratedAt, HydrationStatus, HydrationReason,
             PublicDisplayNamePolicyVersion)
            VALUES
            (@Id, @TicketKey, @Priority, @Resolution, @ResolutionDescriptionPlain, @Specification, @RaisedInVersion, @SelectedBallot,
             @ChangeCategory, @Impact, @Labels, @CommentCount, @DescriptionPlain, @DescriptionHtml, @ResolutionDescriptionHtml,
             @Reporter, @Assignee, @CreatedAt, @RelatedArtifactsRaw, @RelatedPagesRaw, @SourceProject,
             @SourceLastSuccessfulRefreshAt, @SourceContentRevision, @HydratedAt, @HydrationStatus, @HydrationReason,
             @PublicDisplayNamePolicyVersion)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        AddNullable(command, "@Priority", row.Priority);
        AddNullable(command, "@Resolution", row.Resolution);
        AddNullable(command, "@ResolutionDescriptionPlain", row.ResolutionDescriptionPlain);
        AddNullable(command, "@Specification", row.Specification);
        AddNullable(command, "@RaisedInVersion", row.RaisedInVersion);
        AddNullable(command, "@SelectedBallot", row.SelectedBallot);
        AddNullable(command, "@ChangeCategory", row.ChangeCategory);
        AddNullable(command, "@Impact", row.Impact);
        AddNullable(command, "@Labels", row.Labels);
        AddNullable(command, "@CommentCount", row.CommentCount);
        AddNullable(command, "@DescriptionPlain", row.DescriptionPlain);
        AddNullable(command, "@DescriptionHtml", row.DescriptionHtml);
        AddNullable(command, "@ResolutionDescriptionHtml", row.ResolutionDescriptionHtml);
        AddNullable(command, "@Reporter", row.Reporter);
        AddNullable(command, "@Assignee", row.Assignee);
        AddNullable(command, "@CreatedAt", row.CreatedAt.HasValue ? Format(row.CreatedAt.Value) : null);
        AddNullable(command, "@RelatedArtifactsRaw", row.RelatedArtifactsRaw);
        AddNullable(command, "@RelatedPagesRaw", row.RelatedPagesRaw);
        AddNullable(command, "@SourceProject", row.SourceProject);
        AddNullable(
            command,
            "@SourceLastSuccessfulRefreshAt",
            row.SourceLastSuccessfulRefreshAt.HasValue
                ? Format(row.SourceLastSuccessfulRefreshAt.Value)
                : null);
        AddNullable(
            command,
            "@SourceContentRevision",
            row.SourceContentRevision);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        AddNullable(
            command,
            "@PublicDisplayNamePolicyVersion",
            row.PublicDisplayNamePolicyVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceCanonicalJiraFieldsAsync(
        SqliteConnection connection,
        PreparedTicketHydrationRow row,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            "DELETE FROM prepared_ticket_jira_content WHERE TicketKey = @ticketKey",
            ct,
            ("@ticketKey", row.TicketKey));
        await ExecuteAsync(
            connection,
            "DELETE FROM prepared_ticket_artifacts WHERE TicketKey = @ticketKey",
            ct,
            ("@ticketKey", row.TicketKey));
        await ExecuteAsync(
            connection,
            "DELETE FROM prepared_ticket_pages WHERE TicketKey = @ticketKey",
            ct,
            ("@ticketKey", row.TicketKey));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_jira_content(TicketKey, DescriptionHtml, ResolutionDescriptionHtml)
            VALUES(@ticketKey, @descriptionHtml, @resolutionDescriptionHtml)
            """,
            ct,
            ("@ticketKey", row.TicketKey),
            ("@descriptionHtml", row.DescriptionHtml),
            ("@resolutionDescriptionHtml", row.ResolutionDescriptionHtml));

        foreach (string artifact in SplitRelatedValues(row.RelatedArtifactsRaw))
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO prepared_ticket_artifacts(TicketKey, Value) VALUES(@ticketKey, @value)",
                ct,
                ("@ticketKey", row.TicketKey),
                ("@value", artifact));
        }
        foreach (string page in SplitRelatedValues(row.RelatedPagesRaw))
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO prepared_ticket_pages(TicketKey, Value) VALUES(@ticketKey, @value)",
                ct,
                ("@ticketKey", row.TicketKey),
                ("@value", page));
        }
    }

    private static IReadOnlyList<string> SplitRelatedValues(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                    [',', ';'],
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static async Task InsertJiraHydrationAsync(SqliteConnection connection, PreparedJiraHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain,
             WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, DescriptionHtml, ResolutionDescriptionHtml,
             Reporter, Assignee, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw, HydratedAt, HydrationStatus, HydrationReason,
             PublicDisplayNamePolicyVersion)
            VALUES
            (@Id, @TicketKey, @JiraKey, @Title, @Status, @Type, @Priority, @Resolution, @ResolutionDescriptionPlain,
             @WorkGroup, @WorkGroupClean, @Specification, @UpdatedAt, @Url, @DescriptionHtml, @ResolutionDescriptionHtml,
             @Reporter, @Assignee, @CreatedAt, @RelatedArtifactsRaw, @RelatedPagesRaw, @HydratedAt, @HydrationStatus, @HydrationReason,
             @PublicDisplayNamePolicyVersion)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@JiraKey", row.JiraKey);
        AddNullable(command, "@Title", row.Title);
        AddNullable(command, "@Status", row.Status);
        AddNullable(command, "@Type", row.Type);
        AddNullable(command, "@Priority", row.Priority);
        AddNullable(command, "@Resolution", row.Resolution);
        AddNullable(command, "@ResolutionDescriptionPlain", row.ResolutionDescriptionPlain);
        AddNullable(command, "@WorkGroup", row.WorkGroup);
        string workGroupCleanRaw = Hl7WorkGroupNameCleaner.Clean(row.WorkGroup);
        AddNullable(command, "@WorkGroupClean", string.IsNullOrEmpty(workGroupCleanRaw) ? null : workGroupCleanRaw);
        AddNullable(command, "@Specification", row.Specification);
        AddNullable(command, "@UpdatedAt", row.UpdatedAt.HasValue ? Format(row.UpdatedAt.Value) : null);
        AddNullable(command, "@Url", row.Url);
        AddNullable(command, "@DescriptionHtml", row.DescriptionHtml);
        AddNullable(command, "@ResolutionDescriptionHtml", row.ResolutionDescriptionHtml);
        AddNullable(command, "@Reporter", row.Reporter);
        AddNullable(command, "@Assignee", row.Assignee);
        AddNullable(command, "@CreatedAt", row.CreatedAt.HasValue ? Format(row.CreatedAt.Value) : null);
        AddNullable(command, "@RelatedArtifactsRaw", row.RelatedArtifactsRaw);
        AddNullable(command, "@RelatedPagesRaw", row.RelatedPagesRaw);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        AddNullable(
            command,
            "@PublicDisplayNamePolicyVersion",
            row.PublicDisplayNamePolicyVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertZulipHydrationAsync(SqliteConnection connection, PreparedZulipHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_zulip_hydration
            (Id, TicketKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount, FirstMessageAt, LastMessageAt,
             FirstMessageExcerpt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @ZulipThreadId, @StreamId, @StreamName, @Topic, @MessageCount, @FirstMessageAt, @LastMessageAt,
             @FirstMessageExcerpt, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@ZulipThreadId", row.ZulipThreadId);
        AddNullable(command, "@StreamId", row.StreamId);
        AddNullable(command, "@StreamName", row.StreamName);
        AddNullable(command, "@Topic", row.Topic);
        AddNullable(command, "@MessageCount", row.MessageCount);
        AddNullable(command, "@FirstMessageAt", row.FirstMessageAt.HasValue ? Format(row.FirstMessageAt.Value) : null);
        AddNullable(command, "@LastMessageAt", row.LastMessageAt.HasValue ? Format(row.LastMessageAt.Value) : null);
        AddNullable(command, "@FirstMessageExcerpt", row.FirstMessageExcerpt);
        AddNullable(command, "@Url", row.Url);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertGitHubHydrationAsync(SqliteConnection connection, PreparedGitHubHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_github_hydration
            (Id, TicketKey, GitHubItemId, Owner, Repo, Number, Path, Title, State, IsPullRequest, Labels, UpdatedAt, Url,
             HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @GitHubItemId, @Owner, @Repo, @Number, @Path, @Title, @State, @IsPullRequest, @Labels, @UpdatedAt, @Url,
             @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@GitHubItemId", row.GitHubItemId);
        AddNullable(command, "@Owner", row.Owner);
        AddNullable(command, "@Repo", row.Repo);
        AddNullable(command, "@Number", row.Number);
        AddNullable(command, "@Path", row.Path);
        AddNullable(command, "@Title", row.Title);
        AddNullable(command, "@State", row.State);
        AddNullable(command, "@IsPullRequest", row.IsPullRequest.HasValue ? (row.IsPullRequest.Value ? 1 : 0) : (object?)null);
        AddNullable(command, "@Labels", row.Labels);
        AddNullable(command, "@UpdatedAt", row.UpdatedAt.HasValue ? Format(row.UpdatedAt.Value) : null);
        AddNullable(command, "@Url", row.Url);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertRepoHydrationAsync(SqliteConnection connection, PreparedRepoHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_repo_hydration
            (Id, TicketKey, Repo, Description, WorkGroup, Specification, CategoryDetail, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @Repo, @Description, @WorkGroup, @Specification, @CategoryDetail, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@Repo", row.Repo);
        AddNullable(command, "@Description", row.Description);
        AddNullable(command, "@WorkGroup", row.WorkGroup);
        AddNullable(command, "@Specification", row.Specification);
        AddNullable(command, "@CategoryDetail", row.CategoryDetail);
        AddNullable(command, "@Url", row.Url);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertJiraXrefAsync(SqliteConnection connection, PreparedTicketJiraXrefRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_jira_xref (Id, TicketKey, JiraKey, Source)
            VALUES (@Id, @TicketKey, @JiraKey, @Source)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@JiraKey", row.JiraKey);
        command.Parameters.AddWithValue("@Source", row.Source);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PreparedTicketHydrationRow?> ReadHydrationParentAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, Priority, Resolution, ResolutionDescriptionPlain, Specification, RaisedInVersion, SelectedBallot,
                   ChangeCategory, Impact, Labels, CommentCount, DescriptionPlain, DescriptionHtml, ResolutionDescriptionHtml,
                   Reporter, Assignee, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw, SourceProject,
                   SourceLastSuccessfulRefreshAt, SourceContentRevision, HydratedAt, HydrationStatus, HydrationReason,
                   PublicDisplayNamePolicyVersion
            FROM prepared_ticket_hydration WHERE TicketKey = @key
            """;
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        int? policyVersion =
            reader.IsDBNull(25) ? null : reader.GetInt32(25);
        return new PreparedTicketHydrationRow(
            TicketKey: reader.GetString(0),
            Priority: ReadNullableString(reader, 1),
            Resolution: ReadNullableString(reader, 2),
            ResolutionDescriptionPlain: ReadNullableString(reader, 3),
            Specification: ReadNullableString(reader, 4),
            RaisedInVersion: ReadNullableString(reader, 5),
            SelectedBallot: ReadNullableString(reader, 6),
            ChangeCategory: ReadNullableString(reader, 7),
            Impact: ReadNullableString(reader, 8),
            Labels: ReadNullableString(reader, 9),
            CommentCount: reader.IsDBNull(10) ? null : reader.GetInt32(10),
            DescriptionPlain: ReadNullableString(reader, 11),
            HydratedAt: ParseDate(reader.GetString(22)),
            HydrationStatus: reader.GetString(23),
            HydrationReason: ReadNullableString(reader, 24),
            DescriptionHtml: ReadNullableString(reader, 12),
            ResolutionDescriptionHtml: ReadNullableString(reader, 13),
            Reporter: NormalizeTrustedDisplayName(
                ReadNullableString(reader, 14),
                policyVersion),
            CreatedAt: reader.IsDBNull(16) ? null : ParseDate(reader.GetString(16)),
            RelatedArtifactsRaw: ReadNullableString(reader, 17),
            RelatedPagesRaw: ReadNullableString(reader, 18),
            Assignee: NormalizeTrustedDisplayName(
                ReadNullableString(reader, 15),
                policyVersion),
            SourceProject: ReadNullableString(reader, 19),
            SourceLastSuccessfulRefreshAt: reader.IsDBNull(20)
                ? null
                : ParseDate(reader.GetString(20)),
            SourceContentRevision: reader.IsDBNull(21)
                ? null
                : reader.GetInt64(21),
            PublicDisplayNamePolicyVersion:
                NormalizePolicyVersion(policyVersion));
    }

    private static async Task<IReadOnlyList<PreparedJiraHydrationRow>> ReadJiraHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain,
                   WorkGroup, Specification, UpdatedAt, Url, DescriptionHtml, ResolutionDescriptionHtml,
                   Reporter, Assignee, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw, HydratedAt, HydrationStatus, HydrationReason,
                   PublicDisplayNamePolicyVersion
            FROM prepared_jira_hydration WHERE TicketKey = @key ORDER BY JiraKey
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedJiraHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int? policyVersion =
                reader.IsDBNull(22) ? null : reader.GetInt32(22);
            rows.Add(new PreparedJiraHydrationRow(
                TicketKey: reader.GetString(0),
                JiraKey: reader.GetString(1),
                Title: ReadNullableString(reader, 2),
                Status: ReadNullableString(reader, 3),
                Type: ReadNullableString(reader, 4),
                Priority: ReadNullableString(reader, 5),
                Resolution: ReadNullableString(reader, 6),
                ResolutionDescriptionPlain: ReadNullableString(reader, 7),
                WorkGroup: ReadNullableString(reader, 8),
                Specification: ReadNullableString(reader, 9),
                UpdatedAt: reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
                Url: ReadNullableString(reader, 11),
                HydratedAt: ParseDate(reader.GetString(19)),
                HydrationStatus: reader.GetString(20),
                HydrationReason: ReadNullableString(reader, 21),
                DescriptionHtml: ReadNullableString(reader, 12),
                ResolutionDescriptionHtml: ReadNullableString(reader, 13),
                Reporter: NormalizeTrustedDisplayName(
                    ReadNullableString(reader, 14),
                    policyVersion),
                CreatedAt: reader.IsDBNull(16) ? null : ParseDate(reader.GetString(16)),
                RelatedArtifactsRaw: ReadNullableString(reader, 17),
                RelatedPagesRaw: ReadNullableString(reader, 18),
                Assignee: NormalizeTrustedDisplayName(
                    ReadNullableString(reader, 15),
                    policyVersion),
                PublicDisplayNamePolicyVersion:
                    NormalizePolicyVersion(policyVersion)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedZulipHydrationRow>> ReadZulipHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount, FirstMessageAt, LastMessageAt,
                   FirstMessageExcerpt, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_zulip_hydration WHERE TicketKey = @key ORDER BY ZulipThreadId
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedZulipHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedZulipHydrationRow(
                TicketKey: reader.GetString(0),
                ZulipThreadId: reader.GetString(1),
                StreamId: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                StreamName: ReadNullableString(reader, 3),
                Topic: ReadNullableString(reader, 4),
                MessageCount: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                FirstMessageAt: reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
                LastMessageAt: reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
                FirstMessageExcerpt: ReadNullableString(reader, 8),
                Url: ReadNullableString(reader, 9),
                HydratedAt: ParseDate(reader.GetString(10)),
                HydrationStatus: reader.GetString(11),
                HydrationReason: ReadNullableString(reader, 12)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedGitHubHydrationRow>> ReadGitHubHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, GitHubItemId, Owner, Repo, Number, Path, Title, State, IsPullRequest, Labels, UpdatedAt, Url,
                   HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_github_hydration WHERE TicketKey = @key ORDER BY GitHubItemId
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedGitHubHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedGitHubHydrationRow(
                TicketKey: reader.GetString(0),
                GitHubItemId: reader.GetString(1),
                Owner: ReadNullableString(reader, 2),
                Repo: ReadNullableString(reader, 3),
                Number: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Path: ReadNullableString(reader, 5),
                Title: ReadNullableString(reader, 6),
                State: ReadNullableString(reader, 7),
                IsPullRequest: reader.IsDBNull(8) ? null : reader.GetInt32(8) != 0,
                Labels: ReadNullableString(reader, 9),
                UpdatedAt: reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
                Url: ReadNullableString(reader, 11),
                HydratedAt: ParseDate(reader.GetString(12)),
                HydrationStatus: reader.GetString(13),
                HydrationReason: ReadNullableString(reader, 14)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedRepoHydrationRow>> ReadRepoHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, Repo, Description, WorkGroup, Specification, CategoryDetail, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_repo_hydration WHERE TicketKey = @key ORDER BY Repo
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedRepoHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedRepoHydrationRow(
                TicketKey: reader.GetString(0),
                Repo: reader.GetString(1),
                Description: ReadNullableString(reader, 2),
                WorkGroup: ReadNullableString(reader, 3),
                Specification: ReadNullableString(reader, 4),
                CategoryDetail: ReadNullableString(reader, 5),
                Url: ReadNullableString(reader, 6),
                HydratedAt: ParseDate(reader.GetString(7)),
                HydrationStatus: reader.GetString(8),
                HydrationReason: ReadNullableString(reader, 9)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedTicketJiraXrefRow>> ReadJiraXrefAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT TicketKey, JiraKey, Source FROM prepared_ticket_jira_xref WHERE TicketKey = @key ORDER BY Source, JiraKey";
        command.Parameters.AddWithValue("@key", key);
        List<PreparedTicketJiraXrefRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedTicketJiraXrefRow(
                TicketKey: reader.GetString(0),
                JiraKey: reader.GetString(1),
                Source: reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedTicketInPersonRequesterRow>>
        ReadInPersonRequestersAsync(
            SqliteConnection connection,
            string key,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, DisplayName, PublicDisplayNamePolicyVersion
            FROM prepared_ticket_in_person_requesters
            WHERE TicketKey = @key COLLATE NOCASE
            ORDER BY DisplayName COLLATE NOCASE, DisplayName
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedTicketInPersonRequesterRow> rows = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int? policyVersion =
                reader.IsDBNull(2) ? null : reader.GetInt32(2);
            string? displayName = NormalizeTrustedDisplayName(
                reader.GetString(1),
                policyVersion);
            if (displayName is null || !seen.Add(displayName))
            {
                continue;
            }
            rows.Add(new PreparedTicketInPersonRequesterRow(
                reader.GetString(0),
                displayName,
                PublicDisplayNamePolicy.CurrentVersion));
        }
        return rows;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddNullable(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public async Task<PreparedTicketDetail?> GetPreparedTicketAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PreparedTicketSummary? summary = await GetSummaryAsync(connection, key, ct);
        if (summary is null)
        {
            return null;
        }

        PreparedTicketRelatedItems relatedItems = await GetRelatedItemsAsync(connection, key, ct);
        return new PreparedTicketDetail(summary, relatedItems);
    }

    public async Task<PreparedTicketRelatedItems> GetPreparedTicketRelatedItemsAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await GetRelatedItemsAsync(connection, key, ct);
    }

    public async Task<IReadOnlyList<PreparedTicketSummary>> ListPreparedTicketsAsync(PreparedTicketQueryFilter filter, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        List<string> where = [];
        AddOptional(command, where, "Recommendation = @recommendation", "@recommendation", filter.Recommendation);
        if (!string.IsNullOrWhiteSpace(filter.Impact))
        {
            where.Add("(ProposalAImpact = @impact OR ProposalBImpact = @impact)");
            command.Parameters.AddWithValue("@impact", filter.Impact);
        }

        AddExists(command, where, "prepared_ticket_repos", "Repo = @repo", "@repo", filter.Repo);
        AddExists(command, where, "prepared_ticket_repos", "RepoCategory = @repoCategory", "@repoCategory", filter.RepoCategory);
        AddExists(command, where, "prepared_ticket_related_jira", "AssociatedTicketKey = @relatedJiraKey", "@relatedJiraKey", filter.RelatedJiraKey);
        AddExists(command, where, "prepared_ticket_related_github", "GitHubItemId = @githubItemId", "@githubItemId", filter.GitHubItemId);
        AddExists(command, where, "prepared_ticket_related_zulip", "ZulipThreadId = @zulipThreadId", "@zulipThreadId", filter.ZulipThreadId);
        string whereSql = where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
        command.CommandText = $"""
            SELECT Key, RequestSummary, ProposalAImpact, ProposalBImpact, Recommendation, RecommendationJustification, SavedAt
            FROM prepared_tickets
            {whereSql}
            ORDER BY SavedAt DESC, Key ASC
            LIMIT @limit OFFSET @offset
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(filter.Limit, 1, 500));
        command.Parameters.AddWithValue("@offset", Math.Max(0, filter.Offset));
        List<PreparedTicketSummary> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadSummary(reader));
        }

        return rows;
    }

    private static async Task SavePreparedTicketCoreAsync(
        SqliteConnection connection,
        PreparedTicketPayload payload,
        DateTimeOffset savedAt,
        CancellationToken ct)
    {
        await DeleteRowsAsync(connection, payload.Key, ct);
        await InsertParentAsync(connection, payload, savedAt, ct);
        foreach (PreparedTicketRepoPayload repo in payload.Repos)
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO prepared_ticket_repos (Id, TicketKey, Repo, RepoCategory, Justification) VALUES (@id, @key, @repo, @category, @justification)",
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@repo", repo.Repo),
                ("@category", repo.RepoCategory),
                ("@justification", repo.Justification));
        }

        foreach (PreparedTicketRelatedJiraPayload related in payload.RelatedJiraTickets)
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO prepared_ticket_related_jira (Id, TicketKey, AssociatedTicketKey, LinkType, Justification) VALUES (@id, @key, @associated, @linkType, @justification)",
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@associated", related.AssociatedTicketKey),
                ("@linkType", related.LinkType),
                ("@justification", related.Justification));
        }

        foreach (PreparedTicketRelatedZulipPayload related in payload.RelatedZulipThreads)
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO prepared_ticket_related_zulip (Id, TicketKey, ZulipThreadId, Justification) VALUES (@id, @key, @thread, @justification)",
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@thread", related.ZulipThreadId),
                ("@justification", related.Justification));
        }

        foreach (PreparedTicketRelatedGitHubPayload related in payload.RelatedGitHubItems)
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO prepared_ticket_related_github (Id, TicketKey, GitHubItemId, Justification) VALUES (@id, @key, @item, @justification)",
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@item", related.GitHubItemId),
                ("@justification", related.Justification));
        }
    }

    private static async Task<string> ComputePreparedGraphHashAsync(
        SqliteConnection connection,
        string key,
        CancellationToken ct)
    {
        StringBuilder builder = new();
        string[] queries =
        [
            """
            SELECT Key, RequestSummary, CommentSummary, LinkedTicketSummary, RelatedTicketSummary,
                   RelatedZulipSummary, RelatedGitHubSummary, ExistingProposed, ProposalA,
                   ProposalAJustification, ProposalAImpact, ProposalB, ProposalBJustification,
                   ProposalBImpact, ProposalC, ProposalCJustification, Recommendation,
                   RecommendationJustification
            FROM prepared_tickets WHERE Key = @key
            """,
            "SELECT Repo, RepoCategory, Justification FROM prepared_ticket_repos WHERE TicketKey = @key ORDER BY Repo, RepoCategory, Justification",
            "SELECT AssociatedTicketKey, LinkType, Justification FROM prepared_ticket_related_jira WHERE TicketKey = @key ORDER BY AssociatedTicketKey, LinkType, Justification",
            "SELECT ZulipThreadId, Justification FROM prepared_ticket_related_zulip WHERE TicketKey = @key ORDER BY ZulipThreadId, Justification",
            "SELECT GitHubItemId, Justification FROM prepared_ticket_related_github WHERE TicketKey = @key ORDER BY GitHubItemId, Justification",
        ];
        foreach (string sql in queries)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@key", key);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    builder.Append(reader.IsDBNull(ordinal) ? "<null>" : reader.GetValue(ordinal));
                    builder.Append('\u001f');
                }
                builder.Append('\n');
            }
        }
        return AuthoringResultHasher.HashNormalizedUtf8(builder.ToString());
    }

    private static async Task DeleteRowsAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        foreach (string table in new[] { "prepared_ticket_repos", "prepared_ticket_related_jira", "prepared_ticket_related_zulip", "prepared_ticket_related_github" })
        {
            await ExecuteAsync(connection, $"DELETE FROM {table} WHERE TicketKey = @key", ct, ("@key", key));
        }

        // Grouping cascade: a per-ticket overwrite removes the ticket's
        // grouping-member rows so it cannot remain pinned to a Topic / Linked
        // Ticket Group that no longer represents its current state. Topic and
        // group rows are intentionally left in place — source guidance is
        // "re-runs replace, do not merge"; the next per-partition PUT will
        // overwrite stale topic rows wholesale.
        await ExecuteAsync(connection, "DELETE FROM prepared_ticket_topic_members WHERE TicketKey = @key", ct, ("@key", key));

        await ExecuteAsync(connection, "DELETE FROM prepared_tickets WHERE Key = @key", ct, ("@key", key));
    }

    private static async Task InsertParentAsync(SqliteConnection connection, PreparedTicketPayload payload, DateTimeOffset savedAt, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_tickets
            (Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary, RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary, ExistingProposed,
             ProposalA, ProposalAJustification, ProposalAImpact, ProposalB, ProposalBJustification, ProposalBImpact, ProposalC, ProposalCJustification,
             Recommendation, RecommendationJustification, SavedAt)
            VALUES
            (@Id, @Key, @RequestSummary, @CommentSummary, @LinkedTicketSummary, @RelatedTicketSummary, @RelatedZulipSummary, @RelatedGitHubSummary, @ExistingProposed,
             @ProposalA, @ProposalAJustification, @ProposalAImpact, @ProposalB, @ProposalBJustification, @ProposalBImpact, @ProposalC, @ProposalCJustification,
             @Recommendation, @RecommendationJustification, @SavedAt)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@Key", payload.Key);
        command.Parameters.AddWithValue("@RequestSummary", payload.RequestSummary);
        command.Parameters.AddWithValue("@CommentSummary", payload.CommentSummary);
        command.Parameters.AddWithValue("@LinkedTicketSummary", payload.LinkedTicketSummary);
        command.Parameters.AddWithValue("@RelatedTicketSummary", payload.RelatedTicketSummary);
        command.Parameters.AddWithValue("@RelatedZulipSummary", payload.RelatedZulipSummary);
        command.Parameters.AddWithValue("@RelatedGitHubSummary", payload.RelatedGitHubSummary);
        command.Parameters.AddWithValue("@ExistingProposed", payload.ExistingProposed);
        command.Parameters.AddWithValue("@ProposalA", payload.ProposalA);
        command.Parameters.AddWithValue("@ProposalAJustification", payload.ProposalAJustification);
        command.Parameters.AddWithValue("@ProposalAImpact", payload.ProposalAImpact);
        command.Parameters.AddWithValue("@ProposalB", payload.ProposalB);
        command.Parameters.AddWithValue("@ProposalBJustification", payload.ProposalBJustification);
        command.Parameters.AddWithValue("@ProposalBImpact", payload.ProposalBImpact);
        command.Parameters.AddWithValue("@ProposalC", payload.ProposalC);
        command.Parameters.AddWithValue("@ProposalCJustification", payload.ProposalCJustification);
        command.Parameters.AddWithValue("@Recommendation", payload.Recommendation);
        command.Parameters.AddWithValue("@RecommendationJustification", payload.RecommendationJustification);
        command.Parameters.AddWithValue("@SavedAt", Format(savedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PreparedTicketSummary?> GetSummaryAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Key, RequestSummary, ProposalAImpact, ProposalBImpact, Recommendation, RecommendationJustification, SavedAt FROM prepared_tickets WHERE Key = @key";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadSummary(reader);
        }

        return null;
    }

    private static async Task<PreparedTicketRelatedItems> GetRelatedItemsAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        List<PreparedTicketRepoItem> repos = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT Repo, RepoCategory, Justification FROM prepared_ticket_repos WHERE TicketKey = @key ORDER BY Repo", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                repos.Add(new PreparedTicketRepoItem(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        List<PreparedTicketRelatedJiraItem> jira = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT AssociatedTicketKey, LinkType, Justification FROM prepared_ticket_related_jira WHERE TicketKey = @key ORDER BY AssociatedTicketKey", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                jira.Add(new PreparedTicketRelatedJiraItem(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        List<PreparedTicketRelatedZulipItem> zulip = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT ZulipThreadId, Justification FROM prepared_ticket_related_zulip WHERE TicketKey = @key ORDER BY ZulipThreadId", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                zulip.Add(new PreparedTicketRelatedZulipItem(reader.GetString(0), reader.GetString(1)));
            }
        }

        List<PreparedTicketRelatedGitHubItem> github = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT GitHubItemId, Justification FROM prepared_ticket_related_github WHERE TicketKey = @key ORDER BY GitHubItemId", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                github.Add(new PreparedTicketRelatedGitHubItem(reader.GetString(0), reader.GetString(1)));
            }
        }

        return new PreparedTicketRelatedItems(repos, jira, zulip, github);
    }

    private static SqliteCommand SelectChildren(SqliteConnection connection, string sql, string key)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        return command;
    }

    private static PreparedTicketSummary ReadSummary(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static void AddOptional(SqliteCommand command, List<string> where, string expression, string parameter, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            where.Add(expression);
            command.Parameters.AddWithValue(parameter, value);
        }
    }

    private static void AddExists(SqliteCommand command, List<string> where, string table, string expression, string parameter, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            where.Add($"EXISTS (SELECT 1 FROM {table} child WHERE child.TicketKey = prepared_tickets.Key AND {expression})");
            command.Parameters.AddWithValue(parameter, value);
        }
    }

    private static void ValidatePublicationReconciliationGroupingStageContext(
        PreparedTicketGroupingStageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.StageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.StageLeaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.InputFingerprint);
        if (!context.HasCompleteReconciliationContext())
        {
            throw new ArgumentException(
                "Complete reconciliation grouping stage context is required.",
                nameof(context));
        }
        if (!string.Equals(
                context.InputFingerprint,
                context.OverlayCorpusFingerprint,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "The reconciliation grouping input fingerprint does not match the candidate overlay.");
        }
        IReadOnlyList<string> revisedTicketKeys =
            context.RevisedTicketKeys!;
        IReadOnlyList<string> ticketKeys = context.TicketKeys!;
        if (revisedTicketKeys.Any(string.IsNullOrWhiteSpace) ||
            ticketKeys.Any(string.IsNullOrWhiteSpace) ||
            revisedTicketKeys
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            revisedTicketKeys.Count ||
            ticketKeys
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            ticketKeys.Count)
        {
            throw new ArgumentException(
                "Reconciliation grouping stage membership must contain non-empty unique ticket keys.",
                nameof(context));
        }
    }

    private static async Task EnsurePublicationReconciliationGroupingStageAsync(
        SqliteConnection connection,
        string runId,
        string partitionKey,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM authoring_runs run
            INNER JOIN prepared_ticket_publication_reconciliations reconciliation
                ON reconciliation.RunId = run.Id
            INNER JOIN prepared_ticket_publication_grouping_impacts impact
                ON impact.RunId = run.Id
               AND impact.PartitionKey = @partitionKey
            WHERE run.Id = @runId
              AND run.Purpose = @purpose
              AND reconciliation.PromotionState = @staged
              AND EXISTS(
                  SELECT 1
                  FROM authoring_mutation_fences fence
                  WHERE fence.ProcessorKind = run.ProcessorKind
                    AND fence.RunId = run.Id)
              AND EXISTS(
                  SELECT 1
                  FROM prepared_ticket_publication_reconciliation_fences fence
                  WHERE fence.RunId = run.Id)
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@purpose",
            PreparedTicketPublicationReconciliationContract.Purpose);
        command.Parameters.AddWithValue(
            "@staged",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Staged);
        command.Parameters.AddWithValue("@partitionKey", partitionKey);
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Grouping partition '{partitionKey}' is not an impacted partition of reconciliation '{runId}'.");
        }
    }

    private static async Task
        EnsurePublicationReconciliationWorkspaceWritableAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string runId,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM prepared_ticket_publication_reconciliations reconciliation
            INNER JOIN authoring_runs run
              ON run.Id = reconciliation.RunId
            INNER JOIN authoring_mutation_fences mutationFence
              ON mutationFence.ProcessorKind = run.ProcessorKind
             AND mutationFence.RunId = run.Id
            INNER JOIN prepared_ticket_publication_reconciliation_fences
                reconciliationFence
              ON reconciliationFence.RunId = run.Id
            WHERE reconciliation.RunId = @runId
              AND reconciliation.PromotionState = @staged
              AND run.Purpose = @purpose
              AND run.Status IN (@running, @finalizing, @error)
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@staged",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Staged);
        command.Parameters.AddWithValue(
            "@purpose",
            PreparedTicketPublicationReconciliationContract.Purpose);
        command.Parameters.AddWithValue(
            "@running",
            AuthoringStatusValues.Runs.Running);
        command.Parameters.AddWithValue(
            "@finalizing",
            AuthoringStatusValues.Runs.Finalizing);
        command.Parameters.AddWithValue(
            "@error",
            AuthoringStatusValues.Runs.Error);
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' workspace is no longer writable.");
        }
    }

    private static async Task EnsureStageLeaseAsync(
        SqliteConnection connection,
        string runId,
        string stageId,
        string stageLeaseId,
        string inputFingerprint,
        string stageName,
        string partitionKey,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM authoring_run_stages
            WHERE Id = @stageId
              AND RunId = @runId
              AND LeaseId = @leaseId
              AND InputFingerprint = @inputFingerprint
              AND StageName = @stageName
              AND PartitionKey = @partitionKey
              AND Status = @status
              AND EXISTS (
                  SELECT 1 FROM authoring_mutation_fences f
                  WHERE f.ProcessorKind = @processorKind AND f.RunId = @runId
              )
            """;
        command.Parameters.AddWithValue("@stageId", stageId);
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@leaseId", stageLeaseId);
        command.Parameters.AddWithValue("@inputFingerprint", inputFingerprint);
        command.Parameters.AddWithValue("@stageName", stageName);
        command.Parameters.AddWithValue("@partitionKey", partitionKey);
        command.Parameters.AddWithValue("@status", AuthoringStatusValues.Stages.InProgress);
        command.Parameters.AddWithValue("@processorKind", AuthoringProcessorKind);
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageLeaseLost,
                $"Authoring stage '{stageId}' no longer owns partition input '{inputFingerprint}'.");
        }
    }

    private static async Task EnsureLegacyAuthoringModeAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Mode
            FROM authoring_processor_modes
            WHERE ProcessorKind = @processorKind
            """;
        command.Parameters.AddWithValue(
            "@processorKind",
            AuthoringProcessorKind);
        string? mode = Convert.ToString(await command.ExecuteScalarAsync(ct));
        if (string.IsNullOrWhiteSpace(mode) ||
            mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            return;
        }
        throw new AuthoringConflictException(
            mode == AuthoringStatusValues.ProcessorModes.CuttingOver
                ? AuthoringConflictCode.CutoverInProgress
                : AuthoringConflictCode.AuthoringNotActivated,
            $"Legacy prepared-ticket writes are unavailable while processor mode is '{mode}'.");
    }

    private static async Task EnsureGeneralMutationAllowedAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        string? fenceRunId = await ReadFenceRunIdAsync(connection, ct);
        if (fenceRunId is null ||
            fenceRunId.StartsWith("maintenance:", StringComparison.Ordinal))
        {
            return;
        }
        throw new AuthoringConflictException(
            AuthoringConflictCode.MutationFenceUnavailable,
            $"Mutation is fenced by authoring run '{fenceRunId}'.");
    }

    private static async Task EnsureTicketMutationAllowedAsync(
        SqliteConnection connection,
        string ticketKey,
        CancellationToken ct)
    {
        string? fenceRunId = await ReadFenceRunIdAsync(connection, ct);
        if (fenceRunId is null ||
            fenceRunId.StartsWith("maintenance:", StringComparison.Ordinal))
        {
            return;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM authoring_run_items item
            INNER JOIN authoring_runs run ON run.Id = item.RunId
            WHERE item.RunId = @runId
              AND item.BusinessKey COLLATE NOCASE = @ticketKey
              AND item.AcceptedReceiptId IS NOT NULL
              AND run.Purpose <> @publicationRefreshPurpose
            """;
        command.Parameters.AddWithValue("@runId", fenceRunId);
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
        command.Parameters.AddWithValue(
            "@publicationRefreshPurpose",
            AuthoringRunPurposeValues.PublicationRefresh);
        if (await command.ExecuteScalarAsync(ct) is not null)
        {
            return;
        }

        throw new AuthoringConflictException(
            AuthoringConflictCode.MutationFenceUnavailable,
            $"Ticket '{ticketKey}' is fenced by authoring run '{fenceRunId}'.");
    }

    private static async Task<string?> ReadFenceRunIdAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT RunId FROM authoring_mutation_fences WHERE ProcessorKind = @processorKind";
        command.Parameters.AddWithValue("@processorKind", AuthoringProcessorKind);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    public async Task<string> ComputeGroupingOutputFingerprintAsync(
        string partitionKey,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ComputeGroupingOutputFingerprintAsync(
            connection,
            partitionKey,
            ct);
    }

    public async Task<PreparedTicketPublicationReconciliationComparison?>
        GetPublicationReconciliationComparisonAsync(
            string runId,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ComparisonJson
            FROM prepared_ticket_publication_reconciliations
            WHERE RunId = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        string? json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null
            ? null
            : JsonSerializer.Deserialize<
                PreparedTicketPublicationReconciliationComparison>(json)
              ?? throw new InvalidOperationException(
                  $"Reconciliation '{runId}' has an invalid comparison.");
    }

    public async Task<bool>
        IsPublicationReconciliationStagingCompleteAsync(
            string runId,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using SqliteConnection connection = OpenConnection();
        return await IsPublicationReconciliationStagingCompleteAsync(
            connection,
            transaction: null,
            runId,
            ct);
    }

    public async Task<PreparedTicketPublicationStagedTicket>
        StagePublicationReconciliationTicketAsync(
            string runId,
            string runItemId,
            string operationId,
            string receiptId,
            string sourceRevision,
            string authoredFingerprint,
            PreparedTicketPayload payload,
            PreparedTicketHydrationBatch hydration,
            DateTimeOffset? now = null,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoredFingerprint);
        PreparedTicketPayloadValidator.ThrowIfInvalid(payload);
        ArgumentNullException.ThrowIfNull(hydration);
        ValidateStagedHydration(payload.Key, hydration);

        DateTimeOffset stagedAt = now ?? DateTimeOffset.UtcNow;
        string payloadJson = JsonSerializer.Serialize(payload);
        string hydrationJson = JsonSerializer.Serialize(hydration);
        string hydrationFingerprint =
            AuthoringResultHasher.HashNormalizedUtf8(hydrationJson);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            PreparedTicketPublicationStagedTicket staged =
                await StagePublicationReconciliationTicketAsync(
                    connection, transaction, runId, runItemId, operationId,
                    receiptId, sourceRevision, authoredFingerprint, payload,
                    hydration, stagedAt, ct);
            await transaction.CommitAsync(ct);
            return staged;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<PreparedTicketPublicationStagedTicket>
        StagePublicationReconciliationTicketAsync(
            SqliteConnection connection,
            string runId,
            string runItemId,
            string operationId,
            string receiptId,
            string sourceRevision,
            string authoredFingerprint,
            PreparedTicketPayload payload,
            HydrationBatch hydration,
            DateTimeOffset? now = null,
            CancellationToken ct = default)
        => await StagePublicationReconciliationTicketAsync(
            connection,
            transaction: null,
            runId,
            runItemId,
            operationId,
            receiptId,
            sourceRevision,
            authoredFingerprint,
            payload,
            ToPreparedHydrationBatch(hydration),
            now ?? DateTimeOffset.UtcNow,
            ct);

    private static async Task<PreparedTicketPublicationStagedTicket>
        StagePublicationReconciliationTicketAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string runId,
            string runItemId,
            string operationId,
            string receiptId,
            string sourceRevision,
            string authoredFingerprint,
            PreparedTicketPayload payload,
            PreparedTicketHydrationBatch hydration,
            DateTimeOffset stagedAt,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoredFingerprint);
        PreparedTicketPayloadValidator.ThrowIfInvalid(payload);
        ArgumentNullException.ThrowIfNull(hydration);
        ValidateStagedHydration(payload.Key, hydration);
        if (transaction is not null &&
            !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        string payloadJson = JsonSerializer.Serialize(payload);
        string hydrationJson = JsonSerializer.Serialize(hydration);
        string hydrationFingerprint =
            AuthoringResultHasher.HashNormalizedUtf8(hydrationJson);
        await using (SqliteCommand validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText =
                    """
                    SELECT i.CurrentSourceRevision, r.Status, r.BusinessKey
                    FROM prepared_ticket_publication_reconciliation_items i
                    INNER JOIN prepared_ticket_publication_reconciliations
                        reconciliation ON reconciliation.RunId = i.RunId
                    INNER JOIN authoring_run_items r
                        ON r.RunId = i.RunId
                       AND r.Id = @runItemId
                       AND r.BusinessKey = i.TicketKey COLLATE NOCASE
                       AND r.ItemKind = i.ItemKind
                       AND r.ExpectedSourceRevision =
                           i.ExpectedSourceRevision
                    WHERE i.RunId = @runId
                      AND i.TicketKey = @ticketKey COLLATE NOCASE
                      AND i.Disposition = @disposition
                      AND reconciliation.PromotionState = @staged
                    """;
            validate.Parameters.AddWithValue("@runId", runId);
            validate.Parameters.AddWithValue("@runItemId", runItemId);
            validate.Parameters.AddWithValue("@ticketKey", payload.Key);
            validate.Parameters.AddWithValue(
                "@disposition",
                PreparedTicketPublicationReconciliationDispositionValues
                    .ReAuthor);
            validate.Parameters.AddWithValue(
                "@staged",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged);
            await using SqliteDataReader reader =
                await validate.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException(
                    $"Ticket '{payload.Key}' is not pending revised work in reconciliation '{runId}'.");
            }
            if (!string.Equals(
                    AuthoringSourceRevision.CanonicalizeTimestamp(
                        reader.GetString(0)),
                    AuthoringSourceRevision.CanonicalizeTimestamp(
                        sourceRevision),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Ticket '{payload.Key}' staging revision does not match the frozen recipe.");
            }
        }
            await InsertIdempotentStageRowAsync(
                connection,
                transaction,
                "prepared_ticket_publication_staged_graphs",
                payload.Key,
                runId,
                """
                RunId, TicketKey, RunItemId, OperationId, SourceRevision,
                AuthoredFingerprint, PayloadJson, StagedAt
                """,
                """
                @runId, @ticketKey, @runItemId, @operationId, @sourceRevision,
                @authoredFingerprint, @payloadJson, @stagedAt
                """,
                """
                RunItemId = @runItemId AND OperationId = @operationId
                AND SourceRevision = @sourceRevision
                AND AuthoredFingerprint = @authoredFingerprint
                AND PayloadJson = @payloadJson
                """,
                ct,
                ("@runItemId", runItemId),
                ("@operationId", operationId),
                ("@sourceRevision", sourceRevision),
                ("@authoredFingerprint", authoredFingerprint),
                ("@payloadJson", payloadJson),
                ("@stagedAt", Format(stagedAt)));
            await InsertIdempotentStageRowAsync(
                connection,
                transaction,
                "prepared_ticket_publication_staged_hydration",
                payload.Key,
                runId,
                "RunId, TicketKey, HydrationFingerprint, HydrationJson, StagedAt",
                "@runId, @ticketKey, @hydrationFingerprint, @hydrationJson, @stagedAt",
                """
                HydrationFingerprint = @hydrationFingerprint
                AND HydrationJson = @hydrationJson
                """,
                ct,
                ("@hydrationFingerprint", hydrationFingerprint),
                ("@hydrationJson", hydrationJson),
                ("@stagedAt", Format(stagedAt)));
            await InsertIdempotentStageRowAsync(
                connection,
                transaction,
                "prepared_ticket_publication_staged_receipts",
                payload.Key,
                runId,
                """
                RunId, TicketKey, ReceiptId, RunItemId, OperationId,
                AuthoredFingerprint, PersistedAt
                """,
                """
                @runId, @ticketKey, @receiptId, @runItemId, @operationId,
                @authoredFingerprint, @stagedAt
                """,
                """
                ReceiptId = @receiptId AND RunItemId = @runItemId
                AND OperationId = @operationId
                AND AuthoredFingerprint = @authoredFingerprint
                """,
                ct,
                ("@receiptId", receiptId),
                ("@runItemId", runItemId),
                ("@operationId", operationId),
                ("@authoredFingerprint", authoredFingerprint),
                ("@stagedAt", Format(stagedAt)));
        return new PreparedTicketPublicationStagedTicket(
            runId,
            payload.Key,
            runItemId,
            operationId,
            receiptId,
            sourceRevision,
            authoredFingerprint,
            hydrationFingerprint,
            payload,
            hydration,
            stagedAt);
    }

    private static PreparedTicketHydrationBatch ToPreparedHydrationBatch(
        HydrationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new PreparedTicketHydrationBatch(
            batch.TicketKey,
            new PreparedTicketHydrationRow(
                batch.Parent.TicketKey, batch.Parent.Priority,
                batch.Parent.Resolution,
                batch.Parent.ResolutionDescriptionPlain,
                batch.Parent.Specification, batch.Parent.RaisedInVersion,
                batch.Parent.SelectedBallot, batch.Parent.ChangeCategory,
                batch.Parent.Impact, batch.Parent.Labels,
                batch.Parent.CommentCount, batch.Parent.DescriptionPlain,
                batch.Parent.HydratedAt, batch.Parent.HydrationStatus,
                batch.Parent.HydrationReason, batch.Parent.DescriptionHtml,
                batch.Parent.ResolutionDescriptionHtml,
                batch.Parent.StructuredReporter, batch.Parent.CreatedAt,
                batch.Parent.RelatedArtifactsRaw,
                batch.Parent.RelatedPagesRaw, batch.Parent.Assignee,
                batch.Parent.SourceIsStable == true
                    ? batch.Parent.SourceProject : null,
                batch.Parent.SourceIsStable == true
                    ? batch.Parent.SourceLastSuccessfulRefreshAt : null,
                batch.Parent.SourceIsStable == true
                    ? batch.Parent.SourceContentRevision : null,
                batch.Parent.PublicDisplayNamePolicyVersion),
            batch.JiraRows.Select(row => new PreparedJiraHydrationRow(
                row.TicketKey, row.JiraKey, row.Title, row.Status, row.Type,
                row.Priority, row.Resolution,
                row.ResolutionDescriptionPlain, row.WorkGroup,
                row.Specification, row.UpdatedAt, row.Url, row.HydratedAt,
                row.HydrationStatus, row.HydrationReason,
                row.DescriptionHtml, row.ResolutionDescriptionHtml,
                row.StructuredReporter, row.CreatedAt,
                row.RelatedArtifactsRaw, row.RelatedPagesRaw, row.Assignee,
                row.PublicDisplayNamePolicyVersion)).ToArray(),
            batch.ZulipRows.Select(row => new PreparedZulipHydrationRow(
                row.TicketKey, row.ZulipThreadId, row.StreamId,
                row.StreamName, row.Topic, row.MessageCount,
                row.FirstMessageAt, row.LastMessageAt,
                row.FirstMessageExcerpt, row.Url, row.HydratedAt,
                row.HydrationStatus, row.HydrationReason)).ToArray(),
            batch.GitHubRows.Select(row => new PreparedGitHubHydrationRow(
                row.TicketKey, row.GitHubItemId, row.Owner, row.Repo,
                row.Number, row.Path, row.Title, row.State,
                row.IsPullRequest, row.Labels, row.UpdatedAt, row.Url,
                row.HydratedAt, row.HydrationStatus,
                row.HydrationReason)).ToArray(),
            batch.RepoRows.Select(row => new PreparedRepoHydrationRow(
                row.TicketKey, row.Repo, row.Description, row.WorkGroup,
                row.Specification, row.CategoryDetail, row.Url,
                row.HydratedAt, row.HydrationStatus,
                row.HydrationReason)).ToArray(),
            batch.JiraXrefRows.Select(row =>
                new PreparedTicketJiraXrefRow(
                    row.TicketKey, row.JiraKey, row.Source)).ToArray(),
            (batch.Parent.InPersonRequesters ?? []).Select(displayName =>
                new PreparedTicketInPersonRequesterRow(
                    batch.TicketKey,
                    displayName,
                    batch.Parent.PublicDisplayNamePolicyVersion)).ToArray());
    }

    public async Task SavePublicationReconciliationGroupingImpactAsync(
        string runId,
        PreparedTicketPublicationReconciliationGroupingImpact impact,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentException.ThrowIfNullOrWhiteSpace(impact.PartitionKey);
        string json = JsonSerializer.Serialize(impact);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_publication_grouping_impacts(
                RunId, PartitionKey, ImpactJson, Complete, UpdatedAt)
            SELECT @runId, @partitionKey, @json, @complete, @updatedAt
            WHERE EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                    reconciliation
                INNER JOIN authoring_runs run
                  ON run.Id = reconciliation.RunId
                INNER JOIN authoring_mutation_fences mutationFence
                  ON mutationFence.ProcessorKind = run.ProcessorKind
                 AND mutationFence.RunId = run.Id
                INNER JOIN
                    prepared_ticket_publication_reconciliation_fences
                        reconciliationFence
                  ON reconciliationFence.RunId = run.Id
                WHERE reconciliation.RunId = @runId
                  AND reconciliation.PromotionState = @staged)
            ON CONFLICT(RunId, PartitionKey) DO UPDATE SET
                ImpactJson = excluded.ImpactJson,
                Complete = excluded.Complete,
                UpdatedAt = excluded.UpdatedAt
            WHERE ImpactJson = excluded.ImpactJson
            """,
            ct,
            ("@runId", runId),
            ("@partitionKey", impact.PartitionKey),
            ("@json", json),
            ("@complete", impact.Complete),
            ("@updatedAt", Format(now ?? DateTimeOffset.UtcNow)),
            ("@staged",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged));
        await using SqliteCommand check = connection.CreateCommand();
        check.CommandText =
            """
            SELECT ImpactJson FROM prepared_ticket_publication_grouping_impacts
            WHERE RunId = @runId AND PartitionKey = @partitionKey
            """;
        check.Parameters.AddWithValue("@runId", runId);
        check.Parameters.AddWithValue("@partitionKey", impact.PartitionKey);
        if (!string.Equals(
                json,
                (string?)await check.ExecuteScalarAsync(ct),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Grouping impact '{impact.PartitionKey}' was already staged with different content.");
        }
    }

    public async Task<PreparedTicketPublicationGroupingDelta>
        PreparePublicationReconciliationGroupingDeltaAsync(
            string runId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            PreparedTicketPublicationReconciliationComparison comparison =
                await GetPublicationReconciliationComparisonAsync(runId, ct)
                ?? throw new KeyNotFoundException(
                    $"Publication reconciliation '{runId}' was not found.");
            EnsureCurrentPublicationReconciliationComparison(comparison);
            PreparedTicketPublicationCorpusOverlay overlay =
                await GetPublicationReconciliationCorpusAsync(runId, ct);
            await using SqliteConnection connection = OpenConnection();
            PreparedTicketPublicationProtectedInventory inventory =
                await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(
                    connection,
                    ct);

            string[] revisedKeys = comparison.Items
                .Where(item => item.Disposition ==
                    PreparedTicketPublicationReconciliationDispositionValues
                        .ReAuthor)
                .Select(item => item.TicketKey)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();
            HashSet<string> revised = revisedKeys.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> impacted = inventory.Grouping
                .Where(partition => partition.Corpus.Any(item =>
                    revised.Contains(item.TicketKey)))
                .Select(partition => partition.PartitionKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (PreparedTicketPublicationCorpusTicket ticket in
                     overlay.Tickets.Where(ticket =>
                         revised.Contains(ticket.TicketKey)))
            {
                PreparedJiraHydrationRow? self = ticket.Hydration.JiraRows
                    .SingleOrDefault(row => string.Equals(
                        row.JiraKey,
                        ticket.TicketKey,
                        StringComparison.OrdinalIgnoreCase));
                if (self is null || string.IsNullOrWhiteSpace(self.Type))
                {
                    continue;
                }
                string workGroupClean =
                    Hl7WorkGroupNameCleaner.Clean(self.WorkGroup);
                if (string.IsNullOrWhiteSpace(workGroupClean))
                {
                    continue;
                }
                impacted.Add(GetPartitionKey(
                    workGroupClean,
                    NormalizePartitionValue(
                        self.Specification,
                        "Unspecified"),
                    NormalizePartitionValue(self.Type, string.Empty)));
            }

            Dictionary<string, PreparedTicketPublicationProtectedGrouping>
                baselineGrouping = inventory.Grouping.ToDictionary(
                    value => value.PartitionKey,
                    StringComparer.Ordinal);
            List<PreparedTicketPublicationReconciliationGroupingImpact> impacts =
                [];
            foreach (string partitionKey in impacted.Order(StringComparer.Ordinal))
            {
                PreparedTicketPublicationProtectedGrouping? baseline =
                    baselineGrouping.GetValueOrDefault(partitionKey);
                string baselineCorpusFingerprint = baseline?.Fingerprint
                    .CorpusFingerprint ??
                    PreparedTicketGroupingDeltaFingerprint([]);
                string baselineOutputFingerprint = baseline?.Fingerprint
                    .OutputFingerprint ??
                    PreparedTicketGroupingDeltaEmptyOutputFingerprint(partitionKey);
                string baselineRowsFingerprint = baseline?.Fingerprint
                    .ProtectedRowsFingerprint ??
                    PreparedTicketEnrichmentEmptyRowsFingerprint();
                string[] partitionRevisedKeys = revisedKeys.Where(key =>
                        (baseline?.Corpus.Any(item => string.Equals(
                            item.TicketKey,
                            key,
                            StringComparison.OrdinalIgnoreCase)) ?? false) ||
                        OverlayTicketHasPartition(
                            overlay,
                            key,
                            partitionKey))
                    .ToArray();
                PreparedTicketPublicationReconciliationGroupingImpact impact =
                    await ReadGroupingImpactAsync(
                        connection,
                        runId,
                        partitionKey,
                        ct)
                    ?? new(
                        partitionKey,
                        partitionRevisedKeys,
                        baselineCorpusFingerprint,
                        baselineOutputFingerprint,
                        baselineRowsFingerprint);
                if (!string.Equals(
                        impact.BaselineCorpusFingerprint,
                        baselineCorpusFingerprint,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        impact.BaselineOutputFingerprint,
                        baselineOutputFingerprint,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        impact.BaselineProtectedRowsFingerprint,
                        baselineRowsFingerprint,
                        StringComparison.Ordinal) ||
                    !impact.RevisedTicketKeys.SequenceEqual(
                        partitionRevisedKeys,
                        StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Grouping impact '{partitionKey}' no longer matches its frozen baseline.");
                }
                if (!impact.Complete)
                {
                    await SavePublicationReconciliationGroupingImpactAsync(
                        runId,
                        impact,
                        ct: ct);
                }
                impacts.Add(impact);
            }

            PreparedTicketPublicationUnaffectedFingerprint unaffected =
                CreateUnaffectedFingerprint(
                    runId,
                    inventory,
                    revised,
                    impacted,
                    DateTimeOffset.UtcNow);
            await SaveOrValidateUnaffectedFingerprintAsync(
                connection,
                unaffected,
                ct);
            return new(
                runId,
                overlay.CorpusFingerprint,
                impacts,
                unaffected);
        }

        public async Task ValidatePublicationReconciliationGroupingStageAsync(
            PreparedTicketGroupingStageContext context,
            CancellationToken ct = default)
        {
            ValidatePublicationReconciliationGroupingStageContext(context);
            await using SqliteConnection connection = OpenConnection();
            await EnsureStageLeaseAsync(
                connection,
                context.RunId,
                context.StageId,
                context.StageLeaseId,
                context.InputFingerprint,
                PublicationReconciliationGroupingStageName,
                context.PartitionKey!,
                ct);
            await EnsurePublicationReconciliationGroupingStageAsync(
                connection,
                context.RunId,
                context.PartitionKey!,
                ct);
        }

        public async Task<AuthoringRunStageReceipt>
            SavePublicationReconciliationGroupingStageAsync(
                PreparedTicketGroupingStageContext context,
                PreparedTicketPublicationStagedGroupingReplacement replacement,
                CancellationToken ct = default)
        {
            ValidatePublicationReconciliationGroupingStageContext(context);
            ArgumentNullException.ThrowIfNull(replacement);
            string partitionKey = context.PartitionKey!;
            if (!string.Equals(
                    context.RunId,
                    replacement.RunId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    partitionKey,
                    replacement.PartitionKey,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The staged grouping replacement does not match its reconciliation stage coordinates.",
                    nameof(replacement));
            }

            PreparedTicketGroupingPayload payload =
                JsonSerializer.Deserialize<PreparedTicketGroupingPayload>(
                    replacement.ReplacementJson)
                ?? throw new ArgumentException(
                    "The staged grouping replacement payload is invalid.",
                    nameof(replacement));
            payload = CanonicalizeGroupingPayload(payload);
            PreparedTicketGroupingPayloadValidator.ThrowIfInvalid(payload);
            HashSet<string> members = context.TicketKeys!.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            string[] unknown = CollectReferencedTicketKeys(payload)
                .Where(ticketKey => !members.Contains(ticketKey))
                .ToArray();
            if (unknown.Length != 0 ||
                members.Count == 0 && payload.Topics.Count != 0)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Grouping replacement for partition '{partitionKey}' does not match its complete overlay membership.");
            }
            if (!string.Equals(
                    GetPartitionKey(
                        payload.WorkGroupClean,
                        payload.Specification,
                        payload.Type),
                    partitionKey,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The staged grouping replacement payload does not match its partition key.",
                    nameof(replacement));
            }

            string expectedCorpusFingerprint =
                AuthoringResultHasher.HashNormalizedUtf8(string.Join(
                    "\n",
                    context.TicketKeys!
                        .OrderBy(
                            value => value,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(value => value, StringComparer.Ordinal)));
            string expectedOutputFingerprint =
                PreparedTicketPublicationContract
                    .ComputeGroupingOutputFingerprint(payload);
            if (!string.Equals(
                    replacement.CorpusFingerprint,
                    expectedCorpusFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    replacement.OutputFingerprint,
                    expectedOutputFingerprint,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Grouping replacement for partition '{partitionKey}' does not match its staged input or output fingerprint.");
            }

            PreparedTicketGroupingSaveResult result = new(
                payload.WorkGroupClean,
                payload.Specification,
                payload.Type,
                payload.Topics.Count,
                payload.Topics.Sum(topic =>
                    topic.LinkedTicketGroups.Count),
                payload.Topics.Sum(topic =>
                    topic.LinkedTicketGroups.Sum(group =>
                        group.Members.Count) +
                    topic.RemainingTicketKeys.Count));
            DateTimeOffset persistedAt = replacement.StagedAt;
            string protectedRowsFingerprint =
                AuthoringResultHasher.HashNormalizedUtf8(
                    replacement.ReplacementJson);

            await using SqliteConnection connection = OpenConnection();
            await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
            try
            {
                await EnsureStageLeaseAsync(
                    connection,
                    context.RunId,
                    context.StageId,
                    context.StageLeaseId,
                    context.InputFingerprint,
                    PublicationReconciliationGroupingStageName,
                    partitionKey,
                    ct);
                await EnsurePublicationReconciliationGroupingStageAsync(
                    connection,
                    context.RunId,
                    partitionKey,
                    ct);
                PreparedTicketPublicationReconciliationGroupingImpact impact =
                    await ReadGroupingImpactAsync(
                        connection,
                        context.RunId,
                        partitionKey,
                        ct)
                    ?? throw new InvalidOperationException(
                        $"Grouping partition '{partitionKey}' is not in the reconciliation impact closure.");
                if (!string.Equals(
                        replacement.ProtectedRowsFingerprint,
                        impact.BaselineProtectedRowsFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageFingerprintMismatch,
                        $"Grouping replacement for partition '{partitionKey}' does not match its protected baseline.");
                }

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO prepared_ticket_publication_staged_grouping(
                        RunId, PartitionKey, ReplacementJson,
                        CorpusFingerprint, OutputFingerprint,
                        ProtectedRowsFingerprint, StagedAt)
                    VALUES(
                        @runId, @partitionKey, @replacementJson,
                        @corpusFingerprint, @outputFingerprint,
                        @protectedRowsFingerprint, @stagedAt)
                    ON CONFLICT(RunId, PartitionKey) DO UPDATE SET
                        StagedAt = excluded.StagedAt
                    WHERE ReplacementJson = excluded.ReplacementJson
                      AND CorpusFingerprint = excluded.CorpusFingerprint
                      AND OutputFingerprint = excluded.OutputFingerprint
                      AND ProtectedRowsFingerprint =
                          excluded.ProtectedRowsFingerprint
                    """,
                    ct,
                    ("@runId", context.RunId),
                    ("@partitionKey", partitionKey),
                    ("@replacementJson", replacement.ReplacementJson),
                    ("@corpusFingerprint", replacement.CorpusFingerprint),
                    ("@outputFingerprint", replacement.OutputFingerprint),
                    ("@protectedRowsFingerprint",
                        protectedRowsFingerprint),
                    ("@stagedAt", Format(persistedAt)));
                await using (SqliteCommand verify = connection.CreateCommand())
                {
                    verify.CommandText =
                        """
                        SELECT 1
                        FROM prepared_ticket_publication_staged_grouping
                        WHERE RunId = @runId
                          AND PartitionKey = @partitionKey
                          AND ReplacementJson = @replacementJson
                          AND CorpusFingerprint = @corpusFingerprint
                          AND OutputFingerprint = @outputFingerprint
                          AND ProtectedRowsFingerprint =
                              @protectedRowsFingerprint
                        """;
                    verify.Parameters.AddWithValue(
                        "@runId",
                        context.RunId);
                    verify.Parameters.AddWithValue(
                        "@partitionKey",
                        partitionKey);
                    verify.Parameters.AddWithValue(
                        "@replacementJson",
                        replacement.ReplacementJson);
                    verify.Parameters.AddWithValue(
                        "@corpusFingerprint",
                        replacement.CorpusFingerprint);
                    verify.Parameters.AddWithValue(
                        "@outputFingerprint",
                        replacement.OutputFingerprint);
                    verify.Parameters.AddWithValue(
                        "@protectedRowsFingerprint",
                        protectedRowsFingerprint);
                    if (await verify.ExecuteScalarAsync(ct) is null)
                    {
                        throw new InvalidOperationException(
                            $"Grouping replacement '{partitionKey}' was already staged with different content.");
                    }
                }

                PreparedTicketPublicationReconciliationGroupingImpact
                    complete = impact with
                    {
                        StagedCorpusFingerprint =
                            replacement.CorpusFingerprint,
                        StagedOutputFingerprint =
                            replacement.OutputFingerprint,
                        StagedProtectedRowsFingerprint =
                            protectedRowsFingerprint,
                        Complete = true,
                    };
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE prepared_ticket_publication_grouping_impacts
                    SET ImpactJson = @json, Complete = 1, UpdatedAt = @updatedAt
                    WHERE RunId = @runId AND PartitionKey = @partitionKey
                    """,
                    ct,
                    ("@json", JsonSerializer.Serialize(complete)),
                    ("@updatedAt", Format(persistedAt)),
                    ("@runId", context.RunId),
                    ("@partitionKey", partitionKey));

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO prepared_ticket_publication_grouping_stage_receipts(
                        RunId, StageId, StageLeaseId, PartitionKey,
                        InputFingerprint, OutputFingerprint, TopicRows,
                        TopicGroupRows, MemberRows, PersistedAt)
                    VALUES(
                        @runId, @stageId, @stageLeaseId, @partitionKey,
                        @inputFingerprint, @outputFingerprint, @topicRows,
                        @topicGroupRows, @memberRows, @persistedAt)
                    ON CONFLICT(RunId, PartitionKey) DO UPDATE SET
                        StageLeaseId = excluded.StageLeaseId,
                        PersistedAt = excluded.PersistedAt
                    WHERE StageId = excluded.StageId
                      AND InputFingerprint = excluded.InputFingerprint
                      AND OutputFingerprint = excluded.OutputFingerprint
                      AND TopicRows = excluded.TopicRows
                      AND TopicGroupRows = excluded.TopicGroupRows
                      AND MemberRows = excluded.MemberRows
                    """,
                    ct,
                    ("@runId", context.RunId),
                    ("@stageId", context.StageId),
                    ("@stageLeaseId", context.StageLeaseId),
                    ("@partitionKey", partitionKey),
                    ("@inputFingerprint", context.InputFingerprint),
                    ("@outputFingerprint", replacement.OutputFingerprint),
                    ("@topicRows", result.TopicRows),
                    ("@topicGroupRows", result.TopicGroupRows),
                    ("@memberRows", result.MemberRows),
                    ("@persistedAt", Format(persistedAt)));
                await using (SqliteCommand verifyReceipt =
                             connection.CreateCommand())
                {
                    verifyReceipt.CommandText =
                        """
                        SELECT 1
                        FROM prepared_ticket_publication_grouping_stage_receipts
                        WHERE RunId = @runId
                          AND StageId = @stageId
                          AND StageLeaseId = @stageLeaseId
                          AND PartitionKey = @partitionKey
                          AND InputFingerprint = @inputFingerprint
                          AND OutputFingerprint = @outputFingerprint
                          AND TopicRows = @topicRows
                          AND TopicGroupRows = @topicGroupRows
                          AND MemberRows = @memberRows
                        """;
                    verifyReceipt.Parameters.AddWithValue(
                        "@runId",
                        context.RunId);
                    verifyReceipt.Parameters.AddWithValue(
                        "@stageId",
                        context.StageId);
                    verifyReceipt.Parameters.AddWithValue(
                        "@stageLeaseId",
                        context.StageLeaseId);
                    verifyReceipt.Parameters.AddWithValue(
                        "@partitionKey",
                        partitionKey);
                    verifyReceipt.Parameters.AddWithValue(
                        "@inputFingerprint",
                        context.InputFingerprint);
                    verifyReceipt.Parameters.AddWithValue(
                        "@outputFingerprint",
                        replacement.OutputFingerprint);
                    verifyReceipt.Parameters.AddWithValue(
                        "@topicRows",
                        result.TopicRows);
                    verifyReceipt.Parameters.AddWithValue(
                        "@topicGroupRows",
                        result.TopicGroupRows);
                    verifyReceipt.Parameters.AddWithValue(
                        "@memberRows",
                        result.MemberRows);
                    if (await verifyReceipt.ExecuteScalarAsync(ct) is null)
                    {
                        throw new InvalidOperationException(
                            $"Grouping stage receipt for partition '{partitionKey}' conflicts with its staged replacement.");
                    }
                }

                await ExecuteRawAsync(connection, "COMMIT", ct);
                return new AuthoringRunStageReceipt(
                    context.RunId,
                    context.StageId,
                    partitionKey,
                    context.InputFingerprint,
                    result.TopicRows,
                    result.TopicGroupRows,
                    result.MemberRows,
                    persistedAt);
            }
            catch
            {
                await ExecuteRawAsync(
                    connection,
                    "ROLLBACK",
                    CancellationToken.None);
                throw;
            }
        }

        public async Task<AuthoringRunStageReceipt?>
            GetPublicationReconciliationGroupingStageReceiptAsync(
                string runId,
                string stageId,
                string partitionKey,
                string inputFingerprint,
                string? stageLeaseId = null,
                CancellationToken ct = default)
        {
            await using SqliteConnection connection = OpenConnection();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT TopicRows, TopicGroupRows, MemberRows, PersistedAt
                FROM prepared_ticket_publication_grouping_stage_receipts
                WHERE RunId = @runId
                  AND StageId = @stageId
                  AND PartitionKey = @partitionKey
                  AND InputFingerprint = @inputFingerprint
                  AND (@stageLeaseId IS NULL OR
                       StageLeaseId = @stageLeaseId)
                """;
            command.Parameters.AddWithValue("@runId", runId);
            command.Parameters.AddWithValue("@stageId", stageId);
            command.Parameters.AddWithValue(
                "@partitionKey",
                partitionKey);
            command.Parameters.AddWithValue(
                "@inputFingerprint",
                inputFingerprint);
            AddNullable(command, "@stageLeaseId", stageLeaseId);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new AuthoringRunStageReceipt(
                    runId,
                    stageId,
                    partitionKey,
                    inputFingerprint,
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    ParseDate(reader.GetString(3)))
                : null;
        }

        public async Task SaveCompletePublicationReconciliationGroupingAsync(
            string runId,
            IReadOnlyList<PreparedTicketPublicationStagedGroupingReplacement>
                replacements,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentNullException.ThrowIfNull(replacements);
            if (replacements.Select(value => value.PartitionKey)
                .Distinct(StringComparer.Ordinal).Count() != replacements.Count)
            {
                throw new ArgumentException(
                    "Staged grouping replacements must have unique partitions.",
                    nameof(replacements));
            }
            await ValidatePublicationReconciliationUnaffectedAsync(runId, ct);
            await using SqliteConnection connection = OpenConnection();
            await using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            try
            {
                await EnsurePublicationReconciliationWorkspaceWritableAsync(
                    connection,
                    transaction,
                    runId,
                    ct);
                List<PreparedTicketPublicationReconciliationGroupingImpact>
                    impacts = await ReadGroupingImpactsAsync(
                        connection,
                        runId,
                        ct);
                string[] expected = impacts.Select(value => value.PartitionKey)
                    .Order(StringComparer.Ordinal).ToArray();
                if (!expected.SequenceEqual(
                        replacements.Select(value => value.PartitionKey)
                            .Order(StringComparer.Ordinal),
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Staged grouping replacements do not exactly cover the impacted closure.");
                }
                foreach (PreparedTicketPublicationStagedGroupingReplacement
                         replacement in replacements)
                {
                    PreparedTicketPublicationReconciliationGroupingImpact impact =
                        impacts.Single(value => value.PartitionKey ==
                            replacement.PartitionKey);
                    string protectedRowsFingerprint =
                        AuthoringResultHasher.HashNormalizedUtf8(
                            replacement.ReplacementJson);
                    await ExecuteInTransactionAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO prepared_ticket_publication_staged_grouping(
                            RunId, PartitionKey, ReplacementJson,
                            CorpusFingerprint, OutputFingerprint,
                            ProtectedRowsFingerprint, StagedAt)
                        VALUES(
                            @runId, @partitionKey, @replacementJson,
                            @corpusFingerprint, @outputFingerprint,
                            @protectedRowsFingerprint, @stagedAt)
                        ON CONFLICT(RunId, PartitionKey) DO UPDATE SET
                            ReplacementJson = excluded.ReplacementJson,
                            CorpusFingerprint = excluded.CorpusFingerprint,
                            OutputFingerprint = excluded.OutputFingerprint,
                            ProtectedRowsFingerprint =
                                excluded.ProtectedRowsFingerprint,
                            StagedAt = excluded.StagedAt
                        WHERE ReplacementJson = excluded.ReplacementJson
                          AND CorpusFingerprint = excluded.CorpusFingerprint
                          AND OutputFingerprint = excluded.OutputFingerprint
                          AND ProtectedRowsFingerprint =
                              excluded.ProtectedRowsFingerprint
                        """,
                        ct,
                        ("@runId", runId),
                        ("@partitionKey", replacement.PartitionKey),
                        ("@replacementJson", replacement.ReplacementJson),
                        ("@corpusFingerprint",
                            replacement.CorpusFingerprint),
                        ("@outputFingerprint",
                            replacement.OutputFingerprint),
                        ("@protectedRowsFingerprint",
                            protectedRowsFingerprint),
                        ("@stagedAt", Format(replacement.StagedAt)));
                    await using (SqliteCommand verify =
                                 connection.CreateCommand())
                    {
                        verify.Transaction = transaction;
                        verify.CommandText =
                            """
                            SELECT 1
                            FROM prepared_ticket_publication_staged_grouping
                            WHERE RunId = @runId
                              AND PartitionKey = @partitionKey
                              AND ReplacementJson = @replacementJson
                              AND CorpusFingerprint = @corpusFingerprint
                              AND OutputFingerprint = @outputFingerprint
                              AND ProtectedRowsFingerprint =
                                  @protectedRowsFingerprint
                            """;
                        verify.Parameters.AddWithValue("@runId", runId);
                        verify.Parameters.AddWithValue(
                            "@partitionKey",
                            replacement.PartitionKey);
                        verify.Parameters.AddWithValue(
                            "@replacementJson",
                            replacement.ReplacementJson);
                        verify.Parameters.AddWithValue(
                            "@corpusFingerprint",
                            replacement.CorpusFingerprint);
                        verify.Parameters.AddWithValue(
                            "@outputFingerprint",
                            replacement.OutputFingerprint);
                        verify.Parameters.AddWithValue(
                            "@protectedRowsFingerprint",
                            protectedRowsFingerprint);
                        if (await verify.ExecuteScalarAsync(ct) is null)
                        {
                            throw new InvalidOperationException(
                                $"Grouping replacement '{replacement.PartitionKey}' was already staged with different content.");
                        }
                    }
                    PreparedTicketPublicationReconciliationGroupingImpact
                        complete = impact with
                        {
                            StagedCorpusFingerprint =
                                replacement.CorpusFingerprint,
                            StagedOutputFingerprint =
                                replacement.OutputFingerprint,
                            StagedProtectedRowsFingerprint =
                                protectedRowsFingerprint,
                            Complete = true,
                        };
                    await ExecuteInTransactionAsync(
                        connection,
                        transaction,
                        """
                        UPDATE prepared_ticket_publication_grouping_impacts
                        SET ImpactJson = @json, Complete = 1, UpdatedAt = @updatedAt
                        WHERE RunId = @runId AND PartitionKey = @partitionKey
                        """,
                        ct,
                        ("@json", JsonSerializer.Serialize(complete)),
                        ("@updatedAt", Format(replacement.StagedAt)),
                        ("@runId", runId),
                        ("@partitionKey", replacement.PartitionKey));
                }
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task ValidatePublicationReconciliationUnaffectedAsync(
            string runId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            await using SqliteConnection connection = OpenConnection();
            string? json;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT FingerprintJson
                    FROM prepared_ticket_publication_unaffected_fingerprints
                    WHERE RunId = @runId
                    """;
                command.Parameters.AddWithValue("@runId", runId);
                json = (string?)await command.ExecuteScalarAsync(ct);
            }
            PreparedTicketPublicationUnaffectedFingerprint expected =
                json is null
                    ? throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has no unaffected-row fingerprint.")
                    : JsonSerializer.Deserialize<
                        PreparedTicketPublicationUnaffectedFingerprint>(json)
                      ?? throw new InvalidOperationException(
                          $"Reconciliation '{runId}' has an invalid unaffected-row fingerprint.");
            PreparedTicketPublicationReconciliationComparison comparison =
                await GetPublicationReconciliationComparisonAsync(runId, ct)
                ?? throw new KeyNotFoundException(
                    $"Publication reconciliation '{runId}' was not found.");
            EnsureCurrentPublicationReconciliationComparison(comparison);
            PreparedTicketPublicationProtectedInventory inventory =
                await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(
                    connection,
                    ct);
            HashSet<string> revised = comparison.Items
                .Where(item => item.Disposition ==
                    PreparedTicketPublicationReconciliationDispositionValues
                        .ReAuthor)
                .Select(item => item.TicketKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            PreparedTicketPublicationUnaffectedFingerprint actual =
                CreateUnaffectedFingerprint(
                    runId,
                    inventory,
                    revised,
                    expected.ImpactedPartitionKeys.ToHashSet(
                        StringComparer.Ordinal),
                    expected.CapturedAt);
            if (!UnaffectedFingerprintsEqual(actual, expected))
            {
                throw new InvalidOperationException(
                    "Canonical rows outside the reconciliation impact closure changed.");
            }
    }

    public async Task SavePublicationReconciliationGroupingReplacementAsync(
        PreparedTicketPublicationStagedGroupingReplacement replacement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.PartitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.ReplacementJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            replacement.CorpusFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            replacement.OutputFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            replacement.ProtectedRowsFingerprint);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_publication_staged_grouping(
                RunId, PartitionKey, ReplacementJson, CorpusFingerprint,
                OutputFingerprint, ProtectedRowsFingerprint, StagedAt)
            SELECT
                @runId, @partitionKey, @replacementJson, @corpusFingerprint,
                @outputFingerprint, @protectedRowsFingerprint, @stagedAt
            WHERE EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                    reconciliation
                INNER JOIN authoring_runs run
                  ON run.Id = reconciliation.RunId
                INNER JOIN authoring_mutation_fences mutationFence
                  ON mutationFence.ProcessorKind = run.ProcessorKind
                 AND mutationFence.RunId = run.Id
                INNER JOIN
                    prepared_ticket_publication_reconciliation_fences
                        reconciliationFence
                  ON reconciliationFence.RunId = run.Id
                WHERE reconciliation.RunId = @runId
                  AND reconciliation.PromotionState = @staged)
            ON CONFLICT(RunId, PartitionKey) DO NOTHING
            """,
            ct,
            ("@runId", replacement.RunId),
            ("@partitionKey", replacement.PartitionKey),
            ("@replacementJson", replacement.ReplacementJson),
            ("@corpusFingerprint", replacement.CorpusFingerprint),
            ("@outputFingerprint", replacement.OutputFingerprint),
            ("@protectedRowsFingerprint",
                replacement.ProtectedRowsFingerprint),
            ("@stagedAt", Format(replacement.StagedAt)),
            ("@staged",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged));
        await using SqliteCommand check = connection.CreateCommand();
        check.CommandText =
            """
            SELECT 1 FROM prepared_ticket_publication_staged_grouping
            WHERE RunId = @runId AND PartitionKey = @partitionKey
              AND ReplacementJson = @replacementJson
              AND CorpusFingerprint = @corpusFingerprint
              AND OutputFingerprint = @outputFingerprint
              AND ProtectedRowsFingerprint = @protectedRowsFingerprint
            """;
        check.Parameters.AddWithValue("@runId", replacement.RunId);
        check.Parameters.AddWithValue(
            "@partitionKey",
            replacement.PartitionKey);
        check.Parameters.AddWithValue(
            "@replacementJson",
            replacement.ReplacementJson);
        check.Parameters.AddWithValue(
            "@corpusFingerprint",
            replacement.CorpusFingerprint);
        check.Parameters.AddWithValue(
            "@outputFingerprint",
            replacement.OutputFingerprint);
        check.Parameters.AddWithValue(
            "@protectedRowsFingerprint",
            replacement.ProtectedRowsFingerprint);
        if (await check.ExecuteScalarAsync(ct) is null)
        {
            throw new InvalidOperationException(
                $"Grouping replacement '{replacement.PartitionKey}' was already staged with different content.");
        }
    }

    public async Task SavePublicationReconciliationSnapshotDescriptorAsync(
        PreparedTicketPublicationSnapshotDescriptor descriptor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DescriptorJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Sha256);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_publication_snapshot_descriptors(
                RunId, DescriptorJson, Sha256, PersistedAt)
            SELECT @runId, @json, @sha256, @persistedAt
            WHERE EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = @runId AND PromotionState = @staged)
            ON CONFLICT(RunId) DO UPDATE SET
                DescriptorJson = excluded.DescriptorJson,
                Sha256 = excluded.Sha256,
                PersistedAt = excluded.PersistedAt
            WHERE EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = @runId AND PromotionState = @staged)
            """,
            ct,
            ("@runId", descriptor.RunId),
            ("@json", descriptor.DescriptorJson),
            ("@sha256", descriptor.Sha256),
            ("@persistedAt", Format(descriptor.PersistedAt)),
            ("@staged",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged));
        await using SqliteCommand verify = connection.CreateCommand();
        verify.CommandText =
            """
            SELECT 1
            FROM prepared_ticket_publication_snapshot_descriptors
            WHERE RunId = @runId
              AND DescriptorJson = @json
              AND Sha256 = @sha256
            """;
        verify.Parameters.AddWithValue("@runId", descriptor.RunId);
        verify.Parameters.AddWithValue("@json", descriptor.DescriptorJson);
        verify.Parameters.AddWithValue("@sha256", descriptor.Sha256);
        if (await verify.ExecuteScalarAsync(ct) is null)
        {
            throw new InvalidOperationException(
                $"Reconciliation '{descriptor.RunId}' can no longer replace its staged candidate snapshot.");
        }
    }

    public async Task<PublicationReconciliationSnapshotReservation>
        ReservePublicationReconciliationSnapshotAsync(
            string runId,
            string temporaryPath,
            string finalPath,
            int schemaVersion,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (schemaVersion != PreparedTicketSnapshotSchemaV3.Version)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                "Publication reconciliation requires snapshot schema v3.");
        }

        temporaryPath = Path.GetFullPath(temporaryPath);
        finalPath = Path.GetFullPath(finalPath);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            string promotionState;
            string runStatus;
            string processorKind;
            long authoringEpoch;
            int itemCount;
            int receiptCount;
            PreparedTicketPublicationReconciliationComparison comparison;
            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    """
                    SELECT reconciliation.PromotionState,
                           reconciliation.ComparisonJson,
                           run.Status,
                           run.ProcessorKind,
                           run.AuthoringEpoch,
                           (SELECT COUNT(*)
                            FROM authoring_run_items
                            WHERE RunId = run.Id),
                           (SELECT COUNT(*)
                            FROM prepared_ticket_publication_reconciliation_items
                            WHERE RunId = run.Id)
                    FROM prepared_ticket_publication_reconciliations
                        reconciliation
                    INNER JOIN authoring_runs run
                      ON run.Id = reconciliation.RunId
                    WHERE reconciliation.RunId = @runId
                    """;
                read.Parameters.AddWithValue("@runId", runId);
                await using SqliteDataReader reader =
                    await read.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    throw new KeyNotFoundException(
                        $"Publication reconciliation '{runId}' was not found.");
                }
                promotionState = reader.GetString(0);
                comparison = JsonSerializer.Deserialize<
                    PreparedTicketPublicationReconciliationComparison>(
                        reader.GetString(1))
                    ?? throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has an invalid comparison.");
                runStatus = reader.GetString(2);
                processorKind = reader.GetString(3);
                authoringEpoch = reader.GetInt64(4);
                itemCount = reader.GetInt32(5);
                receiptCount = reader.GetInt32(6);
            }
            EnsureCurrentPublicationReconciliationComparison(comparison);
            if (promotionState !=
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged ||
                runStatus is not (
                    AuthoringStatusValues.Runs.Running or
                    AuthoringStatusValues.Runs.Finalizing) ||
                !string.Equals(
                    processorKind,
                    AuthoringProcessorKind,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' cannot reserve a snapshot from its current state.");
            }

            await EnsurePublicationSnapshotReservationPreconditionsAsync(
                connection,
                transaction,
                runId,
                ct);
            await using (SqliteCommand snapshotCheck =
                         connection.CreateCommand())
            {
                snapshotCheck.Transaction = transaction;
                snapshotCheck.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM authoring_review_snapshots
                    WHERE RunId = @runId
                    """;
                snapshotCheck.Parameters.AddWithValue("@runId", runId);
                if (Convert.ToInt32(
                        await snapshotCheck.ExecuteScalarAsync(ct),
                        CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has a snapshot record before database promotion.");
                }
            }
            (string DescriptorJson, string Sha256)? descriptor =
                await ReadPublicationCandidateDescriptorAsync(
                    connection,
                    transaction,
                    runId,
                    ct);
            string? journalDescriptor =
                await ReadPublicationJournalDescriptorAsync(
                    connection,
                    transaction,
                    runId,
                    ct);
            long sequence;
            await using (SqliteCommand sequenceCommand =
                         connection.CreateCommand())
            {
                sequenceCommand.Transaction = transaction;
                sequenceCommand.CommandText =
                    """
                    SELECT COALESCE(MAX(Sequence), 0) + 1
                    FROM authoring_review_snapshots
                    WHERE ProcessorKind = @processorKind
                    """;
                sequenceCommand.Parameters.AddWithValue(
                    "@processorKind",
                    processorKind);
                sequence = Convert.ToInt64(
                    await sequenceCommand.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture);
            }
            if (descriptor is not null)
            {
                PreparedTicketPublicationCandidateSnapshot candidate =
                    JsonSerializer.Deserialize<
                        PreparedTicketPublicationCandidateSnapshot>(
                            descriptor.Value.DescriptorJson)
                    ?? throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has an invalid candidate descriptor.");
                if (!string.Equals(
                        descriptor.Value.Sha256,
                        candidate.Sha256,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        candidate.RunId,
                        runId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        candidate.ProcessorKind,
                        processorKind,
                        StringComparison.Ordinal) ||
                    !PathsEqual(
                        candidate.TemporaryPath,
                        temporaryPath) ||
                    !PathsEqual(candidate.FinalPath, finalPath) ||
                    candidate.SchemaVersion != schemaVersion ||
                    candidate.Sequence != sequence ||
                    candidate.AuthoringEpoch != authoringEpoch ||
                    candidate.ItemCount != itemCount ||
                    candidate.ReceiptCount != receiptCount)
                {
                    throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has conflicting candidate reservation evidence.");
                }
                ValidatePublicationCandidate(candidate);
                if (!string.Equals(
                        journalDescriptor,
                        descriptor.Value.DescriptorJson,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Reconciliation '{runId}' candidate does not match its staged journal.");
                }
                await transaction.CommitAsync(ct);
                return new(
                    PublicationReconciliationSnapshotReservationKind,
                    runId,
                    candidate.SnapshotId,
                    processorKind,
                    temporaryPath,
                    finalPath,
                    schemaVersion,
                    sequence,
                    authoringEpoch,
                    itemCount,
                    receiptCount,
                    candidate.CreatedAt,
                    candidate);
            }

            PublicationReconciliationSnapshotReservation reservation;
            if (journalDescriptor is not null)
            {
                reservation = JsonSerializer.Deserialize<
                    PublicationReconciliationSnapshotReservation>(
                        journalDescriptor)
                    ?? throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has an invalid snapshot reservation.");
                EnsureReservationMatchesCurrent(
                    reservation,
                    runId,
                    processorKind,
                    temporaryPath,
                    finalPath,
                    schemaVersion,
                    sequence,
                    authoringEpoch,
                    itemCount,
                    receiptCount);
            }
            else
            {
                reservation = new(
                    PublicationReconciliationSnapshotReservationKind,
                    runId,
                    Guid.NewGuid().ToString("N"),
                    processorKind,
                    temporaryPath,
                    finalPath,
                    schemaVersion,
                    sequence,
                    authoringEpoch,
                    itemCount,
                    receiptCount,
                    DateTimeOffset.UtcNow,
                    null);
                int reserved = await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    """
                    UPDATE prepared_ticket_publication_reconciliation_journal
                    SET SnapshotDescriptorJson = @descriptor,
                        UpdatedAt = @updatedAt
                    WHERE RunId = @runId
                      AND State = @staged
                      AND SnapshotDescriptorJson IS NULL
                    """,
                    ct,
                    ("@descriptor", JsonSerializer.Serialize(reservation)),
                    ("@updatedAt", Format(reservation.CreatedAt)),
                    ("@runId", runId),
                    ("@staged",
                        PreparedTicketPublicationReconciliationPromotionStateValues
                            .Staged));
                if (reserved != 1)
                {
                    throw new InvalidOperationException(
                        $"Reconciliation '{runId}' changed before snapshot reservation.");
                }
            }
            await transaction.CommitAsync(ct);
            return reservation;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task SavePublicationReconciliationCandidateEvidenceAsync(
        PreparedTicketPublicationCandidateSnapshot candidate,
        PreparedTicketPublicationReconciliationProof proof,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Sha256);
        ValidatePublicationCandidate(candidate);
        if (proof.ContractVersion !=
                PreparedTicketPublicationReconciliationContract
                    .CurrentVersion ||
            proof.Purpose !=
                PreparedTicketPublicationReconciliationContract.Purpose)
        {
            throw new ArgumentException(
                "The publication proof is not a current reconciliation proof.",
                nameof(proof));
        }

        string descriptorJson = JsonSerializer.Serialize(candidate);
        string proofJson = JsonSerializer.Serialize(proof);
        DateTimeOffset persistedAt = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            string promotionState;
            string runStatus;
            string processorKind;
            long authoringEpoch;
            int itemCount;
            int receiptCount;
            long sequence;
            int snapshotCount;
            PreparedTicketPublicationReconciliationComparison comparison;
            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    """
                    SELECT reconciliation.PromotionState,
                           reconciliation.ComparisonJson,
                           run.Status,
                           run.ProcessorKind,
                           run.AuthoringEpoch,
                           (SELECT COUNT(*) FROM authoring_run_items
                            WHERE RunId = run.Id),
                           (SELECT COUNT(*)
                            FROM prepared_ticket_publication_reconciliation_items
                            WHERE RunId = run.Id),
                           (SELECT COALESCE(MAX(Sequence), 0) + 1
                            FROM authoring_review_snapshots
                            WHERE ProcessorKind = run.ProcessorKind),
                           (SELECT COUNT(*)
                            FROM authoring_review_snapshots
                            WHERE RunId = run.Id OR Id = @snapshotId)
                    FROM prepared_ticket_publication_reconciliations
                        reconciliation
                    INNER JOIN authoring_runs run
                      ON run.Id = reconciliation.RunId
                    WHERE reconciliation.RunId = @runId
                    """;
                read.Parameters.AddWithValue("@runId", candidate.RunId);
                read.Parameters.AddWithValue(
                    "@snapshotId",
                    candidate.SnapshotId);
                await using SqliteDataReader reader =
                    await read.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    throw new KeyNotFoundException(
                        $"Publication reconciliation '{candidate.RunId}' was not found.");
                }
                promotionState = reader.GetString(0);
                comparison = JsonSerializer.Deserialize<
                    PreparedTicketPublicationReconciliationComparison>(
                        reader.GetString(1))
                    ?? throw new InvalidOperationException(
                        $"Reconciliation '{candidate.RunId}' has an invalid comparison.");
                runStatus = reader.GetString(2);
                processorKind = reader.GetString(3);
                authoringEpoch = reader.GetInt64(4);
                itemCount = reader.GetInt32(5);
                receiptCount = reader.GetInt32(6);
                sequence = reader.GetInt64(7);
                snapshotCount = reader.GetInt32(8);
            }
            EnsureCurrentPublicationReconciliationComparison(comparison);
            if (promotionState !=
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged ||
                runStatus != AuthoringStatusValues.Runs.Finalizing)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{candidate.RunId}' can no longer persist finalizing candidate evidence.");
            }
            await EnsurePublicationSnapshotReservationPreconditionsAsync(
                connection,
                transaction,
                candidate.RunId,
                ct);
            if (snapshotCount != 0 ||
                !string.Equals(
                    candidate.ProcessorKind,
                    processorKind,
                    StringComparison.Ordinal) ||
                candidate.AuthoringEpoch != authoringEpoch ||
                candidate.Sequence != sequence ||
                candidate.ItemCount != itemCount ||
                candidate.ReceiptCount != receiptCount)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{candidate.RunId}' candidate no longer matches its reserved coordinates.");
            }
            if (!string.Equals(
                    proof.SourceRunId,
                    comparison.SourceRunId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    proof.SourceSnapshotId,
                    comparison.SourceSnapshotId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    proof.StableJiraGeneration,
                    comparison.StableJiraGeneration,
                    StringComparison.Ordinal) ||
                proof.AcceptedTicketCount != comparison.Items.Count ||
                proof.CarryForwardTicketCount != comparison.Items.Count(
                    item => item.Disposition ==
                        PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward) ||
                proof.ReAuthorTicketCount != comparison.Items.Count(
                    item => item.Disposition ==
                        PreparedTicketPublicationReconciliationDispositionValues
                            .ReAuthor) ||
                !string.Equals(
                    candidate.OverlayCorpusFingerprint,
                    proof.CorpusFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    candidate.GroupingImpactFingerprint,
                    proof.GroupingImpactFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The reconciliation candidate evidence does not match the frozen comparison.");
            }
            string journalDescriptor =
                await ReadPublicationJournalDescriptorAsync(
                    connection,
                    transaction,
                    candidate.RunId,
                    ct)
                ?? throw new InvalidOperationException(
                    $"Reconciliation '{candidate.RunId}' has no snapshot reservation.");
            if (!string.Equals(
                    journalDescriptor,
                    descriptorJson,
                    StringComparison.Ordinal))
            {
                PublicationReconciliationSnapshotReservation reservation =
                    JsonSerializer.Deserialize<
                        PublicationReconciliationSnapshotReservation>(
                            journalDescriptor)
                    ?? throw new InvalidOperationException(
                        $"Reconciliation '{candidate.RunId}' has invalid snapshot reservation evidence.");
                EnsureReservationMatchesCandidate(reservation, candidate);
            }

            _ = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO prepared_ticket_publication_snapshot_descriptors(
                    RunId, DescriptorJson, Sha256, PersistedAt)
                VALUES(@runId, @json, @sha256, @persistedAt)
                ON CONFLICT(RunId) DO NOTHING
                """,
                ct,
                ("@runId", candidate.RunId),
                ("@json", descriptorJson),
                ("@sha256", candidate.Sha256),
                ("@persistedAt", Format(persistedAt)));
            (string DescriptorJson, string Sha256)? savedDescriptor =
                await ReadPublicationCandidateDescriptorAsync(
                    connection,
                    transaction,
                    candidate.RunId,
                    ct);
            if (savedDescriptor is null ||
                !string.Equals(
                    savedDescriptor.Value.DescriptorJson,
                    descriptorJson,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    savedDescriptor.Value.Sha256,
                    candidate.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{candidate.RunId}' already has different trusted candidate evidence.");
            }
            _ = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO prepared_ticket_publication_reconciliation_proofs(
                    RunId, ProofJson, CapturedAt)
                VALUES(@runId, @json, @capturedAt)
                ON CONFLICT(RunId) DO UPDATE SET
                    ProofJson = excluded.ProofJson,
                    CapturedAt = excluded.CapturedAt
                """,
                ct,
                ("@runId", candidate.RunId),
                ("@json", proofJson),
                ("@capturedAt", Format(proof.CapturedAt)));
            int journalSaved = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliation_journal
                SET SnapshotDescriptorJson = @descriptor,
                    LastRecoveryAttemptAt = NULL,
                    FailureCode = NULL,
                    FailureDetail = NULL,
                    UpdatedAt = @updatedAt
                WHERE RunId = @runId
                  AND State = @staged
                  AND SnapshotDescriptorJson = @expectedDescriptor
                """,
                ct,
                ("@runId", candidate.RunId),
                ("@descriptor", descriptorJson),
                ("@expectedDescriptor", journalDescriptor),
                ("@staged",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged),
                ("@updatedAt", Format(persistedAt)));
            if (journalSaved != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{candidate.RunId}' can no longer replace its staged promotion journal.");
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DiscardPublicationReconciliationCandidateEvidenceAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            string? promotionState;
            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    """
                    SELECT PromotionState
                    FROM prepared_ticket_publication_reconciliations
                    WHERE RunId = @runId
                    """;
                read.Parameters.AddWithValue("@runId", runId);
                promotionState =
                    (string?)await read.ExecuteScalarAsync(ct);
            }
            if (promotionState is null)
            {
                throw new KeyNotFoundException(
                    $"Publication reconciliation '{runId}' was not found.");
            }
            if (promotionState !=
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' can no longer discard provisional candidate evidence.");
            }
            foreach (string table in new[]
            {
                "prepared_ticket_publication_snapshot_descriptors",
                "prepared_ticket_publication_reconciliation_proofs",
                "prepared_ticket_publication_reconciliation_journal",
            })
            {
                _ = await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    $"DELETE FROM {table} WHERE RunId = @runId",
                    ct,
                    ("@runId", runId));
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task SavePublicationReconciliationProofAsync(
        string runId,
        PreparedTicketPublicationReconciliationProof proof,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.ContractVersion !=
                PreparedTicketPublicationReconciliationContract.CurrentVersion ||
            proof.Purpose !=
                PreparedTicketPublicationReconciliationContract.Purpose)
        {
            throw new ArgumentException(
                "The publication proof is not a current reconciliation proof.",
                nameof(proof));
        }
        string json = JsonSerializer.Serialize(proof);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_publication_reconciliation_proofs(
                RunId, ProofJson, CapturedAt)
            SELECT @runId, @json, @capturedAt
            WHERE EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = @runId AND PromotionState = @staged)
            ON CONFLICT(RunId) DO UPDATE SET
                ProofJson = excluded.ProofJson,
                CapturedAt = excluded.CapturedAt
            WHERE EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = @runId AND PromotionState = @staged)
            """,
            ct,
            ("@runId", runId),
            ("@json", json),
            ("@capturedAt", Format(proof.CapturedAt)),
            ("@staged",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged));
        await using SqliteCommand verify = connection.CreateCommand();
        verify.CommandText =
            """
            SELECT 1
            FROM prepared_ticket_publication_reconciliation_proofs
            WHERE RunId = @runId AND ProofJson = @json
            """;
        verify.Parameters.AddWithValue("@runId", runId);
        verify.Parameters.AddWithValue("@json", json);
        if (await verify.ExecuteScalarAsync(ct) is null)
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' can no longer replace its staged publication proof.");
        }
    }

    public async Task UpdatePublicationReconciliationJournalAsync(
        PreparedTicketPublicationPromotionJournal journal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrWhiteSpace(journal.RunId);
        if (!PreparedTicketPublicationReconciliationPromotionStateValues
                .IsValid(journal.State))
        {
            throw new ArgumentException(
                $"Unknown reconciliation journal state '{journal.State}'.",
                nameof(journal));
        }
        if (journal.State ==
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Cancelled)
        {
            throw new ArgumentException(
                "Use the dedicated reconciliation cancellation transaction.",
                nameof(journal));
        }
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            int journalUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliation_journal
                SET State = @state,
                    SnapshotDescriptorJson = @descriptor,
                    LastRecoveryAttemptAt = @recoveryAt,
                    FailureCode = @failureCode,
                    FailureDetail = @failureDetail,
                    UpdatedAt = @updatedAt
                WHERE RunId = @runId AND State <> @cancelled
                """,
                ct,
                ("@state", journal.State),
                ("@descriptor", journal.SnapshotDescriptorJson),
                ("@recoveryAt", journal.LastRecoveryAttemptAt is null
                    ? null
                    : Format(journal.LastRecoveryAttemptAt.Value)),
                ("@failureCode", journal.FailureCode),
                ("@failureDetail", journal.FailureDetail),
                ("@updatedAt", Format(journal.UpdatedAt)),
                ("@runId", journal.RunId),
                ("@cancelled",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Cancelled));
            int reconciliationUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliations
                SET PromotionState = @state
                WHERE RunId = @runId AND PromotionState <> @cancelled
                """,
                ct,
                ("@state", journal.State),
                ("@runId", journal.RunId),
                ("@cancelled",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Cancelled));
            if (journalUpdated != 1 || reconciliationUpdated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{journal.RunId}' journal is no longer mutable.");
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public sealed record PublicationReconciliationSnapshotReservation(
        string ReservationKind,
        string RunId,
        string SnapshotId,
        string ProcessorKind,
        string TemporaryPath,
        string FinalPath,
        int SchemaVersion,
        long Sequence,
        long AuthoringEpoch,
        int ItemCount,
        int ReceiptCount,
        DateTimeOffset CreatedAt,
        PreparedTicketPublicationCandidateSnapshot? Candidate);

    public sealed record PublicationReconciliationPromotion(
        string RunId,
        string SnapshotId,
        string ProcessorKind,
        string TemporaryPath,
        string FinalPath,
        string CandidateSha256,
        long CandidateSizeBytes,
        int SchemaVersion,
        long Sequence,
        long AuthoringEpoch,
        int ItemCount,
        int ReceiptCount,
        IReadOnlyDictionary<string, long> TableCounts,
        DateTimeOffset CreatedAt);

    public sealed record PublicationReconciliationRecoveryEvidence(
        string State,
        PublicationReconciliationPromotion Promotion,
        PreparedTicketPublicationCandidateSnapshot? Candidate);

    public async Task<PublicationReconciliationPromotion?>
        GetPendingPublicationReconciliationAsync(
            string runId,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationReconciliationComparison? comparison =
            await GetPublicationReconciliationComparisonAsync(runId, ct);
        if (comparison is null)
        {
            return null;
        }
        EnsureCurrentPublicationReconciliationComparison(comparison);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotDescriptorJson
            FROM prepared_ticket_publication_reconciliation_journal
            WHERE RunId = @runId AND State = @state
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@state",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .SnapshotPublishPending);
        string? json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null
            ? null
            : JsonSerializer.Deserialize<PublicationReconciliationPromotion>(
                json)
              ?? throw new InvalidOperationException(
                  $"Reconciliation '{runId}' has an invalid promotion journal.");
    }

    public async Task<PublicationReconciliationRecoveryEvidence?>
        GetRecoverablePublicationReconciliationAsync(
            string runId,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationReconciliationComparison? comparison =
            await GetPublicationReconciliationComparisonAsync(runId, ct);
        if (comparison is null)
        {
            return null;
        }
        EnsureCurrentPublicationReconciliationComparison(comparison);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT journal.State,
                   journal.SnapshotDescriptorJson,
                   candidate.DescriptorJson,
                   candidate.Sha256
            FROM prepared_ticket_publication_reconciliation_journal journal
            LEFT JOIN prepared_ticket_publication_snapshot_descriptors
                candidate ON candidate.RunId = journal.RunId
            WHERE journal.RunId = @runId
              AND journal.State IN (@pending, @ready)
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@pending",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .SnapshotPublishPending);
        command.Parameters.AddWithValue(
            "@ready",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Ready);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        string state = reader.GetString(0);
        PublicationReconciliationPromotion promotion =
            JsonSerializer.Deserialize<PublicationReconciliationPromotion>(
                reader.GetString(1))
            ?? throw new InvalidOperationException(
                $"Reconciliation '{runId}' has an invalid recovery journal.");
        PreparedTicketPublicationCandidateSnapshot? candidate =
            reader.IsDBNull(2)
                ? null
                : JsonSerializer.Deserialize<
                    PreparedTicketPublicationCandidateSnapshot>(
                        reader.GetString(2))
                  ?? throw new InvalidOperationException(
                      $"Reconciliation '{runId}' has an invalid trusted candidate descriptor.");
        if (candidate is not null &&
            !string.Equals(
                candidate.Sha256,
                reader.GetString(3),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' has conflicting candidate digest evidence.");
        }
        return new(state, promotion, candidate);
    }

    public async Task<IReadOnlyList<string>>
        ListPendingPublicationReconciliationsAsync(
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RunId
            FROM prepared_ticket_publication_reconciliation_journal
            WHERE State = @state
            ORDER BY UpdatedAt, RunId
            """;
        command.Parameters.AddWithValue(
            "@state",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .SnapshotPublishPending);
        List<string> runIds = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runIds.Add(reader.GetString(0));
        }
        return runIds;
    }

    public async Task<PublicationReconciliationPromotion>
        PromotePublicationReconciliationAsync(
            string runId,
            string finalPath,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        finalPath = Path.GetFullPath(finalPath);
        PreparedTicketPublicationReconciliationComparison comparison =
            await GetPublicationReconciliationComparisonAsync(runId, ct)
            ?? throw new KeyNotFoundException(
                $"Publication reconciliation '{runId}' was not found.");
        EnsureCurrentPublicationReconciliationComparison(comparison);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            string state;
            string descriptorJson;
            string descriptorSha256;
            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    """
                    SELECT reconciliation.PromotionState,
                           descriptor.DescriptorJson,
                           descriptor.Sha256
                    FROM prepared_ticket_publication_reconciliations reconciliation
                    INNER JOIN prepared_ticket_publication_snapshot_descriptors descriptor
                      ON descriptor.RunId = reconciliation.RunId
                    WHERE reconciliation.RunId = @runId
                    """;
                read.Parameters.AddWithValue("@runId", runId);
                await using SqliteDataReader reader =
                    await read.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has no verified candidate snapshot.");
                }
                state = reader.GetString(0);
                descriptorJson = reader.GetString(1);
                descriptorSha256 = reader.GetString(2);
            }
            if (state ==
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending)
            {
                await transaction.CommitAsync(ct);
                return await GetPendingPublicationReconciliationAsync(
                    runId,
                    ct)
                    ?? throw new InvalidOperationException(
                        $"Reconciliation '{runId}' lost its pending journal.");
            }
            if (state !=
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' cannot promote from '{state}'.");
            }

            PreparedTicketPublicationCandidateSnapshot candidate =
                JsonSerializer.Deserialize<
                    PreparedTicketPublicationCandidateSnapshot>(
                    descriptorJson)
                ?? throw new InvalidOperationException(
                    $"Reconciliation '{runId}' has an invalid candidate snapshot.");
            ValidatePublicationCandidate(candidate);
            if (!string.Equals(
                    candidate.RunId,
                    runId,
                    StringComparison.Ordinal) ||
                !PathsEqual(candidate.FinalPath, finalPath) ||
                !string.Equals(
                    descriptorSha256,
                    candidate.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The verified reconciliation candidate descriptor conflicts with promotion.");
            }

            await EnsurePublicationPromotionPreconditionsAsync(
                connection,
                transaction,
                runId,
                ct);
            await EnsurePublicationCandidateReservationCurrentAsync(
                connection,
                transaction,
                candidate,
                ct);
            PreparedTicketPublicationReconciliationProof proof =
                await ReadPublicationReconciliationProofAsync(
                    connection,
                    transaction,
                    runId,
                    ct);
            string snapshotProofJson = JsonSerializer.Serialize(
                CreateSnapshotPublicationProof(proof),
                JsonSerializerOptions.Web);
            AuthoringReviewSnapshotRecord snapshot =
                CreateCandidateSnapshotRecord(
                    candidate,
                    snapshotProofJson);
            SqliteReviewSnapshotValidationResult validation =
                await SqliteReviewSnapshotValidator.ValidateAsync(
                    snapshot,
                    candidate.TemporaryPath,
                    ct: ct);
            if (!validation.IsValid ||
                validation.SizeBytes != candidate.SizeBytes ||
                !string.Equals(
                    validation.ChecksumSha256,
                    candidate.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The verified reconciliation candidate is missing or corrupt: {validation.Error ?? "digest or size mismatch"}");
            }

            await ApplyPublicationReconciliationOverlayAsync(
                connection,
                runId,
                ct);
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO authoring_review_snapshots(
                    Id, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                    SchemaVersion, Status, TempPath, Path, ChecksumSha256,
                    SizeBytes, ItemCount, ReceiptCount, TableCountsJson,
                    PublicationProofJson, CreatedAt)
                VALUES(
                    @snapshotId, @processorKind, @runId, @authoringEpoch,
                    @sequence, @schemaVersion, @status, @temporaryPath,
                    @finalPath, @sha256, @sizeBytes, @itemCount,
                    @receiptCount, @tableCounts, @proof, @createdAt)
                """,
                ct,
                ("@snapshotId", candidate.SnapshotId),
                ("@processorKind", candidate.ProcessorKind),
                ("@runId", candidate.RunId),
                ("@authoringEpoch", candidate.AuthoringEpoch),
                ("@sequence", candidate.Sequence),
                ("@schemaVersion", candidate.SchemaVersion),
                ("@status", AuthoringStatusValues.Snapshots.Creating),
                ("@temporaryPath", candidate.TemporaryPath),
                ("@finalPath", candidate.FinalPath),
                ("@sha256", candidate.Sha256),
                ("@sizeBytes", candidate.SizeBytes),
                ("@itemCount", candidate.ItemCount),
                ("@receiptCount", candidate.ReceiptCount),
                ("@tableCounts",
                    JsonSerializer.Serialize(candidate.TableCounts)),
                ("@proof", snapshotProofJson),
                ("@createdAt", Format(candidate.CreatedAt)));

            PublicationReconciliationPromotion promotion = new(
                runId,
                candidate.SnapshotId,
                candidate.ProcessorKind,
                candidate.TemporaryPath,
                candidate.FinalPath,
                candidate.Sha256,
                candidate.SizeBytes,
                candidate.SchemaVersion,
                candidate.Sequence,
                candidate.AuthoringEpoch,
                candidate.ItemCount,
                candidate.ReceiptCount,
                candidate.TableCounts,
                candidate.CreatedAt);
            string promotionJson = JsonSerializer.Serialize(promotion);
            DateTimeOffset promotedAt = DateTimeOffset.UtcNow;
            int journalUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliation_journal
                SET State = @pending, SnapshotDescriptorJson = @descriptor,
                    FailureCode = NULL, FailureDetail = NULL,
                    UpdatedAt = @updatedAt
                WHERE RunId = @runId
                  AND State = @staged
                  AND SnapshotDescriptorJson = @candidateDescriptor
                """,
                ct,
                ("@pending",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .SnapshotPublishPending),
                ("@descriptor", promotionJson),
                ("@candidateDescriptor", descriptorJson),
                ("@updatedAt", Format(promotedAt)),
                ("@runId", runId),
                ("@staged",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged));
            if (journalUpdated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' lost its exact candidate journal before promotion.");
            }
            int reconciliationUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliations
                SET PromotionState = @pending
                WHERE RunId = @runId AND PromotionState = @staged
                """,
                ct,
                ("@pending",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .SnapshotPublishPending),
                ("@runId", runId),
                ("@staged",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged));
            if (reconciliationUpdated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' changed before promotion.");
            }
            await transaction.CommitAsync(ct);
            return promotion;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RecordPublicationReconciliationRecoveryFailureAsync(
        string runId,
        string detail,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(
            connection,
            """
            UPDATE prepared_ticket_publication_reconciliation_journal
            SET LastRecoveryAttemptAt = @attemptedAt,
                FailureCode = @failureCode,
                FailureDetail = @detail,
                UpdatedAt = @attemptedAt
            WHERE RunId = @runId AND State = @pending
            """,
            ct,
            ("@attemptedAt", Format(DateTimeOffset.UtcNow)),
            ("@failureCode",
                PreparedTicketPublicationReconciliationFailureCodes
                    .PromotionRecoveryFailure),
            ("@detail", detail),
            ("@runId", runId),
            ("@pending",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending));
    }

    public async Task MarkPublicationReconciliationReadyAsync(
        string runId,
        CancellationToken ct = default)
    {
        PreparedTicketPublicationReconciliationComparison comparison =
            await GetPublicationReconciliationComparisonAsync(runId, ct)
            ?? throw new KeyNotFoundException(
                $"Publication reconciliation '{runId}' was not found.");
        EnsureCurrentPublicationReconciliationComparison(comparison);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            int updated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliation_journal
                SET State = @ready, LastRecoveryAttemptAt = @attemptedAt,
                    FailureCode = NULL, FailureDetail = NULL,
                    UpdatedAt = @attemptedAt
                WHERE RunId = @runId AND State IN (@pending, @ready)
                """,
                ct,
                ("@ready",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Ready),
                ("@attemptedAt", Format(DateTimeOffset.UtcNow)),
                ("@runId", runId),
                ("@pending",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .SnapshotPublishPending));
            if (updated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' journal changed during recovery.");
            }
            int reconciliationUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliations
                SET PromotionState = @ready
                WHERE RunId = @runId
                  AND PromotionState IN (@pending, @ready)
                """,
                ct,
                ("@ready",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Ready),
                ("@runId", runId),
                ("@pending",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .SnapshotPublishPending));
            if (reconciliationUpdated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' journal changed during recovery.");
            }
            foreach (string table in new[]
            {
                "prepared_ticket_publication_staged_graphs",
                "prepared_ticket_publication_staged_hydration",
                "prepared_ticket_publication_staged_receipts",
                "prepared_ticket_publication_grouping_impacts",
                "prepared_ticket_publication_staged_grouping",
                "prepared_ticket_publication_grouping_stage_receipts",
                "prepared_ticket_publication_snapshot_descriptors",
            })
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    $"DELETE FROM {table} WHERE RunId = @runId",
                    ct,
                    ("@runId", runId));
            }
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                DELETE FROM prepared_ticket_publication_reconciliation_fences
                WHERE RunId = @runId
                """,
                ct,
                ("@runId", runId));
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<bool>
        IsPublicationReconciliationStagingCompleteAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string runId,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                    reconciliation
                INNER JOIN authoring_runs run
                    ON run.Id = reconciliation.RunId
                WHERE reconciliation.RunId = @runId
                  AND run.Purpose = @purpose
                  AND NOT EXISTS(
                      SELECT 1
                      FROM authoring_run_items item
                      WHERE item.RunId = run.Id
                        AND item.Status <> @complete)
                  AND NOT EXISTS(
                      SELECT 1
                      FROM prepared_ticket_publication_reconciliation_items
                          decision
                      WHERE decision.RunId = run.Id
                        AND decision.Disposition = @reAuthor
                        AND NOT EXISTS(
                            SELECT 1
                            FROM authoring_run_items item
                            INNER JOIN
                                prepared_ticket_publication_staged_graphs graph
                              ON graph.RunId = item.RunId
                             AND graph.TicketKey =
                                 item.BusinessKey COLLATE NOCASE
                             AND graph.RunItemId = item.Id
                            INNER JOIN
                                prepared_ticket_publication_staged_hydration
                                    hydration
                              ON hydration.RunId = graph.RunId
                             AND hydration.TicketKey =
                                 graph.TicketKey COLLATE NOCASE
                            INNER JOIN
                                prepared_ticket_publication_staged_receipts
                                    stagedReceipt
                              ON stagedReceipt.RunId = graph.RunId
                             AND stagedReceipt.TicketKey =
                                 graph.TicketKey COLLATE NOCASE
                             AND stagedReceipt.RunItemId = item.Id
                             AND stagedReceipt.OperationId =
                                 graph.OperationId
                             AND stagedReceipt.AuthoredFingerprint =
                                 graph.AuthoredFingerprint
                            INNER JOIN authoring_result_receipts receipt
                              ON receipt.Id = stagedReceipt.ReceiptId
                             AND receipt.RunId = item.RunId
                             AND receipt.RunItemId = item.Id
                             AND receipt.OperationId = graph.OperationId
                             AND receipt.BusinessKey =
                                 item.BusinessKey COLLATE NOCASE
                             AND receipt.ContentHash =
                                 graph.AuthoredFingerprint
                             AND receipt.ExpectedSourceRevision =
                                 item.ExpectedSourceRevision
                             AND receipt.ObservedSourceRevision =
                                 graph.SourceRevision
                            WHERE item.RunId = decision.RunId
                              AND item.BusinessKey =
                                  decision.TicketKey COLLATE NOCASE
                              AND item.ItemKind = decision.ItemKind
                              AND item.ExpectedSourceRevision =
                                  decision.ExpectedSourceRevision
                              AND item.Status = @complete
                              AND item.AcceptedReceiptId =
                                  stagedReceipt.ReceiptId
                              AND graph.SourceRevision =
                                  decision.CurrentSourceRevision
                              AND length(hydration.HydrationFingerprint) > 0
                              AND length(hydration.HydrationJson) > 0)))
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@purpose",
            PreparedTicketPublicationReconciliationContract.Purpose);
        command.Parameters.AddWithValue(
            "@complete",
            AuthoringStatusValues.Items.Complete);
        command.Parameters.AddWithValue(
            "@reAuthor",
            PreparedTicketPublicationReconciliationDispositionValues
                .ReAuthor);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    private static async Task
        EnsurePublicationSnapshotReservationPreconditionsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string runId,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM authoring_runs run
            INNER JOIN prepared_ticket_publication_reconciliations
                reconciliation ON reconciliation.RunId = run.Id
            INNER JOIN prepared_ticket_publication_reconciliation_journal
                journal ON journal.RunId = run.Id
            WHERE run.Id = @runId
              AND run.Status IN (@running, @finalizing)
              AND run.Purpose = @purpose
              AND reconciliation.PromotionState = @staged
              AND journal.State = @staged
              AND NOT EXISTS(
                  SELECT 1 FROM authoring_run_items item
                  WHERE item.RunId = run.Id
                    AND item.Status <> @complete)
              AND NOT EXISTS(
                  SELECT 1 FROM authoring_run_stages stage
                  WHERE stage.RunId = run.Id
                    AND stage.Status <> @complete)
              AND EXISTS(
                  SELECT 1 FROM authoring_mutation_fences fence
                  WHERE fence.ProcessorKind = run.ProcessorKind
                    AND fence.RunId = run.Id)
              AND EXISTS(
                  SELECT 1
                  FROM prepared_ticket_publication_reconciliation_fences fence
                  WHERE fence.RunId = run.Id)
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@running",
            AuthoringStatusValues.Runs.Running);
        command.Parameters.AddWithValue(
            "@finalizing",
            AuthoringStatusValues.Runs.Finalizing);
        command.Parameters.AddWithValue(
            "@purpose",
            PreparedTicketPublicationReconciliationContract.Purpose);
        command.Parameters.AddWithValue(
            "@staged",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Staged);
        command.Parameters.AddWithValue(
            "@complete",
            AuthoringStatusValues.Items.Complete);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture) != 1 ||
            !await IsPublicationReconciliationStagingCompleteAsync(
                connection,
                transaction,
                runId,
                ct))
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' is incomplete or lost its mutation fence before snapshot reservation.");
        }
    }

    private static async Task
        EnsurePublicationCandidateReservationCurrentAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            PreparedTicketPublicationCandidateSnapshot candidate,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT run.ProcessorKind,
                   run.AuthoringEpoch,
                   (SELECT COUNT(*) FROM authoring_run_items
                    WHERE RunId = run.Id),
                   (SELECT COUNT(*)
                    FROM prepared_ticket_publication_reconciliation_items
                    WHERE RunId = run.Id),
                   (SELECT COALESCE(MAX(Sequence), 0) + 1
                    FROM authoring_review_snapshots
                    WHERE ProcessorKind = run.ProcessorKind),
                   (SELECT COUNT(*)
                    FROM authoring_review_snapshots
                    WHERE RunId = run.Id OR Id = @snapshotId)
            FROM authoring_runs run
            WHERE run.Id = @runId
            """;
        command.Parameters.AddWithValue("@runId", candidate.RunId);
        command.Parameters.AddWithValue(
            "@snapshotId",
            candidate.SnapshotId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new KeyNotFoundException(
                $"Authoring run '{candidate.RunId}' was not found.");
        }
        bool matches =
            string.Equals(
                reader.GetString(0),
                candidate.ProcessorKind,
                StringComparison.Ordinal) &&
            reader.GetInt64(1) == candidate.AuthoringEpoch &&
            reader.GetInt32(2) == candidate.ItemCount &&
            reader.GetInt32(3) == candidate.ReceiptCount &&
            reader.GetInt64(4) == candidate.Sequence &&
            reader.GetInt32(5) == 0;
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Reconciliation '{candidate.RunId}' reserved snapshot coordinates are no longer current.");
        }
    }

    private static AuthoringReviewSnapshotRecord CreateCandidateSnapshotRecord(
        PreparedTicketPublicationCandidateSnapshot candidate,
        string publicationProofJson)
        => new()
        {
            Id = candidate.SnapshotId,
            ProcessorKind = candidate.ProcessorKind,
            RunId = candidate.RunId,
            AuthoringEpoch = candidate.AuthoringEpoch,
            Sequence = candidate.Sequence,
            SchemaVersion = candidate.SchemaVersion,
            Status = AuthoringStatusValues.Snapshots.Creating,
            TempPath = candidate.TemporaryPath,
            Path = candidate.FinalPath,
            ChecksumSha256 = candidate.Sha256,
            SizeBytes = candidate.SizeBytes,
            ItemCount = candidate.ItemCount,
            ReceiptCount = candidate.ReceiptCount,
            TableCountsJson = JsonSerializer.Serialize(candidate.TableCounts),
            PublicationProofJson = publicationProofJson,
            CreatedAt = candidate.CreatedAt,
        };

    private static async Task<(string DescriptorJson, string Sha256)?>
        ReadPublicationCandidateDescriptorAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string runId,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DescriptorJson, Sha256
            FROM prepared_ticket_publication_snapshot_descriptors
            WHERE RunId = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<string?>
        ReadPublicationJournalDescriptorAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string runId,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT SnapshotDescriptorJson
            FROM prepared_ticket_publication_reconciliation_journal
            WHERE RunId = @runId AND State = @staged
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@staged",
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Staged);
        object? value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void EnsureReservationMatchesCurrent(
        PublicationReconciliationSnapshotReservation reservation,
        string runId,
        string processorKind,
        string temporaryPath,
        string finalPath,
        int schemaVersion,
        long sequence,
        long authoringEpoch,
        int itemCount,
        int receiptCount)
    {
        if (!string.Equals(
                reservation.ReservationKind,
                PublicationReconciliationSnapshotReservationKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                reservation.RunId,
                runId,
                StringComparison.Ordinal) ||
            !string.Equals(
                reservation.ProcessorKind,
                processorKind,
                StringComparison.Ordinal) ||
            !PathsEqual(reservation.TemporaryPath, temporaryPath) ||
            !PathsEqual(reservation.FinalPath, finalPath) ||
            reservation.SchemaVersion != schemaVersion ||
            reservation.Sequence != sequence ||
            reservation.AuthoringEpoch != authoringEpoch ||
            reservation.ItemCount != itemCount ||
            reservation.ReceiptCount != receiptCount ||
            reservation.CreatedAt == default ||
            reservation.Candidate is not null ||
            string.IsNullOrWhiteSpace(reservation.SnapshotId))
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' has conflicting snapshot reservation evidence.");
        }
    }

    private static void EnsureReservationMatchesCandidate(
        PublicationReconciliationSnapshotReservation reservation,
        PreparedTicketPublicationCandidateSnapshot candidate)
    {
        EnsureReservationMatchesCurrent(
            reservation,
            candidate.RunId,
            candidate.ProcessorKind,
            candidate.TemporaryPath,
            candidate.FinalPath,
            candidate.SchemaVersion,
            candidate.Sequence,
            candidate.AuthoringEpoch,
            candidate.ItemCount,
            candidate.ReceiptCount);
        if (!string.Equals(
                reservation.SnapshotId,
                candidate.SnapshotId,
                StringComparison.Ordinal) ||
            reservation.CreatedAt != candidate.CreatedAt)
        {
            throw new InvalidOperationException(
                $"Reconciliation '{candidate.RunId}' candidate does not match its snapshot reservation.");
        }
    }

    private static void ValidatePublicationCandidate(
        PreparedTicketPublicationCandidateSnapshot candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.SnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ProcessorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.TemporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.FinalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Sha256);
        ArgumentNullException.ThrowIfNull(candidate.TableCounts);
        if (!string.Equals(
                candidate.ProcessorKind,
                AuthoringProcessorKind,
                StringComparison.Ordinal) ||
            candidate.SchemaVersion != PreparedTicketSnapshotSchemaV3.Version ||
            candidate.Sequence < 1 ||
            candidate.AuthoringEpoch < 0 ||
            candidate.ItemCount < 0 ||
            candidate.ReceiptCount < 0 ||
            candidate.SizeBytes < 1 ||
            candidate.CreatedAt == default ||
            candidate.Sha256.Length != 64 ||
            candidate.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            candidate.TableCounts.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0) ||
            !candidate.TableCounts.Keys
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(PreparedTicketSnapshotSchemaV3.CountedTables) ||
            !Path.IsPathFullyQualified(candidate.TemporaryPath) ||
            !Path.IsPathFullyQualified(candidate.FinalPath) ||
            PathsEqual(candidate.TemporaryPath, candidate.FinalPath))
        {
            throw new ArgumentException(
                "The reconciliation candidate has invalid immutable snapshot coordinates.",
                nameof(candidate));
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static AuthoringSnapshotPublicationProof
        CreateSnapshotPublicationProof(
            PreparedTicketPublicationReconciliationProof proof)
        => new(
            PreparedTicketPublicationContract.CurrentVersion,
            proof.Purpose,
            proof.SourceRunId,
            PreparedTicketPublicationContract.JiraSourceName,
            proof.CapturedAt,
            long.TryParse(
                proof.StableJiraGeneration,
                CultureInfo.InvariantCulture,
                out long generation)
                ? generation
                : 0,
            0,
            proof.CorpusFingerprint,
            proof.GroupingImpactFingerprint,
            proof.CapturedAt);

    private static async Task EnsurePublicationPromotionPreconditionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM authoring_runs run
            WHERE run.Id = @runId
              AND run.Status = @finalizing
              AND run.Purpose = @purpose
              AND NOT EXISTS(
                  SELECT 1 FROM authoring_run_items item
                  WHERE item.RunId = run.Id
                    AND item.Status <> @complete)
              AND EXISTS(
                  SELECT 1 FROM authoring_mutation_fences fence
                  WHERE fence.ProcessorKind = run.ProcessorKind
                    AND fence.RunId = run.Id)
              AND EXISTS(
                  SELECT 1
                  FROM prepared_ticket_publication_reconciliation_fences fence
                  WHERE fence.RunId = run.Id)
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@finalizing",
            AuthoringStatusValues.Runs.Finalizing);
        command.Parameters.AddWithValue(
            "@purpose",
            PreparedTicketPublicationReconciliationContract.Purpose);
        command.Parameters.AddWithValue(
            "@complete",
            AuthoringStatusValues.Items.Complete);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture) != 1 ||
            !await IsPublicationReconciliationStagingCompleteAsync(
                connection,
                transaction,
                runId,
                ct))
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' is incomplete or lost its mutation fence.");
        }
    }

    private static async Task<PreparedTicketPublicationReconciliationProof>
        ReadPublicationReconciliationProofAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string runId,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT ProofJson
            FROM prepared_ticket_publication_reconciliation_proofs
            WHERE RunId = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        string? json = (string?)await command.ExecuteScalarAsync(ct);
        PreparedTicketPublicationReconciliationProof proof = json is null
            ? throw new InvalidOperationException(
                $"Reconciliation '{runId}' has no publication proof.")
            : JsonSerializer.Deserialize<
                PreparedTicketPublicationReconciliationProof>(json)
              ?? throw new InvalidOperationException(
                  $"Reconciliation '{runId}' has an invalid publication proof.");
        if (proof.ContractVersion !=
                PreparedTicketPublicationReconciliationContract
                    .CurrentVersion ||
            proof.Purpose !=
                PreparedTicketPublicationReconciliationContract.Purpose)
        {
            throw new NotSupportedException(
                $"Reconciliation '{runId}' does not have a current publication proof.");
        }
        return proof;
    }

    public async Task<(DateTimeOffset CancelledAt, string Reason)>
        CancelPublicationReconciliationAsync(
            string runId,
            string reason,
            DateTimeOffset? now = null,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string normalizedReason = reason.Trim();
        DateTimeOffset cancelledAt = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            string promotionState;
            string runStatus;
            string? journalState;
            string? journalDescriptor;
            DateTimeOffset? existingCancelledAt;
            string? existingReason;
            bool hasTrustedCandidate;
            bool hasCanonicalReplacement;
            await using (SqliteCommand state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText =
                    """
                    SELECT reconciliation.PromotionState,
                           run.Status,
                           journal.State,
                           journal.SnapshotDescriptorJson,
                           reconciliation.CancelledAt,
                           reconciliation.CancellationReason,
                           EXISTS(
                               SELECT 1
                               FROM prepared_ticket_publication_snapshot_descriptors
                               WHERE RunId = reconciliation.RunId),
                           EXISTS(
                               SELECT 1
                               FROM authoring_review_snapshots
                               WHERE RunId = reconciliation.RunId)
                           OR EXISTS(
                               SELECT 1
                               FROM prepared_ticket_authoring_state
                               WHERE RunId = reconciliation.RunId)
                    FROM prepared_ticket_publication_reconciliations
                        reconciliation
                    INNER JOIN authoring_runs run
                      ON run.Id = reconciliation.RunId
                    LEFT JOIN
                        prepared_ticket_publication_reconciliation_journal
                            journal
                      ON journal.RunId = reconciliation.RunId
                    WHERE reconciliation.RunId = @runId
                    """;
                state.Parameters.AddWithValue("@runId", runId);
                await using SqliteDataReader reader =
                    await state.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    throw new KeyNotFoundException(
                        $"Publication reconciliation '{runId}' was not found.");
                }
                promotionState = reader.GetString(0);
                runStatus = reader.GetString(1);
                journalState = reader.IsDBNull(2)
                    ? null
                    : reader.GetString(2);
                journalDescriptor = reader.IsDBNull(3)
                    ? null
                    : reader.GetString(3);
                existingCancelledAt = reader.IsDBNull(4)
                    ? null
                    : reader.GetDateTimeOffset(4);
                existingReason = reader.IsDBNull(5)
                    ? null
                    : reader.GetString(5);
                hasTrustedCandidate = reader.GetBoolean(6);
                hasCanonicalReplacement = reader.GetBoolean(7);
            }

            if (promotionState ==
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Cancelled)
            {
                if (existingCancelledAt is null ||
                    string.IsNullOrWhiteSpace(existingReason))
                {
                    throw new InvalidOperationException(
                        $"Cancelled reconciliation '{runId}' has no cancellation audit.");
                }
                await transaction.CommitAsync(ct);
                return (existingCancelledAt.Value, existingReason);
            }

            bool activeRun = runStatus is
                AuthoringStatusValues.Runs.Queued or
                AuthoringStatusValues.Runs.Running or
                AuthoringStatusValues.Runs.Finalizing or
                AuthoringStatusValues.Runs.Error;
            if (promotionState !=
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged ||
                journalState is not null &&
                journalState !=
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged ||
                hasTrustedCandidate ||
                hasCanonicalReplacement ||
                !activeRun)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' can only be cancelled while staged, before trusted candidate or canonical promotion.");
            }

            int reconciliationUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliations
                SET PromotionState = @cancelled,
                    CancelledAt = @cancelledAt,
                    CancellationReason = @reason
                WHERE RunId = @runId
                  AND PromotionState = @staged
                  AND NOT EXISTS(
                      SELECT 1
                      FROM prepared_ticket_publication_snapshot_descriptors
                      WHERE RunId = @runId)
                  AND NOT EXISTS(
                      SELECT 1
                      FROM authoring_review_snapshots
                      WHERE RunId = @runId)
                  AND NOT EXISTS(
                      SELECT 1
                      FROM prepared_ticket_authoring_state
                      WHERE RunId = @runId)
                """,
                ct,
                ("@cancelled",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Cancelled),
                ("@cancelledAt", Format(cancelledAt)),
                ("@reason", normalizedReason),
                ("@runId", runId),
                ("@staged",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged));
            if (reconciliationUpdated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' changed before cancellation.");
            }
            string? disposableTemporaryPath =
                ReadDisposableReconciliationTemporaryPath(
                    runId,
                    journalDescriptor);

            int journalUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO prepared_ticket_publication_reconciliation_journal(
                    RunId, State, SnapshotDescriptorJson,
                    LastRecoveryAttemptAt, FailureCode, FailureDetail,
                    UpdatedAt)
                VALUES(
                    @runId, @cancelled, NULL, NULL, NULL, @reason,
                    @cancelledAt)
                ON CONFLICT(RunId) DO UPDATE SET
                    State = excluded.State,
                    SnapshotDescriptorJson = NULL,
                    LastRecoveryAttemptAt = NULL,
                    FailureCode = NULL,
                    FailureDetail = excluded.FailureDetail,
                    UpdatedAt = excluded.UpdatedAt
                WHERE prepared_ticket_publication_reconciliation_journal.State
                    = @staged
                """,
                ct,
                ("@runId", runId),
                ("@cancelled",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Cancelled),
                ("@reason", normalizedReason),
                ("@cancelledAt", Format(cancelledAt)),
                ("@staged",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Staged));
            if (journalUpdated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' promotion journal changed before cancellation.");
            }

            string cancellationError =
                $"cancelled: {normalizedReason}";
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE authoring_run_attempts
                SET Status = @superseded,
                    CompletedAt = COALESCE(CompletedAt, @cancelledAt),
                    Error = COALESCE(Error, @error)
                WHERE RunId = @runId AND Status = @active
                """,
                ct,
                ("@superseded", AuthoringStatusValues.Attempts.Superseded),
                ("@cancelledAt", Format(cancelledAt)),
                ("@error", cancellationError),
                ("@runId", runId),
                ("@active", AuthoringStatusValues.Attempts.Active));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE authoring_run_items
                SET Status = @superseded,
                    CompletedAt = COALESCE(CompletedAt, @cancelledAt),
                    PostPersistenceLeaseId = NULL,
                    PostPersistenceLeaseAcquiredAt = NULL,
                    Error = CASE
                        WHEN Status = @complete THEN Error
                        ELSE @error
                    END
                WHERE RunId = @runId
                """,
                ct,
                ("@complete", AuthoringStatusValues.Items.Complete),
                ("@superseded", AuthoringStatusValues.Items.Superseded),
                ("@cancelledAt", Format(cancelledAt)),
                ("@error", cancellationError),
                ("@runId", runId));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE authoring_run_stages
                SET Status = @errorStatus,
                    CompletedAt = COALESCE(CompletedAt, @cancelledAt),
                    LeaseId = NULL,
                    LeaseAcquiredAt = NULL,
                    Error = @error
                WHERE RunId = @runId AND Status <> @complete
                """,
                ct,
                ("@errorStatus", AuthoringStatusValues.Stages.Error),
                ("@cancelledAt", Format(cancelledAt)),
                ("@error", cancellationError),
                ("@runId", runId),
                ("@complete", AuthoringStatusValues.Stages.Complete));

            foreach (string table in new[]
            {
                "prepared_ticket_publication_staged_graphs",
                "prepared_ticket_publication_staged_hydration",
                "prepared_ticket_publication_staged_receipts",
                "prepared_ticket_publication_grouping_impacts",
                "prepared_ticket_publication_staged_grouping",
                "prepared_ticket_publication_grouping_stage_receipts",
                "prepared_ticket_publication_unaffected_fingerprints",
                "prepared_ticket_publication_reconciliation_proofs",
            })
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    $"DELETE FROM {table} WHERE RunId = @runId",
                    ct,
                    ("@runId", runId));
            }
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                DELETE FROM prepared_ticket_publication_reconciliation_fences
                WHERE RunId = @runId
                """,
                ct,
                ("@runId", runId));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                DELETE FROM authoring_mutation_fences
                WHERE ProcessorKind = @processorKind AND RunId = @runId
                """,
                ct,
                ("@processorKind", AuthoringProcessorKind),
                ("@runId", runId));

            int runUpdated = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE authoring_runs
                SET Status = @superseded,
                    CompletedAt = @cancelledAt,
                    Error = @error
                WHERE Id = @runId AND Status = @expectedStatus
                """,
                ct,
                ("@superseded", AuthoringStatusValues.Runs.Superseded),
                ("@cancelledAt", Format(cancelledAt)),
                ("@error", cancellationError),
                ("@runId", runId),
                ("@expectedStatus", runStatus));
            if (runUpdated != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' authoring run changed before cancellation.");
            }
            if (disposableTemporaryPath is not null &&
                File.Exists(disposableTemporaryPath))
            {
                try
                {
                    File.Delete(disposableTemporaryPath);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"Reconciliation '{runId}' provisional candidate could not be deleted.",
                        ex);
                }
            }

            await transaction.CommitAsync(ct);
            return (cancelledAt, normalizedReason);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string? ReadDisposableReconciliationTemporaryPath(
        string runId,
        string? journalDescriptor)
    {
        if (journalDescriptor is null)
        {
            return null;
        }
        PublicationReconciliationSnapshotReservation reservation;
        try
        {
            reservation = JsonSerializer.Deserialize<
                PublicationReconciliationSnapshotReservation>(
                    journalDescriptor)
                ?? throw new InvalidOperationException(
                    $"Reconciliation '{runId}' has an empty staged snapshot reservation.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' has an invalid staged snapshot reservation.",
                ex);
        }
        if (!string.Equals(
                reservation.ReservationKind,
                PublicationReconciliationSnapshotReservationKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                reservation.RunId,
                runId,
                StringComparison.Ordinal) ||
            reservation.Candidate is not null ||
            !Path.IsPathFullyQualified(reservation.TemporaryPath) ||
            !Path.IsPathFullyQualified(reservation.FinalPath) ||
            !Path.GetFileName(reservation.TemporaryPath).EndsWith(
                $"{runId}.reconciliation.tmp",
                StringComparison.Ordinal) ||
            PathsEqual(
                reservation.TemporaryPath,
                reservation.FinalPath))
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' has conflicting staged snapshot reservation evidence.");
        }
        return Path.GetFullPath(reservation.TemporaryPath);
    }

    public async Task CleanupPublicationReconciliationWorkspaceAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            string? promotionState;
            string? journalState;
            bool abandoned;
            await using (SqliteCommand state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText =
                    """
                    SELECT reconciliation.PromotionState,
                           journal.State,
                           reconciliation.AbandonedAt IS NOT NULL
                    FROM prepared_ticket_publication_reconciliations reconciliation
                    LEFT JOIN prepared_ticket_publication_reconciliation_journal journal
                      ON journal.RunId = reconciliation.RunId
                    WHERE reconciliation.RunId = @runId
                    """;
                state.Parameters.AddWithValue("@runId", runId);
                await using SqliteDataReader reader =
                    await state.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    await transaction.CommitAsync(ct);
                    return;
                }
                promotionState = reader.GetString(0);
                journalState = reader.IsDBNull(1)
                    ? null
                    : reader.GetString(1);
                abandoned = reader.GetBoolean(2);
            }
            if (!abandoned &&
                promotionState !=
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Ready &&
                promotionState !=
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .Cancelled)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' workspace is still required.");
            }
            if (journalState ==
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' has a pending promotion journal.");
            }
            foreach (string table in new[]
            {
                "prepared_ticket_publication_staged_graphs",
                "prepared_ticket_publication_staged_hydration",
                "prepared_ticket_publication_staged_receipts",
                "prepared_ticket_publication_grouping_impacts",
                "prepared_ticket_publication_staged_grouping",
                "prepared_ticket_publication_grouping_stage_receipts",
                "prepared_ticket_publication_snapshot_descriptors",
                "prepared_ticket_publication_reconciliation_fences",
            })
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    $"DELETE FROM {table} WHERE RunId = @runId",
                    ct,
                    ("@runId", runId));
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<PreparedTicketPublicationCorpusOverlay>
        GetPublicationReconciliationCorpusAsync(
            string runId,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationReconciliationComparison comparison =
            await GetPublicationReconciliationComparisonAsync(runId, ct)
            ?? throw new KeyNotFoundException(
                $"Publication reconciliation '{runId}' was not found.");
        EnsureCurrentPublicationReconciliationComparison(comparison);
        List<PreparedTicketPublicationCorpusTicket> tickets =
            new(comparison.Items.Count);
        await using SqliteConnection connection = OpenConnection();
        foreach (PreparedTicketPublicationReconciliationItemDecision decision in
                 comparison.Items.OrderBy(
                     item => item.TicketKey,
                     StringComparer.OrdinalIgnoreCase))
        {
            tickets.Add(decision.Disposition ==
                PreparedTicketPublicationReconciliationDispositionValues
                    .CarryForward
                ? await ReadCarriedCorpusTicketAsync(
                    connection,
                    decision,
                    ct)
                : await ReadStagedCorpusTicketAsync(
                    connection,
                    runId,
                    decision,
                    ct));
        }
        if (tickets.Select(ticket => ticket.TicketKey)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            comparison.Items.Count)
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' overlay contains duplicate or missing tickets.");
        }
        string corpusFingerprint = PreparedTicketPublicationContract
            .ComputeCorpusFingerprint(
                tickets.Select(ticket => ticket.ToPublicationCorpusItem()));
        return new PreparedTicketPublicationCorpusOverlay(
            runId,
            corpusFingerprint,
            tickets);
    }

    public async Task AbandonPublicationReconciliationAsync(
        string runId,
        string reason,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DateTimeOffset abandonedAt = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        try
        {
            int abandoned = await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliations
                SET PromotionState = @state,
                    AbandonedAt = @abandonedAt,
                    AbandonmentReason = @reason
                WHERE RunId = @runId
                  AND PromotionState = @pending
                """,
                ct,
                ("@state",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .CanonicalUnpublished),
                ("@abandonedAt", Format(abandonedAt)),
                ("@reason", reason),
                ("@runId", runId),
                ("@pending",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .SnapshotPublishPending));
            if (abandoned != 1)
            {
                throw new InvalidOperationException(
                    $"Reconciliation '{runId}' can only be abandoned after canonical promotion.");
            }
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE prepared_ticket_publication_reconciliation_journal
                SET State = @state, UpdatedAt = @abandonedAt,
                    LastRecoveryAttemptAt = @abandonedAt,
                    FailureCode = NULL, FailureDetail = @reason
                WHERE RunId = @runId AND State = @pending
                """,
                ct,
                ("@state",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .CanonicalUnpublished),
                ("@abandonedAt", Format(abandonedAt)),
                ("@reason", reason),
                ("@runId", runId),
                ("@pending",
                    PreparedTicketPublicationReconciliationPromotionStateValues
                        .SnapshotPublishPending));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                DELETE FROM prepared_ticket_publication_reconciliation_fences
                WHERE RunId = @runId
                """,
                ct,
                ("@runId", runId));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                DELETE FROM authoring_mutation_fences
                WHERE ProcessorKind = @processorKind AND RunId = @runId
                """,
                ct,
                ("@processorKind", AuthoringProcessorKind),
                ("@runId", runId));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE authoring_runs
                SET Status = @status, CompletedAt = @abandonedAt,
                    Error = @reason
                WHERE Id = @runId
                  AND Status IN (@finalizing, @error)
                """,
                ct,
                ("@status", AuthoringStatusValues.Runs.Error),
                ("@abandonedAt", Format(abandonedAt)),
                ("@reason",
                    $"canonical-unpublished: {reason}"),
                ("@runId", runId),
                ("@finalizing", AuthoringStatusValues.Runs.Finalizing),
                ("@error", AuthoringStatusValues.Runs.Error));
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<
        PreparedTicketPublicationReconciliationGroupingImpact>>
        GetPublicationReconciliationGroupingImpactsAsync(
            string runId,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ReadGroupingImpactsAsync(connection, runId, ct);
    }

    public async Task ApplyPublicationReconciliationOverlayAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        PreparedTicketPublicationReconciliationComparison comparison;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT ComparisonJson
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", runId);
            string? json = (string?)await command.ExecuteScalarAsync(ct);
            comparison = json is null
                ? throw new KeyNotFoundException(
                    $"Publication reconciliation '{runId}' was not found.")
                : JsonSerializer.Deserialize<
                    PreparedTicketPublicationReconciliationComparison>(json)
                  ?? throw new InvalidOperationException(
                      $"Reconciliation '{runId}' has an invalid comparison.");
        }
        EnsureCurrentPublicationReconciliationComparison(comparison);
        foreach (PreparedTicketPublicationReconciliationItemDecision decision
                 in comparison.Items.Where(item => item.Disposition ==
                     PreparedTicketPublicationReconciliationDispositionValues
                         .ReAuthor))
        {
            PreparedTicketPublicationCorpusTicket ticket =
                await ReadStagedCorpusTicketAsync(
                    connection,
                    runId,
                    decision,
                    ct);
            await SavePreparedTicketCoreAsync(
                connection,
                ticket.Payload,
                ticket.Payload.SavedAt ?? DateTimeOffset.UtcNow,
                ct);
            await SaveHydrationCoreAsync(
                connection,
                ticket.Hydration,
                ct);
            string graphHash = await ComputePreparedGraphHashAsync(
                connection,
                ticket.TicketKey,
                ct);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_authoring_state(
                    TicketKey, Classification, GraphHash,
                    ReceiptContentHash, RunId, RunItemId, OperationId,
                    UpdatedAt)
                SELECT graph.TicketKey, 'receipt-backed',
                       @graphHash,
                       graph.AuthoredFingerprint, graph.RunId,
                       graph.RunItemId, graph.OperationId, graph.StagedAt
                FROM prepared_ticket_publication_staged_graphs graph
                WHERE graph.RunId = @runId
                  AND graph.TicketKey = @ticketKey COLLATE NOCASE
                ON CONFLICT(TicketKey) DO UPDATE SET
                    Classification = excluded.Classification,
                    GraphHash = excluded.GraphHash,
                    ReceiptContentHash = excluded.ReceiptContentHash,
                    RunId = excluded.RunId,
                    RunItemId = excluded.RunItemId,
                    OperationId = excluded.OperationId,
                    UpdatedAt = excluded.UpdatedAt
                """,
                ct,
                ("@graphHash", graphHash),
                ("@runId", runId),
                ("@ticketKey", ticket.TicketKey));
        }
        List<PreparedTicketPublicationReconciliationGroupingImpact> impacts =
            await ReadGroupingImpactsAsync(connection, runId, ct);
        if (impacts.Any(impact => !impact.Complete))
        {
            throw new InvalidOperationException(
                $"Reconciliation '{runId}' grouping closure is incomplete.");
        }
        foreach (PreparedTicketPublicationReconciliationGroupingImpact impact
                 in impacts)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT ReplacementJson, CorpusFingerprint,
                       OutputFingerprint, ProtectedRowsFingerprint
                FROM prepared_ticket_publication_staged_grouping
                WHERE RunId = @runId AND PartitionKey = @partitionKey
                """;
            command.Parameters.AddWithValue("@runId", runId);
            command.Parameters.AddWithValue(
                "@partitionKey",
                impact.PartitionKey);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException(
                    $"Grouping impact '{impact.PartitionKey}' has no complete replacement.");
            }
            string replacementJson = reader.GetString(0);
            if (!string.Equals(
                    reader.GetString(1),
                    impact.StagedCorpusFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(2),
                    impact.StagedOutputFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(3),
                    impact.StagedProtectedRowsFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Grouping impact '{impact.PartitionKey}' replacement fingerprints diverged.");
            }
            PreparedTicketGroupingPayload payload =
                JsonSerializer.Deserialize<PreparedTicketGroupingPayload>(
                    replacementJson)
                ?? throw new InvalidOperationException(
                    $"Grouping impact '{impact.PartitionKey}' has an invalid replacement.");
            await reader.DisposeAsync();
            PreparedTicketGroupingPayloadValidator.ThrowIfInvalid(payload);
            string outputFingerprint = PreparedTicketPublicationContract
                .ComputeGroupingOutputFingerprint(payload);
            if (!string.Equals(
                    outputFingerprint,
                    impact.StagedOutputFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    AuthoringResultHasher.HashNormalizedUtf8(
                        replacementJson),
                    impact.StagedProtectedRowsFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Grouping impact '{impact.PartitionKey}' replacement content diverged.");
            }
            await SaveGroupingCoreAsync(
                connection,
                payload,
                payload.SavedAt ?? DateTimeOffset.UtcNow,
                ct);
        }
    }

    private static async Task SaveHydrationCoreAsync(
        SqliteConnection connection,
        PreparedTicketHydrationBatch batch,
        CancellationToken ct)
    {
        PreparedTicketHydrationRow parent =
            NormalizeTrustedPeople(batch.Parent);
        PreparedJiraHydrationRow[] jiraRows = batch.JiraRows
            .Select(NormalizeTrustedPeople)
            .ToArray();
        IReadOnlyList<PreparedTicketInPersonRequesterRow> requesters =
            NormalizeInPersonRequesters(
                batch.TicketKey,
                batch.InPersonRequesters);
        await DeleteHydrationRowsAsync(connection, batch.TicketKey, ct);
        await InsertHydrationParentAsync(connection, parent, ct);
        await ReplaceCanonicalJiraFieldsAsync(connection, parent, ct);
        foreach (PreparedTicketInPersonRequesterRow row in requesters)
        {
            await InsertInPersonRequesterAsync(connection, row, ct);
        }
        foreach (PreparedJiraHydrationRow row in jiraRows)
        {
            await InsertJiraHydrationAsync(connection, row, ct);
        }
        foreach (PreparedZulipHydrationRow row in batch.ZulipRows)
        {
            await InsertZulipHydrationAsync(connection, row, ct);
        }
        foreach (PreparedGitHubHydrationRow row in batch.GitHubRows)
        {
            await InsertGitHubHydrationAsync(connection, row, ct);
        }
        foreach (PreparedRepoHydrationRow row in batch.RepoRows)
        {
            await InsertRepoHydrationAsync(connection, row, ct);
        }
        foreach (PreparedTicketJiraXrefRow row in batch.JiraXrefRows)
        {
            await InsertJiraXrefAsync(connection, row, ct);
        }
    }

    private static async Task<List<
        PreparedTicketPublicationReconciliationGroupingImpact>>
        ReadGroupingImpactsAsync(
            SqliteConnection connection,
            string runId,
            CancellationToken ct)
    {
        List<PreparedTicketPublicationReconciliationGroupingImpact> impacts =
            [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ImpactJson
            FROM prepared_ticket_publication_grouping_impacts
            WHERE RunId = @runId
            ORDER BY PartitionKey
            """;
        command.Parameters.AddWithValue("@runId", runId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            impacts.Add(JsonSerializer.Deserialize<
                PreparedTicketPublicationReconciliationGroupingImpact>(
                    reader.GetString(0))
                ?? throw new InvalidOperationException(
                    $"Reconciliation '{runId}' has an invalid grouping impact."));
        }
        return impacts;
    }

    private static async Task<
        PreparedTicketPublicationReconciliationGroupingImpact?>
        ReadGroupingImpactAsync(
            SqliteConnection connection,
            string runId,
            string partitionKey,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ImpactJson
            FROM prepared_ticket_publication_grouping_impacts
            WHERE RunId = @runId AND PartitionKey = @partitionKey
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@partitionKey", partitionKey);
        string? json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null
            ? null
            : JsonSerializer.Deserialize<
                PreparedTicketPublicationReconciliationGroupingImpact>(json)
              ?? throw new InvalidOperationException(
                  $"Reconciliation '{runId}' has an invalid grouping impact.");
    }

    private static PreparedTicketPublicationUnaffectedFingerprint
        CreateUnaffectedFingerprint(
            string runId,
            PreparedTicketPublicationProtectedInventory inventory,
            IReadOnlySet<string> revisedTicketKeys,
            IReadOnlySet<string> impactedPartitionKeys,
            DateTimeOffset capturedAt)
    {
        PreparedTicketPublicationProtectedRow[] authoredRows = inventory.Rows
            .Where(row =>
                !revisedTicketKeys.Contains(row.Scope) &&
                !(row.Table == "authoring_runs" &&
                  string.Equals(
                      row.Scope,
                      runId,
                      StringComparison.Ordinal)))
            .ToArray();
        PreparedTicketPublicationCorpusItem[] receipts = inventory.Corpus
            .Where(item => !revisedTicketKeys.Contains(item.TicketKey))
            .ToArray();
        PreparedTicketPublicationProtectedGroupingFingerprint[] grouping =
            inventory.Grouping
                .Where(partition =>
                    !impactedPartitionKeys.Contains(partition.PartitionKey))
                .Select(partition => partition.Fingerprint)
                .ToArray();
        string authored = PreparedTicketPublicationEnrichmentContract
            .ComputeProtectedContentFingerprint(authoredRows);
        string receipt = PreparedTicketPublicationContract
            .ComputeCorpusFingerprint(receipts);
        string groupingRows = PreparedTicketPublicationEnrichmentContract
            .ComputeRetainedGroupingFingerprint(grouping);
        string combined = AuthoringResultHasher.HashNormalizedUtf8(
            $"{authored}\n{receipt}\n{groupingRows}");
        return new(
            runId,
            impactedPartitionKeys.Order(StringComparer.Ordinal).ToArray(),
            authored,
            receipt,
            groupingRows,
            combined,
            capturedAt);
    }

    private static async Task SaveOrValidateUnaffectedFingerprintAsync(
        SqliteConnection connection,
        PreparedTicketPublicationUnaffectedFingerprint fingerprint,
        CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(fingerprint);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_publication_unaffected_fingerprints(
                RunId, FingerprintJson, CapturedAt)
            SELECT @runId, @json, @capturedAt
            WHERE EXISTS(
                SELECT 1
                FROM prepared_ticket_publication_reconciliations
                    reconciliation
                INNER JOIN authoring_runs run
                  ON run.Id = reconciliation.RunId
                INNER JOIN authoring_mutation_fences mutationFence
                  ON mutationFence.ProcessorKind = run.ProcessorKind
                 AND mutationFence.RunId = run.Id
                INNER JOIN
                    prepared_ticket_publication_reconciliation_fences
                        reconciliationFence
                  ON reconciliationFence.RunId = run.Id
                WHERE reconciliation.RunId = @runId
                  AND reconciliation.PromotionState = @staged)
            ON CONFLICT(RunId) DO NOTHING
            """,
            ct,
            ("@runId", fingerprint.RunId),
            ("@json", json),
            ("@capturedAt", Format(fingerprint.CapturedAt)),
            ("@staged",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .Staged));
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FingerprintJson
            FROM prepared_ticket_publication_unaffected_fingerprints
            WHERE RunId = @runId
            """;
        command.Parameters.AddWithValue("@runId", fingerprint.RunId);
        string stored = (string)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "The unaffected-row fingerprint was not persisted."));
        PreparedTicketPublicationUnaffectedFingerprint expected =
            JsonSerializer.Deserialize<
                PreparedTicketPublicationUnaffectedFingerprint>(stored)
            ?? throw new InvalidOperationException(
                "The unaffected-row fingerprint is invalid.");
        if (!UnaffectedFingerprintsEqual(
                expected with { CapturedAt = fingerprint.CapturedAt },
                fingerprint))
        {
            throw new InvalidOperationException(
                "Canonical rows outside the reconciliation impact closure changed.");
        }
    }

    private static void EnsureCurrentPublicationReconciliationComparison(
        PreparedTicketPublicationReconciliationComparison comparison)
    {
        if (comparison.ContractVersion !=
            PreparedTicketPublicationReconciliationContract.CurrentVersion)
        {
            throw new NotSupportedException(
                $"Reconciliation contract version {comparison.ContractVersion} is readable for status and audit only.");
        }
        foreach (PreparedTicketPublicationReconciliationItemDecision item in
                 comparison.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ItemKind) ||
                string.IsNullOrWhiteSpace(item.ExpectedSourceRevision))
            {
                throw new InvalidOperationException(
                    $"Reconciliation item '{item.TicketKey}' is missing its contract-v{PreparedTicketPublicationReconciliationContract.CurrentVersion} publication corpus coordinate.");
            }
            string selectedSourceRevision =
                item.Disposition ==
                    PreparedTicketPublicationReconciliationDispositionValues
                        .CarryForward
                    ? item.BaselineSourceRevision
                    : item.CurrentSourceRevision;
            if (!string.Equals(
                    item.ExpectedSourceRevision,
                    selectedSourceRevision,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Reconciliation item '{item.TicketKey}' does not retain its selected source revision.");
            }
        }
    }

    private static bool UnaffectedFingerprintsEqual(
        PreparedTicketPublicationUnaffectedFingerprint left,
        PreparedTicketPublicationUnaffectedFingerprint right)
        => string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
           left.ImpactedPartitionKeys.SequenceEqual(
               right.ImpactedPartitionKeys,
               StringComparer.Ordinal) &&
           string.Equals(
               left.AuthoredRowsFingerprint,
               right.AuthoredRowsFingerprint,
               StringComparison.Ordinal) &&
           string.Equals(
               left.ReceiptCoordinatesFingerprint,
               right.ReceiptCoordinatesFingerprint,
               StringComparison.Ordinal) &&
           string.Equals(
               left.GroupingRowsFingerprint,
               right.GroupingRowsFingerprint,
               StringComparison.Ordinal) &&
           string.Equals(
               left.CombinedFingerprint,
               right.CombinedFingerprint,
               StringComparison.Ordinal) &&
           left.CapturedAt.Equals(right.CapturedAt);

    private static bool OverlayTicketHasPartition(
        PreparedTicketPublicationCorpusOverlay overlay,
        string ticketKey,
        string partitionKey)
    {
        PreparedTicketPublicationCorpusTicket ticket = overlay.Tickets.Single(
            value => string.Equals(
                value.TicketKey,
                ticketKey,
                StringComparison.OrdinalIgnoreCase));
        PreparedJiraHydrationRow? self = ticket.Hydration.JiraRows
            .SingleOrDefault(row => string.Equals(
                row.JiraKey,
                ticketKey,
                StringComparison.OrdinalIgnoreCase));
        if (self is null || string.IsNullOrWhiteSpace(self.Type))
        {
            return false;
        }
        string workGroupClean = Hl7WorkGroupNameCleaner.Clean(self.WorkGroup);
        return !string.IsNullOrWhiteSpace(workGroupClean) &&
            string.Equals(
                GetPartitionKey(
                    workGroupClean,
                    NormalizePartitionValue(
                        self.Specification,
                        "Unspecified"),
                    NormalizePartitionValue(self.Type, string.Empty)),
                partitionKey,
                StringComparison.Ordinal);
    }

    private static string PreparedTicketGroupingDeltaFingerprint(
        IEnumerable<string> ticketKeys)
        => AuthoringResultHasher.HashNormalizedUtf8(string.Join(
            "\n",
            ticketKeys.Order(StringComparer.Ordinal)));

    private static string PreparedTicketGroupingDeltaEmptyOutputFingerprint(
        string partitionKey)
    {
        string[] coordinates = partitionKey.Split('\u001f');
        if (coordinates.Length != 3)
        {
            throw new InvalidOperationException(
                $"Grouping partition key '{partitionKey}' is invalid.");
        }
        return PreparedTicketPublicationContract
            .ComputeGroupingOutputFingerprint(new()
            {
                WorkGroupClean = coordinates[0],
                WorkGroupDisplay = coordinates[0],
                Specification = coordinates[1],
                Type = coordinates[2],
                Topics = [],
            });
    }

    private static string PreparedTicketEnrichmentEmptyRowsFingerprint()
        => PreparedTicketPublicationEnrichmentContract
            .ComputeProtectedContentFingerprint(
                Array.Empty<PreparedTicketPublicationProtectedRow>());

    private static void ValidateStagedHydration(
        string ticketKey,
        PreparedTicketHydrationBatch hydration)
    {
        if (!string.Equals(
                ticketKey,
                hydration.TicketKey,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                ticketKey,
                hydration.Parent.TicketKey,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Staged graph and hydration ticket keys must match.",
                nameof(hydration));
        }
        IEnumerable<string> childKeys =
            hydration.JiraRows.Select(row => row.TicketKey)
                .Concat(hydration.ZulipRows.Select(row => row.TicketKey))
                .Concat(hydration.GitHubRows.Select(row => row.TicketKey))
                .Concat(hydration.RepoRows.Select(row => row.TicketKey))
                .Concat(hydration.JiraXrefRows.Select(row => row.TicketKey))
                .Concat((hydration.InPersonRequesters ?? [])
                    .Select(row => row.TicketKey));
        if (childKeys.Any(key => !string.Equals(
                key,
                ticketKey,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "A staged hydration child belongs to a different ticket.",
                nameof(hydration));
        }
        if (hydration.JiraRows
                .Select(row => row.JiraKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            hydration.JiraRows.Count ||
            hydration.ZulipRows
                .Select(row => row.ZulipThreadId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            hydration.ZulipRows.Count ||
            hydration.GitHubRows
                .Select(row => row.GitHubItemId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            hydration.GitHubRows.Count ||
            hydration.RepoRows
                .Select(row => row.Repo)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            hydration.RepoRows.Count)
        {
            throw new ArgumentException(
                "A staged hydration graph contains duplicate child rows.",
                nameof(hydration));
        }
    }

    private static async Task<string> ComputeGroupingOutputFingerprintAsync(
        SqliteConnection connection,
        string partitionKey,
        CancellationToken ct)
    {
        PreparedTicketGroupingPayload payload =
            await ReadCanonicalGroupingPayloadAsync(
                connection,
                partitionKey,
                ct);
        return PreparedTicketPublicationContract
            .ComputeGroupingOutputFingerprint(payload);
    }

    private static async Task<PreparedTicketGroupingPayload>
        ReadCanonicalGroupingPayloadAsync(
            SqliteConnection connection,
            string partitionKey,
            CancellationToken ct)
    {
        string[] coordinates = partitionKey.Split('\u001f');
        if (coordinates.Length != 3 ||
            coordinates.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                $"Grouping partition key '{partitionKey}' is invalid.",
                nameof(partitionKey));
        }
        string workGroupClean = coordinates[0].Trim();
        string specification = coordinates[1].Trim();
        string type = coordinates[2].Trim();

        List<GroupingFingerprintTopicRow> topicRows = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT RowId, WorkGroupDisplay, ShortDescription,
                       LongerDescription, RenderOrderHint
                FROM prepared_ticket_topics
                WHERE WorkGroupClean = @workGroupClean
                  AND Specification = @specification
                  AND Type = @type
                ORDER BY RowId
                """;
            command.Parameters.AddWithValue(
                "@workGroupClean",
                workGroupClean);
            command.Parameters.AddWithValue(
                "@specification",
                specification);
            command.Parameters.AddWithValue("@type", type);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                topicRows.Add(
                    new GroupingFingerprintTopicRow(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3)
                            ? string.Empty
                            : reader.GetString(3),
                        reader.IsDBNull(4)
                            ? null
                            : checked((int)reader.GetInt64(4))));
            }
        }

        string workGroupDisplay;
        if (topicRows.Count > 0)
        {
            workGroupDisplay = topicRows[0].WorkGroupDisplay;
            if (topicRows.Any(topic => !string.Equals(
                    topic.WorkGroupDisplay,
                    workGroupDisplay,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Grouping partition '{partitionKey}' has inconsistent workgroup display values.");
            }
        }
        else
        {
            List<string> displayValues = [];
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT DISTINCT TRIM(j.WorkGroup)
                FROM prepared_tickets ticket
                INNER JOIN prepared_jira_hydration j
                    ON j.TicketKey = ticket.Key COLLATE NOCASE
                   AND j.JiraKey = j.TicketKey COLLATE NOCASE
                WHERE j.WorkGroupClean = @workGroupClean
                  AND COALESCE(
                        NULLIF(TRIM(j.Specification), ''),
                        'Unspecified') = @specification
                  AND j.Type = @type
                  AND NULLIF(TRIM(j.WorkGroup), '') IS NOT NULL
                ORDER BY TRIM(j.WorkGroup)
                """;
            command.Parameters.AddWithValue(
                "@workGroupClean",
                workGroupClean);
            command.Parameters.AddWithValue(
                "@specification",
                specification);
            command.Parameters.AddWithValue("@type", type);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                displayValues.Add(reader.GetString(0));
            }
            if (displayValues.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Grouping partition '{partitionKey}' has no unique workgroup display value.");
            }
            workGroupDisplay = displayValues[0];
        }

        PreparedTicketGroupingPayload payload = new()
        {
            WorkGroupClean = workGroupClean,
            WorkGroupDisplay = workGroupDisplay,
            Specification = specification,
            Type = type,
            Topics = [],
        };
        foreach (GroupingFingerprintTopicRow topicRow in topicRows)
        {
            PreparedTicketTopicPayload topic = new()
            {
                ShortDescription = topicRow.ShortDescription,
                LongerDescription = topicRow.LongerDescription,
                RenderOrderHint = topicRow.RenderOrderHint,
                LinkedTicketGroups = [],
                RemainingTicketKeys = [],
            };

            List<GroupingFingerprintGroupRow> groupRows = [];
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT RowId, FirstTicketKey, Rationale, OrderInTopic
                    FROM prepared_ticket_topic_groups
                    WHERE TopicRowId = @topicRowId
                    ORDER BY OrderInTopic, RowId
                    """;
                command.Parameters.AddWithValue(
                    "@topicRowId",
                    topicRow.RowId);
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    groupRows.Add(
                        new GroupingFingerprintGroupRow(
                            reader.GetInt64(0),
                            reader.GetString(1),
                            reader.IsDBNull(2)
                                ? string.Empty
                                : reader.GetString(2),
                            checked((int)reader.GetInt64(3))));
                }
            }

            for (int groupIndex = 0;
                 groupIndex < groupRows.Count;
                 groupIndex++)
            {
                GroupingFingerprintGroupRow groupRow =
                    groupRows[groupIndex];
                if (groupRow.OrderInTopic != groupIndex)
                {
                    throw new InvalidOperationException(
                        $"Grouping topic {topicRow.RowId} has non-canonical group order.");
                }
                PreparedTicketTopicGroupPayload group = new()
                {
                    FirstTicketKey = groupRow.FirstTicketKey,
                    Rationale = groupRow.Rationale,
                    Members = [],
                };
                await using SqliteCommand command =
                    connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT TicketKey, OrderInContainer
                    FROM prepared_ticket_topic_members
                    WHERE TopicRowId = @topicRowId
                      AND TopicGroupRowId = @groupRowId
                    ORDER BY OrderInContainer,
                             TicketKey COLLATE NOCASE, TicketKey
                    """;
                command.Parameters.AddWithValue(
                    "@topicRowId",
                    topicRow.RowId);
                command.Parameters.AddWithValue(
                    "@groupRowId",
                    groupRow.RowId);
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    group.Members.Add(
                        new PreparedTicketTopicGroupMemberPayload
                        {
                            TicketKey = reader.GetString(0),
                            Order = checked((int)reader.GetInt64(1)),
                        });
                }
                topic.LinkedTicketGroups.Add(group);
            }

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT TicketKey, OrderInContainer
                    FROM prepared_ticket_topic_members
                    WHERE TopicRowId = @topicRowId
                      AND TopicGroupRowId IS NULL
                    ORDER BY OrderInContainer,
                             TicketKey COLLATE NOCASE, TicketKey
                    """;
                command.Parameters.AddWithValue(
                    "@topicRowId",
                    topicRow.RowId);
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(ct);
                int expectedOrder = 0;
                while (await reader.ReadAsync(ct))
                {
                    if (reader.GetInt64(1) != expectedOrder++)
                    {
                        throw new InvalidOperationException(
                            $"Grouping topic {topicRow.RowId} has non-canonical ungrouped member order.");
                    }
                    topic.RemainingTicketKeys.Add(reader.GetString(0));
                }
            }

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM prepared_ticket_topic_members member
                    LEFT JOIN prepared_ticket_topic_groups grouping
                        ON grouping.RowId = member.TopicGroupRowId
                       AND grouping.TopicRowId = member.TopicRowId
                    WHERE member.TopicRowId = @topicRowId
                      AND member.TopicGroupRowId IS NOT NULL
                      AND grouping.RowId IS NULL
                    """;
                command.Parameters.AddWithValue(
                    "@topicRowId",
                    topicRow.RowId);
                long orphanMembers = Convert.ToInt64(
                    await command.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture);
                if (orphanMembers != 0)
                {
                    throw new InvalidOperationException(
                        $"Grouping topic {topicRow.RowId} has orphaned group members.");
                }
            }
            payload.Topics.Add(topic);
        }
        return payload;
    }

    private static async Task<PreparedTicketGroupingReceiptCoordinate?>
        ReadLatestGroupingReceiptAsync(
            SqliteConnection connection,
            string partitionKey,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RunId, StageId, PartitionKey, InputFingerprint,
                   OutputFingerprint, PersistedAt
            FROM prepared_ticket_partition_receipts
            WHERE PartitionKey = @partitionKey
            ORDER BY julianday(PersistedAt) DESC, PersistedAt DESC,
                     RunId DESC, StageId DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@partitionKey", partitionKey);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new PreparedTicketGroupingReceiptCoordinate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadNullableString(reader, 4),
                ParseDate(reader.GetString(5)))
            : null;
    }

    private static async Task<PreparedTicketPartitionCertificationRecord?>
        ReadPartitionCertificationAsync(
            SqliteConnection connection,
            string runId,
            string partitionKey,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, RunId, StageId, PartitionKey,
                   InputFingerprint, SourceRunId, SourceStageId,
                   SourceInputFingerprint, OutputFingerprint, CertifiedAt
            FROM prepared_ticket_partition_certifications
            WHERE RunId = @runId AND PartitionKey = @partitionKey
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@partitionKey", partitionKey);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new PreparedTicketPartitionCertificationRecord
            {
                RowId = reader.GetInt32(0),
                RunId = reader.GetString(1),
                StageId = reader.GetString(2),
                PartitionKey = reader.GetString(3),
                InputFingerprint = reader.GetString(4),
                SourceRunId = reader.GetString(5),
                SourceStageId = reader.GetString(6),
                SourceInputFingerprint = reader.GetString(7),
                OutputFingerprint = reader.GetString(8),
                CertifiedAt = ParseDate(reader.GetString(9)),
            }
            : null;
    }

    private static void EnsureMatchingPartitionCertification(
        PreparedTicketPartitionCertificationRecord certification,
        string stageId,
        PreparedTicketRunPartition partition,
        PreparedTicketGroupingReceiptCoordinate sourceReceipt,
        string outputFingerprint)
    {
        if (!string.Equals(
                certification.StageId,
                stageId,
                StringComparison.Ordinal) ||
            !string.Equals(
                certification.InputFingerprint,
                partition.InputFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                certification.SourceRunId,
                sourceReceipt.RunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                certification.SourceStageId,
                sourceReceipt.StageId,
                StringComparison.Ordinal) ||
            !string.Equals(
                certification.SourceInputFingerprint,
                sourceReceipt.InputFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                certification.OutputFingerprint,
                outputFingerprint,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Grouping certification for partition '{partition.PartitionKey}' does not match the current output and source receipt.");
        }
    }

    private static async Task SavePartitionReceiptAsync(
        SqliteConnection connection,
        string runId,
        string stageId,
        string partitionKey,
        string inputFingerprint,
        string outputFingerprint,
        PreparedTicketGroupingSaveResult result,
        DateTimeOffset persistedAt,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_partition_receipts(
                RunId, StageId, PartitionKey, InputFingerprint,
                OutputFingerprint,
                TopicRows, TopicGroupRows, MemberRows, PersistedAt)
            VALUES(
                @runId, @stageId, @partitionKey, @inputFingerprint,
                @outputFingerprint,
                @topicRows, @topicGroupRows, @memberRows, @persistedAt)
            ON CONFLICT(RunId, PartitionKey) DO UPDATE SET
                StageId = excluded.StageId,
                InputFingerprint = excluded.InputFingerprint,
                OutputFingerprint = excluded.OutputFingerprint,
                TopicRows = excluded.TopicRows,
                TopicGroupRows = excluded.TopicGroupRows,
                MemberRows = excluded.MemberRows,
                PersistedAt = excluded.PersistedAt
            """,
            ct,
            ("@runId", runId),
            ("@stageId", stageId),
            ("@partitionKey", partitionKey),
            ("@inputFingerprint", inputFingerprint),
            ("@outputFingerprint", outputFingerprint),
            ("@topicRows", result.TopicRows),
            ("@topicGroupRows", result.TopicGroupRows),
            ("@memberRows", result.MemberRows),
            ("@persistedAt", Format(persistedAt)));
    }

    private sealed record GroupingFingerprintTopicRow(
        long RowId,
        string WorkGroupDisplay,
        string ShortDescription,
        string LongerDescription,
        int? RenderOrderHint);

    private sealed record GroupingFingerprintGroupRow(
        long RowId,
        string FirstTicketKey,
        string Rationale,
        int OrderInTopic);

    private static async Task<PreparedTicketGroupingSaveResult> ReadGroupingCountsAsync(
        SqliteConnection connection,
        PreparedTicketRunPartition partition,
        CancellationToken ct)
    {
        int topicRows = await CountPartitionRowsAsync(
            connection,
            "prepared_ticket_topics",
            "WorkGroupClean = @wg AND Specification = @spec AND Type = @type",
            partition,
            ct);
        int groupRows = await CountPartitionRowsAsync(
            connection,
            "prepared_ticket_topic_groups",
            """
            TopicRowId IN (
                SELECT RowId FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
            )
            """,
            partition,
            ct);
        int memberRows = await CountPartitionRowsAsync(
            connection,
            "prepared_ticket_topic_members",
            """
            TopicRowId IN (
                SELECT RowId FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
            )
            """,
            partition,
            ct);
        return new PreparedTicketGroupingSaveResult(
            partition.WorkGroupClean,
            partition.Specification,
            partition.Type,
            topicRows,
            groupRows,
            memberRows);
    }

    private static async Task<int> CountPartitionRowsAsync(
        SqliteConnection connection,
        string table,
        string where,
        PreparedTicketRunPartition partition,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        command.Parameters.AddWithValue("@wg", partition.WorkGroupClean);
        command.Parameters.AddWithValue("@spec", partition.Specification);
        command.Parameters.AddWithValue("@type", partition.Type);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public static string GetPartitionKey(
        string workGroupClean,
        string specification,
        string type)
        => string.Join(
            "\u001f",
            workGroupClean.Trim(),
            specification.Trim(),
            type.Trim());

    private static string NormalizeWorkGroupClean(
        string? workGroupClean,
        string? workGroupDisplay)
    {
        string cleaned = Hl7WorkGroupNameCleaner.Clean(workGroupDisplay);
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            return cleaned;
        }
        return NormalizePartitionValue(workGroupClean, "unattributed");
    }

    private static string NormalizePartitionValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ExecuteInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertIdempotentStageRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string ticketKey,
        string runId,
        string columns,
        string values,
        string equalityPredicate,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             INSERT INTO {table}({columns})
             VALUES({values})
             ON CONFLICT(RunId, TicketKey) DO NOTHING
             """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(ct);

        command.Parameters.Clear();
        command.CommandText =
            $"""
             SELECT 1 FROM {table}
             WHERE RunId = @runId AND TicketKey = @ticketKey COLLATE NOCASE
               AND {equalityPredicate}
             """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            throw new InvalidOperationException(
                $"Ticket '{ticketKey}' was already staged with different content.");
        }
    }

    private static async Task<PreparedTicketPublicationCorpusTicket>
        ReadCarriedCorpusTicketAsync(
            SqliteConnection connection,
            PreparedTicketPublicationReconciliationItemDecision decision,
            CancellationToken ct)
    {
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT state.GraphHash, state.RunId, state.RunItemId,
                       item.AcceptedReceiptId, item.ItemKind,
                       item.ExpectedSourceRevision
                FROM prepared_ticket_authoring_state state
                INNER JOIN authoring_run_items item
                    ON item.Id = state.RunItemId
                   AND item.RunId = state.RunId
                WHERE state.TicketKey = @ticketKey COLLATE NOCASE
                  AND state.Classification = 'receipt-backed'
                """;
            command.Parameters.AddWithValue("@ticketKey", decision.TicketKey);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) ||
                !string.Equals(
                    reader.GetString(0),
                    decision.BaselineAuthoredFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(1),
                    decision.BaselineContributingRunId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(2),
                    decision.BaselineRunItemId,
                    StringComparison.Ordinal) ||
                reader.IsDBNull(3) ||
                !string.Equals(
                    reader.GetString(3),
                    decision.BaselineReceiptId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(4),
                    decision.ItemKind,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(5),
                    decision.ExpectedSourceRevision,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Carried ticket '{decision.TicketKey}' diverged from its frozen accepted coordinate.");
            }
        }
        PreparedTicketPayload payload =
            await ReadPreparedTicketPayloadAsync(
                connection,
                decision.TicketKey,
                ct);
        string graphHash = await ComputePreparedGraphHashAsync(
            connection,
            decision.TicketKey,
            ct);
        if (!string.Equals(
                graphHash,
                decision.BaselineAuthoredFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Carried ticket '{decision.TicketKey}' graph fingerprint diverged.");
        }
        PreparedTicketHydrationBatch hydration =
            await ReadHydrationBatchAsync(
                connection,
                decision.TicketKey,
                ct);
        return new PreparedTicketPublicationCorpusTicket(
            decision.TicketKey,
            decision.Disposition,
            decision.BaselineReceiptId,
            decision.BaselineRunItemId,
            decision.BaselineContributingRunId,
            decision.ItemKind!,
            decision.ExpectedSourceRevision!,
            decision.BaselineAuthoredFingerprint,
            payload,
            hydration);
    }

    private static async Task<PreparedTicketPublicationCorpusTicket>
        ReadStagedCorpusTicketAsync(
            SqliteConnection connection,
            string runId,
            PreparedTicketPublicationReconciliationItemDecision decision,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT graph.RunItemId, graph.OperationId, graph.SourceRevision,
                   graph.AuthoredFingerprint, graph.PayloadJson,
                   hydration.HydrationFingerprint, hydration.HydrationJson,
                   receipt.ReceiptId, item.ItemKind,
                   item.ExpectedSourceRevision
            FROM prepared_ticket_publication_staged_graphs graph
            INNER JOIN prepared_ticket_publication_staged_hydration hydration
                ON hydration.RunId = graph.RunId
               AND hydration.TicketKey = graph.TicketKey COLLATE NOCASE
            INNER JOIN prepared_ticket_publication_staged_receipts receipt
                ON receipt.RunId = graph.RunId
               AND receipt.TicketKey = graph.TicketKey COLLATE NOCASE
               AND receipt.RunItemId = graph.RunItemId
               AND receipt.OperationId = graph.OperationId
               AND receipt.AuthoredFingerprint = graph.AuthoredFingerprint
            INNER JOIN authoring_run_items item
                ON item.RunId = graph.RunId
               AND item.Id = graph.RunItemId
               AND item.BusinessKey = graph.TicketKey COLLATE NOCASE
            WHERE graph.RunId = @runId
              AND graph.TicketKey = @ticketKey COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@ticketKey", decision.TicketKey);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"Revised ticket '{decision.TicketKey}' does not have a complete staged graph.");
        }
        string runItemId = reader.GetString(0);
        string sourceRevision = reader.GetString(2);
        string authoredFingerprint = reader.GetString(3);
        string payloadJson = reader.GetString(4);
        string hydrationFingerprint = reader.GetString(5);
        string hydrationJson = reader.GetString(6);
        string receiptId = reader.GetString(7);
        string itemKind = reader.GetString(8);
        string expectedSourceRevision = reader.GetString(9);
        if (!string.Equals(
                AuthoringSourceRevision.CanonicalizeTimestamp(sourceRevision),
                AuthoringSourceRevision.CanonicalizeTimestamp(
                    decision.CurrentSourceRevision),
                StringComparison.Ordinal) ||
            !string.Equals(
                itemKind,
                decision.ItemKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                expectedSourceRevision,
                decision.ExpectedSourceRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                hydrationFingerprint,
                AuthoringResultHasher.HashNormalizedUtf8(hydrationJson),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Revised ticket '{decision.TicketKey}' staged fingerprints diverged.");
        }
        PreparedTicketPayload payload =
            JsonSerializer.Deserialize<PreparedTicketPayload>(payloadJson)
            ?? throw new InvalidOperationException(
                $"Revised ticket '{decision.TicketKey}' has an invalid staged graph.");
        PreparedTicketHydrationBatch hydration =
            JsonSerializer.Deserialize<PreparedTicketHydrationBatch>(
                hydrationJson)
            ?? throw new InvalidOperationException(
                $"Revised ticket '{decision.TicketKey}' has invalid staged hydration.");
        PreparedTicketPayloadValidator.ThrowIfInvalid(payload);
        ValidateStagedHydration(decision.TicketKey, hydration);
        if (!string.Equals(
                payload.Key,
                decision.TicketKey,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Revised ticket '{decision.TicketKey}' staged graph belongs to another ticket.");
        }
        return new PreparedTicketPublicationCorpusTicket(
            decision.TicketKey,
            decision.Disposition,
            receiptId,
            runItemId,
            runId,
            itemKind,
            expectedSourceRevision,
            authoredFingerprint,
            payload,
            hydration);
    }

    private static async Task<PreparedTicketPayload>
        ReadPreparedTicketPayloadAsync(
            SqliteConnection connection,
            string key,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                   RelatedTicketSummary, RelatedZulipSummary,
                   RelatedGitHubSummary, ExistingProposed, ProposalA,
                   ProposalAJustification, ProposalAImpact, ProposalB,
                   ProposalBJustification, ProposalBImpact, ProposalC,
                   ProposalCJustification, Recommendation,
                   RecommendationJustification, SavedAt
            FROM prepared_tickets
            WHERE Key = @key COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"Carried ticket '{key}' is missing its canonical graph.");
        }
        PreparedTicketPayload payload = new()
        {
            Key = reader.GetString(0),
            RequestSummary = reader.GetString(1),
            CommentSummary = reader.GetString(2),
            LinkedTicketSummary = reader.GetString(3),
            RelatedTicketSummary = reader.GetString(4),
            RelatedZulipSummary = reader.GetString(5),
            RelatedGitHubSummary = reader.GetString(6),
            ExistingProposed = reader.GetString(7),
            ProposalA = reader.GetString(8),
            ProposalAJustification = reader.GetString(9),
            ProposalAImpact = reader.GetString(10),
            ProposalB = reader.GetString(11),
            ProposalBJustification = reader.GetString(12),
            ProposalBImpact = reader.GetString(13),
            ProposalC = reader.GetString(14),
            ProposalCJustification = reader.GetString(15),
            Recommendation = reader.GetString(16),
            RecommendationJustification = reader.GetString(17),
            SavedAt = reader.GetDateTimeOffset(18),
        };
        await reader.DisposeAsync();
        payload.Repos = (await GetRelatedItemsAsync(connection, key, ct)).Repos
            .Select(row => new PreparedTicketRepoPayload
            {
                Repo = row.Repo,
                RepoCategory = row.RepoCategory,
                Justification = row.Justification,
            }).ToList();
        PreparedTicketRelatedItems related =
            await GetRelatedItemsAsync(connection, key, ct);
        payload.RelatedJiraTickets = related.JiraTickets.Select(row =>
            new PreparedTicketRelatedJiraPayload
            {
                AssociatedTicketKey = row.AssociatedTicketKey,
                LinkType = row.LinkType,
                Justification = row.Justification,
            }).ToList();
        payload.RelatedZulipThreads = related.ZulipThreads.Select(row =>
            new PreparedTicketRelatedZulipPayload
            {
                ZulipThreadId = row.ZulipThreadId,
                Justification = row.Justification,
            }).ToList();
        payload.RelatedGitHubItems = related.GitHubItems.Select(row =>
            new PreparedTicketRelatedGitHubPayload
            {
                GitHubItemId = row.GitHubItemId,
                Justification = row.Justification,
            }).ToList();
        return payload;
    }

    private static async Task<PreparedTicketHydrationBatch>
        ReadHydrationBatchAsync(
            SqliteConnection connection,
            string key,
            CancellationToken ct)
    {
        PreparedTicketHydrationRow parent =
            await ReadHydrationParentAsync(connection, key, ct)
            ?? throw new InvalidOperationException(
                $"Carried ticket '{key}' is missing canonical hydration.");
        return new PreparedTicketHydrationBatch(
            key,
            parent,
            await ReadJiraHydrationAsync(connection, key, ct),
            await ReadZulipHydrationAsync(connection, key, ct),
            await ReadGitHubHydrationAsync(connection, key, ct),
            await ReadRepoHydrationAsync(connection, key, ct),
            await ReadJiraXrefAsync(connection, key, ct),
            await ReadInPersonRequestersAsync(connection, key, ct));
    }

    private static async Task<int> ExecuteAsyncWithCount(
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
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteRawAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}

public sealed record PreparedTicketRunPartition(
    string WorkGroupClean,
    string WorkGroupDisplay,
    string Specification,
    string Type,
    string PartitionKey,
    string InputFingerprint,
    IReadOnlyList<string> TicketKeys);

public sealed record PreparerMaintenanceLease(
    string RunId,
    string LeaseId);
