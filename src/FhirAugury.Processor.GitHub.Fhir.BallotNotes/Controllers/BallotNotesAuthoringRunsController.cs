using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Controllers;

[ApiController]
[Route("api/v1/ballot-notes/authoring/runs")]
[Produces("application/json")]
public sealed class BallotNotesAuthoringRunsController(
    AuthoringRunStore authoringStore,
    AuthoringRunControlService controlService,
    BallotNotesAuthoringRunCoordinator coordinator,
    BallotNotesDatabase database) : ControllerBase
{
    public const string OperationTokenHeader =
        "X-Fhir-Augury-Authoring-Token";

    [HttpPost]
    public async Task<IActionResult> CreateRun(
        [FromBody] BallotNotesAuthoringRunRequest request,
        CancellationToken ct)
    {
        IActionResult? modeFailure = await GetModeFailureAsync(ct);
        if (modeFailure is not null)
        {
            return modeFailure;
        }
        try
        {
            BallotNotesAuthoringRunCreation creation =
                await coordinator.CreateRunAsync(request, ct);
            AuthoringRunControlStatus status =
                await controlService.GetStatusAsync(
                    coordinator.ProcessorKind,
                    creation.Run.Id,
                    ct);
            return Accepted(
                $"/api/v1/ballot-notes/authoring/runs/{creation.Run.Id}",
                ToResponse(status));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = "hydration-execution-not-found", detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "invalid-authoring-run", detail = ex.Message });
        }
        catch (AuthoringConflictException ex)
        {
            return Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "hydration-execution-not-ready", detail = ex.Message });
        }
    }

    [HttpGet("{runId}")]
    public async Task<IActionResult> GetRun(
        string runId,
        CancellationToken ct)
    {
        try
        {
            return Ok(ToResponse(await controlService.GetStatusAsync(
                coordinator.ProcessorKind,
                runId,
                ct)));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("{runId}/items/{itemId}/retry")]
    public async Task<IActionResult> RetryItem(
        string runId,
        string itemId,
        CancellationToken ct)
    {
        try
        {
            return Ok(await controlService.RetryItemAsync(
                coordinator.ProcessorKind,
                runId,
                itemId,
                ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (AuthoringConflictException ex)
        {
            return Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
        }
    }

    [HttpPost("{runId}/items/{itemId}/supersede")]
    public async Task<IActionResult> SupersedeItem(
        string runId,
        string itemId,
        [FromBody] AuthoringItemSupersedeRequest? request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "A non-empty supersede reason is required." });
        }

        try
        {
            return Ok(await controlService.SupersedeItemAsync(
                coordinator.ProcessorKind,
                runId,
                itemId,
                request,
                ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (AuthoringConflictException ex)
        {
            return Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
        }
    }

    [HttpGet("{runId}/operations/{operationId}/receipt")]
    public async Task<IActionResult> GetReceipt(
        string runId,
        string operationId,
        CancellationToken ct)
    {
        AuthoringResultReceipt? receipt =
            await authoringStore.GetReceiptByOperationAsync(operationId, ct);
        return receipt is null ||
            !string.Equals(receipt.RunId, runId, StringComparison.Ordinal)
            ? NotFound(new { error = "receipt-not-found" })
            : Ok(receipt);
    }

    [HttpGet("{runId}/snapshot")]
    public async Task<IActionResult> GetSnapshot(
        string runId,
        CancellationToken ct)
    {
        AuthoringRunRecord? run = await authoringStore.GetRunAsync(runId, ct);
        if (run is null)
        {
            return NotFound(new { error = "authoring-run-not-found" });
        }
        if (run.SnapshotId is null)
        {
            return NotFound(new { error = "snapshot-not-ready" });
        }
        AuthoringSnapshotDescriptor? descriptor =
            await authoringStore.GetSnapshotDescriptorAsync(
                run.SnapshotId,
                ct);
        return descriptor is null
            ? NotFound(new { error = "snapshot-not-ready" })
            : Ok(descriptor);
    }

    [HttpGet("{runId}/snapshot/bytes")]
    public async Task<IActionResult> GetSnapshotBytes(
        string runId,
        CancellationToken ct)
    {
        AuthoringReviewSnapshotRecord? snapshot =
            await authoringStore.GetReadySnapshotRecordAsync(runId, ct);
        if (snapshot is null || !System.IO.File.Exists(snapshot.Path))
        {
            return NotFound(new { error = "snapshot-not-ready" });
        }

        return PhysicalFile(
            Path.GetFullPath(snapshot.Path),
            "application/vnd.sqlite3",
            Path.GetFileName(snapshot.Path),
            enableRangeProcessing: true);
    }

    [HttpPost("{runId}/items/{itemId}/{type}/{slug}/result")]
    public async Task<IActionResult> SubmitResult(
        string runId,
        string itemId,
        string type,
        string slug,
        [FromHeader(Name = OperationTokenHeader)] string? operationToken,
        [FromBody] BallotNoteAuthoringResultRequest request,
        CancellationToken ct)
    {
        IActionResult? modeFailure = await GetModeFailureAsync(ct);
        if (modeFailure is not null)
        {
            return modeFailure;
        }
        if (string.IsNullOrWhiteSpace(operationToken))
        {
            return Unauthorized(new { error = "operation-token-required" });
        }
        if (request.Submission.RunId != runId ||
            request.Submission.ItemId != itemId)
        {
            return Conflict(new { error = "route-coordinate-mismatch" });
        }

        AuthoringRunItemRecord? item =
            (await authoringStore.GetRunItemsAsync(runId, ct))
            .SingleOrDefault(value => value.Id == itemId);
        if (item is null)
        {
            return NotFound(new { error = "authoring-item-not-found" });
        }
        if (!string.Equals(
                item.BusinessKey,
                slug,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                item.ItemKind,
                type,
                StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { error = "slug-type-route-mismatch" });
        }

        BallotNoteProse prose = new()
        {
            NeedsNote = request.Prose.NeedsNote,
            ProposedBallotNoteHtml =
                request.Prose.ProposedBallotNoteHtml ?? string.Empty,
            RollupSummaryMarkdown =
                request.Prose.RollupSummaryMarkdown ?? string.Empty,
            NotesForReviewerMarkdown =
                request.Prose.NotesForReviewerMarkdown ?? string.Empty,
            SourceFilesNote = request.Prose.SourceFilesNote ?? string.Empty,
        };
        string contentHash = BallotNotesDatabase.ComputeProseHash(prose);
        if (!string.Equals(
            contentHash,
            request.Submission.ContentHash,
            StringComparison.Ordinal))
        {
            return Conflict(new { error = "content-hash-mismatch" });
        }

        try
        {
            AuthoringReceiptAcceptance acceptance =
                await authoringStore.AcceptResultAsync(
                    request.Submission,
                    operationToken,
                    (connection, cancellationToken) =>
                        database.SaveNoteProseForAuthoringAsync(
                            connection,
                            runId,
                            itemId,
                            request.Submission.OperationId,
                            slug,
                            type,
                            request.Submission.ObservedSourceRevision,
                            prose,
                            contentHash,
                            cancellationToken),
                    ct: ct);
            return Ok(acceptance);
        }
        catch (AuthoringConflictException ex)
        {
            return ex.Code == AuthoringConflictCode.InvalidOperationToken
                ? Unauthorized(new { error = ex.Code.ToString(), detail = ex.Message })
                : Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
        }
    }

    private async Task<IActionResult?> GetModeFailureAsync(
        CancellationToken ct)
    {
        string mode = (await authoringStore.EnsureProcessorModeAsync(
            coordinator.ProcessorKind,
            ct: ct)).Mode;
        if (string.Equals(
            mode,
            AuthoringStatusValues.ProcessorModes.Legacy,
            StringComparison.Ordinal))
        {
            return Conflict(new { error = "authoring-not-activated" });
        }
        if (string.Equals(
            mode,
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            StringComparison.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "cutover-in-progress" });
        }
        return null;
    }

    private static BallotNotesAuthoringRunResponse ToResponse(
        AuthoringRunControlStatus status)
        => new(status.Run, status.Items);
}
