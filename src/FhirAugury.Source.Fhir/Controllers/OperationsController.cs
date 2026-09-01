using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class OperationsController(FhirReleaseResolver resolver, FhirSpecReader reader)
    : FhirControllerBase(resolver)
{
    [HttpGet("{release}/operations")]
    public IActionResult List(string release)
        => ResolvedList(release, pk => reader.ListOperations(pk));

    [HttpGet("{release}/operations/{idOrCode}")]
    public IActionResult Get(string release, string idOrCode)
        => ResolvedItem(release, pk => reader.GetOperation(pk, idOrCode),
            $"Operation '{idOrCode}' not found.");
}
