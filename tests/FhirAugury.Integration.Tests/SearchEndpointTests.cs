using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Integration.Tests;

public class SearchEndpointTests : IClassFixture<SearchEndpointTests.SearchTestFactory>
{
    private readonly SearchTestFactory _factory;

    public SearchEndpointTests(SearchTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_UnifiedSearch_WithQuery_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/search?q=patient");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnifiedSearch_MissingQuery_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_SourceSearch_Jira_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/search/jira?q=patient");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_SourceSearch_UnknownSource_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/search/unknown?q=test");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Stats_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("jiraIssues", out _));
        Assert.True(json.TryGetProperty("totalItems", out _));
    }

    [Fact]
    public async Task Get_SourceStats_Jira_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/stats/jira");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_SourceStats_Unknown_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/stats/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Health_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.Equal("healthy", status.GetString());
    }

    public class SearchTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fhir-augury-test-search-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DatabaseService));
                if (descriptor is not null) services.Remove(descriptor);

                var db = new DatabaseService(_dbPath);
                db.InitializeDatabase();

                // Seed some test data
                using var conn = db.OpenConnection();

                var issue = new JiraIssueRecord
                {
                    Id = JiraIssueRecord.GetIndex(),
                    Key = "FHIR-99999",
                    ProjectKey = "FHIR",
                    Title = "Test patient issue",
                    Description = "This is about the Patient resource",
                    Summary = "Test patient issue",
                    Type = "Change Request",
                    Priority = "Medium",
                    Status = "Triaged",
                    Resolution = null,
                    ResolutionDescription = null,
                    Assignee = null,
                    Reporter = "tester",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    ResolvedAt = null,
                    WorkGroup = "FHIR Infrastructure",
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
                };
                JiraIssueRecord.Insert(conn, issue);

                services.AddSingleton(db);
            });
        }

        public void Cleanup()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
            try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
            try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Cleanup();
        }
    }
}
