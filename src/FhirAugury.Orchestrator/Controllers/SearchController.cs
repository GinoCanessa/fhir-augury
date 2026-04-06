using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Search;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1")]
public class SearchController(UnifiedSearchService searchService) : ControllerBase
{
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] string? sources,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required" });

        List<string>? sourceList = CsvParser.ParseSourceList(sources);
        (List<ScoredItem>? results, List<string>? warnings) = await searchService.SearchAsync(q, sourceList, limit ?? 0, ct);

        return Ok(new
        {
            query = q,
            total = results.Count,
            warnings,
            results = results.Select(r => new
            {
                r.Source, r.ContentType, r.Id, r.Title, r.Snippet, r.Score, r.Url, r.UpdatedAt, r.Metadata,
            }),
        });
    }
}
