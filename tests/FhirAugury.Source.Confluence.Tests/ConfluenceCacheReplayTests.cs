using System.Text;
using System.Text.Json;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins manifest-driven replay: cache self-sufficiency, idempotency, that
/// tombstones never reach the database, and that a page which disappears is
/// <b>deleted</b> rather than left as a phantom row.
/// </summary>
/// <remarks>
/// Replay opens no socket, so every test here runs against a test-created cache
/// tree and a temp database with no HTTP at all.
/// </remarks>
public class ConfluenceCacheReplayTests : IDisposable
{
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly FileSystemResponseCache _cache;
    private readonly ConfluenceDatabase _database;

    public ConfluenceCacheReplayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-replay-{Guid.NewGuid():N}");
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

    private ConfluenceSource CreateSource()
    {
        IOptions<ConfluenceServiceOptions> options = Options.Create(new ConfluenceServiceOptions
        {
            BaseUrl = BaseUrl,
            CachePath = _cache.RootPath,
            DatabasePath = _dbPath,
        });

        return new ConfluenceSource(
            options,
            _database,
            _cache,
            new ConfluenceSpaceDiscovery(options, _cache, NullLogger<ConfluenceSpaceDiscovery>.Instance,
                (_, _) => throw new InvalidOperationException("replay must not fetch")),
            new ConfluenceSweep(options, _cache, NullLogger<ConfluenceSweep>.Instance,
                (_, _) => throw new InvalidOperationException("replay must not fetch")),
            NullLogger<ConfluenceSource>.Instance,
            (_, _) => throw new InvalidOperationException("replay must not fetch"));
    }

    private IngestionResult Replay() =>
        CreateSource().LoadFromCacheAsync(CancellationToken.None).GetAwaiter().GetResult();

    private void Write(string key, string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void WriteCatalog(params string[] spaceKeys) =>
        Write(ConfluenceCacheLayout.GetSpaceCatalogCacheKey(), new ConfluenceSpaceCatalog
        {
            DiscoveredAt = DateTimeOffset.UtcNow,
            Complete = true,
            Spaces = [.. spaceKeys.Select(k => new ConfluenceCatalogedSpace { Key = k, Name = $"{k} Space" })],
        }.ToJson());

    private void WriteSpace(string spaceKey) =>
        Write(ConfluenceCacheLayout.GetSpaceCacheKey(spaceKey), JsonSerializer.Serialize(new
        {
            key = spaceKey,
            name = $"{spaceKey} Space",
            description = new { plain = new { value = $"About {spaceKey}" } },
        }));

    private void WriteManifest(string spaceKey, params ConfluenceManifestEntry[] entries) =>
        Write(ConfluenceCacheLayout.GetManifestCacheKey(spaceKey), new ConfluenceManifest
        {
            SpaceKey = spaceKey,
            Profiles = ConfluenceManifestProfiles.Current,
            SweptAt = DateTimeOffset.UtcNow,
            Complete = true,
            Entries = [.. entries],
        }.ToJson());

    private void WritePage(string spaceKey, string id, string title = "A Page", string body = "<p>hello</p>")
    {
        string payload = JsonSerializer.Serialize(new
        {
            id,
            type = "page",
            status = "current",
            title,
            body = new { storage = new { value = body } },
            version = new { number = 1, when = "2026-08-01T00:00:00.000Z", by = new { displayName = "Ada" } },
            _links = new { webui = $"/spaces/{spaceKey}/pages/{id}" },
        });

        WriteArtifact(ConfluenceCacheLayout.GetPageCacheKey(spaceKey, id), payload, ContentTypes.Page, spaceKey);
    }

    private void WriteComment(string spaceKey, string id, string containerId, string body = "<p>a comment</p>")
    {
        string payload = JsonSerializer.Serialize(new
        {
            id,
            type = "comment",
            status = "current",
            title = $"Re: {containerId}",
            body = new { storage = new { value = body } },
            version = new { number = 1, when = "2026-08-02T00:00:00.000Z", by = new { displayName = "Grace" } },
            container = new { id = containerId },
        });

        WriteArtifact(ConfluenceCacheLayout.GetCommentCacheKey(spaceKey, id), payload, ContentTypes.Comment, spaceKey);
    }

    private void WriteArtifact(string key, string payload, string type, string spaceKey)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        Write(key, ConfluenceCachedArtifact.Wrap(document.RootElement, type, spaceKey, version: 1).ToJson());
    }

    private static ConfluenceManifestEntry PageEntry(string id, string title = "A Page") =>
        new() { Id = id, Type = ContentTypes.Page, Title = title, Version = 1 };

    private static ConfluenceManifestEntry CommentEntry(string id, string containerId) =>
        new() { Id = id, Type = ContentTypes.Comment, Version = 1, ContainerId = containerId };

    private SqliteConnection Open() => _database.OpenConnection();

    private int CountComments(string confluencePageId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM confluence_comments WHERE ConfluencePageId = @id";
        cmd.Parameters.AddWithValue("@id", confluencePageId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<int> CommentIds(string confluencePageId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM confluence_comments WHERE ConfluencePageId = @id ORDER BY Id";
        cmd.Parameters.AddWithValue("@id", confluencePageId);

        List<int> ids = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    // ── Happy path ────────────────────────────────────────────────────

    [Fact]
    public void Replay_ProducesSpacePageCommentAndLinkRows()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100", "Live Page",
            """<p>see <ac:link><ri:page ri:content-id="101" /></ac:link></p>""");
        WriteComment("FHIR", "200", "100");
        WriteManifest("FHIR", PageEntry("100", "Live Page"), CommentEntry("200", "100"));

        IngestionResult result = Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
        Assert.Equal(1, ConfluenceSpaceRecord.SelectCount(connection));
        Assert.Equal(1, CountComments("100"));
        Assert.Equal(1, result.ItemsProcessed);

        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "100")!;
        Assert.Equal("Live Page", page.Title);
        Assert.Equal("FHIR", page.SpaceKey);
        Assert.Equal($"{BaseUrl}/spaces/FHIR/pages/100", page.Url);
        Assert.Equal("Ada", page.LastModifiedBy);

        ConfluenceSpaceRecord space = ConfluenceSpaceRecord.SelectSingle(connection, Key: "FHIR")!;
        Assert.Equal("FHIR Space", space.Name);
        Assert.Equal("About FHIR", space.Description);

        Assert.True(ConfluencePageLinkRecord.SelectCount(connection) > 0);
    }

    [Fact]
    public void Replay_NeedsNoNetworkAndNoWatermark()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));

        // The source's fetch throws if touched, so this passing is the assertion.
        Assert.Equal(1, Replay().ItemsProcessed);
    }

    [Fact]
    public void Replay_IsIdempotentAcrossConsecutiveRuns()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WriteComment("FHIR", "200", "100");
        WriteComment("FHIR", "201", "100");
        WriteManifest("FHIR", PageEntry("100"), CommentEntry("200", "100"), CommentEntry("201", "100"));

        Replay();
        List<int> firstIds = CommentIds("100");

        Replay();
        List<int> secondIds = CommentIds("100");

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
        Assert.Equal(2, CountComments("100"));

        // Deterministic insertion order means an unchanged page keeps its ids.
        Assert.Equal(firstIds, secondIds);
    }

    [Fact]
    public void Replay_DoesNotDoubleLinksAcrossRuns()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100", body: """<ac:link><ri:page ri:content-id="101" /></ac:link>""");
        WriteManifest("FHIR", PageEntry("100"));

        Replay();
        using SqliteConnection first = Open();
        int afterOne = ConfluencePageLinkRecord.SelectCount(first);
        first.Close();

        Replay();
        using SqliteConnection second = Open();
        Assert.Equal(afterOne, ConfluencePageLinkRecord.SelectCount(second));
    }

    // ── Cache hygiene ─────────────────────────────────────────────────

    [Fact]
    public void Replay_IgnoresFilesUnderVanished()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));

        // A tombstoned page still on disk. FileSystemResponseCache filters
        // _meta_*.json by file name only, so nothing under _vanished/ is
        // excluded by that filter — manifest-driven replay is what keeps it out.
        string live = ConfluenceCacheLayout.GetPageCacheKey("FHIR", "999");
        WritePage("FHIR", "999");
        using (Stream? stream = _cache.TryGet(ConfluenceCacheLayout.SourceName, live, out Stream? s) ? s : null)
        {
            _cache.PutAsync(ConfluenceCacheLayout.SourceName,
                ConfluenceCacheLayout.GetVanishedCacheKey(live), stream!, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        _cache.Remove(ConfluenceCacheLayout.SourceName, live);

        Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
        Assert.Null(ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "999"));
    }

    [Fact]
    public void Replay_IgnoresAPageOnDiskThatNoManifestNames()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WritePage("FHIR", "999");
        WriteManifest("FHIR", PageEntry("100"));

        Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
    }

    [Fact]
    public void Replay_WithNoCatalog_DoesNothingRatherThanEmptyingTheDatabase()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));
        Replay();

        _cache.Remove(ConfluenceCacheLayout.SourceName, ConfluenceCacheLayout.GetSpaceCatalogCacheKey());

        IngestionResult result = Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(0, result.ItemsProcessed);
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
    }

    // ── Deletion ──────────────────────────────────────────────────────

    [Fact]
    public void Replay_DeletesAPageWhoseManifestEntryDisappeared()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WritePage("FHIR", "101");
        WriteComment("FHIR", "200", "101");
        WriteManifest("FHIR", PageEntry("100"), PageEntry("101"), CommentEntry("200", "101"));
        Replay();

        Assert.Equal(1, CountComments("101"));

        // 101 disappears from the manifest.
        WriteManifest("FHIR", PageEntry("100"));
        Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
        Assert.Null(ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "101"));
        Assert.Equal(0, CountComments("101"));
    }

    [Fact]
    public void Replay_DeletesTheLinksOfADisappearedPageOnBothSides()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100", body: """<ac:link><ri:page ri:content-id="101" /></ac:link>""");
        WritePage("FHIR", "101", body: """<ac:link><ri:page ri:content-id="100" /></ac:link>""");
        WriteManifest("FHIR", PageEntry("100"), PageEntry("101"));
        Replay();

        using (SqliteConnection before = Open())
        {
            Assert.True(ConfluencePageLinkRecord.SelectCount(before) >= 2);
        }

        WriteManifest("FHIR", PageEntry("100"));
        Replay();

        using SqliteConnection connection = Open();
        List<ConfluencePageLinkRecord> links = ConfluencePageLinkRecord.SelectList(connection);
        Assert.DoesNotContain(links, l => l.SourcePageId == "101" || l.TargetPageId == "101");
    }

    [Fact]
    public void Replay_DeletesASpaceDroppedFromTheCatalog()
    {
        WriteCatalog("FHIR", "SOA");
        WriteSpace("FHIR");
        WriteSpace("SOA");
        WritePage("FHIR", "100");
        WritePage("SOA", "300");
        WriteManifest("FHIR", PageEntry("100"));
        WriteManifest("SOA", PageEntry("300"));
        Replay();

        using (SqliteConnection before = Open())
        {
            Assert.Equal(2, ConfluenceSpaceRecord.SelectCount(before));
        }

        WriteCatalog("FHIR");
        Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluenceSpaceRecord.SelectCount(connection));
        Assert.Null(ConfluenceSpaceRecord.SelectSingle(connection, Key: "SOA"));

        // Its pages go too — the manifest no longer names them.
        Assert.Null(ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "300"));
    }

    [Fact]
    public void Replay_RehomesAPageMovedBetweenTrackedSpacesRatherThanDeletingIt()
    {
        // This is why deletion runs globally, after every space is materialized.
        WriteCatalog("FHIR", "SOA");
        WriteSpace("FHIR");
        WriteSpace("SOA");
        WritePage("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));
        WriteManifest("SOA");
        Replay();

        using (SqliteConnection before = Open())
        {
            Assert.Equal("FHIR", ConfluencePageRecord.SelectSingle(before, ConfluenceId: "100")!.SpaceKey);
        }

        // The page moves: it leaves FHIR's manifest and joins SOA's.
        WriteManifest("FHIR");
        WriteManifest("SOA", PageEntry("100"));
        WritePage("SOA", "100");
        Replay();

        using SqliteConnection connection = Open();
        ConfluencePageRecord moved = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "100")!;
        Assert.Equal("SOA", moved.SpaceKey);
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
    }

    [Fact]
    public void Replay_ExplicitlyEmptyCatalog_ClearsEverything()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));
        Replay();

        WriteCatalog();
        Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(0, ConfluenceSpaceRecord.SelectCount(connection));
        Assert.Equal(0, ConfluencePageRecord.SelectCount(connection));
    }

    // ── Robustness ────────────────────────────────────────────────────

    [Fact]
    public void Replay_SkipsAManifestEntryWithNoCachedArtifact()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"), PageEntry("101"));

        IngestionResult result = Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
        Assert.Equal(1, result.ItemsProcessed);
    }

    [Fact]
    public void Replay_SurvivesAMalformedCachedArtifact()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WritePage("FHIR", "100");
        Write(ConfluenceCacheLayout.GetPageCacheKey("FHIR", "101"), "{ not json");
        WriteManifest("FHIR", PageEntry("100"), PageEntry("101"));

        Replay();

        using SqliteConnection connection = Open();
        Assert.Equal(1, ConfluencePageRecord.SelectCount(connection));
    }

    [Fact]
    public void Replay_UsesTheSpaceKeyEvenWhenSpaceMetadataIsAbsent()
    {
        WriteCatalog("FHIR");
        WritePage("FHIR", "100");
        WriteManifest("FHIR", PageEntry("100"));

        Replay();

        using SqliteConnection connection = Open();
        Assert.Equal("FHIR", ConfluenceSpaceRecord.SelectSingle(connection, Key: "FHIR")!.Name);
    }

    [Fact]
    public void Replay_SkipsACommentWhoseContainerPageIsNotPresent()
    {
        WriteCatalog("FHIR");
        WriteSpace("FHIR");
        WriteComment("FHIR", "200", "999");
        WriteManifest("FHIR", CommentEntry("200", "999"));

        Replay();

        Assert.Equal(0, CountComments("999"));
    }
}
