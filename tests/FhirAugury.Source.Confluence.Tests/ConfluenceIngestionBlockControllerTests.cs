using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Controllers;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins the operator control plane: the block is visible, clearable, and refuses
/// exactly the two network ingestion entry points — no more and no less.
/// </summary>
/// <remarks>
/// The un-gated assertions matter as much as the gated ones. The block is about
/// outgoing HTTP, so a cache rebuild must stay available; a gate that took the
/// whole service down would be a worse outage than the one it is preventing.
/// </remarks>
public class ConfluenceIngestionBlockControllerTests : IDisposable
{
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly FileSystemResponseCache _cache;
    private readonly ConfluenceDatabase _database;
    private readonly ConfluenceIngestionGate _gate;

    public ConfluenceIngestionBlockControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-blockctl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _cache = new FileSystemResponseCache(Path.Combine(_root, "cache"));
        _dbPath = Path.Combine(_root, "confluence.db");
        _database = new ConfluenceDatabase(_dbPath, NullLogger<ConfluenceDatabase>.Instance);
        _database.Initialize();
        _gate = new ConfluenceIngestionGate(_database, NullLogger<ConfluenceIngestionGate>.Instance);
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
        new(405, "Not Allowed", "captcha", $"{BaseUrl}/rest/api/content");

    private IOptions<ConfluenceServiceOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new ConfluenceServiceOptions
        {
            BaseUrl = BaseUrl,
            CachePath = _cache.RootPath,
            DatabasePath = _dbPath,
            OrchestratorAddress = string.Empty,
        });

    private ConfluenceSource CreateSource()
    {
        IOptions<ConfluenceServiceOptions> options = Options();

        ConfluenceFetch fetch = (_, _) => throw Challenge();

        return new ConfluenceSource(
            options,
            _database,
            _cache,
            new ConfluenceSpaceDiscovery(options, _cache, NullLogger<ConfluenceSpaceDiscovery>.Instance, fetch),
            new ConfluenceSweep(options, _cache, NullLogger<ConfluenceSweep>.Instance, fetch),
            NullLogger<ConfluenceSource>.Instance,
            fetch);
    }

    private ConfluenceIndexer CreateIndexer() =>
        new(_database,
            new AuxiliaryDatabase(new AuxiliaryDatabaseOptions(), NullLogger<AuxiliaryDatabase>.Instance),
            new Bm25Options(),
            NullLogger<ConfluenceIndexer>.Instance);

    private ConfluenceIngestionPipeline CreatePipeline() =>
        new(CreateSource(),
            _database,
            CreateIndexer(),
            new ConfluenceXRefRebuilder(_database, NullLogger<ConfluenceXRefRebuilder>.Instance),
            new IndexTracker(),
            new ThrowingHttpClientFactory(),
            _gate,
            Options(),
            NullLogger<ConfluenceIngestionPipeline>.Instance);

    private IngestionBlockController BlockController() => new(_gate);

    private IngestionController IngestionController() =>
        new(CreatePipeline(),
            new IngestionWorkQueue(new StubLifetime(), NullLogger<IngestionWorkQueue>.Instance),
            _database,
            CreateIndexer(),
            new ConfluenceXRefRebuilder(_database, NullLogger<ConfluenceXRefRebuilder>.Instance),
            new ConfluenceLinkRebuilder(_database, NullLogger<ConfluenceLinkRebuilder>.Instance),
            new IndexTracker(),
            _gate);

    private LifecycleController LifecycleController() =>
        new(CreatePipeline(),
            CreateSource(),
            _database,
            _cache,
            new IndexTracker(),
            _gate);

    private static T Body<T>(IActionResult result) => (T)((ObjectResult)result).Value!;

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException($"the controller tests must not create the '{name}' HTTP client");
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    // ── The block endpoint ────────────────────────────────────────────

    [Fact]
    public void GetIngestionBlock_ReportsOpenThenBlocked()
    {
        IngestionBlockResponse open = Body<IngestionBlockResponse>(BlockController().GetIngestionBlock());

        Assert.False(open.Blocked);
        Assert.Null(open.BlockedAt);
        Assert.Contains("ingestion-block/clear", open.Remediation, StringComparison.Ordinal);

        _gate.Block(Challenge());

        IngestionBlockResponse blocked = Body<IngestionBlockResponse>(BlockController().GetIngestionBlock());

        Assert.True(blocked.Blocked);
        Assert.NotNull(blocked.BlockedAt);
        Assert.Equal(405, blocked.HttpStatus);
        Assert.Equal("Not Allowed", blocked.ReasonPhrase);
        Assert.Contains("x-amzn-waf-action", blocked.Fingerprint!, StringComparison.Ordinal);
        Assert.Equal($"{BaseUrl}/rest/api/content", blocked.RequestUrl);
    }

    [Fact]
    public void ClearIngestionBlock_ReopensAndReportsWasBlocked()
    {
        _gate.Block(Challenge());

        IngestionBlockClearResponse cleared =
            Body<IngestionBlockClearResponse>(BlockController().ClearIngestionBlock("gino"));

        Assert.True(cleared.WasBlocked);
        Assert.False(cleared.Blocked);
        Assert.Equal("gino", cleared.ClearedBy);

        // Clearing an open gate is reported, not refused.
        IngestionBlockClearResponse again =
            Body<IngestionBlockClearResponse>(BlockController().ClearIngestionBlock(null));

        Assert.False(again.WasBlocked);
        Assert.False(again.Blocked);
    }

    // ── The gated ingestion entry points ──────────────────────────────

    [Fact]
    public async Task TriggerIngestion_ReturnsPreconditionFailedWhileBlocked()
    {
        _gate.Block(Challenge());

        ObjectResult result =
            (ObjectResult)await IngestionController().TriggerIngestion("incremental", CancellationToken.None);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, result.StatusCode);
    }

    [Fact]
    public void QueueIngestion_ReturnsPreconditionFailedWhileBlockedAnd202WhenOpen()
    {
        Assert.IsType<AcceptedResult>(IngestionController().QueueIngestion("incremental"));

        _gate.Block(Challenge());

        ObjectResult refused = (ObjectResult)IngestionController().QueueIngestion("incremental");

        Assert.Equal(StatusCodes.Status412PreconditionFailed, refused.StatusCode);
    }

    [Fact]
    public async Task RebuildFromCache_IsNotBlocked()
    {
        _gate.Block(Challenge());

        // Cache-only work never leaves the process, so the gate must not touch it.
        IActionResult result = await IngestionController().RebuildFromCache(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Status and health ─────────────────────────────────────────────

    [Fact]
    public void GetStatus_ReportsTheBlockAndItsRecord()
    {
        _gate.Block(Challenge());

        IngestionStatusResponse status = Body<IngestionStatusResponse>(LifecycleController().GetStatus());

        Assert.Equal(ConfluenceIngestionGate.BlockedStatus, status.Status);
        Assert.Contains("ingestion-block/clear", status.LastError!, StringComparison.Ordinal);
        Assert.NotNull(status.AdditionalData);
        Assert.True(status.AdditionalData.ContainsKey("ingestionBlock"));
        Assert.Equal(405, status.AdditionalData["ingestionBlock"].GetProperty("HttpStatus").GetInt32());
    }

    [Fact]
    public void GetHealth_DegradesWhileBlockedAndRecoversOnClear()
    {
        Assert.Equal("healthy", Body<HealthCheckResponse>(LifecycleController().GetHealth()).Status);

        _gate.Block(Challenge());

        HealthCheckResponse degraded = Body<HealthCheckResponse>(LifecycleController().GetHealth());
        Assert.Equal("degraded", degraded.Status);
        Assert.Contains("blocked", degraded.Message!, StringComparison.OrdinalIgnoreCase);

        _gate.Clear("gino");

        Assert.Equal("healthy", Body<HealthCheckResponse>(LifecycleController().GetHealth()).Status);
    }
}
