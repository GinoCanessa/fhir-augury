using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Index table with per-user in-person request counts.</summary>
[LdgSQLiteTable("jira_index_inpersons")]
public partial record class JiraIndexInPersonRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    public required int IssueCount { get; set; }
}
