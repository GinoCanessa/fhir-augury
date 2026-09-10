using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets.Tests;

public sealed class TicketSitePublisherTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ticket-site-publisher-{Guid.NewGuid():N}");

    public TicketSitePublisherTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_root);

    [Fact]
    public async Task PublishesDiscussionSiteAndChooserFromVerifiedPair()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "discussion-site");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    output,
                    "Tickets for Discussion"));

        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.Equal("preparer", result.Manifest.SiteKind);
        Assert.Equal(pair.SnapshotId, result.Manifest.SnapshotId);
        Assert.True(File.Exists(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.False(Directory.Exists(Path.Combine(output, "applying")));
        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "index.html"));
        Assert.Contains(
            $"assets/app.js?v={result.Manifest.RendererAssetsVersion}",
            html,
            StringComparison.Ordinal);
        string script = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "assets",
            "app.js"));
        Assert.Contains(
            "CREATE TEMP VIEW jira_processing_source_tickets",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "pushInList('t.WorkGroupDisplay', wgValues)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishesApplyingSiteFromVerifiedPair()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "applying-site");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Applying,
                    output,
                    "Tickets for Applying"));

        Assert.Equal("planner", result.Manifest.SiteKind);
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "index.html")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "assets",
            "marked.min.js")));
        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "applying",
            "index.html"));
        Assert.Contains(
            $"assets/purify.min.js?v={result.Manifest.RendererAssetsVersion}",
            html,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
    }

    [Fact]
    public async Task ServiceMismatchIsRejectedBeforeDatabaseValidation()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        await File.WriteAllTextAsync(pair.DatabasePath, "not sqlite");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Applying,
                    Path.Combine(_root, "mismatch"),
                    "Wrong")));

        Assert.Equal(
            TicketSitePublishFailure.ServiceMismatch,
            exception.Failure);
        Assert.False(Directory.Exists(Path.Combine(_root, "mismatch")));
    }

    [Fact]
    public async Task ChangedVerifiedDatabaseIsRejectedByChecksum()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        await File.AppendAllTextAsync(pair.DatabasePath, "changed");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, Path.Combine(_root, "checksum"))));

        Assert.Equal(
            TicketSitePublishFailure.SnapshotValidation,
            exception.Failure);
        Assert.Contains(
            "length",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SchemaDriftIsRejectedUsingPublicCatalog()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        pair = await MutateAndReverifyAsync(
            pair,
            """
            ALTER TABLE prepared_ticket_repos
            ADD COLUMN UnexpectedV2Column TEXT
            """);

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, Path.Combine(_root, "schema-drift"))));

        Assert.Equal(
            TicketSitePublishFailure.SnapshotValidation,
            exception.Failure);
        Assert.Contains("schema v1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnexpectedV2Column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvenanceMismatchIsRejected()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        pair = await MutateAndReverifyAsync(
            pair,
            "UPDATE authoring_snapshot_provenance SET RunId = 'other-run'");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, Path.Combine(_root, "provenance"))));

        Assert.Contains(
            "provenance",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FiltersResolveCanonicallyAndTrimEmbeddedSite()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "filtered"),
                    "Tickets",
                    new TicketSiteFilters(
                        Specification: "fhir",
                        Project: "fhir",
                        WorkGroup: "fhir-i")));

        Assert.Equal("FHIR", result.ResolvedFilters.Specification);
        Assert.Equal("FHIR", result.ResolvedFilters.Project);
        Assert.Equal(
            "FHIR Infrastructure",
            result.ResolvedFilters.WorkGroup);
        Assert.Equal(1, result.Manifest.IncludedItemCount);
        Assert.Equal(1, result.Manifest.TableCounts["prepared_tickets"]);
    }

    [Fact]
    public async Task UnknownFilterReturnsTypedFailure()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "unknown-filter"),
                    "Tickets",
                    new TicketSiteFilters(Specification: "unknown"))));

        Assert.Equal(
            TicketSitePublishFailure.FilterValidation,
            exception.Failure);
        Assert.Contains("Available values", exception.Message);
    }

    [Fact]
    public async Task RepeatedBuildIsIdempotent()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 7);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "idempotent");
        TicketSitePublisher publisher = new();

        TicketSitePublishResult first =
            await publisher.PublishAsync(Request(pair, output));
        string manifestPath = Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName);
        DateTime firstWrite = File.GetLastWriteTimeUtc(manifestPath);
        await Task.Delay(50);
        TicketSitePublishResult second =
            await publisher.PublishAsync(Request(pair, output));

        Assert.Equal(TicketSitePublishOutcome.Published, first.Outcome);
        Assert.Equal(TicketSitePublishOutcome.Idempotent, second.Outcome);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(manifestPath));
        Assert.Equal(first.Manifest.GeneratedAt, second.Manifest.GeneratedAt);
    }

    [Fact]
    public async Task OlderSequenceCannotReplacePublishedSite()
    {
        TicketSnapshotFixture newer =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 11,
                snapshotId: "snapshot-11");
        TicketSnapshotFixture older =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 10,
                snapshotId: "snapshot-10");
        VerifiedAuthoringSnapshotPair newerPair =
            await newer.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair olderPair =
            await older.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "stale");
        TicketSitePublisher publisher = new();
        await publisher.PublishAsync(Request(newerPair, output));

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => publisher.PublishAsync(Request(olderPair, output)));

        Assert.Contains("older than", exception.Message, StringComparison.OrdinalIgnoreCase);
        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName));
        Assert.Equal("snapshot-11", manifest.SnapshotId);
    }

    [Fact]
    public async Task ValidationFailureRollsBackToPreviousSite()
    {
        TicketSnapshotFixture firstFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 20,
                snapshotId: "snapshot-20");
        TicketSnapshotFixture secondFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 21,
                snapshotId: "snapshot-21");
        VerifiedAuthoringSnapshotPair firstPair =
            await firstFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair secondPair =
            await secondFixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "rollback");
        await new TicketSitePublisher().PublishAsync(Request(firstPair, output));
        TicketSitePublisher failing = new(new TicketSitePublisherTestHooks(
            BeforeStageValidationAsync: (_, _) =>
                throw new IOException("simulated staged validation failure")));

        await Assert.ThrowsAsync<TicketSitePublishException>(
            () => failing.PublishAsync(Request(secondPair, output)));

        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName));
        Assert.Equal("snapshot-20", manifest.SnapshotId);
    }

    [Fact]
    public async Task CleanupFailureBecomesSuccessfulResultWarning()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string? deferredPath = null;
        TicketSitePublisher publisher = new(new TicketSitePublisherTestHooks(
            DeleteFilteredDatabaseAsync: path =>
            {
                deferredPath = path;
                throw new IOException("simulated cleanup failure");
            }));

        try
        {
            TicketSitePublishResult result = await publisher.PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "cleanup-warning"),
                    "Tickets",
                    new TicketSiteFilters(Specification: "FHIR")));

            Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains(
                    "deferred cleanup",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (deferredPath is not null)
            {
                TestFileCleanup.SafeDeleteFile(deferredPath);
            }
        }
    }

    [Fact]
    public async Task CleanupFailureIsObservableWhenPublicationFails()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string? deferredPath = null;
        List<string> observedWarnings = [];
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                DeleteFilteredDatabaseAsync: path =>
                {
                    deferredPath = path;
                    throw new IOException("simulated cleanup failure");
                },
                BeforeStageValidationAsync: (_, _) =>
                    throw new IOException("simulated publication failure")),
            observedWarnings.Add);

        try
        {
            await Assert.ThrowsAsync<TicketSitePublishException>(
                () => publisher.PublishAsync(
                    new TicketSitePublishRequest(
                        pair,
                        TicketSiteKind.Discussion,
                        Path.Combine(_root, "failed-cleanup-warning"),
                        "Tickets",
                        new TicketSiteFilters(Specification: "FHIR"))));

            Assert.Contains(
                observedWarnings,
                warning => warning.Contains(
                    "deferred cleanup",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (deferredPath is not null)
            {
                TestFileCleanup.SafeDeleteFile(deferredPath);
            }
        }
    }

    [Fact]
    public async Task CleanupFailureIsObservableWhenPublicationIsCanceled()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string? deferredPath = null;
        List<string> observedWarnings = [];
        using CancellationTokenSource cancellation = new();
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                DeleteFilteredDatabaseAsync: path =>
                {
                    deferredPath = path;
                    throw new IOException("simulated cleanup failure");
                },
                BeforeStageValidationAsync: (_, token) =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
            observedWarnings.Add);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => publisher.PublishAsync(
                    new TicketSitePublishRequest(
                        pair,
                        TicketSiteKind.Discussion,
                        Path.Combine(_root, "canceled-cleanup-warning"),
                        "Tickets",
                        new TicketSiteFilters(Specification: "FHIR")),
                    cancellation.Token));

            Assert.Contains(
                observedWarnings,
                warning => warning.Contains(
                    "deferred cleanup",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (deferredPath is not null)
            {
                TestFileCleanup.SafeDeleteFile(deferredPath);
            }
        }
    }

    [Fact]
    public async Task ConcurrentSiteKindsSerializeChooserAndPreserveBothStates()
    {
        TicketSnapshotFixture preparerFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        TicketSnapshotFixture plannerFixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair preparerPair =
            await preparerFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair plannerPair =
            await plannerFixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "concurrent-chooser");
        object counterGate = new();
        int activeChooserWriters = 0;
        int maximumChooserWriters = 0;
        TicketSitePublisherTestHooks hooks = new(
            BeforeChooserCommitAsync: async (_, _, token) =>
            {
                lock (counterGate)
                {
                    activeChooserWriters++;
                    maximumChooserWriters = Math.Max(
                        maximumChooserWriters,
                        activeChooserWriters);
                }
                try
                {
                    await Task.Delay(75, token);
                }
                finally
                {
                    lock (counterGate)
                    {
                        activeChooserWriters--;
                    }
                }
            });

        Task<TicketSitePublishResult> discussion =
            new TicketSitePublisher(hooks).PublishAsync(
                Request(preparerPair, output));
        Task<TicketSitePublishResult> applying =
            new TicketSitePublisher(hooks).PublishAsync(
                new TicketSitePublishRequest(
                    plannerPair,
                    TicketSiteKind.Applying,
                    output,
                    "Tickets for Applying"));
        await Task.WhenAll(discussion, applying);

        Assert.Equal(1, maximumChooserWriters);
        string chooser = await File.ReadAllTextAsync(
            Path.Combine(output, "index.html"));
        Assert.Contains(
            "card card-discussion live",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "card card-applying live",
            chooser,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicationLockStaysInsideCallerOwnedOutputRoot()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string parent = Path.Combine(_root, "read-only-parent");
        string output = Path.Combine(parent, "site");
        Directory.CreateDirectory(output);
        string expectedLockPath = Path.Combine(
            output,
            TicketSiteOutputRootLock.LockFileName);
        int openAttempts = 0;
        TicketSitePublisher publisher = new(new TicketSitePublisherTestHooks(
            OpenOutputRootLockFile: path =>
            {
                openAttempts++;
                if (!string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(expectedLockPath),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException(
                        "The simulated read-only parent rejects lock files.");
                }

                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }));

        TicketSitePublishResult result = await publisher.PublishAsync(
            Request(pair, output));

        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.Equal(1, openAttempts);
        Assert.True(File.Exists(expectedLockPath));
        Assert.Equal(
            [output],
            Directory.EnumerateFileSystemEntries(parent).ToArray());
    }

    [Fact]
    public async Task PermanentOutputRootLockIoFailureIsNotRetried()
    {
        string output = Path.Combine(_root, "permanent-lock-failure");
        int openAttempts = 0;

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => TicketSiteOutputRootLock.AcquireAsync(
                    output,
                    CancellationToken.None,
                    path =>
                    {
                        openAttempts++;
                        Assert.True(Directory.Exists(output));
                        throw new IOException(
                            "simulated disk-full failure",
                            unchecked((int)0x80070070));
                    })
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(1, openAttempts);
        Assert.Contains(output, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            TicketSiteOutputRootLock.LockFileName,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "simulated disk-full failure",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChooserFailureAfterCommitReturnsSuccessAndReplacesNothing()
    {
        TicketSnapshotFixture preparerFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        TicketSnapshotFixture plannerFixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair preparerPair =
            await preparerFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair plannerPair =
            await plannerFixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "chooser-failure");
        await new TicketSitePublisher().PublishAsync(
            Request(preparerPair, output));
        string chooserPath = Path.Combine(output, "index.html");
        string chooserCssPath = Path.Combine(output, "assets", "chooser.css");
        string originalChooser = await File.ReadAllTextAsync(chooserPath);
        string originalCss = await File.ReadAllTextAsync(chooserCssPath);
        List<string> observedWarnings = [];
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                BeforeChooserCommitAsync: (_, _, _) =>
                    throw new IOException("simulated chooser failure")),
            observedWarnings.Add);

        TicketSitePublishResult result = await publisher.PublishAsync(
            new TicketSitePublishRequest(
                plannerPair,
                TicketSiteKind.Applying,
                output,
                "Tickets for Applying"));

        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "index.html")));
        Assert.Equal(originalChooser, await File.ReadAllTextAsync(chooserPath));
        Assert.Equal(originalCss, await File.ReadAllTextAsync(chooserCssPath));
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains(
                "chooser",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            observedWarnings,
            warning => warning.Contains(
                "chooser",
                StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(
            output,
            "*.tmp-*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationAfterSiteCommitDoesNotHideSuccess()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "post-commit-cancellation");
        using CancellationTokenSource cancellation = new();
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                BeforeChooserCommitAsync: (_, _, _) =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }));

        TicketSitePublishResult result = await publisher.PublishAsync(
            Request(pair, output),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.True(File.Exists(Path.Combine(output, "discussion", "index.html")));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task UnownedSiteRequiresForceBeforeTakeover()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "unowned");
        string discussion = Path.Combine(output, "discussion");
        Directory.CreateDirectory(discussion);
        await File.WriteAllTextAsync(
            Path.Combine(discussion, "index.html"),
            "unrelated");
        TicketSitePublisher publisher = new();

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => publisher.PublishAsync(Request(pair, output)));
        Assert.Contains(
            "not owned",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        TicketSitePublishResult forced = await publisher.PublishAsync(
            Request(pair, output) with { Force = true });
        Assert.Equal(TicketSitePublishOutcome.Published, forced.Outcome);
        Assert.True(File.Exists(Path.Combine(
            discussion,
            TicketSiteManifest.FileName)));
    }

    [Fact]
    public async Task PublicationCanRetryFromSameAlreadyVerifiedPair()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "retry");
        TicketSitePublisher failing = new(new TicketSitePublisherTestHooks(
            BeforeStageValidationAsync: (_, _) =>
                throw new IOException("first attempt failed")));

        await Assert.ThrowsAsync<TicketSitePublishException>(
            () => failing.PublishAsync(Request(pair, output)));
        TicketSitePublishResult retried =
            await new TicketSitePublisher().PublishAsync(Request(pair, output));

        Assert.Equal(TicketSitePublishOutcome.Published, retried.Outcome);
        Assert.Equal(pair.SnapshotId, retried.Manifest.SnapshotId);
    }

    [Fact]
    public async Task OutputCannotContainOrBeContainedByInputPair()
    {
        string output = Path.Combine(_root, "contained");
        Directory.CreateDirectory(output);
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(output);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, output)));

        Assert.Contains(
            "publisher control",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        TicketSnapshotFixture outside =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair outsidePair =
            await outside.CreateVerifiedPairAsync("Preparer");
        string nestedOutput = Path.Combine(
            outsidePair.DirectoryPath,
            "site");
        TicketSitePublishException nested = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(outsidePair, nestedOutput)));
        Assert.Equal(TicketSitePublishFailure.InvalidRequest, nested.Failure);
    }

    [Fact]
    public async Task CancellationStopsBeforePublication()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "canceled");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, output),
                cancellation.Token));

        Assert.False(Directory.Exists(output));
    }

    private static TicketSitePublishRequest Request(
        VerifiedAuthoringSnapshotPair pair,
        string output)
        => new(
            pair,
            TicketSiteKind.Discussion,
            output,
            "Tickets");

    private static async Task<VerifiedAuthoringSnapshotPair>
        MutateAndReverifyAsync(
            VerifiedAuthoringSnapshotPair pair,
            string sql)
    {
        await using (SqliteConnection connection = new(
            $"Data Source={pair.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        string databaseSha256 = await ComputeHashAsync(pair.DatabasePath);
        AuthoringSnapshotDescriptor descriptor = pair.Descriptor with
        {
            Sha256 = databaseSha256,
            SizeBytes = new FileInfo(pair.DatabasePath).Length,
        };
        await File.WriteAllTextAsync(
            pair.DescriptorPath,
            JsonSerializer.Serialize(descriptor, JsonOptions));
        byte[] descriptorBytes =
            await File.ReadAllBytesAsync(pair.DescriptorPath);
        AuthoringSnapshotPairManifest manifest = pair.Manifest with
        {
            SizeBytes = descriptor.SizeBytes,
            DescriptorSha256 =
                Convert.ToHexString(SHA256.HashData(descriptorBytes))
                    .ToLowerInvariant(),
            DatabaseSha256 = databaseSha256,
        };
        await File.WriteAllTextAsync(
            pair.ReadyManifestPath!,
            JsonSerializer.Serialize(manifest, JsonOptions));
        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                pair.ServiceName,
                pair.RunId,
                pair.DirectoryPath);
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }
}
