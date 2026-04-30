using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;

/// <summary>
/// Mirrors <c>PlannedTicketRepoChangeRecord</c> as actually applied for the
/// (ticket, repo). Captures the planner change identity plus the post-apply outcome
/// per change so reviewers can correlate planned vs applied edits.
/// </summary>
[LdgSQLiteTable("applied_ticket_repo_changes")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(TicketRepoId))]
[LdgSQLiteIndex(nameof(RepoKey))]
[LdgSQLiteIndex(nameof(FilePath))]
[LdgSQLiteIndex(nameof(PlannedChangeId))]
public partial record class AppliedTicketRepoChangeRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public required string PlannedChangeId { get; set; }
    public int ChangeSequence { get; set; }
    public required string FilePath { get; set; }
    public string ChangeTitle { get; set; } = string.Empty;
    public string ApplyOutcome { get; set; } = string.Empty;
    public string? ApplyErrorSummary { get; set; }
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
}
