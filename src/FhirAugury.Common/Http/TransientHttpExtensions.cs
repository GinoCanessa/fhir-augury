using System.Net;

namespace FhirAugury.Common.Http;

/// <summary>
/// Extension methods for classifying transient HTTP errors.
/// </summary>
public static class TransientHttpExtensions
{
    /// <summary>
    /// Returns true if the HTTP response indicates a transient server error.
    /// </summary>
    public static bool IsTransientHttpError(this HttpResponseMessage response)
        => response.StatusCode is HttpStatusCode.ServiceUnavailable
                               or HttpStatusCode.GatewayTimeout
                               or HttpStatusCode.RequestTimeout;

    /// <summary>
    /// Returns true if the exception is a transient HTTP error (connection refused, timeout, etc.)
    /// that typically occurs during service startup or shutdown.
    /// When true, <paramref name="statusDescription"/> contains the status code or error type.
    /// </summary>
    public static bool IsTransientHttpError(this Exception ex, out string statusDescription)
    {
        if (ex is HttpRequestException httpEx)
        {
            statusDescription = httpEx.StatusCode?.ToString() ?? "ConnectionRefused";
            return true;
        }

        if (ex is TaskCanceledException { InnerException: TimeoutException })
        {
            statusDescription = "Timeout";
            return true;
        }

        statusDescription = "";
        return false;
    }
}
