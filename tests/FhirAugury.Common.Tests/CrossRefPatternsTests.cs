using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class CrossRefPatternsTests
{
    [Fact]
    public void ExtractLinks_FindsJiraKey()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("See FHIR-43499 for details");
        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("FHIR-43499", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_FindsJiraUrl()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("Check https://jira.hl7.org/browse/FHIR-12345");
        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("FHIR-12345", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_FindsGitHubUrl()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("See https://github.com/HL7/fhir/issues/42");
        Assert.Single(links);
        Assert.Equal("github", links[0].TargetType, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("HL7/fhir#42", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_FindsGitHubShortRef()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks("Related to HL7/fhir#823");
        Assert.Single(links);
        Assert.Equal("github", links[0].TargetType, StringComparer.OrdinalIgnoreCase);
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

    // ── New Project Detection via Delegation ─────────────────────────

    [Theory]
    [InlineData("See BALLOT-100 here", "BALLOT-100")]
    [InlineData("See PSS-50 here", "PSS-50")]
    [InlineData("See UP-796 here", "UP-796")]
    public void ExtractLinks_FindsNewProjectKeys(string text, string expectedId)
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks(text);
        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType);
        Assert.Equal(expectedId, links[0].TargetId);
    }

    // ── Alias Detection via Delegation ───────────────────────────────

    [Theory]
    [InlineData("See JF-1234 here", "FHIR-1234")]
    [InlineData("See GF#5678 here", "FHIR-5678")]
    public void ExtractLinks_FindsJiraAliases(string text, string expectedId)
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks(text);
        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType);
        Assert.Equal(expectedId, links[0].TargetId);
    }

    // ── New URL Formats via Delegation ───────────────────────────────

    [Theory]
    [InlineData("See https://jira.hl7.org/projects/FHIR/issues/FHIR-54988", "FHIR-54988")]
    [InlineData("See https://jira.hl7.org/browse/BALLOT-42", "BALLOT-42")]
    public void ExtractLinks_FindsNewJiraUrlFormats(string url, string expectedId)
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks(url);
        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType);
        Assert.Equal(expectedId, links[0].TargetId);
    }

    // ── Mixed Cross-System ───────────────────────────────────────────

    [Fact]
    public void ExtractLinks_MixedCrossSystems()
    {
        List<CrossReference> links = CrossRefPatterns.ExtractLinks(
            "FHIR-100 and https://github.com/HL7/fhir/issues/42 and BALLOT-200");

        Assert.Equal(3, links.Count);
        Assert.Equal(2, links.Count(l => l.TargetType == "jira"));
        Assert.Equal(1, links.Count(l => l.TargetType == "github"));
    }
}
