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
}

public sealed class AuthoringConflictException(
    AuthoringConflictCode code,
    string message)
    : InvalidOperationException(message)
{
    public AuthoringConflictCode Code { get; } = code;
}
