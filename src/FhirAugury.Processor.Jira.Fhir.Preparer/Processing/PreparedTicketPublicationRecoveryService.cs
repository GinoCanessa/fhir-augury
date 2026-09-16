using System.Globalization;
using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketPublicationRecoveryService(
    PreparerDatabase database,
    AuthoringRunStore authoringStore,
    ILogger<PreparedTicketPublicationRecoveryService> logger)
{
    public async Task RecoverPendingAsync(CancellationToken ct = default)
    {
        foreach (string runId in
                 await database.ListPendingPublicationReconciliationsAsync(ct))
        {
            try
            {
                _ = await RecoverAsync(runId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Publication reconciliation {RunId} remains pending",
                    runId);
            }
        }
    }

    public async Task<AuthoringSnapshotDescriptor> RecoverAsync(
        string runId,
        CancellationToken ct = default)
    {
        try
        {
            PreparerDatabase.PublicationReconciliationPromotion promotion =
                await database.GetPendingPublicationReconciliationAsync(
                    runId,
                    ct)
                ?? throw new InvalidOperationException(
                    $"Reconciliation '{runId}' is not awaiting recovery.");
            AuthoringReviewSnapshotRecord snapshot = (
                    await authoringStore.GetSnapshotRecordsAsync(ct))
                .SingleOrDefault(value =>
                    string.Equals(
                        value.Id,
                        promotion.SnapshotId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        value.RunId,
                        runId,
                        StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Pending snapshot '{promotion.SnapshotId}' has no database record.");

            if (!File.Exists(promotion.FinalPath))
            {
                await PrepareTemporarySnapshotAsync(
                    promotion,
                    snapshot,
                    ct);
                try
                {
                    File.Move(
                        promotion.TemporaryPath,
                        promotion.FinalPath,
                        overwrite: false);
                }
                catch (IOException) when (File.Exists(promotion.FinalPath))
                {
                    // Another recovery attempt won the immutable publication
                    // race. Validate its bytes below; never overwrite it.
                }
            }

            SqliteReviewSnapshotValidationResult validation =
                await SqliteReviewSnapshotValidator.ValidateAsync(
                    snapshot,
                    promotion.FinalPath,
                    ct: ct);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    $"Conflicting or corrupt final snapshot evidence: {validation.Error}");
            }

            if (snapshot.Status == AuthoringStatusValues.Snapshots.Creating)
            {
                await authoringStore.MarkSnapshotPromotedAsync(
                    snapshot.Id,
                    validation.ChecksumSha256
                        ?? throw new InvalidOperationException(
                            "The verified snapshot has no checksum."),
                    validation.SizeBytes,
                    ct: ct);
                snapshot.Status = AuthoringStatusValues.Snapshots.Promoted;
            }
            AuthoringSnapshotDescriptor descriptor =
                snapshot.Status == AuthoringStatusValues.Snapshots.Ready
                    ? await authoringStore.GetSnapshotDescriptorAsync(
                        snapshot.Id,
                        ct)
                      ?? throw new InvalidOperationException(
                          $"Ready snapshot '{snapshot.Id}' has no descriptor.")
                    : await authoringStore.MarkSnapshotReadyAsync(
                        snapshot.Id,
                        ct: ct);

            AuthoringRunRecord run =
                await authoringStore.GetRunAsync(runId, ct)
                ?? throw new KeyNotFoundException(
                    $"Authoring run '{runId}' was not found.");
            if (run.Status is not (
                    AuthoringStatusValues.Runs.Completed or
                    AuthoringStatusValues.Runs.CompletedDatabaseOnly))
            {
                await authoringStore.MarkRunFinalizingAsync(runId, ct: ct);
                await authoringStore.CompleteRunAsync(
                    runId,
                    snapshot.Id,
                    ct: ct);
            }
            await database.MarkPublicationReconciliationReadyAsync(
                runId,
                ct);
            await database.CleanupPublicationReconciliationWorkspaceAsync(
                runId,
                ct);
            return descriptor;
        }
        catch (Exception ex)
        {
            await database.RecordPublicationReconciliationRecoveryFailureAsync(
                runId,
                ex.Message,
                CancellationToken.None);
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .PromotionRecoveryFailure,
                ex.Message);
        }
    }

    private static async Task PrepareTemporarySnapshotAsync(
        PreparerDatabase.PublicationReconciliationPromotion promotion,
        AuthoringReviewSnapshotRecord snapshot,
        CancellationToken ct)
    {
        if (!File.Exists(promotion.TemporaryPath))
        {
            throw new FileNotFoundException(
                "The pending reconciliation has neither a temporary nor a final snapshot.",
                promotion.TemporaryPath);
        }
        string checksum =
            await SqliteReviewSnapshotWriter.ComputeSha256Async(
                promotion.TemporaryPath,
                ct);
        bool hasPromotionProvenance =
            await HasMatchingProvenanceAsync(
                promotion.TemporaryPath,
                snapshot.Id,
                ct);
        if (!hasPromotionProvenance &&
            !string.Equals(
                checksum,
                promotion.CandidateSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The temporary reconciliation snapshot checksum does not match the journal.");
        }
        if (!hasPromotionProvenance)
        {
            await WriteProvenanceAsync(
                promotion.TemporaryPath,
                promotion,
                ct);
        }
        SqliteReviewSnapshotValidationResult validation =
            await SqliteReviewSnapshotValidator.ValidateAsync(
                snapshot,
                promotion.TemporaryPath,
                ct: ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Temporary reconciliation snapshot validation failed: {validation.Error}");
        }
    }

    private static async Task<bool> HasMatchingProvenanceAsync(
        string path,
        string snapshotId,
        CancellationToken ct)
    {
        await using SqliteConnection connection =
            await OpenReadWriteAsync(path, ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotId
            FROM authoring_snapshot_provenance
            LIMIT 1
            """;
        try
        {
            return string.Equals(
                (string?)await command.ExecuteScalarAsync(ct),
                snapshotId,
                StringComparison.Ordinal);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return false;
        }
    }

    private static async Task WriteProvenanceAsync(
        string path,
        PreparerDatabase.PublicationReconciliationPromotion promotion,
        CancellationToken ct)
    {
        await using SqliteConnection connection =
            await OpenReadWriteAsync(path, ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            DROP TABLE IF EXISTS authoring_snapshot_provenance;
            CREATE TABLE authoring_snapshot_provenance(
                SnapshotId TEXT NOT NULL,
                ProcessorKind TEXT NOT NULL,
                RunId TEXT NOT NULL,
                AuthoringEpoch INTEGER NOT NULL,
                Sequence INTEGER NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                ItemCount INTEGER NOT NULL,
                ReceiptCount INTEGER NOT NULL,
                TableCountsJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            INSERT INTO authoring_snapshot_provenance(
                SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                SchemaVersion, ItemCount, ReceiptCount, TableCountsJson,
                CreatedAt)
            VALUES(
                @snapshotId, 'jira-fhir', @runId, @epoch, @sequence,
                @schemaVersion, @itemCount, @receiptCount, @counts,
                @createdAt);
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        command.Parameters.AddWithValue("@snapshotId", promotion.SnapshotId);
        command.Parameters.AddWithValue("@runId", promotion.RunId);
        command.Parameters.AddWithValue("@epoch", promotion.AuthoringEpoch);
        command.Parameters.AddWithValue("@sequence", promotion.Sequence);
        command.Parameters.AddWithValue(
            "@schemaVersion",
            promotion.SchemaVersion);
        command.Parameters.AddWithValue("@itemCount", promotion.ItemCount);
        command.Parameters.AddWithValue(
            "@receiptCount",
            promotion.ReceiptCount);
        command.Parameters.AddWithValue(
            "@counts",
            JsonSerializer.Serialize(promotion.TableCounts));
        command.Parameters.AddWithValue(
            "@createdAt",
            promotion.CreatedAt.ToString(
                "O",
                CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<SqliteConnection> OpenReadWriteAsync(
        string path,
        CancellationToken ct)
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(ct);
        return connection;
    }
}
