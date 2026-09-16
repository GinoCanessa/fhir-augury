using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

public static class PreparedTicketPublicationReconciliationContract
{
    public const int CurrentVersion = 1;
    public const string Purpose = "publication-reconciliation";
}

public static class PreparedTicketPublicationReconciliationDispositionValues
{
    public const string CarryForward = "carry-forward";
    public const string ReAuthor = "re-author";

    public static bool IsValid(string? disposition)
        => disposition is CarryForward or ReAuthor;

    public static void EnsureValid(string disposition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disposition);
        if (!IsValid(disposition))
        {
            throw new ArgumentException(
                $"Unknown publication reconciliation disposition '{disposition}'.",
                nameof(disposition));
        }
    }
}

public static class PreparedTicketPublicationReconciliationPromotionStateValues
{
    public const string Staged = "staged";
    public const string SnapshotPublishPending = "snapshot-publish-pending";
    public const string Ready = "ready";
    public const string CanonicalUnpublished = "canonical-unpublished";

    public static bool IsValid(string? state)
        => state is
            Staged or
            SnapshotPublishPending or
            Ready or
            CanonicalUnpublished;
}

/// <summary>
/// Stable machine-readable failure codes for publication reconciliation.
/// </summary>
public static class PreparedTicketPublicationReconciliationFailureCodes
{
    public const string InvalidBaseline = "invalid-baseline";
    public const string UnstableJiraGeneration =
        "unstable-jira-generation";
    public const string RevisionInvalidation = "revision-invalidation";
    public const string StagingMismatch = "staging-mismatch";
    public const string GroupingImpactMismatch =
        "grouping-impact-mismatch";
    public const string RecoveryInProgress = "recovery-in-progress";
    public const string PromotionRecoveryFailure =
        "promotion-recovery-failure";
    public const string CanonicalUnpublishedRestriction =
        "canonical-unpublished-restriction";

    public static bool IsKnown(string? code)
        => code is
            InvalidBaseline or
            UnstableJiraGeneration or
            RevisionInvalidation or
            StagingMismatch or
            GroupingImpactMismatch or
            RecoveryInProgress or
            PromotionRecoveryFailure or
            CanonicalUnpublishedRestriction;
}

public sealed record PreparedTicketPublicationReconciliationStartRequest(
    string SourceRunId);

public sealed record PreparedTicketPublicationReconciliationItemDecision(
    string TicketKey,
    string Disposition,
    string BaselineSourceRevision,
    string CurrentSourceRevision,
    string BaselineReceiptId,
    string BaselineRunItemId,
    string BaselineContributingRunId,
    string BaselineAuthoredFingerprint,
    string BaselineGroupingFingerprint);

public sealed record PreparedTicketPublicationReconciliationComparison(
    int ContractVersion,
    string SourceRunId,
    string SourceSnapshotId,
    string SourceSnapshotSha256,
    string StableJiraGeneration,
    DateTimeOffset CapturedAt,
    string CorpusFingerprint,
    IReadOnlyList<PreparedTicketPublicationReconciliationItemDecision> Items);

public sealed record PreparedTicketPublicationReconciliationGroupingImpact(
    string PartitionKey,
    IReadOnlyList<string> RevisedTicketKeys,
    string BaselineCorpusFingerprint,
    string BaselineOutputFingerprint,
    string BaselineProtectedRowsFingerprint,
    string? StagedCorpusFingerprint = null,
    string? StagedOutputFingerprint = null,
    string? StagedProtectedRowsFingerprint = null,
    bool Complete = false);

public sealed record PreparedTicketPublicationReconciliationPromotionStatus(
    string State,
    string? JournalState,
    bool MutationFenceHeld,
    DateTimeOffset? LastRecoveryAttemptAt = null,
    string? FailureCode = null,
    string? FailureDetail = null,
    DateTimeOffset? AbandonedAt = null,
    string? AbandonmentReason = null);

public sealed record PreparedTicketPublicationReconciliationProof(
    int ContractVersion,
    string Purpose,
    string SourceRunId,
    string SourceSnapshotId,
    string StableJiraGeneration,
    int AcceptedTicketCount,
    int CarryForwardTicketCount,
    int ReAuthorTicketCount,
    string CorpusFingerprint,
    string GroupingImpactFingerprint,
    DateTimeOffset CapturedAt);

public sealed record PreparedTicketPublicationReconciliationStartResult(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items,
    PreparedTicketPublicationReconciliationComparison Comparison,
    AuthoringRunReconciliationCounts Counts);

public sealed record PreparedTicketPublicationReconciliationStatusResult(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items,
    PreparedTicketPublicationReconciliationComparison Comparison,
    AuthoringRunReconciliationCounts Counts,
    IReadOnlyList<PreparedTicketPublicationReconciliationGroupingImpact> GroupingImpacts,
    PreparedTicketPublicationReconciliationPromotionStatus Promotion,
    IReadOnlyList<string> InvalidatedTicketKeys,
    PreparedTicketPublicationReconciliationProof? PublicationProof = null,
    string? FailureCode = null,
    string? FailureDetail = null);

public sealed record PreparedTicketPublicationReconciliationRetryResult(
    PreparedTicketPublicationReconciliationStatusResult Status,
    bool RecoveryStarted);

public sealed record PreparedTicketPublicationReconciliationAbandonRequest(
    string Reason);

public sealed record PreparedTicketPublicationReconciliationAbandonResult(
    PreparedTicketPublicationReconciliationStatusResult Status,
    DateTimeOffset AbandonedAt,
    string Reason);

public sealed record PreparedTicketPublicationReconciliationFailure(
    string Error,
    string? Detail = null,
    IReadOnlyList<string>? ConflictingRunIds = null,
    string? RunId = null);
