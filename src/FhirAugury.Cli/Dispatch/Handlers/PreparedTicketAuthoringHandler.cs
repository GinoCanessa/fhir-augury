using FhirAugury.Cli.Models;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class PreparedTicketAuthoringHandler
{
    private const int RefreshReconciliationRunLimit = 20;

    public static async Task<object> HandleAsync(
        PreparedTicketAuthoringRequest request,
        string orchestratorAddress,
        CancellationToken ct,
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null)
    {
        using AuthoringHttpClient client =
            new(orchestratorAddress, handler);
        return request.Action.Trim().ToLowerInvariant() switch
        {
            "start" => await client.StartAsync(
                "Preparer",
                new PreparedTicketAuthoringRunRequest(
                    request.TicketKeys,
                    request.DatabaseOnly),
                ct),
            "status" => await client.GetStatusAsync(
                "Preparer",
                Required(request.RunId, "runId"),
                ct),
            "retry" => await client.RetryAsync(
                "Preparer",
                Required(request.RunId, "runId"),
                Required(request.ItemId, "itemId"),
                ct),
            "supersede" => await client.SupersedeAsync(
                "Preparer",
                Required(request.RunId, "runId"),
                Required(request.ItemId, "itemId"),
                Required(request.Reason, "reason"),
                ct),
            "submit" => await SubmitAsync(request, client, ct),
            "snapshot" => await client.DownloadSnapshotAsync(
                "Preparer",
                Required(request.RunId, "runId"),
                Required(request.SnapshotPath, "snapshotPath"),
                request.DescriptorPath,
                ct),
            "refresh-publication" =>
                await RefreshPublicationAsync(
                    request,
                    client,
                    timeProvider ?? TimeProvider.System,
                    ct),
            _ => throw new ArgumentException(
                "Action must be start, status, retry, supersede, submit, snapshot, or refresh-publication."),
        };
    }

    private static async Task<object> RefreshPublicationAsync(
        PreparedTicketAuthoringRequest request,
        AuthoringHttpClient client,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        string sourceRunId = Required(request.RunId, "runId");
        DateTimeOffset submissionBegan =
            timeProvider.GetUtcNow();
        try
        {
            return await client.StartPublicationRefreshAsync(
                "Preparer",
                sourceRunId,
                ct);
        }
        catch (AuthoringMutationOutcomeUnknownException)
        {
            return await ReconcilePublicationRefreshAsync(
                client,
                sourceRunId,
                submissionBegan,
                ct);
        }
    }

    private static async Task<object>
        ReconcilePublicationRefreshAsync(
            AuthoringHttpClient client,
            string sourceRunId,
            DateTimeOffset submissionBegan,
            CancellationToken ct)
    {
        try
        {
            AuthoringRunListResponse list =
                await client.ListAsync(
                    "Preparer",
                    RefreshReconciliationRunLimit,
                    ct);
            IReadOnlyList<AuthoringRunStatus> candidates =
                Array.AsReadOnly(
                    list.Runs
                        .Where(run =>
                            run.CreatedAt >= submissionBegan &&
                            string.Equals(
                                run.Purpose,
                                PreparedTicketPublicationContract
                                    .PublicationRefreshPurpose,
                                StringComparison.Ordinal) &&
                            string.Equals(
                                run.SourceRunId,
                                sourceRunId,
                                StringComparison.Ordinal))
                        .ToArray());
            string message = candidates.Count switch
            {
                0 =>
                    "The refresh POST was not replayed, and no matching run was found. Review Preparer runs before trying again.",
                1 =>
                    "The refresh POST was not replayed. Select the single matching candidate run before continuing with status, snapshot download, and site generation.",
                _ =>
                    "The refresh POST was not replayed. Multiple matching candidate runs require operator review before continuing.",
            };
            if (list.Truncated)
            {
                message +=
                    " The bounded recent-run response was truncated.";
            }
            return new AuthoringPublicationRefreshReconciliationResponse(
                "outcome-unknown",
                sourceRunId,
                "succeeded",
                candidates,
                list.Truncated,
                message);
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or
                InvalidOperationException or TimeoutException or
                OperationCanceledException)
        {
            return new AuthoringPublicationRefreshReconciliationResponse(
                "outcome-unknown",
                sourceRunId,
                "failed",
                [],
                ListTruncated: false,
                "The refresh POST was not replayed, but read reconciliation failed. Review Preparer runs before trying again.",
                ex.Message);
        }
    }

    private static async Task<object> SubmitAsync(
        PreparedTicketAuthoringRequest request,
        AuthoringHttpClient client,
        CancellationToken ct)
    {
        PreparedTicketPayload payload = request.Payload
            ?? throw new ArgumentException("payload is required for submit.");
        PreparedTicketPayloadValidator.ThrowIfInvalid(payload);
        AuthoringWorkerContext context =
            AuthoringHttpClient.ReadWorkerContext();
        string observedSourceRevision = Required(
            request.ObservedSourceRevision,
            "observedSourceRevision");
        observedSourceRevision =
            AuthoringSourceRevision.CanonicalizeTimestamp(
                observedSourceRevision);
        string contentHash = AuthoringHttpClient.HashWebJson(payload);
        return await client.SubmitAsync(
            new PreparedTicketAuthoringResultRequest(
                new AuthoringResultSubmission(
                    context.RunId,
                    context.ItemId,
                    context.OperationId,
                    observedSourceRevision,
                    contentHash),
                payload),
            contentHash,
            observedSourceRevision,
            ct);
    }

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.")
            : value;
}
