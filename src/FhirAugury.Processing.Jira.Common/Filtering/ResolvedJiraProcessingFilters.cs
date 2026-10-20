namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed record ResolvedJiraProcessingFilters
{
    public IReadOnlyList<string>? TicketStatuses { get; init; }
    public IReadOnlyList<string>? Projects { get; init; }
    public IReadOnlyList<string>? Specifications { get; init; }
    public IReadOnlyList<string>? WorkGroups { get; init; }
    public IReadOnlyList<string>? TicketTypes { get; init; }
    public IReadOnlyList<string>? LabelsToInclude { get; init; }
    public IReadOnlyList<string>? LabelsToExclude { get; init; }
    public string SourceTicketShape { get; init; } = "fhir";

    public bool HasLabelTextFilters =>
        LabelsToInclude?.Any(static value => !string.IsNullOrWhiteSpace(value)) == true ||
        LabelsToExclude?.Any(static value => !string.IsNullOrWhiteSpace(value)) == true;
}
