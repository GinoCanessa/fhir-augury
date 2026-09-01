namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// Neutral row shapes spoken by the shared
/// <see cref="OrchestratorHydrationFetcher"/> and the
/// <see cref="IHydrationTargetDatabase.SaveHydrationAsync"/> boundary.
/// Each concrete database implementation maps these into its own
/// table-bound record shape before issuing INSERTs. The field shapes
/// are intentionally identical to the existing preparer
/// <c>Prepared*Row</c> records so the 1:1 mapping is mechanical.
/// </summary>
public sealed record HydrationTicketRow(
    string TicketKey,
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

public sealed record HydrationJiraRow(
    string TicketKey,
    string JiraKey,
    string? Title,
    string? Status,
    string? Type,
    string? Priority,
    string? Resolution,
    string? ResolutionDescriptionPlain,
    string? WorkGroup,
    string? Specification,
    DateTimeOffset? UpdatedAt,
    string? Url,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record HydrationZulipRow(
    string TicketKey,
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

public sealed record HydrationGitHubRow(
    string TicketKey,
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

public sealed record HydrationRepoRow(
    string TicketKey,
    string Repo,
    string? Description,
    string? WorkGroup,
    string? Specification,
    string? CategoryDetail,
    string? Url,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record HydrationJiraXrefRow(
    string TicketKey,
    string JiraKey,
    string Source);

public sealed record HydrationBatch(
    string TicketKey,
    HydrationTicketRow Parent,
    IReadOnlyList<HydrationJiraRow> JiraRows,
    IReadOnlyList<HydrationZulipRow> ZulipRows,
    IReadOnlyList<HydrationGitHubRow> GitHubRows,
    IReadOnlyList<HydrationRepoRow> RepoRows,
    IReadOnlyList<HydrationJiraXrefRow> JiraXrefRows);
