using System.Globalization;
using System.Text;
using FhirAugury.Common.Database;
using FhirAugury.Common.WorkGroups;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;

public sealed class PlannerDatabase(string dbPath, ILogger<PlannerDatabase> logger, bool readOnly = false)
    : FhirAugury.Processing.Common.Database.ProcessingDatabase(dbPath, logger, readOnly),
      IHydrationTargetDatabase,
      IAuthoringCutoverParticipant
{
    private const string AuthoringProcessorKind = "jira-fhir";
    public const string CurrentSnapshotReceiptBackedTicketsTable =
        "current_planned_receipt_backed_tickets";
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
                $"Another Planner service generation owns database '{DatabasePath}'.",
                ex);
        }
    }

    /// <summary>
    /// Idempotent. Creates every planner table via <c>CREATE TABLE IF NOT EXISTS</c>
    /// and follows up with the composite-unique-index passes required by
    /// CsLightDbGen's lack of composite-unique support. Safe to call against
    /// a connection the planner does not own (e.g., <c>ticket-site</c>'s trim
    /// step temp copy).
    /// </summary>
    public static void EnsureSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        JiraProcessingSourceTicketStore.EnsureSchema(connection);
        AuthoringRunStore.EnsureSchema(connection);

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
            SqliteSchemaHelpers.AddColumnIfMissing(connection, "planned_ticket_hydration", column, "TEXT NULL");
            SqliteSchemaHelpers.AddColumnIfMissing(connection, "planned_jira_hydration", column, "TEXT NULL");
        }

        // Agent-authored rows
        PlannedTicketRecord.CreateTable(connection);
        PlannedTicketJiraContentRecord.CreateTable(connection);
        PlannedTicketRepoRecord.CreateTable(connection);
        PlannedTicketRepoChangeRecord.CreateTable(connection);
        PlannedTicketRepoImpactRecord.CreateTable(connection);
        PlannedTicketChangeValidationRecord.CreateTable(connection);
        PlannedTicketTestingConsiderationRecord.CreateTable(connection);
        PlannedTicketOpenQuestionRecord.CreateTable(connection);

        // Hydration tables (Phase 2)
        PlannedTicketRelatedJiraRecord.CreateTable(connection);
        PlannedTicketRelatedZulipRecord.CreateTable(connection);
        PlannedTicketRelatedGitHubRecord.CreateTable(connection);
        PlannedTicketJiraXrefRecord.CreateTable(connection);
        PlannedTicketHydrationRecord.CreateTable(connection);
        PlannedJiraHydrationRecord.CreateTable(connection);
        PlannedZulipHydrationRecord.CreateTable(connection);
        PlannedGitHubHydrationRecord.CreateTable(connection);
        PlannedRepoHydrationRecord.CreateTable(connection);

        // Topic tables (Phase 2)
        PlannedTicketTopicRecord.CreateTable(connection);
        PlannedTicketTopicGroupRecord.CreateTable(connection);
        PlannedTicketTopicMemberRecord.CreateTable(connection);
        PlannedTicketTopicRepoRecord.CreateTable(connection);
        JiraReviewWorkGroupRecord.CreateTable(connection);

        // Follow-on composite-unique indexes (CsLightDbGen has no Unique
        // property on LdgSQLiteIndex; see memory: "CsLightDbGen [LdgSQLiteIndex]
        // attribute supports only `params string[] columns`").
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_ticket_topic_repos_topic_repo " +
            "ON planned_ticket_topic_repos(TopicRowId, RepoKey);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_jira_hydration_issue_jira " +
            "ON planned_jira_hydration(IssueKey, JiraKey);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_zulip_hydration_issue_thread " +
            "ON planned_zulip_hydration(IssueKey, ZulipThreadId);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_github_hydration_issue_item " +
            "ON planned_github_hydration(IssueKey, GitHubItemId);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_repo_hydration_issue_repo " +
            "ON planned_repo_hydration(IssueKey, RepoKey);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_ticket_related_jira_issue_jira " +
            "ON planned_ticket_related_jira(IssueKey, JiraKey);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_ticket_related_zulip_issue_thread " +
            "ON planned_ticket_related_zulip(IssueKey, ZulipThreadId);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_ticket_related_github_issue_item " +
            "ON planned_ticket_related_github(IssueKey, GitHubItemId);");
        ExecuteRaw(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_ticket_jira_xref_issue_jira " +
            "ON planned_ticket_jira_xref(IssueKey, JiraKey);");

        // Idempotent migrations marker (matches preparer pattern).
        ExecuteRaw(connection, """
            CREATE TABLE IF NOT EXISTS planner_schema_migrations (
                Id TEXT PRIMARY KEY,
                AppliedAt TEXT NOT NULL
            );
            """);
        EnsureAuthoringStateTables(connection);
        EnsureApplierProjectionTrigger(connection);
    }

    private static void EnsureAuthoringStateTables(SqliteConnection connection)
    {
        ExecuteRaw(connection, """
            CREATE TABLE IF NOT EXISTS planned_ticket_authoring_state(
                TicketKey TEXT PRIMARY KEY,
                Classification TEXT NOT NULL,
                GraphHash TEXT NOT NULL,
                ReceiptContentHash TEXT NULL,
                LegacyCompletionId TEXT NULL,
                LegacyCompletedProcessingAt TEXT NULL,
                RunId TEXT NULL,
                RunItemId TEXT NULL,
                OperationId TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_planned_ticket_authoring_state_classification
            ON planned_ticket_authoring_state(Classification);

            CREATE TABLE IF NOT EXISTS planned_ticket_partition_receipts(
                RunId TEXT NOT NULL,
                StageId TEXT NOT NULL,
                PartitionKey TEXT NOT NULL,
                InputFingerprint TEXT NOT NULL,
                TopicRows INTEGER NOT NULL,
                TopicGroupRows INTEGER NOT NULL,
                MemberRows INTEGER NOT NULL,
                PersistedAt TEXT NOT NULL,
                PRIMARY KEY(RunId, PartitionKey)
            );

            CREATE TABLE IF NOT EXISTS planned_ticket_run_item_partitions(
                RunItemId TEXT PRIMARY KEY,
                TicketKey TEXT NOT NULL,
                WorkGroupClean TEXT NULL,
                WorkGroupDisplay TEXT NULL,
                Specification TEXT NULL,
                Type TEXT NULL,
                CapturedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS planned_ticket_applier_projection_pending(
                OperationId TEXT PRIMARY KEY,
                TicketKey TEXT NOT NULL,
                PreserveLegacy INTEGER NOT NULL
            );
            """);
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "planned_ticket_authoring_state",
            "ReceiptContentHash",
            "TEXT NULL");
    }

    private static void EnsureApplierProjectionTrigger(SqliteConnection connection)
    {
        ExecuteRaw(connection, """
            CREATE TRIGGER IF NOT EXISTS trg_planned_ticket_receipt_applier_projection
            AFTER INSERT ON authoring_result_receipts
            WHEN EXISTS(
                SELECT 1 FROM planned_ticket_applier_projection_pending
                WHERE OperationId = NEW.OperationId)
            BEGIN
                UPDATE jira_processing_source_tickets
                SET ProcessingStatus = 'complete',
                    ProcessingError = NULL,
                    ErrorMessage = NULL,
                    AgentExitCode = NULL,
                    ErrorOccurredAt = NULL,
                    CompletionId = CASE
                        WHEN (SELECT PreserveLegacy FROM planned_ticket_applier_projection_pending
                              WHERE OperationId = NEW.OperationId) = 1
                        THEN CompletionId
                        ELSE NEW.Id
                    END,
                    CompletedProcessingAt = CASE
                        WHEN (SELECT PreserveLegacy FROM planned_ticket_applier_projection_pending
                              WHERE OperationId = NEW.OperationId) = 1
                        THEN CompletedProcessingAt
                        ELSE NEW.PersistedAt
                    END
                WHERE Key = (
                    SELECT TicketKey FROM planned_ticket_applier_projection_pending
                    WHERE OperationId = NEW.OperationId);

                DELETE FROM planned_ticket_applier_projection_pending
                WHERE OperationId = NEW.OperationId;
            END;
            """);
    }

    protected override void InitializeSchema(SqliteConnection connection) => EnsureSchema(connection);

    // ---------------------------------------------------------------------
    // Agent-payload persistence

    public async Task SavePlannedTicketAsync(PlannedTicketPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        PlannedTicketPayloadValidator.ThrowIfInvalid(payload);
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureLegacyAuthoringModeAsync(connection, ct);
            await EnsureGeneralMutationAllowedAsync(connection, ct);
            DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
            await SavePlannedTicketCoreAsync(connection, payload, savedAt, ct);
            string graphHash = await ComputePlannedGraphHashAsync(connection, payload.Key, ct);
            await UpsertAuthoringStateAsync(
                connection,
                payload.Key,
                "legacy-unverified",
                graphHash,
                receiptContentHash: null,
                runId: null,
                runItemId: null,
                operationId: null,
                savedAt,
                ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task SavePlannedTicketForAuthoringAsync(
        SqliteConnection connection,
        PlannedTicketPayload payload,
        string receiptContentHash,
        string runId,
        string runItemId,
        string operationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        PlannedTicketPayloadValidator.ThrowIfInvalid(payload);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
        await CapturePreChangePartitionAsync(connection, payload.Key, runItemId, savedAt, ct);

        string? priorClassification = null;
        string? priorGraphHash = null;
        await using (SqliteCommand prior = connection.CreateCommand())
        {
            prior.CommandText =
                "SELECT Classification, GraphHash FROM planned_ticket_authoring_state WHERE TicketKey = @key";
            prior.Parameters.AddWithValue("@key", payload.Key);
            await using SqliteDataReader reader = await prior.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                priorClassification = reader.GetString(0);
                priorGraphHash = reader.GetString(1);
            }
        }

        await SavePlannedTicketCoreAsync(connection, payload, savedAt, ct);
        string graphHash = await ComputePlannedGraphHashAsync(connection, payload.Key, ct);
        bool preserveLegacy =
            priorClassification is not null &&
            string.Equals(priorGraphHash, graphHash, StringComparison.Ordinal);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO planned_ticket_applier_projection_pending(OperationId, TicketKey, PreserveLegacy)
            VALUES(@operationId, @ticketKey, @preserveLegacy)
            """,
            ct,
            ("@operationId", operationId),
            ("@ticketKey", payload.Key),
            ("@preserveLegacy", preserveLegacy ? 1 : 0));
        await UpsertAuthoringStateAsync(
            connection,
            payload.Key,
            "receipt-backed",
            graphHash,
            receiptContentHash,
            runId,
            runItemId,
            operationId,
            savedAt,
            ct);
    }

    private static async Task SavePlannedTicketCoreAsync(
        SqliteConnection connection,
        PlannedTicketPayload payload,
        DateTimeOffset savedAt,
        CancellationToken ct)
    {
        await DeletePlanForTicketAsync(connection, payload.Key, ct);
        await ExecuteAsync(connection,
            "INSERT INTO planned_tickets (Id, Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt) " +
            "VALUES (@Id, @Key, @Resolution, @ResolutionSummary, @FeatureProposal, @DesignRationale, @SavedAt)",
            ct,
            ("@Id", Guid.NewGuid().ToString("N")),
            ("@Key", payload.Key),
            ("@Resolution", payload.Resolution),
            ("@ResolutionSummary", payload.ResolutionSummary),
            ("@FeatureProposal", payload.FeatureProposal),
            ("@DesignRationale", payload.DesignRationale),
            ("@SavedAt", Format(savedAt)));

        foreach (PlannedTicketRepoPayload repo in payload.Repos)
            await ExecuteAsync(connection, "INSERT INTO planned_ticket_repos (Id, IssueKey, RepoKey, RepoRevision, Justification) VALUES (@Id, @IssueKey, @RepoKey, @RepoRevision, @Justification)", ct, ("@Id", Guid.NewGuid().ToString("N")), ("@IssueKey", payload.Key), ("@RepoKey", repo.RepoKey), ("@RepoRevision", (object?)repo.RepoRevision ?? DBNull.Value), ("@Justification", repo.Justification));
        foreach (PlannedTicketRepoChangePayload row in payload.RepoChanges)
            await ExecuteAsync(connection, "INSERT INTO planned_ticket_repo_changes (Id, IssueKey, TicketRepoId, RepoKey, ChangeSequence, FilePath, ChangeTitle, ChangeDescription, SourceLineStart, SourceLineEnd, ReplacementLines, Reason) VALUES (@Id, @IssueKey, @TicketRepoId, @RepoKey, @ChangeSequence, @FilePath, @ChangeTitle, @ChangeDescription, @SourceLineStart, @SourceLineEnd, @ReplacementLines, @Reason)", ct, ("@Id", Guid.NewGuid().ToString("N")), ("@IssueKey", payload.Key), ("@TicketRepoId", row.TicketRepoId), ("@RepoKey", row.RepoKey), ("@ChangeSequence", row.ChangeSequence), ("@FilePath", row.FilePath), ("@ChangeTitle", row.ChangeTitle), ("@ChangeDescription", row.ChangeDescription), ("@SourceLineStart", (object?)row.SourceLineStart ?? DBNull.Value), ("@SourceLineEnd", (object?)row.SourceLineEnd ?? DBNull.Value), ("@ReplacementLines", ReplacementLineJson.Serialize(row.ReplacementLines)), ("@Reason", row.Reason));
        foreach (PlannedTicketRepoImpactPayload row in payload.RepoImpacts)
            await ExecuteAsync(connection, "INSERT INTO planned_ticket_repo_impacts (Id, IssueKey, TicketRepoId, RepoKey, TicketRepoChangeId, AffectedFilePath, HowAffected) VALUES (@Id, @IssueKey, @TicketRepoId, @RepoKey, @TicketRepoChangeId, @AffectedFilePath, @HowAffected)", ct, ("@Id", Guid.NewGuid().ToString("N")), ("@IssueKey", payload.Key), ("@TicketRepoId", row.TicketRepoId), ("@RepoKey", row.RepoKey), ("@TicketRepoChangeId", (object?)row.TicketRepoChangeId ?? DBNull.Value), ("@AffectedFilePath", row.AffectedFilePath), ("@HowAffected", row.HowAffected));
        foreach (PlannedTicketChangeValidationPayload row in payload.ChangeValidations)
            await ExecuteAsync(connection, "INSERT INTO planned_ticket_change_validations (Id, IssueKey, TicketRepoId, RepoKey, ValidationSequence, Action) VALUES (@Id, @IssueKey, @TicketRepoId, @RepoKey, @ValidationSequence, @Action)", ct, ("@Id", Guid.NewGuid().ToString("N")), ("@IssueKey", payload.Key), ("@TicketRepoId", row.TicketRepoId), ("@RepoKey", row.RepoKey), ("@ValidationSequence", row.ValidationSequence), ("@Action", row.Action));
        foreach (PlannedTicketTestingConsiderationPayload row in payload.TestingConsiderations)
            await ExecuteAsync(connection, "INSERT INTO planned_ticket_testing_considerations (Id, IssueKey, TicketRepoId, RepoKey, ConsiderationSequence, Consideration) VALUES (@Id, @IssueKey, @TicketRepoId, @RepoKey, @ConsiderationSequence, @Consideration)", ct, ("@Id", Guid.NewGuid().ToString("N")), ("@IssueKey", payload.Key), ("@TicketRepoId", row.TicketRepoId), ("@RepoKey", row.RepoKey), ("@ConsiderationSequence", row.ConsiderationSequence), ("@Consideration", row.Consideration));
        foreach (PlannedTicketOpenQuestionPayload row in payload.OpenQuestions)
            await ExecuteAsync(connection, "INSERT INTO planned_ticket_open_questions (Id, IssueKey, TicketRepoId, RepoKey, QuestionSequence, Question) VALUES (@Id, @IssueKey, @TicketRepoId, @RepoKey, @QuestionSequence, @Question)", ct, ("@Id", Guid.NewGuid().ToString("N")), ("@IssueKey", payload.Key), ("@TicketRepoId", row.TicketRepoId), ("@RepoKey", row.RepoKey), ("@QuestionSequence", row.QuestionSequence), ("@Question", row.Question));
    }

    public async Task DeletePlanForTicketAsync(string issueKey, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await DeletePlanForTicketAsync(connection, issueKey, ct);
    }

    public static async Task DeletePlanForTicketAsync(SqliteConnection connection, string issueKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueKey);
        await ExecuteAsync(
            connection,
            "DELETE FROM planned_ticket_topic_members WHERE TicketKey = @issueKey",
            ct,
            ("@issueKey", issueKey));
        foreach (string table in DeleteOrder)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = table == "planned_tickets"
                ? $"DELETE FROM {table} WHERE Key = @issueKey"
                : $"DELETE FROM {table} WHERE IssueKey = @issueKey";
            command.Parameters.Add(new SqliteParameter("@issueKey", issueKey));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> PlanExistsAsync(string issueKey, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM planned_tickets WHERE Key = @issueKey LIMIT 1";
        command.Parameters.Add(new SqliteParameter("@issueKey", issueKey));
        object? value = await command.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task<int> ClassifyLegacyPlannedTicketsAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            int count = await ClassifyLegacyPlannedTicketsCoreAsync(
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
        await ClassifyLegacyPlannedTicketsCoreAsync(connection, ct);
        List<AuthoringRunItemDefinition> items = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.Key, s.SourceTicketShape, s.LastUpdated, s.Title,
                   s.Status, s.WorkGroup, s.Type, s.Specification
            FROM planned_tickets p
            INNER JOIN planned_ticket_authoring_state a
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
                    $"Planned ticket '{reader.GetString(0)}' has no current Jira source row for revalidation.");
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
        }
        return new AuthoringCutoverPreparation(items);
    }

    private async Task<int> ClassifyLegacyPlannedTicketsCoreAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<string> keys = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT p.Key
                FROM planned_tickets p
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
            string graphHash = await ComputePlannedGraphHashAsync(
                connection,
                key,
                ct);
            if (await IsCurrentPlannedReceiptBackedAsync(
                    connection,
                    key,
                    graphHash,
                    ct))
            {
                continue;
            }
            await UpsertAuthoringStateAsync(
                connection,
                key,
                "legacy-unverified",
                graphHash,
                receiptContentHash: null,
                runId: null,
                runItemId: null,
                operationId: null,
                DateTimeOffset.UtcNow,
                ct);
            classified++;
        }
        return classified;
    }

    private static async Task<bool> IsCurrentPlannedReceiptBackedAsync(
        SqliteConnection connection,
        string ticketKey,
        string graphHash,
        CancellationToken ct)
        => await ReadCurrentPlannedReceiptCoordinatesAsync(
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
            command.CommandText = "SELECT Key FROM planned_tickets ORDER BY Key";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                keys.Add(reader.GetString(0));
            }
        }

        foreach (string key in keys)
        {
            string graphHash = await ComputePlannedGraphHashAsync(
                connection,
                key,
                ct);
            (string ReceiptId, string RunItemId, string RunId)? coordinates =
                await ReadCurrentPlannedReceiptCoordinatesAsync(
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

    private static async Task<(string ReceiptId, string RunItemId, string RunId)?>
        ReadCurrentPlannedReceiptCoordinatesAsync(
            SqliteConnection connection,
            string ticketKey,
            string graphHash,
            CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.Id, i.Id, i.RunId
            FROM planned_ticket_authoring_state s
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
            "SELECT COUNT(*) FROM planned_ticket_authoring_state WHERE Classification = 'legacy-unverified'";
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
            SELECT COUNT(*)
            FROM planned_ticket_authoring_state s
            WHERE s.Classification = 'legacy-unverified'
              AND NOT EXISTS (
                  SELECT 1
                  FROM authoring_run_items i
                  WHERE i.RunId = @runId
                    AND i.BusinessKey = s.TicketKey COLLATE NOCASE
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
                UPDATE planned_ticket_authoring_state
                SET Classification = 'superseded',
                    ReceiptContentHash = NULL,
                    RunId = @runId,
                    RunItemId = (
                        SELECT i.Id
                        FROM authoring_run_items i
                        WHERE i.RunId = @runId
                          AND i.BusinessKey =
                              planned_ticket_authoring_state.TicketKey COLLATE NOCASE
                          AND i.Status = @superseded
                          AND i.AcceptedReceiptId IS NULL
                        LIMIT 1
                    ),
                    OperationId = NULL,
                    UpdatedAt = @updatedAt
                WHERE Classification = 'legacy-unverified'
                  AND EXISTS (
                      SELECT 1
                      FROM authoring_run_items i
                      WHERE i.RunId = @runId
                        AND i.BusinessKey =
                            planned_ticket_authoring_state.TicketKey COLLATE NOCASE
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
            FROM planned_ticket_authoring_state s
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

    public async Task<PlannerMaintenanceLease?> TryAcquireMaintenanceLeaseAsync(
        string operation,
        CancellationToken ct = default)
    {
        string runId = $"maintenance:{MaintenanceOwnerGeneration}:{operation}:{Guid.NewGuid():N}";
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
            return inserted == 1 ? new PlannerMaintenanceLease(runId, leaseId) : null;
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<int> RecoverInterruptedMaintenanceLeasesAsync(CancellationToken ct = default)
    {
        if (_startupOwnerLock is null)
        {
            throw new InvalidOperationException(
                "Planner startup ownership must be acquired before recovering maintenance leases.");
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
        PlannerMaintenanceLease lease,
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

    public async Task<IReadOnlyList<PlannedTicketRunPartition>> GetRunPartitionsAsync(
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
                  AND i.AcceptedReceiptId IS NOT NULL)
            SELECT j.WorkGroupClean, j.WorkGroup, IFNULL(j.Specification, 'Unspecified'), IFNULL(j.Type, '')
            FROM completed i
            INNER JOIN planned_jira_hydration j
                ON j.IssueKey = i.BusinessKey AND j.JiraKey = i.BusinessKey
            WHERE j.WorkGroupClean IS NOT NULL AND j.WorkGroupClean <> '' AND j.Type IS NOT NULL AND j.Type <> ''
            UNION ALL
            SELECT p.WorkGroupClean, p.WorkGroupDisplay, p.Specification, p.Type
            FROM completed i
            INNER JOIN planned_ticket_run_item_partitions p ON p.RunItemId = i.Id
            WHERE p.WorkGroupClean IS NOT NULL AND p.WorkGroupClean <> '' AND p.Type IS NOT NULL AND p.Type <> ''
            UNION ALL
            SELECT t.WorkGroupClean, t.WorkGroupDisplay, t.Specification, t.Type
            FROM planned_ticket_topics t
            WHERE EXISTS(
                SELECT 1
                FROM authoring_run_items i
                WHERE i.RunId = @runId
                  AND i.ItemKind LIKE 'maintenance:%')
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
        List<(string? Clean, string? Display, string? Specification, string? Type)> rows = [];
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
            .Select(row => (
                WorkGroupClean: NormalizePartitionValue(row.Clean, "unattributed"),
                WorkGroupDisplay: string.IsNullOrWhiteSpace(row.Display)
                    ? NormalizePartitionValue(row.Clean, "unattributed")
                    : row.Display.Trim(),
                Specification: NormalizePartitionValue(row.Specification, "Unspecified"),
                Type: NormalizePartitionValue(row.Type, string.Empty)))
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

        List<PlannedTicketRunPartition> partitions = [];
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
                string.Join("\n", members.Select(member => $"{member.Key}:{member.ReceiptId}")));
            partitions.Add(new PlannedTicketRunPartition(
                partition.WorkGroupClean,
                partition.WorkGroupDisplay,
                partition.Specification,
                partition.Type,
                GetPartitionKey(partition.WorkGroupClean, partition.Specification, partition.Type),
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
                "One or more planned tickets lack valid current receipt provenance.");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> GetSnapshotTableCountsAsync(
        CancellationToken ct = default)
    {
        string[] tables =
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
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        await using SqliteConnection connection = OpenConnection();
        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            counts[table] = Convert.ToInt64(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture);
        }
        return counts;
    }

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

    private static readonly string[] DeleteOrder =
    [
        "planned_ticket_open_questions",
        "planned_ticket_testing_considerations",
        "planned_ticket_change_validations",
        "planned_ticket_repo_impacts",
        "planned_ticket_repo_changes",
        "planned_ticket_repos",
        "planned_tickets",
    ];

    // ---------------------------------------------------------------------
    // IHydrationTargetDatabase

    public async Task<IReadOnlyList<string>> ListRelatedJiraKeysForTicketAsync(string ticketKey, CancellationToken ct)
        => await ReadStringColumnAsync(
            "SELECT JiraKey FROM planned_ticket_related_jira WHERE IssueKey = @key ORDER BY JiraKey",
            ticketKey, ct);

    public async Task<IReadOnlyList<string>> ListRelatedZulipThreadIdsForTicketAsync(string ticketKey, CancellationToken ct)
        => await ReadStringColumnAsync(
            "SELECT ZulipThreadId FROM planned_ticket_related_zulip WHERE IssueKey = @key ORDER BY ZulipThreadId",
            ticketKey, ct);

    public async Task<IReadOnlyList<string>> ListRelatedGitHubItemIdsForTicketAsync(string ticketKey, CancellationToken ct)
        => await ReadStringColumnAsync(
            "SELECT GitHubItemId FROM planned_ticket_related_github WHERE IssueKey = @key ORDER BY GitHubItemId",
            ticketKey, ct);

    public async Task<IReadOnlyList<string>> ListReposForTicketAsync(string ticketKey, CancellationToken ct)
        => await ReadStringColumnAsync(
            "SELECT DISTINCT RepoKey FROM planned_ticket_repos WHERE IssueKey = @key ORDER BY RepoKey",
            ticketKey, ct);

    public async Task<IReadOnlyList<string>> ListUnresolvedOrMissingHydrationKeysAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT pt.Key FROM planned_tickets pt
            LEFT JOIN planned_ticket_hydration h ON h.IssueKey = pt.Key
            WHERE h.IssueKey IS NULL OR h.HydrationStatus = 'unresolved'
            ORDER BY pt.Key
            """;
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    public async Task SaveHydrationAsync(HydrationBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync(ct);
        try
        {
            await EnsureTicketMutationAllowedAsync(connection, batch.TicketKey, ct);
            await DeleteHydrationRowsAsync(connection, batch.TicketKey, ct);
            await InsertParentAsync(connection, batch.Parent, ct);
            await ReplaceCanonicalJiraFieldsAsync(connection, batch.Parent, ct);
            foreach (HydrationJiraRow r in batch.JiraRows) await InsertJiraAsync(connection, r, ct);
            foreach (HydrationZulipRow r in batch.ZulipRows) await InsertZulipAsync(connection, r, ct);
            foreach (HydrationGitHubRow r in batch.GitHubRows) await InsertGitHubAsync(connection, r, ct);
            foreach (HydrationRepoRow r in batch.RepoRows) await InsertRepoAsync(connection, r, ct);
            foreach (HydrationJiraXrefRow r in batch.JiraXrefRows) await InsertJiraXrefAsync(connection, r, ct);

            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static async Task DeleteHydrationRowsAsync(SqliteConnection connection, string issueKey, CancellationToken ct)
    {
        foreach (string table in new[]
        {
            "planned_ticket_hydration",
            "planned_jira_hydration",
            "planned_zulip_hydration",
            "planned_github_hydration",
            "planned_repo_hydration",
            "planned_ticket_jira_xref",
        })
        {
            await ExecuteAsync(connection, $"DELETE FROM {table} WHERE IssueKey = @key", ct, ("@key", issueKey));
        }
    }

    private static async Task InsertParentAsync(SqliteConnection connection, HydrationTicketRow r, CancellationToken ct)
    {
        await ExecuteAsync(connection,
            "INSERT INTO planned_ticket_hydration (IssueKey, Priority, Resolution, ResolutionDescriptionPlain, Specification, RaisedInVersion, SelectedBallot, ChangeCategory, Impact, Labels, CommentCount, DescriptionPlain, DescriptionHtml, ResolutionDescriptionHtml, Reporter, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw, HydratedAt, HydrationStatus, HydrationReason) " +
            "VALUES (@IssueKey, @Priority, @Resolution, @ResolutionDescriptionPlain, @Specification, @RaisedInVersion, @SelectedBallot, @ChangeCategory, @Impact, @Labels, @CommentCount, @DescriptionPlain, @DescriptionHtml, @ResolutionDescriptionHtml, @Reporter, @CreatedAt, @RelatedArtifactsRaw, @RelatedPagesRaw, @HydratedAt, @HydrationStatus, @HydrationReason)",
            ct,
            ("@IssueKey", r.TicketKey),
            ("@Priority", Nullable(r.Priority)),
            ("@Resolution", Nullable(r.Resolution)),
            ("@ResolutionDescriptionPlain", Nullable(r.ResolutionDescriptionPlain)),
            ("@Specification", Nullable(r.Specification)),
            ("@RaisedInVersion", Nullable(r.RaisedInVersion)),
            ("@SelectedBallot", Nullable(r.SelectedBallot)),
            ("@ChangeCategory", Nullable(r.ChangeCategory)),
            ("@Impact", Nullable(r.Impact)),
            ("@Labels", Nullable(r.Labels)),
            ("@CommentCount", Nullable(r.CommentCount)),
            ("@DescriptionPlain", Nullable(r.DescriptionPlain)),
            ("@DescriptionHtml", Nullable(r.DescriptionHtml)),
            ("@ResolutionDescriptionHtml", Nullable(r.ResolutionDescriptionHtml)),
            ("@Reporter", Nullable(r.Reporter)),
            ("@CreatedAt", r.CreatedAt.HasValue ? Format(r.CreatedAt.Value) : (object)DBNull.Value),
            ("@RelatedArtifactsRaw", Nullable(r.RelatedArtifactsRaw)),
            ("@RelatedPagesRaw", Nullable(r.RelatedPagesRaw)),
            ("@HydratedAt", Format(r.HydratedAt)),
            ("@HydrationStatus", r.HydrationStatus),
            ("@HydrationReason", Nullable(r.HydrationReason)));
    }

    private static async Task InsertJiraAsync(SqliteConnection connection, HydrationJiraRow r, CancellationToken ct)
    {
        string? workGroupClean = string.IsNullOrEmpty(r.WorkGroup)
            ? null
            : (Hl7WorkGroupNameCleaner.Clean(r.WorkGroup) is string s && !string.IsNullOrEmpty(s) ? s : null);
        await ExecuteAsync(connection,
            "INSERT INTO planned_jira_hydration (IssueKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, DescriptionHtml, ResolutionDescriptionHtml, Reporter, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw, HydratedAt, HydrationStatus, HydrationReason) " +
            "VALUES (@IssueKey, @JiraKey, @Title, @Status, @Type, @Priority, @Resolution, @ResolutionDescriptionPlain, @WorkGroup, @WorkGroupClean, @Specification, @UpdatedAt, @Url, @DescriptionHtml, @ResolutionDescriptionHtml, @Reporter, @CreatedAt, @RelatedArtifactsRaw, @RelatedPagesRaw, @HydratedAt, @HydrationStatus, @HydrationReason)",
            ct,
            ("@IssueKey", r.TicketKey),
            ("@JiraKey", r.JiraKey),
            ("@Title", Nullable(r.Title)),
            ("@Status", Nullable(r.Status)),
            ("@Type", Nullable(r.Type)),
            ("@Priority", Nullable(r.Priority)),
            ("@Resolution", Nullable(r.Resolution)),
            ("@ResolutionDescriptionPlain", Nullable(r.ResolutionDescriptionPlain)),
            ("@WorkGroup", Nullable(r.WorkGroup)),
            ("@WorkGroupClean", Nullable(workGroupClean)),
            ("@Specification", Nullable(r.Specification)),
            ("@UpdatedAt", r.UpdatedAt.HasValue ? Format(r.UpdatedAt.Value) : (object)DBNull.Value),
            ("@Url", Nullable(r.Url)),
            ("@DescriptionHtml", Nullable(r.DescriptionHtml)),
            ("@ResolutionDescriptionHtml", Nullable(r.ResolutionDescriptionHtml)),
            ("@Reporter", Nullable(r.Reporter)),
            ("@CreatedAt", r.CreatedAt.HasValue ? Format(r.CreatedAt.Value) : (object)DBNull.Value),
            ("@RelatedArtifactsRaw", Nullable(r.RelatedArtifactsRaw)),
            ("@RelatedPagesRaw", Nullable(r.RelatedPagesRaw)),
            ("@HydratedAt", Format(r.HydratedAt)),
            ("@HydrationStatus", r.HydrationStatus),
            ("@HydrationReason", Nullable(r.HydrationReason)));
    }

    private static async Task ReplaceCanonicalJiraFieldsAsync(
        SqliteConnection connection,
        HydrationTicketRow row,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            "DELETE FROM planned_ticket_jira_content WHERE TicketKey = @ticketKey",
            ct,
            ("@ticketKey", row.TicketKey));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO planned_ticket_jira_content(TicketKey, DescriptionHtml, ResolutionDescriptionHtml)
            VALUES(@ticketKey, @descriptionHtml, @resolutionDescriptionHtml)
            """,
            ct,
            ("@ticketKey", row.TicketKey),
            ("@descriptionHtml", row.DescriptionHtml),
            ("@resolutionDescriptionHtml", row.ResolutionDescriptionHtml));
    }

    private static async Task InsertZulipAsync(SqliteConnection connection, HydrationZulipRow r, CancellationToken ct)
    {
        await ExecuteAsync(connection,
            "INSERT INTO planned_zulip_hydration (IssueKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount, FirstMessageAt, LastMessageAt, FirstMessageExcerpt, Url, HydratedAt, HydrationStatus, HydrationReason) " +
            "VALUES (@IssueKey, @ZulipThreadId, @StreamId, @StreamName, @Topic, @MessageCount, @FirstMessageAt, @LastMessageAt, @FirstMessageExcerpt, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)",
            ct,
            ("@IssueKey", r.TicketKey),
            ("@ZulipThreadId", r.ZulipThreadId),
            ("@StreamId", Nullable(r.StreamId)),
            ("@StreamName", Nullable(r.StreamName)),
            ("@Topic", Nullable(r.Topic)),
            ("@MessageCount", Nullable(r.MessageCount)),
            ("@FirstMessageAt", r.FirstMessageAt.HasValue ? Format(r.FirstMessageAt.Value) : (object)DBNull.Value),
            ("@LastMessageAt", r.LastMessageAt.HasValue ? Format(r.LastMessageAt.Value) : (object)DBNull.Value),
            ("@FirstMessageExcerpt", Nullable(r.FirstMessageExcerpt)),
            ("@Url", Nullable(r.Url)),
            ("@HydratedAt", Format(r.HydratedAt)),
            ("@HydrationStatus", r.HydrationStatus),
            ("@HydrationReason", Nullable(r.HydrationReason)));
    }

    private static async Task InsertGitHubAsync(SqliteConnection connection, HydrationGitHubRow r, CancellationToken ct)
    {
        await ExecuteAsync(connection,
            "INSERT INTO planned_github_hydration (IssueKey, GitHubItemId, Owner, Repo, Number, Path, Title, State, IsPullRequest, Labels, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason) " +
            "VALUES (@IssueKey, @GitHubItemId, @Owner, @Repo, @Number, @Path, @Title, @State, @IsPullRequest, @Labels, @UpdatedAt, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)",
            ct,
            ("@IssueKey", r.TicketKey),
            ("@GitHubItemId", r.GitHubItemId),
            ("@Owner", Nullable(r.Owner)),
            ("@Repo", Nullable(r.Repo)),
            ("@Number", Nullable(r.Number)),
            ("@Path", Nullable(r.Path)),
            ("@Title", Nullable(r.Title)),
            ("@State", Nullable(r.State)),
            ("@IsPullRequest", r.IsPullRequest.HasValue ? (object)(r.IsPullRequest.Value ? 1 : 0) : DBNull.Value),
            ("@Labels", Nullable(r.Labels)),
            ("@UpdatedAt", r.UpdatedAt.HasValue ? Format(r.UpdatedAt.Value) : (object)DBNull.Value),
            ("@Url", Nullable(r.Url)),
            ("@HydratedAt", Format(r.HydratedAt)),
            ("@HydrationStatus", r.HydrationStatus),
            ("@HydrationReason", Nullable(r.HydrationReason)));
    }

    private static async Task InsertRepoAsync(SqliteConnection connection, HydrationRepoRow r, CancellationToken ct)
    {
        await ExecuteAsync(connection,
            "INSERT INTO planned_repo_hydration (IssueKey, RepoKey, Description, WorkGroup, Specification, CategoryDetail, Url, HydratedAt, HydrationStatus, HydrationReason) " +
            "VALUES (@IssueKey, @RepoKey, @Description, @WorkGroup, @Specification, @CategoryDetail, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)",
            ct,
            ("@IssueKey", r.TicketKey),
            ("@RepoKey", r.Repo),
            ("@Description", Nullable(r.Description)),
            ("@WorkGroup", Nullable(r.WorkGroup)),
            ("@Specification", Nullable(r.Specification)),
            ("@CategoryDetail", Nullable(r.CategoryDetail)),
            ("@Url", Nullable(r.Url)),
            ("@HydratedAt", Format(r.HydratedAt)),
            ("@HydrationStatus", r.HydrationStatus),
            ("@HydrationReason", Nullable(r.HydrationReason)));
    }

    private static async Task InsertJiraXrefAsync(SqliteConnection connection, HydrationJiraXrefRow r, CancellationToken ct)
    {
        await ExecuteAsync(connection,
            "INSERT INTO planned_ticket_jira_xref (IssueKey, JiraKey, Source) VALUES (@IssueKey, @JiraKey, @Source)",
            ct,
            ("@IssueKey", r.TicketKey),
            ("@JiraKey", r.JiraKey),
            ("@Source", r.Source));
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
            await ReplaceWorkGroupCatalogAsync(connection, workGroups, ct);
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
            await ReplaceWorkGroupCatalogAsync(connection, workGroups, ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static async Task ReplaceWorkGroupCatalogAsync(
        SqliteConnection connection,
        IReadOnlyList<HydrationWorkGroupRow> workGroups,
        CancellationToken ct)
    {
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
    }

    // ---------------------------------------------------------------------
    // Hydration reads

    public async Task<PlannedTicketHydrationReadModel?> GetHydrationAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PlannedTicketHydrationRow? parent = await ReadHydrationParentAsync(connection, key, ct);
        IReadOnlyList<PlannedJiraHydrationRow> jira = await ReadJiraHydrationAsync(connection, key, ct);
        IReadOnlyList<PlannedZulipHydrationRow> zulip = await ReadZulipHydrationAsync(connection, key, ct);
        IReadOnlyList<PlannedGitHubHydrationRow> github = await ReadGitHubHydrationAsync(connection, key, ct);
        IReadOnlyList<PlannedRepoHydrationRow> repos = await ReadRepoHydrationAsync(connection, key, ct);
        IReadOnlyList<PlannedTicketJiraXrefRow> xref = await ReadJiraXrefAsync(connection, key, ct);
        if (parent is null && jira.Count == 0 && zulip.Count == 0 && github.Count == 0 && repos.Count == 0 && xref.Count == 0)
        {
            return null;
        }
        return new PlannedTicketHydrationReadModel(parent, jira, zulip, github, repos, xref);
    }

    private static async Task<PlannedTicketHydrationRow?> ReadHydrationParentAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey, Priority, Resolution, ResolutionDescriptionPlain, Specification, RaisedInVersion, SelectedBallot, ChangeCategory, Impact, Labels, CommentCount, DescriptionPlain, HydratedAt, HydrationStatus, HydrationReason, DescriptionHtml, ResolutionDescriptionHtml, Reporter, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw FROM planned_ticket_hydration WHERE IssueKey = @key";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PlannedTicketHydrationRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : (int?)reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            ParseDate(reader.GetString(12)),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : (DateTimeOffset?)ParseDate(reader.GetString(18)),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20));
    }

    private static async Task<IReadOnlyList<PlannedJiraHydrationRow>> ReadJiraHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        List<PlannedJiraHydrationRow> rows = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason, DescriptionHtml, ResolutionDescriptionHtml, Reporter, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw FROM planned_jira_hydration WHERE IssueKey = @key ORDER BY JiraKey";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PlannedJiraHydrationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : (DateTimeOffset?)ParseDate(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                ParseDate(reader.GetString(13)),
                reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : (DateTimeOffset?)ParseDate(reader.GetString(19)),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<PlannedZulipHydrationRow>> ReadZulipHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        List<PlannedZulipHydrationRow> rows = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount, FirstMessageAt, LastMessageAt, FirstMessageExcerpt, Url, HydratedAt, HydrationStatus, HydrationReason FROM planned_zulip_hydration WHERE IssueKey = @key ORDER BY ZulipThreadId";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PlannedZulipHydrationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : (int?)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : (int?)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : (DateTimeOffset?)ParseDate(reader.GetString(6)),
                reader.IsDBNull(7) ? null : (DateTimeOffset?)ParseDate(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                ParseDate(reader.GetString(10)),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<PlannedGitHubHydrationRow>> ReadGitHubHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        List<PlannedGitHubHydrationRow> rows = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey, GitHubItemId, Owner, Repo, Number, Path, Title, State, IsPullRequest, Labels, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason FROM planned_github_hydration WHERE IssueKey = @key ORDER BY GitHubItemId";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PlannedGitHubHydrationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : (int?)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : (bool?)(reader.GetInt32(8) != 0),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : (DateTimeOffset?)ParseDate(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                ParseDate(reader.GetString(12)),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<PlannedRepoHydrationRow>> ReadRepoHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        List<PlannedRepoHydrationRow> rows = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey, RepoKey, Description, WorkGroup, Specification, CategoryDetail, Url, HydratedAt, HydrationStatus, HydrationReason FROM planned_repo_hydration WHERE IssueKey = @key ORDER BY RepoKey";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PlannedRepoHydrationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ParseDate(reader.GetString(7)),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<PlannedTicketJiraXrefRow>> ReadJiraXrefAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        List<PlannedTicketJiraXrefRow> rows = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey, JiraKey, Source FROM planned_ticket_jira_xref WHERE IssueKey = @key ORDER BY JiraKey";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PlannedTicketJiraXrefRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }

    // ---------------------------------------------------------------------
    // Related-* writes (used when an agent payload also includes related items,
    // or when tests want to seed hydration inputs).

    public async Task UpsertRelatedJiraAsync(string issueKey, string jiraKey, string? source = null, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(connection,
            "INSERT OR IGNORE INTO planned_ticket_related_jira (IssueKey, JiraKey, Source) VALUES (@i, @j, @s)",
            ct, ("@i", issueKey), ("@j", jiraKey), ("@s", source ?? string.Empty));
    }

    public async Task UpsertRelatedZulipAsync(string issueKey, string threadId, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(connection,
            "INSERT OR IGNORE INTO planned_ticket_related_zulip (IssueKey, ZulipThreadId) VALUES (@i, @t)",
            ct, ("@i", issueKey), ("@t", threadId));
    }

    public async Task UpsertRelatedGitHubAsync(string issueKey, string itemId, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteAsync(connection,
            "INSERT OR IGNORE INTO planned_ticket_related_github (IssueKey, GitHubItemId) VALUES (@i, @g)",
            ct, ("@i", issueKey), ("@g", itemId));
    }

    // ---------------------------------------------------------------------
    // Topic persistence

    public async Task SaveTopicGroupingAsync(PlannedTicketTopicGroupingPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        PlannedTicketTopicGroupingPayloadValidator.ThrowIfInvalid(payload);

        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureLegacyAuthoringModeAsync(connection, ct);
            await EnsureGeneralMutationAllowedAsync(connection, ct);
            _ = await SaveTopicGroupingCoreAsync(connection, payload, savedAt, ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task SaveTopicGroupingForRunAsync(
        PlannedTicketTopicGroupingPayload payload,
        string runId,
        string stageId,
        string stageLeaseId,
        string inputFingerprint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        PlannedTicketTopicGroupingPayloadValidator.ThrowIfInvalid(payload);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
        string partitionKey = GetPartitionKey(
            payload.WorkGroupClean,
            payload.Specification,
            payload.Type);
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
                "grouping",
                partitionKey,
                ct);
            await EnsureGroupingTicketsBelongToPartitionAsync(connection, payload, ct);
            (int topics, int groups, int members) =
                await SaveTopicGroupingCoreAsync(connection, payload, savedAt, ct);
            await SavePartitionReceiptAsync(
                connection,
                runId,
                stageId,
                partitionKey,
                inputFingerprint,
                topics,
                groups,
                members,
                savedAt,
                ct);
            await ExecuteRawAsync(connection, "COMMIT", ct);
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
            FROM planned_ticket_partition_receipts
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

    private static async Task<(int Topics, int Groups, int Members)> SaveTopicGroupingCoreAsync(
        SqliteConnection connection,
        PlannedTicketTopicGroupingPayload payload,
        DateTimeOffset savedAt,
        CancellationToken ct)
    {
        await using (SqliteCommand findTopics = connection.CreateCommand())
        {
            findTopics.CommandText = "SELECT RowId FROM planned_ticket_topics WHERE WorkGroupClean = @w AND Specification = @s AND Type = @t";
            findTopics.Parameters.AddWithValue("@w", payload.WorkGroupClean);
            findTopics.Parameters.AddWithValue("@s", payload.Specification);
            findTopics.Parameters.AddWithValue("@t", payload.Type);
            List<long> oldTopicRowIds = [];
            await using SqliteDataReader reader = await findTopics.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                oldTopicRowIds.Add(reader.GetInt64(0));
            }
            foreach (long rowId in oldTopicRowIds)
            {
                await ExecuteAsync(connection, "DELETE FROM planned_ticket_topic_repos WHERE TopicRowId = @r", ct, ("@r", rowId));
                await ExecuteAsync(connection, "DELETE FROM planned_ticket_topic_members WHERE TopicRowId = @r", ct, ("@r", rowId));
                await ExecuteAsync(connection, "DELETE FROM planned_ticket_topic_groups WHERE TopicRowId = @r", ct, ("@r", rowId));
                await ExecuteAsync(connection, "DELETE FROM planned_ticket_topics WHERE RowId = @r", ct, ("@r", rowId));
            }
        }

        int topicRows = 0;
        int groupRows = 0;
        int memberRows = 0;
        foreach (PlannedTicketTopicPayload topic in payload.Topics)
        {
            long topicRowId = await InsertTopicAndReturnRowIdAsync(connection, payload, topic, savedAt, ct);
            topicRows++;
            IReadOnlyList<string> normalizedRepos = PlannedTicketTopicGroupingPayloadValidator.NormalizeSpannedRepos(topic.SpannedRepos);
            for (int i = 0; i < normalizedRepos.Count; i++)
                await ExecuteAsync(connection, "INSERT INTO planned_ticket_topic_repos (Id, TopicRowId, RepoKey, OrderInTopic) VALUES (@id, @t, @r, @o)", ct, ("@id", Guid.NewGuid().ToString("N")), ("@t", topicRowId), ("@r", normalizedRepos[i]), ("@o", i));

            int groupOrderInTopic = 0;
            foreach (PlannedTicketTopicGroupPayload group in topic.LinkedTicketGroups)
            {
                long topicGroupRowId = await InsertTopicGroupAndReturnRowIdAsync(connection, topicRowId, group, savedAt, groupOrderInTopic++, ct);
                groupRows++;
                foreach (PlannedTicketTopicGroupMemberPayload member in group.Members)
                {
                    await ExecuteAsync(connection, "INSERT INTO planned_ticket_topic_members (Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer) VALUES (@id, @t, @g, @k, @o)", ct, ("@id", Guid.NewGuid().ToString("N")), ("@t", topicRowId), ("@g", topicGroupRowId), ("@k", member.TicketKey), ("@o", member.Order));
                    memberRows++;
                }
            }
            int remainingOrder = 0;
            foreach (string remaining in topic.RemainingTicketKeys)
            {
                await ExecuteAsync(connection, "INSERT INTO planned_ticket_topic_members (Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer) VALUES (@id, @t, NULL, @k, @o)", ct, ("@id", Guid.NewGuid().ToString("N")), ("@t", topicRowId), ("@k", remaining), ("@o", remainingOrder++));
                memberRows++;
            }
        }
        return (topicRows, groupRows, memberRows);
    }

    private static async Task<long> InsertTopicAndReturnRowIdAsync(
        SqliteConnection connection,
        PlannedTicketTopicGroupingPayload payload,
        PlannedTicketTopicPayload topic,
        DateTimeOffset savedAt,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO planned_ticket_topics (Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
            VALUES (@Id, @WorkGroupClean, @WorkGroupDisplay, @Specification, @Type, @ShortDescription, @LongerDescription, @RenderOrderHint, @SavedAt);
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
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertTopicGroupAndReturnRowIdAsync(
        SqliteConnection connection,
        long topicRowId,
        PlannedTicketTopicGroupPayload group,
        DateTimeOffset savedAt,
        int orderInTopic,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO planned_ticket_topic_groups (Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic, SavedAt)
            VALUES (@Id, @TopicRowId, @FirstTicketKey, @Rationale, @OrderInTopic, @SavedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TopicRowId", topicRowId);
        command.Parameters.AddWithValue("@FirstTicketKey", group.FirstTicketKey);
        command.Parameters.AddWithValue("@Rationale", group.Rationale);
        command.Parameters.AddWithValue("@OrderInTopic", orderInTopic);
        command.Parameters.AddWithValue("@SavedAt", Format(savedAt));
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<PlannedTicketTopicsForCategory?> GetWorkGroupTopicsAsync(
        string workGroupClean, string specification, string type, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();

        List<(long RowId, string Id, string ShortDesc, string LongDesc, int? RenderHint, string WgDisplay, DateTimeOffset SavedAt)> topics = [];
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT RowId, Id, ShortDescription, LongerDescription, RenderOrderHint, WorkGroupDisplay, SavedAt FROM planned_ticket_topics WHERE WorkGroupClean = @w AND Specification = @s AND Type = @t ORDER BY COALESCE(RenderOrderHint, 1000000), ShortDescription";
            cmd.Parameters.AddWithValue("@w", workGroupClean);
            cmd.Parameters.AddWithValue("@s", specification);
            cmd.Parameters.AddWithValue("@t", type);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                topics.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : (int?)r.GetInt32(4),
                    r.GetString(5),
                    ParseDate(r.GetString(6))));
            }
        }

        if (topics.Count == 0) return null;

        List<PlannedTicketTopicDetail> topicDetails = [];
        DateTimeOffset latestSaved = topics.Max(t => t.SavedAt);
        string workGroupDisplay = topics.First().WgDisplay;

        foreach (var t in topics)
        {
            List<string> spannedRepos = [];
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT RepoKey FROM planned_ticket_topic_repos WHERE TopicRowId = @r ORDER BY OrderInTopic";
                cmd.Parameters.AddWithValue("@r", t.RowId);
                await using SqliteDataReader rr = await cmd.ExecuteReaderAsync(ct);
                while (await rr.ReadAsync(ct)) spannedRepos.Add(rr.GetString(0));
            }

            List<(long Id, string FirstTicketKey, string Rationale, int Order)> groups = [];
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT RowId, FirstTicketKey, Rationale, OrderInTopic FROM planned_ticket_topic_groups WHERE TopicRowId = @r ORDER BY OrderInTopic";
                cmd.Parameters.AddWithValue("@r", t.RowId);
                await using SqliteDataReader rg = await cmd.ExecuteReaderAsync(ct);
                while (await rg.ReadAsync(ct))
                {
                    groups.Add((rg.GetInt64(0), rg.GetString(1), rg.GetString(2), rg.GetInt32(3)));
                }
            }

            List<PlannedTicketTopicGroup> groupDetails = [];
            foreach (var g in groups)
            {
                List<PlannedTicketTopicGroupMember> members = [];
                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT TicketKey, OrderInContainer FROM planned_ticket_topic_members WHERE TopicGroupRowId = @g ORDER BY OrderInContainer";
                cmd.Parameters.AddWithValue("@g", g.Id);
                await using SqliteDataReader rm = await cmd.ExecuteReaderAsync(ct);
                while (await rm.ReadAsync(ct))
                {
                    members.Add(new PlannedTicketTopicGroupMember(rm.GetString(0), rm.GetInt32(1)));
                }
                groupDetails.Add(new PlannedTicketTopicGroup(g.FirstTicketKey, g.Rationale, members));
            }

            List<string> remaining = [];
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT TicketKey FROM planned_ticket_topic_members WHERE TopicRowId = @r AND TopicGroupRowId IS NULL ORDER BY OrderInContainer";
                cmd.Parameters.AddWithValue("@r", t.RowId);
                await using SqliteDataReader rr = await cmd.ExecuteReaderAsync(ct);
                while (await rr.ReadAsync(ct)) remaining.Add(rr.GetString(0));
            }

            topicDetails.Add(new PlannedTicketTopicDetail(t.Id, t.ShortDesc, t.LongDesc, t.RenderHint, spannedRepos, groupDetails, remaining));
        }

        return new PlannedTicketTopicsForCategory(workGroupClean, workGroupDisplay, specification, type, latestSaved, topicDetails);
    }

    // ---------------------------------------------------------------------
    // Ticket queries

    public async Task<IReadOnlyList<PlannedTicketSummary>> ListPlannedTicketsAsync(PlannedTicketQueryFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new PlannedTicketQueryFilter();
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        List<string> wheres = [];
        if (!string.IsNullOrEmpty(filter.Repo))
        {
            wheres.Add("EXISTS (SELECT 1 FROM planned_ticket_repos r WHERE r.IssueKey = pt.Key AND r.RepoKey = @repo)");
            command.Parameters.AddWithValue("@repo", filter.Repo);
        }
        if (!string.IsNullOrEmpty(filter.AffectedFilePath))
        {
            wheres.Add("EXISTS (SELECT 1 FROM planned_ticket_repo_impacts i WHERE i.IssueKey = pt.Key AND i.AffectedFilePath = @afp)");
            command.Parameters.AddWithValue("@afp", filter.AffectedFilePath);
        }
        if (!string.IsNullOrEmpty(filter.RelatedJiraKey))
        {
            wheres.Add("EXISTS (SELECT 1 FROM planned_ticket_related_jira j WHERE j.IssueKey = pt.Key AND j.JiraKey = @rj)");
            command.Parameters.AddWithValue("@rj", filter.RelatedJiraKey);
        }
        string whereClause = wheres.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", wheres);
        command.CommandText = $"SELECT Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt FROM planned_tickets pt {whereClause} ORDER BY Key LIMIT @limit OFFSET @offset";
        command.Parameters.AddWithValue("@limit", filter.Limit);
        command.Parameters.AddWithValue("@offset", filter.Offset);
        List<PlannedTicketSummary> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PlannedTicketSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseDate(reader.GetString(5))));
        }
        return rows;
    }

    public async Task<PlannedTicketDetail?> GetPlannedTicketAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PlannedTicketSummary? summary = null;
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt FROM planned_tickets WHERE Key = @k";
            cmd.Parameters.AddWithValue("@k", key);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                summary = new PlannedTicketSummary(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), ParseDate(r.GetString(5)));
            }
        }
        if (summary is null) return null;

        List<PlannedTicketRepoItem> repos = [];
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT RepoKey, RepoRevision, Justification FROM planned_ticket_repos WHERE IssueKey = @k ORDER BY RepoKey";
            cmd.Parameters.AddWithValue("@k", key);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                repos.Add(new PlannedTicketRepoItem(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
            }
        }

        List<PlannedTicketRepoChangeItem> changes = [];
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, TicketRepoId, RepoKey, ChangeSequence, FilePath, ChangeTitle, ChangeDescription, SourceLineStart, SourceLineEnd, ReplacementLines, Reason FROM planned_ticket_repo_changes WHERE IssueKey = @k ORDER BY ChangeSequence";
            cmd.Parameters.AddWithValue("@k", key);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string repls = r.GetString(9);
                changes.Add(new PlannedTicketRepoChangeItem(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                    r.GetString(5), r.GetString(6),
                    r.IsDBNull(7) ? null : (int?)r.GetInt32(7),
                    r.IsDBNull(8) ? null : (int?)r.GetInt32(8),
                    ReplacementLineJson.Deserialize(repls),
                    r.GetString(10)));
            }
        }

        List<PlannedTicketRepoImpactItem> impacts = [];
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TicketRepoId, RepoKey, TicketRepoChangeId, AffectedFilePath, HowAffected FROM planned_ticket_repo_impacts WHERE IssueKey = @k";
            cmd.Parameters.AddWithValue("@k", key);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                impacts.Add(new PlannedTicketRepoImpactItem(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4)));
            }
        }

        List<PlannedTicketChangeValidationItem> validations = [];
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TicketRepoId, RepoKey, ValidationSequence, Action FROM planned_ticket_change_validations WHERE IssueKey = @k ORDER BY ValidationSequence";
            cmd.Parameters.AddWithValue("@k", key);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                validations.Add(new PlannedTicketChangeValidationItem(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
            }
        }

        List<PlannedTicketTestingConsiderationItem> tests = [];
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TicketRepoId, RepoKey, ConsiderationSequence, Consideration FROM planned_ticket_testing_considerations WHERE IssueKey = @k ORDER BY ConsiderationSequence";
            cmd.Parameters.AddWithValue("@k", key);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                tests.Add(new PlannedTicketTestingConsiderationItem(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
            }
        }

        List<PlannedTicketOpenQuestionItem> questions = [];
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TicketRepoId, RepoKey, QuestionSequence, Question FROM planned_ticket_open_questions WHERE IssueKey = @k ORDER BY QuestionSequence";
            cmd.Parameters.AddWithValue("@k", key);
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                questions.Add(new PlannedTicketOpenQuestionItem(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
            }
        }

        return new PlannedTicketDetail(summary, repos, changes, impacts, validations, tests, questions);
    }

    public async Task<IReadOnlyList<PlannedJiraHydrationRow>> ListJiraHydrationDisplayForWorkGroupAsync(string workGroupClean, CancellationToken ct = default)
    {
        List<PlannedJiraHydrationRow> rows = [];
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason, DescriptionHtml, ResolutionDescriptionHtml, Reporter, CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw FROM planned_jira_hydration WHERE WorkGroupClean = @w AND IssueKey = JiraKey ORDER BY JiraKey";
        command.Parameters.AddWithValue("@w", workGroupClean);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PlannedJiraHydrationRow(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : (DateTimeOffset?)ParseDate(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                ParseDate(reader.GetString(13)),
                reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : (DateTimeOffset?)ParseDate(reader.GetString(19)),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21)));
        }
        return rows;
    }

    /// <summary>
    /// Returns the per-ticket clustering signal projection the
    /// <c>planner-topic-groupings</c> skill needs to bucket tickets
    /// into Topics / Linked Ticket Groups for one workgroup. Returns
    /// <c>null</c> when the workgroup has zero
    /// <c>planned_jira_hydration</c> self-rows (controller 404s).
    /// <para>
    /// The anchor differs from the preparer-side query: it joins
    /// <c>jira_processing_source_tickets</c> (the source-of-truth
    /// workgroup filter) so tickets that are planned but lack a
    /// self-Jira hydration row entirely are still surfaced with
    /// <c>HydrationStatus = null</c>. The skill enforces Open
    /// Question 3's abort contract on either <c>null</c> or any
    /// non-<c>"resolved"</c> value.
    /// </para>
    /// Per-ticket repo / repo-change / repo-impact rows are pulled in
    /// three follow-up queries scoped through
    /// <c>planned_jira_hydration</c> for indexed-scope efficiency and
    /// then bucketed by <c>IssueKey</c>. Tickets without matching
    /// rows get empty lists.
    /// </summary>
    public async Task<PlannedTicketClusteringSignals?> GetClusteringSignalsAsync(string workGroupClean, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();

        List<PlannedTicketClusteringSignal> tickets = [];
        Dictionary<string, List<string>> reposByTicket = new(StringComparer.Ordinal);
        Dictionary<string, List<PlannedTicketClusteringRepoChange>> changesByTicket = new(StringComparer.Ordinal);
        Dictionary<string, List<PlannedTicketClusteringRepoImpact>> impactsByTicket = new(StringComparer.Ordinal);

        // 1) Anchor: every jira_processing_source_tickets row whose
        //    WorkGroup (display form) is associated with the requested
        //    workgroup-clean slug. The planner stores WorkGroup as the
        //    display form on jira_processing_source_tickets and as the
        //    cleaned form on planned_jira_hydration; we resolve the
        //    display form via the inner subquery (hydration always
        //    carries WorkGroupClean), then filter src by that display
        //    form. Tickets with no self-hydration row still appear (their
        //    HydrationStatus comes back NULL via the LEFT JOIN). Tickets
        //    that have neither a planned_tickets row nor a hydration
        //    self-row are filtered out (they cannot be partitioned and
        //    have nothing to write).
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT src.Key AS IssueKey,
                       COALESCE(j.Title, src.Title) AS Title,
                       COALESCE(j.Status, src.Status) AS Status,
                       COALESCE(j.Specification, src.Specification) AS Specification,
                       COALESCE(j.Type, src.Type) AS Type,
                       j.HydrationStatus,
                       COALESCE(pt.ResolutionSummary, '') AS ResolutionSummary,
                       COALESCE(pt.FeatureProposal, '')   AS FeatureProposal,
                       COALESCE(pt.DesignRationale, '')   AS DesignRationale,
                       CASE WHEN pt.Key IS NULL THEN 0 ELSE 1 END AS HasPlannedTicket
                FROM jira_processing_source_tickets src
                LEFT JOIN planned_jira_hydration j
                       ON j.IssueKey = src.Key AND j.JiraKey = j.IssueKey
                LEFT JOIN planned_tickets pt
                       ON pt.Key = src.Key
                WHERE src.WorkGroup IN (
                        SELECT DISTINCT j2.WorkGroup
                        FROM planned_jira_hydration j2
                        WHERE j2.WorkGroupClean = @wg
                          AND j2.JiraKey = j2.IssueKey
                          AND j2.WorkGroup IS NOT NULL
                      )
                  AND (pt.Key IS NOT NULL OR j.IssueKey IS NOT NULL)
                ORDER BY src.Key ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string issueKey = reader.GetString(0);
                List<string> repos = [];
                List<PlannedTicketClusteringRepoChange> changes = [];
                List<PlannedTicketClusteringRepoImpact> impacts = [];
                reposByTicket[issueKey] = repos;
                changesByTicket[issueKey] = changes;
                impactsByTicket[issueKey] = impacts;
                tickets.Add(new PlannedTicketClusteringSignal(
                    IssueKey: issueKey,
                    Title: ReadNullableString(reader, 1),
                    Status: ReadNullableString(reader, 2),
                    Specification: ReadNullableString(reader, 3),
                    Type: ReadNullableString(reader, 4),
                    HydrationStatus: ReadNullableString(reader, 5),
                    ResolutionSummary: ReadNullableString(reader, 6) ?? string.Empty,
                    FeatureProposal: ReadNullableString(reader, 7) ?? string.Empty,
                    DesignRationale: ReadNullableString(reader, 8) ?? string.Empty,
                    HasPlannedTicket: reader.GetInt32(9) == 1,
                    Repos: repos,
                    RepoChanges: changes,
                    RepoImpacts: impacts));
            }
        }

        if (tickets.Count == 0)
        {
            return null;
        }

        // 2) Per-ticket repos.
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.IssueKey, r.RepoKey
                FROM planned_ticket_repos r
                INNER JOIN planned_jira_hydration j
                        ON j.IssueKey = r.IssueKey AND j.JiraKey = j.IssueKey
                WHERE j.WorkGroupClean = @wg
                ORDER BY r.IssueKey ASC, r.RepoKey ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string issueKey = reader.GetString(0);
                if (reposByTicket.TryGetValue(issueKey, out List<string>? bucket))
                {
                    bucket.Add(reader.GetString(1));
                }
            }
        }

        // 3) Per-ticket repo changes.
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.IssueKey, c.RepoKey, c.FilePath
                FROM planned_ticket_repo_changes c
                INNER JOIN planned_jira_hydration j
                        ON j.IssueKey = c.IssueKey AND j.JiraKey = j.IssueKey
                WHERE j.WorkGroupClean = @wg
                ORDER BY c.IssueKey ASC, c.RepoKey ASC, c.FilePath ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string issueKey = reader.GetString(0);
                if (changesByTicket.TryGetValue(issueKey, out List<PlannedTicketClusteringRepoChange>? bucket))
                {
                    bucket.Add(new PlannedTicketClusteringRepoChange(
                        RepoKey: reader.GetString(1),
                        FilePath: reader.GetString(2)));
                }
            }
        }

        // 4) Per-ticket repo impacts.
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.IssueKey, i.RepoKey, i.AffectedFilePath
                FROM planned_ticket_repo_impacts i
                INNER JOIN planned_jira_hydration j
                        ON j.IssueKey = i.IssueKey AND j.JiraKey = j.IssueKey
                WHERE j.WorkGroupClean = @wg
                ORDER BY i.IssueKey ASC, i.RepoKey ASC, i.AffectedFilePath ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string issueKey = reader.GetString(0);
                if (impactsByTicket.TryGetValue(issueKey, out List<PlannedTicketClusteringRepoImpact>? bucket))
                {
                    bucket.Add(new PlannedTicketClusteringRepoImpact(
                        RepoKey: reader.GetString(1),
                        AffectedFilePath: reader.GetString(2)));
                }
            }
        }

        string? workGroupDisplay = await ResolveWorkGroupDisplayAsync(connection, workGroupClean, ct);
        return new PlannedTicketClusteringSignals(workGroupClean, workGroupDisplay, tickets);
    }

    /// <summary>
    /// Resolves the display form of a workgroup-clean slug. Tries the
    /// most-recent <c>planned_ticket_topics.WorkGroupDisplay</c> first,
    /// then falls back to the most-recent
    /// <c>planned_jira_hydration.WorkGroup</c> self-row. Mirrors the
    /// preparer's two-tier fallback. Returns <c>null</c> when both
    /// tiers come up empty.
    /// </summary>
    private static async Task<string?> ResolveWorkGroupDisplayAsync(SqliteConnection connection, string workGroupClean, CancellationToken ct)
    {
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT WorkGroupDisplay FROM planned_ticket_topics
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
                SELECT j.WorkGroup FROM planned_jira_hydration j
                WHERE j.JiraKey = j.IssueKey
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

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    // ---------------------------------------------------------------------
    // Helpers

    private async Task<IReadOnlyList<string>> ReadStringColumnAsync(string sql, string key, CancellationToken ct)
    {
        List<string> rows = [];
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private static async Task CapturePreChangePartitionAsync(
        SqliteConnection connection,
        string ticketKey,
        string runItemId,
        DateTimeOffset capturedAt,
        CancellationToken ct)
    {
        string? workGroupClean = null;
        string? workGroupDisplay = null;
        string? specification = null;
        string? type = null;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT WorkGroupClean, WorkGroup, IFNULL(Specification, 'Unspecified'), IFNULL(Type, '')
                FROM planned_jira_hydration
                WHERE IssueKey = @key AND JiraKey = @key
                LIMIT 1
                """;
            command.Parameters.AddWithValue("@key", ticketKey);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
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
            INSERT INTO planned_ticket_run_item_partitions(
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

    private static async Task UpsertAuthoringStateAsync(
        SqliteConnection connection,
        string ticketKey,
        string classification,
        string graphHash,
        string? receiptContentHash,
        string? runId,
        string? runItemId,
        string? operationId,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO planned_ticket_authoring_state(
                TicketKey, Classification, GraphHash, ReceiptContentHash,
                LegacyCompletionId, LegacyCompletedProcessingAt,
                RunId, RunItemId, OperationId, UpdatedAt)
            VALUES(
                @ticketKey, @classification, @graphHash, @receiptContentHash,
                (SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = @ticketKey),
                (SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = @ticketKey),
                @runId, @runItemId, @operationId, @updatedAt)
            ON CONFLICT(TicketKey) DO UPDATE SET
                Classification = excluded.Classification,
                GraphHash = excluded.GraphHash,
                ReceiptContentHash = excluded.ReceiptContentHash,
                LegacyCompletionId = CASE
                    WHEN excluded.Classification = 'legacy-unverified'
                    THEN excluded.LegacyCompletionId
                    ELSE planned_ticket_authoring_state.LegacyCompletionId
                END,
                LegacyCompletedProcessingAt = CASE
                    WHEN excluded.Classification = 'legacy-unverified'
                    THEN excluded.LegacyCompletedProcessingAt
                    ELSE planned_ticket_authoring_state.LegacyCompletedProcessingAt
                END,
                RunId = excluded.RunId,
                RunItemId = excluded.RunItemId,
                OperationId = excluded.OperationId,
                UpdatedAt = excluded.UpdatedAt
            """,
            ct,
            ("@ticketKey", ticketKey),
            ("@classification", classification),
            ("@graphHash", graphHash),
            ("@receiptContentHash", receiptContentHash),
            ("@runId", runId),
            ("@runItemId", runItemId),
            ("@operationId", operationId),
            ("@updatedAt", Format(updatedAt)));
    }

    private static async Task<string> ComputePlannedGraphHashAsync(
        SqliteConnection connection,
        string key,
        CancellationToken ct)
    {
        StringBuilder builder = new();
        (string Section, string Sql, int? ReplacementLinesOrdinal)[] queries =
        [
            ("ticket", "SELECT Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale FROM planned_tickets WHERE Key = @key", null),
            ("repos", "SELECT RepoKey, RepoRevision, Justification FROM planned_ticket_repos WHERE IssueKey = @key ORDER BY RepoKey, RepoRevision, Justification", null),
            ("changes", "SELECT RepoKey, ChangeSequence, FilePath, ChangeTitle, ChangeDescription, SourceLineStart, SourceLineEnd, ReplacementLines, Reason FROM planned_ticket_repo_changes WHERE IssueKey = @key ORDER BY RepoKey, ChangeSequence, FilePath", 7),
            ("impacts", "SELECT RepoKey, AffectedFilePath, HowAffected FROM planned_ticket_repo_impacts WHERE IssueKey = @key ORDER BY RepoKey, AffectedFilePath", null),
            ("validations", "SELECT RepoKey, ValidationSequence, Action FROM planned_ticket_change_validations WHERE IssueKey = @key ORDER BY RepoKey, ValidationSequence", null),
            ("testing", "SELECT RepoKey, ConsiderationSequence, Consideration FROM planned_ticket_testing_considerations WHERE IssueKey = @key ORDER BY RepoKey, ConsiderationSequence", null),
            ("questions", "SELECT RepoKey, QuestionSequence, Question FROM planned_ticket_open_questions WHERE IssueKey = @key ORDER BY RepoKey, QuestionSequence", null),
        ];
        foreach ((string section, string sql, int? replacementLinesOrdinal) in queries)
        {
            builder.Append("section:").Append(section).Append('\n');
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@key", key);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (reader.IsDBNull(ordinal))
                    {
                        builder.Append("<null>");
                    }
                    else if (replacementLinesOrdinal == ordinal)
                    {
                        builder.Append(ReplacementLineJson.Serialize(
                            ReplacementLineJson.Deserialize(reader.GetString(ordinal))));
                    }
                    else
                    {
                        builder.Append(reader.GetValue(ordinal));
                    }
                    builder.Append('\u001f');
                }
                builder.Append('\n');
            }
        }
        return AuthoringResultHasher.HashNormalizedUtf8(builder.ToString());
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
            SELECT 1 FROM authoring_run_stages
            WHERE Id = @stageId AND RunId = @runId AND LeaseId = @leaseId
              AND InputFingerprint = @inputFingerprint
              AND StageName = @stageName AND PartitionKey = @partitionKey
              AND Status = @status
              AND EXISTS(
                  SELECT 1 FROM authoring_mutation_fences
                  WHERE ProcessorKind = @processorKind AND RunId = @runId)
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
                $"Stage '{stageId}' no longer owns input '{inputFingerprint}'.");
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
            $"Legacy planned-ticket writes are unavailable while processor mode is '{mode}'.");
    }

    private static async Task EnsureGeneralMutationAllowedAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        string? fenceRunId = await ReadFenceRunIdAsync(connection, ct);
        if (fenceRunId is null || fenceRunId.StartsWith("maintenance:", StringComparison.Ordinal))
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
        if (fenceRunId is null || fenceRunId.StartsWith("maintenance:", StringComparison.Ordinal))
        {
            return;
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1 FROM authoring_run_items
            WHERE RunId = @runId AND BusinessKey COLLATE NOCASE = @ticketKey
              AND AcceptedReceiptId IS NOT NULL
            """;
        command.Parameters.AddWithValue("@runId", fenceRunId);
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
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

    private static async Task EnsureGroupingTicketsBelongToPartitionAsync(
        SqliteConnection connection,
        PlannedTicketTopicGroupingPayload payload,
        CancellationToken ct)
    {
        IEnumerable<string> ticketKeys = payload.Topics.SelectMany(topic =>
            topic.RemainingTicketKeys.Concat(
                topic.LinkedTicketGroups.SelectMany(group => group.Members.Select(member => member.TicketKey))));
        foreach (string ticketKey in ticketKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM planned_ticket_authoring_state s
                    INNER JOIN authoring_result_receipts r ON r.OperationId = s.OperationId
                    INNER JOIN authoring_run_items i
                        ON i.Id = s.RunItemId
                       AND i.AcceptedReceiptId = r.Id
                       AND i.Status IN (@complete, @superseded)
                    INNER JOIN planned_jira_hydration j
                        ON j.IssueKey = s.TicketKey AND j.JiraKey = s.TicketKey
                    WHERE s.Classification = 'receipt-backed'
                      AND s.TicketKey = @ticketKey
                      AND j.WorkGroupClean = @workGroupClean
                      AND IFNULL(j.Specification, 'Unspecified') = @specification
                      AND IFNULL(j.Type, '') = @type)
                """;
            command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
            command.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
            command.Parameters.AddWithValue("@ticketKey", ticketKey);
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
                    $"Ticket '{ticketKey}' does not belong to grouping partition '{GetPartitionKey(payload.WorkGroupClean, payload.Specification, payload.Type)}'.");
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
            INNER JOIN planned_jira_hydration j
                ON j.IssueKey = v.TicketKey AND j.JiraKey = v.TicketKey
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

    private static Task SavePartitionReceiptAsync(
        SqliteConnection connection,
        string runId,
        string stageId,
        string partitionKey,
        string inputFingerprint,
        int topicRows,
        int groupRows,
        int memberRows,
        DateTimeOffset persistedAt,
        CancellationToken ct)
        => ExecuteAsync(
            connection,
            """
            INSERT INTO planned_ticket_partition_receipts(
                RunId, StageId, PartitionKey, InputFingerprint,
                TopicRows, TopicGroupRows, MemberRows, PersistedAt)
            VALUES(@runId, @stageId, @partitionKey, @inputFingerprint,
                   @topicRows, @groupRows, @memberRows, @persistedAt)
            ON CONFLICT(RunId, PartitionKey) DO UPDATE SET
                StageId = excluded.StageId,
                InputFingerprint = excluded.InputFingerprint,
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
            ("@topicRows", topicRows),
            ("@groupRows", groupRows),
            ("@memberRows", memberRows),
            ("@persistedAt", Format(persistedAt)));

    public static string GetPartitionKey(
        string? workGroupClean,
        string? specification,
        string? type)
        => string.Join(
            "\u001f",
            NormalizePartitionValue(workGroupClean, "unattributed"),
            NormalizePartitionValue(specification, "Unspecified"),
            NormalizePartitionValue(type, string.Empty));

    private static string NormalizePartitionValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void ExecuteRaw(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task ExecuteRawAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
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

    private static object Nullable(object? value) => value ?? DBNull.Value;

    private static string Format(DateTimeOffset value)
        => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
