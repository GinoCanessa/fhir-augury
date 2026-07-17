using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Roll-up of labels across the four Jira issue-shape tables. See <see cref="JiraIndexPriorityRecord"/> for the SourceTable convention.</summary>
[LdgSQLiteTable("jira_index_labels")]
[LdgSQLiteIndex(nameof(SourceTable), nameof(Name))]
public partial record class JiraIndexLabelRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Name { get; set; }

    public string SourceTable { get; set; } = "all";

    public required int IssueCount { get; set; }
}
