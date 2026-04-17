using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraIssueProcessedLocallyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public JiraIssueProcessedLocallyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_pl_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static JiraIssueRecord NewIssue(string key, DateTimeOffset? processedAt = null)
    {
        return new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = "FHIR",
            Title = $"Issue {key}",
            Description = null,
            Summary = null,
            Type = "Bug",
            Priority = "Major",
            Status = "Open",
            Resolution = null,
            ResolutionDescription = null,
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
            ProcessedLocallyAt = processedAt,
        };
    }

    [Fact]
    public void Insert_Null_RoundTripsAsNull()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord issue = NewIssue("FHIR-1", processedAt: null);
        JiraIssueRecord.Insert(conn, issue);

        JiraIssueRecord? fetched = JiraIssueRecord.SelectList(conn, Key: "FHIR-1").FirstOrDefault();
        Assert.NotNull(fetched);
        Assert.Null(fetched.ProcessedLocallyAt);
    }

    [Fact]
    public void Update_NonNull_RoundTripsValue()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord issue = NewIssue("FHIR-2", processedAt: null);
        JiraIssueRecord.Insert(conn, issue);

        DateTimeOffset marker = DateTimeOffset.UtcNow;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE jira_issues SET ProcessedLocallyAt = @v WHERE Key = @k";
            cmd.Parameters.Add(new SqliteParameter("@v", marker.ToString("o")));
            cmd.Parameters.Add(new SqliteParameter("@k", "FHIR-2"));
            cmd.ExecuteNonQuery();
        }

        JiraIssueRecord? fetched = JiraIssueRecord.SelectList(conn, Key: "FHIR-2").FirstOrDefault();
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.ProcessedLocallyAt);
        Assert.True(Math.Abs((fetched.ProcessedLocallyAt.Value - marker).TotalSeconds) < 1);
    }

    [Fact]
    public void Schema_HasProcessedLocallyAtIndex()
    {
        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_list(jira_issues)";
        using SqliteDataReader reader = cmd.ExecuteReader();
        List<string> indexNames = [];
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(1));
        }
        reader.Close();

        bool hasProcessedLocallyIndex = false;
        foreach (string name in indexNames)
        {
            using SqliteCommand infoCmd = conn.CreateCommand();
            infoCmd.CommandText = $"PRAGMA index_info({name})";
            using SqliteDataReader infoReader = infoCmd.ExecuteReader();
            while (infoReader.Read())
            {
                if (infoReader.GetString(2).Equals("ProcessedLocallyAt", StringComparison.OrdinalIgnoreCase))
                {
                    hasProcessedLocallyIndex = true;
                    break;
                }
            }
            if (hasProcessedLocallyIndex) break;
        }

        Assert.True(hasProcessedLocallyIndex, "Expected an index on ProcessedLocallyAt");
    }
}
