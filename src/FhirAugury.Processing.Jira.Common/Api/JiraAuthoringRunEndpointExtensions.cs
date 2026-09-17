using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Api;

public sealed record JiraAuthoringRunRequest(
    IReadOnlyList<string>? TicketKeys = null,
    bool DatabaseOnly = false);

public static class JiraAuthoringRunEndpointExtensions
{
    internal static void MapAuthoringCore(IEndpointRouteBuilder endpoints, string prefix)
    {
        endpoints.MapGet($"{prefix}/processing/authoring/runs", ListRunsAsync);
        endpoints.MapPost($"{prefix}/processing/authoring/runs", CreateRunAsync);
        endpoints.MapGet($"{prefix}/processing/authoring/runs/{{runId}}", GetRunAsync);
        endpoints.MapPost(
            $"{prefix}/processing/authoring/runs/{{runId}}/items/{{itemId}}/retry",
            RetryItemAsync);
        endpoints.MapPost(
            $"{prefix}/processing/authoring/runs/{{runId}}/items/{{itemId}}/supersede",
            SupersedeItemAsync);
        endpoints.MapGet(
            $"{prefix}/processing/authoring/runs/{{runId}}/operations/{{operationId}}/receipt",
            GetReceiptAsync);
        endpoints.MapGet(
            $"{prefix}/processing/authoring/runs/{{runId}}/snapshot",
            GetSnapshotAsync);
        endpoints.MapGet(
            $"{prefix}/processing/authoring/runs/{{runId}}/snapshot/bytes",
            GetSnapshotBytesAsync);
    }

    private static async Task<IResult> CreateRunAsync(
        JiraAuthoringRunRequest request,
        JiraAuthoringRunCoordinator coordinator,
        JiraProcessingSourceTicketStore sourceStore,
        IJiraTicketDiscoveryClient discoveryClient,
        AuthoringRunStore authoringStore,
        AuthoringRunControlService controlService,
        IOptions<JiraProcessingOptions> optionsAccessor,
        CancellationToken ct)
    {
        IResult? modeFailure = await GetModeFailureAsync(authoringStore, coordinator, ct);
        if (modeFailure is not null)
        {
            return modeFailure;
        }

        JiraAuthoringRunCreation? creation;
        if (request.TicketKeys is null || request.TicketKeys.Count == 0)
        {
            try
            {
                creation = await coordinator.CreateScheduledRunAsync(
                    databaseOnly: request.DatabaseOnly,
                    ct: ct);
            }
            catch (AuthoringConflictException ex)
                when (ex.Code is
                    AuthoringConflictCode.ActiveRunCapacityReached or
                    AuthoringConflictCode.CanonicalUnpublishedRestriction)
            {
                return Results.Conflict(ToConflictResponse(
                    ToConflictCode(ex.Code),
                    ex));
            }
            if (creation is null)
            {
                return Results.NoContent();
            }
        }
        else
        {
            List<JiraProcessingSourceTicketRecord> tickets = [];
            foreach (string key in request.TicketKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                JiraProcessingSourceTicketRecord? row = await sourceStore.GetByKeyAsync(
                    key,
                    optionsAccessor.Value.SourceTicketShape,
                    ct);
                if (row is null)
                {
                    JiraTicketDiscoveryItem? discovered =
                        await discoveryClient.GetTicketWithProvenanceAsync(
                            key,
                            optionsAccessor.Value.SourceTicketShape,
                            ct);
                    if (discovered is null)
                    {
                        return Results.NotFound(new { error = $"Ticket {key} was not found." });
                    }
                    row = await sourceStore.UpsertAsync(
                        discovered.Ticket,
                        optionsAccessor.Value.SourceTicketShape,
                        resetProcessingStatus: false,
                        provenance: discovered.Provenance,
                        ct: ct);
                }
                tickets.Add(row);
            }

            try
            {
                creation = await coordinator.CreateExplicitRunAsync(
                    tickets,
                    request.DatabaseOnly,
                    ct);
            }
            catch (AuthoringConflictException ex)
            {
                if (ex.Code == AuthoringConflictCode.ActiveRunCapacityReached)
                {
                    return Results.Conflict(ToConflictResponse(
                        "active-run-capacity-reached",
                        ex));
                }
                if (ex.Code ==
                    AuthoringConflictCode.CanonicalUnpublishedRestriction)
                {
                    return Results.Conflict(ToConflictResponse(
                        ToConflictCode(ex.Code),
                        ex));
                }
                return Results.Conflict(ToConflictResponse(
                    "revision-already-scheduled",
                    ex));
            }
        }

        AuthoringRunControlStatus status = await controlService.GetStatusAsync(
            coordinator.ProcessorKind,
            creation.Run.Id,
            ct);
        return Results.Accepted(
            $"/processing/authoring/runs/{Uri.EscapeDataString(creation.Run.Id)}",
            ToResponse(status));
    }

    private static async Task<IResult> ListRunsAsync(
        int? limit,
        JiraAuthoringRunCoordinator coordinator,
        AuthoringRunControlService controlService,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await controlService.ListAsync(
                coordinator.ProcessorKind,
                limit ?? AuthoringRunStore.DefaultOperatorRunLimit,
                ct));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetRunAsync(
        string runId,
        JiraAuthoringRunCoordinator coordinator,
        AuthoringRunControlService controlService,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(ToResponse(await controlService.GetStatusAsync(
                coordinator.ProcessorKind,
                runId,
                ct)));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> RetryItemAsync(
        string runId,
        string itemId,
        JiraAuthoringRunCoordinator coordinator,
        AuthoringRunControlService controlService,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await controlService.RetryItemAsync(
                coordinator.ProcessorKind,
                runId,
                itemId,
                ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (AuthoringConflictException ex)
        {
            return Results.Conflict(ToConflictResponse(
                ToConflictCode(ex.Code),
                ex));
        }
    }

    private static async Task<IResult> SupersedeItemAsync(
        string runId,
        string itemId,
        AuthoringItemSupersedeRequest? request,
        JiraAuthoringRunCoordinator coordinator,
        AuthoringRunControlService controlService,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await controlService.SupersedeItemAsync(
                coordinator.ProcessorKind,
                runId,
                itemId,
                request ?? new AuthoringItemSupersedeRequest(string.Empty),
                ct));
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(
                new { error = "A non-empty supersede reason is required." });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (AuthoringConflictException ex)
        {
            return Results.Conflict(ToConflictResponse(
                ToConflictCode(ex.Code),
                ex));
        }
    }

    private static async Task<IResult> GetReceiptAsync(
        string runId,
        string operationId,
        AuthoringRunStore store,
        CancellationToken ct)
    {
        AuthoringResultReceipt? receipt = await store.GetReceiptByOperationAsync(operationId, ct);
        return receipt is null || !string.Equals(receipt.RunId, runId, StringComparison.Ordinal)
            ? Results.NotFound(new { error = $"Receipt for operation '{operationId}' was not found." })
            : Results.Ok(receipt);
    }

    private static async Task<IResult> GetSnapshotAsync(
        string runId,
        AuthoringRunStore store,
        CancellationToken ct)
    {
        AuthoringRunRecord? run = await store.GetRunAsync(runId, ct);
        if (run is null)
        {
            return Results.NotFound(new { error = $"Authoring run '{runId}' was not found." });
        }
        if (run.SnapshotId is null)
        {
            return Results.NotFound(new { error = $"Run '{runId}' has no ready snapshot." });
        }

        AuthoringSnapshotDescriptor? descriptor =
            await store.GetSnapshotDescriptorAsync(run.SnapshotId, ct);
        return descriptor is null
            ? Results.NotFound(new { error = $"Run '{runId}' has no ready snapshot." })
            : Results.Ok(descriptor);
    }

    private static async Task<IResult> GetSnapshotBytesAsync(
        string runId,
        AuthoringRunStore store,
        CancellationToken ct)
    {
        AuthoringReviewSnapshotRecord? snapshot =
            await store.GetReadySnapshotRecordAsync(runId, ct);
        if (snapshot is null || !File.Exists(snapshot.Path))
        {
            return Results.NotFound(new { error = $"Run '{runId}' has no ready snapshot bytes." });
        }

        return Results.File(
            snapshot.Path,
            "application/vnd.sqlite3",
            Path.GetFileName(snapshot.Path),
            enableRangeProcessing: true);
    }

    internal static async Task<IResult?> GetModeFailureAsync(
        AuthoringRunStore store,
        JiraAuthoringRunCoordinator coordinator,
        CancellationToken ct)
    {
        AuthoringProcessorModeRecord mode = await store.EnsureProcessorModeAsync(
            coordinator.ProcessorKind,
            ct: ct);
        if (string.Equals(mode.Mode, AuthoringStatusValues.ProcessorModes.Legacy, StringComparison.Ordinal))
        {
            return Results.Conflict(ToConflictResponse("authoring-not-activated"));
        }
        if (string.Equals(mode.Mode, AuthoringStatusValues.ProcessorModes.CuttingOver, StringComparison.Ordinal))
        {
            return Results.Json(
                ToConflictResponse("cutover-in-progress"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (mode.RevalidationRequired)
        {
            string[] conflictingRunIds = mode.RevalidationRunId is null
                ? []
                : [mode.RevalidationRunId];
            return Results.Conflict(ToConflictResponse(
                "revalidation-required",
                relatedRunIds: conflictingRunIds,
                runId: mode.RevalidationRunId));
        }
        return null;
    }

    private static AuthoringRunResponse ToResponse(
        AuthoringRunControlStatus status)
        => new(status.Run, status.Items);

    private static AuthoringConflictResponse ToConflictResponse(
        string error,
        AuthoringConflictException exception)
        => ToConflictResponse(
            error,
            exception.Message,
            exception.RelatedRunIds);

    private static AuthoringConflictResponse ToConflictResponse(
        string error,
        string? detail = null,
        IEnumerable<string>? relatedRunIds = null,
        string? runId = null)
    {
        string[] conflictingRunIds = (relatedRunIds ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AuthoringConflictResponse(
            error,
            detail,
            conflictingRunIds,
            runId ?? (conflictingRunIds.Length == 1
                ? conflictingRunIds[0]
                : null));
    }

    private static string ToConflictCode(AuthoringConflictCode code)
        => code switch
        {
            AuthoringConflictCode.ReconciliationCancelRequired =>
                "reconciliation-cancel-required",
            AuthoringConflictCode.CanonicalUnpublishedRestriction =>
                AuthoringConflictException
                    .CanonicalUnpublishedRestrictionCode,
            AuthoringConflictCode.ActiveRunCapacityReached =>
                "active-run-capacity-reached",
            _ => code.ToString(),
        };
}

public sealed record JiraAuthoringRunResponse(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);
