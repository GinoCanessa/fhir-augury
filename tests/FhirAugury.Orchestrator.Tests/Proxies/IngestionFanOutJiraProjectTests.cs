using System.Net;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests.Proxies;

/// <summary>
/// Verifies the orchestrator <c>POST api/v1/ingest/trigger</c> fan-out leg's
/// handling of the <c>jira-project</c> query parameter (Phase C, §5.4.3).
/// </summary>
public class IngestionFanOutJiraProjectTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string SourceName { get; }
        public List<HttpRequestMessage> Requests { get; } = [];

        public CapturingHandler(string sourceName) { SourceName = sourceName; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ok","itemsTotal":0}""", System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static (IngestionController Controller, Dictionary<string, CapturingHandler> Handlers) CreateController()
    {
        Dictionary<string, SourceServiceConfig> services = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jira"] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://jira:5000" },
            ["zulip"] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://zulip:5000" },
            ["confluence"] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://confluence:5000" },
            ["github"] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://github:5000" },
        };

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        Dictionary<string, CapturingHandler> handlers = [];
        foreach (string source in services.Keys)
        {
            CapturingHandler handler = new(source);
            handlers[source] = handler;
            HttpClient client = new(handler) { BaseAddress = new Uri($"http://{source}:5000") };
            factory.CreateClient($"source-{source}").Returns(client);
        }

        SourceHttpClient http = new(factory, Options.Create(new OrchestratorOptions { Services = services }),
            NullLogger<SourceHttpClient>.Instance);

        // OrchestratorDatabase is required by IngestionController construction
        // (TriggerIngestion does not touch the DB, but the field is non-null).
        string dbPath = Path.Combine(Path.GetTempPath(), $"fa-test-{Guid.NewGuid():N}.db");
        OrchestratorDatabase db = new(dbPath, NullLogger<OrchestratorDatabase>.Instance);
        IngestionController controller = new(http, db, NullLoggerFactory.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, handlers);
    }

    [Fact]
    public async Task TriggerIngestion_NullJiraProject_OmitsProjectFromAllLegs()
    {
        (IngestionController c, Dictionary<string, CapturingHandler> handlers) = CreateController();

        IActionResult r = await c.TriggerIngestion("incremental", null, null, default);

        Assert.IsType<OkObjectResult>(r);
        foreach ((string name, CapturingHandler h) in handlers)
        {
            Assert.Single(h.Requests);
            string q = h.Requests[0].RequestUri!.Query;
            Assert.DoesNotContain("project=", q);
        }
    }

    [Fact]
    public async Task TriggerIngestion_WithJiraProject_ForwardsOnlyToJiraLeg()
    {
        (IngestionController c, Dictionary<string, CapturingHandler> handlers) = CreateController();

        IActionResult r = await c.TriggerIngestion("full", null, "FHIR", default);

        Assert.IsType<OkObjectResult>(r);

        // Jira leg must include project=FHIR
        string jiraQuery = handlers["jira"].Requests[0].RequestUri!.Query;
        Assert.Contains("project=FHIR", jiraQuery);
        Assert.Contains("type=full", jiraQuery);

        // Other legs must NOT include project= at all
        foreach (string leg in new[] { "zulip", "confluence", "github" })
        {
            string q = handlers[leg].Requests[0].RequestUri!.Query;
            Assert.DoesNotContain("project=", q);
            Assert.DoesNotContain("jira-project=", q);
        }
    }

    [Fact]
    public async Task TriggerIngestion_SourcesFilteredOutNonJira_StillNoLeak()
    {
        (IngestionController c, Dictionary<string, CapturingHandler> handlers) = CreateController();

        IActionResult r = await c.TriggerIngestion("incremental", "zulip,confluence", "FHIR", default);

        Assert.IsType<OkObjectResult>(r);
        Assert.Empty(handlers["jira"].Requests);
        Assert.Empty(handlers["github"].Requests);
        Assert.Single(handlers["zulip"].Requests);
        Assert.Single(handlers["confluence"].Requests);

        Assert.DoesNotContain("project=", handlers["zulip"].Requests[0].RequestUri!.Query);
        Assert.DoesNotContain("project=", handlers["confluence"].Requests[0].RequestUri!.Query);
    }
}
