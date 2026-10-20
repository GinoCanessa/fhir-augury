using System.Net;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Recognizes the AWS WAF edge challenge that <c>confluence.hl7.org</c> serves
/// in place of a real response, so a blocked run can stop instead of grinding
/// through thousands of doomed requests.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint is deliberately narrow: HTTP <c>405</c> <b>plus</b> an
/// <c>x-amzn-waf-action</c> response header. AWS sets that header only when the
/// web ACL takes a non-allow action, so any value (<c>captcha</c>,
/// <c>challenge</c>, <c>block</c>) is the same class of event. A headerless
/// <c>405</c> stays an ordinary per-item failure, which is what keeps a genuine
/// <c>405 Method Not Allowed</c> from manufacturing a permanent operator block.
/// </para>
/// <para>
/// Detection lives here, in the Confluence source, and not in the shared
/// <see cref="Common.HttpRetryHelper"/>: reinterpreting <c>405</c> for Jira,
/// Zulip, and GitHub would invent false positives on real verb rejections.
/// </para>
/// </remarks>
public static class ConfluenceChallengeDetector
{
    /// <summary>The response header AWS WAF stamps on a non-allow action.</summary>
    public const string WafActionHeader = "x-amzn-waf-action";

    /// <summary>
    /// Returns the ready-to-throw exception when <paramref name="response"/>
    /// carries the edge-challenge fingerprint, or <see langword="null"/> when it
    /// is an ordinary response the caller should keep handling.
    /// </summary>
    /// <param name="response">The response as returned by the retry helper.</param>
    /// <param name="url">The request URL, recorded for the operator.</param>
    public static ConfluenceHumanInterventionRequiredException? Detect(
        HttpResponseMessage response, string url)
    {
        if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        if (!response.Headers.TryGetValues(WafActionHeader, out IEnumerable<string>? values))
        {
            return null;
        }

        string? action = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        return new ConfluenceHumanInterventionRequiredException(
            (int)response.StatusCode, response.ReasonPhrase, action, url);
    }
}
