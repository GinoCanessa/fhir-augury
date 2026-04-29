namespace FhirAugury.Processing.Jira.Common.Agent;

public sealed record JiraAgentCommand(string FileName, IReadOnlyList<string> Arguments);
