using FhirAugury.Common.Api;
using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 1 (slot 0625-02): the items surface is PR-aware. The
/// <c>?pullRequest=true|false</c> filter selects PRs vs non-PR issues, and
/// every row/response carries a <c>content_type</c> of <c>pr</c> or
/// <c>issue</c> derived from <c>IsPullRequest</c>.
/// </summary>
public class ItemsControllerPrFilterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly ItemsController _controller;

    public ItemsControllerPrFilterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_items_pr_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _controller = new ItemsController(_db);

        using SqliteConnection conn = _db.OpenConnection();
        GitHubIssueRecord.Insert(conn, MakeIssue("HL7/fhir#1", 1, isPr: false, "An issue"));
        GitHubIssueRecord.Insert(conn, MakeIssue("HL7/fhir#2", 2, isPr: true, "A pull request"));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void ListItems_PullRequestTrue_ReturnsOnlyPr()
    {
        ItemListResponse resp = GetList(pullRequest: true);
        ItemSummary item = Assert.Single(resp.Items);
        Assert.Equal("HL7/fhir#2", item.Id);
        Assert.Equal(ContentTypes.Pr, item.Metadata!["content_type"]);
    }

    [Fact]
    public void ListItems_PullRequestFalse_ReturnsOnlyIssue()
    {
        ItemListResponse resp = GetList(pullRequest: false);
        ItemSummary item = Assert.Single(resp.Items);
        Assert.Equal("HL7/fhir#1", item.Id);
        Assert.Equal(ContentTypes.Issue, item.Metadata!["content_type"]);
    }

    [Fact]
    public void ListItems_Omitted_ReturnsBoth()
    {
        ItemListResponse resp = GetList(pullRequest: null);
        Assert.Equal(2, resp.Items.Count);
    }

    [Fact]
    public void GetItem_Pr_IsLabeledPr()
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetItem("HL7/fhir#2", includeContent: false, includeComments: false));
        ItemResponse resp = Assert.IsType<ItemResponse>(ok.Value);
        Assert.Equal(ContentTypes.Pr, resp.ContentType);
        Assert.Equal(ContentTypes.Pr, resp.Metadata!["content_type"]);
    }

    [Fact]
    public void GetContent_Pr_IsLabeledPr()
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetContent("HL7/fhir#2", format: null));
        ContentResponse resp = Assert.IsType<ContentResponse>(ok.Value);
        Assert.Equal(ContentTypes.Pr, resp.ContentType);
    }

    [Fact]
    public void GetSnapshot_Issue_IsLabeledIssue()
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetSnapshot("HL7/fhir#1", includeComments: false, includeRefs: false));
        SnapshotResponse resp = Assert.IsType<SnapshotResponse>(ok.Value);
        Assert.Equal(ContentTypes.Issue, resp.ContentType);
    }

    private ItemListResponse GetList(bool? pullRequest)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.ListItems(limit: null, offset: null, pullRequest: pullRequest));
        return Assert.IsType<ItemListResponse>(ok.Value);
    }

    private static GitHubIssueRecord MakeIssue(string uniqueKey, int number, bool isPr, string title) => new()
    {
        Id = GitHubIssueRecord.GetIndex(),
        UniqueKey = uniqueKey,
        RepoFullName = "HL7/fhir",
        Number = number,
        IsPullRequest = isPr,
        Title = title,
        Body = "body",
        State = "open",
        Author = "octocat",
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
