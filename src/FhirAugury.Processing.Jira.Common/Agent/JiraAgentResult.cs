namespace FhirAugury.Processing.Jira.Common.Agent;

public sealed record JiraAgentResult(
    int ExitCode,
    string StdoutTail,
    string StderrTail,
    TimeSpan Elapsed,
    bool Canceled);
