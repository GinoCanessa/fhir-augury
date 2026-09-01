namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;

// Hydration row shapes (concrete planner mapping of the neutral
// Hydration.Common rows; field shape identical, but the column names
// follow the planner's IssueKey / RepoKey convention).

public sealed record PlannedTicketHydrationRow(
    string IssueKey,
    string? Priority,
    string? Resolution,
    string? ResolutionDescriptionPlain,
    string? Specification,
    string? RaisedInVersion,
    string? SelectedBallot,
    string? ChangeCategory,
    string? Impact,
    string? Labels,
    int? CommentCount,
    string? DescriptionPlain,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record PlannedJiraHydrationRow(
    string IssueKey,
    string JiraKey,
    string? Title,
    string? Status,
    string? Type,
    string? Priority,
    string? Resolution,
    string? ResolutionDescriptionPlain,
    string? WorkGroup,
    string? WorkGroupClean,
    string? Specification,
    DateTimeOffset? UpdatedAt,
    string? Url,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record PlannedZulipHydrationRow(
    string IssueKey,
    string ZulipThreadId,
    int? StreamId,
    string? StreamName,
    string? Topic,
    int? MessageCount,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? LastMessageAt,
    string? FirstMessageExcerpt,
    string? Url,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record PlannedGitHubHydrationRow(
    string IssueKey,
    string GitHubItemId,
    string? Owner,
    string? Repo,
    int? Number,
    string? Path,
    string? Title,
    string? State,
    bool? IsPullRequest,
    string? Labels,
    DateTimeOffset? UpdatedAt,
    string? Url,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record PlannedRepoHydrationRow(
    string IssueKey,
    string RepoKey,
    string? Description,
    string? WorkGroup,
    string? Specification,
    string? CategoryDetail,
    string? Url,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record PlannedTicketJiraXrefRow(string IssueKey, string JiraKey, string Source);

public sealed record PlannedTicketHydrationBatch(
    string IssueKey,
    PlannedTicketHydrationRow Parent,
    IReadOnlyList<PlannedJiraHydrationRow> JiraRows,
    IReadOnlyList<PlannedZulipHydrationRow> ZulipRows,
    IReadOnlyList<PlannedGitHubHydrationRow> GitHubRows,
    IReadOnlyList<PlannedRepoHydrationRow> RepoRows,
    IReadOnlyList<PlannedTicketJiraXrefRow> JiraXrefRows);

// Query models

public sealed record PlannedTicketQueryFilter(
    string? Repo = null,
    string? AffectedFilePath = null,
    string? RelatedJiraKey = null,
    int Limit = 50,
    int Offset = 0);

public sealed record PlannedTicketSummary(
    string Key,
    string Resolution,
    string ResolutionSummary,
    string FeatureProposal,
    string DesignRationale,
    DateTimeOffset SavedAt);

public sealed record PlannedTicketRepoItem(string RepoKey, string? RepoRevision, string Justification);
public sealed record PlannedTicketRepoChangeItem(
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
public sealed record PlannedTicketRepoImpactItem(string TicketRepoId, string RepoKey, string? TicketRepoChangeId, string AffectedFilePath, string HowAffected);
public sealed record PlannedTicketChangeValidationItem(string TicketRepoId, string RepoKey, int ValidationSequence, string Action);
public sealed record PlannedTicketTestingConsiderationItem(string TicketRepoId, string RepoKey, int ConsiderationSequence, string Consideration);
public sealed record PlannedTicketOpenQuestionItem(string TicketRepoId, string RepoKey, int QuestionSequence, string Question);

public sealed record PlannedTicketDetail(
    PlannedTicketSummary Ticket,
    IReadOnlyList<PlannedTicketRepoItem> Repos,
    IReadOnlyList<PlannedTicketRepoChangeItem> RepoChanges,
    IReadOnlyList<PlannedTicketRepoImpactItem> RepoImpacts,
    IReadOnlyList<PlannedTicketChangeValidationItem> ChangeValidations,
    IReadOnlyList<PlannedTicketTestingConsiderationItem> TestingConsiderations,
    IReadOnlyList<PlannedTicketOpenQuestionItem> OpenQuestions);

public sealed record PlannedTicketHydrationReadModel(
    PlannedTicketHydrationRow? Parent,
    IReadOnlyList<PlannedJiraHydrationRow> JiraRows,
    IReadOnlyList<PlannedZulipHydrationRow> ZulipRows,
    IReadOnlyList<PlannedGitHubHydrationRow> GitHubRows,
    IReadOnlyList<PlannedRepoHydrationRow> RepoRows,
    IReadOnlyList<PlannedTicketJiraXrefRow> JiraXrefRows);

// Topic models

public sealed record PlannedTicketTopicGroupMember(string TicketKey, int Order);

public sealed record PlannedTicketTopicGroup(
    string FirstTicketKey,
    string Rationale,
    IReadOnlyList<PlannedTicketTopicGroupMember> Members);

public sealed record PlannedTicketTopicDetail(
    string Id,
    string ShortDescription,
    string LongerDescription,
    int? RenderOrderHint,
    IReadOnlyList<string> SpannedRepos,
    IReadOnlyList<PlannedTicketTopicGroup> LinkedTicketGroups,
    IReadOnlyList<string> RemainingTicketKeys);

public sealed record PlannedTicketTopicsForCategory(
    string WorkGroupClean,
    string WorkGroupDisplay,
    string Specification,
    string Type,
    DateTimeOffset SavedAt,
    IReadOnlyList<PlannedTicketTopicDetail> Topics);
