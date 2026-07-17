using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class ReleasesController(FhirSpecReader reader) : ControllerBase
{
    /// <summary>Lists the FHIR releases available in the spec database.</summary>
    [HttpGet("releases")]
    public IActionResult GetReleases() => Ok(reader.ListReleases());
}
