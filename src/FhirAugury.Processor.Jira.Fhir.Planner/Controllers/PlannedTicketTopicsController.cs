using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Controllers;

/// <summary>
/// Read + write endpoints for planner topic groupings. PUT is the contract
/// that a future <c>orchestrate-planner-topic-groupings</c> orchestrator will
/// drive (per Open Questions in the slot's plan); GET serves the reviewer UI.
/// </summary>
[ApiController]
[Route("api/v1/planned-ticket-topics")]
[Produces("application/json")]
public sealed class PlannedTicketTopicsController(PlannerDatabase database) : ControllerBase
{
    [HttpGet("{workGroupClean}/{specification}/{type}")]
    [ProducesResponseType(typeof(PlannedTicketTopicGroupingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlannedTicketTopicGroupingResponse>> GetTopics(
        string workGroupClean, string specification, string type, CancellationToken ct)
    {
        string canonical = Hl7WorkGroupNameCleaner.Clean(workGroupClean);
        if (string.IsNullOrEmpty(canonical))
        {
            canonical = workGroupClean;
        }

        PlannedTicketTopicsForCategory? result = await database.GetWorkGroupTopicsAsync(canonical, specification, type, ct);
        return result is null ? NotFound() : Ok(PlannedTicketDtoMapper.ToDto(result));
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PutTopics([FromBody] PlannedTicketTopicGroupingRequest request, CancellationToken ct)
    {
        try
        {
            PlannedTicketTopicGroupingPayload payload = request.ToPayload();
            await database.SaveTopicGroupingAsync(payload, ct);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
