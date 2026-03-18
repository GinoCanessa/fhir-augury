using Microsoft.Data.Sqlite;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;
using FhirAugury.Indexing.Bm25;

namespace FhirAugury.Indexing.Tests;

public class Bm25CalculatorTests
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

        return conn;
    }

    [Fact]
    public void CountAndClassifyTokens_FiltersStopWords()
    {
        var tokens = new List<string> { "the", "patient", "is", "a", "resource", "patient" };
        var result = Bm25Calculator.CountAndClassifyTokens(tokens);

        Assert.DoesNotContain("the", result.Keys);
        Assert.DoesNotContain("is", result.Keys);
        Assert.DoesNotContain("a", result.Keys);
        Assert.Contains("patient", result.Keys);
        Assert.Equal(2, result["patient"].Count);
        Assert.Contains("resource", result.Keys);
    }

    [Fact]
    public void BuildFullIndex_WithJiraIssues_CreatesKeywordRecords()
    {
        using var conn = CreateInMemoryDb();

        InsertJiraIssue(conn, "FHIR-100", "Patient resource validation",
            "The Patient resource needs validation rules for name and birthDate.");
        InsertJiraIssue(conn, "FHIR-200", "Observation resource profile",
            "Create a profile for Observation resource with required value element.");

        Bm25Calculator.BuildFullIndex(conn);

        var keywords = KeywordRecord.SelectList(conn);
        Assert.NotEmpty(keywords);

        // Check that both documents have keyword records
        Assert.Contains(keywords, k => k.SourceId == "FHIR-100");
        Assert.Contains(keywords, k => k.SourceId == "FHIR-200");
    }

    [Fact]
    public void BuildFullIndex_ComputesBm25Scores()
    {
        using var conn = CreateInMemoryDb();

        InsertJiraIssue(conn, "FHIR-100", "Patient resource", "Patient resource details.");
        InsertJiraIssue(conn, "FHIR-200", "Observation value", "Observation value element.");

        Bm25Calculator.BuildFullIndex(conn);

        var keywords = KeywordRecord.SelectList(conn);
        // BM25 scores should be non-zero for non-trivial documents
        Assert.Contains(keywords, k => k.Bm25Score != 0.0);
    }

    [Fact]
    public void BuildFullIndex_CreatesCorpusStats()
    {
        using var conn = CreateInMemoryDb();

        InsertJiraIssue(conn, "FHIR-100", "Patient resource", "Patient details.");
        InsertJiraIssue(conn, "FHIR-200", "Observation value", "Observation details.");

        Bm25Calculator.BuildFullIndex(conn);

        var corpusRecords = CorpusKeywordRecord.SelectList(conn);
        Assert.NotEmpty(corpusRecords);

        // Verify IDF is computed
        Assert.All(corpusRecords, c => Assert.NotEqual(0.0, c.Idf));
    }

    [Fact]
    public void BuildFullIndex_CreatesDocStats()
    {
        using var conn = CreateInMemoryDb();

        InsertJiraIssue(conn, "FHIR-100", "Patient resource", "Patient details.");
        InsertJiraIssue(conn, "FHIR-200", "Observation value", "Observation details.");

        Bm25Calculator.BuildFullIndex(conn);

        var docStats = DocStatsRecord.SelectList(conn);
        Assert.NotEmpty(docStats);
        Assert.Contains(docStats, ds => ds.SourceType == "jira" && ds.TotalDocuments == 2);
    }

    [Fact]
    public void UpdateIndex_AddsNewItems()
    {
        using var conn = CreateInMemoryDb();

        // Build initial index
        InsertJiraIssue(conn, "FHIR-100", "Patient resource", "Patient details.");
        Bm25Calculator.BuildFullIndex(conn);

        var initialCount = KeywordRecord.SelectList(conn).Count;

        // Add a new item via incremental update
        var newItems = new List<(string, string, string)>
        {
            ("jira", "FHIR-200", "Observation resource with value element and status code"),
        };
        Bm25Calculator.UpdateIndex(conn, newItems);

        var finalCount = KeywordRecord.SelectList(conn).Count;
        Assert.True(finalCount > initialCount);

        // Verify new item has keyword records
        var newKeywords = KeywordRecord.SelectList(conn, SourceId: "FHIR-200");
        Assert.NotEmpty(newKeywords);
    }

    [Fact]
    public void BuildFullIndex_BM25_UniqueTermScoresHigherThanCommon()
    {
        using var conn = CreateInMemoryDb();

        // Doc 1 mentions "patient" (appears in both docs) and "allergy" (unique to doc 1)
        InsertJiraIssue(conn, "FHIR-100", "Patient allergy", "Patient allergy intolerance details.");
        // Doc 2 mentions "patient" (common) and "medication" (unique to doc 2)
        InsertJiraIssue(conn, "FHIR-200", "Patient medication", "Patient medication administration.");

        Bm25Calculator.BuildFullIndex(conn);

        // Get keyword records for FHIR-100
        var doc1Keywords = KeywordRecord.SelectList(conn, SourceType: "jira", SourceId: "FHIR-100");

        var patientScore = doc1Keywords.FirstOrDefault(k => k.Keyword == "patient")?.Bm25Score ?? 0;
        var allergyScore = doc1Keywords.FirstOrDefault(k => k.Keyword == "allergy")?.Bm25Score ?? 0;

        // "allergy" appears in fewer docs, so should have higher IDF and BM25
        Assert.True(allergyScore > patientScore,
            $"Unique term 'allergy' ({allergyScore:F4}) should score higher than common 'patient' ({patientScore:F4})");
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
