using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;

/// <summary>
/// One row per terminal apply attempt for a ticket. Durable per-ticket history;
/// re-applying the same ticket replaces the prior row.
/// </summary>
[LdgSQLiteTable("applied_tickets")]
[LdgSQLiteIndex(nameof(AppliedAt))]
public partial record class AppliedTicketRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [LdgSQLiteUnique]
    public required string Key { get; set; }

    public required string PlannerCompletionId { get; set; }
    public required string ApplyCompletionId { get; set; }
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Aggregated terminal outcome: <c>Success</c> or <c>Failed</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    public string? ErrorSummary { get; set; }
}
