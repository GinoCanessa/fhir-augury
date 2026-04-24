using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Regression guard for the architecture decision recorded on
/// <see cref="GitHubRepoWorkGroupRecord"/>: derived work-group attribution
/// for a repo lives in its own <c>github_repo_workgroups</c> table so that
/// API-driven upserts to <c>github_repos</c> (which fully rewrite the row
/// from <c>MapRepo</c> output) cannot blank out the value derived by
/// <see cref="Ingestion.WorkGroupResolutionPass"/>.
/// </summary>
public class RepoUpsertPreservesDerivedWorkGroupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitHubDatabase _database;

    public RepoUpsertPreservesDerivedWorkGroupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wg-upsert-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _database = new GitHubDatabase(Path.Combine(_tempDir, "test.db"), NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        _database.Dispose();
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    [Fact]
    public void RepoUpsert_DoesNotBlankDerivedWorkGroup()
    {
        const string Repo = "HL7/fhir";
        using SqliteConnection conn = _database.OpenConnection();

        // Seed: repo row + derived work-group attribution.
        GitHubRepoRecord initial = new()
        {
            Id = GitHubRepoRecord.GetIndex(),
            FullName = Repo,
            Owner = "HL7",
            Name = "fhir",
            Description = "initial",
            HasIssues = true,
            LastFetchedAt = DateTimeOffset.UtcNow,
            Category = "FhirCore",
        };
        GitHubRepoRecord.Insert(conn, initial, ignoreDuplicates: true);

        GitHubRepoWorkGroupRecord.Insert(conn, new GitHubRepoWorkGroupRecord
        {
            Id = GitHubRepoWorkGroupRecord.GetIndex(),
            RepoFullName = Repo,
            WorkGroup = "fhir-i",
            WorkGroupRaw = null,
            Source = "majority-jira-spec",
            ResolvedAt = DateTimeOffset.UtcNow,
        });

        // Simulate the upsert path: rewrite the github_repos row in-place
        // using MapRepo-style data that knows nothing about work groups.
        GitHubRepoRecord? existing = GitHubRepoRecord.SelectSingle(conn, FullName: Repo);
        Assert.NotNull(existing);
        GitHubRepoRecord rewritten = new()
        {
            Id = existing.Id,
            FullName = Repo,
            Owner = "HL7",
            Name = "fhir",
            Description = "updated description from API",
            HasIssues = true,
            LastFetchedAt = DateTimeOffset.UtcNow,
            Category = "FhirCore",
        };
        GitHubRepoRecord.Update(conn, rewritten);

        // The derived work-group row must be untouched.
        GitHubRepoWorkGroupRecord? wg = GitHubRepoWorkGroupRecord.SelectSingle(conn, RepoFullName: Repo);
        Assert.NotNull(wg);
        Assert.Equal("fhir-i", wg.WorkGroup);
        Assert.Equal("majority-jira-spec", wg.Source);
    }
}
