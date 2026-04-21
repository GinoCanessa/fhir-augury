using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_index_workgroups")]
public partial record class JiraIndexWorkGroupRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    /// <summary>
    /// Optional FK into <c>hl7_workgroups.Id</c> resolved by the index
    /// builder (Name first, then NameClean fallback). Null when no canonical
    /// HL7 work group could be matched.
    /// </summary>
    public int? WorkGroupId { get; set; }

    public required int IssueCount { get; set; }

    public required int IssueCountSubmitted { get; set; }
    public required int IssueCountTriaged { get; set; }
    public required int IssueCountWaitingForInput { get; set; }
    public required int IssueCountNoChange { get; set; }
    public required int IssueCountChangeRequired { get; set; }
    public required int IssueCountPublished { get; set; }
    public required int IssueCountApplied { get; set; }
    public required int IssueCountDuplicate { get; set; }
    public required int IssueCountClosed { get; set; }
    public required int IssueCountBalloted { get; set; }
    public required int IssueCountWithdrawn { get; set; }
    public required int IssueCountDeferred { get; set; }
    public required int IssueCountOther { get; set; }
}
