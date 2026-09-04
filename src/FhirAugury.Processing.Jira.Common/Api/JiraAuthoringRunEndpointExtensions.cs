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
        endpoints.MapPost($"{prefix}/processing/authoring/runs", CreateRunAsync);
        endpoints.MapGet($"{prefix}/processing/authoring/runs/{{runId}}", GetRunAsync);
        endpoints.MapPost(
            $"{prefix}/processing/authoring/runs/{{runId}}/items/{{itemId}}/retry",
            RetryItemAsync);
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
            creation = await coordinator.CreateScheduledRunAsync(
                databaseOnly: request.DatabaseOnly,
                ct: ct);
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
                    FhirAugury.Common.Api.JiraIssueSummaryEntry? discovered =
                        await discoveryClient.GetTicketAsync(
                            key,
                            optionsAccessor.Value.SourceTicketShape,
                            ct);
                    if (discovered is null)
                    {
                        return Results.NotFound(new { error = $"Ticket {key} was not found." });
                    }
                    row = await sourceStore.UpsertAsync(
                        discovered,
                        optionsAccessor.Value.SourceTicketShape,
                        resetProcessingStatus: false,
                        ct);
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
                return Results.Conflict(new
                {
                    error = "revision-already-scheduled",
                    detail = ex.Message,
                });
            }
        }

        return Results.Accepted(
            $"/processing/authoring/runs/{Uri.EscapeDataString(creation.Run.Id)}",
            ToResponse(creation.Run, creation.Items));
    }

    private static async Task<IResult> GetRunAsync(
        string runId,
        AuthoringRunStore store,
        CancellationToken ct)
    {
        AuthoringRunRecord? run = await store.GetRunAsync(runId, ct);
        return run is null
            ? Results.NotFound(new { error = $"Authoring run '{runId}' was not found." })
            : Results.Ok(ToResponse(run, await store.GetRunItemsAsync(runId, ct)));
    }

    private static async Task<IResult> RetryItemAsync(
        string runId,
        string itemId,
        AuthoringRunStore store,
        CancellationToken ct)
    {
        AuthoringRunItemRecord? item = (await store.GetRunItemsAsync(runId, ct))
            .SingleOrDefault(value => value.Id == itemId);
        if (item is null)
        {
            return Results.NotFound(new { error = $"Authoring item '{itemId}' was not found in run '{runId}'." });
        }

        try
        {
            return Results.Ok(await store.RetryItemAsync(itemId, ct: ct));
        }
        catch (AuthoringConflictException ex)
        {
            return Results.Conflict(new { error = ex.Code.ToString(), detail = ex.Message });
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
        string mode = (await store.EnsureProcessorModeAsync(coordinator.ProcessorKind, ct: ct)).Mode;
        if (string.Equals(mode, AuthoringStatusValues.ProcessorModes.Legacy, StringComparison.Ordinal))
        {
            return Results.Conflict(new { error = "authoring-not-activated" });
        }
        if (string.Equals(mode, AuthoringStatusValues.ProcessorModes.CuttingOver, StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "cutover-in-progress" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return null;
    }

    private static JiraAuthoringRunResponse ToResponse(
        AuthoringRunRecord run,
        IReadOnlyList<AuthoringRunItemRecord> items)
        => new(
            new AuthoringRunStatus(
                run.Id,
                run.ProcessorKind,
                run.AuthoringEpoch,
                run.Status,
                run.DatabaseOnly,
                run.TotalItems,
                items.Count(item => item.Status == AuthoringStatusValues.Items.Complete),
                items.Count(item => item.Status == AuthoringStatusValues.Items.Error),
                run.CreatedAt,
                run.StartedAt,
                run.CompletedAt,
                run.Error),
            items.Select(item => new AuthoringRunItemStatus(
                item.Id,
                item.RunId,
                item.BusinessKey,
                item.ItemKind,
                item.ExpectedSourceRevision,
                item.Status,
                item.CurrentOperationId,
                item.AcceptedReceiptId,
                item.AttemptCount,
                item.CreatedAt,
                item.StartedAt,
                item.CompletedAt,
                item.Error)).ToArray());
}

public sealed record JiraAuthoringRunResponse(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);
