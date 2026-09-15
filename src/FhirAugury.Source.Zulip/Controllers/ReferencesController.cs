using FhirAugury.Common.Api;
using FhirAugury.Source.Zulip.Queries;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1/references")]
public class ReferencesController(ZulipReferenceResolver resolver) : ControllerBase
{
    /// <summary>Resolve a message ID or legacy streamName:topic using indexed source context only.</summary>
    [HttpGet("resolve")]
    [ProducesResponseType<ZulipReferenceResolutionResponse>(200)]
    [ProducesResponseType<ZulipReferenceResolutionResponse>(400)]
    [ProducesResponseType<ZulipReferenceResolutionResponse>(404)]
    [ProducesResponseType<ZulipReferenceResolutionResponse>(409)]
    [ProducesResponseType<ZulipReferenceResolutionResponse>(503)]
    public IActionResult Resolve([FromQuery] string? reference, CancellationToken ct)
    {
        ZulipReferenceResolutionResponse response = resolver.Resolve(reference, ct);
        return StatusCode(ZulipReferenceContract.HttpStatus(response.Outcome!.Value), response);
    }
}
