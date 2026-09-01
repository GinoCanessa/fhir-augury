using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class ResolveController(FhirReleaseResolver resolver, FhirSpecReader reader)
    : FhirControllerBase(resolver)
{
    // Canonical URLs contain '/', so the url is passed as a query parameter.
    [HttpGet("{release}/resolve")]
    public IActionResult Resolve(string release, [FromQuery] string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "Query parameter 'url' is required." });
        }
        return ResolvedItem(release, pk => reader.Resolve(pk, url),
            $"No artifact resolved for '{url}'.");
    }
}
