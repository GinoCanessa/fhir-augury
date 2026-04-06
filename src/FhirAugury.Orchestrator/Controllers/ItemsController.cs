using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1/items")]
public class ItemsController(SourceHttpClient httpClient) : ControllerBase
{
    [HttpGet("{source}/{*id}")]
    public async Task<IActionResult> GetItem(
        [FromRoute] string source,
        [FromRoute] string id,
        CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled(source))
            return NotFound(new { error = $"Source '{source}' not found or disabled" });

        try
        {
            ItemResponse? item = await httpClient.GetItemAsync(source, id, ct);
            if (item is null)
                return NotFound(new { error = $"Item '{id}' not found in source '{source}'" });

            return Ok(new
            {
                item.Source, item.Id, item.Title, item.Content, item.Url,
                item.CreatedAt, item.UpdatedAt, item.Metadata, item.Comments,
            });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("{source}/snapshot/{*id}")]
    public async Task<IActionResult> GetSnapshot(
        [FromRoute] string source,
        [FromRoute] string id,
        CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled(source))
            return NotFound(new { error = $"Source '{source}' not found or disabled" });

        try
        {
            SnapshotResponse? snapshot = await httpClient.GetSnapshotAsync(source, id, ct);
            if (snapshot is null)
                return NotFound(new { error = $"Snapshot for '{id}' not found in source '{source}'" });

            return Ok(new { snapshot.Id, snapshot.Source, snapshot.Markdown, snapshot.Url });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("{source}/content/{*id}")]
    public async Task<IActionResult> GetContent(
        [FromRoute] string source,
        [FromRoute] string id,
        [FromQuery] string? format,
        CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled(source))
            return NotFound(new { error = $"Source '{source}' not found or disabled" });

        try
        {
            ContentResponse? content = await httpClient.GetContentAsync(source, id, format ?? "text", ct);
            if (content is null)
                return NotFound(new { error = $"Content for '{id}' not found in source '{source}'" });

            return Ok(new
            {
                content.Id, content.Source, content.Content, content.Format, content.Url, content.Metadata,
            });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
