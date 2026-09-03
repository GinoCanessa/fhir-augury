using System.Security.Cryptography;
using FhirAugury.Tools.TicketMdToDb.Compilation;

namespace FhirAugury.Tools.TicketMdToDb.Import;

public sealed record ImportFileSnapshot(bool Exists, long Length, string? Sha256)
{
    public static ImportFileSnapshot Capture(string path)
    {
        if (!File.Exists(path))
        {
            return new ImportFileSnapshot(false, 0, null);
        }

        FileInfo info = new(path);
        return new ImportFileSnapshot(true, info.Length, ImportFileHash.ComputeSha256(path));
    }
}

public sealed record GuardedImportPaths(
    string InputRoot,
    IReadOnlyList<string> SourceFiles,
    string DestinationDatabase,
    string DestinationWal,
    string DestinationShm,
    string? OverridesPath,
    string AuditPath,
    string OverrideTemplatePath,
    string PromotionLockPath,
    string StagingDatabasePath,
    string StagingWalPath,
    string StagingShmPath,
    string CandidateAuditPath,
    string FailedAuditPath,
    string DatabaseBackupPath,
    string DatabaseBackupWalPath,
    string DatabaseBackupShmPath,
    string MatchingAuditBackupPath,
    string PriorAuditEvidencePath,
    string RecoveryCandidatePath,
    ImportFileSnapshot DestinationSnapshot,
    ImportFileSnapshot AuditSnapshot);

public sealed class UnsafeImportTopologyException(string message) : InvalidOperationException(message);

public static class DestinationGuard
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static GuardedImportPaths Validate(
        CliOptions options,
        CompilationResult compilation,
        string runId)
    {
        ValidateRunId(runId);

        string inputRoot = Canonicalize(options.InputRoot);
        string destination = Canonicalize(options.DatabasePath);
        string audit = Canonicalize(options.AuditPath);
        string template = Canonicalize(options.OverrideTemplatePath);
        string? overrides = options.OverridesPath is null
            ? null
            : Canonicalize(options.OverridesPath);
        string[] sourceFiles = compilation.Manifest.Files
            .Select(file => Canonicalize(Path.Combine(inputRoot, file.RelativePath)))
            .OrderBy(path => path, PathComparer)
            .ToArray();

        string destinationWal = Canonicalize($"{destination}-wal");
        string destinationShm = Canonicalize($"{destination}-shm");
        string promotionLock = Canonicalize($"{destination}.promotion.lock");
        string staging = Canonicalize($"{destination}.{runId}.staging");
        string stagingWal = Canonicalize($"{staging}-wal");
        string stagingShm = Canonicalize($"{staging}-shm");
        string candidateAudit = Canonicalize($"{audit}.{runId}.candidate");
        string failedAudit = Canonicalize($"{audit}.{runId}.failed");
        string databaseBackup = Canonicalize($"{destination}.{runId}.backup");
        string databaseBackupWal = Canonicalize($"{databaseBackup}-wal");
        string databaseBackupShm = Canonicalize($"{databaseBackup}-shm");
        string matchingAuditBackup = Canonicalize($"{audit}.{runId}.backup");
        string priorAuditEvidence = Canonicalize($"{audit}.{runId}.prior");
        string recoveryCandidate = Canonicalize(
            $"{destination}.{runId}.recovery-candidate");
        string recoveryCandidateWal = Canonicalize($"{recoveryCandidate}-wal");
        string recoveryCandidateShm = Canonicalize($"{recoveryCandidate}-shm");

        string[] writablePaths =
        [
            destination,
            destinationWal,
            destinationShm,
            audit,
            template,
            promotionLock,
            staging,
            stagingWal,
            stagingShm,
            candidateAudit,
            failedAudit,
            databaseBackup,
            databaseBackupWal,
            databaseBackupShm,
            matchingAuditBackup,
            priorAuditEvidence,
            recoveryCandidate,
            recoveryCandidateWal,
            recoveryCandidateShm,
        ];

        EnsureDistinct(writablePaths);
        foreach (string writable in writablePaths)
        {
            if (!options.DryRun && IsAtOrBelow(writable, inputRoot))
            {
                throw new UnsafeImportTopologyException(
                    $"Writable path is at or beneath the Markdown source tree: {writable}");
            }

            if (overrides is not null && PathComparer.Equals(writable, overrides))
            {
                throw new UnsafeImportTopologyException(
                    $"Writable path collides with the overrides input: {writable}");
            }

            if (sourceFiles.Any(source => PathComparer.Equals(source, writable)))
            {
                throw new UnsafeImportTopologyException(
                    $"Writable path collides with source Markdown: {writable}");
            }
        }

        EnsureSameVolume(
            destination,
            audit,
            staging,
            candidateAudit,
            failedAudit,
            databaseBackup,
            matchingAuditBackup,
            priorAuditEvidence,
            recoveryCandidate);

        if (!options.DryRun)
        {
            if (File.Exists(destination) && !options.ReplaceExisting)
            {
                throw new UnsafeImportTopologyException(
                    $"Destination already exists; use --replace-existing to replace it: {destination}");
            }

            if (!File.Exists(destination)
                && (File.Exists(destinationWal) || File.Exists(destinationShm)))
            {
                throw new UnsafeImportTopologyException(
                    "Destination SQLite sidecars exist without a destination database.");
            }

            foreach (string residue in new[]
                     {
                         staging,
                         stagingWal,
                         stagingShm,
                         candidateAudit,
                         failedAudit,
                         databaseBackup,
                         databaseBackupWal,
                         databaseBackupShm,
                         matchingAuditBackup,
                         priorAuditEvidence,
                         recoveryCandidate,
                         recoveryCandidateWal,
                         recoveryCandidateShm,
                     })
            {
                if (File.Exists(residue) || Directory.Exists(residue))
                {
                    throw new UnsafeImportTopologyException(
                        $"Run-specific path already exists and will not be reused: {residue}");
                }
            }
        }
        else if (File.Exists(destination) && File.Exists(audit))
        {
            throw new UnsafeImportTopologyException(
                "Dry run will not overwrite an existing destination's audit; choose a separate --audit path.");
        }

        return new GuardedImportPaths(
            inputRoot,
            sourceFiles,
            destination,
            destinationWal,
            destinationShm,
            overrides,
            audit,
            template,
            promotionLock,
            staging,
            stagingWal,
            stagingShm,
            candidateAudit,
            failedAudit,
            databaseBackup,
            databaseBackupWal,
            databaseBackupShm,
            matchingAuditBackup,
            priorAuditEvidence,
            recoveryCandidate,
            ImportFileSnapshot.Capture(destination),
            ImportFileSnapshot.Capture(audit));
    }

    internal static string Canonicalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new UnsafeImportTopologyException($"Path has no volume root: {fullPath}");
        string relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return root;
        }

        string current = root;
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            string candidate = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            if (info is not null)
            {
                try
                {
                    FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                    current = target?.FullName ?? candidate;
                    continue;
                }
                catch (IOException)
                {
                    // A concurrently removed path is handled as a normal non-existing component.
                }
            }

            current = candidate;
        }

        return Path.GetFullPath(current);
    }

    internal static bool SnapshotsEqual(ImportFileSnapshot expected, ImportFileSnapshot actual) =>
        expected.Exists == actual.Exists
        && expected.Length == actual.Length
        && string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal);

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || runId.Contains(Path.DirectorySeparatorChar)
            || runId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Run ID is not safe for use in a file name.", nameof(runId));
        }
    }

    private static void EnsureDistinct(IEnumerable<string> paths)
    {
        HashSet<string> seen = new(PathComparer);
        foreach (string path in paths)
        {
            if (!seen.Add(path))
            {
                throw new UnsafeImportTopologyException(
                    $"Two writable import paths resolve to the same location: {path}");
            }
        }
    }

    private static bool IsAtOrBelow(string path, string root)
    {
        if (PathComparer.Equals(path, root))
        {
            return true;
        }

        string rootedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(
            rootedPrefix,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static void EnsureSameVolume(string first, params string[] remaining)
    {
        string firstRoot = Path.GetPathRoot(first) ?? string.Empty;
        foreach (string path in remaining)
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            if (!PathComparer.Equals(firstRoot, root))
            {
                throw new UnsafeImportTopologyException(
                    $"Promotion artifacts must be on the destination volume: {path}");
            }
        }
    }
}

internal static class ImportFileHash
{
    public static string ComputeSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
