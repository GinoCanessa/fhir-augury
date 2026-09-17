using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

[ApiController]
[Route("processing/authoring/runs")]
[Produces("application/json")]
public sealed class PreparedTicketPublicationMaintenanceController
    : ControllerBase
{
    private readonly PreparedTicketPublicationRefreshService _service;
    private readonly PreparedTicketPublicationReconciliationPlanner?
        _reconciliationPlanner;
    private readonly PreparedTicketPublicationRecoveryService?
        _recoveryService;
    private readonly PreparerDatabase? _database;

    public PreparedTicketPublicationMaintenanceController(
        PreparedTicketPublicationRefreshService service)
    {
        _service = service;
    }

    [ActivatorUtilitiesConstructor]
    public PreparedTicketPublicationMaintenanceController(
        PreparedTicketPublicationRefreshService service,
        PreparedTicketPublicationReconciliationPlanner reconciliationPlanner,
        PreparedTicketPublicationRecoveryService recoveryService,
        PreparerDatabase database)
    {
        _service = service;
        _reconciliationPlanner = reconciliationPlanner;
        _recoveryService = recoveryService;
        _database = database;
    }

    [HttpPost("{sourceRunId}/publication-refresh")]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationRefreshResult),
        StatusCodes.Status202Accepted)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationRefreshFailure),
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationRefreshFailure),
        StatusCodes.Status404NotFound)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationRefreshFailure),
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(
        string sourceRunId,
        CancellationToken ct)
    {
        try
        {
            PreparedTicketPublicationRefreshResult result =
                await _service.StartAsync(sourceRunId, ct);
            string location =
                $"/processing/authoring/runs/{Uri.EscapeDataString(result.Run.RunId)}";
            return Accepted(location, result);
        }

        catch (KeyNotFoundException ex)
        {
            return NotFound(
                new PreparedTicketPublicationRefreshFailure(
                    PreparedTicketPublicationRefreshFailureCodes
                        .SourceRunNotFound,
                    ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(
                new PreparedTicketPublicationRefreshFailure(
                    PreparedTicketPublicationRefreshFailureCodes
                        .InvalidSourceRun,
                    ex.Message));
        }
        catch (PreparedTicketPublicationProtectionException ex)
        {
            return Conflict(new PreparedTicketPublicationRefreshFailure(ex.FailureCode, ex.Message));
        }
        catch (AuthoringConflictException ex)
        {
            string[] conflictingRunIds = ex.RelatedRunIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return Conflict(
                new PreparedTicketPublicationRefreshFailure(
                    ToFailureCode(ex.Code),
                    ex.Message,
                    conflictingRunIds,
                    conflictingRunIds.Length == 1
                        ? conflictingRunIds[0]
                        : null));
        }
    }

    [HttpPost("{sourceRunId}/publication-reconciliation")]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationStartResult),
        StatusCodes.Status202Accepted)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationFailure),
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartReconciliation(
        string sourceRunId,
        CancellationToken ct)
    {
        PreparedTicketPublicationReconciliationPlanner planner =
            _reconciliationPlanner ??
            throw new InvalidOperationException(
                "Publication reconciliation is not configured.");
        try
        {
            PreparedTicketPublicationReconciliationStartResult result =
                await planner.StartAsync(sourceRunId, ct);
            return Accepted(
                $"/processing/authoring/runs/{Uri.EscapeDataString(result.Run.RunId)}/publication-reconciliation",
                result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                new PreparedTicketPublicationReconciliationFailure(
                    PreparedTicketPublicationReconciliationFailureCodes
                        .InvalidBaseline,
                    ex.Message));
        }
        catch (PreparedTicketPublicationReconciliationException ex)
        {
            return Conflict(
                new PreparedTicketPublicationReconciliationFailure(
                    ex.FailureCode,
                    ex.Message));
        }
        catch (AuthoringConflictException ex)
        {
            string[] runIds = ex.RelatedRunIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return Conflict(
                new PreparedTicketPublicationReconciliationFailure(
                    ex.Code == AuthoringConflictCode.MutationFenceUnavailable
                        ? PreparedTicketPublicationReconciliationFailureCodes
                            .RecoveryInProgress
                        : PreparedTicketPublicationReconciliationFailureCodes
                            .InvalidBaseline,
                    ex.Message,
                    runIds,
                    runIds.Length == 1 ? runIds[0] : null));
        }
    }

    [HttpGet("{runId}/publication-reconciliation")]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationStatusResult),
        StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReconciliationStatus(
        string runId,
        CancellationToken ct)
    {
        PreparedTicketPublicationReconciliationPlanner planner =
            _reconciliationPlanner ??
            throw new InvalidOperationException(
                "Publication reconciliation is not configured.");
        try
        {
            return Ok(await planner.GetStatusAsync(runId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                new PreparedTicketPublicationReconciliationFailure(
                    PreparedTicketPublicationReconciliationFailureCodes
                        .InvalidBaseline,
                    ex.Message,
                    RunId: runId));
        }
    }

    [HttpPost("{runId}/publication-reconciliation/retry")]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationRetryResult),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationFailure),
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RetryReconciliation(
        string runId,
        CancellationToken ct)
    {
        PreparedTicketPublicationRecoveryService recovery =
            _recoveryService ??
            throw new InvalidOperationException(
                "Publication reconciliation recovery is not configured.");
        PreparedTicketPublicationReconciliationPlanner planner =
            _reconciliationPlanner ??
            throw new InvalidOperationException(
                "Publication reconciliation is not configured.");
        try
        {
            _ = await recovery.RecoverAsync(runId, ct);
            return Ok(new PreparedTicketPublicationReconciliationRetryResult(
                await planner.GetStatusAsync(runId, ct),
                true));
        }
        catch (PreparedTicketPublicationReconciliationException ex)
        {
            return Conflict(
                new PreparedTicketPublicationReconciliationFailure(
                    ex.FailureCode,
                    ex.Message,
                    RunId: runId));
        }
    }

    [HttpPost("{runId}/publication-reconciliation/cancel")]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationCancelResult),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationFailure),
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationFailure),
        StatusCodes.Status404NotFound)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationFailure),
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelReconciliation(
        string runId,
        PreparedTicketPublicationReconciliationCancelRequest request,
        CancellationToken ct)
    {
        PreparedTicketPublicationReconciliationPlanner planner =
            _reconciliationPlanner ??
            throw new InvalidOperationException(
                "Publication reconciliation is not configured.");
        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(
                new PreparedTicketPublicationReconciliationFailure(
                    PreparedTicketPublicationReconciliationFailureCodes
                        .CancellationNotAllowed,
                    "A non-empty cancellation reason is required.",
                    RunId: runId));
        }

        try
        {
            return Ok(await planner.CancelAsync(
                runId,
                request.Reason,
                ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                new PreparedTicketPublicationReconciliationFailure(
                    PreparedTicketPublicationReconciliationFailureCodes
                        .CancellationNotAllowed,
                    ex.Message,
                    RunId: runId));
        }
        catch (PreparedTicketPublicationReconciliationException ex)
        {
            return Conflict(
                new PreparedTicketPublicationReconciliationFailure(
                    ex.FailureCode,
                    ex.Message,
                    RunId: runId));
        }
    }

    [HttpPost("{runId}/publication-reconciliation/abandon")]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationAbandonResult),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(PreparedTicketPublicationReconciliationFailure),
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AbandonReconciliation(
        string runId,
        PreparedTicketPublicationReconciliationAbandonRequest request,
        CancellationToken ct)
    {
        PreparerDatabase database = _database ??
            throw new InvalidOperationException(
                "Publication reconciliation recovery is not configured.");
        PreparedTicketPublicationReconciliationPlanner planner =
            _reconciliationPlanner ??
            throw new InvalidOperationException(
                "Publication reconciliation is not configured.");
        try
        {
            DateTimeOffset abandonedAt = DateTimeOffset.UtcNow;
            await database.AbandonPublicationReconciliationAsync(
                runId,
                request.Reason,
                abandonedAt,
                ct);
            return Ok(
                new PreparedTicketPublicationReconciliationAbandonResult(
                    await planner.GetStatusAsync(runId, ct),
                    abandonedAt,
                    request.Reason));
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException)
        {
            return Conflict(
                new PreparedTicketPublicationReconciliationFailure(
                    PreparedTicketPublicationReconciliationFailureCodes
                        .PromotionRecoveryFailure,
                    ex.Message,
                    RunId: runId));
        }
    }

    private static string ToFailureCode(AuthoringConflictCode code)
        => code switch
        {
            AuthoringConflictCode.AuthoringNotActivated =>
                PreparedTicketPublicationRefreshFailureCodes
                    .AuthoringNotActivated,
            AuthoringConflictCode.CutoverInProgress =>
                PreparedTicketPublicationRefreshFailureCodes
                    .CutoverInProgress,
            AuthoringConflictCode.RevalidationRequired =>
                PreparedTicketPublicationRefreshFailureCodes
                    .RevalidationRequired,
            AuthoringConflictCode.MutationFenceUnavailable =>
                PreparedTicketPublicationRefreshFailureCodes
                    .MutationFenceUnavailable,
            AuthoringConflictCode.SourceRevisionMismatch =>
                PreparedTicketPublicationRefreshFailureCodes
                    .SourceRevisionMismatch,
            AuthoringConflictCode.StageFingerprintMismatch =>
                PreparedTicketPublicationRefreshFailureCodes
                    .StageFingerprintMismatch,
            _ => PreparedTicketPublicationRefreshFailureCodes
                .RunNotActive,
        };
}
