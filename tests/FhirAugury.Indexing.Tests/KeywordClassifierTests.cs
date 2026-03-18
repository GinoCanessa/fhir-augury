using FhirAugury.Indexing.Bm25;

namespace FhirAugury.Indexing.Tests;

public class KeywordClassifierTests
{
    [Theory]
    [InlineData("$validate", KeywordClassifier.TypeFhirOperation)]
    [InlineData("$expand", KeywordClassifier.TypeFhirOperation)]
    [InlineData("$everything", KeywordClassifier.TypeFhirOperation)]
    public void Classify_FhirOperation_ReturnsCorrectType(string token, string expected)
    {
        Assert.Equal(expected, KeywordClassifier.Classify(token));
    }

    [Theory]
    [InlineData("patient", KeywordClassifier.TypeFhirPath)]
    [InlineData("observation", KeywordClassifier.TypeFhirPath)]
    [InlineData("questionnaire", KeywordClassifier.TypeFhirPath)]
    public void Classify_FhirResourceName_ReturnsFhirPath(string token, string expected)
    {
        Assert.Equal(expected, KeywordClassifier.Classify(token));
    }

    [Fact]
    public void Classify_FhirElementPath_ReturnsFhirPath()
    {
        Assert.Equal(KeywordClassifier.TypeFhirPath, KeywordClassifier.Classify("patient.name.given"));
    }

    [Theory]
    [InlineData("the")]
    [InlineData("is")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("a")]
    [InlineData("in")]
    public void Classify_StopWords_ReturnsStopWord(string token)
    {
        Assert.Equal(KeywordClassifier.TypeStopWord, KeywordClassifier.Classify(token));
    }

    [Theory]
    [InlineData("ballot")]
    [InlineData("normative")]
    [InlineData("specification")]
    [InlineData("validation")]
    public void Classify_RegularWords_ReturnsWord(string token)
    {
        Assert.Equal(KeywordClassifier.TypeWord, KeywordClassifier.Classify(token));
    }
}
