using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_issue_labels")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(LabelId))]
public partial record class JiraIssueLabelRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required string IssueKey { get; set; }
    public required int LabelId { get; set; }
}
