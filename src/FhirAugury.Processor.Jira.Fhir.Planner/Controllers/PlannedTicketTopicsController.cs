using FhirAugury.Common.WorkGroups;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Contracts;
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
public sealed class PlannedTicketTopicsController : ControllerBase
{
    private readonly PlannerDatabase _database;
    private readonly AuthoringRunStore _authoringStore;

    public PlannedTicketTopicsController(
        PlannerDatabase database,
        AuthoringRunStore? authoringStore = null)
    {
        _database = database;
        _authoringStore = authoringStore ?? new AuthoringRunStore(database);
    }

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

        PlannedTicketTopicsForCategory? result = await _database.GetWorkGroupTopicsAsync(canonical, specification, type, ct);
        return result is null ? NotFound() : Ok(PlannedTicketDtoMapper.ToDto(result));
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PutTopics([FromBody] PlannedTicketTopicGroupingRequest request, CancellationToken ct)
    {
        try
        {
            IActionResult? modeFailure = await GetWriteModeFailureAsync(
                request.Authoring is not null,
                ct);
            if (modeFailure is not null)
            {
                return modeFailure;
            }

            PlannedTicketTopicGroupingPayload payload = request.ToPayload();
            if (request.Authoring is null)
            {
                await _database.SaveTopicGroupingAsync(payload, ct);
                return NoContent();
            }

            await _database.SaveTopicGroupingForRunAsync(
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
                    PlannerDatabase.GetPartitionKey(
                        payload.WorkGroupClean,
                        payload.Specification,
                        payload.Type),
                    request.Authoring.InputFingerprint,
                    ct)
                ?? throw new InvalidOperationException(
                    "Grouping persistence completed without a durable partition receipt.");
            return Ok(receipt);
        }
        catch (Exception ex) when (ex is ArgumentException or AuthoringConflictException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IActionResult?> GetWriteModeFailureAsync(
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
