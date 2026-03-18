using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>A comment on a Jira issue.</summary>
[LdgSQLiteTable("jira_comments")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(CreatedAt))]
public partial record class JiraCommentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(JiraIssueRecord.Id))]
    public required int IssueId { get; set; }

    public required string IssueKey { get; set; }
    public required string Author { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string Body { get; set; }
}
