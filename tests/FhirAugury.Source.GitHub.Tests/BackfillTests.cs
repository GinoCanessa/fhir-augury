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

    internal static GitHubIngestionPipeline CreatePipeline(GitHubDatabase database)
    {
        GitHubServiceOptions options = new()
        {
            SyncSchedule = "01:00:00",
            FhirCoreRepositories = ["HL7/fhir", "HL7/us-core"],
            UtgRepositories = [],
            FhirExtensionsPackRepositories = [],
        };

        return new GitHubIngestionPipeline(
            source: null!,
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
