namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

public sealed class PreparedTicketPayload
{
    public required string Key { get; set; }
    public required string RequestSummary { get; set; }
    public string CommentSummary { get; set; } = string.Empty;
    public string LinkedTicketSummary { get; set; } = string.Empty;
    public string RelatedTicketSummary { get; set; } = string.Empty;
    public string RelatedZulipSummary { get; set; } = string.Empty;
    public string RelatedGitHubSummary { get; set; } = string.Empty;
    public string ExistingProposed { get; set; } = string.Empty;
    public required string ProposalA { get; set; }
    public string ProposalAJustification { get; set; } = string.Empty;
    public required string ProposalAImpact { get; set; }
    public required string ProposalB { get; set; }
    public string ProposalBJustification { get; set; } = string.Empty;
    public required string ProposalBImpact { get; set; }
    public required string ProposalC { get; set; }
    public string ProposalCJustification { get; set; } = string.Empty;
    public required string Recommendation { get; set; }
    public required string RecommendationJustification { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public List<PreparedTicketRepoPayload> Repos { get; set; } = [];
    public List<PreparedTicketRelatedJiraPayload> RelatedJiraTickets { get; set; } = [];
    public List<PreparedTicketRelatedZulipPayload> RelatedZulipThreads { get; set; } = [];
    public List<PreparedTicketRelatedGitHubPayload> RelatedGitHubItems { get; set; } = [];
}

public sealed class PreparedTicketRepoPayload
{
    public required string Repo { get; set; }
    public string RepoCategory { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
}

public sealed class PreparedTicketRelatedJiraPayload
{
    public required string AssociatedTicketKey { get; set; }
    public required string LinkType { get; set; }
    public string Justification { get; set; } = string.Empty;
}

public sealed class PreparedTicketRelatedZulipPayload
{
    public required string ZulipThreadId { get; set; }
    public string Justification { get; set; } = string.Empty;
}

public sealed class PreparedTicketRelatedGitHubPayload
{
    public required string GitHubItemId { get; set; }
    public string Justification { get; set; } = string.Empty;
}
