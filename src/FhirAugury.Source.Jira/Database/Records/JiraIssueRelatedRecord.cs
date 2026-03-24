using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_issue_related")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(RelatedIssueKey))]
[LdgSQLiteIndex(nameof(IssueKey), nameof(RelatedIssueKey))]
public partial record class JiraIssueRelatedRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required int IssueId { get; set; }
    public required string IssueKey { get; set; }
    public required string RelatedIssueKey { get; set; }
}
