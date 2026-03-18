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
        ZulipStreamRecord.CreateTable(conn);
        ZulipStreamRecord.LoadMaxKey(conn);
        ZulipMessageRecord.CreateTable(conn);
        ZulipMessageRecord.LoadMaxKey(conn);
        FtsSetup.CreateJiraFts(conn);
        FtsSetup.CreateZulipFts(conn);

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

    public static ZulipStreamRecord CreateSampleStream(
        int zulipStreamId,
        string name,
        bool isWebPublic = true)
    {
        return new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = zulipStreamId,
            Name = name,
            Description = $"Description for {name}",
            IsWebPublic = isWebPublic,
            MessageCount = 0,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    public static ZulipMessageRecord CreateSampleMessage(
        int zulipMessageId,
        int streamDbId,
        string streamName,
        string topic,
        string senderName = "Test User",
        string content = "Test message content")
    {
        return new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = zulipMessageId,
            StreamId = streamDbId,
            StreamName = streamName,
            Topic = topic,
            SenderId = 1000 + zulipMessageId,
            SenderName = senderName,
            SenderEmail = $"{senderName.ToLower().Replace(' ', '.')}@example.com",
            ContentHtml = $"<p>{content}</p>",
            ContentPlain = content,
            Timestamp = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            Reactions = null,
        };
    }
}
