using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Index table with per-user in-person request counts. See <see cref="JiraIndexPriorityRecord"/> for the SourceTable convention.</summary>
[LdgSQLiteTable("jira_index_inpersons")]
[LdgSQLiteIndex(nameof(SourceTable), nameof(Name))]
public partial record class JiraIndexInPersonRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Name { get; set; }

    public string SourceTable { get; set; } = "all";

    public required int IssueCount { get; set; }
}
