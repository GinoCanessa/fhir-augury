using FhirAugury.Common.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Pins the response-shape additions introduced by the preparer-hydration
/// feature (slot 0517-02, Phase 2): the additional Jira-issue metadata
/// keys land in <c>ItemResponse.Metadata</c> when the underlying record
/// carries them, and are absent (not empty-string) when null.
/// </summary>
public class ItemsControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly ItemsController _controller;

    public ItemsControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_items_ctrl_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        IOptions<JiraServiceOptions> options = Options.Create(new JiraServiceOptions { BaseUrl = "https://jira.example.com" });
        _controller = new ItemsController(_db, options);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void GetItem_SurfacesAllHydrationMetadataKeysWhenPresent()
    {
        InsertIssue(NewIssue("FHIR-100", configure: i =>
        {
            i.CommentCount = 7;
            i.Resolution = "Persuasive";
            i.ResolutionDescriptionPlain = "Done because reasons.";
            i.RaisedInVersion = "5.0.0-ballot1";
            i.SelectedBallot = "2026-Jan";
            i.ChangeCategory = "Refinement";
            i.Impact = "Compatible, substantive";
            i.DuplicateOf = "FHIR-99";
            i.RelatedIssues = "FHIR-101, FHIR-102";
            i.RelatedArtifacts = "FHIR-103, R4/observation";
            i.DescriptionPlain = "plaintext description body";
        }));

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetItem("FHIR-100", includeContent: true, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);
        Dictionary<string, string> metadata = response.Metadata!;

        Assert.Equal("7", metadata["comment_count"]);
        Assert.Equal("Persuasive", metadata["resolution"]);
        Assert.Equal("Done because reasons.", metadata["resolution_description_plain"]);
        Assert.Equal("5.0.0-ballot1", metadata["raised_in_version"]);
        Assert.Equal("2026-Jan", metadata["selected_ballot"]);
        Assert.Equal("Refinement", metadata["change_category"]);
        Assert.Equal("Compatible, substantive", metadata["impact"]);
        Assert.Equal("FHIR-99", metadata["duplicate_of"]);
        Assert.Equal("FHIR-101, FHIR-102", metadata["related_issues"]);
        Assert.Equal("FHIR-103, R4/observation", metadata["related_artifacts"]);
        Assert.Equal("plaintext description body", metadata["description_plain"]);
    }

    [Fact]
    public void GetItem_OmitsNullMetadataKeys()
    {
        InsertIssue(NewIssue("FHIR-200"));

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetItem("FHIR-200", includeContent: true, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);
        Dictionary<string, string> metadata = response.Metadata!;

        Assert.False(metadata.ContainsKey("raised_in_version"));
        Assert.False(metadata.ContainsKey("selected_ballot"));
        Assert.False(metadata.ContainsKey("change_category"));
        Assert.False(metadata.ContainsKey("impact"));
        Assert.False(metadata.ContainsKey("duplicate_of"));
        Assert.False(metadata.ContainsKey("related_issues"));
        Assert.False(metadata.ContainsKey("related_artifacts"));
        Assert.False(metadata.ContainsKey("description_plain"));
        Assert.False(metadata.ContainsKey("resolution_description_plain"));
        Assert.Equal("0", metadata["comment_count"]);
    }

    [Fact]
    public void GetItem_OmitsDescriptionPlainWhenIncludeContentFalse()
    {
        InsertIssue(NewIssue("FHIR-300", configure: i => i.DescriptionPlain = "should not surface"));

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetItem("FHIR-300", includeContent: false, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);
        Assert.False(response.Metadata!.ContainsKey("description_plain"));
    }

    private void InsertIssue(JiraIssueRecord issue)
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, issue);
    }

    private static JiraIssueRecord NewIssue(string key, Action<JiraIssueRecord>? configure = null)
    {
        JiraIssueRecord issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = "FHIR",
            Title = $"Issue {key}",
            Description = null,
            DescriptionPlain = null,
            Summary = null,
            Type = "Bug",
            Priority = "Major",
            Status = "Triaged",
            Resolution = null,
            ResolutionDescription = null,
            ResolutionDescriptionPlain = null,
            Assignee = null,
            Reporter = null,
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
            ChangeCategory = null,
            ChangeImpact = null,
        };
        configure?.Invoke(issue);
        return issue;
    }
}
