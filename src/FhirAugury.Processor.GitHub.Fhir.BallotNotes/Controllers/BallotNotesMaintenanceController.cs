using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Controllers;

[ApiController]
[Route("api/v1/ballot-notes/maintenance")]
[Produces("application/json")]
public sealed class BallotNotesMaintenanceController(
    BallotNotesDatabase database) : ControllerBase
{
    [HttpPost("workgroups/reallocate")]
    public async Task<IActionResult> ReallocateWorkGroups(
        [FromBody] BallotNotesWorkGroupReallocationRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await database.ReallocateWorkGroupsAsync(request, ct));
        }
        catch (AuthoringConflictException ex)
        {
            return Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "invalid-reallocation", detail = ex.Message });
        }
    }
}
