using System.Net;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Recognizes the authentication failure that
/// <see cref="Common.HttpRetryHelper.ExecuteWithRetryAsync"/> throws on a 401 or
/// 403, so every network loop can abort the whole run rather than record
/// thousands of per-item failures.
/// </summary>
/// <remarks>
/// Centralized deliberately. Discovery, the three sweep streams, the fill, and
/// the attachment blob download all route their catch through here; handling it
/// in only one of six places is exactly how an expired mid-run cookie turns into
/// a cache full of holes that look like deletions.
/// </remarks>
public static class ConfluenceAuthFailure
{
    /// <summary>True when the exception represents a 401 or 403 from Confluence.</summary>
    public static bool IsAuthFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status }
                && (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rethrows when the exception is an auth failure, so callers can express
    /// "record this item and carry on, unless the credential died".
    /// </summary>
    public static void ThrowIfAuthFailure(Exception exception)
    {
        if (IsAuthFailure(exception))
        {
            throw new ConfluenceAuthFailureException(exception);
        }
    }
}

/// <summary>Aborts a run because the credential is no longer accepted.</summary>
public sealed class ConfluenceAuthFailureException(Exception inner)
    : Exception(
        "Confluence rejected the configured credential (HTTP 401/403); aborting the run rather than " +
        "recording every remaining item as a failure. Refresh the cookie or API token and re-run.",
        inner);
