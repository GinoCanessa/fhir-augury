using System.Net;
using System.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests;

public class SourceProxyControllerTests
{
    private sealed class MockHttpMessageHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> SentRequests { get; } = [];
        public List<string?> SentBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            string? body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            SentBodies.Add(body);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (SourceProxyController Controller, MockHttpMessageHandler JiraHandler, MockHttpMessageHandler ZulipHandler) CreateController(
        string jiraResponseJson = "{}",
        string zulipResponseJson = "{}",
        bool jiraEnabled = true,
        bool zulipEnabled = true)
    {
        Dictionary<string, SourceServiceConfig> services = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jira"] = new SourceServiceConfig { Enabled = jiraEnabled, HttpAddress = "http://jira:5160" },
            ["zulip"] = new SourceServiceConfig { Enabled = zulipEnabled, HttpAddress = "http://zulip:5170" },
        };

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        MockHttpMessageHandler jiraHandler = new(jiraResponseJson);
        HttpClient jiraClient = new(jiraHandler) { BaseAddress = new Uri("http://jira:5160") };
        factory.CreateClient("source-jira").Returns(jiraClient);

        MockHttpMessageHandler zulipHandler = new(zulipResponseJson);
        HttpClient zulipClient = new(zulipHandler) { BaseAddress = new Uri("http://zulip:5170") };
        factory.CreateClient("source-zulip").Returns(zulipClient);

        OrchestratorOptions options = new() { Services = services };
        IOptions<OrchestratorOptions> opts = Options.Create(options);
        SourceHttpClient sourceHttpClient = new(factory, opts, NullLogger<SourceHttpClient>.Instance);

        SourceProxyController controller = new(sourceHttpClient, factory);
        return (controller, jiraHandler, zulipHandler);
    }

    private static void SetRequestBody(SourceProxyController controller, string bodyJson)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyJson));
        httpContext.Request.ContentType = "application/json";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task JiraWorkGroups_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"FHIR Infrastructure","issueCount":4231}]""";
        (SourceProxyController controller, MockHttpMessageHandler jiraHandler, _) = CreateController(jiraResponseJson: expectedJson);

        IActionResult result = await controller.JiraWorkGroups(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        Assert.Contains("FHIR Infrastructure", content.Content);
        Assert.Single(jiraHandler.SentRequests);
        Assert.Contains("/api/v1/work-groups", jiraHandler.SentRequests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task JiraWorkGroups_Disabled_Returns404()
    {
        (SourceProxyController controller, _, _) = CreateController(jiraEnabled: false);

        IActionResult result = await controller.JiraWorkGroups(CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task JiraSpecifications_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"FHIR Core","issueCount":300}]""";
        (SourceProxyController controller, _, _) = CreateController(jiraResponseJson: expectedJson);

        IActionResult result = await controller.JiraSpecifications(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Contains("FHIR Core", content.Content);
    }

    [Fact]
    public async Task JiraLabels_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"bug","issueCount":42}]""";
        (SourceProxyController controller, _, _) = CreateController(jiraResponseJson: expectedJson);

        IActionResult result = await controller.JiraLabels(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Contains("bug", content.Content);
    }

    [Fact]
    public async Task JiraStatuses_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"Open","issueCount":1000},{"name":"Closed","issueCount":2000}]""";
        (SourceProxyController controller, _, _) = CreateController(jiraResponseJson: expectedJson);

        IActionResult result = await controller.JiraStatuses(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Contains("Open", content.Content);
        Assert.Contains("Closed", content.Content);
    }

    // ── Jira Query Proxy ──────────────────────────────────────────────

    [Fact]
    public async Task JiraQuery_ForwardsBodyToQueryEndpoint()
    {
        string responseJson = """{"results":[{"key":"FHIR-100","title":"Test","status":"Open"}]}""";
        (SourceProxyController controller, MockHttpMessageHandler jiraHandler, _) = CreateController(jiraResponseJson: responseJson);
        string requestBody = """{"statuses":["Triaged"],"limit":10}""";
        SetRequestBody(controller, requestBody);

        IActionResult result = await controller.JiraQuery(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        Assert.Contains("FHIR-100", content.Content);
        Assert.Single(jiraHandler.SentRequests);
        Assert.Equal(HttpMethod.Post, jiraHandler.SentRequests[0].Method);
        Assert.Contains("/api/v1/query", jiraHandler.SentRequests[0].RequestUri!.ToString());
        Assert.Contains("Triaged", jiraHandler.SentBodies[0]);
    }

    [Fact]
    public async Task JiraQuery_Disabled_Returns404()
    {
        (SourceProxyController controller, _, _) = CreateController(jiraEnabled: false);
        SetRequestBody(controller, "{}");

        IActionResult result = await controller.JiraQuery(CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Zulip Query Proxy ─────────────────────────────────────────────

    [Fact]
    public async Task ZulipQuery_ForwardsBodyToQueryEndpoint()
    {
        string responseJson = """{"total":1,"results":[{"id":1,"streamName":"general","topic":"test"}]}""";
        (SourceProxyController controller, _, MockHttpMessageHandler zulipHandler) = CreateController(zulipResponseJson: responseJson);
        string requestBody = """{"streamNames":["implementers"],"query":"search"}""";
        SetRequestBody(controller, requestBody);

        IActionResult result = await controller.ZulipQuery(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        Assert.Contains("general", content.Content);
        Assert.Single(zulipHandler.SentRequests);
        Assert.Equal(HttpMethod.Post, zulipHandler.SentRequests[0].Method);
        Assert.Contains("/api/v1/query", zulipHandler.SentRequests[0].RequestUri!.ToString());
        Assert.Contains("implementers", zulipHandler.SentBodies[0]);
    }

    [Fact]
    public async Task ZulipQuery_Disabled_Returns404()
    {
        (SourceProxyController controller, _, _) = CreateController(zulipEnabled: false);
        SetRequestBody(controller, "{}");

        IActionResult result = await controller.ZulipQuery(CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
