using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

/// <summary>
/// Cheap orchestrator-local liveness and readiness endpoints. These
/// deliberately do NOT make outbound calls to source services — that is the
/// job of <c>GET api/v1/services</c> (the aggregate dashboard).
/// </summary>
[ApiController]
[Route("api/v1")]
public class LifecycleController(SourceHttpClient sources) : ControllerBase
{
    /// <summary>
    /// Cheap liveness probe. Always returns 200 with <c>{ status = "ok" }</c>
    /// when the orchestrator process is up; performs no I/O.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHealth() => Ok(new { status = "ok" });

    /// <summary>
    /// Orchestrator-local readiness view. Returns 200 with details when the
    /// in-process source registry has at least one enabled source, otherwise
    /// 503 with the same shape so callers can distinguish "process up,
    /// nothing wired" from "process down".
    /// </summary>
    /// <remarks>
    /// This endpoint never reaches out to source services. For a per-source
    /// reachability/health view see <c>GET api/v1/services</c>.
    /// </remarks>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetStatus()
    {
        IReadOnlyList<string> enabled = sources.GetEnabledSourceNames();
        bool ready = enabled.Count > 0;

        object body = new
        {
            status = ready ? "ok" : "not-ready",
            sourceRegistry = new
            {
                hydrated = ready,
                enabledCount = enabled.Count,
                enabledNames = enabled,
            },
        };

        return ready
            ? Ok(body)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
