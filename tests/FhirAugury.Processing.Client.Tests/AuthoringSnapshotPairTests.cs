using System.Net;
using System.Text.Json;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Client.Tests;

public sealed class AuthoringSnapshotPairTests
{
    [Fact]
    public async Task DownloadStreamsAndPromotesVerifiedReadyPair()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3, 4, 5];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(bytes);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(bytes));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);
        string pairDirectory = root.Combine("pair");

        VerifiedAuthoringSnapshotPair pair =
            await client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None);

        Assert.True(pair.IsDurable);
        Assert.Equal("Preparer", pair.ServiceName);
        Assert.Equal("run-1", pair.RunId);
        Assert.Equal("snapshot-1", pair.SnapshotId);
        Assert.Equal(
            bytes,
            await File.ReadAllBytesAsync(pair.DatabasePath));
        Assert.Equal(
            AuthoringSnapshotPairManifest.ReadyFileName,
            Path.GetFileName(pair.ReadyManifestPath));
        Assert.Equal(
            [
                "snapshot.db",
                "snapshot.db.descriptor.json",
                "verified-pair.json",
            ],
            Directory.GetFiles(pairDirectory)
                .Select(path => Path.GetFileName(path)!)
                .Order()
                .ToArray());

        AuthoringSnapshotPairManifest manifest =
            JsonSerializer.Deserialize<AuthoringSnapshotPairManifest>(
                await File.ReadAllTextAsync(pair.ReadyManifestPath!),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Preparer", manifest.ServiceName);
        Assert.Equal(
            AuthoringClientTestData.Hash(bytes),
            manifest.DatabaseSha256);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ExistingMatchingPairIsReverifiedAndReused()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(bytes);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(bytes));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);
        string pairDirectory = root.Combine("pair");
        VerifiedAuthoringSnapshotPair first =
            await client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None);
        DateTime firstWrite =
            File.GetLastWriteTimeUtc(first.DatabasePath);

        VerifiedAuthoringSnapshotPair second =
            await client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None);

        Assert.Equal(first.Manifest, second.Manifest);
        Assert.Equal(
            firstWrite,
            File.GetLastWriteTimeUtc(second.DatabasePath));
        Assert.Equal(2, handler.Calls);
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.staging-*"));
    }

    [Fact]
    public async Task DownloadRejectsUnownedNonEmptyTargetWithoutDeletingIt()
    {
        using TemporaryDirectory root = new();
        string pairDirectory = root.Combine("pair");
        Directory.CreateDirectory(pairDirectory);
        string sentinelPath = Path.Combine(pairDirectory, "operator.txt");
        await File.WriteAllTextAsync(sentinelPath, "keep me");
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new InvalidOperationException(
                "Network must not be used for an unsafe target."));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None));

        Assert.Contains(
            "will not be replaced",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep me", await File.ReadAllTextAsync(sentinelPath));
        Assert.Equal(0, handler.Calls);
        Assert.Single(Directory.GetFileSystemEntries(pairDirectory));
    }

    [Fact]
    public async Task DownloadDoesNotDeleteExtraContentBesideManagedPair()
    {
        using TemporaryDirectory root = new();
        string pairDirectory = root.Combine("pair");
        await DownloadAsync(
            root,
            [1, 2, 3],
            pairDirectory: pairDirectory,
            runId: "run-old");
        string sentinelPath = Path.Combine(
            pairDirectory,
            "operator.txt");
        await File.WriteAllTextAsync(sentinelPath, "keep me");
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new InvalidOperationException(
                "Network must not be used for a mixed-ownership target."));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        await Assert.ThrowsAsync<IOException>(
            () => client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-new",
                pairDirectory,
                CancellationToken.None));

        Assert.Equal("keep me", await File.ReadAllTextAsync(sentinelPath));
        Assert.True(File.Exists(
            Path.Combine(
                pairDirectory,
                AuthoringSnapshotPairManifest.ReadyFileName)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReadyManifestBindsPairToServiceAndRun()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3];
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, bytes);
        AuthoringSnapshotPairVerifier verifier = new();

        InvalidOperationException serviceError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => verifier.VerifyReadyPairAsync(
                    "Planner",
                    "run-1",
                    pair.DirectoryPath));
        InvalidOperationException runError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => verifier.VerifyReadyPairAsync(
                    "Preparer",
                    "run-2",
                    pair.DirectoryPath));

        Assert.Contains("service", serviceError.Message);
        Assert.Contains("run", runError.Message);
    }

    [Theory]
    [InlineData("CON.db")]
    [InlineData("COM¹")]
    [InlineData("COM².db")]
    [InlineData("COM³.snapshot")]
    [InlineData("LPT¹")]
    [InlineData("LPT².db")]
    [InlineData("LPT³.snapshot")]
    [InlineData("notes.db:payload")]
    [InlineData("../notes.db")]
    [InlineData("notes.db.")]
    public async Task DownloadRejectsUnsafeWindowsDescriptorFileName(
        string fileName)
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(
                bytes,
                fileName: fileName);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => throw new InvalidOperationException(
                    "Bytes must not be requested."));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadSnapshotPairAsync(
                    "Preparer",
                    "run-1",
                    root.Combine("pair"),
                    CancellationToken.None));

        Assert.Contains(
            "file name",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Calls);
        Assert.False(Directory.Exists(root.Combine("pair")));
    }

    [Fact]
    public async Task DownloadRejectsDescriptorRunCoordinateMismatch()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(
                bytes,
                runId: "run-other");
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(bytes));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadSnapshotPairAsync(
                    "Preparer",
                    "run-1",
                    root.Combine("pair"),
                    CancellationToken.None));

        Assert.Contains("run-other", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadRejectsChecksumOrLengthMismatch(
        bool checksumMismatch)
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(
                bytes,
                sha256: checksumMismatch
                    ? new string('0', 64)
                    : null,
                sizeBytes: checksumMismatch
                    ? null
                    : bytes.Length + 1);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(bytes));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadSnapshotPairAsync(
                    "Preparer",
                    "run-1",
                    root.Combine("pair"),
                    CancellationToken.None));

        Assert.Contains("do not match", error.Message);
        Assert.False(Directory.Exists(root.Combine("pair")));
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.staging-*"));
    }

    [Fact]
    public async Task InterruptedStreamDeletesPartialDirectoryAndRetries()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3, 4];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(bytes);
        int byteRequests = 0;
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith(
                    "/bytes",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    DelegateHttpHandler.Json(descriptor));
            }

            byteRequests++;
            Stream stream = byteRequests == 1
                ? new InterruptedReadStream()
                : new MemoryStream(bytes);
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxStreamRetries: 1);

        VerifiedAuthoringSnapshotPair pair =
            await client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                root.Combine("pair"),
                CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(pair.DatabasePath));
        Assert.Equal(2, byteRequests);
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.staging-*"));
    }

    [Fact]
    public async Task CallerCancellationDeletesPartialDownload()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(bytes);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new BlockingReadStream());
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxStreamRetries: 3);
        using CancellationTokenSource cancellation =
            new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                root.Combine("pair"),
                cancellation.Token));

        Assert.False(Directory.Exists(root.Combine("pair")));
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.staging-*"));
    }

    [Fact]
    public async Task VerifierRejectsMissingAndPartialReadyManifest()
    {
        using TemporaryDirectory root = new();
        string pairDirectory = root.Combine("pair");
        Directory.CreateDirectory(pairDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(pairDirectory, "snapshot.db"),
            [1, 2, 3]);
        AuthoringSnapshotPairVerifier verifier = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.VerifyReadyPairAsync(
                "Preparer",
                "run-1",
                pairDirectory));
        await File.WriteAllTextAsync(
            Path.Combine(
                pairDirectory,
                AuthoringSnapshotPairManifest.ReadyFileName),
            """{"formatVersion":1,"serviceName":"Preparer"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.VerifyReadyPairAsync(
                "Preparer",
                "run-1",
                pairDirectory));
    }

    [Fact]
    public async Task VerifierDetectsDatabaseChangedAfterReadyManifest()
    {
        using TemporaryDirectory root = new();
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, [1, 2, 3]);
        await File.AppendAllTextAsync(pair.DatabasePath, "changed");

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new AuthoringSnapshotPairVerifier()
                    .VerifyReadyPairAsync(
                        "Preparer",
                        "run-1",
                        pair.DirectoryPath));

        Assert.Contains(
            "length",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InterruptedDirectorySwapRecoversCompleteIncomingPair()
    {
        using TemporaryDirectory root = new();
        string pairDirectory = root.Combine("pair");
        byte[] oldBytes = [1, 1, 1];
        await DownloadAsync(
            root,
            oldBytes,
            pairDirectory: pairDirectory,
            runId: "run-old",
            snapshotId: "snapshot-old");

        byte[] newBytes = [2, 2, 2, 2];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(
                newBytes,
                snapshotId: "snapshot-2",
                sequence: 2);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(newBytes));
        AuthoringSnapshotPairPromotionHooks hooks = new(
            (checkpoint, _, _) =>
            {
                if (checkpoint ==
                    AuthoringSnapshotPairPromotionCheckpoint
                        .AfterBackupCreated)
                {
                    throw new
                        AuthoringSnapshotPairSimulatedCrashException(
                            "simulated crash");
                }
                return Task.CompletedTask;
            });
        AuthoringControlClient crashingClient =
            AuthoringClientTestData.CreateClient(
                handler,
                hooks: hooks);

        await Assert.ThrowsAsync<
            AuthoringSnapshotPairSimulatedCrashException>(
            () => crashingClient.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None));
        Assert.False(Directory.Exists(pairDirectory));
        Assert.NotEmpty(
            Directory.GetDirectories(
                root.Path,
                ".pair.backup-*"));
        Assert.NotEmpty(
            Directory.GetDirectories(
                root.Path,
                ".pair.staging-*"));

        DelegateHttpHandler outageHandler = new((_, _, _) =>
            throw new HttpRequestException("orchestrator unavailable"));
        AuthoringControlClient recoveringClient =
            AuthoringClientTestData.CreateClient(outageHandler);
        VerifiedAuthoringSnapshotPair recovered =
            await recoveringClient.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None);

        Assert.Equal("snapshot-2", recovered.SnapshotId);
        Assert.Equal(
            newBytes,
            await File.ReadAllBytesAsync(recovered.DatabasePath));
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.backup-*"));
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.staging-*"));
        Assert.False(File.Exists(
            root.Combine(
                ".pair.fhir-augury-pair-swap.json")));
        Assert.Equal(0, outageHandler.Calls);
    }

    [Fact]
    public async Task PromotionFailureRollsBackPriorVerifiedPair()
    {
        using TemporaryDirectory root = new();
        string pairDirectory = root.Combine("pair");
        byte[] oldBytes = [1, 1, 1];
        VerifiedAuthoringSnapshotPair oldPair =
            await DownloadAsync(
                root,
                oldBytes,
                pairDirectory: pairDirectory,
                runId: "run-old",
                snapshotId: "snapshot-old");
        AuthoringSnapshotPairManifest oldManifest =
            oldPair.Manifest;

        byte[] newBytes = [2, 2, 2, 2];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(
                newBytes,
                snapshotId: "snapshot-2",
                sequence: 2);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(newBytes));
        AuthoringSnapshotPairPromotionHooks hooks = new(
            (checkpoint, _, _) =>
            {
                if (checkpoint ==
                    AuthoringSnapshotPairPromotionCheckpoint
                        .AfterTargetPromoted)
                {
                    throw new IOException("target unavailable");
                }
                return Task.CompletedTask;
            });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                hooks: hooks);

        await Assert.ThrowsAsync<IOException>(
            () => client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None));

        VerifiedAuthoringSnapshotPair restored =
            await new AuthoringSnapshotPairVerifier()
                .VerifyReadyPairAsync(
                    "Preparer",
                    "run-old",
                    pairDirectory);
        Assert.Equal(oldManifest, restored.Manifest);
        Assert.Equal(
            oldBytes,
            await File.ReadAllBytesAsync(restored.DatabasePath));
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.backup-*"));
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.staging-*"));
        Assert.False(File.Exists(
            root.Combine(
                ".pair.fhir-augury-pair-swap.json")));
    }

    [Fact]
    public async Task IncompleteRollbackRetainsSwapStateForRecovery()
    {
        using TemporaryDirectory root = new();
        string pairDirectory = root.Combine("pair");
        VerifiedAuthoringSnapshotPair oldPair =
            await DownloadAsync(
                root,
                [1, 1, 1],
                pairDirectory: pairDirectory,
                runId: "run-old",
                snapshotId: "snapshot-old");

        byte[] newBytes = [2, 2, 2, 2];
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(
                newBytes,
                snapshotId: "snapshot-2",
                sequence: 2);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(newBytes));
        AuthoringSnapshotPairPromotionHooks hooks = new(
            async (checkpoint, _, ct) =>
            {
                if (checkpoint ==
                    AuthoringSnapshotPairPromotionCheckpoint
                        .AfterTargetPromoted)
                {
                    string backup = Directory.GetDirectories(
                        root.Path,
                        ".pair.backup-*").Single();
                    await File.AppendAllTextAsync(
                        Path.Combine(
                            backup,
                            oldPair.Descriptor.FileName),
                        "corrupt",
                        ct);
                    throw new IOException(
                        "previous target became unverifiable");
                }
            });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                hooks: hooks);

        await Assert.ThrowsAsync<IOException>(
            () => client.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None));

        string statePath = root.Combine(
            ".pair.fhir-augury-pair-swap.json");
        Assert.True(File.Exists(statePath));
        Assert.Empty(
            Directory.GetDirectories(
                root.Path,
                ".pair.backup-*"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AuthoringSnapshotPairVerifier()
                .VerifyReadyPairAsync(
                    "Preparer",
                    "run-old",
                    pairDirectory));

        DelegateHttpHandler outageHandler = new((_, _, _) =>
            throw new InvalidOperationException(
                "Recovery must fail locally before network access."));
        AuthoringControlClient recoveringClient =
            AuthoringClientTestData.CreateClient(outageHandler);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recoveringClient.DownloadSnapshotPairAsync(
                "Preparer",
                "run-1",
                pairDirectory,
                CancellationToken.None));

        Assert.True(File.Exists(statePath));
        Assert.Equal(0, outageHandler.Calls);
    }

    [Fact]
    public async Task LegacyMaterializationCopiesAndReverifiesBothFiles()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3];
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, bytes);
        string output = root.Combine("legacy");
        Directory.CreateDirectory(output);
        string snapshotPath =
            Path.Combine(output, pair.Descriptor.FileName);
        string descriptorPath =
            $"{snapshotPath}.descriptor.json";

        AuthoringSnapshotPairMaterialization result =
            await AuthoringSnapshotPairMaterializer.MaterializeAsync(
                pair,
                snapshotPath,
                descriptorPath);

        Assert.Equal(snapshotPath, result.SnapshotPath);
        Assert.Equal(descriptorPath, result.DescriptorPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshotPath));
        Assert.False(
            File.Exists(
                Path.Combine(
                    output,
                    AuthoringSnapshotPairManifest.ReadyFileName)));
        AuthoringSnapshotPairVerifier verifier = new();
        foreach (string ambiguousService in new[]
                 {
                     "Preparer",
                     "Planner",
                 })
        {
            InvalidOperationException error =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => verifier.VerifyLegacyPairAsync(
                        ambiguousService,
                        "run-1",
                        descriptorPath,
                        snapshotPath));
            Assert.Contains(
                "trusted durable pair binding",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task BallotNotesLegacyPairRetainsUnambiguousCompatibility()
    {
        using TemporaryDirectory root = new();
        byte[] bytes = [1, 2, 3];
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(
                root,
                bytes,
                serviceName: "BallotNotes");
        string output = root.Combine("legacy");
        Directory.CreateDirectory(output);
        string snapshotPath =
            Path.Combine(output, pair.Descriptor.FileName);
        string descriptorPath =
            Path.Combine(output, "notes.json");
        await AuthoringSnapshotPairMaterializer.MaterializeAsync(
            pair,
            snapshotPath,
            descriptorPath);

        VerifiedAuthoringSnapshotPair legacy =
            await new AuthoringSnapshotPairVerifier()
                .VerifyLegacyPairAsync(
                    "BallotNotes",
                    "run-1",
                    descriptorPath,
                    snapshotPath);

        Assert.False(legacy.IsDurable);
        Assert.Equal("BallotNotes", legacy.ServiceName);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(legacy.DatabasePath));
    }

    [Fact]
    public async Task CustomDescriptorDoesNotPoisonDurablePairReplacement()
    {
        using TemporaryDirectory root = new();
        string pairDirectory = root.Combine("pair");
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(
                root,
                [1, 2, 3],
                pairDirectory: pairDirectory,
                runId: "run-old",
                snapshotId: "snapshot-old");
        string customDescriptorPath =
            root.Combine("legacy", "custom-descriptor.json");

        await AuthoringSnapshotPairMaterializer.MaterializeAsync(
            pair,
            pair.DatabasePath,
            customDescriptorPath);

        Assert.True(File.Exists(customDescriptorPath));
        Assert.False(File.Exists(
            $"{pair.DatabasePath}.fhir-augury.publish.lock"));
        Assert.Equal(
            [
                "snapshot.db",
                "snapshot.db.descriptor.json",
                "verified-pair.json",
            ],
            Directory.GetFileSystemEntries(pairDirectory)
                .Select(path => Path.GetFileName(path)!)
                .Order()
                .ToArray());

        byte[] replacementBytes = [4, 5, 6, 7];
        VerifiedAuthoringSnapshotPair replacement =
            await DownloadAsync(
                root,
                replacementBytes,
                pairDirectory: pairDirectory,
                runId: "run-new",
                snapshotId: "snapshot-new");

        Assert.Equal("run-new", replacement.RunId);
        Assert.Equal(
            replacementBytes,
            await File.ReadAllBytesAsync(replacement.DatabasePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaterializationRejectsCustomOutputInsideDurablePair(
        bool customSnapshot)
    {
        using TemporaryDirectory root = new();
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, [1, 2, 3]);
        string snapshotPath = customSnapshot
            ? Path.Combine(pair.DirectoryPath, "custom.db")
            : pair.DatabasePath;
        string descriptorPath = customSnapshot
            ? pair.DescriptorPath
            : Path.Combine(
                pair.DirectoryPath,
                "custom-descriptor.json");

        ArgumentException error =
            await Assert.ThrowsAsync<ArgumentException>(
                () => AuthoringSnapshotPairMaterializer.MaterializeAsync(
                    pair,
                    snapshotPath,
                    descriptorPath));

        Assert.Contains(
            "durable snapshot pair directory",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [
                "snapshot.db",
                "snapshot.db.descriptor.json",
                "verified-pair.json",
            ],
            Directory.GetFileSystemEntries(pair.DirectoryPath)
                .Select(path => Path.GetFileName(path)!)
                .Order()
                .ToArray());
    }

    [Fact]
    public async Task MaterializationCannotOverwriteReadyManifest()
    {
        using TemporaryDirectory root = new();
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, [1, 2, 3]);
        string manifestBefore =
            await File.ReadAllTextAsync(pair.ReadyManifestPath!);

        await Assert.ThrowsAsync<ArgumentException>(
            () => AuthoringSnapshotPairMaterializer.MaterializeAsync(
                pair,
                pair.DatabasePath,
                pair.ReadyManifestPath!));

        Assert.Equal(
            manifestBefore,
            await File.ReadAllTextAsync(pair.ReadyManifestPath!));
        VerifiedAuthoringSnapshotPair verified =
            await new AuthoringSnapshotPairVerifier()
                .VerifyReadyPairAsync(
                    "Preparer",
                    "run-1",
                    pair.DirectoryPath);
        Assert.Equal(pair.Manifest, verified.Manifest);
    }

    [Theory]
    [InlineData(".fhir-augury-authoring-pair-staging")]
    [InlineData(".pair.fhir-augury-pair.lock")]
    [InlineData(".pair.fhir-augury-pair-swap.json")]
    [InlineData("notes.json.fhir-augury.publish.lock")]
    [InlineData("notes.0123456789abcdef0123456789abcdef.tmp")]
    public async Task MaterializationRejectsInternalControlPath(
        string descriptorFileName)
    {
        using TemporaryDirectory root = new();
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, [1, 2, 3]);
        string output = root.Combine("legacy");
        string snapshotPath = Path.Combine(
            output,
            pair.Descriptor.FileName);
        string descriptorPath = Path.Combine(
            output,
            descriptorFileName);

        ArgumentException error =
            await Assert.ThrowsAsync<ArgumentException>(
                () => AuthoringSnapshotPairMaterializer.MaterializeAsync(
                    pair,
                    snapshotPath,
                    descriptorPath));

        Assert.Contains(
            "control or internal",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(snapshotPath));
        Assert.False(File.Exists(descriptorPath));
    }

    [Fact]
    public async Task MaterializationRejectsGeneratedStagingAncestor()
    {
        using TemporaryDirectory root = new();
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, [1, 2, 3]);
        string internalDirectory = root.Combine(
            $".pair.staging-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<ArgumentException>(
            () => AuthoringSnapshotPairMaterializer.MaterializeAsync(
                pair,
                root.Combine("legacy", pair.Descriptor.FileName),
                Path.Combine(internalDirectory, "notes.json")));

        Assert.False(Directory.Exists(internalDirectory));
    }

    [Fact]
    public async Task LegacyMaterializationRestoresPriorFileWhenPairCannotComplete()
    {
        using TemporaryDirectory root = new();
        VerifiedAuthoringSnapshotPair pair =
            await DownloadAsync(root, [1, 2, 3]);
        string output = root.Combine("legacy");
        Directory.CreateDirectory(output);
        string snapshotPath =
            Path.Combine(output, pair.Descriptor.FileName);
        byte[] priorBytes = [9, 9, 9];
        await File.WriteAllBytesAsync(snapshotPath, priorBytes);
        string descriptorPath =
            Path.Combine(output, "descriptor-directory");
        Directory.CreateDirectory(descriptorPath);

        await Assert.ThrowsAnyAsync<IOException>(
            () => AuthoringSnapshotPairMaterializer.MaterializeAsync(
                pair,
                snapshotPath,
                descriptorPath));

        Assert.Equal(
            priorBytes,
            await File.ReadAllBytesAsync(snapshotPath));
        Assert.True(Directory.Exists(descriptorPath));
    }

    private static async Task<VerifiedAuthoringSnapshotPair> DownloadAsync(
        TemporaryDirectory root,
        byte[] bytes,
        string? pairDirectory = null,
        string runId = "run-1",
        string snapshotId = "snapshot-1",
        string serviceName = "Preparer")
    {
        AuthoringSnapshotDescriptor descriptor =
            AuthoringClientTestData.Descriptor(
                bytes,
                serviceName: serviceName,
                runId: runId,
                snapshotId: snapshotId);
        DelegateHttpHandler handler =
            AuthoringClientTestData.SnapshotHandler(
                descriptor,
                _ => new MemoryStream(bytes));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);
        return await client.DownloadSnapshotPairAsync(
            serviceName,
            runId,
            pairDirectory ?? root.Combine("pair"),
            CancellationToken.None);
    }

    private sealed class InterruptedReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
            => throw new IOException("connection interrupted");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(
                new IOException("connection interrupted"));

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(
            long offset,
            SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return 0;
        }

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(
            long offset,
            SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();
    }
}
