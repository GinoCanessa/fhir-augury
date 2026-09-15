using System.Globalization;

namespace FhirAugury.Processing.Contracts;

public static class AuthoringSourceRevision
{
    public static string CanonicalizeTimestamp(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        bool looksLikeIsoTimestamp =
            value.Length >= 20 &&
            value[4] == '-' &&
            value[7] == '-' &&
            value[10] == 'T' &&
            (value.EndsWith('Z') ||
             value.Length >= 6 &&
             (value[^6] is '+' or '-') &&
             value[^3] == ':');
        return looksLikeIsoTimestamp &&
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp)
            ? timestamp.ToString("O", CultureInfo.InvariantCulture)
            : value;
    }
}

public sealed record AuthoringRunItemDefinition(
    string BusinessKey,
    string ItemKind,
    string ExpectedSourceRevision);

public sealed record AuthoringRunInputProvenanceDefinition(
    string Source,
    DateTimeOffset? LatestSuccessfulRefreshAt,
    long? ContentRevision);

public sealed record AuthoringRunCreateRequest(
    string ProcessorKind,
    IReadOnlyList<AuthoringRunItemDefinition> Items,
    bool DatabaseOnly = false);

public sealed record AuthoringAllowedActions(
    bool CanRetryNow,
    bool CanSupersede);

public sealed record AuthoringRunStateInfo(
    bool IsTerminal,
    bool IsRecoverable,
    DateTimeOffset? NextAutomaticRecoveryAt = null);

public sealed record AuthoringRunCorpusComparison(
    string SourceSnapshotId,
    int SourceExportedTicketCount,
    int CurrentAcceptedTicketCount,
    int AdditionalTicketCount)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceSnapshotId);
        ArgumentOutOfRangeException.ThrowIfNegative(SourceExportedTicketCount);
        ArgumentOutOfRangeException.ThrowIfNegative(CurrentAcceptedTicketCount);
        ArgumentOutOfRangeException.ThrowIfNegative(AdditionalTicketCount);
        if ((long)SourceExportedTicketCount + AdditionalTicketCount !=
            CurrentAcceptedTicketCount)
        {
            throw new ArgumentException(
                "The source and additional ticket counts must equal the current accepted ticket count.");
        }
    }
}

public sealed record AuthoringRunStatus(
    string RunId,
    string ProcessorKind,
    long AuthoringEpoch,
    string Status,
    bool DatabaseOnly,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    int RetryableErrorItems = 0,
    int SupersededItems = 0,
    AuthoringRunStateInfo? State = null,
    string? Purpose = null,
    string? SourceRunId = null,
    AuthoringRunCorpusComparison? CorpusComparison = null);

public sealed record AuthoringRunItemStatus(
    string ItemId,
    string RunId,
    string BusinessKey,
    string ItemKind,
    string ExpectedSourceRevision,
    string Status,
    string? CurrentOperationId,
    string? AcceptedReceiptId,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    int? AttemptsRemaining = null,
    DateTimeOffset? NextAutomaticRetryAt = null,
    string? CurrentError = null,
    string? SupersessionReason = null,
    AuthoringAllowedActions? AllowedActions = null);

public sealed record AuthoringRunResponse(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);

public sealed record AuthoringRunListResponse(
    IReadOnlyList<AuthoringRunStatus> Runs,
    bool Truncated);

public sealed record AuthoringConflictResponse(
    string Error,
    string? Detail = null,
    IReadOnlyList<string>? ConflictingRunIds = null,
    string? RunId = null);

public sealed record AuthoringItemSupersedeRequest(string Reason);

public sealed record AuthoringItemSupersedeResult(
    string RunId,
    string ItemId,
    string Status,
    string Reason);

public sealed record AuthoringResultSubmission(
    string RunId,
    string ItemId,
    string OperationId,
    string ObservedSourceRevision,
    string ContentHash);

public sealed record AuthoringResultReceipt(
    string ReceiptId,
    string RunId,
    string ItemId,
    string OperationId,
    string BusinessKey,
    string ContentHash,
    string ExpectedSourceRevision,
    string ObservedSourceRevision,
    long AuthoringEpoch,
    DateTimeOffset PersistedAt);

public sealed record AuthoringRunStageStatus(
    string StageId,
    string RunId,
    string StageName,
    string PartitionKey,
    string InputFingerprint,
    string Status,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public sealed record AuthoringRunStageReceipt(
    string RunId,
    string StageId,
    string PartitionKey,
    string InputFingerprint,
    int TopicRows,
    int TopicGroupRows,
    int MemberRows,
    DateTimeOffset PersistedAt);

public sealed record AuthoringSnapshotPublicationProof(
    int ContractVersion,
    string Purpose,
    string SourceRunId,
    string SourceName,
    DateTimeOffset SourceLastSuccessfulRefreshAt,
    long SourceContentRevision,
    int PublicDisplayNamePolicyVersion,
    string CorpusFingerprint,
    string GroupingFingerprint,
    DateTimeOffset CapturedAt);

public sealed record AuthoringSnapshotDescriptor(
    string ProcessorKind,
    string RunId,
    string SnapshotId,
    long AuthoringEpoch,
    long Sequence,
    int SchemaVersion,
    string Sha256,
    long SizeBytes,
    int ItemCount,
    int ReceiptCount,
    IReadOnlyDictionary<string, long> TableCounts,
    string FileName,
    DateTimeOffset CreatedAt,
    AuthoringSnapshotPublicationProof? PublicationProof = null);
