using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

[ApiController]
[Route("api/v1/prepared-tickets")]
[Produces("application/json")]
public sealed class PreparedTicketsController(PreparerDatabase database) : ControllerBase
{
    /// <summary>Gets one prepared ticket with all related items.</summary>
    [HttpGet("{key}")]
    [ProducesResponseType(typeof(PreparedTicketDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreparedTicketDetailDto>> GetPreparedTicket(string key, CancellationToken ct)
    {
        PreparedTicketDetail? detail = await database.GetPreparedTicketAsync(key, ct);
        if (detail is null)
        {
            return NotFound();
        }

        return Ok(PreparedTicketDtoMapper.ToDto(detail));
    }

    /// <summary>Lists prepared tickets using common query-string filters.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PreparedTicketListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedTicketListResponse>> ListPreparedTickets(
        [FromQuery] string? recommendation,
        [FromQuery] string? impact,
        [FromQuery] string? repo,
        [FromQuery] string? relatedJiraKey,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        PreparedTicketQueryRequest request = new()
        {
            Recommendation = recommendation,
            Impact = impact,
            Repo = repo,
            RelatedJiraKey = relatedJiraKey,
            Limit = limit,
            Offset = offset,
        };
        return await QueryPreparedTickets(request, ct);
    }

    /// <summary>Queries prepared tickets with combined filters.</summary>
    [HttpPost("query")]
    [ProducesResponseType(typeof(PreparedTicketListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedTicketListResponse>> QueryPreparedTickets([FromBody] PreparedTicketQueryRequest request, CancellationToken ct)
    {
        PreparedTicketQueryFilter filter = request.ToFilter();
        IReadOnlyList<PreparedTicketSummary> rows = await database.ListPreparedTicketsAsync(filter, ct);
        PreparedTicketListResponse response = new(rows.Select(PreparedTicketDtoMapper.ToDto).ToArray(), Math.Clamp(filter.Limit, 1, 500), Math.Max(0, filter.Offset));
        return Ok(response);
    }

    /// <summary>Gets only related items for a prepared ticket.</summary>
    [HttpGet("{key}/related")]
    [ProducesResponseType(typeof(PreparedTicketRelatedItemsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedTicketRelatedItemsDto>> GetPreparedTicketRelated(string key, CancellationToken ct)
    {
        PreparedTicketRelatedItems related = await database.GetPreparedTicketRelatedItemsAsync(key, ct);
        return Ok(PreparedTicketDtoMapper.ToDto(related));
    }
}
