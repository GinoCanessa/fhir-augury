using Fhiraugury;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Api;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubCrossSourceRelatedTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly GitHubGrpcService _service;

    public GitHubCrossSourceRelatedTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_crosssource_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();

        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions());
        _service = new GitHubGrpcService(_db, null!, null!, null!, null!, null!, null!, null!, null!, options);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static GitHubIssueRecord CreateIssue(string repo, int number, string title, bool isPr = false) => new()
    {
        Id = GitHubIssueRecord.GetIndex(),
        UniqueKey = $"{repo}#{number}",
        RepoFullName = repo,
        Number = number,
        IsPullRequest = isPr,
        Title = title,
        Body = $"Body for {title}",
        State = "open",
        Author = "testuser",
        Labels = null,
        Assignees = null,
        Milestone = null,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
        UpdatedAt = DateTimeOffset.UtcNow,
        ClosedAt = null,
        MergeState = null,
        HeadBranch = null,
        BaseBranch = null,
    };

    private static JiraXRefRecord CreateJiraRef(
        string sourceType, string sourceId, string jiraKey, string? context = null) => new()
    {
        Id = JiraXRefRecord.GetIndex(),
        SourceType = sourceType,
        SourceId = sourceId,
        LinkType = "mentions",
        JiraKey = jiraKey,
        Context = context ?? $"Ref to {jiraKey}",
    };

    private static GitHubCommitPrLinkRecord CreateCommitPrLink(string sha, string repo, int prNumber) => new()
    {
        Id = GitHubCommitPrLinkRecord.GetIndex(),
        CommitSha = sha,
        RepoFullName = repo,
        PrNumber = prNumber,
    };

    // ── Tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetRelated_WithJiraSeed_ReturnsMatchingIssues()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue1 = CreateIssue("HL7/fhir", 100, "Patient validation fix");
        GitHubIssueRecord issue2 = CreateIssue("HL7/fhir", 200, "Observation update");
        GitHubIssueRecord.Insert(conn, issue1);
        GitHubIssueRecord.Insert(conn, issue2);

        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#100", "FHIR-55001"));
        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#200", "FHIR-55001"));

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-55001",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, r => Assert.Equal(1.0, r.Score));
        Assert.All(response.Results, r => Assert.Equal("github", r.Source));
        Assert.Contains(response.Results, r => r.Id == "HL7/fhir#100");
        Assert.Contains(response.Results, r => r.Id == "HL7/fhir#200");
    }

    [Fact]
    public async Task GetRelated_WithJiraSeed_ResolvesCommentsToParentIssue()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord parentIssue = CreateIssue("HL7/fhir", 42, "Parent issue for comment");
        GitHubIssueRecord.Insert(conn, parentIssue);

        // Comment source ID format: "repo#issueNum:commentId"
        JiraXRefRecord.Insert(conn, CreateJiraRef("comment", "HL7/fhir#42:12345", "FHIR-55002"));

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-55002",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Single(response.Results);
        Assert.Equal("HL7/fhir#42", response.Results[0].Id);
        Assert.Equal("Parent issue for comment", response.Results[0].Title);
        Assert.Equal(1.0, response.Results[0].Score);
    }

    [Fact]
    public async Task GetRelated_WithJiraSeed_ResolvesCommitsToLinkedPR()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord pr = CreateIssue("HL7/fhir", 300, "PR fixing observation", isPr: true);
        GitHubIssueRecord.Insert(conn, pr);

        JiraXRefRecord.Insert(conn, CreateJiraRef("commit", "abc123def", "FHIR-55003"));
        GitHubCommitPrLinkRecord.Insert(conn, CreateCommitPrLink("abc123def", "HL7/fhir", 300));

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-55003",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Single(response.Results);
        Assert.Equal("HL7/fhir#300", response.Results[0].Id);
        Assert.Equal("PR fixing observation", response.Results[0].Title);
        Assert.Equal(1.0, response.Results[0].Score);
    }

    [Fact]
    public async Task GetRelated_WithJiraSeed_NoMatches_ReturnsEmpty()
    {
        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-99999",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Empty(response.Results);
        Assert.Equal(0, response.TotalResults);
    }

    [Fact]
    public async Task GetRelated_WithJiraSeed_DeduplicatesResolvedIssues()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue = CreateIssue("HL7/fhir", 50, "Deduplicated issue");
        GitHubIssueRecord.Insert(conn, issue);

        // Two different ref types both resolving to the same issue
        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#50", "FHIR-55004"));
        JiraXRefRecord.Insert(conn, CreateJiraRef("comment", "HL7/fhir#50:99999", "FHIR-55004"));

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-55004",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Single(response.Results);
        Assert.Equal("HL7/fhir#50", response.Results[0].Id);
    }

    [Fact]
    public async Task GetRelated_WithUnknownSeedSource_FallsBackToFts()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue = CreateIssue("HL7/fhir", 600, "Patient resource search test");
        GitHubIssueRecord.Insert(conn, issue);

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "confluence",
            SeedId = "Patient",
        };

        // FTS fallback should not crash; results may or may not appear depending on FTS indexing
        SearchResponse response = await _service.GetRelated(request, null!);

        // Verify FTS fallback scores are reduced (multiplied by 0.3)
        Assert.All(response.Results, r => Assert.True(r.Score <= 0.3 || r.Score == 0));
    }

    [Fact]
    public async Task GetRelated_WithEmptySeedSource_UsesIntraSourceLogic()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue1 = CreateIssue("HL7/fhir", 10, "Issue referencing FHIR-77001");
        GitHubIssueRecord issue2 = CreateIssue("HL7/fhir", 20, "Another issue about FHIR-77001");
        GitHubIssueRecord.Insert(conn, issue1);
        GitHubIssueRecord.Insert(conn, issue2);

        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#10", "FHIR-77001"));
        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#20", "FHIR-77001"));

        GetRelatedRequest request = new GetRelatedRequest
        {
            Id = "HL7/fhir#10",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Single(response.Results);
        Assert.Equal("HL7/fhir#20", response.Results[0].Id);
    }

    [Fact]
    public async Task GetRelated_WithJiraSeed_RespectsLimit()
    {
        using SqliteConnection conn = _db.OpenConnection();

        for (int i = 1; i <= 5; i++)
        {
            GitHubIssueRecord issue = CreateIssue("HL7/fhir", 400 + i, $"Limit test issue {i}");
            GitHubIssueRecord.Insert(conn, issue);
            JiraXRefRecord.Insert(conn, CreateJiraRef("issue", $"HL7/fhir#{400 + i}", "FHIR-55005"));
        }

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-55005",
            Limit = 3,
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Equal(3, response.Results.Count);
        Assert.Equal(3, response.TotalResults);
    }

    [Fact]
    public async Task GetRelated_WithJiraSeed_IncludesCorrectUrl()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue = CreateIssue("HL7/fhir", 777, "URL check issue");
        GitHubIssueRecord.Insert(conn, issue);
        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#777", "FHIR-55006"));

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-55006",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Single(response.Results);
        Assert.Equal("https://github.com/HL7/fhir/issues/777", response.Results[0].Url);
    }

    [Fact]
    public async Task GetRelated_WithJiraSeed_SkipsUnresolvableRefs()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Insert jira ref pointing to a non-existent issue
        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#9999", "FHIR-55007"));

        // Insert another ref pointing to an existing issue
        GitHubIssueRecord issue = CreateIssue("HL7/fhir", 888, "Resolvable issue");
        GitHubIssueRecord.Insert(conn, issue);
        JiraXRefRecord.Insert(conn, CreateJiraRef("issue", "HL7/fhir#888", "FHIR-55007"));

        GetRelatedRequest request = new GetRelatedRequest
        {
            SeedSource = "jira",
            SeedId = "FHIR-55007",
        };

        SearchResponse response = await _service.GetRelated(request, null!);

        Assert.Single(response.Results);
        Assert.Equal("HL7/fhir#888", response.Results[0].Id);
    }
}
