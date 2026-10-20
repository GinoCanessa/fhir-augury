using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

public sealed record PreparedTicketAuthoringRunRequest(
    IReadOnlyList<string> TicketKeys,
    bool DatabaseOnly = false);

public sealed record PreparedTicketAuthoringResultRequest(
    AuthoringResultSubmission Submission,
    PreparedTicketPayload Payload);

public sealed record PreparedTicketAuthoringRunResponse(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);

public sealed record PreparedTicketGroupingStageContext(
    string RunId,
    string StageId,
    string StageLeaseId,
    string InputFingerprint,
    string? PartitionKey = null,
    string? OverlayCorpusFingerprint = null,
    IReadOnlyList<string>? RevisedTicketKeys = null,
    IReadOnlyList<string>? TicketKeys = null)
{
    public bool ClaimsReconciliationContext()
        => PartitionKey is not null ||
           OverlayCorpusFingerprint is not null ||
           RevisedTicketKeys is not null ||
           TicketKeys is not null;

    public bool HasCompleteReconciliationContext()
        => !string.IsNullOrWhiteSpace(PartitionKey) &&
           !string.IsNullOrWhiteSpace(OverlayCorpusFingerprint) &&
           RevisedTicketKeys is not null &&
           TicketKeys is not null;
}
