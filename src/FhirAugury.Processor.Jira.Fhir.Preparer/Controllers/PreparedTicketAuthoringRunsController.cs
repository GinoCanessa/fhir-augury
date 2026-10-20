using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

[ApiController]
[Route("processing/authoring/runs")]
[Produces("application/json")]
public sealed class PreparedTicketAuthoringRunsController(
    AuthoringRunStore authoringStore,
    JiraAuthoringRunCoordinator coordinator,
    PreparerDatabase database,
    PreparedTicketRunWorkflowRegistry? workflows = null) : ControllerBase
{
    public const string OperationTokenHeader = "X-Fhir-Augury-Authoring-Token";

    [HttpPost("{runId}/items/{itemId}/result")]
    public async Task<IActionResult> SubmitResult(
        string runId,
        string itemId,
        [FromHeader(Name = OperationTokenHeader)] string? operationToken,
        [FromBody] PreparedTicketAuthoringResultRequest request,
        CancellationToken ct)
    {
        string mode = (await authoringStore.EnsureProcessorModeAsync(
            coordinator.ProcessorKind,
            ct: ct)).Mode;
        if (mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            return Conflict(new { error = "authoring-not-activated" });
        }
        if (mode == AuthoringStatusValues.ProcessorModes.CuttingOver)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "cutover-in-progress" });
        }
        if (string.IsNullOrWhiteSpace(operationToken))
        {
            return Unauthorized(new { error = "operation-token-required" });
        }
        if (request.Submission.RunId != runId || request.Submission.ItemId != itemId)
        {
            return Conflict(new { error = "route-coordinate-mismatch" });
        }

        AuthoringRunItemRecord? item = (await authoringStore.GetRunItemsAsync(runId, ct))
            .SingleOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
        {
            return NotFound(new { error = "authoring-item-not-found" });
        }
        if (!string.Equals(item.BusinessKey, request.Payload.Key, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { error = "ticket-key-mismatch" });
        }
        AuthoringRunRecord run = await authoringStore.GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException(
                $"Authoring run '{runId}' disappeared while accepting a result.");

        string contentHash = PreparedTicketAuthoringDtos.ComputeContentHash(request.Payload);
        if (!string.Equals(contentHash, request.Submission.ContentHash, StringComparison.Ordinal))
        {
            return Conflict(new { error = "content-hash-mismatch" });
        }

        try
        {
            AuthoringReceiptAcceptance result = workflows is null
                ? await authoringStore.AcceptResultAsync(
                    request.Submission,
                    operationToken,
                    async (connection, cancellationToken) =>
                    {
                        await JiraProcessingSourceTicketStore
                            .EnsureCurrentSourceRevisionAsync(
                                connection,
                                request.Payload.Key,
                                item.ItemKind,
                                request.Submission.ObservedSourceRevision,
                                cancellationToken);
                        await database.SavePreparedTicketForAuthoringAsync(
                            connection,
                            request.Payload,
                            contentHash,
                            runId,
                            itemId,
                            request.Submission.OperationId,
                            cancellationToken);
                    },
                    ct: ct)
                : await workflows.AcceptResultAsync(
                    run,
                    item,
                    request,
                    operationToken,
                    ct);
            return Ok(result);
        }
        catch (AuthoringConflictException ex)
        {
            if (ex.Code == AuthoringConflictCode.InvalidOperationToken)
            {
                return Unauthorized(new { error = ex.Code.ToString(), detail = ex.Message });
            }
            return Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "invalid-prepared-ticket", detail = ex.Message });
        }
    }
}
