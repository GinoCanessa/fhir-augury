using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Controllers;

[ApiController]
[Route("api/v1/planned-tickets")]
[Produces("application/json")]
public sealed class PlannedTicketsController(PlannerDatabase database) : ControllerBase
{
    [HttpGet("{key}")]
    [ProducesResponseType(typeof(PlannedTicketDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlannedTicketDetailDto>> GetPlannedTicket(string key, CancellationToken ct)
    {
        PlannedTicketDetail? detail = await database.GetPlannedTicketAsync(key, ct);
        return detail is null ? NotFound() : Ok(PlannedTicketDtoMapper.ToDto(detail));
    }

    [HttpGet]
    [ProducesResponseType(typeof(PlannedTicketListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlannedTicketListResponse>> ListPlannedTickets(
        [FromQuery] string? repo,
        [FromQuery] string? affectedFilePath,
        [FromQuery] string? relatedJiraKey,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        PlannedTicketQueryRequest request = new()
        {
            Repo = repo,
            AffectedFilePath = affectedFilePath,
            RelatedJiraKey = relatedJiraKey,
            Limit = limit,
            Offset = offset,
        };
        return await QueryPlannedTickets(request, ct);
    }

    [HttpPost("query")]
    [ProducesResponseType(typeof(PlannedTicketListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlannedTicketListResponse>> QueryPlannedTickets([FromBody] PlannedTicketQueryRequest request, CancellationToken ct)
    {
        PlannedTicketQueryFilter filter = request.ToFilter();
        IReadOnlyList<PlannedTicketSummary> rows = await database.ListPlannedTicketsAsync(filter, ct);
        return Ok(new PlannedTicketListResponse(
            rows.Select(PlannedTicketDtoMapper.ToDto).ToArray(),
            filter.Limit,
            filter.Offset));
    }

    [HttpGet("{key}/related")]
    [ProducesResponseType(typeof(PlannedTicketRelatedItemsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlannedTicketRelatedItemsDto>> GetPlannedTicketRelated(string key, CancellationToken ct)
    {
        PlannedTicketDetail? detail = await database.GetPlannedTicketAsync(key, ct);
        if (detail is null) return NotFound();
        return Ok(new PlannedTicketRelatedItemsDto(
            detail.Repos.Select(r => new PlannedTicketRepoDto(r.RepoKey, r.RepoRevision, r.Justification)).ToArray()));
    }
}
