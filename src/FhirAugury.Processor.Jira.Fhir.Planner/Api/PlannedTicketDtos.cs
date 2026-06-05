namespace FhirAugury.Processor.Jira.Fhir.Planner.Api;

public sealed record PlannedTicketSummaryDto(
    string Key,
    string Resolution,
    string ResolutionSummary,
    string FeatureProposal,
    string DesignRationale,
    DateTimeOffset SavedAt);

public sealed record PlannedTicketRepoDto(string RepoKey, string? RepoRevision, string Justification);

public sealed record PlannedTicketRepoChangeDto(
    string Id,
    string TicketRepoId,
    string RepoKey,
    int ChangeSequence,
    string FilePath,
    string ChangeTitle,
    string ChangeDescription,
    int? SourceLineStart,
    int? SourceLineEnd,
    IReadOnlyList<string> ReplacementLines,
    string Reason);

public sealed record PlannedTicketRepoImpactDto(
    string TicketRepoId,
    string RepoKey,
    string? TicketRepoChangeId,
    string AffectedFilePath,
    string HowAffected);

public sealed record PlannedTicketChangeValidationDto(string TicketRepoId, string RepoKey, int ValidationSequence, string Action);

public sealed record PlannedTicketTestingConsiderationDto(string TicketRepoId, string RepoKey, int ConsiderationSequence, string Consideration);

public sealed record PlannedTicketOpenQuestionDto(string TicketRepoId, string RepoKey, int QuestionSequence, string Question);

public sealed record PlannedTicketDetailDto(
    PlannedTicketSummaryDto Ticket,
    IReadOnlyList<PlannedTicketRepoDto> Repos,
    IReadOnlyList<PlannedTicketRepoChangeDto> RepoChanges,
    IReadOnlyList<PlannedTicketRepoImpactDto> RepoImpacts,
    IReadOnlyList<PlannedTicketChangeValidationDto> ChangeValidations,
    IReadOnlyList<PlannedTicketTestingConsiderationDto> TestingConsiderations,
    IReadOnlyList<PlannedTicketOpenQuestionDto> OpenQuestions);

public sealed record PlannedTicketListResponse(
    IReadOnlyList<PlannedTicketSummaryDto> Results,
    int Limit,
    int Offset);

public sealed record PlannedTicketRelatedItemsDto(
    IReadOnlyList<PlannedTicketRepoDto> Repos);

public sealed class PlannedTicketQueryRequest
{
    public string? Repo { get; set; }
    public string? AffectedFilePath { get; set; }
    public string? RelatedJiraKey { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }
}
