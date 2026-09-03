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
