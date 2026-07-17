using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Controllers;

/// <summary>
/// On-demand planner hydration sweep endpoint. Mirrors the preparer's
/// admin controller line-for-line; replaces
/// <c>PreparedHydrationSweeper</c> with <see cref="PlannedHydrationSweeper"/>.
/// </summary>
[ApiController]
[Route("api/v1/admin/hydration")]
[Produces("application/json")]
public sealed class HydrationAdminController(
    PlannedHydrationSweeper sweeper,
    ILogger<HydrationAdminController> logger,
    IHostApplicationLifetime lifetime) : ControllerBase
{
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

        Task perTicket = Task.Run(
            () => sweeper.RunPerTicketSweepAsync(lifetime.ApplicationStopping),
            lifetime.ApplicationStopping);
        _ = perTicket.ContinueWith(
            t => logger.LogError(t.Exception, "Admin-triggered planner per-ticket sweep faulted."),
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
