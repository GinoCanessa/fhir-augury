using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubRepoWorkGroupRecordTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitHubDatabase _database;

    public GitHubRepoWorkGroupRecordTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gh-repo-wg-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Insert_RoundTrips_WithCodeAndRaw()
    {
        using SqliteConnection conn = _database.OpenConnection();
        DateTimeOffset when = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);
        GitHubRepoWorkGroupRecord row = new()
        {
            Id = GitHubRepoWorkGroupRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            WorkGroup = "fhir-i",
            WorkGroupRaw = "FHIR Infrastructure",
            Source = "config",
            ResolvedAt = when,
        };
        GitHubRepoWorkGroupRecord.Insert(conn, row);

        GitHubRepoWorkGroupRecord loaded = GitHubRepoWorkGroupRecord.SelectList(conn, RepoFullName: "HL7/fhir").Single();
        Assert.Equal("fhir-i", loaded.WorkGroup);
        Assert.Equal("FHIR Infrastructure", loaded.WorkGroupRaw);
        Assert.Equal("config", loaded.Source);
        Assert.Equal(when, loaded.ResolvedAt);
    }

    [Fact]
    public void Insert_AllowsCodeNullWithRawOnly()
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubRepoWorkGroupRecord row = new()
        {
            Id = GitHubRepoWorkGroupRecord.GetIndex(),
            RepoFullName = "HL7/example",
            WorkGroup = null,
            WorkGroupRaw = "Some Workgroup That Doesn't Resolve",
            Source = "majority-jira-spec",
            ResolvedAt = DateTimeOffset.UtcNow,
        };
        GitHubRepoWorkGroupRecord.Insert(conn, row);

        GitHubRepoWorkGroupRecord loaded = GitHubRepoWorkGroupRecord.SelectList(conn, RepoFullName: "HL7/example").Single();
        Assert.Null(loaded.WorkGroup);
        Assert.Equal("Some Workgroup That Doesn't Resolve", loaded.WorkGroupRaw);
        Assert.Equal("majority-jira-spec", loaded.Source);
    }

    [Fact]
    public void RepoFullName_IsUnique()
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubRepoWorkGroupRecord row1 = new()
        {
            Id = GitHubRepoWorkGroupRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            WorkGroup = "fhir-i",
            WorkGroupRaw = null,
            Source = "config",
            ResolvedAt = DateTimeOffset.UtcNow,
        };
        GitHubRepoWorkGroupRecord.Insert(conn, row1);

        GitHubRepoWorkGroupRecord row2 = new()
        {
            Id = GitHubRepoWorkGroupRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            WorkGroup = "oo",
            WorkGroupRaw = null,
            Source = "config",
            ResolvedAt = DateTimeOffset.UtcNow,
        };

        Assert.Throws<SqliteException>(() => GitHubRepoWorkGroupRecord.Insert(conn, row2));
    }
}
