using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public JiraDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
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

        Assert.Contains("jira_issues", tables);
        Assert.Contains("jira_comments", tables);
        Assert.Contains("jira_issue_links", tables);
        Assert.Contains("jira_spec_artifacts", tables);
        Assert.Contains("sync_state", tables);
        Assert.Contains("index_keywords", tables);
        Assert.Contains("index_corpus", tables);
        Assert.Contains("index_doc_stats", tables);
    }

    [Fact]
    public void Initialize_CreatesFtsVirtualTables()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("jira_issues_fts", tables);
        Assert.Contains("jira_comments_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_JiraIssue_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord issue = CreateSampleIssue("FHIR-12345");

        JiraIssueRecord.Insert(conn, issue);
        JiraIssueRecord? result = JiraIssueRecord.SelectSingle(conn, Key: "FHIR-12345");

        Assert.NotNull(result);
        Assert.Equal("FHIR-12345", result.Key);
        Assert.Equal("Patient resource missing field", result.Title);
        Assert.Equal("Open", result.Status);
        Assert.Equal("FHIR-I", result.WorkGroup);
    }

    [Fact]
    public void SelectList_ByStatus_FiltersCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-1", status: "Open"));
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-2", status: "Closed"));
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-3", status: "Open"));

        List<JiraIssueRecord> openIssues = JiraIssueRecord.SelectList(conn, Status: "Open");

        Assert.Equal(2, openIssues.Count);
        Assert.All(openIssues, i => Assert.Equal("Open", i.Status));
    }

    [Fact]
    public void Fts5_IndexesIssuesOnInsert()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-100", title: "Patient resource validation", labels: "resource"));
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-101", title: "Observation code system", labels: "coding"));

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key FROM jira_issues WHERE Id IN (SELECT rowid FROM jira_issues_fts WHERE jira_issues_fts MATCH '\"validation\"')";
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<string> keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));

        Assert.Single(keys);
        Assert.Equal("FHIR-100", keys[0]);
    }

    [Fact]
    public void InsertAndSelect_JiraComment_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-500"));

        JiraCommentRecord comment = new JiraCommentRecord
        {
            Id = JiraCommentRecord.GetIndex(),
            IssueId = 1,
            IssueKey = "FHIR-500",
            Author = "testuser",
            CreatedAt = DateTimeOffset.UtcNow,
            Body = "This is a test comment",
        };
        JiraCommentRecord.Insert(conn, comment);

        List<JiraCommentRecord> result = JiraCommentRecord.SelectList(conn, IssueKey: "FHIR-500");
        Assert.Single(result);
        Assert.Equal("This is a test comment", result[0].Body);
    }

    [Fact]
    public void InsertAndSelect_SpecArtifact_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraSpecArtifactRecord artifact = new JiraSpecArtifactRecord
        {
            Id = JiraSpecArtifactRecord.GetIndex(),
            Family = "FHIR",
            SpecKey = "fhir-core",
            SpecName = "FHIR Core Specification",
            GitUrl = "https://github.com/HL7/fhir",
            PublishedUrl = "https://hl7.org/fhir",
            DefaultWorkgroup = "FHIR-I",
        };
        JiraSpecArtifactRecord.Insert(conn, artifact);

        JiraSpecArtifactRecord? result = JiraSpecArtifactRecord.SelectSingle(conn, SpecKey: "fhir-core");
        Assert.NotNull(result);
        Assert.Equal("FHIR Core Specification", result.SpecName);
        Assert.Equal("https://github.com/HL7/fhir", result.GitUrl);
    }

    [Fact]
    public void ResetDatabase_ClearsAndRecreates()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-999"));
        conn.Close();

        _db.ResetDatabase();

        using SqliteConnection conn2 = _db.OpenConnection();
        List<JiraIssueRecord> issues = JiraIssueRecord.SelectList(conn2);
        Assert.Empty(issues);

        // Tables still exist
        List<string> tables = GetTableNames(conn2);
        Assert.Contains("jira_issues", tables);
        Assert.Contains("jira_issues_fts", tables);
    }

    [Fact]
    public void CheckIntegrity_ReturnsOk()
    {
        string result = _db.CheckIntegrity();
        Assert.Equal("ok", result);
    }

    private static JiraIssueRecord CreateSampleIssue(
        string key,
        string title = "Patient resource missing field",
        string status = "Open",
        string workGroup = "FHIR-I",
        string labels = "bug,patient") => new()
    {
        Id = JiraIssueRecord.GetIndex(),
        Key = key,
        ProjectKey = "FHIR",
        Title = title,
        Description = "Sample description for testing",
        Summary = title,
        Type = "Bug",
        Priority = "Major",
        Status = status,
        Resolution = null,
        ResolutionDescription = null,
        Assignee = "testuser",
        Reporter = "reporter",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        WorkGroup = workGroup,
        Specification = "FHIR Core",
        RaisedInVersion = "R4",
        SelectedBallot = null,
        RelatedArtifacts = null,
        RelatedIssues = null,
        DuplicateOf = null,
        AppliedVersions = null,
        ChangeType = null,
        Impact = null,
        Vote = null,
        Labels = labels,
        CommentCount = 0,
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
