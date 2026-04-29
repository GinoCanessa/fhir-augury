namespace FhirAugury.Processing.Jira.Common.Agent;

public sealed record JiraAgentCommandContext
{
    public required string TicketKey { get; init; }
    public required string SourceTicketId { get; init; }
    public required string DatabasePath { get; init; }
    public string SourceTicketShape { get; init; } = "fhir";
    public IReadOnlyDictionary<string, string> ExtensionTokens { get; init; } = new Dictionary<string, string>();
}
