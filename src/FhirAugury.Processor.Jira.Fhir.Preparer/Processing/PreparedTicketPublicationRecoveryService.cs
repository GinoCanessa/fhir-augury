using System.Collections.Concurrent;
using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketPublicationRecoveryService(
    PreparerDatabase database,
    AuthoringRunStore authoringStore,
    ILogger<PreparedTicketPublicationRecoveryService> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        RecoveryLocks = new(StringComparer.Ordinal);

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
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        SemaphoreSlim recoveryLock = RecoveryLocks.GetOrAdd(
            runId,
            static _ => new SemaphoreSlim(1, 1));
        bool lockTaken = false;
        try
        {
            await recoveryLock.WaitAsync(ct);
            lockTaken = true;
            return await RecoverCoreAsync(runId, ct);
        }
        catch (Exception ex)
        {
            string detail = ex.Message;
            try
            {
                await database
                    .RecordPublicationReconciliationRecoveryFailureAsync(
                        runId,
                        detail,
                        CancellationToken.None);
            }
            catch (Exception recordFailure)
            {
                logger.LogError(
                    recordFailure,
                    "Could not persist publication reconciliation recovery failure for {RunId}",
                    runId);
                detail =
                    $"{detail} Recovery failure persistence also failed: {recordFailure.Message}";
            }
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .PromotionRecoveryFailure,
                detail);
        }
        finally
        {
            if (lockTaken)
            {
                recoveryLock.Release();
            }
        }
    }

    private async Task<AuthoringSnapshotDescriptor> RecoverCoreAsync(
        string runId,
        CancellationToken ct)
    {
        PreparerDatabase.PublicationReconciliationRecoveryEvidence evidence =
            await database.GetRecoverablePublicationReconciliationAsync(
                runId,
                ct)
            ?? throw new InvalidOperationException(
                $"Reconciliation '{runId}' is not awaiting recovery.");
        PreparerDatabase.PublicationReconciliationPromotion promotion =
            evidence.Promotion;
        if (evidence.State ==
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending &&
            evidence.Candidate is null)
        {
            throw new InvalidOperationException(
                $"Pending reconciliation '{runId}' has no trusted candidate descriptor.");
        }
        if (evidence.Candidate is not null)
        {
            EnsureCandidateMatchesPromotion(
                evidence.Candidate,
                promotion);
        }

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
        EnsurePromotionMatchesSnapshot(promotion, snapshot);

        if (!File.Exists(promotion.FinalPath))
        {
            await ValidateSnapshotFileAsync(
                promotion,
                snapshot,
                promotion.TemporaryPath,
                "temporary",
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
                // Another process may have won the immutable move. Its exact
                // bytes are independently validated below.
            }
        }

        await ValidateSnapshotFileAsync(
            promotion,
            snapshot,
            promotion.FinalPath,
            "final",
            ct);
        if (snapshot.Status == AuthoringStatusValues.Snapshots.Creating)
        {
            await authoringStore.MarkSnapshotPromotedAsync(
                snapshot.Id,
                promotion.CandidateSha256,
                promotion.CandidateSizeBytes,
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
        return descriptor;
    }

    private static async Task ValidateSnapshotFileAsync(
        PreparerDatabase.PublicationReconciliationPromotion promotion,
        AuthoringReviewSnapshotRecord snapshot,
        string path,
        string evidenceName,
        CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The pending reconciliation {evidenceName} snapshot is missing.",
                path);
        }
        if (new FileInfo(path).Length != promotion.CandidateSizeBytes)
        {
            throw new InvalidOperationException(
                $"The {evidenceName} reconciliation snapshot size does not match the journal.");
        }
        SqliteReviewSnapshotValidationResult validation =
            await SqliteReviewSnapshotValidator.ValidateAsync(
                snapshot,
                path,
                ct: ct);
        if (!validation.IsValid ||
            validation.SizeBytes != promotion.CandidateSizeBytes ||
            !string.Equals(
                validation.ChecksumSha256,
                promotion.CandidateSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{evidenceName} reconciliation snapshot validation failed: {validation.Error ?? "digest or size mismatch"}");
        }
    }

    private static void EnsureCandidateMatchesPromotion(
        PreparedTicketPublicationCandidateSnapshot candidate,
        PreparerDatabase.PublicationReconciliationPromotion promotion)
    {
        if (!string.Equals(
                candidate.RunId,
                promotion.RunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.SnapshotId,
                promotion.SnapshotId,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.ProcessorKind,
                promotion.ProcessorKind,
                StringComparison.Ordinal) ||
            !PathsEqual(
                candidate.TemporaryPath,
                promotion.TemporaryPath) ||
            !PathsEqual(candidate.FinalPath, promotion.FinalPath) ||
            !string.Equals(
                candidate.Sha256,
                promotion.CandidateSha256,
                StringComparison.Ordinal) ||
            candidate.SizeBytes != promotion.CandidateSizeBytes ||
            candidate.SchemaVersion != promotion.SchemaVersion ||
            candidate.Sequence != promotion.Sequence ||
            candidate.AuthoringEpoch != promotion.AuthoringEpoch ||
            candidate.ItemCount != promotion.ItemCount ||
            candidate.ReceiptCount != promotion.ReceiptCount ||
            candidate.CreatedAt != promotion.CreatedAt ||
            !TableCountsEqual(candidate.TableCounts, promotion.TableCounts))
        {
            throw new InvalidOperationException(
                $"Reconciliation '{promotion.RunId}' candidate descriptor conflicts with its promotion journal.");
        }
    }

    private static void EnsurePromotionMatchesSnapshot(
        PreparerDatabase.PublicationReconciliationPromotion promotion,
        AuthoringReviewSnapshotRecord snapshot)
    {
        IReadOnlyDictionary<string, long> snapshotCounts =
            JsonSerializer.Deserialize<Dictionary<string, long>>(
                snapshot.TableCountsJson)
            ?? throw new InvalidOperationException(
                $"Snapshot '{snapshot.Id}' has empty table-count evidence.");
        bool matches =
            !string.IsNullOrWhiteSpace(promotion.SnapshotId) &&
            !string.IsNullOrWhiteSpace(promotion.RunId) &&
            string.Equals(
                promotion.ProcessorKind,
                "jira-fhir",
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(promotion.CandidateSha256) &&
            promotion.CandidateSha256.Length == 64 &&
            promotion.CandidateSha256.All(Uri.IsHexDigit) &&
            promotion.CandidateSizeBytes > 0 &&
            promotion.SchemaVersion ==
                PreparedTicketSnapshotSchemaV3.Version &&
            promotion.Sequence > 0 &&
            promotion.AuthoringEpoch >= 0 &&
            promotion.ItemCount >= 0 &&
            promotion.ReceiptCount >= 0 &&
            promotion.CreatedAt != default &&
            promotion.TableCounts.Keys
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(PreparedTicketSnapshotSchemaV3.CountedTables) &&
            string.Equals(
                promotion.SnapshotId,
                snapshot.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                promotion.ProcessorKind,
                snapshot.ProcessorKind,
                StringComparison.Ordinal) &&
            string.Equals(
                promotion.RunId,
                snapshot.RunId,
                StringComparison.Ordinal) &&
            promotion.AuthoringEpoch == snapshot.AuthoringEpoch &&
            promotion.Sequence == snapshot.Sequence &&
            promotion.SchemaVersion == snapshot.SchemaVersion &&
            promotion.ItemCount == snapshot.ItemCount &&
            promotion.ReceiptCount == snapshot.ReceiptCount &&
            promotion.CreatedAt == snapshot.CreatedAt &&
            PathsEqual(promotion.TemporaryPath, snapshot.TempPath) &&
            PathsEqual(promotion.FinalPath, snapshot.Path) &&
            string.Equals(
                promotion.CandidateSha256,
                snapshot.ChecksumSha256,
                StringComparison.Ordinal) &&
            promotion.CandidateSizeBytes == snapshot.SizeBytes &&
            TableCountsEqual(promotion.TableCounts, snapshotCounts) &&
            snapshot.Status is
                AuthoringStatusValues.Snapshots.Creating or
                AuthoringStatusValues.Snapshots.Promoted or
                AuthoringStatusValues.Snapshots.Ready;
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Pending snapshot '{promotion.SnapshotId}' conflicts with its promotion journal.");
        }
    }

    private static bool TableCountsEqual(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
        => left.Count == right.Count &&
           left.All(pair =>
               right.TryGetValue(pair.Key, out long value) &&
               value == pair.Value);

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
