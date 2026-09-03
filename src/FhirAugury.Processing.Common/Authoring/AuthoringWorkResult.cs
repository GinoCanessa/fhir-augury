using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Common.Authoring;

public enum AuthoringWorkDisposition
{
    Persisted,
    Complete,
    RetryableError,
    PermanentError,
}

public sealed record AuthoringWorkResult(
    AuthoringWorkDisposition Disposition,
    string? ReceiptId = null,
    string? Error = null)
{
    public static AuthoringWorkResult Persisted(string receiptId) => new(AuthoringWorkDisposition.Persisted, receiptId);
    public static AuthoringWorkResult Complete(string receiptId) => new(AuthoringWorkDisposition.Complete, receiptId);
    public static AuthoringWorkResult Retry(string error) => new(AuthoringWorkDisposition.RetryableError, Error: error);
    public static AuthoringWorkResult Fail(string error) => new(AuthoringWorkDisposition.PermanentError, Error: error);
}

public sealed record AuthoringOperationClaim(
    string RunId,
    string ItemId,
    string BusinessKey,
    string ItemKind,
    string ExpectedSourceRevision,
    string OperationId,
    string OperationToken,
    int AttemptNumber);

public sealed record AuthoringReceiptAcceptance(
    AuthoringResultReceipt Receipt,
    bool IsReplay);

public sealed record AuthoringRetryResult(
    string ItemId,
    bool RequiresAuthoring);

public sealed record AuthoringRunStageLease(
    string StageId,
    string LeaseId,
    int AttemptNumber);

public delegate Task AuthoringDomainPersistence(
    SqliteConnection connection,
    CancellationToken cancellationToken);
