using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

/// <summary>
/// On-demand hydration sweep endpoint. Matches the unauthenticated
/// convention of the existing processing-lifecycle minimal-API
/// endpoints in <c>ProcessingEndpointRouteBuilderExtensions</c> (no
/// authentication; deployment is expected to gate access at the
/// network / reverse-proxy layer).
/// </summary>
[ApiController]
[Route("api/v1/admin/hydration")]
[Produces("application/json")]
public sealed class HydrationAdminController(
    PreparedHydrationSweeper sweeper,
    ILogger<HydrationAdminController> logger,
    IHostApplicationLifetime lifetime) : ControllerBase
{
    /// <summary>
    /// Triggers a full hydration sweep without bouncing the service.
    /// Runs the Specification backfill synchronously (so its result
    /// can be surfaced as 503 when the Jira-source upstream is
    /// unreachable) and fires the per-ticket hydration sweep
    /// fire-and-forget; returns <c>202 Accepted</c> with the
    /// Specification counters.
    /// </summary>
    [HttpPost("backfill")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> TriggerBackfill(CancellationToken ct)
    {
        SpecificationBackfillResult spec = await sweeper.RunSpecificationBackfillAsync(ct);
        if (spec.Failure is not null)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Jira source unreachable",
                detail: spec.Failure.Reason);
        }

        // Fire-and-forget the per-ticket phase; the lifetime token cancels it on shutdown.
        // The sweeper's HydrateAsync never throws (per its contract), but we still attach a
        // ContinueWith to log any unexpected fault rather than leaking an unobserved exception.
        Task perTicket = Task.Run(
            () => sweeper.RunPerTicketSweepAsync(lifetime.ApplicationStopping),
            lifetime.ApplicationStopping);
        _ = perTicket.ContinueWith(
            t => logger.LogError(t.Exception, "Admin-triggered per-ticket sweep faulted."),
            TaskContinuationOptions.OnlyOnFaulted);

        return Accepted(new
        {
            specification = new
            {
                updated = spec.Updated,
                stillEmpty = spec.StillEmpty,
                notFound = spec.NotFound,
            },
        });
    }
}
