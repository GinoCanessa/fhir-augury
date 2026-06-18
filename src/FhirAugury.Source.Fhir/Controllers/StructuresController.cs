using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

[ApiController]
[Route("api/v1")]
public class StructuresController(FhirReleaseResolver resolver, FhirSpecReader reader)
    : FhirControllerBase(resolver)
{
    [HttpGet("{release}/resources")]
    public IActionResult GetResources(
        string release,
        [FromQuery] string? workGroup = null,
        [FromQuery] int? maturity = null,
        [FromQuery] string? status = null,
        [FromQuery] string? kind = null)
        => ResolvedList(release, pk => reader.ListStructures(pk, ["Resource"], workGroup, maturity, status, kind));

    [HttpGet("{release}/datatypes")]
    public IActionResult GetDataTypes(
        string release,
        [FromQuery] string? workGroup = null,
        [FromQuery] int? maturity = null,
        [FromQuery] string? status = null)
        => ResolvedList(release, pk => reader.ListStructures(pk, ["ComplexType", "PrimitiveType"], workGroup, maturity, status));

    [HttpGet("{release}/profiles")]
    public IActionResult GetProfiles(
        string release,
        [FromQuery] string? workGroup = null,
        [FromQuery] int? maturity = null,
        [FromQuery] string? status = null)
        => ResolvedList(release, pk => reader.ListStructures(pk, ["Profile"], workGroup, maturity, status));

    [HttpGet("{release}/interfaces")]
    public IActionResult GetInterfaces(
        string release,
        [FromQuery] string? workGroup = null,
        [FromQuery] int? maturity = null,
        [FromQuery] string? status = null)
        => ResolvedList(release, pk => reader.ListStructures(pk, ["Interface"], workGroup, maturity, status));

    [HttpGet("{release}/structures/{name}")]
    public IActionResult GetStructure(string release, string name)
        => ResolvedItem(release, pk => reader.GetStructureDetail(pk, name), $"Structure '{name}' not found.");

    [HttpGet("{release}/structures/{name}/elements")]
    public IActionResult GetElements(string release, string name, [FromQuery] bool nested = false)
        => ResolvedItem(release, pk => reader.GetElements(pk, name, nested), $"Structure '{name}' not found.");

    [HttpGet("{release}/structures/{name}/elements/{*path}")]
    public IActionResult GetElement(string release, string name, string path)
        => ResolvedItem(release, pk => reader.GetElement(pk, name, path), $"Element '{path}' not found in '{name}'.");
}
