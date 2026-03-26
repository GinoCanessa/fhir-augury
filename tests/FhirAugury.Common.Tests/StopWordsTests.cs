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
        IReadOnlySet<string> merged = StopWords.CreateMergedSet();
        Assert.Contains("the", merged);
        Assert.Contains("is", merged);
        Assert.Contains("using", merged);
    }

    [Fact]
    public void CreateMergedSet_IncludesAdditionalWords()
    {
        IReadOnlySet<string> merged = StopWords.CreateMergedSet(["custom1", "custom2"]);
        Assert.Contains("custom1", merged);
        Assert.Contains("custom2", merged);
        // Still contains defaults
        Assert.Contains("the", merged);
    }

    [Fact]
    public void CreateMergedSet_HandlesNull()
    {
        IReadOnlySet<string> merged = StopWords.CreateMergedSet(null);
        Assert.Contains("the", merged);
    }
}
