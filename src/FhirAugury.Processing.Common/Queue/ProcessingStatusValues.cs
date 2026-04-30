namespace FhirAugury.Processing.Common.Queue;

/// <summary>
/// String values persisted in ProcessingStatus. Null means pending.
/// </summary>
public static class ProcessingStatusValues
{
    public const string InProgress = "in-progress";
    public const string Complete = "complete";
    public const string Error = "error";

    /// <summary>
    /// Marks a previously-completed work item whose upstream input has changed and now
    /// needs to be re-processed. Stale rows are eligible for claiming alongside null
    /// (never-processed) rows.
    /// </summary>
    public const string Stale = "stale";
}
