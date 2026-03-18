using FhirAugury.Indexing;

namespace FhirAugury.Indexing.Tests;

public class CrossRefLinkerTests
{
    [Fact]
    public void ExtractLinks_JiraKey_MatchesPattern()
    {
        var links = CrossRefLinker.ExtractLinks("This references FHIR-12345 in the text.");

        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType);
        Assert.Equal("FHIR-12345", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_JiraUrl_MatchesPattern()
    {
        var links = CrossRefLinker.ExtractLinks("See https://jira.hl7.org/browse/FHIR-43499 for details.");

        Assert.Single(links);
        Assert.Equal("jira", links[0].TargetType);
        Assert.Equal("FHIR-43499", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_JiraUrlAndKey_Deduplicates()
    {
        var links = CrossRefLinker.ExtractLinks(
            "Issue FHIR-43499 is at https://jira.hl7.org/browse/FHIR-43499");

        Assert.Single(links);
        Assert.Equal("FHIR-43499", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_ZulipUrl_ExtractsStreamAndTopic()
    {
        var links = CrossRefLinker.ExtractLinks(
            "Discussed at https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Patient%20resource");

        Assert.Single(links);
        Assert.Equal("zulip", links[0].TargetType);
        Assert.Equal("179166:Patient resource", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_GitHubIssueUrl_MatchesPattern()
    {
        var links = CrossRefLinker.ExtractLinks(
            "Fixed in https://github.com/HL7/fhir/issues/42");

        Assert.Single(links);
        Assert.Equal("github", links[0].TargetType);
        Assert.Equal("HL7/fhir#42", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_ConfluenceUrl_MatchesPattern()
    {
        var links = CrossRefLinker.ExtractLinks(
            "Documentation at https://confluence.hl7.org/display/FHIR/12345");

        Assert.Single(links);
        Assert.Equal("confluence", links[0].TargetType);
        Assert.Equal("12345", links[0].TargetId);
    }

    [Fact]
    public void ExtractLinks_MultiplePatterns_ExtractsAll()
    {
        var text = "Issue FHIR-100 mentions https://github.com/HL7/fhir/issues/50 and FHIR-200";
        var links = CrossRefLinker.ExtractLinks(text);

        Assert.Equal(3, links.Count);
        Assert.Contains(links, l => l.TargetType == "jira" && l.TargetId == "FHIR-100");
        Assert.Contains(links, l => l.TargetType == "jira" && l.TargetId == "FHIR-200");
        Assert.Contains(links, l => l.TargetType == "github" && l.TargetId == "HL7/fhir#50");
    }

    [Fact]
    public void ExtractLinks_NoMatches_ReturnsEmpty()
    {
        var links = CrossRefLinker.ExtractLinks("Just some plain text with no references.");
        Assert.Empty(links);
    }

    [Fact]
    public void ExtractLinks_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(CrossRefLinker.ExtractLinks(""));
        Assert.Empty(CrossRefLinker.ExtractLinks("   "));
    }

    [Fact]
    public void GetSurroundingText_MiddleOfText_ExtractsContext()
    {
        var text = "Lorem ipsum dolor sit amet, FHIR-12345 is referenced here, consectetur adipiscing elit.";
        var matchIndex = text.IndexOf("FHIR-12345");

        var context = CrossRefLinker.GetSurroundingText(text, matchIndex, 40);

        Assert.Contains("FHIR-12345", context);
        Assert.True(context.Length <= 50); // 40 + some ellipsis
    }

    [Fact]
    public void GetSurroundingText_StartOfText_NoPrefixEllipsis()
    {
        var text = "FHIR-12345 at the start";
        var context = CrossRefLinker.GetSurroundingText(text, 0, 40);

        Assert.StartsWith("FHIR-12345", context);
    }

    [Fact]
    public void ExtractLinks_DuplicateJiraKeys_OnlyFirstKept()
    {
        var links = CrossRefLinker.ExtractLinks("FHIR-100 and again FHIR-100 and FHIR-100");

        Assert.Single(links);
        Assert.Equal("FHIR-100", links[0].TargetId);
    }
}
