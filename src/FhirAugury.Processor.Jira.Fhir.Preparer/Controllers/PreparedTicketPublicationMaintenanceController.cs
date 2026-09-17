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
    private const string AuthoringProcessorKind = "jira-fhir";

    private readonly PreparedTicketPublicationRefreshService _service;
    private readonly PreparedTicketPublicationReconciliationPlanner?
        _reconciliationPlanner;
    private readonly PreparedTicketPublicationRecoveryService?
        _recoveryService;
    private readonly PreparedTicketCanonicalEpochRecoveryService?
        _canonicalEpochRecoveryService;
    private readonly PreparerDatabase? _database;

    public PreparedTicketPublicationMaintenanceController(
        PreparedTicketPublicationRefreshService service)
    {
        _service = service;
    }

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

    [ActivatorUtilitiesConstructor]
    public PreparedTicketPublicationMaintenanceController(
        PreparedTicketPublicationRefreshService service,
        PreparedTicketPublicationReconciliationPlanner reconciliationPlanner,
        PreparedTicketPublicationRecoveryService recoveryService,
        PreparedTicketCanonicalEpochRecoveryService
            canonicalEpochRecoveryService,
        PreparerDatabase database)
        : this(
            service,
            reconciliationPlanner,
            recoveryService,
            database)
    {
        _canonicalEpochRecoveryService =
            canonicalEpochRecoveryService;
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
            if (_database is not null)
            {
                await _database.EnsureSnapshotWorkflowAllowedAsync(
                    new AuthoringSnapshotWorkflowIntent(
                        AuthoringProcessorKind,
                        DatabaseOnly: false,
                        AuthoringRunPurposeValues.PublicationRefresh),
                    ct);
            }
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
            AuthoringConflictException failure =
                await EnrichCanonicalRestrictionAsync(
                    ex,
                    AuthoringRunPurposeValues.PublicationRefresh,
                    ct);
            string[] conflictingRunIds = failure.RelatedRunIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return Conflict(
                new PreparedTicketPublicationRefreshFailure(
                    ToFailureCode(failure.Code),
                    failure.Message,
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
            if (_database is not null)
            {
                await _database.EnsureSnapshotWorkflowAllowedAsync(
                    new AuthoringSnapshotWorkflowIntent(
                        AuthoringProcessorKind,
                        DatabaseOnly: false,
                        AuthoringRunPurposeValues
                            .PublicationReconciliation),
                    ct);
            }
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
            AuthoringConflictException failure =
                await EnrichCanonicalRestrictionAsync(
                    ex,
                    AuthoringRunPurposeValues.PublicationReconciliation,
                    ct);
            string[] runIds = failure.RelatedRunIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return Conflict(
                new PreparedTicketPublicationReconciliationFailure(
                    failure.Code switch
                    {
                        AuthoringConflictCode.MutationFenceUnavailable =>
                            PreparedTicketPublicationReconciliationFailureCodes
                                .RecoveryInProgress,
                        AuthoringConflictCode
                            .CanonicalUnpublishedRestriction =>
                            PreparedTicketPublicationReconciliationFailureCodes
                                .CanonicalUnpublishedRestriction,
                        _ =>
                            PreparedTicketPublicationReconciliationFailureCodes
                                .InvalidBaseline,
                    },
                    failure.Message,
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
            return Ok(await AttachRecoveryLinkAsync(
                await planner.GetStatusAsync(runId, ct),
                ct));
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
                await AttachRecoveryLinkAsync(
                    await planner.GetStatusAsync(runId, ct),
                    ct),
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
        catch (AuthoringConflictException ex)
        {
            AuthoringConflictException failure =
                await EnrichCanonicalRestrictionAsync(
                    ex,
                    AuthoringRunPurposeValues.PublicationReconciliation,
                    ct);
            string[] runIds = failure.RelatedRunIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return Conflict(
                new PreparedTicketPublicationReconciliationFailure(
                    failure.Code ==
                        AuthoringConflictCode
                            .CanonicalUnpublishedRestriction
                        ? PreparedTicketPublicationReconciliationFailureCodes
                            .CanonicalUnpublishedRestriction
                        : PreparedTicketPublicationReconciliationFailureCodes
                            .PromotionRecoveryFailure,
                    failure.Message,
                    runIds,
                    runIds.Length == 1
                        ? runIds[0]
                        : failure.Code ==
                            AuthoringConflictCode
                                .CanonicalUnpublishedRestriction
                            ? null
                            : runId));
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
            PreparedTicketPublicationReconciliationCancelResult result =
                await planner.CancelAsync(
                runId,
                request.Reason,
                ct);
            return Ok(result with
            {
                Status = await AttachRecoveryLinkAsync(result.Status, ct),
            });
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
                    await AttachRecoveryLinkAsync(
                        await planner.GetStatusAsync(runId, ct),
                        ct),
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

    [HttpPost("{sourceRunId}/canonical-epoch-recovery")]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryStartResult),
        StatusCodes.Status202Accepted)]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryFailure),
        StatusCodes.Status404NotFound)]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryFailure),
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartCanonicalEpochRecovery(
        string sourceRunId,
        CancellationToken ct)
    {
        PreparedTicketCanonicalEpochRecoveryService service =
            RequireCanonicalEpochRecoveryService();
        try
        {
            PreparedTicketCanonicalEpochRecoveryStartResult result =
                await service.StartAsync(sourceRunId, ct);
            return Accepted(
                $"/processing/authoring/runs/{Uri.EscapeDataString(result.Status.Run.RunId)}/canonical-epoch-recovery",
                result);
        }
        catch (PreparedTicketCanonicalEpochRecoveryException ex)
        {
            PreparedTicketCanonicalEpochRecoveryFailure failure = new(
                ex.FailureCode,
                ex.Message,
                ex.RelatedRunIds,
                ex.RelatedRunIds.Count == 1
                    ? ex.RelatedRunIds[0]
                    : null);
            return ex.FailureCode ==
                PreparedTicketCanonicalEpochRecoveryFailureCodes
                    .InvalidSourceReconciliation
                ? NotFound(failure)
                : Conflict(failure);
        }
        catch (AuthoringConflictException ex)
        {
            return Conflict(
                new PreparedTicketCanonicalEpochRecoveryFailure(
                    PreparedTicketCanonicalEpochRecoveryFailureCodes
                        .RecoveryInProgress,
                    ex.Message,
                    ex.RelatedRunIds,
                    ex.RelatedRunIds.Count == 1
                        ? ex.RelatedRunIds[0]
                        : null));
        }
    }

    [HttpGet("{runId}/canonical-epoch-recovery")]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryStatusResult),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryFailure),
        StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCanonicalEpochRecoveryStatus(
        string runId,
        CancellationToken ct)
    {
        try
        {
            return Ok(await RequireCanonicalEpochRecoveryService()
                .GetStatusAsync(runId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                new PreparedTicketCanonicalEpochRecoveryFailure(
                    PreparedTicketCanonicalEpochRecoveryFailureCodes
                        .InvalidSourceReconciliation,
                    ex.Message,
                    RunId: runId));
        }
    }

    [HttpPost("{runId}/canonical-epoch-recovery/retry")]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryRetryResult),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryFailure),
        StatusCodes.Status404NotFound)]
    [ProducesResponseType(
        typeof(PreparedTicketCanonicalEpochRecoveryFailure),
        StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RetryCanonicalEpochRecovery(
        string runId,
        CancellationToken ct)
    {
        PreparedTicketCanonicalEpochRecoveryService service =
            RequireCanonicalEpochRecoveryService();
        try
        {
            _ = await service.RecoverAsync(runId, ct);
            return Ok(new PreparedTicketCanonicalEpochRecoveryRetryResult(
                await service.GetStatusAsync(runId, ct),
                true));
        }
        catch (PreparedTicketCanonicalEpochRecoveryException ex)
        {
            return Conflict(
                new PreparedTicketCanonicalEpochRecoveryFailure(
                    ex.FailureCode,
                    ex.Message,
                    ex.RelatedRunIds,
                    runId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                new PreparedTicketCanonicalEpochRecoveryFailure(
                    PreparedTicketCanonicalEpochRecoveryFailureCodes
                        .InvalidSourceReconciliation,
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
            AuthoringConflictCode.CanonicalUnpublishedRestriction =>
                PreparedTicketPublicationReconciliationFailureCodes
                    .CanonicalUnpublishedRestriction,
            _ => PreparedTicketPublicationRefreshFailureCodes
                .RunNotActive,
        };

    private async Task<AuthoringConflictException>
        EnrichCanonicalRestrictionAsync(
            AuthoringConflictException exception,
            string purpose,
            CancellationToken ct)
    {
        if (exception.Code !=
                AuthoringConflictCode.CanonicalUnpublishedRestriction ||
            exception.RelatedRunIds.Count != 0 ||
            _database is null)
        {
            return exception;
        }

        try
        {
            await _database.EnsureSnapshotWorkflowAllowedAsync(
                new AuthoringSnapshotWorkflowIntent(
                    AuthoringProcessorKind,
                    DatabaseOnly: false,
                    purpose),
                ct);
        }
        catch (AuthoringConflictException enriched)
            when (enriched.Code ==
                AuthoringConflictCode.CanonicalUnpublishedRestriction)
        {
            return enriched;
        }
        return exception;
    }

    private async Task<PreparedTicketPublicationReconciliationStatusResult>
        AttachRecoveryLinkAsync(
            PreparedTicketPublicationReconciliationStatusResult status,
            CancellationToken ct)
        => _canonicalEpochRecoveryService is null
            ? status
            : await _canonicalEpochRecoveryService.AttachRecoveryLinkAsync(
                status,
                ct);

    private PreparedTicketCanonicalEpochRecoveryService
        RequireCanonicalEpochRecoveryService()
        => _canonicalEpochRecoveryService ??
           throw new InvalidOperationException(
               "Canonical-epoch recovery is not configured.");
}
