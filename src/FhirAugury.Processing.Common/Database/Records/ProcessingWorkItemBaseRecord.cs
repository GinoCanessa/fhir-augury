using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

/// <summary>
/// Shared processing columns inherited by concrete work-item records.
/// </summary>
[LdgSQLiteBaseClass]
[LdgSQLiteIndex(nameof(ProcessingStatus))]
[LdgSQLiteIndex(nameof(StartedProcessingAt))]
[LdgSQLiteIndex(nameof(CompletedProcessingAt))]
[LdgSQLiteIndex(nameof(LastProcessingAttemptAt))]
[LdgSQLiteIndex(nameof(CompletionId))]
public partial record class ProcessingWorkItemBaseRecord
{
    public DateTimeOffset? StartedProcessingAt { get; set; }
    public DateTimeOffset? CompletedProcessingAt { get; set; }
    public DateTimeOffset? LastProcessingAttemptAt { get; set; }
    public string? ProcessingStatus { get; set; }
    public string? ProcessingError { get; set; }
    public int ProcessingAttemptCount { get; set; }

    /// <summary>
    /// GUID stamped on each terminal completion of this work item. Cleared whenever the
    /// row re-enters pending / in-progress / error / stale state. Downstream processors
    /// observe a change in <see cref="CompletionId"/> to detect that an upstream item
    /// they previously consumed has been re-processed.
    /// </summary>
    public string? CompletionId { get; set; }
}
