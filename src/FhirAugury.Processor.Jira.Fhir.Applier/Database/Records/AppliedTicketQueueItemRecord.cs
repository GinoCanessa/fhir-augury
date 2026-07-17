using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Processing.Common.Database.Records;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;

/// <summary>
/// Per-ticket work item the applier queue runner processes. Mirrors the columns from
/// <see cref="ProcessingWorkItemBaseRecord"/> directly (cross-project
/// <c>[LdgSQLiteBaseClass]</c> is silently ignored by the generator) plus the
/// applier-specific planner-completion bookkeeping needed to detect superseded plans.
/// </summary>
[LdgSQLiteTable("applied_ticket_queue_items")]
[LdgSQLiteIndex(nameof(Key))]
[LdgSQLiteIndex(nameof(SourceTicketShape))]
[LdgSQLiteIndex(nameof(PlannerCompletionId))]
[LdgSQLiteIndex(nameof(ProcessingStatus))]
[LdgSQLiteIndex(nameof(StartedProcessingAt))]
[LdgSQLiteIndex(nameof(CompletedProcessingAt))]
[LdgSQLiteIndex(nameof(LastProcessingAttemptAt))]
[LdgSQLiteIndex(nameof(CompletionId))]
public partial record class AppliedTicketQueueItemRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string Key { get; set; }
    public string SourceTicketShape { get; set; } = "fhir";

    /// <summary>
    /// The planner-side <c>CompletionId</c> observed at the moment this row was last
    /// updated by <c>PlannerWorkQueue</c>. The applier compares this against the live
    /// planner row to detect when a plan has been superseded.
    /// </summary>
    public string? PlannerCompletionId { get; set; }

    public DateTimeOffset? PlannerCompletedAt { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSyncedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedProcessingAt { get; set; }
    public DateTimeOffset? CompletedProcessingAt { get; set; }
    public DateTimeOffset? LastProcessingAttemptAt { get; set; }
    public string? ProcessingStatus { get; set; }
    public string? ProcessingError { get; set; }
    public int ProcessingAttemptCount { get; set; }
    public string? CompletionId { get; set; }

    /// <summary>
    /// Aggregated outcome from the most recent terminal apply attempt
    /// (<c>Success</c> / <c>Failed</c>). Null when the row has never completed.
    /// </summary>
    public string? Outcome { get; set; }

    public string? ErrorSummary { get; set; }
}
