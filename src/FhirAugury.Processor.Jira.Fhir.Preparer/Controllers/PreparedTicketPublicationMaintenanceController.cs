using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

[ApiController]
[Route("processing/authoring/runs")]
[Produces("application/json")]
public sealed class PreparedTicketPublicationMaintenanceController(
    PreparedTicketPublicationRefreshService service) : ControllerBase
{
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
                await service.StartAsync(sourceRunId, ct);
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
