using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Junction table linking issues to users requesting in-person discussion.</summary>
[LdgSQLiteTable("jira_issue_inpersons")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(UserId))]
public partial record class JiraIssueInPersonRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string IssueKey { get; set; }
    public required int UserId { get; set; }
}
