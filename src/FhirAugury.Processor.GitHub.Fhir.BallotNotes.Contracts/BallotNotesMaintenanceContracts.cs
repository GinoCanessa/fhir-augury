namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;

public sealed record BallotNoteWorkGroupReallocation(
    string NoteId,
    string ExpectedEvidenceRevision,
    string WorkGroup,
    string WorkGroupCode,
    string WorkGroupNames,
    string WorkGroupCodes);

public sealed record BallotNotesWorkGroupReallocationRequest(
    IReadOnlyList<BallotNoteWorkGroupReallocation> Changes);

public sealed record BallotNotesWorkGroupReallocationResult(
    int UpdatedCount);
