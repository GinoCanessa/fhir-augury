using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class SearchController(FhirReleaseResolver resolver, FhirSearchReader searchReader)
    : FhirControllerBase(resolver)
{
    [HttpGet("{release}/search")]
    public IActionResult Search(
        string release,
        [FromQuery] string? q,
        [FromQuery] string? types = null,
        [FromQuery] int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new { error = "Query parameter 'q' is required." });
        }

        if (!Resolver.TryResolve(release, out _, out ReleaseInfo? info, out string? error))
        {
            return NotFound(new { error });
        }

        int effectiveLimit = Math.Clamp(limit ?? 20, 1, 100);
        List<string>? kinds = string.IsNullOrWhiteSpace(types)
            ? null
            : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        FhirSearchResponse result = searchReader.Search(q, info!.ShortName, kinds, effectiveLimit);
        return Ok(new FhirReleaseResponse<FhirSearchResponse>(info, result));
    }
}
