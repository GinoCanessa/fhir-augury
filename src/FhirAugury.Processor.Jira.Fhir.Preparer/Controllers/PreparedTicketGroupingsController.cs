using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

/// <summary>
/// Reads and writes the reviewer-facing
/// <c>(WorkGroup, Specification, Type) → Topic → Linked Ticket Group</c>
/// decomposition documented in the <c>index-prepared</c> skill. Callers
/// must percent-encode the <c>{specification}</c> and <c>{type}</c>
/// path segments — they typically contain spaces (e.g. <c>"FHIR Core"</c>,
/// <c>"Change Request"</c>).
/// </summary>
[ApiController]
[Route("api/v1/prepared-ticket-groupings")]
[Produces("application/json")]
public sealed class PreparedTicketGroupingsController(PreparerDatabase database) : ControllerBase
{
    /// <summary>Gets every partition the work group can render.</summary>
    /// <remarks>
    /// <paramref name="workGroupClean"/> may arrive in any of <c>name</c>
    /// / <c>nameClean</c> form — the controller normalises it via
    /// <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/> defensively.
    /// The <c>code</c> form (e.g. <c>"oo"</c>) requires pre-resolution
    /// at the orchestrator / CLI / MCP layer.
    /// </remarks>
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PreparedTicketGroupingWorkGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreparedTicketGroupingWorkGroupDto>> GetWorkGroup(string workGroupClean, CancellationToken ct)
    {
        string canonical = Canonicalise(workGroupClean);
        PreparedTicketGroupingWorkGroupView? view = await database.GetWorkGroupGroupingsAsync(canonical, ct);
        if (view is null || view.Partitions.Count == 0)
        {
            return NotFound();
        }

        return Ok(PreparedTicketGroupingDtoMapper.ToDto(view));
    }

    /// <summary>Gets a single partition's topics, individual tickets, and metadata.</summary>
    [HttpGet("{workGroupClean}/{specification}/{type}")]
    [ProducesResponseType(typeof(PreparedTicketGroupingPartitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreparedTicketGroupingPartitionDto>> GetPartition(
        string workGroupClean,
        string specification,
        string type,
        CancellationToken ct)
    {
        string canonical = Canonicalise(workGroupClean);
        PreparedTicketGroupingPartition? partition = await database.GetGroupingAsync(canonical, specification, type, ct);
        if (partition is null)
        {
            return NotFound();
        }

        return Ok(PreparedTicketGroupingDtoMapper.ToDto(partition));
    }

    /// <summary>
    /// Replaces the partition's grouping rows atomically. Path segments
    /// override the body for <c>WorkGroupClean</c>, <c>Specification</c>,
    /// and <c>Type</c>; the body supplies the <c>WorkGroupDisplay</c> and
    /// the Topics list. Returns 400 when any referenced ticket key is
    /// unknown or the payload violates the validator's contract.
    /// </summary>
    [HttpPut("{workGroupClean}/{specification}/{type}")]
    [ProducesResponseType(typeof(PreparedTicketGroupingSaveResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PreparedTicketGroupingSaveResultDto>> PutPartition(
        string workGroupClean,
        string specification,
        string type,
        [FromBody] PreparedTicketGroupingPutRequest request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid grouping payload", Detail = "Body is required." });
        }

        try
        {
            string canonical = Canonicalise(workGroupClean);
            PreparedTicketGroupingSaveResult result = await database.SaveGroupingAsync(
                PreparedTicketGroupingDtoMapper.ToPayload(canonical, specification, type, request),
                ct);
            return Ok(PreparedTicketGroupingDtoMapper.ToDto(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid grouping payload", Detail = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a partition's grouping rows. Idempotent — deleting an
    /// already-empty partition returns <c>204</c>.
    /// </summary>
    [HttpDelete("{workGroupClean}/{specification}/{type}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeletePartition(string workGroupClean, string specification, string type, CancellationToken ct)
    {
        string canonical = Canonicalise(workGroupClean);
        await database.DeleteGroupingAsync(canonical, specification, type, ct);
        return NoContent();
    }

    private static string Canonicalise(string raw)
    {
        string cleaned = Hl7WorkGroupNameCleaner.Clean(raw);
        return string.IsNullOrEmpty(cleaned) ? raw : cleaned;
    }
}
