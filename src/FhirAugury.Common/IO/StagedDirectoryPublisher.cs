using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

[assembly: InternalsVisibleTo("FhirAugury.Common.Tests")]

namespace FhirAugury.Common.IO;

public enum StagedDirectoryPublicationMode
{
    Legacy,
    Snapshot,
}

public sealed record StagedDirectoryVersion(
    string Owner,
    StagedDirectoryPublicationMode Mode,
    string BuildIdentity,
    string? ProcessorKind = null,
    long? Sequence = null,
    string? SnapshotId = null)
{
    public static StagedDirectoryVersion Legacy(
        string owner,
        string buildIdentity)
        => new(owner, StagedDirectoryPublicationMode.Legacy, buildIdentity);

    public static StagedDirectoryVersion Snapshot(
        string owner,
        string processorKind,
        long sequence,
        string snapshotId,
        string buildIdentity)
        => new(
            owner,
            StagedDirectoryPublicationMode.Snapshot,
            buildIdentity,
            processorKind,
            sequence,
            snapshotId);
}

public enum StagedDirectoryPublishOutcome
{
    Promoted,
    Idempotent,
}

public sealed record StagedDirectoryPublishResult(
    StagedDirectoryPublishOutcome Outcome,
    StagedDirectoryVersion Version);

public sealed record StagedDirectoryPublishOptions(
    bool Force = false,
    Func<string, bool, CancellationToken, Task>? ValidateLegacyReplacementAsync = null);

public sealed class StaleDirectoryPublicationException(string message)
    : InvalidOperationException(message);

public sealed class ImmutableFileSnapshot : IAsyncDisposable
{
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    private readonly string _directory;
    private readonly Action<string>? _cleanupWarning;
    private bool _disposed;

    private ImmutableFileSnapshot(
        string directory,
        string path,
        string sourceFileName,
        long sizeBytes,
        string sha256,
        Action<string>? cleanupWarning)
    {
        _directory = directory;
        _cleanupWarning = cleanupWarning;
        Path = path;
        SourceFileName = sourceFileName;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    public string Path { get; }
    public string SourceFileName { get; }
    public long SizeBytes { get; }
    public string Sha256 { get; }

    public static async Task<ImmutableFileSnapshot> CreateAsync(
        string sourcePath,
        CancellationToken ct = default,
        Action<string>? cleanupWarning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string source = System.IO.Path.GetFullPath(sourcePath);
        string sourceFileName = System.IO.Path.GetFileName(source);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Snapshot database file was not found.", source);
        }

        RejectSidecars(source);

        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"fhir-augury-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string destination = System.IO.Path.Combine(directory, sourceFileName);

        try
        {
            long sourceLength;
            DateTime sourceWriteTime;
            await using (FileStream input = new(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sourceLength = input.Length;
                sourceWriteTime = File.GetLastWriteTimeUtc(source);
                RejectSidecars(source);

                await using FileStream output = new(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);

                if (input.Length != sourceLength ||
                    File.GetLastWriteTimeUtc(source) != sourceWriteTime)
                {
                    throw new IOException(
                        $"Snapshot source '{source}' changed while it was being copied.");
                }
                RejectSidecars(source);
            }

            FileInfo copyInfo = new(destination);
            if (copyInfo.Length != sourceLength)
            {
                throw new IOException(
                    $"Snapshot copy length mismatch: source={sourceLength}, copy={copyInfo.Length}.");
            }

            await using FileStream hashInput = new(
                destination,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string sha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(hashInput, ct).ConfigureAwait(false))
                .ToLowerInvariant();
            File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
            return new ImmutableFileSnapshot(
                directory,
                destination,
                sourceFileName,
                sourceLength,
                sha256,
                cleanupWarning);
        }
        catch
        {
            TryDeletePrivateDirectory(directory);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await DeleteDirectoryWithRetryAsync(_directory, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                _cleanupWarning?.Invoke(
                    $"Deferred cleanup of private snapshot directory '{_directory}': {ex.Message}");
            }
            catch (Exception warningError) when (
                warningError is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RejectSidecars(string source)
    {
        string[] sidecars = SidecarSuffixes
            .Select(suffix => source + suffix)
            .Where(File.Exists)
            .ToArray();
        if (sidecars.Length > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot source has SQLite sidecar files and is not immutable: " +
                string.Join(", ", sidecars.Select(System.IO.Path.GetFileName)));
        }
    }

    private static void TryDeletePrivateDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                ClearReadOnlyAttributes(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string path,
        CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException &&
                attempt < 20)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (string file in Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (string directory in Directory.EnumerateDirectories(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Directory);
        }
        File.SetAttributes(root, FileAttributes.Directory);
    }
}

internal enum StagedDirectoryPublishCheckpoint
{
    BeforeBackupCreated,
    AfterBackupCreated,
    AfterTargetPromoted,
    BeforePostCommitCleanup,
}

internal sealed record StagedDirectoryPublisherTestHooks(
    Func<StagedDirectoryPublishCheckpoint, string, CancellationToken, Task> OnCheckpointAsync);

internal sealed class StagedDirectorySimulatedCrashException(string message)
    : Exception(message);

public static class StagedDirectoryPublisher
{
    public const string VersionFileName = ".fhir-augury-publication.json";

    private const string ReadyFileName = ".fhir-augury-publication-ready.json";
    private const int StateFormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Task<StagedDirectoryPublishResult> PublishAsync(
        string outputDirectory,
        StagedDirectoryVersion incomingVersion,
        Func<string, CancellationToken, Task> renderAsync,
        Func<string, CancellationToken, Task> validateAsync,
        StagedDirectoryPublishOptions? options = null,
        CancellationToken ct = default)
        => PublishAsync(
            outputDirectory,
            incomingVersion,
            renderAsync,
            validateAsync,
            options,
            hooks: null,
            ct);

    internal static async Task<StagedDirectoryPublishResult> PublishAsync(
        string outputDirectory,
        StagedDirectoryVersion incomingVersion,
        Func<string, CancellationToken, Task> renderAsync,
        Func<string, CancellationToken, Task> validateAsync,
        StagedDirectoryPublishOptions? options,
        StagedDirectoryPublisherTestHooks? hooks,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(incomingVersion);
        ArgumentNullException.ThrowIfNull(renderAsync);
        ArgumentNullException.ThrowIfNull(validateAsync);
        ValidateVersion(incomingVersion);
        options ??= new StagedDirectoryPublishOptions();

        PublicationPaths paths = PublicationPaths.Create(outputDirectory);
        Directory.CreateDirectory(paths.Parent);

        string token = Guid.NewGuid().ToString("N");
        string staging = System.IO.Path.Combine(
            paths.Parent,
            $".{paths.Name}.staging-{token}");
        string leasePath = staging + ".lease";
        bool committed = false;
        bool preserveForRecovery = false;

        Directory.CreateDirectory(staging);
        await using FileStream lease = new(
            leasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);

        try
        {
            await renderAsync(staging, ct).ConfigureAwait(false);
            await WriteJsonAtomicAsync(
                System.IO.Path.Combine(staging, VersionFileName),
                incomingVersion,
                ct).ConfigureAwait(false);
            await validateAsync(staging, ct).ConfigureAwait(false);
            await WriteJsonAtomicAsync(
                System.IO.Path.Combine(staging, ReadyFileName),
                new ReadyPublication(incomingVersion, options.Force),
                ct).ConfigureAwait(false);

            await using FileStream publishLock =
                await AcquireLockAsync(paths.LockPath, ct).ConfigureAwait(false);

            PublicationState state = await ReadStateAsync(paths.StatePath, ct)
                .ConfigureAwait(false);
            state = await RecoverSwapAsync(paths, state, hooks, ct)
                .ConfigureAwait(false);

            StagedDirectoryVersion? current =
                await ReadDirectoryVersionAsync(paths.Target, ct).ConfigureAwait(false);
            state = await ReconcileCurrentStateAsync(
                paths,
                state,
                current,
                options.Force,
                ct).ConfigureAwait(false);

            state = await RecoverOrphansAsync(paths, staging, state, ct)
                .ConfigureAwait(false);

            current = Directory.Exists(paths.Target)
                ? await ReadDirectoryVersionAsync(paths.Target, ct).ConfigureAwait(false)
                : null;
            state = await ReconcileCurrentStateAsync(
                paths,
                state,
                current,
                options.Force,
                ct).ConfigureAwait(false);

            await ValidateOwnershipAndOrderAsync(
                paths.Target,
                state,
                current,
                incomingVersion,
                options,
                ct).ConfigureAwait(false);

            if (current is not null &&
                VersionsEqual(current, incomingVersion))
            {
                return new StagedDirectoryPublishResult(
                    StagedDirectoryPublishOutcome.Idempotent,
                    current);
            }

            state = await PromoteAsync(
                paths,
                staging,
                incomingVersion,
                options.Force,
                state,
                hooks,
                ct).ConfigureAwait(false);
            committed = true;
            return new StagedDirectoryPublishResult(
                StagedDirectoryPublishOutcome.Promoted,
                state.Current!);
        }
        catch (StagedDirectorySimulatedCrashException)
        {
            preserveForRecovery = true;
            throw;
        }
        catch
        {
            if (!committed)
            {
                await DeleteDirectoryWithRetryAsync(staging, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            TryDeleteFile(leasePath);
            if (!preserveForRecovery)
            {
                await DeleteDirectoryBestEffortAsync(staging).ConfigureAwait(false);
            }
        }
    }

    public static string GetRendererAssetsVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.ManifestModule.ModuleVersionId.ToString("N");
    }

    private static async Task ValidateOwnershipAndOrderAsync(
        string target,
        PublicationState state,
        StagedDirectoryVersion? current,
        StagedDirectoryVersion incoming,
        StagedDirectoryPublishOptions options,
        CancellationToken ct)
    {
        bool targetExists = Directory.Exists(target);
        if (targetExists && current is null && !options.Force)
        {
            if (incoming.Mode == StagedDirectoryPublicationMode.Legacy &&
                options.ValidateLegacyReplacementAsync is not null)
            {
                try
                {
                    await options.ValidateLegacyReplacementAsync(
                        target,
                        false,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"Output directory '{target}' is not owned by this publisher. " +
                        "Pass --force to take explicit ownership.",
                        ex);
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Output directory '{target}' is not owned by this publisher. " +
                    "Pass --force to take explicit ownership.");
            }
        }

        if (current is not null)
        {
            if ((!string.Equals(current.Owner, incoming.Owner, StringComparison.Ordinal) ||
                 current.Mode != incoming.Mode) &&
                !options.Force)
            {
                throw new InvalidOperationException(
                    $"Output directory '{target}' is owned by '{current.Owner}' in " +
                    $"{current.Mode.ToString().ToLowerInvariant()} mode. Pass --force " +
                    $"to replace it with '{incoming.Owner}' in " +
                    $"{incoming.Mode.ToString().ToLowerInvariant()} mode.");
            }

            if (incoming.Mode == StagedDirectoryPublicationMode.Legacy &&
                current.Mode == StagedDirectoryPublicationMode.Legacy &&
                string.Equals(current.Owner, incoming.Owner, StringComparison.Ordinal))
            {
                if (options.ValidateLegacyReplacementAsync is not null)
                {
                    await options.ValidateLegacyReplacementAsync(
                        target,
                        options.Force,
                        ct).ConfigureAwait(false);
                }
                else if (!options.Force)
                {
                    throw new InvalidOperationException(
                        $"Output directory '{target}' already contains a legacy publication. " +
                        "Pass --force to replace it.");
                }
            }
        }

        if (incoming.Mode != StagedDirectoryPublicationMode.Snapshot)
        {
            return;
        }

        string processor = incoming.ProcessorKind!;
        long sequence = incoming.Sequence!.Value;
        if (state.SnapshotHighWatermarks.TryGetValue(
                processor,
                out SnapshotHighWatermark? highWatermark))
        {
            if (sequence < highWatermark.Sequence)
            {
                throw new StaleDirectoryPublicationException(
                    $"Snapshot '{incoming.SnapshotId}' sequence {sequence} is older than " +
                    $"the preserved sequence {highWatermark.Sequence} for processor '{processor}'.");
            }
            if (sequence == highWatermark.Sequence &&
                !string.Equals(
                    incoming.SnapshotId,
                    highWatermark.SnapshotId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snapshot sequence {sequence} for processor '{processor}' is already " +
                    $"preserved for snapshot '{highWatermark.SnapshotId ?? "unknown"}'.");
            }
            if (sequence > highWatermark.Sequence &&
                string.Equals(
                    incoming.SnapshotId,
                    highWatermark.SnapshotId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snapshot '{incoming.SnapshotId}' has inconsistent sequences " +
                    $"{highWatermark.Sequence} and {sequence}.");
            }
        }

        if (current?.Mode != StagedDirectoryPublicationMode.Snapshot ||
            !string.Equals(current.ProcessorKind, processor, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(current.SnapshotId, incoming.SnapshotId, StringComparison.Ordinal))
        {
            if (current.Sequence != incoming.Sequence)
            {
                throw new InvalidOperationException(
                    $"Snapshot '{incoming.SnapshotId}' has inconsistent sequences " +
                    $"{current.Sequence} and {incoming.Sequence}.");
            }
            return;
        }

        if (sequence < current.Sequence)
        {
            throw new StaleDirectoryPublicationException(
                $"Snapshot '{incoming.SnapshotId}' sequence {sequence} is older than " +
                $"the currently published snapshot '{current.SnapshotId}' sequence " +
                $"{current.Sequence} for processor '{processor}'.");
        }
        if (sequence == current.Sequence)
        {
            throw new InvalidOperationException(
                $"Snapshot sequence {sequence} for processor '{processor}' is already " +
                "published with a different snapshot ID.");
        }
    }

    private static async Task<PublicationState> PromoteAsync(
        PublicationPaths paths,
        string staging,
        StagedDirectoryVersion incoming,
        bool force,
        PublicationState state,
        StagedDirectoryPublisherTestHooks? hooks,
        CancellationToken ct)
    {
        string token = Guid.NewGuid().ToString("N");
        string backup = System.IO.Path.Combine(
            paths.Parent,
            $".{paths.Name}.backup-{token}");
        SwapState swap = new(
            paths.Target,
            staging,
            backup,
            incoming,
            force,
            SwapPhase.Prepared);
        await WriteJsonAtomicAsync(paths.SwapPath, swap, ct).ConfigureAwait(false);

        bool backupCreated = false;
        bool targetPromoted = false;
        try
        {
            if (Directory.Exists(paths.Target))
            {
                await InvokeHookAsync(
                    hooks,
                    StagedDirectoryPublishCheckpoint.BeforeBackupCreated,
                    paths.Target,
                    ct).ConfigureAwait(false);
                Directory.Move(paths.Target, backup);
                backupCreated = true;
            }
            swap = swap with { Phase = SwapPhase.BackupCreated };
            await WriteJsonAtomicAsync(paths.SwapPath, swap, ct).ConfigureAwait(false);
            await InvokeHookAsync(
                hooks,
                StagedDirectoryPublishCheckpoint.AfterBackupCreated,
                backup,
                ct).ConfigureAwait(false);

            Directory.Move(staging, paths.Target);
            targetPromoted = true;
            swap = swap with { Phase = SwapPhase.TargetPromoted };
            await WriteJsonAtomicAsync(paths.SwapPath, swap, ct).ConfigureAwait(false);
            await InvokeHookAsync(
                hooks,
                StagedDirectoryPublishCheckpoint.AfterTargetPromoted,
                paths.Target,
                ct).ConfigureAwait(false);

            state = WithCurrent(state, incoming);
            await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
        }
        catch (StagedDirectorySimulatedCrashException)
        {
            throw;
        }
        catch
        {
            await RollBackAsync(
                paths.Target,
                staging,
                backup,
                backupCreated,
                targetPromoted).ConfigureAwait(false);
            TryDeleteFile(paths.SwapPath);
            throw;
        }

        try
        {
            await InvokeHookAsync(
                hooks,
                StagedDirectoryPublishCheckpoint.BeforePostCommitCleanup,
                backup,
                CancellationToken.None).ConfigureAwait(false);
            await DeleteDirectoryWithRetryAsync(backup, CancellationToken.None)
                .ConfigureAwait(false);
            TryDeleteFile(paths.SwapPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // The target and durable state are already committed. Leave the
            // swap record so the next publisher invocation can finish cleanup.
        }

        return state;
    }

    private static async Task<PublicationState> RecoverSwapAsync(
        PublicationPaths paths,
        PublicationState state,
        StagedDirectoryPublisherTestHooks? hooks,
        CancellationToken ct)
    {
        SwapState? swap = await ReadJsonAsync<SwapState>(paths.SwapPath, ct)
            .ConfigureAwait(false);
        if (swap is null)
        {
            return state;
        }
        ValidateSwapPaths(paths, swap);

        StagedDirectoryVersion? targetVersion =
            await ReadDirectoryVersionAsync(paths.Target, ct).ConfigureAwait(false);
        if (targetVersion is not null &&
            VersionsEqual(targetVersion, swap.Incoming))
        {
            state = WithCurrent(state, swap.Incoming);
            await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
            await CompleteRecoveredCleanupAsync(paths, swap).ConfigureAwait(false);
            return state;
        }

        if (Directory.Exists(swap.Staging))
        {
            ReadyPublication? ready = await ReadJsonAsync<ReadyPublication>(
                System.IO.Path.Combine(swap.Staging, ReadyFileName),
                ct).ConfigureAwait(false);
            StagedDirectoryVersion? stagedVersion =
                await ReadDirectoryVersionAsync(swap.Staging, ct).ConfigureAwait(false);
            if (ready is null ||
                stagedVersion is null ||
                !VersionsEqual(ready.Version, swap.Incoming) ||
                !VersionsEqual(stagedVersion, swap.Incoming))
            {
                throw new InvalidOperationException(
                    $"Interrupted publication staging directory '{swap.Staging}' is not valid.");
            }

            if (Directory.Exists(paths.Target) && !Directory.Exists(swap.Backup))
            {
                Directory.Move(paths.Target, swap.Backup);
            }
            if (!Directory.Exists(paths.Target))
            {
                Directory.Move(swap.Staging, paths.Target);
            }

            state = WithCurrent(state, swap.Incoming);
            await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
            await CompleteRecoveredCleanupAsync(paths, swap).ConfigureAwait(false);
            return state;
        }

        if (!Directory.Exists(paths.Target) && Directory.Exists(swap.Backup))
        {
            Directory.Move(swap.Backup, paths.Target);
            StagedDirectoryVersion? restored =
                await ReadDirectoryVersionAsync(paths.Target, ct).ConfigureAwait(false);
            state = restored is null
                ? state with { Current = null }
                : WithCurrent(state, restored);
            await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
            TryDeleteFile(paths.SwapPath);
            return state;
        }

        if (targetVersion is not null)
        {
            state = WithCurrent(state, targetVersion);
            await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
        }
        TryDeleteFile(paths.SwapPath);
        return state;
    }

    private static async Task CompleteRecoveredCleanupAsync(
        PublicationPaths paths,
        SwapState swap)
    {
        try
        {
            await DeleteDirectoryWithRetryAsync(swap.Backup, CancellationToken.None)
                .ConfigureAwait(false);
            await DeleteDirectoryWithRetryAsync(swap.Staging, CancellationToken.None)
                .ConfigureAwait(false);
            TryDeleteFile(paths.SwapPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<PublicationState> RecoverOrphansAsync(
        PublicationPaths paths,
        string currentStaging,
        PublicationState state,
        CancellationToken ct)
    {
        List<RecoveryCandidate> markedBackups = [];
        foreach (string backup in Directory.GetDirectories(
            paths.Parent,
            $".{paths.Name}.backup-*"))
        {
            StagedDirectoryVersion? version =
                await TryReadDirectoryVersionAsync(backup, ct).ConfigureAwait(false);
            if (version is not null)
            {
                markedBackups.Add(new RecoveryCandidate(
                    backup,
                    version,
                    IsStaging: false,
                    Force: false));
            }
        }

        List<RecoveryCandidate> markedStaging = [];
        foreach (string staging in Directory.GetDirectories(
                paths.Parent,
                $".{paths.Name}.staging-*"))
        {
            if (string.Equals(
                    staging,
                    currentStaging,
                    StringComparison.OrdinalIgnoreCase) ||
                IsLeaseActive(staging + ".lease"))
            {
                continue;
            }

            ReadyPublication? ready = await TryReadJsonAsync<ReadyPublication>(
                System.IO.Path.Combine(staging, ReadyFileName),
                ct).ConfigureAwait(false);
            StagedDirectoryVersion? version =
                await TryReadDirectoryVersionAsync(staging, ct).ConfigureAwait(false);
            if (ready is not null &&
                version is not null &&
                VersionsEqual(ready.Version, version))
            {
                markedStaging.Add(new RecoveryCandidate(
                    staging,
                    version,
                    IsStaging: true,
                    ready.Force));
            }
        }

        RecoveryCandidate? selected = null;
        if (!Directory.Exists(paths.Target))
        {
            RecoveryCandidate[] matchingBackups = state.Current is null
                ? []
                : markedBackups
                    .Where(candidate => VersionsEqual(
                        candidate.Version,
                        state.Current))
                    .ToArray();
            if (matchingBackups.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple backup directories match the durable publication state for " +
                    $"'{paths.Target}'.");
            }

            List<RecoveryCandidate> validStaging = [];
            foreach (RecoveryCandidate candidate in markedStaging)
            {
                try
                {
                    await ValidateOwnershipAndOrderAsync(
                        paths.Target,
                        state,
                        state.Current,
                        candidate.Version,
                        new StagedDirectoryPublishOptions(Force: candidate.Force),
                        ct).ConfigureAwait(false);
                    validStaging.Add(candidate);
                }
                catch (StaleDirectoryPublicationException)
                {
                }
            }

            selected = SelectRecoveryCandidate(
                validStaging,
                matchingBackups.SingleOrDefault());
            if (selected is not null)
            {
                Directory.Move(selected.Path, paths.Target);
                state = WithCurrent(state, selected.Version);
                await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
            }
        }

        if (!await TargetMatchesStateAsync(paths, state, ct).ConfigureAwait(false))
        {
            return state;
        }

        foreach (RecoveryCandidate backup in markedBackups)
        {
            if (selected is null ||
                !string.Equals(
                    backup.Path,
                    selected.Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                await DeleteDirectoryBestEffortAsync(backup.Path).ConfigureAwait(false);
            }
        }
        foreach (RecoveryCandidate staging in markedStaging)
        {
            if (selected is null ||
                !string.Equals(
                    staging.Path,
                    selected.Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                await DeleteDirectoryBestEffortAsync(staging.Path).ConfigureAwait(false);
                TryDeleteFile(staging.Path + ".lease");
            }
        }
        return state;
    }

    private static RecoveryCandidate? SelectRecoveryCandidate(
        IReadOnlyCollection<RecoveryCandidate> stagingCandidates,
        RecoveryCandidate? durableBackup)
    {
        RecoveryCandidate? selected = durableBackup;
        foreach (RecoveryCandidate candidate in stagingCandidates)
        {
            if (selected is null)
            {
                selected = candidate;
                continue;
            }
            if (VersionsEqual(selected.Version, candidate.Version))
            {
                throw new InvalidOperationException(
                    "Multiple orphan publication artifacts have the same build identity.");
            }
            if (selected.Version.Mode != StagedDirectoryPublicationMode.Snapshot ||
                candidate.Version.Mode != StagedDirectoryPublicationMode.Snapshot ||
                !string.Equals(
                    selected.Version.Owner,
                    candidate.Version.Owner,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    selected.Version.ProcessorKind,
                    candidate.Version.ProcessorKind,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Orphan publication artifacts cannot be ordered safely.");
            }

            long selectedSequence = selected.Version.Sequence!.Value;
            long candidateSequence = candidate.Version.Sequence!.Value;
            if (candidateSequence > selectedSequence)
            {
                selected = candidate;
                continue;
            }
            if (candidateSequence < selectedSequence)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Orphan snapshot sequence {candidateSequence} has ambiguous snapshot or build identities.");
        }
        return selected;
    }

    private static async Task<PublicationState> ReconcileCurrentStateAsync(
        PublicationPaths paths,
        PublicationState state,
        StagedDirectoryVersion? targetVersion,
        bool force,
        CancellationToken ct)
    {
        if (!Directory.Exists(paths.Target))
        {
            return state;
        }

        if (targetVersion is null)
        {
            if (state.Current is not null && !force)
            {
                throw new InvalidOperationException(
                    $"Output directory '{paths.Target}' does not match its durable publication state.");
            }
            if (state.Current is not null)
            {
                state = state with { Current = null };
                await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
            }
            return state;
        }

        state = PreserveHighWatermark(state, targetVersion);
        if (state.Current is null)
        {
            state = WithCurrent(state, targetVersion);
            await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
            return state;
        }

        if (!VersionsEqual(state.Current, targetVersion))
        {
            if (!force)
            {
                throw new InvalidOperationException(
                    $"Output directory '{paths.Target}' does not match its durable publication state.");
            }
            state = WithCurrent(state, targetVersion);
            await WriteJsonAtomicAsync(paths.StatePath, state, ct).ConfigureAwait(false);
        }
        return state;
    }

    private static PublicationState WithCurrent(
        PublicationState state,
        StagedDirectoryVersion version)
    {
        state = PreserveHighWatermark(state, version);
        return state with { Current = version };
    }

    private static PublicationState PreserveHighWatermark(
        PublicationState state,
        StagedDirectoryVersion version)
    {
        if (version.Mode != StagedDirectoryPublicationMode.Snapshot)
        {
            return state;
        }

        Dictionary<string, SnapshotHighWatermark> highWatermarks =
            new(state.SnapshotHighWatermarks, StringComparer.Ordinal);
        string processor = version.ProcessorKind!;
        long sequence = version.Sequence!.Value;
        if (highWatermarks.TryGetValue(
                processor,
                out SnapshotHighWatermark? existing))
        {
            if (sequence < existing.Sequence)
            {
                return state;
            }
            if (sequence == existing.Sequence)
            {
                if (existing.SnapshotId is null)
                {
                    highWatermarks[processor] =
                        new SnapshotHighWatermark(sequence, version.SnapshotId);
                }
                else if (!string.Equals(
                    existing.SnapshotId,
                    version.SnapshotId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Snapshot sequence {sequence} for processor '{processor}' is " +
                        $"associated with both '{existing.SnapshotId}' and '{version.SnapshotId}'.");
                }
                return state with { SnapshotHighWatermarks = highWatermarks };
            }
            if (string.Equals(
                existing.SnapshotId,
                version.SnapshotId,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snapshot '{version.SnapshotId}' has inconsistent sequences " +
                    $"{existing.Sequence} and {sequence}.");
            }
        }
        highWatermarks[processor] =
            new SnapshotHighWatermark(sequence, version.SnapshotId);
        return state with { SnapshotHighWatermarks = highWatermarks };
    }

    private static async Task<PublicationState> ReadStateAsync(
        string path,
        CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return EmptyState();
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using JsonDocument document =
            await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
        JsonElement root = document.RootElement;
        int formatVersion = root.TryGetProperty("formatVersion", out JsonElement format)
            ? format.GetInt32()
            : 1;
        StagedDirectoryVersion? current = root.TryGetProperty(
            "current",
            out JsonElement currentElement) &&
            currentElement.ValueKind != JsonValueKind.Null
            ? currentElement.Deserialize<StagedDirectoryVersion>(JsonOptions)
            : null;
        Dictionary<string, SnapshotHighWatermark> highWatermarks =
            new(StringComparer.Ordinal);
        if (root.TryGetProperty(
                "snapshotHighWatermarks",
                out JsonElement highWatermarkElement) &&
            highWatermarkElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in highWatermarkElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    long sequence = property.Value.GetInt64();
                    string? snapshotId =
                        current?.Mode == StagedDirectoryPublicationMode.Snapshot &&
                        string.Equals(
                            current.ProcessorKind,
                            property.Name,
                            StringComparison.Ordinal) &&
                        current.Sequence == sequence
                            ? current.SnapshotId
                            : null;
                    highWatermarks[property.Name] =
                        new SnapshotHighWatermark(sequence, snapshotId);
                    continue;
                }

                SnapshotHighWatermark? highWatermark =
                    property.Value.Deserialize<SnapshotHighWatermark>(JsonOptions);
                if (highWatermark is null || highWatermark.Sequence < 1)
                {
                    throw new InvalidOperationException(
                        $"Publication state file '{path}' has an invalid high-water mark.");
                }
                highWatermarks[property.Name] = highWatermark;
            }
        }
        return new PublicationState(
            Math.Max(formatVersion, StateFormatVersion),
            current,
            highWatermarks);
    }

    private static PublicationState EmptyState()
        => new(
            StateFormatVersion,
            null,
            new Dictionary<string, SnapshotHighWatermark>(StringComparer.Ordinal));

    private static async Task<StagedDirectoryVersion?> ReadDirectoryVersionAsync(
        string directory,
        CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }
        return await ReadJsonAsync<StagedDirectoryVersion>(
            System.IO.Path.Combine(directory, VersionFileName),
            ct).ConfigureAwait(false);
    }

    private static async Task<StagedDirectoryVersion?> TryReadDirectoryVersionAsync(
        string directory,
        CancellationToken ct)
    {
        try
        {
            return await ReadDirectoryVersionAsync(directory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path,
        CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Publication state file '{path}' is empty.");
    }

    private static async Task<T?> TryReadJsonAsync<T>(
        string path,
        CancellationToken ct)
    {
        try
        {
            return await ReadJsonAsync<T>(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException)
        {
            return default;
        }
    }

    public static void RejectSourcePathsWithinPublication(
        string outputDirectory,
        params string[] sourcePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(sourcePaths);

        PublicationPaths paths = PublicationPaths.Create(outputDirectory);
        foreach (string sourcePath in sourcePaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            string source = System.IO.Path.GetFullPath(sourcePath);
            if (IsSameOrDescendant(source, paths.Target) ||
                string.Equals(source, paths.LockPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, paths.StatePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, paths.SwapPath, StringComparison.OrdinalIgnoreCase) ||
                IsPublicationArtifactPath(paths, source))
            {
                throw new InvalidOperationException(
                    $"Input path '{source}' is contained within output or publisher control " +
                    $"paths for '{paths.Target}'.");
            }
        }
    }

    private static bool IsSameOrDescendant(string candidate, string directory)
    {
        string relative = System.IO.Path.GetRelativePath(directory, candidate);
        return string.Equals(relative, ".", StringComparison.Ordinal) ||
            (!IsOutsideRelativePath(relative) &&
             !System.IO.Path.IsPathRooted(relative));
    }

    private static bool IsPublicationArtifactPath(
        PublicationPaths paths,
        string candidate)
    {
        if (!string.Equals(
                System.IO.Path.GetDirectoryName(candidate),
                paths.Parent,
                StringComparison.OrdinalIgnoreCase))
        {
            string relative = System.IO.Path.GetRelativePath(paths.Parent, candidate);
            if (IsOutsideRelativePath(relative) ||
                System.IO.Path.IsPathRooted(relative))
            {
                return false;
            }
            string firstSegment = relative.Split(
                [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                2)[0];
            return firstSegment.StartsWith(
                    $".{paths.Name}.staging-",
                    PathComparison) ||
                firstSegment.StartsWith(
                    $".{paths.Name}.backup-",
                    PathComparison);
        }

        string fileName = System.IO.Path.GetFileName(candidate);
        return fileName.StartsWith(
                $".{paths.Name}.staging-",
                PathComparison) ||
            fileName.StartsWith(
                $".{paths.Name}.backup-",
                PathComparison);
    }

    private static bool IsOutsideRelativePath(string relative)
        => string.Equals(relative, "..", StringComparison.Ordinal) ||
           relative.StartsWith(
               $"..{System.IO.Path.DirectorySeparatorChar}",
               StringComparison.Ordinal) ||
           relative.StartsWith(
               $"..{System.IO.Path.AltDirectorySeparatorChar}",
               StringComparison.Ordinal);

    private static async Task<bool> TargetMatchesStateAsync(
        PublicationPaths paths,
        PublicationState state,
        CancellationToken ct)
    {
        if (!Directory.Exists(paths.Target))
        {
            return state.Current is null;
        }
        StagedDirectoryVersion? target =
            await TryReadDirectoryVersionAsync(paths.Target, ct).ConfigureAwait(false);
        return target is null
            ? state.Current is null
            : state.Current is not null && VersionsEqual(target, state.Current);
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken ct)
    {
        string tempPath = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void ValidateVersion(StagedDirectoryVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(version.BuildIdentity);
        if (version.Mode == StagedDirectoryPublicationMode.Legacy)
        {
            if (version.ProcessorKind is not null ||
                version.Sequence is not null ||
                version.SnapshotId is not null)
            {
                throw new ArgumentException(
                    "Legacy publication versions cannot carry snapshot coordinates.",
                    nameof(version));
            }
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(version.ProcessorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(version.SnapshotId);
        if (version.Sequence is null or < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version.Sequence,
                "Snapshot sequence must be positive.");
        }
    }

    private static bool VersionsEqual(
        StagedDirectoryVersion left,
        StagedDirectoryVersion right)
        => string.Equals(left.Owner, right.Owner, StringComparison.Ordinal) &&
           left.Mode == right.Mode &&
           string.Equals(left.BuildIdentity, right.BuildIdentity, StringComparison.Ordinal) &&
           string.Equals(left.ProcessorKind, right.ProcessorKind, StringComparison.Ordinal) &&
           left.Sequence == right.Sequence &&
           string.Equals(left.SnapshotId, right.SnapshotId, StringComparison.Ordinal);

    private static async Task RollBackAsync(
        string target,
        string staging,
        string backup,
        bool backupCreated,
        bool targetPromoted)
    {
        Exception? rollbackError = null;
        try
        {
            if (targetPromoted && Directory.Exists(target))
            {
                await DeleteDirectoryWithRetryAsync(target, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            if (backupCreated && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }
        }
        catch (Exception ex)
        {
            rollbackError = ex;
        }

        if (rollbackError is not null)
        {
            throw new AggregateException(
                "Directory promotion failed and rollback also failed.",
                rollbackError);
        }
        await DeleteDirectoryBestEffortAsync(staging).ConfigureAwait(false);
    }

    private static async Task InvokeHookAsync(
        StagedDirectoryPublisherTestHooks? hooks,
        StagedDirectoryPublishCheckpoint checkpoint,
        string path,
        CancellationToken ct)
    {
        if (hooks is not null)
        {
            await hooks.OnCheckpointAsync(checkpoint, path, ct).ConfigureAwait(false);
        }
    }

    private static void ValidateSwapPaths(
        PublicationPaths paths,
        SwapState swap)
    {
        if (!string.Equals(
                System.IO.Path.GetFullPath(swap.Target),
                paths.Target,
                StringComparison.OrdinalIgnoreCase) ||
            !IsOwnedSibling(paths, swap.Staging, "staging") ||
            !IsOwnedSibling(paths, swap.Backup, "backup"))
        {
            throw new InvalidOperationException(
                $"Publication swap state '{paths.SwapPath}' contains invalid paths.");
        }
    }

    private static bool IsOwnedSibling(
        PublicationPaths paths,
        string candidate,
        string kind)
    {
        string full = System.IO.Path.GetFullPath(candidate);
        return string.Equals(
                   System.IO.Path.GetDirectoryName(full),
                   paths.Parent,
                   PathComparison) &&
               System.IO.Path.GetFileName(full).StartsWith(
                   $".{paths.Name}.{kind}-",
                   PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsLeaseActive(string leasePath)
    {
        if (!File.Exists(leasePath))
        {
            return false;
        }
        try
        {
            using FileStream lease = new(
                leasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async Task<FileStream> AcquireLockAsync(
        string lockPath,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 200)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10 * attempt, 100)), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string path,
        CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException &&
                attempt < 20)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteDirectoryBestEffortAsync(string path)
    {
        try
        {
            await DeleteDirectoryWithRetryAsync(path, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (string file in Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (string directory in Directory.EnumerateDirectories(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Directory);
        }
        File.SetAttributes(root, FileAttributes.Directory);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PublicationState(
        int FormatVersion,
        StagedDirectoryVersion? Current,
        IReadOnlyDictionary<string, SnapshotHighWatermark> SnapshotHighWatermarks);

    private sealed record SnapshotHighWatermark(
        long Sequence,
        string? SnapshotId);

    private sealed record RecoveryCandidate(
        string Path,
        StagedDirectoryVersion Version,
        bool IsStaging,
        bool Force);

    private sealed record ReadyPublication(
        StagedDirectoryVersion Version,
        bool Force);

    private enum SwapPhase
    {
        Prepared,
        BackupCreated,
        TargetPromoted,
    }

    private sealed record SwapState(
        string Target,
        string Staging,
        string Backup,
        StagedDirectoryVersion Incoming,
        bool Force,
        SwapPhase Phase);

    private sealed record PublicationPaths(
        string Target,
        string Parent,
        string Name,
        string LockPath,
        string StatePath,
        string SwapPath)
    {
        public static PublicationPaths Create(string outputDirectory)
        {
            string target = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(outputDirectory));
            string? parent = System.IO.Path.GetDirectoryName(target);
            string name = System.IO.Path.GetFileName(target);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "Output directory must have a parent directory.",
                    nameof(outputDirectory));
            }
            return new PublicationPaths(
                target,
                parent,
                name,
                System.IO.Path.Combine(parent, $".{name}.publish.lock"),
                System.IO.Path.Combine(parent, $".{name}.publish-state.json"),
                System.IO.Path.Combine(parent, $".{name}.publish-swap.json"));
        }
    }
}
