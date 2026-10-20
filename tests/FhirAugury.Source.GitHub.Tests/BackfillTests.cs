using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 3 (slot 0626-02): the gh-CLI history-backfill arg builder drops the
/// <c>-S "updated:&gt;="</c> bound and uses the configured BackfillLimit, while
/// the incremental path keeps it.
/// </summary>
public class GitHubCliBackfillArgsTests
{
    [Theory]
    [InlineData("issue list", GitHubCliProvider.IssueListFields)]
    [InlineData("pr list", GitHubCliProvider.PrListFields)]
    public void BackfillArgs_DropUpdatedFilter_AndUseBackfillLimit(string command, string fields)
    {
        string args = GitHubCliProvider.BuildListArgs(command, "--repo HL7/fhir", limit: 5000, searchFilter: null, fields);

        Assert.Contains("--state all", args);
        Assert.Contains("--limit 5000", args);
        Assert.DoesNotContain("-S \"updated:", args);
        Assert.Contains(command, args);
    }

    [Theory]
    [InlineData("issue list", GitHubCliProvider.IssueListFields)]
    [InlineData("pr list", GitHubCliProvider.PrListFields)]
    public void IncrementalArgs_KeepUpdatedFilter(string command, string fields)
    {
        string args = GitHubCliProvider.BuildListArgs(command, "--repo HL7/fhir", limit: 1000, searchFilter: "updated:>=2026-01-01T00:00:00Z", fields);

        Assert.Contains("--state all", args);
        Assert.Contains("--limit 1000", args);
        Assert.Contains("-S \"updated:>=2026-01-01T00:00:00Z\"", args);
    }
}

/// <summary>
/// Phase 3 (slot 0626-02): per-repo backfill marker gating and the operational
/// sync-state read guard (backfill markers must never satisfy "last sync").
/// </summary>
public class BackfillMarkerGatingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly GitHubIngestionPipeline _pipeline;

    public BackfillMarkerGatingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"backfill_gate_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _pipeline = CreatePipeline(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    internal static GitHubIngestionPipeline CreatePipeline(
        GitHubDatabase database,
        IGitHubDataProvider? source = null)
    {
        GitHubServiceOptions options = new()
        {
            SyncSchedule = "01:00:00",
            FhirCoreRepositories = ["HL7/fhir", "HL7/us-core"],
            UtgRepositories = [],
            FhirExtensionsPackRepositories = [],
        };

        return new GitHubIngestionPipeline(
            source: source!,
            database: database,
            indexer: null!,
            cloner: null!,
            commitExtractor: null!,
            fileContentIndexer: null!,
            canonicalArtifactIndexer: null!,
            structureDefinitionIndexer: null!,
            fshArtifactIndexer: null!,
            categoryStrategies: [],
            weightResolver: null!,
            xrefRebuilder: null!,
            prTicketLinkRebuilder: null!,
            httpClientFactory: null!,
            tracker: null!,
            optionsAccessor: Options.Create(options),
            checkpointStore: new GitHubBackfillCheckpointStore(
                database, NullLogger<GitHubBackfillCheckpointStore>.Instance),
            workGroupAcquirer: null!,
            workGroupIndexer: null!,
            workGroupResolver: null!,
            workGroupResolutionPass: null!,
            logger: NullLogger<GitHubIngestionPipeline>.Instance);
    }

    private void SeedSyncState(string subSource, DateTimeOffset lastSyncAt)
    {
        using SqliteConnection connection = _db.OpenConnection();
        GitHubSyncStateRecord.Insert(connection, new GitHubSyncStateRecord
        {
            Id = GitHubSyncStateRecord.GetIndex(),
            SourceName = IGitHubDataProvider.SourceName,
            SubSource = subSource,
            LastSyncAt = lastSyncAt,
            LastCursor = null,
            ItemsIngested = 0,
            SyncSchedule = null,
            NextScheduledAt = null,
            Status = "success",
            LastError = null,
        });
    }

    [Fact]
    public void GetReposNeedingBackfill_ExcludesMarkedRepo_IncludesUnmarked()
    {
        SeedSyncState("backfill:HL7/fhir", DateTimeOffset.UtcNow);

        List<string> needing = _pipeline.GetReposNeedingBackfill();

        Assert.DoesNotContain("HL7/fhir", needing);
        Assert.Contains("HL7/us-core", needing);
    }

    [Fact]
    public void GetReposNeedingBackfill_NoMarkers_IncludesAll()
    {
        List<string> needing = _pipeline.GetReposNeedingBackfill();

        Assert.Contains("HL7/fhir", needing);
        Assert.Contains("HL7/us-core", needing);
    }
}

/// <summary>
/// A stub <see cref="IGitHubDataProvider"/> that records every backfill request and can
/// report the first repo as cancelled, so the pipeline's cancellation handling can be
/// exercised without a provider or a <c>gh</c> process.
/// </summary>
internal sealed class StubBackfillProvider : IGitHubDataProvider
{
    private readonly List<string?> _backfillCalls = [];
    private readonly List<GitHubBackfillCursor?> _cursors = [];

    /// <summary>Repo filters passed to <see cref="DownloadBackfillAsync"/>, in call order.</summary>
    public IReadOnlyList<string?> BackfillCalls => _backfillCalls;

    /// <summary>Resume cursors passed to <see cref="DownloadBackfillAsync"/>, in call order.</summary>
    public IReadOnlyList<GitHubBackfillCursor?> Cursors => _cursors;

    /// <summary>When true, every returned result is flagged <c>Canceled</c>.</summary>
    public bool ReportCanceled { get; set; }

    public Task<IngestionResult> DownloadBackfillAsync(
        string? repoFilter = null,
        GitHubBackfillCursor? resumeFrom = null,
        CancellationToken ct = default)
    {
        _backfillCalls.Add(repoFilter);
        _cursors.Add(resumeFrom);

        return Task.FromResult(new IngestionResult(3, 3, 0, 0, [], DateTimeOffset.UtcNow)
        {
            Canceled = ReportCanceled,
        });
    }

    public Task<IngestionResult> DownloadAllAsync(string? repoFilter = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>
/// Phase 3 (slot 0826-01): a cancelled backfill is clean-but-incomplete — the repo must not
/// be marked backfilled, and the remaining repo list must be abandoned rather than swept.
/// </summary>
public class BackfillCancellationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public BackfillCancellationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"backfill_cancel_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public async Task BackfillReposAsync_WhenProviderCanceled_DoesNotMarkRepoBackfilled()
    {
        StubBackfillProvider provider = new StubBackfillProvider { ReportCanceled = true };
        GitHubIngestionPipeline pipeline = BackfillMarkerGatingTests.CreatePipeline(_db, provider);

        await pipeline.BackfillReposAsync(["HL7/fhir", "HL7/us-core"], CancellationToken.None);

        List<string> needing = pipeline.GetReposNeedingBackfill();
        Assert.Contains("HL7/fhir", needing);
        Assert.Contains("HL7/us-core", needing);
    }

    [Fact]
    public async Task BackfillReposAsync_WhenProviderCanceled_DoesNotAttemptRemainingRepos()
    {
        StubBackfillProvider provider = new StubBackfillProvider { ReportCanceled = true };
        GitHubIngestionPipeline pipeline = BackfillMarkerGatingTests.CreatePipeline(_db, provider);

        await pipeline.BackfillReposAsync(["HL7/fhir", "HL7/us-core"], CancellationToken.None);

        Assert.Single(provider.BackfillCalls);
        Assert.Equal("HL7/fhir", provider.BackfillCalls[0]);
    }

    [Fact]
    public async Task BackfillReposAsync_WhenNotCanceled_ContinuesToRemainingRepos()
    {
        StubBackfillProvider provider = new StubBackfillProvider { ReportCanceled = false };
        GitHubIngestionPipeline pipeline = BackfillMarkerGatingTests.CreatePipeline(_db, provider);

        await pipeline.BackfillReposAsync(["HL7/fhir", "HL7/us-core"], CancellationToken.None);

        Assert.Equal(2, provider.BackfillCalls.Count);
    }

    /// <summary>
    /// Terminal state moved to the provider in Phase 4 — only it knows whether each phase
    /// enumerated to exhaustion. The pipeline must therefore write no markers of its own.
    /// </summary>
    [Fact]
    public async Task BackfillReposAsync_WritesNoCompletionMarker()
    {
        StubBackfillProvider provider = new StubBackfillProvider { ReportCanceled = false };
        GitHubIngestionPipeline pipeline = BackfillMarkerGatingTests.CreatePipeline(_db, provider);

        await pipeline.BackfillReposAsync(["HL7/fhir", "HL7/us-core"], CancellationToken.None);

        List<string> needing = pipeline.GetReposNeedingBackfill();
        Assert.Contains("HL7/fhir", needing);
        Assert.Contains("HL7/us-core", needing);
    }

    /// <summary>The stored resume cursor must reach the provider, or a resume silently restarts.</summary>
    [Fact]
    public async Task BackfillReposAsync_PassesStoredCursorToProvider()
    {
        GitHubBackfillCheckpointStore store = new GitHubBackfillCheckpointStore(
            _db, NullLogger<GitHubBackfillCheckpointStore>.Instance);

        store.WriteCheckpoint(
            "HL7/fhir",
            new GitHubBackfillCursor { PrsCompletedAbove = 4200, PendingRetry = [4199] },
            itemsIngested: 10,
            lastError: null);

        StubBackfillProvider provider = new StubBackfillProvider { ReportCanceled = false };
        GitHubIngestionPipeline pipeline = BackfillMarkerGatingTests.CreatePipeline(_db, provider);

        await pipeline.BackfillReposAsync(["HL7/fhir"], CancellationToken.None);

        Assert.Single(provider.Cursors);
        Assert.NotNull(provider.Cursors[0]);
        Assert.Equal(4200, provider.Cursors[0]!.PrsCompletedAbove);
        Assert.Equal([4199], provider.Cursors[0]!.PendingRetry);
    }
}

/// <summary>
/// Phase 3 (slot 0626-02): operational sync-state reads must ignore
/// <c>backfill:&lt;repo&gt;</c> marker rows so a backfill marker can never
/// corrupt the incremental window or surface as the reported "last sync".
/// </summary>
public class OperationalSyncStateReadTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public OperationalSyncStateReadTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"op_syncstate_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private void Seed(string subSource, DateTimeOffset lastSyncAt)
    {
        using SqliteConnection connection = _db.OpenConnection();
        GitHubSyncStateRecord.Insert(connection, new GitHubSyncStateRecord
        {
            Id = GitHubSyncStateRecord.GetIndex(),
            SourceName = IGitHubDataProvider.SourceName,
            SubSource = subSource,
            LastSyncAt = lastSyncAt,
            LastCursor = null,
            ItemsIngested = 0,
            SyncSchedule = null,
            NextScheduledAt = null,
            Status = "success",
            LastError = null,
        });
    }

    [Fact]
    public void GetMostRecentOperational_IgnoresNewerBackfillMarker()
    {
        DateTimeOffset incrementalAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset backfillAt = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero); // newer

        Seed("incremental", incrementalAt);
        Seed("backfill:HL7/fhir", backfillAt);

        using SqliteConnection connection = _db.OpenConnection();
        GitHubSyncStateRecord? row = GitHubSyncStateReader.GetMostRecentOperational(connection);

        Assert.NotNull(row);
        Assert.Equal("incremental", row!.SubSource);
        Assert.Equal(incrementalAt, row.LastSyncAt);
    }

    [Fact]
    public void GetLastSyncCompletedAt_ReturnsOperationalTimestamp_NotBackfillMarker()
    {
        DateTimeOffset incrementalAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset backfillAt = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Seed("incremental", incrementalAt);
        Seed("backfill:HL7/fhir", backfillAt);

        GitHubIngestionPipeline pipeline = BackfillMarkerGatingTests.CreatePipeline(_db);

        Assert.Equal(incrementalAt, pipeline.GetLastSyncCompletedAt());
    }
}
