using Microsoft.Data.Sqlite;
using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

internal static class TestHelper
{
    public static SqliteConnection CreateInMemoryDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        SyncStateRecord.CreateTable(conn);
        SyncStateRecord.LoadMaxKey(conn);
        IngestionLogRecord.CreateTable(conn);
        IngestionLogRecord.LoadMaxKey(conn);
        JiraIssueRecord.CreateTable(conn);
        JiraIssueRecord.LoadMaxKey(conn);
        JiraCommentRecord.CreateTable(conn);
        JiraCommentRecord.LoadMaxKey(conn);
        FtsSetup.CreateJiraFts(conn);

        return conn;
    }

    public static JiraIssueRecord CreateSampleIssue(
        string key,
        string title,
        string projectKey = "FHIR",
        string status = "Triaged",
        string type = "Change Request",
        string priority = "Medium")
    {
        return new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = title,
            Description = null,
            Summary = title,
            Type = type,
            Priority = priority,
            Status = status,
            Resolution = null,
            ResolutionDescription = null,
            Assignee = null,
            Reporter = "tester",
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
        };
    }
}
