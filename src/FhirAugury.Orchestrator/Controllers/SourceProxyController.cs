using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1")]
public class SourceProxyController(SourceHttpClient httpClient, IHttpClientFactory httpClientFactory) : ControllerBase
{
    [HttpPost("jira/query")]
    public async Task<IActionResult> JiraQuery(CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("jira"))
            return NotFound(new { error = "Jira service not configured or disabled" });

        HttpClient client = httpClientFactory.CreateClient("source-jira");
        using StreamContent body = new(Request.Body);
        body.Headers.ContentType = new("application/json");
        HttpResponseMessage response = await client.PostAsync("/api/v1/query", body, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        return Content(json, "application/json");
    }

    [HttpGet("jira/work-groups")]
    public async Task<IActionResult> JiraWorkGroups(CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("jira"))
            return NotFound(new { error = "Jira service not configured or disabled" });

        HttpClient client = httpClientFactory.CreateClient("source-jira");
        string json = await client.GetStringAsync("/api/v1/work-groups", ct);
        return Content(json, "application/json");
    }

    [HttpGet("jira/specifications")]
    public async Task<IActionResult> JiraSpecifications(CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("jira"))
            return NotFound(new { error = "Jira service not configured or disabled" });

        HttpClient client = httpClientFactory.CreateClient("source-jira");
        string json = await client.GetStringAsync("/api/v1/specifications", ct);
        return Content(json, "application/json");
    }

    [HttpGet("jira/labels")]
    public async Task<IActionResult> JiraLabels(CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("jira"))
            return NotFound(new { error = "Jira service not configured or disabled" });

        HttpClient client = httpClientFactory.CreateClient("source-jira");
        string json = await client.GetStringAsync("/api/v1/labels", ct);
        return Content(json, "application/json");
    }

    [HttpGet("jira/statuses")]
    public async Task<IActionResult> JiraStatuses(CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("jira"))
            return NotFound(new { error = "Jira service not configured or disabled" });

        HttpClient client = httpClientFactory.CreateClient("source-jira");
        string json = await client.GetStringAsync("/api/v1/statuses", ct);
        return Content(json, "application/json");
    }

    [HttpPost("zulip/query")]
    public async Task<IActionResult> ZulipQuery(CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("zulip"))
            return NotFound(new { error = "Zulip service not configured or disabled" });

        HttpClient client = httpClientFactory.CreateClient("source-zulip");
        using StreamContent body = new(Request.Body);
        body.Headers.ContentType = new("application/json");
        HttpResponseMessage response = await client.PostAsync("/api/v1/query", body, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        return Content(json, "application/json");
    }
}
