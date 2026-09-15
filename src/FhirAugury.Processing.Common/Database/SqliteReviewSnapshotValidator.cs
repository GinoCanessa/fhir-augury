using System.Globalization;
using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Common.Database;

public sealed record SqliteReviewSnapshotValidationResult(
    string? Error,
    string? ChecksumSha256 = null,
    long SizeBytes = 0)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Checks a file against a trusted store record without promoting, repairing,
/// deleting, or updating either the file or the store. A private byte copy can
/// be validated against the same record by supplying its path.
/// </summary>
public static class SqliteReviewSnapshotValidator
{
    public static async Task<SqliteReviewSnapshotValidationResult> ValidateAsync(
        AuthoringReviewSnapshotRecord record,
        string? snapshotPath = null,
        bool requireReady = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        string path = snapshotPath ?? record.Path;
        bool ready = string.Equals(
            record.Status,
            AuthoringStatusValues.Snapshots.Ready,
            StringComparison.Ordinal);
        if (requireReady && !ready)
        {
            return new("Snapshot record is not ready.");
        }
        if (record.SchemaVersion < 1 || record.AuthoringEpoch < 0 ||
            record.Sequence < 1 || record.ItemCount < 0 ||
            record.ReceiptCount < 0 || record.SizeBytes < 0)
        {
            return new("Snapshot record contains invalid identity or count coordinates.");
        }
        if ((ready || record.Status == AuthoringStatusValues.Snapshots.Promoted) &&
            (record.SizeBytes == 0 || !IsSha256(record.ChecksumSha256)))
        {
            return new("Promoted snapshot record has no valid digest or size.");
        }

        try
        {
            if (record.PublicationProofJson is not null &&
                JsonSerializer.Deserialize<AuthoringSnapshotPublicationProof>(
                    record.PublicationProofJson,
                    JsonSerializerOptions.Web) is null)
            {
                return new("Snapshot publication proof is empty.");
            }
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            {
                if (File.Exists(path + suffix))
                {
                    return new("Snapshot has unexpected SQLite sidecar files.");
                }
            }

            // Hold a read handle throughout validation so a local writer cannot
            // replace the bytes between the digest and provenance checks.
            await using FileStream file = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            long sizeBytes = file.Length;
            if (record.SizeBytes != 0 && sizeBytes != record.SizeBytes)
            {
                return new("Snapshot size does not match the trusted snapshot record.");
            }
            string checksum = await SqliteReviewSnapshotWriter.ComputeSha256Async(path, ct);
            if (record.ChecksumSha256 is not null &&
                !string.Equals(record.ChecksumSha256, checksum, StringComparison.Ordinal))
            {
                return new("Snapshot checksum does not match the trusted snapshot record.");
            }

            await using SqliteConnection connection = await OpenReadOnlyAsync(path, ct);
            await using (SqliteCommand integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                await using SqliteDataReader reader = await integrity.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) ||
                    !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase) ||
                    await reader.ReadAsync(ct))
                {
                    return new("Snapshot integrity check failed.");
                }
            }

            IReadOnlyDictionary<string, long> expectedCounts =
                ReadTableCounts(record.TableCountsJson);
            await using (SqliteCommand provenance = connection.CreateCommand())
            {
                provenance.CommandText =
                    """
                    SELECT SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion,
                           ItemCount, ReceiptCount, TableCountsJson, CreatedAt
                    FROM authoring_snapshot_provenance
                    """;
                await using SqliteDataReader reader = await provenance.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    return new("Snapshot provenance is missing.");
                }
                if (Enumerable.Range(0, reader.FieldCount).Any(reader.IsDBNull))
                {
                    return new("Snapshot provenance contains null identity coordinates.");
                }
                IReadOnlyDictionary<string, long> actualCounts =
                    ReadTableCounts(reader.GetString(8));
                bool matches = string.Equals(reader.GetString(0), record.Id, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(1), record.ProcessorKind, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(2), record.RunId, StringComparison.Ordinal) &&
                    reader.GetInt64(3) == record.AuthoringEpoch &&
                    reader.GetInt64(4) == record.Sequence &&
                    reader.GetInt32(5) == record.SchemaVersion &&
                    reader.GetInt32(6) == record.ItemCount &&
                    reader.GetInt32(7) == record.ReceiptCount &&
                    expectedCounts.Count == actualCounts.Count &&
                    expectedCounts.All(pair =>
                        actualCounts.TryGetValue(pair.Key, out long value) && value == pair.Value) &&
                    string.Equals(
                        reader.GetString(9),
                        record.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                        StringComparison.Ordinal);
                if (!matches)
                {
                    return new("Snapshot provenance does not match the trusted snapshot record.");
                }
                if (await reader.ReadAsync(ct))
                {
                    return new("Snapshot provenance contains more than one row.");
                }
            }

            foreach ((string table, long count) in expectedCounts)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                long actual = Convert.ToInt64(
                    await command.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture);
                if (actual != count)
                {
                    return new($"Snapshot table count does not match provenance for '{table}'.");
                }
            }
            return new(null, checksum, sizeBytes);
        }
        catch (Exception ex) when (
            ex is SqliteException or JsonException or InvalidCastException or
                FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            return new($"Snapshot validation failed: {ex.Message}");
        }
    }

    public static async Task<SqliteConnection> OpenReadOnlyAsync(
        string path,
        CancellationToken ct = default)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            // Backups can retain a WAL-mode header even though the immutable
            // file is self-contained. Without immutable=1 SQLite may create
            // WAL/SHM sidecars even for a read-only connection.
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA query_only = ON; PRAGMA trusted_schema = OFF;";
            await pragma.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static IReadOnlyDictionary<string, long> ReadTableCounts(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Snapshot table counts must be an object.");
        }
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) ||
                property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt64(out long count) || count < 0 ||
                !counts.TryAdd(property.Name, count))
            {
                throw new JsonException("Snapshot table counts contain an invalid or duplicate entry.");
            }
        }
        return counts;
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
