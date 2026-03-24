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
