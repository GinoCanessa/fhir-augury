using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// The regression pin for the reported defect: an edge challenge must stop the
/// run at the first request instead of walking the work list and recording
/// thousands of per-item failures.
/// </summary>
/// <remarks>
/// Two layers. The source layer proves the acquisition loop aborts; the pipeline
/// layer proves the abort is recorded durably and that the <em>next</em> run is
/// refused without touching the network — which is what "wait for a human"
/// actually means.
/// </remarks>
public class ConfluenceIngestionBlockTests : IDisposable
{
    private const string Space = "FHIR";
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly FileSystemResponseCache _cache;
    private readonly ConfluenceDatabase _database;

    public ConfluenceIngestionBlockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-block-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _cache = new FileSystemResponseCache(Path.Combine(_root, "cache"));
        _dbPath = Path.Combine(_root, "confluence.db");
        _database = new ConfluenceDatabase(_dbPath, NullLogger<ConfluenceDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────

    private static ConfluenceHumanInterventionRequiredException Challenge() =>
        new(405, "Not Allowed", "captcha", $"{BaseUrl}/rest/api/space");

    private IOptions<ConfluenceServiceOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new ConfluenceServiceOptions
        {
            BaseUrl = BaseUrl,
            CachePath = _cache.RootPath,
            DatabasePath = _dbPath,
            Spaces = [Space],
            // Blank, so NotifyOrchestratorAsync returns before touching HTTP.
            OrchestratorAddress = string.Empty,
        });

    /// <summary>A source whose every fetch answers with the edge challenge.</summary>
    private ConfluenceSource CreateSource(Func<int> onFetch)
    {
        IOptions<ConfluenceServiceOptions> options = Options();

        ConfluenceFetch fetch = (_, _) =>
        {
            onFetch();
            throw Challenge();
        };

        return new ConfluenceSource(
            options,
            _database,
            _cache,
            new ConfluenceSpaceDiscovery(options, _cache, NullLogger<ConfluenceSpaceDiscovery>.Instance, fetch),
            new ConfluenceSweep(options, _cache, NullLogger<ConfluenceSweep>.Instance, fetch),
            NullLogger<ConfluenceSource>.Instance,
            fetch);
    }

    private ConfluenceIngestionPipeline CreatePipeline(ConfluenceSource source, ConfluenceIngestionGate gate)
    {
        IOptions<ConfluenceServiceOptions> options = Options();

        return new ConfluenceIngestionPipeline(
            source,
            _database,
            new ConfluenceIndexer(
                _database,
                new AuxiliaryDatabase(new AuxiliaryDatabaseOptions(), NullLogger<AuxiliaryDatabase>.Instance),
                new Bm25Options(),
                NullLogger<ConfluenceIndexer>.Instance),
            new ConfluenceXRefRebuilder(_database, NullLogger<ConfluenceXRefRebuilder>.Instance),
            new IndexTracker(),
            new ThrowingHttpClientFactory(),
            gate,
            options,
            NullLogger<ConfluenceIngestionPipeline>.Instance);
    }

    private ConfluenceIngestionGate Gate() =>
        new(_database, NullLogger<ConfluenceIngestionGate>.Instance);

    /// <summary>A blocked run must never reach the network, orchestrator included.</summary>
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException($"a blocked run must not create the '{name}' HTTP client");
    }

    // ── Source layer ──────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_ChallengeStopsAfterOneRequest()
    {
        int calls = 0;
        ConfluenceSource source = CreateSource(() => ++calls);

        await Assert.ThrowsAsync<ConfluenceHumanInterventionRequiredException>(
            () => source.ReconcileAsync(source.BuildPolicy(), CancellationToken.None));

        // Not "one request per manifest entry, all failing" — one request, then
        // the run ended.
        Assert.Equal(1, calls);
    }

    // ── Pipeline layer ────────────────────────────────────────────────

    [Fact]
    public async Task Pipeline_ChallengeRecordsTheBlockAndReportsIt()
    {
        int calls = 0;
        ConfluenceIngestionGate gate = Gate();
        ConfluenceIngestionPipeline pipeline = CreatePipeline(CreateSource(() => ++calls), gate);

        await Assert.ThrowsAsync<ConfluenceHumanInterventionRequiredException>(
            () => pipeline.RunIncrementalIngestionAsync(CancellationToken.None));

        Assert.True(gate.IsBlocked);
        Assert.Equal(ConfluenceIngestionGate.BlockedStatus, pipeline.CurrentStatus);
        Assert.Equal(405, gate.Current!.HttpStatus);
    }

    [Fact]
    public async Task Pipeline_ChallengeWritesTheBlockedSyncStateRows()
    {
        ConfluenceIngestionGate gate = Gate();
        ConfluenceIngestionPipeline pipeline = CreatePipeline(CreateSource(() => 0), gate);

        await Assert.ThrowsAsync<ConfluenceHumanInterventionRequiredException>(
            () => pipeline.RunIncrementalIngestionAsync(CancellationToken.None));

        using SqliteConnection connection = _database.OpenConnection();

        ConfluenceSyncStateRecord run = ConfluenceSyncStateRecord.SelectSingle(
            connection, SourceName: ConfluenceSource.SourceName, SubSource: "incremental")!;
        ConfluenceSyncStateRecord scheduling = ConfluenceSyncStateRecord.SelectSingle(
            connection, SourceName: ConfluenceSource.SourceName,
            SubSource: ConfluenceSource.SchedulingSubSource)!;

        Assert.Equal(ConfluenceIngestionGate.BlockedStatus, run.Status);
        Assert.Equal(ConfluenceIngestionGate.BlockedStatus, scheduling.Status);
        Assert.Contains("ingestion-block/clear", run.LastError!, StringComparison.Ordinal);
        Assert.Null(scheduling.NextScheduledAt);
    }

    [Fact]
    public async Task Pipeline_SecondRunIsRefusedWithoutTouchingTheNetwork()
    {
        int calls = 0;
        ConfluenceIngestionGate gate = Gate();
        ConfluenceIngestionPipeline pipeline = CreatePipeline(CreateSource(() => ++calls), gate);

        await Assert.ThrowsAsync<ConfluenceHumanInterventionRequiredException>(
            () => pipeline.RunIncrementalIngestionAsync(CancellationToken.None));

        int afterFirstRun = calls;

        ConfluenceIngestionBlockedException refused =
            await Assert.ThrowsAsync<ConfluenceIngestionBlockedException>(
                () => pipeline.RunIncrementalIngestionAsync(CancellationToken.None));

        Assert.Equal(afterFirstRun, calls);
        Assert.Equal(405, refused.Block.HttpStatus);

        // And the full run is refused on the same terms.
        await Assert.ThrowsAsync<ConfluenceIngestionBlockedException>(
            () => pipeline.RunFullIngestionAsync(CancellationToken.None));
        Assert.Equal(afterFirstRun, calls);
    }

    [Fact]
    public async Task Pipeline_RunsAgainOnceTheBlockIsCleared()
    {
        int calls = 0;
        ConfluenceIngestionGate gate = Gate();
        ConfluenceIngestionPipeline pipeline = CreatePipeline(CreateSource(() => ++calls), gate);

        await Assert.ThrowsAsync<ConfluenceHumanInterventionRequiredException>(
            () => pipeline.RunIncrementalIngestionAsync(CancellationToken.None));

        Assert.True(gate.Clear("test"));

        // Still challenged, so it stops again — but it did try, which is the
        // point: clearing reopens the gate rather than the fetch succeeding.
        await Assert.ThrowsAsync<ConfluenceHumanInterventionRequiredException>(
            () => pipeline.RunIncrementalIngestionAsync(CancellationToken.None));

        Assert.Equal(2, calls);
    }
}
