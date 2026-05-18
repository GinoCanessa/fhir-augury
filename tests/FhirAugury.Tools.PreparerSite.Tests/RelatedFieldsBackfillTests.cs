using FhirAugury.Tools.PreparerSite;

namespace FhirAugury.Tools.PreparerSite.Tests;

public sealed class RelatedFieldsBackfillTests
{
    [Fact]
    public void NormalizeAndSplit_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(RelatedFieldsBackfill.NormalizeAndSplit(null));
        Assert.Empty(RelatedFieldsBackfill.NormalizeAndSplit(string.Empty));
    }

    [Fact]
    public void NormalizeAndSplit_TrimsAndDropsEmptySegments()
    {
        string[] result = [.. RelatedFieldsBackfill.NormalizeAndSplit("  Observation , , Patient  ,,")];
        Assert.Equal(new[] { "Observation", "Patient" }, result);
    }

    [Fact]
    public void NormalizeAndSplit_DedupsCaseInsensitivelyAndKeepsFirstSeenSpelling()
    {
        string[] result = [.. RelatedFieldsBackfill.NormalizeAndSplit("Observation, OBSERVATION, observation, Patient")];
        Assert.Equal(new[] { "Observation", "Patient" }, result);
    }

    [Fact]
    public void NormalizeAndSplit_WhitespaceOnly_ReturnsEmpty()
    {
        Assert.Empty(RelatedFieldsBackfill.NormalizeAndSplit("   "));
        Assert.Empty(RelatedFieldsBackfill.NormalizeAndSplit(",,,"));
    }

    [Fact]
    public void NormalizeAndSplit_SingleValue_RoundTrips()
    {
        string[] result = [.. RelatedFieldsBackfill.NormalizeAndSplit("Observation")];
        Assert.Equal(new[] { "Observation" }, result);
    }
}
