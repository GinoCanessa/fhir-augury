namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

public sealed record PreparedTicketSaveResult(string Key, int PreparedTicketRows, int RepoRows, int RelatedJiraRows, int RelatedZulipRows, int RelatedGitHubRows);
public sealed record PreparedTicketQueryFilter(string? Recommendation = null, string? Impact = null, string? Repo = null, string? RepoCategory = null, string? RelatedJiraKey = null, string? GitHubItemId = null, string? ZulipThreadId = null, int Limit = 50, int Offset = 0);
public sealed record PreparedTicketSummary(string Key, string RequestSummary, string ProposalAImpact, string ProposalBImpact, string Recommendation, string RecommendationJustification, DateTimeOffset SavedAt);
public sealed record PreparedTicketDetail(PreparedTicketSummary Ticket, PreparedTicketRelatedItems RelatedItems);
public sealed record PreparedTicketRelatedItems(IReadOnlyList<PreparedTicketRepoItem> Repos, IReadOnlyList<PreparedTicketRelatedJiraItem> JiraTickets, IReadOnlyList<PreparedTicketRelatedZulipItem> ZulipThreads, IReadOnlyList<PreparedTicketRelatedGitHubItem> GitHubItems);
public sealed record PreparedTicketRepoItem(string Repo, string RepoCategory, string Justification);
public sealed record PreparedTicketRelatedJiraItem(string AssociatedTicketKey, string LinkType, string Justification);
public sealed record PreparedTicketRelatedZulipItem(string ZulipThreadId, string Justification);
public sealed record PreparedTicketRelatedGitHubItem(string GitHubItemId, string Justification);

public sealed record PreparedTicketHydrationRow(
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

public sealed record PreparedJiraHydrationRow(
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

public sealed record PreparedZulipHydrationRow(
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

public sealed record PreparedGitHubHydrationRow(
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

public sealed record PreparedRepoHydrationRow(
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

public sealed record PreparedTicketJiraXrefRow(
    string TicketKey,
    string JiraKey,
    string Source);

public sealed record PreparedTicketHydrationBatch(
    string TicketKey,
    PreparedTicketHydrationRow Parent,
    IReadOnlyList<PreparedJiraHydrationRow> JiraRows,
    IReadOnlyList<PreparedZulipHydrationRow> ZulipRows,
    IReadOnlyList<PreparedGitHubHydrationRow> GitHubRows,
    IReadOnlyList<PreparedRepoHydrationRow> RepoRows,
    IReadOnlyList<PreparedTicketJiraXrefRow> JiraXrefRows);

public sealed record PreparedTicketHydrationReadModel(
    PreparedTicketHydrationRow? Parent,
    IReadOnlyList<PreparedJiraHydrationRow> JiraRows,
    IReadOnlyList<PreparedZulipHydrationRow> ZulipRows,
    IReadOnlyList<PreparedGitHubHydrationRow> GitHubRows,
    IReadOnlyList<PreparedRepoHydrationRow> RepoRows,
    IReadOnlyList<PreparedTicketJiraXrefRow> JiraXrefRows);
