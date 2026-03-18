using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Integration.Tests;

public class IngestEndpointTests : IClassFixture<IngestEndpointTests.IngestTestFactory>, IDisposable
{
    private readonly IngestTestFactory _factory;

    public IngestEndpointTests(IngestTestFactory factory)
    {
        _factory = factory;
    }

    public void Dispose() => _factory.Cleanup();

    [Fact]
    public async Task Post_TriggerIngestion_ReturnsAccepted()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/v1/ingest/jira?type=Incremental", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("requestId", out var requestId));
        Assert.False(string.IsNullOrEmpty(requestId.GetString()));
    }

    [Fact]
    public async Task Post_TriggerIngestion_InvalidType_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/v1/ingest/jira?type=InvalidType", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_SubmitItem_ReturnsAccepted()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/ingest/jira/item", new { identifier = "FHIR-12345" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("identifier", out var id));
        Assert.Equal("FHIR-12345", id.GetString());
    }

    [Fact]
    public async Task Post_SubmitItem_MissingIdentifier_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/ingest/jira/item", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_SyncAll_ReturnsAccepted()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/v1/ingest/sync", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("requests", out _));
    }

    [Fact]
    public async Task Get_Status_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/ingest/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("queueDepth", out _));
    }

    [Fact]
    public async Task Get_History_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/ingest/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_Schedule_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/ingest/schedule");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public class IngestTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fhir-augury-test-ingest-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Remove existing DatabaseService registration
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DatabaseService));
                if (descriptor is not null) services.Remove(descriptor);

                var db = new DatabaseService(_dbPath);
                db.InitializeDatabase();
                services.AddSingleton(db);
            });
        }

        public void Cleanup()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
            try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
            try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
        }
    }
}
