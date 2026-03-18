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
        ConfluenceSpaceRecord.CreateTable(conn);
        ConfluenceSpaceRecord.LoadMaxKey(conn);
        ConfluencePageRecord.CreateTable(conn);
        ConfluencePageRecord.LoadMaxKey(conn);
        ConfluenceCommentRecord.CreateTable(conn);
        ConfluenceCommentRecord.LoadMaxKey(conn);
        GitHubRepoRecord.CreateTable(conn);
        GitHubRepoRecord.LoadMaxKey(conn);
        GitHubIssueRecord.CreateTable(conn);
        GitHubIssueRecord.LoadMaxKey(conn);
        GitHubCommentRecord.CreateTable(conn);
        GitHubCommentRecord.LoadMaxKey(conn);
        FtsSetup.CreateConfluenceFts(conn);
        FtsSetup.CreateGitHubFts(conn);

        return conn;
    }

    public static ConfluencePageRecord CreateSamplePage(
        string confluenceId,
        string title,
        string spaceKey = "FHIR",
        string? bodyPlain = null)
    {
        return new ConfluencePageRecord
        {
            Id = ConfluencePageRecord.GetIndex(),
            ConfluenceId = confluenceId,
            SpaceKey = spaceKey,
            Title = title,
            ParentId = null,
            BodyStorage = null,
            BodyPlain = bodyPlain ?? $"Content for {title}",
            Labels = null,
            VersionNumber = 1,
            LastModifiedBy = "tester",
            LastModifiedAt = DateTimeOffset.UtcNow,
            Url = $"https://confluence.hl7.org/pages/{confluenceId}",
        };
    }

    public static GitHubIssueRecord CreateSampleGitHubIssue(
        string repoFullName,
        int number,
        string title,
        bool isPullRequest = false,
        string state = "open")
    {
        return new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(),
            UniqueKey = $"{repoFullName}#{number}",
            RepoFullName = repoFullName,
            Number = number,
            IsPullRequest = isPullRequest,
            Title = title,
            Body = $"Body for {title}",
            State = state,
            Author = "tester",
            Labels = null,
            Assignees = null,
            Milestone = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ClosedAt = null,
            MergeState = null,
            HeadBranch = null,
            BaseBranch = null,
        };
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
