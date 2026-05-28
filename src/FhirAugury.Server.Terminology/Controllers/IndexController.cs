using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Server.Terminology.Controllers;

/// <summary>
/// Endpoints describing the state of the THO terminology index.
/// </summary>
/// <remarks>
/// Phase 1 ships a stub. Phase 2 replaces <c>Status</c> with real
/// per-package counts and adds <c>POST refresh</c>.
/// </remarks>
[ApiController]
[Route("api/v1/terminology/index")]
public class IndexController : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            ready = false,
            message = "index not yet implemented"
        });
    }
}
