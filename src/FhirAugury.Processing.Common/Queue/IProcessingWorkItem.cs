namespace FhirAugury.Processing.Common.Queue;

/// <summary>
/// Minimal work-item shape consumed by the common runner.
/// Pending work is represented by <see cref="ProcessingStatus"/> being null; in-flight is
/// <see cref="ProcessingStatusValues.InProgress"/>, complete is <see cref="ProcessingStatusValues.Complete"/>,
/// and failed is <see cref="ProcessingStatusValues.Error"/>.
/// </summary>
public interface IProcessingWorkItem
{
    string Id { get; }
    DateTimeOffset? StartedProcessingAt { get; set; }
    DateTimeOffset? CompletedProcessingAt { get; set; }
    DateTimeOffset? LastProcessingAttemptAt { get; set; }
    string? ProcessingStatus { get; set; }
    string? ProcessingError { get; set; }
    int ProcessingAttemptCount { get; set; }
}
