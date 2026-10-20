using System.Net;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins the central run-stop guard: which exceptions end the whole run, and
/// which ones a network loop is still allowed to record as a per-item failure.
/// </summary>
/// <remarks>
/// The last test is the one that proves the catch-site change landed: a
/// challenge thrown by the fetch seam has to make the sweep <b>throw</b>, not
/// return <c>Succeeded = false</c>, because a failed space is exactly the
/// grinding behaviour this change exists to remove.
/// </remarks>
public class ConfluenceRunStopTests : IDisposable
{
    private const string Space = "FHIR";
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly FileSystemResponseCache _cache;

    public ConfluenceRunStopTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-runstop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _cache = new FileSystemResponseCache(_root);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private static ConfluenceHumanInterventionRequiredException Challenge() =>
        new(405, "Not Allowed", "captcha", $"{BaseUrl}/rest/api/content");

    [Fact]
    public void ThrowIfRunMustStop_RethrowsAChallenge()
    {
        ConfluenceHumanInterventionRequiredException challenge = Challenge();

        ConfluenceHumanInterventionRequiredException thrown =
            Assert.Throws<ConfluenceHumanInterventionRequiredException>(
                () => ConfluenceRunStop.ThrowIfRunMustStop(challenge));

        Assert.Same(challenge, thrown);
    }

    [Fact]
    public void ThrowIfRunMustStop_RethrowsANestedChallenge()
    {
        ConfluenceHumanInterventionRequiredException challenge = Challenge();
        Exception wrapped = new InvalidOperationException(
            "space sweep failed", new AggregateException(challenge));

        ConfluenceHumanInterventionRequiredException thrown =
            Assert.Throws<ConfluenceHumanInterventionRequiredException>(
                () => ConfluenceRunStop.ThrowIfRunMustStop(wrapped));

        Assert.Same(challenge, thrown);
    }

    [Fact]
    public void ThrowIfRunMustStop_StillRecognizesAnAuthFailure()
    {
        HttpRequestException unauthorized =
            new("denied", null, HttpStatusCode.Unauthorized);

        Assert.Throws<ConfluenceAuthFailureException>(
            () => ConfluenceRunStop.ThrowIfRunMustStop(unauthorized));
    }

    [Fact]
    public void ThrowIfRunMustStop_PassesAnUnrelatedExceptionThrough()
    {
        // No throw: the caller records the item and carries on.
        ConfluenceRunStop.ThrowIfRunMustStop(new HttpRequestException("connection reset"));
        ConfluenceRunStop.ThrowIfRunMustStop(
            new HttpRequestException("gone", null, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task SweepSpace_ChallengeFromTheFetchSeam_AbortsTheRun()
    {
        int calls = 0;
        ConfluenceFetch fetch = (_, _) =>
        {
            calls++;
            throw Challenge();
        };

        ConfluenceSweep sweep = new(
            Microsoft.Extensions.Options.Options.Create(new ConfluenceServiceOptions
            {
                BaseUrl = BaseUrl,
                SweepPageSize = 2,
                SpaceSweepMaxAge = "00:00:00",
            }),
            _cache,
            NullLogger<ConfluenceSweep>.Instance,
            fetch);

        await Assert.ThrowsAsync<ConfluenceHumanInterventionRequiredException>(
            () => sweep.SweepSpaceAsync(Space, CancellationToken.None));

        // One request, then the run ended — not three streams' worth of retries.
        Assert.Equal(1, calls);
    }
}
