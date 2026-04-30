using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;

/// <summary>
/// One row per (ticket, repo) apply attempt. Holds per-repo apply outcome and the
/// branch / commit / push state for downstream consumers.
/// </summary>
[LdgSQLiteTable("applied_ticket_repos")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(RepoKey))]
[LdgSQLiteIndex(nameof(Outcome))]
public partial record class AppliedTicketRepoRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string RepoKey { get; set; }
    public required string BaselineCommitSha { get; set; }
    public string? BranchName { get; set; }
    public string? CommitSha { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? ErrorSummary { get; set; }

    /// <summary>One of <c>NotPushed</c> / <c>Pushed</c> / <c>PushFailed</c>.</summary>
    public string PushState { get; set; } = "NotPushed";

    public DateTimeOffset? PushedAt { get; set; }
    public string? PushedCommitSha { get; set; }
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
}
