using FhirAugury.Common;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Api;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubCrossSourceRelatedTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public GitHubCrossSourceRelatedTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_crosssource_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
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
        ContentType = sourceType,
        SourceId = sourceId,
        LinkType = "mentions",
        JiraKey = jiraKey,
        OriginalLiteral = jiraKey,
        Context = context ?? $"Ref to {jiraKey}",
    };

    private static GitHubCommitPrLinkRecord CreateCommitPrLink(string sha, string repo, int prNumber) => new()
    {
        Id = GitHubCommitPrLinkRecord.GetIndex(),
        CommitSha = sha,
        RepoFullName = repo,
        PrNumber = prNumber,
    };

    /// <summary>
    /// Simulates the cross-source related resolution logic from the HTTP API.
    /// Given a Jira key, finds all GitHub items referencing it via xref records.
    /// </summary>
    private List<GitHubHttpApi.ResolvedItem> FindRelatedViaJiraSeed(string jiraKey, int limit = 10)
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(conn, JiraKey: jiraKey);
        List<GitHubHttpApi.ResolvedItem> results = [];
        HashSet<string> seen = [];

        foreach (JiraXRefRecord jiraRef in refs)
        {
            GitHubHttpApi.ResolvedItem? resolved = GitHubHttpApi.ResolveXRef(conn, jiraRef);
            if (resolved is null || !seen.Add(resolved.Id)) continue;
            results.Add(resolved);
            if (results.Count >= limit) break;
        }

        return results;
    }

    /// <summary>
    /// Simulates intra-source related resolution (same-source, via shared Jira keys).
    /// </summary>
    private List<GitHubHttpApi.ResolvedItem> FindRelatedIntraSource(string key, int limit = 10)
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<JiraXRefRecord> sourceRefs = JiraXRefRecord.SelectList(conn, SourceId: key);
        HashSet<string> relatedIds = [];
        List<GitHubHttpApi.ResolvedItem> results = [];

        foreach (JiraXRefRecord jiraRef in sourceRefs)
        {
            List<JiraXRefRecord> sameKeyRefs = JiraXRefRecord.SelectList(conn, JiraKey: jiraRef.JiraKey);
            foreach (JiraXRefRecord r in sameKeyRefs)
            {
                if (r.SourceId == key) continue;
                GitHubHttpApi.ResolvedItem? resolved = GitHubHttpApi.ResolveXRef(conn, r);
                if (resolved is null || !relatedIds.Add(resolved.Id)) continue;
                results.Add(resolved);
                if (results.Count >= limit) break;
            }
            if (results.Count >= limit) break;
        }

        return results;
    }

    // ── Tests ─────────────────────────────────────────────────────

    [Fact]
    public void GetRelated_WithJiraSeed_ReturnsMatchingIssues()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue1 = CreateIssue("HL7/fhir", 100, "Patient validation fix");
        GitHubIssueRecord issue2 = CreateIssue("HL7/fhir", 200, "Observation update");
        GitHubIssueRecord.Insert(conn, issue1);
        GitHubIssueRecord.Insert(conn, issue2);

        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#100", "FHIR-55001"));
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#200", "FHIR-55001"));

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-55001");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "HL7/fhir#100");
        Assert.Contains(results, r => r.Id == "HL7/fhir#200");
    }

    [Fact]
    public void GetRelated_WithJiraSeed_ResolvesCommentsToParentIssue()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord parentIssue = CreateIssue("HL7/fhir", 42, "Parent issue for comment");
        GitHubIssueRecord.Insert(conn, parentIssue);

        // Comment source ID format: "repo#issueNum:commentId"
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Comment, "HL7/fhir#42:12345", "FHIR-55002"));

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-55002");

        Assert.Single(results);
        Assert.Equal("HL7/fhir#42", results[0].Id);
        Assert.Equal("Parent issue for comment", results[0].Title);
    }

    [Fact]
    public void GetRelated_WithJiraSeed_ResolvesCommitsToLinkedPR()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord pr = CreateIssue("HL7/fhir", 300, "PR fixing observation", isPr: true);
        GitHubIssueRecord.Insert(conn, pr);

        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Commit, "abc123def", "FHIR-55003"));
        GitHubCommitPrLinkRecord.Insert(conn, CreateCommitPrLink("abc123def", "HL7/fhir", 300));

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-55003");

        Assert.Single(results);
        Assert.Equal("HL7/fhir#300", results[0].Id);
        Assert.Equal("PR fixing observation", results[0].Title);
    }

    [Fact]
    public void GetRelated_WithJiraSeed_NoMatches_ReturnsEmpty()
    {
        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-99999");

        Assert.Empty(results);
    }

    [Fact]
    public void GetRelated_WithJiraSeed_DeduplicatesResolvedIssues()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue = CreateIssue("HL7/fhir", 50, "Deduplicated issue");
        GitHubIssueRecord.Insert(conn, issue);

        // Two different ref types both resolving to the same issue
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#50", "FHIR-55004"));
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Comment, "HL7/fhir#50:99999", "FHIR-55004"));

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-55004");

        Assert.Single(results);
        Assert.Equal("HL7/fhir#50", results[0].Id);
    }

    [Fact]
    public void GetRelated_WithEmptySeedSource_UsesIntraSourceLogic()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue1 = CreateIssue("HL7/fhir", 10, "Issue referencing FHIR-77001");
        GitHubIssueRecord issue2 = CreateIssue("HL7/fhir", 20, "Another issue about FHIR-77001");
        GitHubIssueRecord.Insert(conn, issue1);
        GitHubIssueRecord.Insert(conn, issue2);

        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#10", "FHIR-77001"));
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#20", "FHIR-77001"));

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedIntraSource("HL7/fhir#10");

        Assert.Single(results);
        Assert.Equal("HL7/fhir#20", results[0].Id);
    }

    [Fact]
    public void GetRelated_WithJiraSeed_RespectsLimit()
    {
        using SqliteConnection conn = _db.OpenConnection();

        for (int i = 1; i <= 5; i++)
        {
            GitHubIssueRecord issue = CreateIssue("HL7/fhir", 400 + i, $"Limit test issue {i}");
            GitHubIssueRecord.Insert(conn, issue);
            JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, $"HL7/fhir#{400 + i}", "FHIR-55005"));
        }

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-55005", limit: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void GetRelated_WithJiraSeed_IncludesCorrectUrl()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubIssueRecord issue = CreateIssue("HL7/fhir", 777, "URL check issue");
        GitHubIssueRecord.Insert(conn, issue);
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#777", "FHIR-55006"));

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-55006");

        Assert.Single(results);
        Assert.Equal("https://github.com/HL7/fhir/issues/777", results[0].Url);
    }

    [Fact]
    public void GetRelated_WithJiraSeed_SkipsUnresolvableRefs()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Insert jira ref pointing to a non-existent issue
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#9999", "FHIR-55007"));

        // Insert another ref pointing to an existing issue
        GitHubIssueRecord issue = CreateIssue("HL7/fhir", 888, "Resolvable issue");
        GitHubIssueRecord.Insert(conn, issue);
        JiraXRefRecord.Insert(conn, CreateJiraRef(ContentTypes.Issue, "HL7/fhir#888", "FHIR-55007"));

        List<GitHubHttpApi.ResolvedItem> results = FindRelatedViaJiraSeed("FHIR-55007");

        Assert.Single(results);
        Assert.Equal("HL7/fhir#888", results[0].Id);
    }

    [Fact]
    public void BuildIssueUrl_FormatsCorrectly()
    {
        Assert.Equal("https://github.com/HL7/fhir/issues/42", GitHubHttpApi.BuildIssueUrl("HL7/fhir#42"));
        Assert.Equal("https://github.com/HL7/fhir", GitHubHttpApi.BuildIssueUrl("HL7/fhir"));
    }
}
