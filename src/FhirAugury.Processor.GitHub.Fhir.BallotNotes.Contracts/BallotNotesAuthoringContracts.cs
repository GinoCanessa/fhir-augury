using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;

public sealed record BallotNotesAuthoringRunRequest(
    string HydrationExecutionId,
    IReadOnlyList<string>? NoteIds = null,
    bool DatabaseOnly = false);

public sealed record BallotNoteAuthoringResultRequest(
    AuthoringResultSubmission Submission,
    BallotNoteProsePutRequest Prose);

public sealed record BallotNotesAuthoringRunResponse(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);
