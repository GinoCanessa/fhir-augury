namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Aborts a run because an edge appliance is challenging us and only a human can
/// clear it.
/// </summary>
/// <remarks>
/// Thrown at the Confluence HTTP boundary the moment the WAF fingerprint is
/// seen, then caught once by <c>ConfluenceIngestionPipeline</c>, which records a
/// durable block. It is the "we just hit the wall" signal; the sibling
/// <c>ConfluenceIngestionBlockedException</c> is the "we already know about the
/// wall" refusal.
/// </remarks>
public sealed class ConfluenceHumanInterventionRequiredException : Exception
{
    /// <summary>The operator instruction, repeated wherever the block surfaces.</summary>
    public const string RemediationText =
        "Open the Confluence site in a browser, solve the challenge, refresh Confluence:Cookie in " +
        "appsettings.local.json if the session identity was flagged, then clear the block with " +
        "POST api/v1/ingestion-block/clear.";

    public ConfluenceHumanInterventionRequiredException(
        int statusCode, string? reasonPhrase, string wafAction, string requestUrl)
        : base(
            $"Confluence answered {statusCode} ({reasonPhrase ?? "Not Allowed"}) with an " +
            $"{ConfluenceChallengeDetector.WafActionHeader}: {wafAction} header — an AWS WAF edge challenge, " +
            $"not a Confluence response. Stopping the run rather than recording every remaining item as a " +
            $"failure. {RemediationText} (request: {requestUrl})")
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        WafAction = wafAction;
        RequestUrl = requestUrl;
    }

    /// <summary>The observed status code — <c>405</c> for every sighting so far.</summary>
    public int StatusCode { get; }

    /// <summary>
    /// The observed reason phrase, preserved verbatim: the appliance returns
    /// <c>Not Allowed</c>, not the RFC's <c>Method Not Allowed</c>.
    /// </summary>
    public string? ReasonPhrase { get; }

    /// <summary>The <c>x-amzn-waf-action</c> value, e.g. <c>captcha</c>.</summary>
    public string WafAction { get; }

    /// <summary>The URL that drew the challenge.</summary>
    public string RequestUrl { get; }

    /// <summary>What a human has to do before ingestion can resume.</summary>
    public string Remediation => RemediationText;

    /// <summary>A compact, loggable form of the fingerprint.</summary>
    public string Fingerprint =>
        $"HTTP {StatusCode} ({ReasonPhrase ?? "Not Allowed"}) + " +
        $"{ConfluenceChallengeDetector.WafActionHeader}: {WafAction}";
}
