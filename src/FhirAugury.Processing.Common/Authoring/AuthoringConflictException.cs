namespace FhirAugury.Processing.Common.Authoring;

public enum AuthoringConflictCode
{
    AuthoringNotActivated,
    CutoverInProgress,
    MutationFenceUnavailable,
    RunNotActive,
    ItemNotClaimable,
    InvalidOperationToken,
    StaleOperation,
    SourceRevisionMismatch,
    ContentChanged,
    StageFingerprintMismatch,
    StageAlreadyInProgress,
    StageLeaseLost,
    RevisionAlreadyScheduled,
    RevalidationRequired,
    AttemptLimitReached,
    ActiveRunCapacityReached,
}

public sealed class AuthoringConflictException(
    AuthoringConflictCode code,
    string message)
    : InvalidOperationException(message)
{
    public AuthoringConflictCode Code { get; } = code;
}
