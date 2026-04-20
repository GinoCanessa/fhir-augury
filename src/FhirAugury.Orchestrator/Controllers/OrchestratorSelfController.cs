using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1/source/orchestrator")]
public class OrchestratorSelfController(SourceHttpClient httpClient) : ControllerBase
{
    [HttpGet("list-sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ListSources()
    {
        return Ok(new { sources = httpClient.GetEnabledSourceNames() });
    }
}
