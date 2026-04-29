namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed record JiraProcessingFilterDefaults
{
    public IReadOnlyList<string>? TicketStatusesToProcess { get; init; }
    public IReadOnlyList<string>? ProjectsToInclude { get; init; }
    public IReadOnlyList<string>? WorkGroupsToInclude { get; init; }
    public IReadOnlyList<string>? TicketTypesToProcess { get; init; }

    public static JiraProcessingFilterDefaults None { get; } = new();
}
