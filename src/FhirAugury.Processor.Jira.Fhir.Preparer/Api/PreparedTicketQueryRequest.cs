using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Api;

public sealed class PreparedTicketQueryRequest
{
    public string? Recommendation { get; set; }
    public string? Impact { get; set; }
    public string? Repo { get; set; }
    public string? RepoCategory { get; set; }
    public string? RelatedJiraKey { get; set; }
    public string? GitHubItemId { get; set; }
    public string? ZulipThreadId { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }

    public PreparedTicketQueryFilter ToFilter() => new(
        Recommendation,
        Impact,
        Repo,
        RepoCategory,
        RelatedJiraKey,
        GitHubItemId,
        ZulipThreadId,
        Limit,
        Offset);
}
