using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Confluence.Tests;

public class ConfluenceDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConfluenceDatabase _db;

    public ConfluenceDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"confluence_test_{Guid.NewGuid()}.db");
        _db = new ConfluenceDatabase(_dbPath, NullLogger<ConfluenceDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Initialize_CreatesAllTables()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("confluence_spaces", tables);
        Assert.Contains("confluence_pages", tables);
        Assert.Contains("confluence_comments", tables);
        Assert.Contains("confluence_page_links", tables);
        Assert.Contains("sync_state", tables);
        Assert.Contains("index_keywords", tables);
    }

    [Fact]
    public void Initialize_CreatesFtsVirtualTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("confluence_pages_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_Space_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ConfluenceSpaceRecord space = new ConfluenceSpaceRecord
        {
            Id = ConfluenceSpaceRecord.GetIndex(),
            Key = "FHIR",
            Name = "FHIR Specification",
            Description = "Main FHIR spec space",
            Url = "https://confluence.hl7.org/display/FHIR",
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        ConfluenceSpaceRecord.Insert(conn, space);
        ConfluenceSpaceRecord? result = ConfluenceSpaceRecord.SelectSingle(conn, Key: "FHIR");

        Assert.NotNull(result);
        Assert.Equal("FHIR Specification", result.Name);
    }

    [Fact]
    public void InsertAndSelect_Page_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ConfluencePageRecord page = CreateSamplePage("12345", "FHIR", "Patient Resource Overview");

        ConfluencePageRecord.Insert(conn, page);
        ConfluencePageRecord? result = ConfluencePageRecord.SelectSingle(conn, ConfluenceId: "12345");

        Assert.NotNull(result);
        Assert.Equal("Patient Resource Overview", result.Title);
        Assert.Equal("FHIR", result.SpaceKey);
    }

    [Fact]
    public void InsertAndSelect_PageLink_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ConfluencePageRecord.Insert(conn, CreateSamplePage("100", "FHIR", "Source Page"));
        ConfluencePageRecord.Insert(conn, CreateSamplePage("200", "FHIR", "Target Page"));

        ConfluencePageLinkRecord link = new ConfluencePageLinkRecord
        {
            Id = ConfluencePageLinkRecord.GetIndex(),
            SourcePageId = "100",
            TargetPageId = "200",
            LinkType = "internal",
        };
        ConfluencePageLinkRecord.Insert(conn, link);

        List<ConfluencePageLinkRecord> links = ConfluencePageLinkRecord.SelectList(conn, SourcePageId: "100");
        Assert.Single(links);
        Assert.Equal("200", links[0].TargetPageId);
    }

    [Fact]
    public void Fts5_IndexesPagesOnInsert()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ConfluencePageRecord.Insert(conn, CreateSamplePage("301", "FHIR", "Patient Resource Overview", "Patient resource details and usage"));
        ConfluencePageRecord.Insert(conn, CreateSamplePage("302", "FHIR", "Observation Codes", "Code systems for observations"));

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ConfluenceId FROM confluence_pages WHERE Id IN (SELECT rowid FROM confluence_pages_fts WHERE confluence_pages_fts MATCH '\"Patient\"')";
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<string> ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));

        Assert.Single(ids);
        Assert.Equal("301", ids[0]);
    }

    [Fact]
    public void CheckIntegrity_ReturnsOk()
    {
        string result = _db.CheckIntegrity();
        Assert.Equal("ok", result);
    }

    private static ConfluencePageRecord CreateSamplePage(
        string confluenceId, string spaceKey, string title,
        string bodyPlain = "Sample page content") => new()
    {
        Id = ConfluencePageRecord.GetIndex(),
        ConfluenceId = confluenceId,
        SpaceKey = spaceKey,
        Title = title,
        ParentId = null,
        BodyStorage = $"<p>{bodyPlain}</p>",
        BodyPlain = bodyPlain,
        Labels = null,
        VersionNumber = 1,
        LastModifiedBy = "admin",
        LastModifiedAt = DateTimeOffset.UtcNow,
        Url = $"https://confluence.hl7.org/pages/viewpage.action?pageId={confluenceId}",
    };

    // ── Keyword query tests ────────────────────────────────────────────

    [Fact]
    public void GetKeywordsForItem_ReturnsMatchingKeywords()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('page', 'test-item-1', 'patient', 5, 'word', 4.5),
                   ('page', 'test-item-1', 'Patient.name', 3, 'fhir_path', 3.2),
                   ('page', 'test-item-1', '$validate', 1, 'fhir_operation', 2.1),
                   ('page', 'test-item-2', 'observation', 2, 'word', 1.5);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-item-1");
        Assert.Equal(3, results.Count);
        Assert.Equal("patient", results[0].Keyword);
        Assert.Equal(4.5, results[0].Bm25Score);
        Assert.Equal("Patient.name", results[1].Keyword);
        Assert.Equal("$validate", results[2].Keyword);
    }

    [Fact]
    public void GetKeywordsForItem_FiltersByKeywordType()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('page', 'test-filter-1', 'patient', 5, 'word', 4.5),
                   ('page', 'test-filter-1', 'Patient.name', 3, 'fhir_path', 3.2),
                   ('page', 'test-filter-1', '$validate', 1, 'fhir_operation', 2.1);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-filter-1", keywordType: "fhir_path");
        Assert.Single(results);
        Assert.Equal("Patient.name", results[0].Keyword);
    }

    [Fact]
    public void GetKeywordsForItem_RespectsLimit()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('page', 'test-limit-1', 'a', 1, 'word', 3.0),
                   ('page', 'test-limit-1', 'b', 1, 'word', 2.0),
                   ('page', 'test-limit-1', 'c', 1, 'word', 1.0);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-limit-1", limit: 2);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void GetKeywordsForItem_ReturnsEmpty_WhenNoMatch()
    {
        using SqliteConnection connection = _db.OpenConnection();
        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "nonexistent-item");
        Assert.Empty(results);
    }

    [Fact]
    public void GetContentTypeForItem_ReturnsContentType()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('page', 'test-ct-1', 'patient', 1, 'word', 1.0);
            """;
        insertCmd.ExecuteNonQuery();

        string contentType = SourceDatabase.GetContentTypeForItem(connection, "test-ct-1");
        Assert.Equal("page", contentType);
    }

    [Fact]
    public void GetRelatedByKeyword_FindsRelatedItems()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('page', 'seed-1', 'patient', 5, 'word', 4.0),
                   ('page', 'seed-1', 'observation', 3, 'word', 3.0),
                   ('page', 'related-1', 'patient', 4, 'word', 3.5),
                   ('page', 'related-1', 'observation', 2, 'word', 2.0),
                   ('page', 'related-2', 'patient', 1, 'word', 1.0),
                   ('page', 'unrelated', 'medication', 5, 'word', 5.0);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "seed-1", minScore: 0.0);
        Assert.True(results.Count >= 1);
        Assert.Equal("related-1", results[0].SourceId);
        Assert.Contains("patient", results[0].SharedKeywords);
    }

    [Fact]
    public void GetRelatedByKeyword_ExcludesSeedItem()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('page', 'self-1', 'patient', 5, 'word', 4.0),
                   ('page', 'other-1', 'patient', 3, 'word', 3.0);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "self-1", minScore: 0.0);
        Assert.DoesNotContain(results, r => r.SourceId == "self-1");
    }

    [Fact]
    public void GetRelatedByKeyword_RespectsMinScore()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('page', 'high-seed', 'patient', 5, 'word', 4.0),
                   ('page', 'high-match', 'patient', 4, 'word', 3.5),
                   ('page', 'low-match', 'patient', 1, 'word', 0.001);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "high-seed", minScore: 1.0);
        Assert.DoesNotContain(results, r => r.SourceId == "low-match");
    }

    private static List<string> GetTableNames(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'trigger') ORDER BY name";
        using SqliteDataReader reader = cmd.ExecuteReader();
        List<string> names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
