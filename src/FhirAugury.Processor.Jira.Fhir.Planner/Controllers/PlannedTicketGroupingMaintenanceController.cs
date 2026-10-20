using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Planner.Processing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Controllers;

public sealed record PlannerGroupingMaintenanceRequest(string? RunId = null);

[ApiController]
[Route("api/v1/planned-ticket-topics/maintenance")]
public sealed class PlannedTicketGroupingMaintenanceController(
    PlannedTicketGroupingMaintenanceService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Rebuild(
        [FromBody] PlannerGroupingMaintenanceRequest? request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await service.RebuildAsync(request?.RunId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (AuthoringConflictException ex)
        {
            return Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
        }
    }
}
