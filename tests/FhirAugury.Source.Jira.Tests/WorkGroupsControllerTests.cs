using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Tests;

public class WorkGroupsControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly WorkGroupsController _controller;

    public WorkGroupsControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_wg_ctrl_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _controller = new WorkGroupsController(_db, Options.Create(new JiraServiceOptions()));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static JiraIndexWorkGroupRecord NewIndexWg(
        string name,
        int? wgId,
        int issueCount,
        int submitted = 0,
        int triaged = 0,
        int closed = 0) => new JiraIndexWorkGroupRecord
        {
            Id = JiraIndexWorkGroupRecord.GetIndex(),
            Name = name,
            WorkGroupId = wgId,
            IssueCount = issueCount,
            IssueCountSubmitted = submitted,
            IssueCountTriaged = triaged,
            IssueCountWaitingForInput = 0,
            IssueCountNoChange = 0,
            IssueCountChangeRequired = 0,
            IssueCountPublished = 0,
            IssueCountApplied = 0,
            IssueCountDuplicate = 0,
            IssueCountClosed = closed,
            IssueCountBalloted = 0,
            IssueCountWithdrawn = 0,
            IssueCountDeferred = 0,
            IssueCountOther = 0,
        };

    private static Hl7WorkGroupRecord NewHl7Wg(string code, string name, string nameClean, string? definition = null) => new Hl7WorkGroupRecord
    {
        Id = Hl7WorkGroupRecord.GetIndex(),
        Code = code,
        Name = name,
        Definition = definition,
        Retired = false,
        NameClean = nameClean,
    };

    private static JiraIssueRecord NewIssue(string key, string workGroup) => new JiraIssueRecord
    {
        Id = JiraIssueRecord.GetIndex(),
        Key = key,
        ProjectKey = "FHIR",
        Title = $"Issue {key}",
        Description = null,
        Summary = null,
        Type = "Bug",
        Priority = "Major",
        Status = "Open",
        Resolution = null,
        ResolutionDescription = null,
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        WorkGroup = workGroup,
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
        ChangeCategory = null,
        ChangeImpact = null,
        ProcessedLocallyAt = null,
    };

    [Fact]
    public void List_OrdersByCountDescThenName_AndPopulatesJoinedMetadata()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            Hl7WorkGroupRecord fhir = NewHl7Wg("fhir", "FHIR Infrastructure", "FHIRInfrastructure", "FHIR I WG.");
            Hl7WorkGroupRecord pc = NewHl7Wg("pc", "Patient Care", "PatientCare", "Patient Care WG.");
            Hl7WorkGroupRecord.Insert(conn, fhir);
            Hl7WorkGroupRecord.Insert(conn, pc);

            JiraIndexWorkGroupRecord.Insert(conn, NewIndexWg("FHIR Infrastructure", fhir.Id, issueCount: 50, submitted: 30, closed: 20));
            JiraIndexWorkGroupRecord.Insert(conn, NewIndexWg("Patient Care", pc.Id, issueCount: 50, triaged: 50));
            JiraIndexWorkGroupRecord.Insert(conn, NewIndexWg("Orphan WG", null, issueCount: 5, submitted: 5));
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.ListWorkGroups());
        List<JiraWorkGroupSummaryEntry> rows = Assert.IsType<List<JiraWorkGroupSummaryEntry>>(ok.Value);

        Assert.Equal(3, rows.Count);
        // 50 FHIR Infrastructure, 50 Patient Care (alphabetical), 5 Orphan
        Assert.Equal("FHIR Infrastructure", rows[0].Name);
        Assert.Equal("Patient Care", rows[1].Name);
        Assert.Equal("Orphan WG", rows[2].Name);

        Assert.Equal("fhir", rows[0].WorkGroupCode);
        Assert.Equal("FHIRInfrastructure", rows[0].WorkGroupNameClean);
        Assert.Equal("FHIR I WG.", rows[0].WorkGroupDefinition);
        Assert.False(rows[0].WorkGroupRetired);
        Assert.Equal(30, rows[0].IssueCountSubmitted);
        Assert.Equal(20, rows[0].IssueCountClosed);

        Assert.Null(rows[2].WorkGroupCode);
        Assert.Null(rows[2].WorkGroupNameClean);
        Assert.Null(rows[2].WorkGroupDefinition);
        Assert.Null(rows[2].WorkGroupRetired);
    }

    [Fact]
    public void Get_LookupByName_QueryParam_ReturnsIssues()
    {
        SeedFhirWithOneIssue();
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroup(groupCode: null, groupName: "FHIR Infrastructure", limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Single(issues);
        Assert.Equal("FHIR-1", issues[0].Key);
    }

    [Fact]
    public void Get_LookupByCode_PathParam_ResolvesToCanonicalNameAndReturnsIssues()
    {
        SeedFhirWithOneIssue();
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroupCode("fhir", limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Single(issues);
        Assert.Equal("FHIR-1", issues[0].Key);
    }

    [Fact]
    public void Get_LookupByCode_QueryParam_ResolvesToCanonicalNameAndReturnsIssues()
    {
        SeedFhirWithOneIssue();
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroup(groupCode: "fhir", groupName: null, limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Single(issues);
        Assert.Equal("FHIR-1", issues[0].Key);
    }

    [Fact]
    public void Get_LookupByNameClean_PathParam_ResolvesToCanonicalNameAndReturnsIssues()
    {
        SeedFhirWithOneIssue();
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroupCode("FHIRInfrastructure", limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Single(issues);
        Assert.Equal("FHIR-1", issues[0].Key);
    }

    [Fact]
    public void Get_UnknownGroupCode_PathParam_ReturnsEmptyList()
    {
        SeedFhirWithOneIssue();
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroupCode("does-not-exist", limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Empty(issues);
    }

    [Fact]
    public void Get_UnknownGroupCode_QueryParam_ReturnsEmptyList()
    {
        SeedFhirWithOneIssue();
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroup(groupCode: "does-not-exist", groupName: null, limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Empty(issues);
    }

    [Fact]
    public void Get_NoFilter_ReturnsAllIssues()
    {
        SeedFhirWithOneIssue();
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraIssueRecord.Insert(conn, NewIssue("PC-1", "Patient Care"));
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroup(groupCode: null, groupName: null, limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void Get_BothCodeAndName_AndedTogether()
    {
        SeedFhirWithOneIssue();
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraIssueRecord.Insert(conn, NewIssue("PC-1", "Patient Care"));
        }

        // Code "fhir" resolves to "FHIR Infrastructure"; name "Patient Care"
        // does not overlap, so the AND yields no results.
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetIssuesForWorkGroup(groupCode: "fhir", groupName: "Patient Care", limit: null, offset: null));
        List<JiraIssueSummaryEntry> issues = Assert.IsType<List<JiraIssueSummaryEntry>>(ok.Value);
        Assert.Empty(issues);
    }

    private void SeedFhirWithOneIssue()
    {
        using SqliteConnection conn = _db.OpenConnection();
        Hl7WorkGroupRecord fhir = NewHl7Wg("fhir", "FHIR Infrastructure", "FHIRInfrastructure");
        Hl7WorkGroupRecord.Insert(conn, fhir);
        JiraIndexWorkGroupRecord.Insert(conn, NewIndexWg("FHIR Infrastructure", fhir.Id, issueCount: 1, submitted: 1));
        JiraIssueRecord.Insert(conn, NewIssue("FHIR-1", "FHIR Infrastructure"));
    }
}
