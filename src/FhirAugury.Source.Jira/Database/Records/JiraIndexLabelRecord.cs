using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_index_labels")]
public partial record class JiraIndexLabelRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    public required int IssueCount { get; set; }
}
