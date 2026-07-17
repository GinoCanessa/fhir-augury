using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Roll-up of issue Resolution. Sourced from <c>jira_issues.Resolution</c> only (Resolution is FHIR-shape-specific). See <see cref="JiraIndexPriorityRecord"/> for the SourceTable convention.</summary>
[LdgSQLiteTable("jira_index_resolutions")]
[LdgSQLiteIndex(nameof(SourceTable), nameof(Name))]
public partial record class JiraIndexResolutionRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Name { get; set; }

    public string SourceTable { get; set; } = "all";

    public required int IssueCount { get; set; }
}
