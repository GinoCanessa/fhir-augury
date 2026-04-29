namespace FhirAugury.Processing.Jira.Common.Configuration;

public class JiraProcessingOptions
{
    public const string SectionName = "Processing:Jira";

    public List<string>? TicketStatusesToProcess { get; set; }
    public List<string>? ProjectsToInclude { get; set; }
    public List<string>? WorkGroupsToInclude { get; set; }
    public List<string>? TicketTypesToProcess { get; set; }
    public string AgentCliCommand { get; set; } = string.Empty;
    public string JiraSourceAddress { get; set; } = string.Empty;
    public string? OrchestratorAddress { get; set; }
    public JiraTicketDiscoverySource DiscoverySource { get; set; } = JiraTicketDiscoverySource.DirectJiraSource;
    public string SourceTicketShape { get; set; } = "fhir";
    public bool MarkUpstreamProcessedOnSuccess { get; set; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(AgentCliCommand))
        {
            yield return "Processing:Jira:AgentCliCommand must be non-empty.";
        }
        else if (!AgentCliCommand.Contains("{ticketKey}", StringComparison.Ordinal))
        {
            yield return "Processing:Jira:AgentCliCommand must include the {ticketKey} token.";
        }

        if (!string.Equals(SourceTicketShape, "fhir", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Processing:Jira:SourceTicketShape supports only 'fhir' in v1.";
        }

        if (DiscoverySource == JiraTicketDiscoverySource.DirectJiraSource && string.IsNullOrWhiteSpace(JiraSourceAddress))
        {
            yield return "Processing:Jira:JiraSourceAddress is required when DiscoverySource is DirectJiraSource.";
        }

        if (DiscoverySource == JiraTicketDiscoverySource.Orchestrator && string.IsNullOrWhiteSpace(OrchestratorAddress))
        {
            yield return "Processing:Jira:OrchestratorAddress is required when DiscoverySource is Orchestrator.";
        }
    }
}
