using FhirAugury.Cli.Models;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class PreparedTicketAuthoringHandler
{
    public static async Task<object> HandleAsync(
        PreparedTicketAuthoringRequest request,
        string orchestratorAddress,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
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
            "submit" => await SubmitAsync(request, client, ct),
            "snapshot" => await client.DownloadSnapshotAsync(
                "Preparer",
                Required(request.RunId, "runId"),
                Required(request.SnapshotPath, "snapshotPath"),
                request.DescriptorPath,
                ct),
            _ => throw new ArgumentException(
                "Action must be start, status, retry, submit, or snapshot."),
        };
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
