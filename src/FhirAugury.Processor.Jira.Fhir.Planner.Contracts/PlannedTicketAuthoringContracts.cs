using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;

public sealed record PlannedTicketAuthoringRunRequest(
    IReadOnlyList<string> TicketKeys,
    bool DatabaseOnly = false);

public sealed record PlannedTicketAuthoringResultRequest(
    AuthoringResultSubmission Submission,
    PlannedTicketPayload Payload);

public sealed record PlannedTicketAuthoringRunResponse(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);

public sealed record PlannedTicketGroupingStageContext(
    string RunId,
    string StageId,
    string StageLeaseId,
    string InputFingerprint);
