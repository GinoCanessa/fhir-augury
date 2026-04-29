namespace FhirAugury.Processing.Common.Queue;

/// <summary>
/// String values persisted in ProcessingStatus. Null means pending.
/// </summary>
public static class ProcessingStatusValues
{
    public const string InProgress = "in-progress";
    public const string Complete = "complete";
    public const string Error = "error";
}
