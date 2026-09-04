using FhirAugury.Processing.Jira.Common.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;

public static class PreparerJiraProcessingDefaults
{
    public const string AgentCliCommand =
        "copilot -p '/ticket-prep {ticketKey}' --allow-all";
    public const string AuthoringAgentCliCommand =
        "copilot -p '/ticket-prep {ticketKey}' --allow-all";
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
        if (string.IsNullOrWhiteSpace(options.AuthoringAgentCliCommand))
        {
            options.AuthoringAgentCliCommand = AuthoringAgentCliCommand;
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

        if (options.AgentCliCommand.Contains(
                "{dbPath}",
                StringComparison.OrdinalIgnoreCase) ||
            options.AgentCliCommand.Contains(
                "{operationToken}",
                StringComparison.OrdinalIgnoreCase))
        {
            yield return "Processing:Jira:AgentCliCommand must use callback context from the worker environment.";
        }
        foreach (string error in ValidateAuthoringCommand(
                     options.AuthoringAgentCliCommand))
        {
            yield return error;
        }
    }

    public static IEnumerable<string> ValidateAuthoringCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) ||
            !command.Contains(
                "{ticketKey}",
                StringComparison.OrdinalIgnoreCase))
        {
            yield return "The staged authoring command must include the {ticketKey} token.";
        }
        if (command.Contains(
                "{dbPath}",
                StringComparison.OrdinalIgnoreCase) ||
            command.Contains(
                "{operationToken}",
                StringComparison.OrdinalIgnoreCase))
        {
            yield return "The staged authoring command must use callback context from the worker environment.";
        }
    }
}
