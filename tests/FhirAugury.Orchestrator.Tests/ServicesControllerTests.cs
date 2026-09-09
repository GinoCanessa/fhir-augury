using System.Net;
using System.Text;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Tests;

public class ServicesControllerTests
{
    [Fact]
    public void GetServices_ReturnsTypedConfiguredAndUnobservedReadiness()
    {
        (ServicesController controller, _) = CreateController();

        OkObjectResult result =
            Assert.IsType<OkObjectResult>(controller.GetServices());
        ServicesStatusResponse response =
            Assert.IsType<ServicesStatusResponse>(result.Value);

        Assert.Null(response.LastCheckedAt);
        ServiceHealthInfo orchestrator =
            Assert.Single(response.Services, service =>
                service.ServiceKind == "orchestrator");
        Assert.Equal("healthy", orchestrator.Status);

        ServiceHealthInfo jira =
            Assert.Single(response.Services, service => service.Name == "Jira");
        Assert.Equal("source", jira.ServiceKind);
        Assert.Equal("unobserved", jira.Status);
        Assert.True(jira.Enabled);
        Assert.True(jira.Configured);
        Assert.Null(jira.CheckedAt);

        ServiceHealthInfo github =
            Assert.Single(response.Services, service => service.Name == "GitHub");
        Assert.Equal("not_configured", github.Status);
        Assert.False(github.Enabled);
        Assert.True(github.Configured);

        ServiceHealthInfo planner =
            Assert.Single(response.Services, service => service.Name == "Planner");
        Assert.Equal("processing", planner.ServiceKind);
        Assert.Equal(["Jira", "GitHub"], planner.RequiredServices);
    }

    [Fact]
    public async Task Refresh_ReturnsTypedSourceAndProcessorObservations()
    {
        (ServicesController controller, RecordingHttpClientFactory factory) =
            CreateController();

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.RefreshServices(CancellationToken.None));
        ServicesStatusResponse response =
            Assert.IsType<ServicesStatusResponse>(result.Value);

        Assert.NotNull(response.LastCheckedAt);
        ServiceHealthInfo jira =
            Assert.Single(response.Services, service => service.Name == "Jira");
        Assert.Equal("healthy", jira.Status);
        Assert.Equal(10, jira.ItemCount);
        Assert.NotNull(jira.CheckedAt);

        ServiceHealthInfo planner =
            Assert.Single(response.Services, service => service.Name == "Planner");
        Assert.Equal("healthy", planner.Status);
        Assert.Equal("running", planner.ProcessingStatus);
        Assert.Equal(7, planner.ProcessingRemainingCount);
        Assert.Equal(["Jira", "GitHub"], planner.RequiredServices);
        Assert.NotNull(planner.CheckedAt);

        Assert.Contains(
            factory.Requests,
            request => request == ("source-jira", "/api/v1/health"));
        Assert.Contains(
            factory.Requests,
            request => request == ("processing-planner", "/api/v1/status"));
        Assert.DoesNotContain(
            factory.Requests,
            request => request.Path.Contains(
                "/start",
                StringComparison.OrdinalIgnoreCase));
    }

    private static (
        ServicesController Controller,
        RecordingHttpClientFactory Factory) CreateController()
    {
        OrchestratorOptions options = new()
        {
            Services = new Dictionary<string, SourceServiceConfig>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Jira"] = new()
                {
                    HttpAddress = "http://jira",
                    Enabled = true,
                },
                ["GitHub"] = new()
                {
                    HttpAddress = "http://github",
                    Enabled = false,
                },
            },
            ProcessingServices =
                new Dictionary<string, ProcessingServiceConfig>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["Planner"] = new()
                    {
                        HttpAddress = "http://planner",
                        Enabled = true,
                        RequiredServices = ["Jira", "GitHub"],
                    },
                },
        };
        IOptions<OrchestratorOptions> optionsAccessor = Options.Create(options);
        RecordingHttpClientFactory factory = new();
        SourceHttpClient sourceClient = new(
            factory,
            optionsAccessor,
            NullLogger<SourceHttpClient>.Instance);
        ProcessingHttpClient processingClient = new(
            factory,
            optionsAccessor,
            NullLogger<ProcessingHttpClient>.Instance);
        ServiceHealthMonitor monitor = new(
            sourceClient,
            optionsAccessor,
            NullLogger<ServiceHealthMonitor>.Instance,
            processingClient);
        OrchestratorDatabase database = new(
            Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-services-{Guid.NewGuid():N}.db"),
            NullLogger<OrchestratorDatabase>.Instance);
        ServicesController controller = new(
            sourceClient,
            monitor,
            database,
            NullLoggerFactory.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return (controller, factory);
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public List<(string ClientName, string Path)> Requests { get; } = [];

        public HttpClient CreateClient(string name) =>
            new(new CannedHandler(name, Requests))
            {
                BaseAddress = new Uri("http://localhost"),
            };
    }

    private sealed class CannedHandler(
        string clientName,
        List<(string ClientName, string Path)> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            requests.Add((clientName, path));
            string json = clientName.StartsWith(
                "source-",
                StringComparison.Ordinal)
                ? SourceJson(path)
                : ProcessingJson(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            });
        }

        private static string SourceJson(string path) => path switch
        {
            "/api/v1/health" =>
                """{"status":"healthy","version":"1","uptimeSeconds":1,"message":"OK"}""",
            "/api/v1/stats" =>
                """{"source":"jira","totalItems":10,"totalComments":0,"databaseSizeBytes":100,"cacheSizeBytes":0,"cacheFiles":0,"lastSyncAt":null,"oldestItem":null,"newestItem":null,"additionalCounts":null}""",
            "/api/v1/status" =>
                """{"source":"jira","status":"ok","lastSyncAt":null,"itemsTotal":10,"itemsProcessed":10,"lastError":null,"syncSchedule":"01:00:00","indexes":[]}""",
            _ => "{}",
        };

        private static string ProcessingJson(string path) => path switch
        {
            "/api/v1/health" =>
                """{"status":"healthy","version":"1","uptimeSeconds":2,"message":"OK"}""",
            "/api/v1/status" =>
                """{"status":"running","isRunning":true,"isPaused":false,"startedAt":"2026-04-29T00:00:00Z","uptimeSeconds":2,"lastPollAt":null,"syncSchedule":"00:05:00","maxConcurrentProcessingThreads":2,"startProcessingOnStartup":true}""",
            "/api/v1/processing/queue" =>
                """{"processedCount":3,"remainingCount":7,"inFlightCount":1,"errorCount":0,"averageItemDurationMs":12.5,"lastItemCompletedAt":null}""",
            _ => "{}",
        };
    }
}
