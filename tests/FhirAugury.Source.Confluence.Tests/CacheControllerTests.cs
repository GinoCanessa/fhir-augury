using System.Reflection;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Controllers;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins that the completeness verdict is answerable with no network, that a
/// never-swept space reports <c>unknown</c> rather than <c>complete</c>, and
/// that a concurrent atomic manifest replacement does not break either side.
/// </summary>
public class CacheControllerTests : IDisposable
{
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly FileSystemResponseCache _cache;
    private readonly ConfluenceDatabase _database;

    public CacheControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-cachectl-{Guid.NewGuid():N}");
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

    private IOptions<ConfluenceServiceOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new ConfluenceServiceOptions
        {
            BaseUrl = BaseUrl,
            CachePath = _cache.RootPath,
            DatabasePath = _dbPath,
        });

    /// <summary>
    /// The source is built with fetches that throw, so any network use fails the
    /// test rather than passing quietly.
    /// </summary>
    private CacheController CreateController()
    {
        IOptions<ConfluenceServiceOptions> options = Options();

        ConfluenceSource source = new(
            options,
            _database,
            _cache,
            new ConfluenceSpaceDiscovery(options, _cache, NullLogger<ConfluenceSpaceDiscovery>.Instance,
                (_, _) => throw new InvalidOperationException("the report must not fetch")),
            new ConfluenceSweep(options, _cache, NullLogger<ConfluenceSweep>.Instance,
                (_, _) => throw new InvalidOperationException("the report must not fetch")),
            NullLogger<ConfluenceSource>.Instance,
            (_, _) => throw new InvalidOperationException("the report must not fetch"));

        return new CacheController(source, options);
    }

    private void Write(string key, string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void WriteCatalog(params string[] keys) =>
        Write(ConfluenceCacheLayout.GetSpaceCatalogCacheKey(), new ConfluenceSpaceCatalog
        {
            DiscoveredAt = DateTimeOffset.UtcNow,
            Complete = true,
            Spaces = [.. keys.Select(k => new ConfluenceCatalogedSpace { Key = k, Name = k })],
        }.ToJson());

    private void WriteManifest(string spaceKey, params ConfluenceManifestEntry[] entries) =>
        Write(ConfluenceCacheLayout.GetManifestCacheKey(spaceKey), new ConfluenceManifest
        {
            SpaceKey = spaceKey,
            Profiles = ConfluenceManifestProfiles.Current,
            SweptAt = DateTimeOffset.UtcNow,
            Complete = true,
            Entries = [.. entries],
        }.ToJson());

    private void WritePageArtifact(string spaceKey, string id)
    {
        using JsonDocument payload = JsonDocument.Parse($$"""{"id":"{{id}}","title":"Page"}""");
        Write(ConfluenceCacheLayout.GetPageCacheKey(spaceKey, id),
            ConfluenceCachedArtifact.Wrap(payload.RootElement, ContentTypes.Page, spaceKey, 1).ToJson());
    }

    private static ConfluenceManifestEntry PageEntry(string id) =>
        new() { Id = id, Type = ContentTypes.Page, Title = $"Page {id}", Version = 1 };

    private static JsonElement Report(IActionResult result)
    {
        object value = Assert.IsType<OkObjectResult>(result).Value!;
        return JsonSerializer.SerializeToElement(value);
    }

    private static JsonElement SpaceReport(JsonElement report, string spaceKey) =>
        report.GetProperty("spaces").EnumerateArray()
            .Single(s => s.GetProperty("spaceKey").GetString() == spaceKey);

    // ── Verdicts ──────────────────────────────────────────────────────

    [Fact]
    public void Report_DistinguishesACompleteSpaceFromANeverSweptOne()
    {
        WriteCatalog("FHIR", "SOA");
        WritePageArtifact("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));
        // SOA is catalogued but has never been swept.

        JsonElement report = Report(CreateController().GetReconcileReport(null));

        Assert.Equal("complete", SpaceReport(report, "FHIR").GetProperty("verdict").GetString());
        Assert.Equal("unknown", SpaceReport(report, "SOA").GetProperty("verdict").GetString());
        Assert.Equal("unknown", report.GetProperty("overallVerdict").GetString());
    }

    [Fact]
    public void Report_AGoodManifestWithALaterFailedSweep_IsUnknown()
    {
        WriteCatalog("FHIR");
        WritePageArtifact("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));
        Write(ConfluenceCacheLayout.GetSweepAttemptCacheKey("FHIR"), new ConfluenceSweepAttempt
        {
            SpaceKey = "FHIR",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(5),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(6),
            Outcome = ConfluenceSweepOutcome.Failed,
            Error = "connection reset",
        }.ToJson());

        JsonElement space = SpaceReport(Report(CreateController().GetReconcileReport(null)), "FHIR");

        Assert.Equal("unknown", space.GetProperty("verdict").GetString());
        Assert.Contains("connection reset", space.GetProperty("unknownReason").GetString());
        Assert.Equal("failed", space.GetProperty("lastSweepOutcome").GetString());
    }

    [Fact]
    public void Report_NamesMissingIdsAndHonoursTheSampleSize()
    {
        WriteCatalog("FHIR");
        WriteManifest("FHIR", PageEntry("100"), PageEntry("101"), PageEntry("102"));

        JsonElement full = SpaceReport(Report(CreateController().GetReconcileReport(null)), "FHIR");
        Assert.Equal("partial", full.GetProperty("verdict").GetString());
        Assert.Equal(3, full.GetProperty("missing").GetInt32());
        Assert.Equal(3, full.GetProperty("missingIds").GetArrayLength());

        JsonElement capped = SpaceReport(Report(CreateController().GetReconcileReport(1)), "FHIR");
        Assert.Equal(1, capped.GetProperty("missingIds").GetArrayLength());

        JsonElement none = SpaceReport(Report(CreateController().GetReconcileReport(0)), "FHIR");
        Assert.Equal(0, none.GetProperty("missingIds").GetArrayLength());
    }

    [Fact]
    public void Report_CarriesTheProfilesAndManifestAge()
    {
        WriteCatalog("FHIR");
        WritePageArtifact("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));

        JsonElement space = SpaceReport(Report(CreateController().GetReconcileReport(null)), "FHIR");

        Assert.Equal(ConfluenceCacheLayout.PageProfile,
            space.GetProperty("profiles").GetProperty("page").GetString());
        Assert.NotEqual(JsonValueKind.Null, space.GetProperty("manifestAge").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, space.GetProperty("sweptAt").ValueKind);
    }

    [Fact]
    public void Report_WithNoCatalog_IsUnknownRatherThanEmptyAndComplete()
    {
        JsonElement report = Report(CreateController().GetReconcileReport(null));

        Assert.Empty(report.GetProperty("spaces").EnumerateArray());
        Assert.Equal("unknown", report.GetProperty("overallVerdict").GetString());
    }

    [Fact]
    public void Report_AnsweredWithNoHttpClientConfiguredAtAll()
    {
        // CacheController takes no IHttpClientFactory, and the source it uses
        // is wired with throwing fetches. Reaching this assertion is the proof.
        WriteCatalog("FHIR");
        WriteManifest("FHIR");

        JsonElement report = Report(CreateController().GetReconcileReport(null));

        Assert.Equal("complete", report.GetProperty("overallVerdict").GetString());
    }

    // ── Concurrency ───────────────────────────────────────────────────

    [Fact]
    public async Task Report_SurvivesAConcurrentAtomicManifestReplacement()
    {
        // FileShare.Delete on the reader is what lets AtomicFileWriter's
        // File.Move(..., overwrite: true) land while the report is reading —
        // otherwise a mid-run report could break the very sweep it reports on.
        WriteCatalog("FHIR");
        for (int i = 0; i < 50; i++) WritePageArtifact("FHIR", i.ToString());
        WriteManifest("FHIR", [.. Enumerable.Range(0, 50).Select(i => PageEntry(i.ToString()))]);

        using CancellationTokenSource cts = new();

        Task writer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                WriteManifest("FHIR", [.. Enumerable.Range(0, 50).Select(i => PageEntry(i.ToString()))]);
                await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None);

        CacheController controller = CreateController();

        for (int i = 0; i < 40; i++)
        {
            JsonElement report = Report(controller.GetReconcileReport(null));
            Assert.NotEqual(JsonValueKind.Undefined, report.GetProperty("spaces").ValueKind);
        }

        await cts.CancelAsync();
        await writer;
    }

    // ── Route shape ───────────────────────────────────────────────────

    [Fact]
    public void RouteShape_IsApiV1CacheReconcileReport()
    {
        RouteAttribute prefix = typeof(CacheController).GetCustomAttribute<RouteAttribute>()!;
        HttpGetAttribute action = typeof(CacheController)
            .GetMethod(nameof(CacheController.GetReconcileReport))!
            .GetCustomAttribute<HttpGetAttribute>()!;

        Assert.Equal("api/v1/cache", prefix.Template);
        Assert.Equal("reconcile-report", action.Template);
    }
}
