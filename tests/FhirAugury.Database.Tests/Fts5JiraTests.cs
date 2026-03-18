using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Database.Tests;

public class Fts5JiraTests
{
    [Fact]
    public void InsertIssue_AutoPopulatesFts5()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-900", "FHIRPath normative review");
        issue.Description = "Review the FHIRPath specification for normative ballot readiness.";
        issue.Specification = "FHIRPath";
        JiraIssueRecord.Insert(conn, issue);

        // FTS5 should have been populated by the INSERT trigger
        var results = FtsSearchService.SearchJiraIssues(conn, "FHIRPath", limit: 10);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "FHIR-900");
    }

    [Fact]
    public void UpdateIssue_UpdatesFts5()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-901", "Original title");
        issue.Description = "Original description about bundles.";
        JiraIssueRecord.Insert(conn, issue);

        // Update title
        issue.Title = "Updated FHIRPath title";
        issue.Description = "Updated description about FHIRPath expressions.";
        JiraIssueRecord.Update(conn, issue);

        // Search should find the updated content
        var results = FtsSearchService.SearchJiraIssues(conn, "FHIRPath", limit: 10);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "FHIR-901");

        // Old content should not match
        var oldResults = FtsSearchService.SearchJiraIssues(conn, "bundles", limit: 10);
        Assert.DoesNotContain(oldResults, r => r.Id == "FHIR-901");
    }

    [Fact]
    public void DeleteIssue_RemovesFromFts5()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-902", "Temporary issue about questionnaires");
        issue.Description = "This issue is about FHIR questionnaires and will be deleted.";
        JiraIssueRecord.Insert(conn, issue);

        // Verify it's in FTS
        var beforeResults = FtsSearchService.SearchJiraIssues(conn, "questionnaires", limit: 10);
        Assert.NotEmpty(beforeResults);

        // Use SQL delete directly (triggers will fire)
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jira_issues WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", issue.Id);
        cmd.ExecuteNonQuery();

        var afterResults = FtsSearchService.SearchJiraIssues(conn, "questionnaires", limit: 10);
        Assert.Empty(afterResults);
    }

    [Fact]
    public void InsertComment_AutoPopulatesCommentFts5()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-903", "Issue for comment FTS test");
        JiraIssueRecord.Insert(conn, issue);

        var comment = new JiraCommentRecord
        {
            Id = JiraCommentRecord.GetIndex(),
            IssueId = issue.Id,
            IssueKey = "FHIR-903",
            Author = "reviewer",
            CreatedAt = DateTimeOffset.UtcNow,
            Body = "This comment discusses aggregate functions in FHIRPath.",
        };
        JiraCommentRecord.Insert(conn, comment);

        var results = FtsSearchService.SearchJiraComments(conn, "aggregate", limit: 10);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void FtsSearch_ReturnsSnippets()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-904", "Snippet test issue");
        issue.Description = "The FHIRPath specification defines a set of aggregate functions including count, sum, and avg.";
        JiraIssueRecord.Insert(conn, issue);

        var results = FtsSearchService.SearchJiraIssues(conn, "aggregate", limit: 10);
        Assert.NotEmpty(results);

        var result = results[0];
        Assert.NotNull(result.Snippet);
    }

    [Fact]
    public void FtsSearch_RanksRelevantResultsHigher()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        // Issue with "normative" in title and description
        var relevantIssue = TestHelper.CreateSampleIssue("FHIR-905", "Normative ballot for FHIRPath normative package");
        relevantIssue.Description = "This normative ballot covers normative content in the FHIRPath normative specification.";
        JiraIssueRecord.Insert(conn, relevantIssue);

        // Issue with "normative" only once
        var lessRelevant = TestHelper.CreateSampleIssue("FHIR-906", "General review comment");
        lessRelevant.Description = "A minor comment that mentions normative once.";
        JiraIssueRecord.Insert(conn, lessRelevant);

        var results = FtsSearchService.SearchJiraIssues(conn, "normative", limit: 10);
        Assert.True(results.Count >= 2);
        Assert.Equal("FHIR-905", results[0].Id);
    }

    [Fact]
    public void RebuildJiraFts_RepopulatesFromContentTables()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-907", "Rebuild test with terminology");
        issue.Description = "Testing terminology binding for CodeSystem resources.";
        JiraIssueRecord.Insert(conn, issue);

        // Rebuild FTS
        FtsSetup.RebuildJiraFts(conn);

        // Search should still work after rebuild
        var results = FtsSearchService.SearchJiraIssues(conn, "terminology", limit: 10);
        Assert.NotEmpty(results);
    }
}
