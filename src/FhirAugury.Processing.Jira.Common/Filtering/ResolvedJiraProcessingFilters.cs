namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed record ResolvedJiraProcessingFilters
{
    public IReadOnlyList<string>? TicketStatuses { get; init; }
    public IReadOnlyList<string>? Projects { get; init; }
    public IReadOnlyList<string>? WorkGroups { get; init; }
    public IReadOnlyList<string>? TicketTypes { get; init; }
    public string SourceTicketShape { get; init; } = "fhir";
}
