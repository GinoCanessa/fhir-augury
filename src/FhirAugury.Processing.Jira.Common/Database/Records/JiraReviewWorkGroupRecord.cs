using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Jira.Common.Database.Records;

[LdgSQLiteTable("jira_review_workgroups")]
public partial record class JiraReviewWorkGroupRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Code { get; set; }

    public required string Name { get; set; }
    public required string NameClean { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
