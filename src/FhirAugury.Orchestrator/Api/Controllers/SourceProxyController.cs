using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class SourceProxyController(SourceHttpClient httpClient) : ControllerBase
{
    [HttpPost("jira/query")]
    public async Task<IActionResult> JiraQuery(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("jira"))
            return NotFound(new { error = "Jira service not configured or disabled" });

        string query = q ?? "";
        int maxResults = limit ?? 50;

        SearchResponse? response = await httpClient.SearchAsync("jira", query, maxResults, ct);
        return Ok(new
        {
            query,
            total = response?.Total ?? 0,
            results = (response?.Results ?? []).Select(r => new
            {
                r.Source, r.Id, r.Title, r.Snippet, r.Score, r.Url,
            }),
        });
    }

    [HttpPost("zulip/query")]
    public async Task<IActionResult> ZulipQuery(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled("zulip"))
            return NotFound(new { error = "Zulip service not configured or disabled" });

        string query = q ?? "";
        int maxResults = limit ?? 20;

        SearchResponse? response = await httpClient.SearchAsync("zulip", query, maxResults, ct);
        return Ok(new
        {
            query,
            total = response?.Total ?? 0,
            results = (response?.Results ?? []).Select(r => new
            {
                r.Source, r.Id, r.Title, r.Snippet, r.Score, r.Url,
            }),
        });
    }
}
