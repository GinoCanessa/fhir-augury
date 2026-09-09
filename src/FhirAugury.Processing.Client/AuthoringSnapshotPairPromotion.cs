using System.Text.Json;

namespace FhirAugury.Processing.Client;

internal enum AuthoringSnapshotPairPromotionCheckpoint
{
    AfterStateWritten,
    AfterBackupCreated,
    AfterTargetPromoted,
}

internal sealed record AuthoringSnapshotPairPromotionHooks(
    Func<
        AuthoringSnapshotPairPromotionCheckpoint,
        string,
        CancellationToken,
        Task> OnCheckpointAsync);

internal sealed class AuthoringSnapshotPairSimulatedCrashException(
    string message)
    : Exception(message);

internal sealed record AuthoringSnapshotPairPaths(
    string Target,
    string Parent,
    string Name,
    string LockPath,
    string StatePath)
{
    public const string StagingMarkerFileName =
        ".fhir-augury-authoring-pair-staging";

    public static AuthoringSnapshotPairPaths Create(string targetDirectory)
    {
        string target = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(targetDirectory));
        string? parent = Path.GetDirectoryName(target);
        string name = Path.GetFileName(target);
        if (string.IsNullOrWhiteSpace(parent) ||
            string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "Snapshot pair directory must not be a filesystem root.",
                nameof(targetDirectory));
        }
        if (File.Exists(target))
        {
            throw new IOException(
                $"Snapshot pair directory '{target}' is an existing file.");
        }

        Directory.CreateDirectory(parent);
        return new AuthoringSnapshotPairPaths(
            target,
            parent,
            name,
            Path.Combine(
                parent,
                $".{name}.fhir-augury-pair.lock"),
            Path.Combine(
                parent,
                $".{name}.fhir-augury-pair-swap.json"));
    }

    public string CreateStagingDirectory()
        => Path.Combine(
            Parent,
            $".{Name}.staging-{Guid.NewGuid():N}");

    public string CreateBackupDirectory()
        => Path.Combine(
            Parent,
            $".{Name}.backup-{Guid.NewGuid():N}");

    public string GetOwnedSibling(string siblingName, string kind)
    {
        AuthoringSnapshotPairVerifier.ValidateSafeWindowsFileName(
            siblingName,
            $"{kind} directory name");
        string fullPath = Path.GetFullPath(Path.Combine(Parent, siblingName));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetDirectoryName(fullPath),
                Parent,
                comparison) ||
            !Path.GetFileName(fullPath).StartsWith(
                $".{Name}.{kind}-",
                comparison))
        {
            throw new InvalidOperationException(
                $"Snapshot pair swap state contains an invalid {kind} directory.");
        }
        return fullPath;
    }
}

internal static class AuthoringSnapshotPairFileSystem
{
    public static async Task<FileStream> AcquireLockAsync(
        string lockPath,
        CancellationToken ct)
    {
        while (true)
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
            catch (IOException)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
    }

    public static async Task WriteBytesDurablyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken ct)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
        await using FileStream output = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(content, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    public static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteBytesDurablyAsync(temporaryPath, bytes, ct);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public static void TryDeleteFile(string path)
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

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

internal sealed record AuthoringSnapshotPairSwapState(
    int FormatVersion,
    string StagingDirectoryName,
    string BackupDirectoryName,
    AuthoringSnapshotPairManifest Incoming,
    bool? TargetExisted = null,
    AuthoringSnapshotPairManifest? Previous = null)
{
    public const int CurrentFormatVersion = 1;
}

internal static class AuthoringSnapshotPairPromoter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

    public static async Task<VerifiedAuthoringSnapshotPair?>
        RecoverAndVerifyAsync(
            AuthoringSnapshotPairPaths paths,
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        await using FileStream publishLock =
            await AuthoringSnapshotPairFileSystem.AcquireLockAsync(
                paths.LockPath,
                ct);
        await RecoverAsync(paths, ct);

        AuthoringSnapshotPairVerifier verifier = new();
        VerifiedAuthoringSnapshotPair? pair =
            await TryVerifyAsync(
                verifier,
                serviceName,
                runId,
                paths.Target,
                ct);
        if (pair is not null)
        {
            return pair;
        }
        if (File.Exists(paths.StatePath))
        {
            throw new InvalidOperationException(
                $"Snapshot pair promotion for '{paths.Target}' still has unresolved recovery state.");
        }

        await InspectReplaceableTargetAsync(
            verifier,
            paths.Target,
            ct);
        return null;
    }

    public static async Task<VerifiedAuthoringSnapshotPair> PromoteAsync(
        AuthoringSnapshotPairPaths paths,
        VerifiedAuthoringSnapshotPair stagedPair,
        AuthoringSnapshotPairPromotionHooks? hooks,
        CancellationToken ct)
    {
        await using FileStream publishLock =
            await AuthoringSnapshotPairFileSystem.AcquireLockAsync(
                paths.LockPath,
                ct);
        await RecoverAsync(paths, ct);

        AuthoringSnapshotPairVerifier verifier = new();
        VerifiedAuthoringSnapshotPair? current =
            await TryVerifyAsync(
                verifier,
                stagedPair.ServiceName,
                stagedPair.RunId,
                paths.Target,
                ct);
        if (current is not null &&
            ManifestsEqual(current.Manifest, stagedPair.Manifest))
        {
            AuthoringSnapshotPairFileSystem.TryDeleteDirectory(
                stagedPair.DirectoryPath);
            return current;
        }
        if (File.Exists(paths.StatePath))
        {
            AuthoringSnapshotPairFileSystem.TryDeleteDirectory(
                stagedPair.DirectoryPath);
            throw new InvalidOperationException(
                $"Snapshot pair promotion for '{paths.Target}' still has unresolved recovery state.");
        }

        TargetState previous =
            await InspectReplaceableTargetAsync(
                verifier,
                paths.Target,
                ct);
        string backup = paths.CreateBackupDirectory();
        AuthoringSnapshotPairSwapState state = new(
            AuthoringSnapshotPairSwapState.CurrentFormatVersion,
            Path.GetFileName(stagedPair.DirectoryPath),
            Path.GetFileName(backup),
            stagedPair.Manifest,
            previous.Exists,
            previous.Pair?.Manifest);
        await AuthoringSnapshotPairFileSystem.WriteJsonAtomicallyAsync(
            paths.StatePath,
            state,
            JsonOptions,
            ct);

        bool backupCreated = false;
        bool targetPromoted = false;
        try
        {
            await InvokeHookAsync(
                hooks,
                AuthoringSnapshotPairPromotionCheckpoint.AfterStateWritten,
                paths.StatePath,
                ct);
            if (previous.Exists)
            {
                Directory.Move(paths.Target, backup);
                backupCreated = true;
            }

            await InvokeHookAsync(
                hooks,
                AuthoringSnapshotPairPromotionCheckpoint.AfterBackupCreated,
                backup,
                ct);
            Directory.Move(stagedPair.DirectoryPath, paths.Target);
            targetPromoted = true;
            await InvokeHookAsync(
                hooks,
                AuthoringSnapshotPairPromotionCheckpoint.AfterTargetPromoted,
                paths.Target,
                ct);

            VerifiedAuthoringSnapshotPair promoted =
                await verifier.VerifyReadyPairAsync(
                    stagedPair.ServiceName,
                    stagedPair.RunId,
                    paths.Target,
                    ct);
            if (!ManifestsEqual(
                    promoted.Manifest,
                    stagedPair.Manifest))
            {
                throw new InvalidOperationException(
                    "Promoted snapshot pair does not match the staged pair.");
            }

            await CompleteCommitAsync(
                paths,
                state,
                stagedPair.DirectoryPath,
                backup);
            return promoted;
        }
        catch (AuthoringSnapshotPairSimulatedCrashException)
        {
            throw;
        }
        catch (Exception original)
        {
            List<Exception> rollbackErrors =
                await RollbackAsync(
                    paths,
                    state,
                    stagedPair.DirectoryPath,
                    backup,
                    targetPromoted,
                    backupCreated);
            if (rollbackErrors.Count > 0)
            {
                throw new IOException(
                    "Snapshot pair promotion failed and the previous pair could not be fully restored.",
                    new AggregateException([original, .. rollbackErrors]));
            }
            throw;
        }
    }

    private static async Task RecoverAsync(
        AuthoringSnapshotPairPaths paths,
        CancellationToken ct)
    {
        if (!File.Exists(paths.StatePath))
        {
            return;
        }

        AuthoringSnapshotPairSwapState state;
        try
        {
            await using FileStream input = new(
                paths.StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            state =
                await JsonSerializer.DeserializeAsync<
                    AuthoringSnapshotPairSwapState>(
                    input,
                    JsonOptions,
                    ct)
                ?? throw new InvalidOperationException(
                    "Snapshot pair swap state is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Snapshot pair swap state '{paths.StatePath}' is invalid.",
                ex);
        }
        if (state.FormatVersion !=
            AuthoringSnapshotPairSwapState.CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported snapshot pair swap state version {state.FormatVersion}.");
        }
        if (state.TargetExisted == false &&
            state.Previous is not null)
        {
            throw new InvalidOperationException(
                "Snapshot pair swap state has inconsistent previous-target coordinates.");
        }

        string service =
            AuthoringServiceBinding.Normalize(state.Incoming.ServiceName);
        string runId = state.Incoming.RunId;
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string staging = paths.GetOwnedSibling(
            state.StagingDirectoryName,
            "staging");
        string backup = paths.GetOwnedSibling(
            state.BackupDirectoryName,
            "backup");
        AuthoringSnapshotPairVerifier verifier = new();

        VerifiedAuthoringSnapshotPair? targetPair =
            await TryVerifyAsync(
                verifier,
                service,
                runId,
                paths.Target,
                ct);
        if (targetPair is not null &&
            ManifestsEqual(
                targetPair.Manifest,
                state.Incoming))
        {
            await CompleteCommitAsync(
                paths,
                state,
                staging,
                backup);
            return;
        }

        VerifiedAuthoringSnapshotPair? stagingPair =
            await TryVerifyManagedAsync(
                verifier,
                staging,
                ct);
        if (stagingPair is not null &&
            ManifestsEqual(
                stagingPair.Manifest,
                state.Incoming))
        {
            if (Directory.Exists(paths.Target))
            {
                if (Directory.Exists(backup))
                {
                    throw new InvalidOperationException(
                        "Interrupted snapshot pair promotion contains both a target and backup but no valid incoming target.");
                }
                await VerifyPreviousDirectoryAsync(
                    verifier,
                    paths.Target,
                    state,
                    ct);
                Directory.Move(paths.Target, backup);
            }
            else
            {
                await ValidateForwardRecoveryBackupAsync(
                    verifier,
                    backup,
                    state,
                    ct);
            }
            Directory.Move(staging, paths.Target);
            VerifiedAuthoringSnapshotPair recovered =
                await verifier.VerifyReadyPairAsync(
                    service,
                    runId,
                    paths.Target,
                    ct);
            if (!ManifestsEqual(
                    recovered.Manifest,
                    state.Incoming))
            {
                throw new InvalidOperationException(
                    "Recovered snapshot pair does not match swap state.");
            }
            await CompleteCommitAsync(
                paths,
                state,
                staging,
                backup);
            return;
        }

        if (!Directory.Exists(paths.Target) &&
            Directory.Exists(backup))
        {
            await VerifyPreviousDirectoryAsync(
                verifier,
                backup,
                state,
                ct);
            Directory.Move(backup, paths.Target);
        }
        else if (Directory.Exists(paths.Target) &&
                 Directory.Exists(backup))
        {
            throw new InvalidOperationException(
                "Interrupted snapshot pair promotion contains both an unverified target and backup.");
        }

        await VerifyPreviousDirectoryAsync(
            verifier,
            paths.Target,
            state,
            ct);
        await DeleteOwnedStagingDirectoryAsync(
            verifier,
            staging,
            state,
            ct);
        if (Directory.Exists(backup))
        {
            throw new InvalidOperationException(
                "Interrupted snapshot pair rollback left its backup directory in place.");
        }
        File.Delete(paths.StatePath);
    }

    private static async Task CompleteCommitAsync(
        AuthoringSnapshotPairPaths paths,
        AuthoringSnapshotPairSwapState state,
        string staging,
        string backup)
    {
        try
        {
            AuthoringSnapshotPairVerifier verifier = new();
            await DeleteOwnedStagingDirectoryAsync(
                verifier,
                staging,
                state,
                CancellationToken.None);
            if (Directory.Exists(backup))
            {
                await VerifyPreviousDirectoryAsync(
                    verifier,
                    backup,
                    state,
                    CancellationToken.None);
                Directory.Delete(backup, recursive: true);
            }
            File.Delete(paths.StatePath);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or
                UnauthorizedAccessException or ArgumentException)
        {
            // The target is already verified. The retained state lets the
            // next invocation complete cleanup under the same lock.
        }
    }

    private static async Task<List<Exception>> RollbackAsync(
        AuthoringSnapshotPairPaths paths,
        AuthoringSnapshotPairSwapState state,
        string staging,
        string backup,
        bool targetPromoted,
        bool backupCreated)
    {
        List<Exception> errors = [];
        AuthoringSnapshotPairVerifier verifier = new();

        if (targetPromoted &&
            Directory.Exists(paths.Target))
        {
            await TryRollbackAsync(
                async () =>
                {
                    await VerifyIncomingDirectoryAsync(
                        verifier,
                        paths.Target,
                        state,
                        CancellationToken.None);
                    Directory.Delete(
                        paths.Target,
                        recursive: true);
                },
                errors);
        }
        if (backupCreated &&
            Directory.Exists(backup))
        {
            await TryRollbackAsync(
                () =>
                {
                    if (Directory.Exists(paths.Target))
                    {
                        throw new IOException(
                            "Snapshot pair rollback cannot restore its backup while the promoted target remains.");
                    }
                    Directory.Move(backup, paths.Target);
                    return Task.CompletedTask;
                },
                errors);
        }

        await TryRollbackAsync(
            () => VerifyPreviousDirectoryAsync(
                verifier,
                paths.Target,
                state,
                CancellationToken.None),
            errors);
        if (errors.Count == 0)
        {
            await TryRollbackAsync(
                () => DeleteOwnedStagingDirectoryAsync(
                    verifier,
                    staging,
                    state,
                    CancellationToken.None),
                errors);
        }
        if (errors.Count == 0 &&
            Directory.Exists(backup))
        {
            errors.Add(
                new IOException(
                    "Snapshot pair rollback left its backup directory in place."));
        }
        if (errors.Count == 0)
        {
            await TryRollbackAsync(
                () =>
                {
                    File.Delete(paths.StatePath);
                    return Task.CompletedTask;
                },
                errors);
        }
        return errors;
    }

    private static async Task<TargetState> InspectReplaceableTargetAsync(
        AuthoringSnapshotPairVerifier verifier,
        string target,
        CancellationToken ct)
    {
        if (!Directory.Exists(target))
        {
            return new TargetState(Exists: false, Pair: null);
        }
        RejectDirectoryReparsePoint(target);

        VerifiedAuthoringSnapshotPair? managed =
            await TryVerifyManagedAsync(
                verifier,
                target,
                ct);
        if (managed is not null)
        {
            return new TargetState(Exists: true, Pair: managed);
        }
        if (!Directory.EnumerateFileSystemEntries(target).Any())
        {
            return new TargetState(Exists: true, Pair: null);
        }

        throw new IOException(
            $"Snapshot pair directory '{target}' is non-empty and is not a verified FHIR Augury snapshot pair. Its contents will not be replaced.");
    }

    private static async Task ValidateForwardRecoveryBackupAsync(
        AuthoringSnapshotPairVerifier verifier,
        string backup,
        AuthoringSnapshotPairSwapState state,
        CancellationToken ct)
    {
        if (Directory.Exists(backup))
        {
            if (state.TargetExisted == false)
            {
                throw new InvalidOperationException(
                    "Snapshot pair swap state has an unexpected backup for a previously absent target.");
            }
            await VerifyPreviousDirectoryAsync(
                verifier,
                backup,
                state,
                ct);
            return;
        }

        if (state.TargetExisted == true)
        {
            throw new InvalidOperationException(
                "Snapshot pair swap state lost the backup for its previous target.");
        }
    }

    private static async Task VerifyIncomingDirectoryAsync(
        AuthoringSnapshotPairVerifier verifier,
        string directory,
        AuthoringSnapshotPairSwapState state,
        CancellationToken ct)
    {
        VerifiedAuthoringSnapshotPair pair =
            await verifier.VerifyManagedReadyPairAsync(
                directory,
                ct);
        if (!ManifestsEqual(pair.Manifest, state.Incoming))
        {
            throw new InvalidOperationException(
                "Snapshot pair directory does not match the incoming swap coordinates.");
        }
    }

    private static async Task VerifyPreviousDirectoryAsync(
        AuthoringSnapshotPairVerifier verifier,
        string directory,
        AuthoringSnapshotPairSwapState state,
        CancellationToken ct)
    {
        if (state.TargetExisted == false)
        {
            if (Directory.Exists(directory))
            {
                throw new InvalidOperationException(
                    "Snapshot pair rollback expected the previous target to be absent.");
            }
            return;
        }
        if (!Directory.Exists(directory))
        {
            if (state.TargetExisted == true)
            {
                throw new InvalidOperationException(
                    "Snapshot pair rollback did not restore the previous target.");
            }
            return;
        }

        RejectDirectoryReparsePoint(directory);
        if (state.Previous is not null)
        {
            VerifiedAuthoringSnapshotPair previous =
                await verifier.VerifyManagedReadyPairAsync(
                    directory,
                    ct);
            if (!ManifestsEqual(previous.Manifest, state.Previous))
            {
                throw new InvalidOperationException(
                    "Restored snapshot pair does not match the previous target recorded by the swap state.");
            }
            return;
        }

        if (state.TargetExisted == true)
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new InvalidOperationException(
                    "Restored snapshot pair target was expected to be empty.");
            }
            return;
        }

        await InspectReplaceableTargetAsync(
            verifier,
            directory,
            ct);
    }

    private static async Task DeleteOwnedStagingDirectoryAsync(
        AuthoringSnapshotPairVerifier verifier,
        string staging,
        AuthoringSnapshotPairSwapState state,
        CancellationToken ct)
    {
        if (!Directory.Exists(staging))
        {
            return;
        }
        await VerifyIncomingDirectoryAsync(
            verifier,
            staging,
            state,
            ct);
        Directory.Delete(staging, recursive: true);
    }

    private static async Task<VerifiedAuthoringSnapshotPair?> TryVerifyAsync(
        AuthoringSnapshotPairVerifier verifier,
        string serviceName,
        string runId,
        string directory,
        CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }
        try
        {
            return await verifier.VerifyReadyPairAsync(
                serviceName,
                runId,
                directory,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<VerifiedAuthoringSnapshotPair?>
        TryVerifyManagedAsync(
            AuthoringSnapshotPairVerifier verifier,
            string directory,
            CancellationToken ct)
    {
        try
        {
            return await verifier.VerifyManagedReadyPairAsync(
                directory,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or
                IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ManifestsEqual(
        AuthoringSnapshotPairManifest left,
        AuthoringSnapshotPairManifest right)
        => left == right;

    private static async Task InvokeHookAsync(
        AuthoringSnapshotPairPromotionHooks? hooks,
        AuthoringSnapshotPairPromotionCheckpoint checkpoint,
        string path,
        CancellationToken ct)
    {
        if (hooks is not null)
        {
            await hooks.OnCheckpointAsync(checkpoint, path, ct);
        }
    }

    private static async Task TryRollbackAsync(
        Func<Task> action,
        ICollection<Exception> errors)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private static void RejectDirectoryReparsePoint(string directory)
    {
        if (File.GetAttributes(directory)
            .HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"Snapshot pair directory '{directory}' cannot be a reparse point.");
        }
    }

    private sealed record TargetState(
        bool Exists,
        VerifiedAuthoringSnapshotPair? Pair);
}
