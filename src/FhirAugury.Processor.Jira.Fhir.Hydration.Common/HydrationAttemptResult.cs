namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

public abstract record HydrationAttemptResult(string TicketKey)
{
    public const string UnexpectedErrorCode = "unexpected-error";
}

public sealed record HydrationAttemptSuccess : HydrationAttemptResult
{
    public HydrationAttemptSuccess(HydrationBatch batch)
        : base(batch.TicketKey)
    {
        Batch = batch;
    }

    public HydrationBatch Batch { get; }
}

public sealed record HydrationAttemptFailure : HydrationAttemptResult
{
    public HydrationAttemptFailure(
        string ticketKey,
        string failureCode,
        string exceptionType,
        string reason)
        : base(ticketKey)
    {
        FailureCode = failureCode;
        ExceptionType = exceptionType;
        Reason = reason;
    }

    public string FailureCode { get; }
    public string ExceptionType { get; }
    public string Reason { get; }
}
