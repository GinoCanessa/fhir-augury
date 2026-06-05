using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Controllers;

/// <summary>
/// Read-only projection over the analytic / clustering inputs the
/// <c>planner-topic-groupings</c> skill needs to bucket tickets into
/// Topics and Linked Ticket Groups on the planner side. Returns
/// per-ticket prose from <c>planned_tickets</c>, partition / display
/// fields from <c>planned_jira_hydration</c> (or
/// <c>jira_processing_source_tickets</c> when the self-row is
/// missing), and the per-ticket repo / repo-change /
/// repo-impact projections that drive the four-tier clustering
/// hierarchy. <c>HydrationStatus</c> is <c>null</c> when no
/// self-Jira hydration row exists at all — the skill treats that
/// (and any non-<c>"resolved"</c> value) as the abort signal for the
/// whole workgroup (Open Question 3).
/// </summary>
[ApiController]
[Route("api/v1/planned-ticket-clustering-signals")]
[Produces("application/json")]
public sealed class PlannedTicketClusteringSignalsController(PlannerDatabase database) : ControllerBase
{
    /// <summary>
    /// Returns the per-ticket clustering signals for the requested
    /// workgroup, ordered by <c>IssueKey</c> ascending. Returns
    /// <c>404</c> when the workgroup has zero <c>planned_jira_hydration</c>
    /// self-rows (mirrors the preparer-side endpoint's "no clustering
    /// input vs empty catalog" disambiguation).
    /// <paramref name="workGroupClean"/> may arrive in any of
    /// <c>name</c> / <c>nameClean</c> form — the controller normalises
    /// it via <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/>
    /// defensively, matching <c>PlannedTicketTopicsController</c> and
    /// <c>PlannedTicketHydrationController</c>.
    /// </summary>
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PlannedTicketClusteringSignalsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlannedTicketClusteringSignalsDto>> GetWorkGroup(
        string workGroupClean,
        CancellationToken ct)
    {
        string cleaned = Hl7WorkGroupNameCleaner.Clean(workGroupClean);
        string canonical = string.IsNullOrEmpty(cleaned) ? workGroupClean : cleaned;
        PlannedTicketClusteringSignals? signals = await database.GetClusteringSignalsAsync(canonical, ct);
        return signals is null
            ? NotFound()
            : Ok(PlannedTicketClusteringSignalsDtoMapper.ToDto(signals));
    }
}
