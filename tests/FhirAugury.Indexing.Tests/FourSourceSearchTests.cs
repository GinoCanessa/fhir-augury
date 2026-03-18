using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Indexing.Tests;

public class FourSourceSearchTests
{
    private static SqliteConnection CreateInMemoryDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        SyncStateRecord.CreateTable(conn); SyncStateRecord.LoadMaxKey(conn);
        IngestionLogRecord.CreateTable(conn); IngestionLogRecord.LoadMaxKey(conn);
        JiraIssueRecord.CreateTable(conn); JiraIssueRecord.LoadMaxKey(conn);
        JiraCommentRecord.CreateTable(conn); JiraCommentRecord.LoadMaxKey(conn);
        ZulipStreamRecord.CreateTable(conn); ZulipStreamRecord.LoadMaxKey(conn);
        ZulipMessageRecord.CreateTable(conn); ZulipMessageRecord.LoadMaxKey(conn);
        ConfluencePageRecord.CreateTable(conn); ConfluencePageRecord.LoadMaxKey(conn);
        ConfluenceSpaceRecord.CreateTable(conn); ConfluenceSpaceRecord.LoadMaxKey(conn);
        ConfluenceCommentRecord.CreateTable(conn); ConfluenceCommentRecord.LoadMaxKey(conn);
        GitHubRepoRecord.CreateTable(conn); GitHubRepoRecord.LoadMaxKey(conn);
        GitHubIssueRecord.CreateTable(conn); GitHubIssueRecord.LoadMaxKey(conn);
        GitHubCommentRecord.CreateTable(conn); GitHubCommentRecord.LoadMaxKey(conn);
        FtsSetup.CreateJiraFts(conn);
        FtsSetup.CreateZulipFts(conn);
        FtsSetup.CreateConfluenceFts(conn);
        FtsSetup.CreateGitHubFts(conn);
        return conn;
    }

    [Fact]
    public void SearchAll_ReturnsFourSourceResults()
    {
        using var conn = CreateInMemoryDb();

        // Insert a Jira issue
        var jira = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(), Key = "FHIR-99999", ProjectKey = "FHIR",
            Title = "Patient validation error", Description = null, Summary = "Patient validation error",
            Type = "Bug", Priority = "High", Status = "Open", Resolution = null,
            ResolutionDescription = null, Assignee = null, Reporter = "test",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, ResolvedAt = null,
            WorkGroup = null, Specification = null, RaisedInVersion = null, SelectedBallot = null,
            RelatedArtifacts = null, RelatedIssues = null, DuplicateOf = null, AppliedVersions = null,
            ChangeType = null, Impact = null, Vote = null, Labels = null, CommentCount = 0,
        };
        JiraIssueRecord.Insert(conn, jira);

        // Insert a Zulip message
        var stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(), ZulipStreamId = 1, Name = "implementers",
            Description = null, IsWebPublic = true, MessageCount = 0, LastFetchedAt = DateTimeOffset.UtcNow,
        };
        ZulipStreamRecord.Insert(conn, stream);
        var msg = new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(), ZulipMessageId = 1, StreamId = stream.Id,
            StreamName = "implementers", Topic = "Patient validation",
            SenderId = 1, SenderName = "test", SenderEmail = null,
            ContentHtml = null, ContentPlain = "Patient validation discussed here",
            Timestamp = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, Reactions = null,
        };
        ZulipMessageRecord.Insert(conn, msg);

        // Insert a Confluence page
        var page = new ConfluencePageRecord
        {
            Id = ConfluencePageRecord.GetIndex(), ConfluenceId = "12345", SpaceKey = "FHIR",
            Title = "Patient Resource Guide", ParentId = null, BodyStorage = null,
            BodyPlain = "Comprehensive guide to Patient validation in FHIR",
            Labels = null, VersionNumber = 1, LastModifiedBy = null,
            LastModifiedAt = DateTimeOffset.UtcNow, Url = null,
        };
        ConfluencePageRecord.Insert(conn, page);

        // Insert a GitHub issue
        var ghIssue = new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(), UniqueKey = "HL7/fhir#1234", RepoFullName = "HL7/fhir",
            Number = 1234, IsPullRequest = false, Title = "Patient resource validation bug",
            Body = "Patient validation is broken", State = "open", Author = "test",
            Labels = null, Assignees = null, Milestone = null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ClosedAt = null, MergeState = null, HeadBranch = null, BaseBranch = null,
        };
        GitHubIssueRecord.Insert(conn, ghIssue);

        // Search across all four sources
        var results = FtsSearchService.SearchAll(conn, "Patient validation");

        Assert.True(results.Count >= 4);
        var sources = results.Select(r => r.Source).Distinct().ToList();
        Assert.Contains("jira", sources);
        Assert.Contains("zulip", sources);
        Assert.Contains("confluence", sources);
        Assert.Contains("github", sources);
    }

    [Fact]
    public void SearchAll_SourceFilter_LimitsResults()
    {
        using var conn = CreateInMemoryDb();

        var page = new ConfluencePageRecord
        {
            Id = ConfluencePageRecord.GetIndex(), ConfluenceId = "1", SpaceKey = "FHIR",
            Title = "Observation Guide", ParentId = null, BodyStorage = null,
            BodyPlain = "Guide about Observation", Labels = null,
            VersionNumber = 1, LastModifiedBy = null,
            LastModifiedAt = DateTimeOffset.UtcNow, Url = null,
        };
        ConfluencePageRecord.Insert(conn, page);

        var ghIssue = new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(), UniqueKey = "HL7/fhir#1", RepoFullName = "HL7/fhir",
            Number = 1, IsPullRequest = false, Title = "Observation bug",
            Body = "Observation resource has issue", State = "open", Author = "test",
            Labels = null, Assignees = null, Milestone = null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ClosedAt = null, MergeState = null, HeadBranch = null, BaseBranch = null,
        };
        GitHubIssueRecord.Insert(conn, ghIssue);

        var confluenceOnly = FtsSearchService.SearchAll(conn, "Observation", new HashSet<string> { "confluence" });
        Assert.All(confluenceOnly, r => Assert.Equal("confluence", r.Source));

        var githubOnly = FtsSearchService.SearchAll(conn, "Observation", new HashSet<string> { "github" });
        Assert.All(githubOnly, r => Assert.Equal("github", r.Source));
    }
}
