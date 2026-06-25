using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubXRefRebuilderIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly GitHubXRefRebuilder _rebuilder;

    public GitHubXRefRebuilderIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"xref_integ_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _rebuilder = new GitHubXRefRebuilder(
            _db,
            Options.Create(new GitHubServiceOptions()),
            NullLogger<GitHubXRefRebuilder>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private static GitHubIssueRecord MakeIssue(string repo, int number, string title, string? body = null) => new()
    {
        Id = GitHubIssueRecord.GetIndex(),
        UniqueKey = $"{repo}#{number}",
        RepoFullName = repo,
        Number = number,
        IsPullRequest = false,
        Title = title,
        Body = body ?? $"Body for {title}",
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

    [Fact]
    public void RebuildAllRepos_MultipleRepos_KeepsBothReposXRefs()
    {
        // Arrange — two repos with different Jira references
        using SqliteConnection connection = _db.OpenConnection();
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/fhir", 1, "Fix for FHIR-100"));
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/fhir", 2, "See FHIR-200"));
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/us-core", 1, "Relates to FHIR-300"));
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/us-core", 2, "About FHIR-400"));

        // Act — batch rebuild across both repos
        _rebuilder.RebuildAllRepos(["HL7/fhir", "HL7/us-core"]);

        // Assert — all four Jira references survive
        List<JiraXRefRecord> allRefs = JiraXRefRecord.SelectList(connection);
        Assert.Equal(4, allRefs.Count);
        Assert.Contains(allRefs, r => r.JiraKey == "FHIR-100");
        Assert.Contains(allRefs, r => r.JiraKey == "FHIR-200");
        Assert.Contains(allRefs, r => r.JiraKey == "FHIR-300");
        Assert.Contains(allRefs, r => r.JiraKey == "FHIR-400");
    }

    [Fact]
    public void RebuildAll_SingleRepo_ClearsAndRebuilds()
    {
        // Arrange — seed issues in two repos, then rebuild for just one
        using SqliteConnection connection = _db.OpenConnection();
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/fhir", 1, "Fix for FHIR-500"));
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/us-core", 1, "See FHIR-600"));

        // Act — rebuild for a single repo (clears ALL then inserts one)
        _rebuilder.RebuildAll("HL7/fhir");

        // Assert — only the single repo's references remain
        List<JiraXRefRecord> allRefs = JiraXRefRecord.SelectList(connection);
        Assert.Single(allRefs);
        Assert.Equal("FHIR-500", allRefs[0].JiraKey);
    }

    [Fact]
    public void RebuildAllRepos_EmptyList_ClearsAll()
    {
        // Arrange — seed some xrefs first
        using SqliteConnection connection = _db.OpenConnection();
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/fhir", 1, "FHIR-700"));
        _rebuilder.RebuildAll("HL7/fhir");
        Assert.NotEmpty(JiraXRefRecord.SelectList(connection));

        // Act — rebuild with empty list
        _rebuilder.RebuildAllRepos([]);

        // Assert — all xrefs cleared
        Assert.Empty(JiraXRefRecord.SelectList(connection));
    }

    [Fact]
    public void RebuildAllRepos_IsIdempotent()
    {
        // Arrange
        using SqliteConnection connection = _db.OpenConnection();
        GitHubIssueRecord.Insert(connection, MakeIssue("HL7/fhir", 1, "FHIR-800"));

        // Act — rebuild twice
        _rebuilder.RebuildAllRepos(["HL7/fhir"]);
        int firstCount = JiraXRefRecord.SelectList(connection).Count;
        _rebuilder.RebuildAllRepos(["HL7/fhir"]);
        int secondCount = JiraXRefRecord.SelectList(connection).Count;

        // Assert — same count both times
        Assert.Equal(firstCount, secondCount);
        Assert.Equal(1, firstCount);
    }

    // ── Repo-scoped bare-number resolution ───────────────────────────

    private static GitHubCommitRecord MakeCommit(string repo, string sha, string message, string? body = null) => new()
    {
        Id = GitHubCommitRecord.GetIndex(),
        Sha = sha,
        RepoFullName = repo,
        Message = message,
        Body = body,
        Author = "Dev",
        Date = DateTimeOffset.UtcNow,
        Url = $"https://github.com/{repo}/commit/{sha}",
    };

    private static GitHubFileContentRecord MakeFile(string repo, string path, string content) => new()
    {
        Id = GitHubFileContentRecord.GetIndex(),
        RepoFullName = repo,
        FilePath = path,
        FileExtension = ".md",
        ParserType = "text",
        ContentText = content,
        ContentLength = content.Length,
        ExtractedLength = content.Length,
    };

    [Fact]
    public void RebuildAllRepos_ScopedCommit_ResolvesBareNumber()
    {
        using SqliteConnection connection = _db.OpenConnection();
        GitHubCommitRecord.Insert(connection, MakeCommit("HL7/fhir", "sha-bare-1", "Fixed 54873 in core"));

        _rebuilder.RebuildAllRepos(["HL7/fhir"]);

        JiraXRefRecord row = Assert.Single(JiraXRefRecord.SelectList(connection));
        Assert.Equal("FHIR-54873", row.JiraKey);
        Assert.Equal("54873", row.OriginalLiteral);
        Assert.Equal("commit", row.ContentType);
    }

    [Fact]
    public void RebuildAllRepos_ScopedCommitBody_ResolvesBareNumber()
    {
        using SqliteConnection connection = _db.OpenConnection();
        GitHubCommitRecord.Insert(connection, MakeCommit("HL7/fhir", "sha-bare-2", "Subject only", body: "Closes 54873"));

        _rebuilder.RebuildAllRepos(["HL7/fhir"]);

        JiraXRefRecord row = Assert.Single(JiraXRefRecord.SelectList(connection));
        Assert.Equal("FHIR-54873", row.JiraKey);
    }

    [Fact]
    public void RebuildAllRepos_UnscopedRepoCommit_YieldsNoBareNumber()
    {
        // HL7/us-core is not in the default config, so it has no Jira scope.
        using SqliteConnection connection = _db.OpenConnection();
        GitHubCommitRecord.Insert(connection, MakeCommit("HL7/us-core", "sha-bare-3", "Fixed 54873 in core"));

        _rebuilder.RebuildAllRepos(["HL7/us-core"]);

        Assert.Empty(JiraXRefRecord.SelectList(connection));
    }

    [Fact]
    public void RebuildAllRepos_FileContent_NotBareMatched()
    {
        using SqliteConnection connection = _db.OpenConnection();
        GitHubFileContentRecord.Insert(connection, MakeFile("HL7/fhir", "notes.md", "incidental integer 54873 in prose"));

        _rebuilder.RebuildAllRepos(["HL7/fhir"]);

        Assert.Empty(JiraXRefRecord.SelectList(connection));
    }
}
