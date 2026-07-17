using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class SearchParametersController(FhirReleaseResolver resolver, FhirSpecReader reader)
    : FhirControllerBase(resolver)
{
    [HttpGet("{release}/searchparameters")]
    public IActionResult List(
        string release,
        [FromQuery(Name = "base")] string? baseResource = null,
        [FromQuery] string? code = null)
        => ResolvedList(release, pk => reader.ListSearchParameters(pk, baseResource, code));

    [HttpGet("{release}/searchparameters/{idOrCode}")]
    public IActionResult Get(string release, string idOrCode)
        => ResolvedItem(release, pk => reader.GetSearchParameter(pk, idOrCode),
            $"Search parameter '{idOrCode}' not found.");
}
