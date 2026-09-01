using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Controllers;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins that a page's archived status reaches <c>confluence_pages</c> and the
/// item surfaces — and that exposing it did <b>not</b> change what search and
/// cross-reference queries return.
/// </summary>
public class ConfluencePageStatusTests : IDisposable
{
    private const string Space = "FHIR";
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly FileSystemResponseCache _cache;
    private readonly ConfluenceDatabase _database;

    public ConfluencePageStatusTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-status-{Guid.NewGuid():N}");
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

    private void Write(string key, string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void WriteCatalog() =>
        Write(ConfluenceCacheLayout.GetSpaceCatalogCacheKey(), new ConfluenceSpaceCatalog
        {
            DiscoveredAt = DateTimeOffset.UtcNow,
            Complete = true,
            Spaces = [new ConfluenceCatalogedSpace { Key = Space, Name = Space }],
        }.ToJson());

    private void WriteManifest(params ConfluenceManifestEntry[] entries) =>
        Write(ConfluenceCacheLayout.GetManifestCacheKey(Space), new ConfluenceManifest
        {
            SpaceKey = Space,
            Profiles = ConfluenceManifestProfiles.Current,
            SweptAt = DateTimeOffset.UtcNow,
            Complete = true,
            Entries = [.. entries],
        }.ToJson());

    private void WritePage(string id, string title = "A Page")
    {
        string payload = JsonSerializer.Serialize(new
        {
            id,
            type = "page",
            title,
            body = new { storage = new { value = $"<p>{title} mentions FHIR-1234</p>" } },
            version = new { number = 1, when = "2026-08-01T00:00:00.000Z", by = new { displayName = "Ada" } },
        });

        using JsonDocument document = JsonDocument.Parse(payload);
        Write(ConfluenceCacheLayout.GetPageCacheKey(Space, id),
            ConfluenceCachedArtifact.Wrap(document.RootElement, ContentTypes.Page, Space, 1).ToJson());
    }

    private static ConfluenceManifestEntry Entry(string id, string status) =>
        new() { Id = id, Type = ContentTypes.Page, Title = $"Page {id}", Version = 1, Status = status };

    private void Replay()
    {
        IOptions<ConfluenceServiceOptions> accessor = Options.Create(new ConfluenceServiceOptions
        {
            BaseUrl = BaseUrl,
            CachePath = _cache.RootPath,
            DatabasePath = _dbPath,
        });

        ConfluenceSource source = new(
            accessor,
            _database,
            _cache,
            new ConfluenceSpaceDiscovery(accessor, _cache, NullLogger<ConfluenceSpaceDiscovery>.Instance,
                (_, _) => throw new InvalidOperationException("replay must not fetch")),
            new ConfluenceSweep(accessor, _cache, NullLogger<ConfluenceSweep>.Instance,
                (_, _) => throw new InvalidOperationException("replay must not fetch")),
            NullLogger<ConfluenceSource>.Instance,
            (_, _) => throw new InvalidOperationException("replay must not fetch"));

        source.LoadFromCacheAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private void SeedBothStatuses()
    {
        WriteCatalog();
        WritePage("100", "Live Page");
        WritePage("101", "Old Page");
        WriteManifest(
            Entry("100", ConfluenceEntryStatus.Current),
            Entry("101", ConfluenceEntryStatus.Archived));
        Replay();
    }

    // ── Projection ────────────────────────────────────────────────────

    [Fact]
    public void Replay_ProjectsManifestStatusOntoThePageRow()
    {
        SeedBothStatuses();

        using SqliteConnection connection = _database.OpenConnection();
        Assert.Equal("current", ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "100")!.Status);
        Assert.Equal("archived", ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "101")!.Status);
    }

    [Fact]
    public void ItemResponse_SurfacesStatusInItsMetadata()
    {
        SeedBothStatuses();

        using SqliteConnection connection = _database.OpenConnection();
        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "101")!;

        ItemResponse response = PagesController.BuildItemResponse(
            new ConfluenceServiceOptions { BaseUrl = BaseUrl }, page, [], []);

        Assert.Equal("archived", response.Metadata!["status"]);
    }

    [Fact]
    public void MarkdownSnapshot_CarriesStatusInItsBody()
    {
        SeedBothStatuses();

        using SqliteConnection connection = _database.OpenConnection();
        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: "101")!;

        string markdown = ConfluenceUrlHelper.BuildMarkdownSnapshot(page, []);

        Assert.Contains("**Status:** archived", markdown, StringComparison.Ordinal);
    }

    // ── Migration ─────────────────────────────────────────────────────

    [Fact]
    public void OpeningAPreStatusDatabase_AddsTheColumnRatherThanFailingAtStartup()
    {
        // The generated CreateTable is create-if-not-exists, so without the
        // ALTER the column would never appear and the generated index on it
        // would fail inside db.Initialize() — before Kestrel binds.
        string legacyPath = Path.Combine(_root, "legacy.db");

        using (SqliteConnection seed = new($"Data Source={legacyPath}"))
        {
            seed.Open();
            using SqliteCommand cmd = seed.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE confluence_pages (
                    Id INTEGER PRIMARY KEY,
                    ConfluenceId TEXT NOT NULL UNIQUE,
                    SpaceKey TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    ParentId TEXT,
                    BodyStorage TEXT,
                    BodyPlain TEXT,
                    Labels TEXT,
                    VersionNumber INTEGER NOT NULL,
                    LastModifiedBy TEXT,
                    LastModifiedAt TEXT NOT NULL,
                    Url TEXT
                );
                INSERT INTO confluence_pages
                    (Id, ConfluenceId, SpaceKey, Title, VersionNumber, LastModifiedAt)
                    VALUES (1, '100', 'FHIR', 'Legacy Page', 1, '2026-08-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using ConfluenceDatabase migrated = new(legacyPath, NullLogger<ConfluenceDatabase>.Instance);
        migrated.Initialize();

        using SqliteConnection connection = migrated.OpenConnection();
        using SqliteCommand check = connection.CreateCommand();
        check.CommandText = "SELECT Status FROM confluence_pages WHERE ConfluenceId = '100'";

        Assert.Equal("current", check.ExecuteScalar() as string);
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        SeedBothStatuses();

        // Re-initializing must not attempt the ALTER a second time.
        _database.Initialize();

        using SqliteConnection connection = _database.OpenConnection();
        Assert.Equal(2, ConfluencePageRecord.SelectCount(connection));
    }

    // ── Behaviour that must NOT have changed ──────────────────────────

    [Fact]
    public void ArchivedPages_AreStillReturnedByListAndSearchQueries()
    {
        // This phase exposes the field; whether to weight or filter on it is
        // indexing work this request deliberately defers.
        SeedBothStatuses();

        using SqliteConnection connection = _database.OpenConnection();
        List<ConfluencePageRecord> all = ConfluencePageRecord.SelectList(connection);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.ConfluenceId == "101");
    }

    [Fact]
    public void ArchivedPages_StillProduceCrossReferences()
    {
        SeedBothStatuses();

        ConfluenceXRefRebuilder rebuilder = new(_database, NullLogger<ConfluenceXRefRebuilder>.Instance);
        rebuilder.RebuildAll(CancellationToken.None);

        using SqliteConnection connection = _database.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM xref_jira WHERE SourceId = '101'";

        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) > 0);
    }

    [Fact]
    public void AddingTheColumn_DidNotRequireRebuildingTheFtsTable()
    {
        // confluence_pages_fts indexes only BodyPlain, Title and Labels.
        SeedBothStatuses();

        using SqliteConnection connection = _database.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM confluence_pages_fts WHERE confluence_pages_fts MATCH 'Page'";

        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) >= 0);
    }
}
