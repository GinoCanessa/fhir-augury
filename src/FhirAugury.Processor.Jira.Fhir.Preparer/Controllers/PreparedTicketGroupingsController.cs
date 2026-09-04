using FhirAugury.Common.WorkGroups;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
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
public sealed class PreparedTicketGroupingsController : ControllerBase
{
    private readonly PreparerDatabase _database;
    private readonly AuthoringRunStore _authoringStore;

    public PreparedTicketGroupingsController(
        PreparerDatabase database,
        AuthoringRunStore? authoringStore = null)
    {
        _database = database;
        _authoringStore = authoringStore ?? new AuthoringRunStore(database);
    }

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
        PreparedTicketGroupingWorkGroupView? view = await _database.GetWorkGroupGroupingsAsync(canonical, ct);
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
        PreparedTicketGroupingPartition? partition = await _database.GetGroupingAsync(canonical, specification, type, ct);
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
            ActionResult? modeFailure = await GetWriteModeFailureAsync(
                request.Authoring is not null,
                ct);
            if (modeFailure is not null)
            {
                return modeFailure;
            }

            string canonical = Canonicalise(workGroupClean);
            PreparedTicketGroupingPayload payload =
                PreparedTicketGroupingDtoMapper.ToPayload(canonical, specification, type, request);
            if (request.Authoring is null)
            {
                PreparedTicketGroupingSaveResult legacyResult =
                    await _database.SaveGroupingAsync(payload, ct);
                return Ok(PreparedTicketGroupingDtoMapper.ToDto(legacyResult));
            }

            PreparedTicketGroupingSaveResult result =
                await _database.SaveGroupingForRunAsync(
                    payload,
                    request.Authoring.RunId,
                    request.Authoring.StageId,
                    request.Authoring.StageLeaseId,
                    request.Authoring.InputFingerprint,
                    ct);
            AuthoringRunStageReceipt receipt =
                await _database.GetGroupingReceiptAsync(
                    request.Authoring.RunId,
                    request.Authoring.StageId,
                    PreparerDatabase.GetPartitionKey(
                        canonical,
                        specification,
                        type),
                    request.Authoring.InputFingerprint,
                    ct)
                ?? throw new InvalidOperationException(
                    "Grouping persistence completed without a durable partition receipt.");
            return Ok(PreparedTicketGroupingDtoMapper.ToDto(result) with
            {
                AuthoringReceipt = receipt,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or AuthoringConflictException)
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
        ActionResult? modeFailure = await GetWriteModeFailureAsync(
            runScoped: false,
            ct);
        if (modeFailure is not null)
        {
            return modeFailure;
        }

        string canonical = Canonicalise(workGroupClean);
        try
        {
            await _database.DeleteGroupingAsync(canonical, specification, type, ct);
            return NoContent();
        }
        catch (AuthoringConflictException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Authoring run active",
                Detail = ex.Message,
            });
        }
    }

    private static string Canonicalise(string raw)
    {
        string cleaned = Hl7WorkGroupNameCleaner.Clean(raw);
        return string.IsNullOrEmpty(cleaned) ? raw : cleaned;
    }

    private async Task<ActionResult?> GetWriteModeFailureAsync(
        bool runScoped,
        CancellationToken ct)
    {
        string mode = (await _authoringStore.GetProcessorModeAsync(
            "jira-fhir",
            ct)).Mode;
        if (string.Equals(
            mode,
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            StringComparison.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "cutover-in-progress" });
        }
        if (runScoped &&
            string.Equals(
                mode,
                AuthoringStatusValues.ProcessorModes.Legacy,
                StringComparison.Ordinal))
        {
            return Conflict(new { error = "authoring-not-activated" });
        }
        if (!runScoped &&
            string.Equals(
                mode,
                AuthoringStatusValues.ProcessorModes.RunBacked,
                StringComparison.Ordinal))
        {
            return Conflict(new { error = "run-backed-write-required" });
        }
        return null;
    }
}
