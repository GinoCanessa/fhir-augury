using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Api;

public sealed record PreparedTicketListResponse(IReadOnlyList<PreparedTicketSummaryDto> Items, int Limit, int Offset);
public sealed record PreparedTicketSummaryDto(string Key, string RequestSummary, string ProposalAImpact, string ProposalBImpact, string Recommendation, string RecommendationJustification, DateTimeOffset SavedAt);
public sealed record PreparedTicketDetailDto(PreparedTicketSummaryDto Ticket, PreparedTicketRelatedItemsDto RelatedItems);
public sealed record PreparedTicketRelatedItemsDto(IReadOnlyList<PreparedTicketRepoDto> Repos, IReadOnlyList<PreparedTicketRelatedJiraDto> JiraTickets, IReadOnlyList<PreparedTicketRelatedZulipDto> ZulipThreads, IReadOnlyList<PreparedTicketRelatedGitHubDto> GitHubItems);
public sealed record PreparedTicketRepoDto(string Repo, string RepoCategory, string Justification);
public sealed record PreparedTicketRelatedJiraDto(string AssociatedTicketKey, string LinkType, string Justification);
public sealed record PreparedTicketRelatedZulipDto(string ZulipThreadId, string Justification);
public sealed record PreparedTicketRelatedGitHubDto(string GitHubItemId, string Justification);

public static class PreparedTicketDtoMapper
{
    public static PreparedTicketSummaryDto ToDto(PreparedTicketSummary summary) => new(summary.Key, summary.RequestSummary, summary.ProposalAImpact, summary.ProposalBImpact, summary.Recommendation, summary.RecommendationJustification, summary.SavedAt);

    public static PreparedTicketDetailDto ToDto(PreparedTicketDetail detail) => new(ToDto(detail.Ticket), ToDto(detail.RelatedItems));

    public static PreparedTicketRelatedItemsDto ToDto(PreparedTicketRelatedItems related) => new(
        related.Repos.Select(item => new PreparedTicketRepoDto(item.Repo, item.RepoCategory, item.Justification)).ToArray(),
        related.JiraTickets.Select(item => new PreparedTicketRelatedJiraDto(item.AssociatedTicketKey, item.LinkType, item.Justification)).ToArray(),
        related.ZulipThreads.Select(item => new PreparedTicketRelatedZulipDto(item.ZulipThreadId, item.Justification)).ToArray(),
        related.GitHubItems.Select(item => new PreparedTicketRelatedGitHubDto(item.GitHubItemId, item.Justification)).ToArray());
}
