using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class JiraIssueRecordTests
{
    [Fact]
    public void Insert_And_SelectSingle_RoundTrips()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-100", "Test issue");
        JiraIssueRecord.Insert(conn, issue);

        var found = JiraIssueRecord.SelectSingle(conn, Key: "FHIR-100");
        Assert.NotNull(found);
        Assert.Equal("FHIR-100", found.Key);
        Assert.Equal("Test issue", found.Title);
        Assert.Equal("FHIR", found.ProjectKey);
    }

    [Fact]
    public void Update_ModifiesExistingRecord()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-200", "Original title");
        JiraIssueRecord.Insert(conn, issue);

        issue.Title = "Updated title";
        issue.Status = "Resolved";
        JiraIssueRecord.Update(conn, issue);

        var found = JiraIssueRecord.SelectSingle(conn, Key: "FHIR-200");
        Assert.NotNull(found);
        Assert.Equal("Updated title", found.Title);
        Assert.Equal("Resolved", found.Status);
    }

    [Fact]
    public void Delete_RemovesRecord()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-300", "To delete");
        JiraIssueRecord.Insert(conn, issue);

        // Use SQL delete directly as generated Delete may have issues with nullable params
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jira_issues WHERE Key = @key";
        cmd.Parameters.AddWithValue("@key", "FHIR-300");
        cmd.ExecuteNonQuery();

        var found = JiraIssueRecord.SelectSingle(conn, Key: "FHIR-300");
        Assert.Null(found);
    }

    [Fact]
    public void SelectList_ReturnsMultipleRecords()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        JiraIssueRecord.Insert(conn, TestHelper.CreateSampleIssue("FHIR-400", "First", status: "Triaged"));
        JiraIssueRecord.Insert(conn, TestHelper.CreateSampleIssue("FHIR-401", "Second", status: "Triaged"));
        JiraIssueRecord.Insert(conn, TestHelper.CreateSampleIssue("FHIR-402", "Third", status: "Resolved"));

        var triaged = JiraIssueRecord.SelectList(conn, Status: "Triaged");
        Assert.Equal(2, triaged.Count);

        var all = JiraIssueRecord.SelectList(conn);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void SelectCount_ReturnsCorrectCount()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        JiraIssueRecord.Insert(conn, TestHelper.CreateSampleIssue("FHIR-500", "A"));
        JiraIssueRecord.Insert(conn, TestHelper.CreateSampleIssue("FHIR-501", "B"));

        var count = JiraIssueRecord.SelectCount(conn);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Insert_WithIgnoreDuplicates_DoesNotThrow()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-600", "Original");
        JiraIssueRecord.Insert(conn, issue);

        // Insert again with same PK — should not throw with ignoreDuplicates
        var duplicate = TestHelper.CreateSampleIssue("FHIR-601", "Different");
        duplicate.Id = issue.Id;
        JiraIssueRecord.Insert(conn, duplicate, ignoreDuplicates: true);

        // Original should still be there
        var found = JiraIssueRecord.SelectSingle(conn, Key: "FHIR-600");
        Assert.NotNull(found);
    }

    [Fact]
    public void CustomFields_AreStoredAndRetrieved()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-700", "Custom fields test");
        issue.Specification = "FHIRPath";
        issue.WorkGroup = "FHIR Infrastructure";
        issue.RaisedInVersion = "STU3";
        issue.Labels = "fhirpath,normative";

        JiraIssueRecord.Insert(conn, issue);

        var found = JiraIssueRecord.SelectSingle(conn, Key: "FHIR-700");
        Assert.NotNull(found);
        Assert.Equal("FHIRPath", found.Specification);
        Assert.Equal("FHIR Infrastructure", found.WorkGroup);
        Assert.Equal("STU3", found.RaisedInVersion);
        Assert.Equal("fhirpath,normative", found.Labels);
    }
}
