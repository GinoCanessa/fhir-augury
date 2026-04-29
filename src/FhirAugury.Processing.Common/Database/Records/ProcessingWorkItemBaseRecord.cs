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
public partial record class ProcessingWorkItemBaseRecord
{
    public DateTimeOffset? StartedProcessingAt { get; set; }
    public DateTimeOffset? CompletedProcessingAt { get; set; }
    public DateTimeOffset? LastProcessingAttemptAt { get; set; }
    public string? ProcessingStatus { get; set; }
    public string? ProcessingError { get; set; }
    public int ProcessingAttemptCount { get; set; }
}
