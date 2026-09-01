using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Controllers;

/// <summary>
/// Returns the workgroup-display projection of <c>planned_jira_hydration</c>
/// self-rows (where <c>JiraKey = IssueKey</c>). The shape is the planner-side
/// analog of the preparer's
/// <c>PreparedTicketHydrationController</c>; both rely on the self-Jira row
/// the hydrator now always writes.
/// </summary>
[ApiController]
[Route("api/v1/planned-ticket-hydration")]
[Produces("application/json")]
public sealed class PlannedTicketHydrationController(PlannerDatabase database) : ControllerBase
{
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PlannedJiraHydrationDisplayResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlannedJiraHydrationDisplayResponse>> GetWorkGroupHydration(string workGroupClean, CancellationToken ct)
    {
        string canonical = Hl7WorkGroupNameCleaner.Clean(workGroupClean);
        if (string.IsNullOrEmpty(canonical))
        {
            canonical = workGroupClean;
        }

        IReadOnlyList<PlannedJiraHydrationRow> rows = await database.ListJiraHydrationDisplayForWorkGroupAsync(canonical, ct);
        return Ok(new PlannedJiraHydrationDisplayResponse(canonical, rows.Select(PlannedTicketDtoMapper.ToDto).ToArray()));
    }
}
