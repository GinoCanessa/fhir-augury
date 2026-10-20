using FhirAugury.Source.Confluence.Configuration;

namespace FhirAugury.Source.Confluence.Tests;

public class ConfluenceServiceOptionsTests
{
    [Fact]
    public void NullSpaces_MeansDiscoverEveryNonArchivedSpace()
    {
        // The three-space default (FHIR, FHIRI, SOA) is retired. Null now means
        // "discover the whole instance", so there is no hard-coded list to
        // return — the space catalog written by discovery is the answer.
        ConfluenceServiceOptions options = new();

        Assert.Null(options.Spaces);
        Assert.False(options.SpacesAreExplicit);
        Assert.False(options.HasExplicitEmptySpaces);
    }

    [Fact]
    public void EmptySpaces_IsAnExplicitRequestToTrackNothing()
    {
        ConfluenceServiceOptions options = new() { Spaces = [] };

        Assert.True(options.HasExplicitEmptySpaces);
        Assert.False(options.SpacesAreExplicit);
    }

    [Fact]
    public void CustomSpaces_RestrictsTheTrackedSet()
    {
        ConfluenceServiceOptions options = new() { Spaces = ["ABC", "DEF"] };

        Assert.True(options.SpacesAreExplicit);
        Assert.False(options.HasExplicitEmptySpaces);
        Assert.Equal(["ABC", "DEF"], options.Spaces);
    }

    [Fact]
    public void SweepDefaults_MatchTheMeasuredInstance()
    {
        ConfluenceServiceOptions options = new();

        // 200 is honoured verbatim by HL7's Confluence; see
        // docs/technical/confluence-api-notes.md.
        Assert.Equal(200, options.SweepPageSize);
        Assert.Equal(104_857_600, options.AttachmentMaxBytes);
    }

    [Fact]
    public void SpaceSweepMaxAge_DefaultsToSweepingEveryRun()
    {
        ConfluenceServiceOptions options = new();

        Assert.Equal(TimeSpan.Zero, options.GetSpaceSweepMaxAge());
    }

    [Theory]
    [InlineData("01:00:00", 3600)]
    [InlineData("1.00:00:00", 86400)]
    public void SpaceSweepMaxAge_ParsesAConfiguredThreshold(string configured, int expectedSeconds)
    {
        ConfluenceServiceOptions options = new() { SpaceSweepMaxAge = configured };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.GetSpaceSweepMaxAge());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a timespan")]
    [InlineData("-01:00:00")]
    public void SpaceSweepMaxAge_FallsBackToZeroRatherThanThrowing(string configured)
    {
        ConfluenceServiceOptions options = new() { SpaceSweepMaxAge = configured };

        Assert.Equal(TimeSpan.Zero, options.GetSpaceSweepMaxAge());
    }
}
