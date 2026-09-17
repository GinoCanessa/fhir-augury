using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

public static class PreparedTicketCanonicalEpochRecoveryContract
{
    public const int CurrentVersion = 1;
    public const string Purpose = "canonical-epoch-recovery";
}

public static class PreparedTicketCanonicalEpochRecoveryStateValues
{
    public const string MaterializationPending = "materialization-pending";
    public const string SnapshotPublishPending = "snapshot-publish-pending";
    public const string Ready = "ready";

    public static bool IsValid(string? state)
        => state is MaterializationPending or SnapshotPublishPending or Ready;
}

public static class PreparedTicketCanonicalEpochRecoveryFailureCodes
{
    public const string InvalidSourceReconciliation =
        "invalid-source-reconciliation";
    public const string SourceNotAbandoned = "source-not-abandoned";
    public const string CanonicalEpochAlreadyRecovered =
        "canonical-epoch-already-recovered";
    public const string StaleCanonicalEpoch = "stale-canonical-epoch";
    public const string CanonicalStateChanged = "canonical-state-changed";
    public const string RecoveryInProgress = "recovery-in-progress";
    public const string SnapshotRecoveryFailure =
        "snapshot-recovery-failure";
    public const string RecoveryEvidenceConflict =
        "recovery-evidence-conflict";
    public const string RunNotRetryable = "run-not-retryable";

    public static bool IsKnown(string? code)
        => code is
            InvalidSourceReconciliation or
            SourceNotAbandoned or
            CanonicalEpochAlreadyRecovered or
            StaleCanonicalEpoch or
            CanonicalStateChanged or
            RecoveryInProgress or
            SnapshotRecoveryFailure or
            RecoveryEvidenceConflict or
            RunNotRetryable;
}

public sealed record PreparedTicketCanonicalEpochRecoveryStartRequest(
    string SourceRunId);

public sealed record PreparedTicketCanonicalEpochRecoverySourceAbandonment(
    string RunId,
    long AuthoringEpoch,
    string PromotionState,
    DateTimeOffset AbandonedAt,
    string Reason);

public sealed record PreparedTicketCanonicalEpochRecoveryFrozenState(
    int AcceptedTicketCount,
    int GroupingPartitionCount,
    string CorpusFingerprint,
    string GroupingFingerprint,
    string RecipeFingerprint,
    DateTimeOffset CapturedAt);

public sealed record PreparedTicketCanonicalEpochRecoveryProof(
    int ContractVersion,
    string Purpose,
    string RunId,
    string SourceRunId,
    long AuthoringEpoch,
    DateTimeOffset AbandonedAt,
    string CorpusFingerprint,
    string GroupingFingerprint,
    string RecipeFingerprint,
    DateTimeOffset CapturedAt);

public sealed record PreparedTicketCanonicalEpochRecoveryLink(
    string RunId,
    string SourceRunId,
    long AuthoringEpoch,
    string State,
    string? SnapshotId = null,
    string? SnapshotSha256 = null,
    DateTimeOffset? ResolvedAt = null);

public sealed record PreparedTicketCanonicalEpochRecoveryLifecycleStatus(
    string State,
    bool MutationFenceHeld,
    DateTimeOffset? LastRecoveryAttemptAt = null,
    string? FailureCode = null,
    string? FailureDetail = null);

public sealed record PreparedTicketCanonicalEpochRecoveryStatusResult(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items,
    PreparedTicketCanonicalEpochRecoverySourceAbandonment SourceAbandonment,
    PreparedTicketCanonicalEpochRecoveryFrozenState Frozen,
    PreparedTicketCanonicalEpochRecoveryLifecycleStatus Recovery,
    PreparedTicketCanonicalEpochRecoveryProof? PublicationProof = null,
    AuthoringSnapshotDescriptor? Snapshot = null);

public sealed record PreparedTicketCanonicalEpochRecoveryStartResult(
    PreparedTicketCanonicalEpochRecoveryStatusResult Status,
    bool ExistingRun);

public sealed record PreparedTicketCanonicalEpochRecoveryRetryResult(
    PreparedTicketCanonicalEpochRecoveryStatusResult Status,
    bool RecoveryStarted);

public sealed record PreparedTicketCanonicalEpochRecoveryFailure(
    string Error,
    string? Detail = null,
    IReadOnlyList<string>? ConflictingRunIds = null,
    string? RunId = null);

public sealed class PreparedTicketCanonicalEpochRecoveryException(
    string failureCode,
    string detail,
    IReadOnlyList<string>? relatedRunIds = null)
    : InvalidOperationException(detail)
{
    public string FailureCode { get; } = failureCode;
    public IReadOnlyList<string> RelatedRunIds { get; } =
        relatedRunIds ?? [];
}
