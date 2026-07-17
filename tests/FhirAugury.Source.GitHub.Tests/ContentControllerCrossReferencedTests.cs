using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class ContentControllerCrossReferencedTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly ContentController _controller;

    public ContentControllerCrossReferencedTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"xref_controller_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _controller = new ContentController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void CrossReferenced_CommitSha_ReturnsJiraHit()
    {
        const string sha = "abc1234def5678abc1234def5678abc1234def56";
        using (SqliteConnection connection = _db.OpenConnection())
        {
            GitHubCommitRecord.Insert(connection, new GitHubCommitRecord
            {
                Id = GitHubCommitRecord.GetIndex(),
                Sha = sha,
                RepoFullName = "HL7/fhir",
                Message = "Fixed 54873 in core",
                Author = "Dev",
                Date = DateTimeOffset.UtcNow,
                Url = $"https://github.com/HL7/fhir/commit/{sha}",
            });
            JiraXRefRecord.Insert(connection, new JiraXRefRecord
            {
                Id = JiraXRefRecord.GetIndex(),
                ContentType = "commit",
                SourceId = sha,
                LinkType = "mentions",
                Context = "Fixed 54873 in core",
                JiraKey = "FHIR-54873",
                OriginalLiteral = "54873",
            });
        }

        IActionResult result = _controller.CrossReferenced(value: sha, sourceType: null, limit: null);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);
        CrossReferenceHit hit = Assert.Single(response.Hits);
        Assert.Equal("FHIR-54873", hit.TargetId);
        Assert.Equal("jira", hit.TargetType);
        Assert.Equal(sha, hit.SourceId);
        Assert.Equal("commit", hit.ContentType);
    }

    [Fact]
    public void CrossReferenced_UnknownSha_ReturnsNoHits()
    {
        IActionResult result = _controller.CrossReferenced(
            value: "ffffffffffffffffffffffffffffffffffffffff", sourceType: null, limit: null);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);
        Assert.Empty(response.Hits);
    }
}
