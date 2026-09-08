using FhirAugury.Cli.Models;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class PlannedTicketAuthoringHandler
{
    public static async Task<object> HandleAsync(
        PlannedTicketAuthoringRequest request,
        string orchestratorAddress,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        using AuthoringHttpClient client =
            new(orchestratorAddress, handler);
        return request.Action.Trim().ToLowerInvariant() switch
        {
            "start" => await client.StartAsync(
                "Planner",
                new PlannedTicketAuthoringRunRequest(
                    request.TicketKeys,
                    request.DatabaseOnly),
                ct),
            "status" => await client.GetStatusAsync(
                "Planner",
                Required(request.RunId, "runId"),
                ct),
            "retry" => await client.RetryAsync(
                "Planner",
                Required(request.RunId, "runId"),
                Required(request.ItemId, "itemId"),
                ct),
            "supersede" => await client.SupersedeAsync(
                "Planner",
                Required(request.RunId, "runId"),
                Required(request.ItemId, "itemId"),
                Required(request.Reason, "reason"),
                ct),
            "submit" => await SubmitAsync(request, client, ct),
            "snapshot" => await client.DownloadSnapshotAsync(
                "Planner",
                Required(request.RunId, "runId"),
                Required(request.SnapshotPath, "snapshotPath"),
                request.DescriptorPath,
                ct),
            _ => throw new ArgumentException(
                "Action must be start, status, retry, supersede, submit, or snapshot."),
        };
    }

    private static async Task<object> SubmitAsync(
        PlannedTicketAuthoringRequest request,
        AuthoringHttpClient client,
        CancellationToken ct)
    {
        PlannedTicketPayload payload = request.Payload
            ?? throw new ArgumentException("payload is required for submit.");
        PlannedTicketPayloadValidator.ThrowIfInvalid(payload);
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
            new PlannedTicketAuthoringResultRequest(
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
