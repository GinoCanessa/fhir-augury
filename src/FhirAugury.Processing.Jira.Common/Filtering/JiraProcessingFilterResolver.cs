using FhirAugury.Processing.Jira.Common.Configuration;

namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed class JiraProcessingFilterResolver(JiraProcessingFilterDefaults? defaults = null)
{
    private readonly JiraProcessingFilterDefaults _defaults = defaults ?? JiraProcessingFilterDefaults.None;

    public ResolvedJiraProcessingFilters Resolve(JiraProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ResolvedJiraProcessingFilters
        {
            TicketStatuses = ResolveList(options.TicketStatusesToProcess, _defaults.TicketStatusesToProcess),
            Projects = ResolveList(options.ProjectsToInclude, _defaults.ProjectsToInclude),
            WorkGroups = ResolveList(options.WorkGroupsToInclude, _defaults.WorkGroupsToInclude),
            TicketTypes = ResolveList(options.TicketTypesToProcess, _defaults.TicketTypesToProcess),
            SourceTicketShape = string.IsNullOrWhiteSpace(options.SourceTicketShape) ? "fhir" : options.SourceTicketShape,
        };
    }

    private static IReadOnlyList<string>? ResolveList(IReadOnlyList<string>? configured, IReadOnlyList<string>? defaultValues)
    {
        if (configured is null)
        {
            return CopyOrNull(defaultValues);
        }

        if (configured.Count == 0)
        {
            return null;
        }

        return configured.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
    }

    private static IReadOnlyList<string>? CopyOrNull(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return values.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
    }
}
