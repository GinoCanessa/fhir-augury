using Microsoft.Data.Sqlite;
using FhirAugury.Database;
using FhirAugury.Database.Records;

#pragma warning disable xUnit1013 // Public method on test class should be marked as a Test

namespace FhirAugury.Mcp.Tests;

/// <summary>Shared helpers for MCP tool tests.</summary>
internal static class McpTestHelper
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
        CrossRefLinkRecord.CreateTable(conn);
        CrossRefLinkRecord.LoadMaxKey(conn);
        KeywordRecord.CreateTable(conn);
        KeywordRecord.LoadMaxKey(conn);
        CorpusKeywordRecord.CreateTable(conn);
        CorpusKeywordRecord.LoadMaxKey(conn);
        DocStatsRecord.CreateTable(conn);
        DocStatsRecord.LoadMaxKey(conn);

        FtsSetup.CreateJiraFts(conn);
        FtsSetup.CreateZulipFts(conn);
        FtsSetup.CreateConfluenceFts(conn);
        FtsSetup.CreateGitHubFts(conn);

        return conn;
    }

    /// <summary>Creates a DatabaseService backed by a temp file with the given connection's data.</summary>
    /// <remarks>
    /// The MCP tools accept DatabaseService (not raw connections), so we need a file-backed
    /// service. The caller should dispose the returned service and delete the temp file.
    /// </remarks>
    public static (DatabaseService Service, string DbPath) CreateTempDatabaseService()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"fhir-augury-mcp-test-{Guid.NewGuid():N}.db");
        var service = new DatabaseService(dbPath);
        service.InitializeDatabase();
        return (service, dbPath);
    }

    public static void CleanupTempDb(DatabaseService service, string dbPath)
    {
        service.Dispose();
        // Clear the SQLite connection pool to release file locks
        SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var path = dbPath + ext;
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    public static JiraIssueRecord CreateSampleIssue(
        string key, string title,
        string status = "Triaged", string? workGroup = null, string? specification = null,
        string? description = null)
    {
        return new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = "FHIR",
            Title = title,
            Description = description,
            Summary = title,
            Type = "Change Request",
            Priority = "Medium",
            Status = status,
            Resolution = null,
            ResolutionDescription = null,
            Assignee = null,
            Reporter = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            WorkGroup = workGroup,
            Specification = specification,
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

    public static JiraCommentRecord CreateSampleComment(
        int issueId, string issueKey, string author, string body)
    {
        return new JiraCommentRecord
        {
            Id = JiraCommentRecord.GetIndex(),
            IssueId = issueId,
            IssueKey = issueKey,
            Author = author,
            CreatedAt = DateTimeOffset.UtcNow,
            Body = body,
        };
    }

    public static ZulipStreamRecord CreateSampleStream(int zulipStreamId, string name)
    {
        return new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = zulipStreamId,
            Name = name,
            Description = $"Description for {name}",
            IsWebPublic = true,
            MessageCount = 0,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    public static ZulipMessageRecord CreateSampleMessage(
        int zulipMessageId, int streamDbId, string streamName, string topic,
        string senderName = "Test User", string content = "Test message content")
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

    public static ConfluenceSpaceRecord CreateSampleSpace(string key, string name)
    {
        return new ConfluenceSpaceRecord
        {
            Id = ConfluenceSpaceRecord.GetIndex(),
            Key = key,
            Name = name,
            Description = $"Description for {name}",
            Url = $"https://confluence.hl7.org/display/{key}",
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    public static ConfluencePageRecord CreateSamplePage(
        string confluenceId, string title, string spaceKey = "FHIR", string? bodyPlain = null)
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

    public static GitHubRepoRecord CreateSampleRepo(string fullName)
    {
        var parts = fullName.Split('/');
        return new GitHubRepoRecord
        {
            Id = GitHubRepoRecord.GetIndex(),
            FullName = fullName,
            Owner = parts[0],
            Name = parts[1],
            Description = $"Description for {fullName}",
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    public static GitHubIssueRecord CreateSampleGitHubIssue(
        string repoFullName, int number, string title,
        bool isPullRequest = false, string state = "open", string? body = null)
    {
        return new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(),
            UniqueKey = $"{repoFullName}#{number}",
            RepoFullName = repoFullName,
            Number = number,
            IsPullRequest = isPullRequest,
            Title = title,
            Body = body ?? $"Body for {title}",
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
}
