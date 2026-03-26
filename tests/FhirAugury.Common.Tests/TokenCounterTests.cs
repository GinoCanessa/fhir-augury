using System.Collections.Frozen;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class TokenCounterTests
{
    [Fact]
    public void CountAndClassifyTokens_CountsTermFrequencies()
    {
        List<string> tokens = ["hello", "world", "hello", "hello"];

        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens(tokens);

        Assert.Equal(3, result["hello"].Count);
        Assert.Equal(1, result["world"].Count);
    }

    [Fact]
    public void CountAndClassifyTokens_FiltersDefaultStopWords()
    {
        List<string> tokens = ["the", "patient", "is", "here"];

        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens(tokens);

        // "the", "is" are stop words by default KeywordClassifier
        Assert.False(result.ContainsKey("the"));
        Assert.False(result.ContainsKey("is"));
        Assert.True(result.ContainsKey("patient"));
    }

    [Fact]
    public void CountAndClassifyTokens_FiltersCustomStopWords()
    {
        FrozenSet<string> customStopWords = new HashSet<string> { "custom", "filtered" }
            .ToFrozenSet(StringComparer.Ordinal);

        List<string> tokens = ["custom", "filtered", "kept"];

        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens(tokens, stopWords: customStopWords);

        Assert.False(result.ContainsKey("custom"));
        Assert.False(result.ContainsKey("filtered"));
        Assert.True(result.ContainsKey("kept"));
    }

    [Fact]
    public void CountAndClassifyTokens_AppliesLemmatization()
    {
        FrozenDictionary<string, string> lemmas = new Dictionary<string, string>
        {
            ["running"] = "run",
            ["patients"] = "patient",
        }.ToFrozenDictionary();
        Lemmatizer lemmatizer = new Lemmatizer(lemmas);

        List<string> tokens = ["running", "patients", "running"];

        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens(tokens, lemmatizer);

        // Lemmatized forms should be used as keys
        Assert.Equal(2, result["run"].Count);
        Assert.Equal(1, result["patient"].Count);
        Assert.False(result.ContainsKey("running"));
        Assert.False(result.ContainsKey("patients"));
    }

    [Fact]
    public void CountAndClassifyTokens_ClassifiesFhirTokens()
    {
        List<string> tokens = ["$validate", "patient"];

        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens(tokens);

        Assert.Equal("fhir_operation", result["$validate"].KeywordType);
        Assert.Equal("fhir_path", result["patient"].KeywordType);
    }

    [Fact]
    public void CountAndClassifyTokens_DoesNotLemmatizeFhirTokens()
    {
        FrozenDictionary<string, string> lemmas = new Dictionary<string, string>
        {
            ["patient"] = "patientlemma",
        }.ToFrozenDictionary();
        Lemmatizer lemmatizer = new Lemmatizer(lemmas);

        List<string> tokens = ["patient"];

        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens(tokens, lemmatizer);

        // FHIR tokens should NOT be lemmatized
        Assert.True(result.ContainsKey("patient"));
        Assert.False(result.ContainsKey("patientlemma"));
        Assert.Equal("fhir_path", result["patient"].KeywordType);
    }

    [Fact]
    public void CountAndClassifyTokens_HandlesEmptyInput()
    {
        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens([]);

        Assert.Empty(result);
    }

    [Fact]
    public void CountAndClassifyTokens_WithNullLemmatizer_WorksLikeNoLemmatization()
    {
        List<string> tokens = ["hello", "world"];

        Dictionary<string, (int Count, string KeywordType)> result =
            TokenCounter.CountAndClassifyTokens(tokens, lemmatizer: null);

        Assert.Equal(1, result["hello"].Count);
        Assert.Equal(1, result["world"].Count);
    }
}
