using FhirAugury.Tools.TicketMdToDb.Audit;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketMdToDb.Import;

public enum PromotionBoundary
{
    BeforePriorAuditMove,
    AfterPriorAuditMove,
    AfterDatabasePromotion,
    AfterAuditPromotion,
}

public sealed record DatabasePromotionResult(
    ImportAudit Audit,
    string PriorAuditClassification,
    string? DatabaseBackupPath,
    string? MatchingAuditBackupPath,
    string? PriorAuditEvidencePath);

public sealed class DatabasePromotionException : IOException
{
    public DatabasePromotionException(
        string message,
        Exception innerException,
        bool promotionBegan,
        IReadOnlyList<string> retainedPaths,
        IReadOnlyList<string> recoveryErrors)
        : base(message, innerException)
    {
        PromotionBegan = promotionBegan;
        RetainedPaths = retainedPaths;
        RecoveryErrors = recoveryErrors;
    }

    public bool PromotionBegan { get; }
    public IReadOnlyList<string> RetainedPaths { get; }
    public IReadOnlyList<string> RecoveryErrors { get; }
}

public sealed class AtomicDatabasePromoter(
    Action<PromotionBoundary>? faultInjector = null)
{
    private readonly Action<PromotionBoundary>? _faultInjector = faultInjector;

    public async Task<DatabasePromotionResult> PromoteAsync(
        GuardedImportPaths paths,
        ImportAudit audit,
        string candidateDigest,
        CancellationToken ct = default)
    {
        await using FileStream promotionLock =
            await AcquirePromotionLockAsync(paths.PromotionLockPath, ct);

        EnsureSnapshotUnchanged(
            "destination database",
            paths.DestinationSnapshot,
            ImportFileSnapshot.Capture(paths.DestinationDatabase));
        EnsureSnapshotUnchanged(
            "destination audit",
            paths.AuditSnapshot,
            ImportFileSnapshot.Capture(paths.AuditPath));

        bool replacement = File.Exists(paths.DestinationDatabase);
        string? priorDatabaseDigest = null;
        if (replacement)
        {
            priorDatabaseDigest = CheckpointExistingDatabase(paths.DestinationDatabase);
        }

        string classification = ClassifyPriorAudit(
            paths.AuditPath,
            replacement,
            priorDatabaseDigest);
        string? movedPriorAuditPath = classification switch
        {
            "absent" => null,
            "matching" => paths.MatchingAuditBackupPath,
            _ => paths.PriorAuditEvidencePath,
        };
        ImportPromotionOutcome promotion = new(
            Replacement: replacement,
            Completed: true,
            CandidateDatabasePath: paths.StagingDatabasePath,
            CandidateAuditPath: paths.CandidateAuditPath,
            PriorDatabaseSha256: priorDatabaseDigest,
            PriorAuditClassification: classification,
            DatabaseBackupPath: replacement ? paths.DatabaseBackupPath : null,
            MatchingAuditBackupPath: classification == "matching"
                ? paths.MatchingAuditBackupPath
                : null,
            PriorAuditEvidencePath: classification is not ("absent" or "matching")
                ? paths.PriorAuditEvidencePath
                : null,
            RecoveryCandidatePath: paths.RecoveryCandidatePath);
        ImportAudit completeAudit = audit with { Promotion = promotion };
        await ImportAuditWriter.WriteNewAuditAsync(
            paths.CandidateAuditPath,
            completeAudit,
            ct);

        bool priorAuditMoved = false;
        bool databasePromoted = false;
        bool auditPromoted = false;
        try
        {
            _faultInjector?.Invoke(PromotionBoundary.BeforePriorAuditMove);
            if (movedPriorAuditPath is not null)
            {
                File.Move(paths.AuditPath, movedPriorAuditPath, overwrite: false);
                priorAuditMoved = true;
            }
            _faultInjector?.Invoke(PromotionBoundary.AfterPriorAuditMove);

            if (replacement)
            {
                File.Replace(
                    paths.StagingDatabasePath,
                    paths.DestinationDatabase,
                    paths.DatabaseBackupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(
                    paths.StagingDatabasePath,
                    paths.DestinationDatabase,
                    overwrite: false);
            }
            databasePromoted = true;
            _faultInjector?.Invoke(PromotionBoundary.AfterDatabasePromotion);

            File.Move(
                paths.CandidateAuditPath,
                paths.AuditPath,
                overwrite: false);
            auditPromoted = true;
            _faultInjector?.Invoke(PromotionBoundary.AfterAuditPromotion);

            string promotedDigest = ImportFileHash.ComputeSha256(
                paths.DestinationDatabase);
            if (!string.Equals(
                    candidateDigest,
                    promotedDigest,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "Promoted database digest does not match the certified candidate.");
            }

            return new DatabasePromotionResult(
                completeAudit,
                classification,
                replacement ? paths.DatabaseBackupPath : null,
                classification == "matching"
                    ? paths.MatchingAuditBackupPath
                    : null,
                classification is not ("absent" or "matching")
                    ? paths.PriorAuditEvidencePath
                    : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            List<string> recoveryErrors = [];
            if (auditPromoted)
            {
                TryRecovery(
                    () => File.Move(
                        paths.AuditPath,
                        paths.CandidateAuditPath,
                        overwrite: false),
                    "retain promoted candidate audit",
                    recoveryErrors);
            }

            if (databasePromoted)
            {
                if (replacement)
                {
                    TryRecovery(
                        () => File.Replace(
                            paths.DatabaseBackupPath,
                            paths.DestinationDatabase,
                            paths.RecoveryCandidatePath,
                            ignoreMetadataErrors: true),
                        "restore prior database",
                        recoveryErrors);
                }
                else
                {
                    TryRecovery(
                        () => File.Move(
                            paths.DestinationDatabase,
                            paths.RecoveryCandidatePath,
                            overwrite: false),
                        "remove failed fresh destination",
                        recoveryErrors);
                }
            }

            if (priorAuditMoved && movedPriorAuditPath is not null)
            {
                TryRecovery(
                    () => File.Move(
                        movedPriorAuditPath,
                        paths.AuditPath,
                        overwrite: false),
                    "restore prior audit",
                    recoveryErrors);
            }

            string[] retained = new[]
                {
                    paths.StagingDatabasePath,
                    paths.CandidateAuditPath,
                    paths.DatabaseBackupPath,
                    paths.MatchingAuditBackupPath,
                    paths.PriorAuditEvidencePath,
                    paths.RecoveryCandidatePath,
                }
                .Where(File.Exists)
                .ToArray();
            throw new DatabasePromotionException(
                $"Database promotion failed at a recoverable boundary: {ex.Message}",
                ex,
                priorAuditMoved || databasePromoted || auditPromoted,
                retained,
                recoveryErrors);
        }
    }

    private static async Task<FileStream> AcquirePromotionLockAsync(
        string path,
        CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Promotion lock has no parent: {path}");
        Directory.CreateDirectory(directory);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                FileStream stream = new(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                stream.SetLength(0);
                await stream.FlushAsync(ct);
                return stream;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            }
        }
    }

    private static void EnsureSnapshotUnchanged(
        string name,
        ImportFileSnapshot expected,
        ImportFileSnapshot actual)
    {
        if (!DestinationGuard.SnapshotsEqual(expected, actual))
        {
            throw new IOException(
                $"The {name} changed after preflight; refusing sequential replacement.");
        }
    }

    private static string CheckpointExistingDatabase(string path)
    {
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using (SqliteConnection connection = new(connectionString))
        {
            connection.Open();
            using SqliteCommand checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using SqliteDataReader reader = checkpoint.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != 0)
            {
                throw new IOException(
                    "Existing destination could not be checkpointed while quiescent.");
            }
            reader.Close();

            using SqliteCommand journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            string mode = journal.ExecuteScalar()?.ToString() ?? string.Empty;
            if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Existing destination could not leave WAL mode: {mode}");
            }
        }

        DeleteClosedSidecar($"{path}-wal", requireEmpty: true);
        DeleteClosedSidecar($"{path}-shm", requireEmpty: false);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
        return ImportFileHash.ComputeSha256(path);
    }

    private static void DeleteClosedSidecar(string path, bool requireEmpty)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (requireEmpty && new FileInfo(path).Length > 0)
        {
            throw new IOException($"Checkpoint left a non-empty SQLite sidecar: {path}");
        }
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
        File.Delete(path);
    }

    private static string ClassifyPriorAudit(
        string auditPath,
        bool replacement,
        string? priorDatabaseDigest)
    {
        if (!File.Exists(auditPath))
        {
            return "absent";
        }
        if (!ImportAuditWriter.TryReadAudit(auditPath, out ImportAudit? prior)
            || prior is null)
        {
            return "malformed";
        }
        if (prior.Database is null)
        {
            return "dry-run";
        }
        if (!replacement || priorDatabaseDigest is null)
        {
            return "orphaned-database-audit";
        }
        bool statusCanBeReady = prior.Run.Status is "ready" or "degraded-accepted";
        return statusCanBeReady
               && string.Equals(
                   prior.Database.Sha256,
                   priorDatabaseDigest,
                   StringComparison.Ordinal)
            ? "matching"
            : "digest-mismatch";
    }

    private static void TryRecovery(
        Action action,
        string description,
        ICollection<string> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            errors.Add($"{description}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
