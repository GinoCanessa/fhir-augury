using FhirAugury.Processing.Jira.Common.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Configuration;

public static class ApplierJiraProcessingDefaults
{
    public const string AgentCliCommand =
        "copilot run ticket-apply --ticket {ticketKey} --db {dbPath} --worktree {worktreePath} --planner-db {plannerDbPath} --repo {repoOwner}/{repoName}";
    public const string JiraSourceAddress = "http://localhost:5160";
    public const string OrchestratorAddress = "http://localhost:5150";

    public static void Apply(JiraProcessingOptions options)
    {
        options.TicketStatusesToProcess ??= ["Resolved - change required"];
        options.ProjectsToInclude ??= null;
        options.WorkGroupsToInclude ??= null;
        options.TicketTypesToProcess ??= ["Change Request", "Technical Correction"];
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

        foreach (string token in RequiredTokens)
        {
            if (!options.AgentCliCommand.Contains(token, StringComparison.Ordinal))
            {
                yield return $"Processing:Jira:AgentCliCommand must include the {token} token.";
            }
        }
    }

    private static readonly string[] RequiredTokens =
    [
        "{ticketKey}",
        "{dbPath}",
        "{worktreePath}",
        "{plannerDbPath}",
        "{repoOwner}",
        "{repoName}",
    ];
}
