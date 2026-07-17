using System.Reflection;
using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Pins the single-repo route introduced by the preparer-hydration
/// feature (slot 0517-02, Phase 2): GET /repos/{owner}/{name} returns
/// the per-row payload mirrored from GET /repos and 404s for unknown
/// repos.
/// </summary>
public class ReposControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly ReposController _controller;

    public ReposControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_repos_ctrl_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _controller = new ReposController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void GetRepository_ReturnsPayloadForKnownRepo()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            GitHubRepoRecord.Insert(conn, new GitHubRepoRecord
            {
                Id = GitHubRepoRecord.GetIndex(),
                FullName = "HL7/fhir",
                Owner = "HL7",
                Name = "fhir",
                Description = "FHIR core spec",
                HasIssues = true,
                LastFetchedAt = DateTimeOffset.UtcNow,
                Category = "FhirCore",
            });
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetRepository("HL7", "fhir"));
        object payload = ok.Value!;
        Assert.Equal("HL7/fhir", GetValue<string>(payload, "FullName"));
        Assert.Equal("FHIR core spec", GetValue<string>(payload, "Description"));
        Assert.Equal("FhirCore", GetValue<string>(payload, "Category"));
        Assert.Equal("https://github.com/HL7/fhir", GetValue<string>(payload, "url"));
        Assert.True(GetValue<bool>(payload, "HasIssues"));
        Assert.Equal(0, GetValue<int>(payload, "issueCount"));
        Assert.Equal(0, GetValue<int>(payload, "prCount"));
    }

    [Fact]
    public void GetRepository_ReturnsNotFoundForUnknownRepo()
    {
        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(_controller.GetRepository("nonexistent", "repo"));
        Assert.NotNull(notFound.Value);
    }

    private static T GetValue<T>(object source, string propertyName)
    {
        PropertyInfo prop = source.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on {source.GetType().Name}");
        object? value = prop.GetValue(source);
        return (T)value!;
    }
}
