using FhirAugury.Processing.Jira.Common.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Configuration;

public static class PlannerJiraProcessingDefaults
{
    public const string AgentCliCommand = "copilot run ticket-plan --ticket {ticketKey} --db {dbPath} --repos {repoFilters}";
    public const string AuthoringAgentCliCommand =
        "copilot run ticket-plan --ticket {ticketKey} --repos {repoFilters}";
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

    public static IEnumerable<string> ValidateAuthoringCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) ||
            !command.Contains("{ticketKey}", StringComparison.Ordinal) ||
            !command.Contains("{repoFilters}", StringComparison.Ordinal))
        {
            yield return "The staged authoring command must include the {ticketKey} and {repoFilters} tokens.";
        }
        if (command.Contains("{dbPath}", StringComparison.Ordinal) ||
            command.Contains("{operationToken}", StringComparison.Ordinal))
        {
            yield return "The staged authoring command must use callback context from the worker environment.";
        }
    }
}
