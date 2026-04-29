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
