using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public GitHubDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
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

        Assert.Contains("github_repos", tables);
        Assert.Contains("github_issues", tables);
        Assert.Contains("github_comments", tables);
        Assert.Contains("github_commits", tables);
        Assert.Contains("github_commit_files", tables);
        Assert.Contains("github_commit_pr_links", tables);
        Assert.Contains("github_spec_file_map", tables);
        Assert.Contains("sync_state", tables);
        Assert.Contains("xref_jira", tables);
        Assert.Contains("xref_zulip", tables);
        Assert.Contains("xref_confluence", tables);
        Assert.Contains("xref_fhir_element", tables);
    }

    [Fact]
    public void Initialize_CreatesFtsVirtualTables()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("github_issues_fts", tables);
        Assert.Contains("github_comments_fts", tables);
        Assert.Contains("github_commits_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_Repo_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        GitHubRepoRecord repo = new GitHubRepoRecord
        {
            Id = GitHubRepoRecord.GetIndex(),
            FullName = "HL7/fhir",
            Owner = "HL7",
            Name = "fhir",
            Description = "FHIR Core Specification",
            HasIssues = false,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        GitHubRepoRecord.Insert(conn, repo);
        GitHubRepoRecord? result = GitHubRepoRecord.SelectSingle(conn, FullName: "HL7/fhir");

        Assert.NotNull(result);
        Assert.Equal("HL7", result.Owner);
        Assert.False(result.HasIssues);
    }

    [Fact]
    public void InsertAndSelect_Issue_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        GitHubIssueRecord issue = CreateSampleIssue("HL7/fhir#42", "HL7/fhir", 42, "Fix patient resource");

        GitHubIssueRecord.Insert(conn, issue);
        GitHubIssueRecord? result = GitHubIssueRecord.SelectSingle(conn, UniqueKey: "HL7/fhir#42");

        Assert.NotNull(result);
        Assert.Equal(42, result.Number);
        Assert.Equal("Fix patient resource", result.Title);
        Assert.False(result.IsPullRequest);
    }

    [Fact]
    public void InsertAndSelect_CommitFile_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        GitHubCommitFileRecord commitFile = new GitHubCommitFileRecord
        {
            Id = GitHubCommitFileRecord.GetIndex(),
            CommitSha = "abc123",
            FilePath = "source/patient/Patient-spreadsheet.xml",
            ChangeType = "M",
        };

        GitHubCommitFileRecord.Insert(conn, commitFile);
        List<GitHubCommitFileRecord> results = GitHubCommitFileRecord.SelectList(conn, CommitSha: "abc123");

        Assert.Single(results);
        Assert.Equal("source/patient/Patient-spreadsheet.xml", results[0].FilePath);
    }

    [Fact]
    public void InsertAndSelect_JiraRef_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraXRefRecord jiraRef = new JiraXRefRecord
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Issue,
            SourceId = "HL7/fhir#42",
            LinkType = "mentions",
            JiraKey = "FHIR-12345",
            Context = "Fix for FHIR-12345 patient resource",
        };

        JiraXRefRecord.Insert(conn, jiraRef);
        List<JiraXRefRecord> results = JiraXRefRecord.SelectList(conn, JiraKey: "FHIR-12345");

        Assert.Single(results);
        Assert.Equal("FHIR-12345", results[0].JiraKey);
    }

    [Fact]
    public void InsertAndSelect_SpecFileMap_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        GitHubSpecFileMapRecord mapping = new GitHubSpecFileMapRecord
        {
            Id = GitHubSpecFileMapRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            ArtifactKey = "patient",
            FilePath = "source/patient/",
            MapType = "directory",
        };

        GitHubSpecFileMapRecord.Insert(conn, mapping);
        List<GitHubSpecFileMapRecord> results = GitHubSpecFileMapRecord.SelectList(conn, RepoFullName: "HL7/fhir");

        Assert.Single(results);
        Assert.Equal("patient", results[0].ArtifactKey);
    }

    [Fact]
    public void Fts5_IndexesIssuesOnInsert()
    {
        using SqliteConnection conn = _db.OpenConnection();
        GitHubIssueRecord.Insert(conn, CreateSampleIssue("HL7/fhir#1", "HL7/fhir", 1, "Patient resource validation bug"));
        GitHubIssueRecord.Insert(conn, CreateSampleIssue("HL7/fhir#2", "HL7/fhir", 2, "Observation code system update"));

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UniqueKey FROM github_issues WHERE Id IN (SELECT rowid FROM github_issues_fts WHERE github_issues_fts MATCH '\"Patient\"')";
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<string> keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));

        Assert.Single(keys);
        Assert.Equal("HL7/fhir#1", keys[0]);
    }

    [Fact]
    public void CheckIntegrity_ReturnsOk()
    {
        string result = _db.CheckIntegrity();
        Assert.Equal("ok", result);
    }

    private static GitHubIssueRecord CreateSampleIssue(
        string uniqueKey, string repoFullName, int number, string title) => new()
    {
        Id = GitHubIssueRecord.GetIndex(),
        UniqueKey = uniqueKey,
        RepoFullName = repoFullName,
        Number = number,
        IsPullRequest = false,
        Title = title,
        Body = $"Body for {title}",
        State = "open",
        Author = "testuser",
        Labels = null,
        Assignees = null,
        Milestone = null,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
        UpdatedAt = DateTimeOffset.UtcNow,
        ClosedAt = null,
        MergeState = null,
        HeadBranch = null,
        BaseBranch = null,
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
