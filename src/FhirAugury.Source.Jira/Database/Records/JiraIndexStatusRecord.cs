using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_index_statuses")]
public partial record class JiraIndexStatusRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    public required int IssueCount { get; set; }
}
