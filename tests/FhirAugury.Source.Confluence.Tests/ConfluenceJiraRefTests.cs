using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.Confluence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Confluence.Tests;

public class ConfluenceJiraRefTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConfluenceDatabase _db;

    public ConfluenceJiraRefTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"confluence_jiraref_test_{Guid.NewGuid()}.db");
        _db = new ConfluenceDatabase(_dbPath, NullLogger<ConfluenceDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void JiraRefRecord_CreateTable_RoundTrip()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraXRefRecord record = new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Page,
            SourceId = "50001",
            LinkType = "mentions",
            JiraKey = "FHIR-12345",
            OriginalLiteral = "FHIR-12345",
            Context = "See FHIR-12345 for patient resource details",
        };

        JiraXRefRecord.Insert(conn, record);
        JiraXRefRecord? result = JiraXRefRecord.SelectSingle(conn, JiraKey: "FHIR-12345");

        Assert.NotNull(result);
        Assert.Equal("50001", result.SourceId);
        Assert.Equal("FHIR-12345", result.JiraKey);
        Assert.Equal("See FHIR-12345 for patient resource details", result.Context);
    }

    [Fact]
    public void JiraRefRecord_SelectByJiraKey()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraXRefRecord.Insert(conn, new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Page, SourceId = "100", LinkType = "mentions",
            JiraKey = "FHIR-1001",
            OriginalLiteral = "FHIR-1001",
            Context = "Referenced in page 100",
        });
        JiraXRefRecord.Insert(conn, new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Page, SourceId = "200", LinkType = "mentions",
            JiraKey = "FHIR-1001",
            OriginalLiteral = "FHIR-1001",
            Context = "Also referenced in page 200",
        });
        JiraXRefRecord.Insert(conn, new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Page, SourceId = "300", LinkType = "mentions",
            JiraKey = "FHIR-9999",
            OriginalLiteral = "FHIR-9999",
            Context = "Different ticket in page 300",
        });

        List<JiraXRefRecord> results = JiraXRefRecord.SelectList(conn, JiraKey: "FHIR-1001");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("FHIR-1001", r.JiraKey));
    }

    [Fact]
    public void JiraRefRecord_SelectBySourceId()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraXRefRecord.Insert(conn, new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Page, SourceId = "400", LinkType = "mentions",
            JiraKey = "FHIR-2001",
            OriginalLiteral = "FHIR-2001",
            Context = "First ref in page 400",
        });
        JiraXRefRecord.Insert(conn, new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Page, SourceId = "400", LinkType = "mentions",
            JiraKey = "FHIR-500",
            OriginalLiteral = "GF-500",
            Context = "Second ref in page 400",
        });
        JiraXRefRecord.Insert(conn, new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Page, SourceId = "500", LinkType = "mentions",
            JiraKey = "FHIR-3001",
            OriginalLiteral = "FHIR-3001",
            Context = "Ref in different page",
        });

        List<JiraXRefRecord> results = JiraXRefRecord.SelectList(conn, SourceId: "400");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("400", r.SourceId));
    }

    [Fact]
    public void JiraTicketExtractor_FindsKeysInPageContent()
    {
        string pageContent = """
            The Patient resource is tracked in FHIR-55001 and related work
            is in GF-123. See also https://jira.hl7.org/browse/FHIR-55002
            for the latest updates. The shorthand J#99999 is sometimes used.
            """;

        List<JiraTicketMatch> tickets = JiraTicketExtractor.ExtractTickets(pageContent);

        List<string> keys = tickets.Select(t => t.JiraKey).ToList();

        Assert.Contains("FHIR-55001", keys);
        Assert.Contains("FHIR-55002", keys);
        Assert.Contains("FHIR-123", keys);   // GF-123 → normalized FHIR-123
        Assert.Contains("FHIR-99999", keys); // J#99999 → FHIR-99999
        Assert.Equal(4, keys.Count);

        Assert.Contains(tickets, t => t.OriginalLiteral == "GF-123");
        Assert.Contains(tickets, t => t.OriginalLiteral == "FHIR-55001");
        Assert.All(tickets, t => Assert.False(string.IsNullOrWhiteSpace(t.Context)));
    }
}
