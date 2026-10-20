using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;

namespace FhirAugury.Processing.Common.Database;

public sealed record SnapshotReconciliationResult(
    string SnapshotId,
    string Status,
    string? Error = null);

public sealed class SqliteReviewSnapshotReconciler(AuthoringRunStore store)
{
    public async Task<IReadOnlyList<SnapshotReconciliationResult>> ReconcileAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<AuthoringReviewSnapshotRecord> records = await store.GetSnapshotRecordsAsync(ct);
        List<SnapshotReconciliationResult> results = [];

        foreach (AuthoringReviewSnapshotRecord record in records)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(record.Status, AuthoringStatusValues.Snapshots.Error, StringComparison.Ordinal))
            {
                string? cleanupError = SqliteReviewSnapshotWriter.TryDeleteSnapshotArtifacts(record.TempPath);
                if (cleanupError is not null)
                {
                    string error = $"Snapshot staging cleanup failed: {cleanupError}";
                    await store.MarkSnapshotErrorAsync(record.Id, error, ct: ct);
                    results.Add(new SnapshotReconciliationResult(
                        record.Id,
                        AuthoringStatusValues.Snapshots.Error,
                        error));
                }
                continue;
            }

            if (!File.Exists(record.Path))
            {
                string? cleanupError = SqliteReviewSnapshotWriter.TryDeleteSnapshotArtifacts(record.TempPath);

                string error = string.Equals(
                    record.Status,
                    AuthoringStatusValues.Snapshots.Ready,
                    StringComparison.Ordinal)
                    ? "Ready snapshot file is missing."
                    : "Snapshot creation was interrupted before promotion.";
                if (cleanupError is not null)
                {
                    error += $" Snapshot staging cleanup failed: {cleanupError}";
                }
                await store.MarkSnapshotErrorAsync(record.Id, error, ct: ct);
                results.Add(new SnapshotReconciliationResult(
                    record.Id,
                    AuthoringStatusValues.Snapshots.Error,
                    error));
                continue;
            }

            SqliteReviewSnapshotValidationResult validation =
                await SqliteReviewSnapshotValidator.ValidateAsync(record, ct: ct);
            if (validation.Error is string validationError)
            {
                await store.MarkSnapshotErrorAsync(record.Id, validationError, ct: ct);
                results.Add(new SnapshotReconciliationResult(
                    record.Id,
                    AuthoringStatusValues.Snapshots.Error,
                    validationError));
                continue;
            }

            if (string.Equals(record.Status, AuthoringStatusValues.Snapshots.Creating, StringComparison.Ordinal))
            {
                await store.MarkSnapshotPromotedAsync(
                    record.Id,
                    validation.ChecksumSha256
                        ?? throw new InvalidOperationException("Validated snapshot has no checksum."),
                    validation.SizeBytes,
                    ct: ct);
            }

            if (!string.Equals(record.Status, AuthoringStatusValues.Snapshots.Ready, StringComparison.Ordinal))
            {
                await store.MarkSnapshotReadyAsync(record.Id, ct: ct);
            }

            string? promotionCleanupError =
                SqliteReviewSnapshotWriter.TryDeleteSnapshotArtifacts(record.TempPath);
            if (promotionCleanupError is not null)
            {
                string error = $"Snapshot staging cleanup failed after reconciliation: {promotionCleanupError}";
                await store.MarkSnapshotErrorAsync(record.Id, error, ct: ct);
                results.Add(new SnapshotReconciliationResult(
                    record.Id,
                    AuthoringStatusValues.Snapshots.Error,
                    error));
                continue;
            }
            results.Add(new SnapshotReconciliationResult(
                record.Id,
                AuthoringStatusValues.Snapshots.Ready));
        }

        return results;
    }
}
