using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Related;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1")]
public class RelatedController(RelatedItemFinder finder) : ControllerBase
{
    [HttpGet("related/{source}/{id}")]
    public async Task<IActionResult> GetRelated(
        [FromRoute] string source,
        [FromRoute] string id,
        [FromQuery] int? limit,
        [FromQuery] string? targetSources,
        CancellationToken ct)
    {
        List<string>? targetSourceList = CsvParser.ParseSourceList(targetSources);
        FindRelatedResponse response = await finder.FindRelatedAsync(source, id, limit ?? 0, targetSourceList, ct);

        return Ok(new
        {
            seedSource = response.SeedSource,
            seedId = response.SeedId,
            seedTitle = response.SeedTitle,
            items = response.Items.Select(i => new
            {
                i.Source, i.ContentType, i.Id, i.Title, i.Snippet, i.Url,
                relevanceScore = i.RelevanceScore,
                i.Relationship, i.Context,
            }),
        });
    }
}
