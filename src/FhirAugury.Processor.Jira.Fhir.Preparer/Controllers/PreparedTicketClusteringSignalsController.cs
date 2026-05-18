using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

/// <summary>
/// Read-only projection over the analytic / clustering inputs the
/// <c>topic-groupings</c> skill needs to bucket tickets into Topics
/// and Linked Ticket Groups. Returns per-ticket summary text from
/// <c>prepared_tickets</c>, partition / display fields from the
/// <c>prepared_jira_hydration</c> self-row, and every
/// <c>prepared_ticket_related_jira</c> edge for the workgroup's
/// tickets. The endpoint is <c>GET</c>-only and does not gate on
/// <c>HydrationStatus</c>, matching the read-only behaviour of the
/// existing grouping / hydration controllers.
/// </summary>
[ApiController]
[Route("api/v1/prepared-ticket-clustering-signals")]
[Produces("application/json")]
public sealed class PreparedTicketClusteringSignalsController(PreparerDatabase database) : ControllerBase
{
    /// <summary>
    /// Returns the per-ticket clustering signals for the requested
    /// workgroup, ordered by <c>TicketKey</c> ascending. Returns
    /// <c>404</c> when the workgroup has zero hydrated self-rows so
    /// callers can distinguish "no clustering input" from "empty
    /// catalog" without inspecting an envelope (this mirrors the
    /// grouping controller's <c>GetWorkGroup</c> behaviour, which also
    /// 404s when the workgroup is empty).
    /// </summary>
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PreparedTicketClusteringSignalsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreparedTicketClusteringSignalsDto>> GetWorkGroup(
        string workGroupClean,
        CancellationToken ct)
    {
        PreparedTicketClusteringSignals? signals = await database.GetClusteringSignalsAsync(workGroupClean, ct);
        if (signals is null)
        {
            return NotFound();
        }

        return Ok(PreparedTicketClusteringSignalsDtoMapper.ToDto(signals));
    }
}
