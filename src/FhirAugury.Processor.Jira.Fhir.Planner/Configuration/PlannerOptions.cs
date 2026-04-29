namespace FhirAugury.Processor.Jira.Fhir.Planner.Configuration;

public sealed class PlannerOptions
{
    public const string SectionName = "Processing:Planner";

    public List<string>? RepoFilters { get; set; }
}
