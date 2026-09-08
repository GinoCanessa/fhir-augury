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

public sealed record AuthoringRunCreateRequest(
    string ProcessorKind,
    IReadOnlyList<AuthoringRunItemDefinition> Items,
    bool DatabaseOnly = false);

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
    int SupersededItems = 0);

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
    DateTimeOffset? NextAutomaticRetryAt = null);

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
    DateTimeOffset CreatedAt);
