using Microsoft.Data.Sqlite;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;
using FhirAugury.Indexing.Bm25;
using FhirAugury.Models;

namespace FhirAugury.Indexing.Tests;

public class SimilaritySearchTests
{
    private static SqliteConnection CreateInMemoryDb()
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
        ConfluenceSpaceRecord.CreateTable(conn); ConfluenceSpaceRecord.LoadMaxKey(conn);
        ConfluencePageRecord.CreateTable(conn); ConfluencePageRecord.LoadMaxKey(conn);
        ConfluenceCommentRecord.CreateTable(conn); ConfluenceCommentRecord.LoadMaxKey(conn);
        GitHubRepoRecord.CreateTable(conn); GitHubRepoRecord.LoadMaxKey(conn);
        GitHubIssueRecord.CreateTable(conn); GitHubIssueRecord.LoadMaxKey(conn);
        GitHubCommentRecord.CreateTable(conn); GitHubCommentRecord.LoadMaxKey(conn);
        FtsSetup.CreateConfluenceFts(conn);
        FtsSetup.CreateGitHubFts(conn);

        return conn;
    }

    [Fact]
    public void FindRelated_WithSharedKeywords_ReturnsRelatedItems()
    {
        using var conn = CreateInMemoryDb();

        InsertJiraIssue(conn, "FHIR-100", "Patient resource validation rules",
            "The Patient resource needs stricter validation for name elements and birthDate.");
        InsertJiraIssue(conn, "FHIR-200", "Patient resource profile constraints",
            "Add Patient resource profile with constraints on name and identifier.");
        InsertJiraIssue(conn, "FHIR-300", "Medication request workflow",
            "Medication request workflow needs pharmacy integration.");

        Bm25Calculator.BuildFullIndex(conn);

        var related = SimilaritySearchService.FindRelated(conn, "jira", "FHIR-100");

        Assert.NotEmpty(related);
        // FHIR-200 shares "patient", "resource", "name" keywords — should rank higher than FHIR-300
        var fhir200 = related.FirstOrDefault(r => r.Id == "FHIR-200");
        var fhir300 = related.FirstOrDefault(r => r.Id == "FHIR-300");

        Assert.NotNull(fhir200);
        if (fhir300 is not null)
        {
            Assert.True(fhir200.Score >= fhir300.Score);
        }
    }

    [Fact]
    public void FindRelated_CrossRefsBoostScore()
    {
        using var conn = CreateInMemoryDb();

        InsertJiraIssue(conn, "FHIR-100", "Patient resource validation",
            "Patient validation rules.");
        InsertJiraIssue(conn, "FHIR-200", "Patient resource profile",
            "Patient profile constraints.");
        InsertJiraIssue(conn, "FHIR-300", "Patient resource search",
            "Patient search parameters.");

        Bm25Calculator.BuildFullIndex(conn);

        // Add explicit cross-reference between FHIR-100 and FHIR-300
        var xref = new CrossRefLinkRecord
        {
            Id = CrossRefLinkRecord.GetIndex(),
            SourceType = "jira",
            SourceId = "FHIR-100",
            TargetType = "jira",
            TargetId = "FHIR-300",
            LinkType = "mention",
            Context = "See FHIR-300",
        };
        CrossRefLinkRecord.Insert(conn, xref);

        var related = SimilaritySearchService.FindRelated(conn, "jira", "FHIR-100");

        var fhir300 = related.FirstOrDefault(r => r.Id == "FHIR-300");
        Assert.NotNull(fhir300);
        // FHIR-300 should have "xref+keyword" relationship type
        Assert.Equal("xref+keyword", fhir300.Snippet);
    }

    [Fact]
    public void FindRelated_CrossSourceResults()
    {
        using var conn = CreateInMemoryDb();

        // Insert Jira issue about Patient validation
        InsertJiraIssue(conn, "FHIR-100", "Patient resource validation",
            "Patient resource validation rules need updating.");

        // Insert Zulip message about Patient validation
        var stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 100,
            Name = "implementers",
            Description = null,
            IsWebPublic = true,
            MessageCount = 1,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
        ZulipStreamRecord.Insert(conn, stream);

        var msg = new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = 5000,
            StreamId = stream.Id,
            StreamName = "implementers",
            Topic = "Patient validation",
            SenderId = 42,
            SenderName = "Alice",
            SenderEmail = null,
            ContentHtml = null,
            ContentPlain = "Patient resource validation is causing issues with our implementation.",
            Timestamp = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            Reactions = null,
        };
        ZulipMessageRecord.Insert(conn, msg);

        Bm25Calculator.BuildFullIndex(conn);

        var related = SimilaritySearchService.FindRelated(conn, "jira", "FHIR-100");

        // Should include the Zulip message that shares keywords
        Assert.Contains(related, r => r.Source == "zulip");
    }

    [Fact]
    public void FindRelated_NoKeywords_ReturnsEmpty()
    {
        using var conn = CreateInMemoryDb();

        // No data indexed
        var related = SimilaritySearchService.FindRelated(conn, "jira", "FHIR-999");
        Assert.Empty(related);
    }

    private static void InsertJiraIssue(SqliteConnection conn, string key, string title, string description)
    {
        var issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = "FHIR",
            Title = title,
            Description = description,
            Summary = title,
            Type = "Change Request",
            Priority = "Medium",
            Status = "Triaged",
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
        JiraIssueRecord.Insert(conn, issue);
    }
}
