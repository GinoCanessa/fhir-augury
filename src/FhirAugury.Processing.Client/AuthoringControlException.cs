using System.Net;

namespace FhirAugury.Processing.Client;

public sealed class AuthoringControlException : HttpRequestException
{
    public AuthoringControlException(
        HttpStatusCode statusCode,
        string errorCode,
        string? detail = null,
        TimeSpan? retryAfter = null,
        IEnumerable<string>? relatedRunIds = null)
        : this(
            statusCode,
            errorCode,
            detail,
            retryAfterHeader: null,
            retryAfter,
            (relatedRunIds ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            endpoint: "authoring control")
    {
    }

    internal AuthoringControlException(
        HttpStatusCode statusCode,
        string errorCode,
        string? detail,
        string? retryAfterHeader,
        TimeSpan? retryAfter,
        IReadOnlyList<string> relatedRunIds,
        string endpoint,
        Exception? innerException = null)
        : base(
            CreateMessage(statusCode, errorCode, detail, endpoint),
            innerException,
            statusCode)
    {
        ErrorCode = errorCode;
        Detail = detail;
        RetryAfterHeader = retryAfterHeader;
        RetryAfter = retryAfter;
        RelatedRunIds = relatedRunIds;
        Endpoint = endpoint;
    }

    public string ErrorCode { get; }

    public string? Detail { get; }

    public string? RetryAfterHeader { get; }

    public TimeSpan? RetryAfter { get; }

    public IReadOnlyList<string> RelatedRunIds { get; }

    public string Endpoint { get; }

    private static string CreateMessage(
        HttpStatusCode statusCode,
        string errorCode,
        string? detail,
        string endpoint)
    {
        string message =
            $"Authoring endpoint '{endpoint}' returned {(int)statusCode} ({errorCode}).";
        return string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message} {detail}";
    }
}

public sealed class AuthoringMutationOutcomeUnknownException
    : HttpRequestException
{
    public AuthoringMutationOutcomeUnknownException(
        string operation,
        string serviceName,
        string? runId,
        string? itemId,
        Exception innerException)
        : base(
            $"The {operation} request for authoring service '{serviceName}' may have reached the server, but no response was received. Reconcile the outcome with a read before trying again.",
            innerException)
    {
        Operation = operation;
        ServiceName = serviceName;
        RunId = runId;
        ItemId = itemId;
    }

    public string Operation { get; }

    public string ServiceName { get; }

    public string? RunId { get; }

    public string? ItemId { get; }
}
