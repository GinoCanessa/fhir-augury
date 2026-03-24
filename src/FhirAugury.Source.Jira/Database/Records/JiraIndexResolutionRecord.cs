using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_index_resolutions")]
public partial record class JiraIndexResolutionRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    public required int IssueCount { get; set; }
}
