using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class CodeSystemsController(FhirReleaseResolver resolver, FhirSpecReader reader)
    : FhirControllerBase(resolver)
{
    [HttpGet("{release}/codesystems")]
    public IActionResult List(string release)
        => ResolvedList(release, pk => reader.ListCodeSystems(pk));

    // Canonical URLs contain '/', so the system is passed as a query parameter.
    [HttpGet("{release}/codesystems/lookup")]
    public IActionResult Lookup(string release, [FromQuery] string? system)
    {
        if (string.IsNullOrWhiteSpace(system))
        {
            return BadRequest(new { error = "Query parameter 'system' is required." });
        }
        return ResolvedItem(release, pk => reader.GetCodeSystem(pk, system),
            $"Code system '{system}' not found.");
    }

    [HttpGet("{release}/codesystems/concepts")]
    public IActionResult Concepts(string release, [FromQuery] string? system, [FromQuery] bool hierarchical = false)
    {
        if (string.IsNullOrWhiteSpace(system))
        {
            return BadRequest(new { error = "Query parameter 'system' is required." });
        }
        return ResolvedItem(release, pk => reader.GetConcepts(pk, system, hierarchical),
            $"Code system '{system}' not found.");
    }

    [HttpGet("{release}/codesystems/concept")]
    public IActionResult Concept(string release, [FromQuery] string? system, [FromQuery] string? code)
    {
        if (string.IsNullOrWhiteSpace(system))
        {
            return BadRequest(new { error = "Query parameter 'system' is required." });
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest(new { error = "Query parameter 'code' is required." });
        }
        return ResolvedItem(release, pk => reader.GetConcept(pk, system, code),
            $"Concept '{code}' not found in '{system}'.");
    }
}
