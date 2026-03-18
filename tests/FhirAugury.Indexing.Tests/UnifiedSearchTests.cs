using Microsoft.Data.Sqlite;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Indexing.Tests;

public class UnifiedSearchTests
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
        FtsSetup.CreateJiraFts(conn);
        FtsSetup.CreateZulipFts(conn);

        return conn;
    }

    [Fact]
    public void SearchAll_ReturnsBothJiraAndZulipResults()
    {
        using var conn = CreateInMemoryDb();

        // Insert Jira data
        var issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = "FHIR-100",
            ProjectKey = "FHIR",
            Title = "FHIRPath normative review",
            Description = "Review the FHIRPath specification for normative ballot.",
            Summary = "FHIRPath normative review",
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
            Specification = "FHIRPath",
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

        // Insert Zulip data
        var stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 100,
            Name = "implementers",
            Description = "Implementer chat",
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
            Topic = "FHIRPath discussion",
            SenderId = 42,
            SenderName = "Alice",
            SenderEmail = "alice@example.com",
            ContentHtml = "<p>FHIRPath normative discussion thread</p>",
            ContentPlain = "FHIRPath normative discussion thread",
            Timestamp = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            Reactions = null,
        };
        ZulipMessageRecord.Insert(conn, msg);

        var results = FtsSearchService.SearchAll(conn, "FHIRPath");

        Assert.True(results.Count >= 2);
        Assert.Contains(results, r => r.Source == "jira");
        Assert.Contains(results, r => r.Source == "zulip");
    }

    [Fact]
    public void SearchAll_ResultsAreInterleavedByScore()
    {
        using var conn = CreateInMemoryDb();

        // Insert Jira issue mentioning "terminology" once
        var issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = "FHIR-200",
            ProjectKey = "FHIR",
            Title = "Minor terminology note",
            Description = "A small note.",
            Summary = "Minor terminology note",
            Type = "Change Request",
            Priority = "Low",
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

        var stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 200,
            Name = "terminology",
            Description = null,
            IsWebPublic = true,
            MessageCount = 0,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
        ZulipStreamRecord.Insert(conn, stream);

        var msg = new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = 6000,
            StreamId = stream.Id,
            StreamName = "terminology",
            Topic = "terminology binding",
            SenderId = 43,
            SenderName = "Bob",
            SenderEmail = null,
            ContentHtml = "<p>terminology binding terminology terminology</p>",
            ContentPlain = "terminology binding terminology terminology",
            Timestamp = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            Reactions = null,
        };
        ZulipMessageRecord.Insert(conn, msg);

        var results = FtsSearchService.SearchAll(conn, "terminology");

        // Should have results from both sources interleaved
        Assert.True(results.Count >= 2);
        var sources = results.Select(r => r.Source).Distinct().ToList();
        Assert.True(sources.Count >= 2);
    }

    [Fact]
    public void SearchAll_SourceFilterLimitsResults()
    {
        using var conn = CreateInMemoryDb();

        var issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = "FHIR-300",
            ProjectKey = "FHIR",
            Title = "Questionnaire resource",
            Description = "Questionnaire improvements.",
            Summary = "Questionnaire resource",
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

        var stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 300,
            Name = "test",
            Description = null,
            IsWebPublic = true,
            MessageCount = 0,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
        ZulipStreamRecord.Insert(conn, stream);

        var msg = new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = 7000,
            StreamId = stream.Id,
            StreamName = "test",
            Topic = "Questionnaire",
            SenderId = 44,
            SenderName = "Carol",
            SenderEmail = null,
            ContentHtml = "<p>Questionnaire testing</p>",
            ContentPlain = "Questionnaire testing",
            Timestamp = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            Reactions = null,
        };
        ZulipMessageRecord.Insert(conn, msg);

        // Search only jira
        var jiraOnly = FtsSearchService.SearchAll(conn, "Questionnaire", new HashSet<string> { "jira" });
        Assert.All(jiraOnly, r => Assert.Equal("jira", r.Source));

        // Search only zulip
        var zulipOnly = FtsSearchService.SearchAll(conn, "Questionnaire", new HashSet<string> { "zulip" });
        Assert.All(zulipOnly, r => Assert.Equal("zulip", r.Source));
    }

    [Fact]
    public void SearchAll_NormalizesScoresAcrossSources()
    {
        using var conn = CreateInMemoryDb();

        var issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = "FHIR-400",
            ProjectKey = "FHIR",
            Title = "Observation resource",
            Description = "Observation resource improvements for normative ballot.",
            Summary = "Observation resource",
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

        var stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 400,
            Name = "implementers",
            Description = null,
            IsWebPublic = true,
            MessageCount = 0,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
        ZulipStreamRecord.Insert(conn, stream);

        var msg = new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = 8000,
            StreamId = stream.Id,
            StreamName = "implementers",
            Topic = "Observation",
            SenderId = 45,
            SenderName = "Dave",
            SenderEmail = null,
            ContentHtml = "<p>Observation resource discussion</p>",
            ContentPlain = "Observation resource discussion",
            Timestamp = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            Reactions = null,
        };
        ZulipMessageRecord.Insert(conn, msg);

        var results = FtsSearchService.SearchAll(conn, "Observation");

        Assert.All(results, r => Assert.NotNull(r.NormalizedScore));
        Assert.All(results, r => Assert.InRange(r.NormalizedScore!.Value, 0.0, 1.0));
    }
}


