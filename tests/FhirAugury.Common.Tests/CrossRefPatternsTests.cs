using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class CrossRefPatternsTests
{
    [Fact]
    public void ExtractLinks_FindsJiraKey()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("See FHIR-43499 for details");
        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType);
        Assert.Equal("FHIR-43499", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_FindsJiraUrl()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("Check https://jira.hl7.org/browse/FHIR-12345");
        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType);
        Assert.Equal("FHIR-12345", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_FindsGitHubUrl()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("See https://github.com/HL7/fhir/issues/42");
        Assert.Single(links);
        Assert.Equal("github", links[0].TargetType);
        Assert.Equal("HL7/fhir#42", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_FindsGitHubShortRef()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("Related to HL7/fhir#823");
        Assert.Single(links);
        Assert.Equal("github", links[0].TargetType);
        Assert.Equal("HL7/fhir#823", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_DeduplicatesJiraUrlAndKey()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks(
            "See https://jira.hl7.org/browse/FHIR-100 and also FHIR-100");
        Assert.Single(links);
    }

    [Fact]
    public void ExtractLinks_ReturnsEmptyForNoMatches()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("No cross references here");
        Assert.Empty(links);
    }
}
