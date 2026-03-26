using FhirAugury.Common.Text;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
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

        ConfluenceJiraRefRecord record = new ConfluenceJiraRefRecord
        {
            Id = ConfluenceJiraRefRecord.GetIndex(),
            ConfluenceId = "50001",
            JiraKey = "FHIR-12345",
            Context = "See FHIR-12345 for patient resource details",
        };

        ConfluenceJiraRefRecord.Insert(conn, record);
        ConfluenceJiraRefRecord? result = ConfluenceJiraRefRecord.SelectSingle(conn, JiraKey: "FHIR-12345");

        Assert.NotNull(result);
        Assert.Equal("50001", result.ConfluenceId);
        Assert.Equal("FHIR-12345", result.JiraKey);
        Assert.Equal("See FHIR-12345 for patient resource details", result.Context);
    }

    [Fact]
    public void JiraRefRecord_SelectByJiraKey()
    {
        using SqliteConnection conn = _db.OpenConnection();

        ConfluenceJiraRefRecord.Insert(conn, new ConfluenceJiraRefRecord
        {
            Id = ConfluenceJiraRefRecord.GetIndex(),
            ConfluenceId = "100",
            JiraKey = "FHIR-1001",
            Context = "Referenced in page 100",
        });
        ConfluenceJiraRefRecord.Insert(conn, new ConfluenceJiraRefRecord
        {
            Id = ConfluenceJiraRefRecord.GetIndex(),
            ConfluenceId = "200",
            JiraKey = "FHIR-1001",
            Context = "Also referenced in page 200",
        });
        ConfluenceJiraRefRecord.Insert(conn, new ConfluenceJiraRefRecord
        {
            Id = ConfluenceJiraRefRecord.GetIndex(),
            ConfluenceId = "300",
            JiraKey = "FHIR-9999",
            Context = "Different ticket in page 300",
        });

        List<ConfluenceJiraRefRecord> results = ConfluenceJiraRefRecord.SelectList(conn, JiraKey: "FHIR-1001");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("FHIR-1001", r.JiraKey));
    }

    [Fact]
    public void JiraRefRecord_SelectByConfluenceId()
    {
        using SqliteConnection conn = _db.OpenConnection();

        ConfluenceJiraRefRecord.Insert(conn, new ConfluenceJiraRefRecord
        {
            Id = ConfluenceJiraRefRecord.GetIndex(),
            ConfluenceId = "400",
            JiraKey = "FHIR-2001",
            Context = "First ref in page 400",
        });
        ConfluenceJiraRefRecord.Insert(conn, new ConfluenceJiraRefRecord
        {
            Id = ConfluenceJiraRefRecord.GetIndex(),
            ConfluenceId = "400",
            JiraKey = "GF-500",
            Context = "Second ref in page 400",
        });
        ConfluenceJiraRefRecord.Insert(conn, new ConfluenceJiraRefRecord
        {
            Id = ConfluenceJiraRefRecord.GetIndex(),
            ConfluenceId = "500",
            JiraKey = "FHIR-3001",
            Context = "Ref in different page",
        });

        List<ConfluenceJiraRefRecord> results = ConfluenceJiraRefRecord.SelectList(conn, ConfluenceId: "400");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("400", r.ConfluenceId));
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
        Assert.Contains("GF-123", keys);
        Assert.Contains("FHIR-99999", keys); // J#99999 → FHIR-99999
        Assert.Equal(4, keys.Count);
        Assert.All(tickets, t => Assert.False(string.IsNullOrWhiteSpace(t.Context)));
    }
}
