using FhirAugury.Processing.Jira.Common.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Configuration;

public static class PlannerJiraProcessingDefaults
{
    public const string AgentCliCommand = "copilot run ticket-plan --ticket {ticketKey} --db {dbPath} --repos {repoFilters}";
    public const string JiraSourceAddress = "http://localhost:5160";
    public const string OrchestratorAddress = "http://localhost:5150";

    public static void Apply(JiraProcessingOptions options)
    {
        options.TicketStatusesToProcess ??= ["Resolved - change required"];
        options.ProjectsToInclude ??= null;
        options.WorkGroupsToInclude ??= null;
        options.TicketTypesToProcess ??= null;
        if (string.IsNullOrWhiteSpace(options.AgentCliCommand))
        {
            options.AgentCliCommand = AgentCliCommand;
        }

        if (string.IsNullOrWhiteSpace(options.JiraSourceAddress))
        {
            options.JiraSourceAddress = JiraSourceAddress;
        }

        options.OrchestratorAddress ??= OrchestratorAddress;
    }

    public static IEnumerable<string> Validate(JiraProcessingOptions options)
    {
        foreach (string error in options.Validate())
        {
            yield return error;
        }

        if (!options.AgentCliCommand.Contains("{dbPath}", StringComparison.Ordinal))
        {
            yield return "Processing:Jira:AgentCliCommand must include the {dbPath} token.";
        }

        if (!options.AgentCliCommand.Contains("{repoFilters}", StringComparison.Ordinal))
        {
            yield return "Processing:Jira:AgentCliCommand must include the {repoFilters} token.";
        }
    }
}
