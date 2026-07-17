using FhirAugury.Processing.Jira.Common.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;

public static class PreparerJiraProcessingDefaults
{
    public const string AgentCliCommand = "copilot run ticket-prep --ticket {ticketKey} --db {dbPath}";
    public const string JiraSourceAddress = "http://localhost:5160";
    public const string OrchestratorAddress = "http://localhost:5150";

    /// <summary>
    /// Applies preparer-specific Jira defaults. Null filter lists preserve the common default behavior, [] means no restriction, and non-empty lists restrict values.
    /// </summary>
    public static void Apply(JiraProcessingOptions options)
    {
        options.TicketStatusesToProcess ??= ["Triaged"];
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
    }
}
