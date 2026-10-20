using FhirAugury.Cli.Models;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class BallotNoteAuthoringHandler
{
    public static async Task<object> HandleAsync(
        BallotNoteAuthoringRequest request,
        string orchestratorAddress,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        using AuthoringHttpClient client =
            new(orchestratorAddress, handler);
        return request.Action.Trim().ToLowerInvariant() switch
        {
            "start" => await client.StartAsync(
                "BallotNotes",
                new BallotNotesAuthoringRunRequest(
                    request.HydrationExecutionId,
                    request.NoteIds,
                    request.DatabaseOnly),
                ct),
            "status" => await client.GetStatusAsync(
                "BallotNotes",
                Required(request.RunId, "runId"),
                ct),
            "retry" => await client.RetryAsync(
                "BallotNotes",
                Required(request.RunId, "runId"),
                Required(request.ItemId, "itemId"),
                ct),
            "supersede" => await client.SupersedeAsync(
                "BallotNotes",
                Required(request.RunId, "runId"),
                Required(request.ItemId, "itemId"),
                Required(request.Reason, "reason"),
                ct),
            "submit" => await SubmitAsync(request, client, ct),
            "snapshot" => await client.DownloadSnapshotAsync(
                "BallotNotes",
                Required(request.RunId, "runId"),
                Required(request.SnapshotPath, "snapshotPath"),
                request.DescriptorPath,
                ct),
            _ => throw new ArgumentException(
                "Action must be start, status, retry, supersede, submit, or snapshot."),
        };
    }

    private static async Task<object> SubmitAsync(
        BallotNoteAuthoringRequest request,
        AuthoringHttpClient client,
        CancellationToken ct)
    {
        BallotNoteProsePutRequest prose = request.Prose
            ?? throw new ArgumentException("prose is required for submit.");
        AuthoringWorkerContext context =
            AuthoringHttpClient.ReadWorkerContext();
        string observedSourceRevision = Required(
            request.ObservedSourceRevision,
            "observedSourceRevision");
        string contentHash =
            AuthoringHttpClient.HashBallotNoteProse(prose);
        return await client.SubmitAsync(
            new BallotNoteAuthoringResultRequest(
                new AuthoringResultSubmission(
                    context.RunId,
                    context.ItemId,
                    context.OperationId,
                    observedSourceRevision,
                    contentHash),
                prose),
            contentHash,
            observedSourceRevision,
            ct);
    }

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.")
            : value;
}
