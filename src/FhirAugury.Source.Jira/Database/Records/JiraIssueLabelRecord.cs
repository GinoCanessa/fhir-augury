using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_issue_labels")]
[LdgSQLiteIndex(nameof(IssueId))]
[LdgSQLiteIndex(nameof(LabelId))]
public partial record class JiraIssueLabelRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required int IssueId { get; set; }
    public required int LabelId { get; set; }
}
