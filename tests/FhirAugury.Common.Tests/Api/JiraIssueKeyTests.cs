using FhirAugury.Common.Api;

namespace FhirAugury.Common.Tests.Api;

public sealed class JiraIssueKeyTests
{
    [Theory]
    [InlineData("FHIR-50783", "FHIR-50783")]
    [InlineData("ballot-42", "BALLOT-42")]
    [InlineData("A-1", "A-1")]
    [InlineData("r5core-0007", "R5CORE-0007")]
    [InlineData("  up-796  ", "UP-796")]
    public void TryParseNormalizesGenericKeys(
        string input,
        string expected)
    {
        Assert.True(JiraIssueKey.TryParse(input, out JiraIssueKey? key));
        Assert.NotNull(key);
        Assert.Equal(expected, key.Value);
        Assert.Equal(
            $"https://jira.hl7.org/browse/{expected}",
            key.BrowseUrl);
        Assert.Equal(expected, key.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1FHIR-2")]
    [InlineData("FHIR")]
    [InlineData("FHIR-")]
    [InlineData("FHIR-A")]
    [InlineData("FHIR--1")]
    [InlineData("FHIR_1")]
    [InlineData("FHIR-1.2")]
    [InlineData("FHIR -1")]
    public void TryParseRejectsInvalidKeys(string? input)
    {
        Assert.False(JiraIssueKey.TryParse(input, out JiraIssueKey? key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData("BALLOT-12")]
    [InlineData("ballot-12")]
    [InlineData("J-9")]
    public void ValueFormatDetectorUsesSharedJiraPolicy(string value)
    {
        Assert.True(ValueFormatDetector.IsJiraKey(value));
        Assert.Equal(
            SourceSystems.Jira,
            ValueFormatDetector.DetectSourceType(value));
    }
}
