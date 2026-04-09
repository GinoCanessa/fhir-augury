using System.Net;
using System.Net.Http.Json;
using System.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Routing;
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static SourceProxyController CreateController(
        MockHttpMessageHandler jiraHandler,
        bool jiraEnabled = true)
    {
        Dictionary<string, SourceServiceConfig> services = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jira"] = new SourceServiceConfig { Enabled = jiraEnabled, HttpAddress = "http://jira:5160" },
            ["zulip"] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://zulip:5170" },
        };

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        HttpClient jiraClient = new(jiraHandler) { BaseAddress = new Uri("http://jira:5160") };
        factory.CreateClient("source-jira").Returns(jiraClient);

        // Zulip mock for completeness
        MockHttpMessageHandler zulipHandler = new("{}");
        HttpClient zulipClient = new(zulipHandler) { BaseAddress = new Uri("http://zulip:5170") };
        factory.CreateClient("source-zulip").Returns(zulipClient);

        OrchestratorOptions options = new() { Services = services };
        IOptions<OrchestratorOptions> opts = Options.Create(options);
        SourceHttpClient sourceHttpClient = new(factory, opts, NullLogger<SourceHttpClient>.Instance);

        return new SourceProxyController(sourceHttpClient, factory);
    }

    [Fact]
    public async Task JiraWorkGroups_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"FHIR Infrastructure","issueCount":4231}]""";
        MockHttpMessageHandler handler = new(expectedJson);
        SourceProxyController controller = CreateController(handler);

        IActionResult result = await controller.JiraWorkGroups(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        Assert.Contains("FHIR Infrastructure", content.Content);
        Assert.Single(handler.SentRequests);
        Assert.Contains("/api/v1/work-groups", handler.SentRequests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task JiraWorkGroups_Disabled_Returns404()
    {
        MockHttpMessageHandler handler = new("[]");
        SourceProxyController controller = CreateController(handler, jiraEnabled: false);

        IActionResult result = await controller.JiraWorkGroups(CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task JiraSpecifications_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"FHIR Core","issueCount":300}]""";
        MockHttpMessageHandler handler = new(expectedJson);
        SourceProxyController controller = CreateController(handler);

        IActionResult result = await controller.JiraSpecifications(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Contains("FHIR Core", content.Content);
    }

    [Fact]
    public async Task JiraLabels_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"bug","issueCount":42}]""";
        MockHttpMessageHandler handler = new(expectedJson);
        SourceProxyController controller = CreateController(handler);

        IActionResult result = await controller.JiraLabels(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Contains("bug", content.Content);
    }

    [Fact]
    public async Task JiraStatuses_ReturnsProxiedJson()
    {
        string expectedJson = """[{"name":"Open","issueCount":1000},{"name":"Closed","issueCount":2000}]""";
        MockHttpMessageHandler handler = new(expectedJson);
        SourceProxyController controller = CreateController(handler);

        IActionResult result = await controller.JiraStatuses(CancellationToken.None);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Contains("Open", content.Content);
        Assert.Contains("Closed", content.Content);
    }
}
