namespace FhirAugury.Processor.Jira.Fhir.Applier.Push;

public sealed record AppliedTicketRepoPushDto(
    string RepoKey,
    string PushState,
    string? PushedCommitSha,
    DateTimeOffset? PushedAt,
    string? ErrorMessage);

public sealed record AppliedTicketPushResponse(
    string TicketKey,
    int RepoCount,
    int PushedCount,
    int FailedCount,
    int SkippedCount,
    IReadOnlyList<AppliedTicketRepoPushDto> Repos);
