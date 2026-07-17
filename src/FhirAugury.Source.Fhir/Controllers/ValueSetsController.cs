using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class ValueSetsController(FhirReleaseResolver resolver, FhirSpecReader reader)
    : FhirControllerBase(resolver)
{
    [HttpGet("{release}/valuesets")]
    public IActionResult List(string release)
        => ResolvedList(release, pk => reader.ListValueSets(pk));

    // Canonical URLs contain '/', so the value set is passed as a query parameter.
    [HttpGet("{release}/valuesets/lookup")]
    public IActionResult Lookup(string release, [FromQuery] string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "Query parameter 'url' is required." });
        }
        return ResolvedItem(release, pk => reader.GetValueSet(pk, url),
            $"Value set '{url}' not found.");
    }

    [HttpGet("{release}/valuesets/concepts")]
    public IActionResult Concepts(string release, [FromQuery] string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "Query parameter 'url' is required." });
        }
        return ResolvedItem(release, pk => reader.GetExpansion(pk, url),
            $"Value set '{url}' not found.");
    }

    [HttpGet("{release}/valuesets/bindings")]
    public IActionResult Bindings(string release, [FromQuery] string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "Query parameter 'url' is required." });
        }
        return ResolvedItem(release, pk => reader.GetBindings(pk, url),
            $"Value set '{url}' not found.");
    }
}
