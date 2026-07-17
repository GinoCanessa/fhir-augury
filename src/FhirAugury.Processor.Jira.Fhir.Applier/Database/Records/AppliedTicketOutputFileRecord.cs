using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;

/// <summary>
/// One row per file copied into the per-(ticket, repo) review subtree under
/// <c>OutputDirectory</c>.
/// </summary>
[LdgSQLiteTable("applied_ticket_output_files")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(RepoKey))]
[LdgSQLiteIndex(nameof(RelativePath))]
[LdgSQLiteIndex(nameof(DiffSummary))]
public partial record class AppliedTicketOutputFileRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string RepoKey { get; set; }
    public required string RelativePath { get; set; }
    public long ByteSize { get; set; }
    public required string Sha256 { get; set; }

    /// <summary>One of <c>added</c> / <c>removed</c> / <c>modified</c>.</summary>
    public required string DiffSummary { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}
