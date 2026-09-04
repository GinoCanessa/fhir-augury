using FhirAugury.Common.IO;

namespace FhirAugury.Common.Tests.IO;

public sealed class StagedDirectoryPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"staged-publisher-{Guid.NewGuid():N}");

    public StagedDirectoryPublisherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishesValidatedSiblingStagingDirectory()
    {
        string output = Path.Combine(_root, "site");
        StagedDirectoryVersion version = Snapshot(1, "snapshot-1", "build-1");

        StagedDirectoryPublishResult result =
            await PublishAsync(output, version, "new");

        Assert.Equal(StagedDirectoryPublishOutcome.Promoted, result.Outcome);
        Assert.Equal("new", File.ReadAllText(Path.Combine(output, "index.html")));
        Assert.True(File.Exists(Path.Combine(
            output,
            StagedDirectoryPublisher.VersionFileName)));
        Assert.Empty(Directory.GetDirectories(_root, ".site.staging-*"));
        Assert.Empty(Directory.GetDirectories(_root, ".site.backup-*"));
    }

    [Fact]
    public async Task ValidationFailureKeepsCurrentDirectoryAndCleansStaging()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(1, "snapshot-1", "build-1"), "current");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StagedDirectoryPublisher.PublishAsync(
                output,
                Snapshot(2, "snapshot-2", "build-2"),
                (staging, _) =>
                {
                    File.WriteAllText(Path.Combine(staging, "index.html"), "invalid");
                    return Task.CompletedTask;
                },
                (_, _) => throw new InvalidOperationException("invalid stage")));

        Assert.Equal("current", File.ReadAllText(Path.Combine(output, "index.html")));
        Assert.Empty(Directory.GetDirectories(_root, ".site.staging-*"));
    }

    [Fact]
    public async Task SameSnapshotAndBuildIsIdempotent()
    {
        string output = Path.Combine(_root, "site");
        StagedDirectoryVersion version = Snapshot(2, "snapshot-2", "build-2");
        await PublishAsync(output, version, "current");

        StagedDirectoryPublishResult result =
            await PublishAsync(output, version, "duplicate");

        Assert.Equal(StagedDirectoryPublishOutcome.Idempotent, result.Outcome);
        Assert.Equal("current", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task SameSnapshotWithDifferentBuildIdentityRebuildsSafely()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(2, "snapshot-2", "build-a"), "old build");

        StagedDirectoryPublishResult result =
            await PublishAsync(output, Snapshot(2, "snapshot-2", "build-b"), "new build");

        Assert.Equal(StagedDirectoryPublishOutcome.Promoted, result.Outcome);
        Assert.Equal("new build", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task ExistingValidatedLegacyOutputIsAdoptedWithoutForce()
    {
        string output = Path.Combine(_root, "site");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "index.html"), "pre-phase-7");
        bool validated = false;
        StagedDirectoryPublishOptions options = new(
            ValidateLegacyReplacementAsync: (_, force, _) =>
            {
                Assert.False(force);
                validated = true;
                return Task.CompletedTask;
            });

        StagedDirectoryPublishResult result = await PublishAsync(
            output,
            StagedDirectoryVersion.Legacy("site-owner", "legacy-build"),
            "adopted",
            options);

        Assert.True(validated);
        Assert.Equal(StagedDirectoryPublishOutcome.Promoted, result.Outcome);
        Assert.Equal("adopted", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task NewerSnapshotPromotesBeforeSlowerOlderSnapshot()
    {
        string output = Path.Combine(_root, "site");
        TaskCompletionSource olderRendered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowOlderValidation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<StagedDirectoryPublishResult> older =
            StagedDirectoryPublisher.PublishAsync(
                output,
                Snapshot(10, "snapshot-10", "build-10"),
                (staging, _) =>
                {
                    File.WriteAllText(Path.Combine(staging, "index.html"), "older");
                    olderRendered.SetResult();
                    return Task.CompletedTask;
                },
                async (_, ct) => await allowOlderValidation.Task.WaitAsync(ct));

        await olderRendered.Task;
        StagedDirectoryPublishResult newer =
            await PublishAsync(
                output,
                Snapshot(11, "snapshot-11", "build-11"),
                "newer");
        allowOlderValidation.SetResult();

        await Assert.ThrowsAsync<StaleDirectoryPublicationException>(
            async () => await older);
        Assert.Equal(StagedDirectoryPublishOutcome.Promoted, newer.Outcome);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task UnownedDirectoryRequiresExplicitForce()
    {
        string output = Path.Combine(_root, "site");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "index.html"), "unrelated");

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PublishAsync(
                    output,
                    Snapshot(1, "snapshot-1", "build-1"),
                    "owned"));
        Assert.Contains("not owned", error.Message, StringComparison.OrdinalIgnoreCase);

        StagedDirectoryPublishResult forced = await PublishAsync(
            output,
            Snapshot(1, "snapshot-1", "build-1"),
            "owned",
            new StagedDirectoryPublishOptions(Force: true));
        Assert.Equal(StagedDirectoryPublishOutcome.Promoted, forced.Outcome);
        Assert.Equal("owned", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task ForcedLegacyReplacementPreservesSnapshotHighWatermark()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(10, "snapshot-10", "build-10"), "snapshot");

        await PublishAsync(
            output,
            StagedDirectoryVersion.Legacy("site-owner", "legacy-build"),
            "legacy",
            new StagedDirectoryPublishOptions(Force: true));

        await Assert.ThrowsAsync<StaleDirectoryPublicationException>(
            () => PublishAsync(
                output,
                Snapshot(9, "snapshot-9", "build-9"),
                "stale",
                new StagedDirectoryPublishOptions(Force: true)));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task ForcedLegacyReplacementRejectsDifferentSnapshotAtSameHighWatermark()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(10, "snapshot-a", "build-a"), "snapshot");

        await PublishAsync(
            output,
            StagedDirectoryVersion.Legacy("site-owner", "legacy-build"),
            "legacy",
            new StagedDirectoryPublishOptions(Force: true));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PublishAsync(
                    output,
                    Snapshot(10, "snapshot-b", "build-b"),
                    "conflict",
                    new StagedDirectoryPublishOptions(Force: true)));

        Assert.Contains("snapshot-a", error.Message, StringComparison.Ordinal);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task InterruptedSwapAfterBackupCreationRecoversNewestSequence()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(1, "snapshot-1", "build-1"), "one");

        StagedDirectoryPublisherTestHooks hooks = new(
            (checkpoint, _, _) =>
            {
                if (checkpoint == StagedDirectoryPublishCheckpoint.AfterBackupCreated)
                {
                    throw new StagedDirectorySimulatedCrashException("simulated crash");
                }
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<StagedDirectorySimulatedCrashException>(
            () => PublishAsync(
                output,
                Snapshot(2, "snapshot-2", "build-2"),
                "two",
                options: null,
                hooks));
        File.Delete(Path.Combine(_root, ".site.publish-swap.json"));

        await Assert.ThrowsAsync<StaleDirectoryPublicationException>(
            () => PublishAsync(
                output,
                Snapshot(1, "snapshot-1", "build-1"),
                "stale"));
        Assert.Equal("two", File.ReadAllText(Path.Combine(output, "index.html")));
        Assert.Empty(Directory.GetDirectories(_root, ".site.backup-*"));
        Assert.Empty(Directory.GetDirectories(_root, ".site.staging-*"));
    }

    [Fact]
    public async Task InconsistentTargetIsRejectedBeforeOrphanCleanup()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(1, "snapshot-1", "build-1"), "one");

        StagedDirectoryPublisherTestHooks hooks = new(
            (checkpoint, _, _) =>
            {
                if (checkpoint == StagedDirectoryPublishCheckpoint.AfterBackupCreated)
                {
                    throw new StagedDirectorySimulatedCrashException("simulated crash");
                }
                return Task.CompletedTask;
            });
        await Assert.ThrowsAsync<StagedDirectorySimulatedCrashException>(
            () => PublishAsync(
                output,
                Snapshot(2, "snapshot-2", "build-2"),
                "two",
                options: null,
                hooks));

        string backup = Assert.Single(
            Directory.GetDirectories(_root, ".site.backup-*"));
        string staging = Assert.Single(
            Directory.GetDirectories(_root, ".site.staging-*"));
        File.Delete(Path.Combine(_root, ".site.publish-swap.json"));
        Directory.Move(staging, output);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PublishAsync(
                    output,
                    Snapshot(3, "snapshot-3", "build-3"),
                    "three"));

        Assert.Contains("durable publication state", error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(backup));
        Assert.Equal("two", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task UnmarkedBackupDirectoryIsNeverDeletedAsAnOrphan()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(1, "snapshot-1", "build-1"), "one");
        string unmarkedBackup = Path.Combine(_root, ".site.backup-user-content");
        Directory.CreateDirectory(unmarkedBackup);
        File.WriteAllText(Path.Combine(unmarkedBackup, "keep.txt"), "keep");

        await PublishAsync(output, Snapshot(2, "snapshot-2", "build-2"), "two");

        Assert.True(File.Exists(Path.Combine(unmarkedBackup, "keep.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task PostCommitCleanupFailureDoesNotFailPublicationAndRecoversLater()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(1, "snapshot-1", "build-1"), "one");

        StagedDirectoryPublisherTestHooks hooks = new(
            (checkpoint, _, _) =>
            {
                if (checkpoint == StagedDirectoryPublishCheckpoint.BeforePostCommitCleanup)
                {
                    throw new IOException("cleanup unavailable");
                }
                return Task.CompletedTask;
            });
        StagedDirectoryVersion incoming = Snapshot(2, "snapshot-2", "build-2");

        StagedDirectoryPublishResult promoted = await PublishAsync(
            output,
            incoming,
            "two",
            options: null,
            hooks);

        Assert.Equal(StagedDirectoryPublishOutcome.Promoted, promoted.Outcome);
        Assert.Equal("two", File.ReadAllText(Path.Combine(output, "index.html")));
        Assert.NotEmpty(Directory.GetDirectories(_root, ".site.backup-*"));

        StagedDirectoryPublishResult recovered =
            await PublishAsync(output, incoming, "duplicate");
        Assert.Equal(StagedDirectoryPublishOutcome.Idempotent, recovered.Outcome);
        Assert.Empty(Directory.GetDirectories(_root, ".site.backup-*"));
        Assert.Empty(Directory.GetDirectories(_root, ".site.staging-*"));
    }

    [Fact]
    public async Task BackupMoveFailureNeverDeletesCurrentTarget()
    {
        string output = Path.Combine(_root, "site");
        await PublishAsync(output, Snapshot(1, "snapshot-1", "build-1"), "current");
        StagedDirectoryPublisherTestHooks hooks = new(
            (checkpoint, _, _) =>
            {
                if (checkpoint == StagedDirectoryPublishCheckpoint.BeforeBackupCreated)
                {
                    throw new IOException("target is locked");
                }
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IOException>(
            () => PublishAsync(
                output,
                Snapshot(2, "snapshot-2", "build-2"),
                "replacement",
                options: null,
                hooks));

        Assert.Equal("current", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task ImmutableSnapshotRejectsSidecarsAndIgnoresLaterSourceChanges()
    {
        string source = Path.Combine(_root, "snapshot.db");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(source + "-wal", "sidecar");

        InvalidOperationException sidecarError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ImmutableFileSnapshot.CreateAsync(source));
        Assert.Contains("sidecar", sidecarError.Message, StringComparison.OrdinalIgnoreCase);

        File.Delete(source + "-wal");
        await using ImmutableFileSnapshot snapshot =
            await ImmutableFileSnapshot.CreateAsync(source);
        await File.WriteAllTextAsync(source, "changed");

        Assert.Equal("original", await File.ReadAllTextAsync(snapshot.Path));
        Assert.Equal("snapshot.db", snapshot.SourceFileName);
        Assert.True(File.GetAttributes(snapshot.Path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void RejectsSourcesWithinOutputAndPublisherControlPaths()
    {
        string output = Path.Combine(_root, "site");

        Assert.Throws<InvalidOperationException>(
            () => StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                output,
                Path.Combine(output, "snapshot.db")));
        Assert.Throws<InvalidOperationException>(
            () => StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                output,
                Path.Combine(_root, ".site.publish-state.json")));
        Assert.Throws<InvalidOperationException>(
            () => StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                output,
                Path.Combine(_root, ".site.staging-user", "descriptor.json")));
    }

    [Fact]
    public void RejectsMixedCasePublisherArtifactPathsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string output = Path.Combine(_root, "Site");
        Assert.Throws<InvalidOperationException>(
            () => StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                output,
                Path.Combine(_root, ".site.staging-user", "descriptor.json")));
    }

    private static StagedDirectoryVersion Snapshot(
        long sequence,
        string snapshotId,
        string buildIdentity)
        => StagedDirectoryVersion.Snapshot(
            "site-owner",
            "processor",
            sequence,
            snapshotId,
            buildIdentity);

    private Task<StagedDirectoryPublishResult> PublishAsync(
        string output,
        StagedDirectoryVersion version,
        string content,
        StagedDirectoryPublishOptions? options = null,
        StagedDirectoryPublisherTestHooks? hooks = null)
        => StagedDirectoryPublisher.PublishAsync(
            output,
            version,
            (staging, _) =>
            {
                Assert.Equal(_root, Path.GetDirectoryName(staging));
                File.WriteAllText(Path.Combine(staging, "index.html"), content);
                return Task.CompletedTask;
            },
            (staging, _) =>
            {
                Assert.True(File.Exists(Path.Combine(staging, "index.html")));
                return Task.CompletedTask;
            },
            options,
            hooks);
}
