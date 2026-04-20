using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Tests for the FTS-rank multiplier driven by jira_projects.BaselineValue.
/// Mirrors the analogous Zulip behaviour (BaselineValue / 5.0 multiplier,
/// 0 suppresses from ranked output, lookup paths unchanged).
/// </summary>
public class JiraProjectBaselineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public JiraProjectBaselineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_baseline_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Initialize_CreatesJiraProjectsTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='jira_projects'";
        object? result = cmd.ExecuteScalar();
        Assert.Equal("jira_projects", result?.ToString());
    }

    [Fact]
    public void RankedQuery_AppliesBaselineMultiplier()
    {
        using SqliteConnection conn = _db.OpenConnection();

        InsertProject(conn, "AAA", baseline: 5);
        InsertProject(conn, "BBB", baseline: 1);

        InsertIssue(conn, "AAA-1", projectKey: "AAA", title: "shared keyword token");
        InsertIssue(conn, "BBB-1", projectKey: "BBB", title: "shared keyword token");

        List<(string Key, double Score)> results = RunRankedQuery(conn, "shared");

        Assert.Equal(2, results.Count);
        // AAA (baseline 5 → multiplier 1.0) ranks above BBB (baseline 1 → 0.2)
        Assert.Equal("AAA-1", results[0].Key);
        Assert.Equal("BBB-1", results[1].Key);
        // BBB score should be 1/5th of AAA score (within tolerance).
        Assert.InRange(results[1].Score / results[0].Score, 0.15, 0.25);
    }

    [Fact]
    public void BaselineZero_SuppressesFromRankedQuery_KeepsLookup()
    {
        using SqliteConnection conn = _db.OpenConnection();

        InsertProject(conn, "AAA", baseline: 5);
        InsertProject(conn, "ZZZ", baseline: 0);

        InsertIssue(conn, "AAA-1", projectKey: "AAA", title: "shared keyword token");
        InsertIssue(conn, "ZZZ-1", projectKey: "ZZZ", title: "shared keyword token");

        List<(string Key, double Score)> ranked = RunRankedQuery(conn, "shared");

        Assert.Single(ranked);
        Assert.Equal("AAA-1", ranked[0].Key);

        // Lookup by key still works (this is the non-ranked path).
        JiraIssueRecord? lookup = JiraIssueRecord.SelectSingle(conn, Key: "ZZZ-1");
        Assert.NotNull(lookup);
        Assert.Equal("ZZZ", lookup.ProjectKey);
    }

    [Fact]
    public void MissingProjectRow_FallsBackToNeutralBaseline()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Note: no jira_projects row inserted for "ORPHAN".
        InsertIssue(conn, "ORPHAN-1", projectKey: "ORPHAN", title: "shared keyword token");

        List<(string Key, double Score)> ranked = RunRankedQuery(conn, "shared");
        Assert.Single(ranked);
        Assert.Equal("ORPHAN-1", ranked[0].Key);
        Assert.True(ranked[0].Score > 0);
    }

    private static void InsertProject(SqliteConnection conn, string key, int baseline)
    {
        JiraProjectRecord record = new JiraProjectRecord
        {
            Id = JiraProjectRecord.GetIndex(),
            Key = key,
            Enabled = true,
            BaselineValue = baseline,
            IssueCount = 0,
            LastSyncAt = null,
        };
        JiraProjectRecord.Insert(conn, record);
    }

    private static void InsertIssue(SqliteConnection conn, string key, string projectKey, string title)
    {
        JiraIssueRecord issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = title,
            Description = $"Description for {key}",
            DescriptionPlain = $"Description for {key}",
            Summary = title,
            Type = "Bug",
            Priority = "Major",
            Status = "Open",
            Resolution = null,
            ResolutionDescription = null,
            ResolutionDescriptionPlain = null,
            Assignee = null,
            Reporter = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            WorkGroup = null,
            Specification = null,
            RaisedInVersion = null,
            SelectedBallot = null,
            RelatedArtifacts = null,
            RelatedIssues = null,
            DuplicateOf = null,
            AppliedVersions = null,
            ChangeType = null,
            Impact = null,
            Vote = null,
            Labels = null,
            CommentCount = 0,
            ChangeCategory = null,
            ChangeImpact = null,
        };
        JiraIssueRecord.Insert(conn, issue);
    }

    private static List<(string Key, double Score)> RunRankedQuery(SqliteConnection conn, string term)
    {
        const string sql = """
            SELECT ji.Key,
                   -(jira_issues_fts.rank * COALESCE(jp.BaselineValue, 5) / 5.0) as Score
            FROM jira_issues_fts
            JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
            LEFT JOIN jira_projects jp ON jp.Key = ji.ProjectKey
            WHERE jira_issues_fts MATCH @query
              AND COALESCE(jp.BaselineValue, 5) > 0
            ORDER BY jira_issues_fts.rank * COALESCE(jp.BaselineValue, 5) / 5.0
            """;

        using SqliteCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("@query", term);

        List<(string, double)> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetDouble(1)));
        }
        return rows;
    }
}
