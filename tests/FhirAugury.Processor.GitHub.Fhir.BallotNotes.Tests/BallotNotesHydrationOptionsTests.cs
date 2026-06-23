using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesHydrationOptionsTests
{
    [Fact]
    public void Validate_accepts_positive_connect_timeout()
    {
        BallotNotesHydrationOptions options = new()
        {
            AttributionConnectTimeout = TimeSpan.FromSeconds(1),
        };

        Assert.DoesNotContain(
            options.Validate(),
            error => error.Contains("AttributionConnectTimeout"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_nonpositive_connect_timeout(int seconds)
    {
        BallotNotesHydrationOptions options = new()
        {
            AttributionConnectTimeout = TimeSpan.FromSeconds(seconds),
        };

        Assert.Contains(
            options.Validate(),
            error => error.Contains("AttributionConnectTimeout"));
    }
}
