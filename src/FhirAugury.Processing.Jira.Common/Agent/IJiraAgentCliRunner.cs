namespace FhirAugury.Processing.Jira.Common.Agent;

public interface IJiraAgentCliRunner
{
    Task<JiraAgentResult> RunAsync(JiraAgentCommand command, JiraAgentCommandContext context, CancellationToken ct);
}
