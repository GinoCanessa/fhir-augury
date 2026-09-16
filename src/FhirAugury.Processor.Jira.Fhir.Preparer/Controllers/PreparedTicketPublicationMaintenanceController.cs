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

    public PreparedTicketPublicationMaintenanceController(
        PreparedTicketPublicationRefreshService service)
    {
        _service = service;
    }

    [ActivatorUtilitiesConstructor]
    public PreparedTicketPublicationMaintenanceController(
        PreparedTicketPublicationRefreshService service,
        PreparedTicketPublicationReconciliationPlanner reconciliationPlanner)
    {
        _service = service;
        _reconciliationPlanner = reconciliationPlanner;
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
