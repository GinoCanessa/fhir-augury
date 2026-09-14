using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

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

            string? validationError = await ValidateSnapshotAsync(record, ct);
            if (validationError is not null)
            {
                await store.MarkSnapshotErrorAsync(record.Id, validationError, ct: ct);
                results.Add(new SnapshotReconciliationResult(
                    record.Id,
                    AuthoringStatusValues.Snapshots.Error,
                    validationError));
                continue;
            }

            string checksum = await SqliteReviewSnapshotWriter.ComputeSha256Async(record.Path, ct);
            if (record.ChecksumSha256 is not null &&
                !string.Equals(record.ChecksumSha256, checksum, StringComparison.Ordinal))
            {
                const string error = "Snapshot checksum mismatch during reconciliation.";
                await store.MarkSnapshotErrorAsync(record.Id, error, ct: ct);
                results.Add(new SnapshotReconciliationResult(
                    record.Id,
                    AuthoringStatusValues.Snapshots.Error,
                    error));
                continue;
            }

            if (string.Equals(record.Status, AuthoringStatusValues.Snapshots.Creating, StringComparison.Ordinal))
            {
                await store.MarkSnapshotPromotedAsync(
                    record.Id,
                    checksum,
                    new FileInfo(record.Path).Length,
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

    private static async Task<string?> ValidateSnapshotAsync(
        AuthoringReviewSnapshotRecord record,
        CancellationToken ct)
    {
        try
        {
            if (record.PublicationProofJson is not null &&
                JsonSerializer.Deserialize<AuthoringSnapshotPublicationProof>(
                    record.PublicationProofJson,
                    JsonSerializerOptions.Web) is null)
            {
                return "Snapshot publication proof is empty.";
            }

            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = record.Path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(ct);
            await using (SqliteCommand integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                string result = (string?)await integrity.ExecuteScalarAsync(ct) ?? "unknown";
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Snapshot integrity check failed during reconciliation: {result}";
                }
            }

            await using SqliteCommand provenance = connection.CreateCommand();
            provenance.CommandText =
                """
                SELECT SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion,
                       ItemCount, ReceiptCount, TableCountsJson
                FROM authoring_snapshot_provenance
                """;
            await using SqliteDataReader reader = await provenance.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return "Snapshot provenance is missing.";
            }

            Dictionary<string, long> expectedCounts =
                JsonSerializer.Deserialize<Dictionary<string, long>>(record.TableCountsJson) ?? [];
            Dictionary<string, long> actualCounts =
                JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(8)) ?? [];
            bool matches = string.Equals(reader.GetString(0), record.Id, StringComparison.Ordinal) &&
                string.Equals(reader.GetString(1), record.ProcessorKind, StringComparison.Ordinal) &&
                string.Equals(reader.GetString(2), record.RunId, StringComparison.Ordinal) &&
                reader.GetInt64(3) == record.AuthoringEpoch &&
                reader.GetInt64(4) == record.Sequence &&
                reader.GetInt32(5) == record.SchemaVersion &&
                reader.GetInt32(6) == record.ItemCount &&
                reader.GetInt32(7) == record.ReceiptCount &&
                DictionaryEquals(expectedCounts, actualCounts);
            if (!matches)
            {
                return "Snapshot provenance does not match the live snapshot record.";
            }
            if (await reader.ReadAsync(ct))
            {
                return "Snapshot provenance contains more than one row.";
            }

            return null;
        }
        catch (Exception ex) when (
            ex is SqliteException or JsonException or InvalidCastException or FormatException or OverflowException)
        {
            return $"Snapshot provenance validation failed: {ex.Message}";
        }
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        return left.All(pair =>
            right.TryGetValue(pair.Key, out long value) && value == pair.Value);
    }
}
