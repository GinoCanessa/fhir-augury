using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class StopWordsTests
{
    [Fact]
    public void IsStopWord_ReturnsTrueForKnownStopWords()
    {
        Assert.True(StopWords.IsStopWord("the"));
        Assert.True(StopWords.IsStopWord("is"));
        Assert.True(StopWords.IsStopWord("and"));
        Assert.True(StopWords.IsStopWord("using"));
    }

    [Fact]
    public void IsStopWord_ReturnsFalseForNonStopWords()
    {
        Assert.False(StopWords.IsStopWord("patient"));
        Assert.False(StopWords.IsStopWord("observation"));
        Assert.False(StopWords.IsStopWord("fhir"));
    }

    [Fact]
    public void CreateMergedSet_IncludesDefaults()
    {
        System.Collections.Frozen.FrozenSet<string> merged = StopWords.CreateMergedSet();
        Assert.True(merged.Contains("the"));
        Assert.True(merged.Contains("is"));
        Assert.True(merged.Contains("using"));
    }

    [Fact]
    public void CreateMergedSet_IncludesAdditionalWords()
    {
        System.Collections.Frozen.FrozenSet<string> merged = StopWords.CreateMergedSet(["custom1", "custom2"]);
        Assert.True(merged.Contains("custom1"));
        Assert.True(merged.Contains("custom2"));
        // Still contains defaults
        Assert.True(merged.Contains("the"));
    }

    [Fact]
    public void CreateMergedSet_HandlesNull()
    {
        System.Collections.Frozen.FrozenSet<string> merged = StopWords.CreateMergedSet(null);
        Assert.True(merged.Contains("the"));
    }
}
