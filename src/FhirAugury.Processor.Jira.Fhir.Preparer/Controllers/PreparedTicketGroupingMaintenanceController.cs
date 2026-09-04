using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

public sealed record GroupingMaintenanceRequest(string? RunId = null);

[ApiController]
[Route("api/v1/prepared-ticket-groupings/maintenance")]
public sealed class PreparedTicketGroupingMaintenanceController(
    PreparedTicketGroupingMaintenanceService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Rebuild(
        [FromBody] GroupingMaintenanceRequest? request,
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
