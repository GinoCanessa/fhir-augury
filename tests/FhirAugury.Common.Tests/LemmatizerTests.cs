using System.Collections.Frozen;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class LemmatizerTests
{
    [Fact]
    public void Lemmatize_ReturnsLemmaWhenFound()
    {
        FrozenDictionary<string, string> lemmas = new Dictionary<string, string>
        {
            ["running"] = "run",
            ["patients"] = "patient",
            ["observations"] = "observation",
        }.ToFrozenDictionary();

        Lemmatizer lemmatizer = new Lemmatizer(lemmas);

        Assert.Equal("run", lemmatizer.Lemmatize("running"));
        Assert.Equal("patient", lemmatizer.Lemmatize("patients"));
        Assert.Equal("observation", lemmatizer.Lemmatize("observations"));
    }

    [Fact]
    public void Lemmatize_ReturnsOriginalWhenNotFound()
    {
        FrozenDictionary<string, string> lemmas = new Dictionary<string, string>
        {
            ["running"] = "run",
        }.ToFrozenDictionary();

        Lemmatizer lemmatizer = new Lemmatizer(lemmas);

        Assert.Equal("unknown", lemmatizer.Lemmatize("unknown"));
        Assert.Equal("fhir", lemmatizer.Lemmatize("fhir"));
    }

    [Fact]
    public void TryLemmatize_ReturnsTrueAndLemmaWhenFound()
    {
        FrozenDictionary<string, string> lemmas = new Dictionary<string, string>
        {
            ["running"] = "run",
        }.ToFrozenDictionary();

        Lemmatizer lemmatizer = new Lemmatizer(lemmas);

        bool result = lemmatizer.TryLemmatize("running", out string lemma);
        Assert.True(result);
        Assert.Equal("run", lemma);
    }

    [Fact]
    public void TryLemmatize_ReturnsFalseAndOriginalWhenNotFound()
    {
        Lemmatizer lemmatizer = Lemmatizer.Empty;

        bool result = lemmatizer.TryLemmatize("hello", out string lemma);
        Assert.False(result);
        Assert.Equal("hello", lemma);
    }

    [Fact]
    public void Empty_HasZeroCount()
    {
        Assert.Equal(0, Lemmatizer.Empty.Count);
    }
}
