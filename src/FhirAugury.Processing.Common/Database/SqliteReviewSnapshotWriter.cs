using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Common.Database;

public sealed record SqliteReviewSnapshotRequest(
    string ProcessorKind,
    string RunId,
    string OutputDirectory,
    int SchemaVersion,
    int ItemCount,
    int ReceiptCount,
    IReadOnlyDictionary<string, long> TableCounts,
    AuthoringSnapshotSanitizer Sanitizer,
    AuthoringSnapshotPublicationProof? PublicationProof = null);

public sealed class SqliteReviewSnapshotWriter(
    Func<SqliteConnection> openSourceConnection,
    AuthoringRunStore store)
{
    public async Task<AuthoringSnapshotDescriptor> WriteAsync(
        SqliteReviewSnapshotRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProcessorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentNullException.ThrowIfNull(request.Sanitizer);

        Directory.CreateDirectory(request.OutputDirectory);
        string fileToken = Guid.NewGuid().ToString("N");
        string safeProcessor = string.Concat(
            request.ProcessorKind.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        string finalPath = Path.GetFullPath(
            Path.Combine(request.OutputDirectory, $"{safeProcessor}-{fileToken}.db"));
        string tempPath = finalPath + ".tmp";

        AuthoringReviewSnapshotRecord record = await store.BeginSnapshotAsync(
            request.ProcessorKind,
            request.RunId,
            request.SchemaVersion,
            tempPath,
            finalPath,
            request.ItemCount,
            request.ReceiptCount,
            request.TableCounts,
            ct: ct,
            publicationProof: request.PublicationProof);

        try
        {
            IReadOnlyDictionary<string, long> actualCounts =
                await CreateSnapshotFileAsync(record, request, ct);
            await store.UpdateSnapshotTableCountsAsync(record.Id, actualCounts, ct);
            File.Move(tempPath, finalPath);
            string? promotionCleanupError = TryDeleteSnapshotArtifacts(tempPath);
            if (promotionCleanupError is not null)
            {
                string error = $"Snapshot staging cleanup failed after promotion: {promotionCleanupError}";
                await store.MarkSnapshotErrorAsync(record.Id, error, ct: CancellationToken.None);
                throw new IOException(error);
            }

            string checksum = await ComputeSha256Async(finalPath, ct);
            long sizeBytes = new FileInfo(finalPath).Length;
            await store.MarkSnapshotPromotedAsync(
                record.Id,
                checksum,
                sizeBytes,
                ct: ct);
            return await store.MarkSnapshotReadyAsync(record.Id, ct: ct);
        }
        catch (Exception ex)
        {
            if (!File.Exists(finalPath))
            {
                string? cleanupError = TryDeleteSnapshotArtifacts(tempPath);
                string error = cleanupError is null
                    ? ex.Message
                    : $"{ex.Message} Snapshot staging cleanup failed: {cleanupError}";
                await store.MarkSnapshotErrorAsync(record.Id, error, ct: CancellationToken.None);
            }
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, long>> CreateSnapshotFileAsync(
        AuthoringReviewSnapshotRecord record,
        SqliteReviewSnapshotRequest request,
        CancellationToken ct)
    {
        if (File.Exists(record.TempPath))
        {
            File.Delete(record.TempPath);
        }

        await using SqliteConnection source = openSourceConnection();
        await using SqliteConnection destination = new(new SqliteConnectionStringBuilder
        {
            DataSource = record.TempPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await destination.OpenAsync(ct);
        source.BackupDatabase(destination);

        await using (SqliteCommand provenance = destination.CreateCommand())
        {
            provenance.CommandText =
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
                    SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion,
                    ItemCount, ReceiptCount, TableCountsJson, CreatedAt)
                VALUES(
                    @snapshotId, @processorKind, @runId, @authoringEpoch, @sequence, @schemaVersion,
                    @itemCount, @receiptCount, @tableCountsJson, @createdAt);
                """;
            provenance.Parameters.AddWithValue("@snapshotId", record.Id);
            provenance.Parameters.AddWithValue("@processorKind", record.ProcessorKind);
            provenance.Parameters.AddWithValue("@runId", record.RunId);
            provenance.Parameters.AddWithValue("@authoringEpoch", record.AuthoringEpoch);
            provenance.Parameters.AddWithValue("@sequence", record.Sequence);
            provenance.Parameters.AddWithValue("@schemaVersion", record.SchemaVersion);
            provenance.Parameters.AddWithValue("@itemCount", record.ItemCount);
            provenance.Parameters.AddWithValue("@receiptCount", record.ReceiptCount);
            provenance.Parameters.AddWithValue("@tableCountsJson", JsonSerializer.Serialize(request.TableCounts));
            provenance.Parameters.AddWithValue(
                "@createdAt",
                record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            await provenance.ExecuteNonQueryAsync(ct);
        }

        await request.Sanitizer.SanitizeAsync(destination, ct);
        IReadOnlyDictionary<string, long> actualCounts =
            await ReadTableCountsAsync(destination, request.TableCounts.Keys, ct);
        await using (SqliteCommand updateProvenance = destination.CreateCommand())
        {
            updateProvenance.CommandText =
                "UPDATE authoring_snapshot_provenance SET TableCountsJson = @tableCountsJson";
            updateProvenance.Parameters.AddWithValue(
                "@tableCountsJson",
                JsonSerializer.Serialize(actualCounts));
            await updateProvenance.ExecuteNonQueryAsync(ct);
        }
        await using SqliteCommand integrity = destination.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        string result = (string?)await integrity.ExecuteScalarAsync(ct) ?? "unknown";
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Snapshot integrity check failed: {result}");
        }
        return actualCounts;
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken ct = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string? TryDeleteSnapshotArtifacts(string databasePath)
    {
        List<string> errors = [];
        foreach (string path in new[]
        {
            databasePath + "-journal",
            databasePath + "-wal",
            databasePath + "-shm",
            databasePath,
        })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadTableCountsAsync(
        SqliteConnection connection,
        IEnumerable<string> tableNames,
        CancellationToken ct)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (string tableName in tableNames.Distinct(StringComparer.Ordinal))
        {
            string quoted = $"\"{tableName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {quoted}";
            counts[tableName] = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        return counts;
    }
}
