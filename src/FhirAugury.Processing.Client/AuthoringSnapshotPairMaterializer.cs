namespace FhirAugury.Processing.Client;

public sealed record AuthoringSnapshotPairMaterialization(
    FhirAugury.Processing.Contracts.AuthoringSnapshotDescriptor Descriptor,
    string SnapshotPath,
    string DescriptorPath);

public static class AuthoringSnapshotPairMaterializer
{
    public static async Task<AuthoringSnapshotPairMaterialization>
        MaterializeAsync(
            VerifiedAuthoringSnapshotPair pair,
            string snapshotPath,
            string descriptorPath,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);

        AuthoringSnapshotPairVerifier verifier = new();
        VerifiedAuthoringSnapshotPair source =
            pair.IsDurable
                ? await verifier.VerifyReadyPairAsync(
                    pair.ServiceName,
                    pair.RunId,
                    pair.DirectoryPath,
                    ct)
                : await verifier.VerifyLegacyPairAsync(
                    pair.ServiceName,
                    pair.RunId,
                    pair.DescriptorPath,
                    pair.DatabasePath,
                    ct);

        string fullSnapshotPath = Path.GetFullPath(snapshotPath);
        string fullDescriptorPath = Path.GetFullPath(descriptorPath);
        if (AuthoringSnapshotPairVerifier.PathsEqual(
                fullSnapshotPath,
                fullDescriptorPath))
        {
            throw new ArgumentException(
                "Snapshot and descriptor paths must be different.");
        }
        AuthoringSnapshotPairVerifier.ValidateSafeWindowsFileName(
            Path.GetFileName(fullSnapshotPath),
            "snapshot output file name");
        AuthoringSnapshotPairVerifier.ValidateSafeWindowsFileName(
            Path.GetFileName(fullDescriptorPath),
            "descriptor output file name");
        RejectPairControlPath(
            fullSnapshotPath,
            "Snapshot output");
        RejectPairControlPath(
            fullDescriptorPath,
            "Descriptor output");
        RejectCrossedSourcePath(
            fullSnapshotPath,
            source.DescriptorPath,
            "Snapshot output");
        RejectCrossedSourcePath(
            fullDescriptorPath,
            source.DatabasePath,
            "Descriptor output");
        RejectCustomOutputInsideDurablePair(
            source,
            source.DatabasePath,
            fullSnapshotPath,
            "Snapshot output");
        RejectCustomOutputInsideDurablePair(
            source,
            source.DescriptorPath,
            fullDescriptorPath,
            "Descriptor output");

        List<string> publicationPaths = [];
        if (!AuthoringSnapshotPairVerifier.PathsEqual(
                source.DatabasePath,
                fullSnapshotPath))
        {
            EnsureParentDirectory(fullSnapshotPath);
            publicationPaths.Add(fullSnapshotPath);
        }
        if (!AuthoringSnapshotPairVerifier.PathsEqual(
                source.DescriptorPath,
                fullDescriptorPath))
        {
            EnsureParentDirectory(fullDescriptorPath);
            publicationPaths.Add(fullDescriptorPath);
        }
        IReadOnlyList<FileStream> publicationLocks =
            await AcquirePublicationLocksAsync(
                publicationPaths,
                ct);
        try
        {
            await MaterializeUnderLockAsync(
                source,
                fullSnapshotPath,
                fullDescriptorPath,
                ct);
        }
        finally
        {
            for (int index = publicationLocks.Count - 1;
                 index >= 0;
                 index--)
            {
                publicationLocks[index].Dispose();
            }
        }

        VerifiedAuthoringSnapshotPair materialized =
            await verifier.VerifyLegacyPairAsync(
                source,
                fullDescriptorPath,
                fullSnapshotPath,
                ct);
        return new AuthoringSnapshotPairMaterialization(
            materialized.Descriptor,
            fullSnapshotPath,
            fullDescriptorPath);
    }

    private static async Task MaterializeUnderLockAsync(
        VerifiedAuthoringSnapshotPair source,
        string snapshotPath,
        string descriptorPath,
        CancellationToken ct)
    {
        OutputFile snapshot = new(source.DatabasePath, snapshotPath);
        OutputFile descriptor = new(source.DescriptorPath, descriptorPath);
        OutputFile[] outputs = [snapshot, descriptor];

        try
        {
            foreach (OutputFile output in outputs)
            {
                if (!output.RequiresCopy)
                {
                    continue;
                }
                await CopyFileAsync(
                    output.SourcePath,
                    output.TemporaryPath,
                    ct);
            }
            ct.ThrowIfCancellationRequested();

            foreach (OutputFile output in outputs)
            {
                if (!output.RequiresCopy)
                {
                    continue;
                }
                if (File.Exists(output.DestinationPath))
                {
                    File.Move(
                        output.DestinationPath,
                        output.BackupPath,
                        overwrite: false);
                    output.BackedUp = true;
                }
                File.Move(
                    output.TemporaryPath,
                    output.DestinationPath,
                    overwrite: false);
                output.Published = true;
            }

            AuthoringSnapshotPairVerifier verifier = new();
            await verifier.VerifyLegacyPairAsync(
                source,
                descriptorPath,
                snapshotPath,
                ct);
        }
        catch (Exception original)
        {
            List<Exception> rollbackErrors = [];
            foreach (OutputFile output in outputs.Reverse())
            {
                TryRollback(
                    () =>
                    {
                        if (output.Published &&
                            File.Exists(output.DestinationPath))
                        {
                            File.Delete(output.DestinationPath);
                        }
                        if (output.BackedUp)
                        {
                            File.Move(
                                output.BackupPath,
                                output.DestinationPath,
                                overwrite: false);
                        }
                    },
                    rollbackErrors);
            }
            if (rollbackErrors.Count > 0)
            {
                throw new IOException(
                    "Snapshot pair materialization failed and the previous output pair could not be fully restored.",
                    new AggregateException([original, .. rollbackErrors]));
            }
            throw;
        }
        finally
        {
            foreach (OutputFile output in outputs)
            {
                AuthoringSnapshotPairFileSystem.TryDeleteFile(
                    output.TemporaryPath);
            }
        }

        foreach (OutputFile output in outputs)
        {
            AuthoringSnapshotPairFileSystem.TryDeleteFile(
                output.BackupPath);
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        await using FileStream input = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    private static async Task<IReadOnlyList<FileStream>>
        AcquirePublicationLocksAsync(
            IReadOnlyCollection<string> outputPaths,
            CancellationToken ct)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string[] lockPaths = outputPaths
            .Select(path => $"{path}.fhir-augury.publish.lock")
            .Distinct(comparer)
            .OrderBy(path => path, comparer)
            .ToArray();
        List<FileStream> streams = [];
        try
        {
            foreach (string lockPath in lockPaths)
            {
                streams.Add(
                    await AuthoringSnapshotPairFileSystem.AcquireLockAsync(
                        lockPath,
                        ct));
            }
            return streams;
        }
        catch
        {
            for (int index = streams.Count - 1;
                 index >= 0;
                 index--)
            {
                streams[index].Dispose();
            }
            throw;
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void RejectCrossedSourcePath(
        string destinationPath,
        string otherSourcePath,
        string description)
    {
        if (AuthoringSnapshotPairVerifier.PathsEqual(
                destinationPath,
                otherSourcePath))
        {
            throw new ArgumentException(
                $"{description} path cannot replace the other member of the verified source pair.");
        }
    }

    private static void RejectCustomOutputInsideDurablePair(
        VerifiedAuthoringSnapshotPair source,
        string sourcePath,
        string destinationPath,
        string description)
    {
        if (!source.IsDurable ||
            AuthoringSnapshotPairVerifier.PathsEqual(
                sourcePath,
                destinationPath) ||
            !IsPathWithinDirectory(
                destinationPath,
                source.DirectoryPath))
        {
            return;
        }

        throw new ArgumentException(
            $"{description} path cannot add files inside the durable snapshot pair directory.");
    }

    private static bool IsPathWithinDirectory(
        string path,
        string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory));
        if (AuthoringSnapshotPairVerifier.PathsEqual(
                fullPath,
                fullDirectory))
        {
            return true;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(
            $"{fullDirectory}{Path.DirectorySeparatorChar}",
            comparison);
    }

    private static void RejectPairControlPath(
        string path,
        string description)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            string name = Path.GetFileName(current);
            if (!string.IsNullOrEmpty(name) &&
                IsPairControlName(name))
            {
                throw new ArgumentException(
                    $"{description} path cannot use snapshot pair control or internal path '{name}'.");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsPairControlName(string name)
    {
        if (string.Equals(
                name,
                AuthoringSnapshotPairManifest.ReadyFileName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                name,
                AuthoringSnapshotPairPaths.StagingMarkerFileName,
                StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(
                ".fhir-augury-pair.lock",
                StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(
                ".fhir-augury-pair-swap.json",
                StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(
                ".fhir-augury.publish.lock",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasGeneratedSuffix(name, ".staging-", extension: null) ||
            HasGeneratedSuffix(name, ".backup-", extension: null) ||
            HasGeneratedSuffix(name, ".", ".tmp") ||
            HasGeneratedSuffix(name, ".", ".bak");
    }

    private static bool HasGeneratedSuffix(
        string name,
        string marker,
        string? extension)
    {
        ReadOnlySpan<char> candidate = name.AsSpan();
        if (extension is not null)
        {
            if (!candidate.EndsWith(
                    extension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            candidate = candidate[..^extension.Length];
        }

        int markerIndex = candidate.LastIndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }
        ReadOnlySpan<char> suffix =
            candidate[(markerIndex + marker.Length)..];
        return suffix.Length == 32 &&
            suffix.ToArray().All(Uri.IsHexDigit);
    }

    private static void TryRollback(
        Action action,
        ICollection<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private sealed class OutputFile
    {
        public OutputFile(
            string sourcePath,
            string destinationPath)
        {
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
            RequiresCopy =
                !AuthoringSnapshotPairVerifier.PathsEqual(
                    sourcePath,
                    destinationPath);
            TemporaryPath =
                $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            BackupPath =
                $"{destinationPath}.{Guid.NewGuid():N}.bak";
        }

        public string SourcePath { get; }

        public string DestinationPath { get; }

        public bool RequiresCopy { get; }

        public string TemporaryPath { get; }

        public string BackupPath { get; }

        public bool BackedUp { get; set; }

        public bool Published { get; set; }
    }
}
