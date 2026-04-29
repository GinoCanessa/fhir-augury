using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database.Records;

[LdgSQLiteTable("planned_ticket_repo_changes")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(TicketRepoId))]
[LdgSQLiteIndex(nameof(RepoKey))]
[LdgSQLiteIndex(nameof(FilePath))]
public partial record class PlannedTicketRepoChangeRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public int ChangeSequence { get; set; }
    public required string FilePath { get; set; }
    public string ChangeTitle { get; set; } = string.Empty;
    public string ChangeDescription { get; set; } = string.Empty;
    public int? SourceLineStart { get; set; }
    public int? SourceLineEnd { get; set; }
    public string ReplacementLines { get; set; } = "[]";
    public string Reason { get; set; } = string.Empty;
}
