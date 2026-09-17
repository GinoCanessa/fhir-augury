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
    ReconciliationCancelRequired,
    CanonicalUnpublishedRestriction,
}

public sealed class AuthoringConflictException(
    AuthoringConflictCode code,
    string message,
    IEnumerable<string>? relatedRunIds = null,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public const string CanonicalUnpublishedRestrictionCode =
        "canonical-unpublished-restriction";

    public AuthoringConflictCode Code { get; } = code;

    public IReadOnlyList<string> RelatedRunIds { get; } = Array.AsReadOnly(
        (relatedRunIds ?? [])
        .Where(runId => !string.IsNullOrWhiteSpace(runId))
        .Distinct(StringComparer.Ordinal)
        .ToArray());

    public static bool IsCanonicalUnpublishedRestriction(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is AuthoringConflictException
            {
                Code: AuthoringConflictCode.CanonicalUnpublishedRestriction,
            } ||
            exception.Message.Contains(
                CanonicalUnpublishedRestrictionCode,
                StringComparison.Ordinal);
    }

    public static AuthoringConflictException
        ForCanonicalUnpublishedRestriction(
            string? detail = null,
            IEnumerable<string>? relatedRunIds = null,
            Exception? innerException = null)
        => new(
            AuthoringConflictCode.CanonicalUnpublishedRestriction,
            string.IsNullOrWhiteSpace(detail)
                ? "Snapshot-producing authoring is blocked while a canonical-unpublished reconciliation remains unresolved."
                : detail,
            relatedRunIds,
            innerException);
}

public sealed record AuthoringSnapshotWorkflowIntent(
    string ProcessorKind,
    bool DatabaseOnly,
    string Purpose,
    string? RunId = null);

public interface IAuthoringSnapshotWorkflowGuard
{
    Task EnsureSnapshotWorkflowAllowedAsync(
        AuthoringSnapshotWorkflowIntent intent,
        CancellationToken ct = default);
}

public sealed class AllowAllAuthoringSnapshotWorkflowGuard
    : IAuthoringSnapshotWorkflowGuard
{
    public static AllowAllAuthoringSnapshotWorkflowGuard Instance { get; } =
        new();

    public Task EnsureSnapshotWorkflowAllowedAsync(
        AuthoringSnapshotWorkflowIntent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Task.CompletedTask;
    }
}
