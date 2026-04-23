using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Phase 2 lock-in: every typed-issue table (jira_pss/jira_baldef/jira_ballot)
/// is created at <see cref="JiraDatabase.Initialize"/> time, exposes the
/// shared base columns, and round-trips a record through the generated
/// Insert/SelectList surface.
/// </summary>
public class JiraNewRecordsSchemaTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public JiraNewRecordsSchemaTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_new_recs_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static HashSet<string> GetColumns(SqliteConnection conn, string table)
    {
        HashSet<string> cols = new HashSet<string>(StringComparer.Ordinal);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    [Fact]
    public void PssTable_HasBaseAndPssColumns_AndRoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        HashSet<string> cols = GetColumns(conn, "jira_pss");
        Assert.Contains("Id", cols);
        Assert.Contains("Key", cols);
        Assert.Contains("ProjectKey", cols);
        Assert.Contains("Title", cols);
        Assert.Contains("Status", cols);
        Assert.Contains("UpdatedAt", cols);
        Assert.Contains("ProcessedLocallyAt", cols);
        // PSS-specific
        Assert.Contains("SponsoringWorkGroup", cols);
        Assert.Contains("SponsoringWorkGroupsLegacy", cols);
        Assert.Contains("ApprovalDate", cols);
        Assert.Contains("ProjectDescription", cols);
        Assert.Contains("ProjectDescriptionPlain", cols);

        JiraProjectScopeStatementRecord rec = new JiraProjectScopeStatementRecord
        {
            Id = JiraProjectScopeStatementRecord.GetIndex(),
            Key = "PSS-100",
            ProjectKey = "PSS",
            Title = "Test PSS",
            Description = "desc",
            Summary = "Test PSS",
            Type = "Project Scope Statement",
            Priority = "Major",
            Status = "Open",
            Assignee = null,
            Reporter = "rep",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            SponsoringWorkGroup = "FHIR-I",
            ProjectDescription = "<p>hi</p>",
            ProjectDescriptionPlain = "hi",
        };
        JiraProjectScopeStatementRecord.Insert(conn, rec);

        List<JiraProjectScopeStatementRecord> got = JiraProjectScopeStatementRecord.SelectList(conn, Key: "PSS-100");
        Assert.Single(got);
        Assert.Equal("FHIR-I", got[0].SponsoringWorkGroup);
        Assert.Equal("hi", got[0].ProjectDescriptionPlain);
    }

    [Fact]
    public void BaldefTable_HasBaseAndBaldefColumns_AndRoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        HashSet<string> cols = GetColumns(conn, "jira_baldef");
        Assert.Contains("Key", cols);
        Assert.Contains("Status", cols);
        Assert.Contains("BallotCode", cols);
        Assert.Contains("BallotCycle", cols);
        Assert.Contains("BallotPackageName", cols);
        Assert.Contains("BallotCategory", cols);
        Assert.Contains("Specification", cols);
        Assert.Contains("VotersAffirmative", cols);

        JiraBaldefRecord rec = new JiraBaldefRecord
        {
            Id = JiraBaldefRecord.GetIndex(),
            Key = "BALDEF-9",
            ProjectKey = "BALDEF",
            Title = "Pkg",
            Description = null,
            Summary = "Pkg",
            Type = "Ballot",
            Priority = "Major",
            Status = "Open",
            Assignee = null,
            Reporter = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            BallotCode = "2024-Sep | FHIR R5",
            BallotCycle = "2024-Sep",
            BallotPackageName = "FHIR R5",
            BallotCategory = "STU",
            Specification = "FHIR Core",
            VotersAffirmative = 12,
        };
        JiraBaldefRecord.Insert(conn, rec);

        List<JiraBaldefRecord> got = JiraBaldefRecord.SelectList(conn, Key: "BALDEF-9");
        Assert.Single(got);
        Assert.Equal("2024-Sep", got[0].BallotCycle);
        Assert.Equal("FHIR R5", got[0].BallotPackageName);
        Assert.Equal(12, got[0].VotersAffirmative);
    }

    [Fact]
    public void BallotTable_HasBaseAndBallotColumns_AndRoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        HashSet<string> cols = GetColumns(conn, "jira_ballot");
        Assert.Contains("Key", cols);
        Assert.Contains("VoteBallot", cols);
        Assert.Contains("BallotPackageCode", cols);
        Assert.Contains("Voter", cols);
        Assert.Contains("BallotCycle", cols);
        Assert.Contains("RelatedFhirIssue", cols);
        Assert.Contains("Organization", cols);

        JiraBallotRecord rec = new JiraBallotRecord
        {
            Id = JiraBallotRecord.GetIndex(),
            Key = "BALLOT-42",
            ProjectKey = "BALLOT",
            Title = "Affirmative - Jane Doe (Acme) : 2024-Sep | FHIR R5",
            Description = null,
            Summary = "Affirmative - Jane Doe (Acme) : 2024-Sep | FHIR R5",
            Type = "Vote",
            Priority = "Major",
            Status = "Open",
            Assignee = null,
            Reporter = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            VoteBallot = "Affirmative",
            BallotPackageCode = "2024-Sep | FHIR R5",
            BallotCycle = "2024-Sep",
            Voter = "Jane Doe",
            Organization = "Acme",
            RelatedFhirIssue = "FHIR-1234",
        };
        JiraBallotRecord.Insert(conn, rec);

        List<JiraBallotRecord> got = JiraBallotRecord.SelectList(conn, Key: "BALLOT-42");
        Assert.Single(got);
        Assert.Equal("Affirmative", got[0].VoteBallot);
        Assert.Equal("Jane Doe", got[0].Voter);
        Assert.Equal("FHIR-1234", got[0].RelatedFhirIssue);
    }
}
