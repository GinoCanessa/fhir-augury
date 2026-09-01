using System.Net;
using FhirAugury.Source.Confluence.Ingestion;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins the WAF challenge fingerprint: HTTP <c>405</c> <b>plus</b> an
/// <c>x-amzn-waf-action</c> header, nothing narrower and nothing wider.
/// </summary>
/// <remarks>
/// The negative cases are the load-bearing ones. A headerless <c>405</c> is a
/// genuine <c>Method Not Allowed</c> and must keep its ordinary per-item
/// behaviour, because a false positive here parks the whole service until a
/// human clears it.
/// </remarks>
public class ConfluenceChallengeDetectorTests
{
    private const string Url = "https://confluence.test/rest/api/content?type=page";

    private static HttpResponseMessage Response(
        HttpStatusCode status, string? reasonPhrase = null, string? wafAction = null)
    {
        HttpResponseMessage response = new(status);

        if (reasonPhrase is not null)
        {
            response.ReasonPhrase = reasonPhrase;
        }

        if (wafAction is not null)
        {
            response.Headers.TryAddWithoutValidation(
                ConfluenceChallengeDetector.WafActionHeader, wafAction);
        }

        return response;
    }

    [Fact]
    public void Detect_MatchesWafCaptcha405()
    {
        using HttpResponseMessage response =
            Response(HttpStatusCode.MethodNotAllowed, "Not Allowed", "captcha");

        ConfluenceHumanInterventionRequiredException? challenge =
            ConfluenceChallengeDetector.Detect(response, Url);

        Assert.NotNull(challenge);
        Assert.Equal(405, challenge.StatusCode);
        Assert.Equal("captcha", challenge.WafAction);
        Assert.Equal(Url, challenge.RequestUrl);
        Assert.Contains("405 (Not Allowed)", challenge.Message, StringComparison.Ordinal);
        Assert.Contains(
            ConfluenceChallengeDetector.WafActionHeader, challenge.Message, StringComparison.Ordinal);
        Assert.Contains("ingestion-block/clear", challenge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_IgnoresPlain405WithoutWafHeader()
    {
        using HttpResponseMessage response =
            Response(HttpStatusCode.MethodNotAllowed, "Method Not Allowed");

        Assert.Null(ConfluenceChallengeDetector.Detect(response, Url));
    }

    [Fact]
    public void Detect_IgnoresWafHeaderOnNon405()
    {
        using HttpResponseMessage forbidden =
            Response(HttpStatusCode.Forbidden, wafAction: "captcha");
        using HttpResponseMessage tooManyRequests =
            Response(HttpStatusCode.TooManyRequests, wafAction: "block");
        using HttpResponseMessage ok = Response(HttpStatusCode.OK, wafAction: "captcha");

        Assert.Null(ConfluenceChallengeDetector.Detect(forbidden, Url));
        Assert.Null(ConfluenceChallengeDetector.Detect(tooManyRequests, Url));
        Assert.Null(ConfluenceChallengeDetector.Detect(ok, Url));
    }

    [Fact]
    public void Detect_PreservesObservedReasonPhrase()
    {
        using HttpResponseMessage response =
            Response(HttpStatusCode.MethodNotAllowed, "Not Allowed", "captcha");

        ConfluenceHumanInterventionRequiredException challenge =
            ConfluenceChallengeDetector.Detect(response, Url)!;

        // The appliance says "Not Allowed"; the RFC says "Method Not Allowed".
        // Recording what was actually observed is what makes the next sighting
        // recognizable from a log line alone.
        Assert.Equal("Not Allowed", challenge.ReasonPhrase);
        Assert.Contains("Not Allowed", challenge.Fingerprint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("captcha")]
    [InlineData("challenge")]
    [InlineData("block")]
    public void Detect_MatchesNonCaptchaWafAction(string action)
    {
        using HttpResponseMessage response =
            Response(HttpStatusCode.MethodNotAllowed, "Not Allowed", action);

        ConfluenceHumanInterventionRequiredException? challenge =
            ConfluenceChallengeDetector.Detect(response, Url);

        Assert.NotNull(challenge);
        Assert.Equal(action, challenge.WafAction);
    }

    [Fact]
    public void Detect_IgnoresAnEmptyWafActionValue()
    {
        using HttpResponseMessage response =
            Response(HttpStatusCode.MethodNotAllowed, "Not Allowed", "   ");

        Assert.Null(ConfluenceChallengeDetector.Detect(response, Url));
    }
}
