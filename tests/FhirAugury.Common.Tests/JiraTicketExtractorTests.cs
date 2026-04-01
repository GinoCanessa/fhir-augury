using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class JiraTicketExtractorTests
{
    // ── Core Pattern Detection ───────────────────────────────────────

    [Fact]
    public void ExtractTickets_FhirKey_Extracts()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("See FHIR-43499 for details");

        Assert.Single(results);
        Assert.Equal("FHIR-43499", results[0].JiraKey);
    }

    [Fact]
    public void ExtractTickets_JfKey_Extracts()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("Related to JF-1234");

        Assert.Single(results);
        Assert.Equal("FHIR-1234", results[0].JiraKey);
        Assert.Equal("JF-1234", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_GfKey_Extracts()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("See GF-5678 for details");

        Assert.Single(results);
        Assert.Equal("FHIR-5678", results[0].JiraKey);
        Assert.Equal("GF-5678", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_JHash_NormalizesToFhir()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("As per J#999");

        Assert.Single(results);
        Assert.Equal("FHIR-999", results[0].JiraKey);
    }

    [Fact]
    public void ExtractTickets_GfHash_NormalizesToFhir()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("Check GF#456 please");

        Assert.Single(results);
        Assert.Equal("FHIR-456", results[0].JiraKey);
        Assert.Equal("GF#456", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_JiraUrl_Extracts()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "See https://jira.hl7.org/browse/FHIR-12345 for info");

        Assert.Single(results);
        Assert.Equal("FHIR-12345", results[0].JiraKey);
    }

    [Fact]
    public void ExtractTickets_FhirKey_OriginalMatchesNormalized()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("See FHIR-43499 for details");

        Assert.Single(results);
        Assert.Equal("FHIR-43499", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_JHash_OriginalPreserved()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("As per J#999");

        Assert.Single(results);
        Assert.Equal("J#999", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_JiraUrl_OriginalMatchesNormalized()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "See https://jira.hl7.org/browse/FHIR-12345 for info");

        Assert.Single(results);
        Assert.Equal("FHIR-12345", results[0].OriginalLiteral);
    }

    // ── Deduplication ────────────────────────────────────────────────

    [Fact]
    public void ExtractTickets_DuplicateKeys_Deduplicated()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "FHIR-100 is referenced here and also FHIR-100 again");

        Assert.Single(results);
    }

    [Fact]
    public void ExtractTickets_UrlAndKey_Deduplicated()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "Check https://jira.hl7.org/browse/FHIR-100 and also FHIR-100");

        Assert.Single(results);
        Assert.Equal("FHIR-100", results[0].JiraKey);
    }

    [Fact]
    public void ExtractTickets_JHashAndFhirKey_Deduplicated()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "J#100 is the same as FHIR-100");

        Assert.Single(results);
        Assert.Equal("FHIR-100", results[0].JiraKey);
    }

    [Fact]
    public void ExtractTickets_GfKeyAndFhirKey_Deduplicated()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "GF-100 is the same as FHIR-100");

        Assert.Single(results);
        Assert.Equal("FHIR-100", results[0].JiraKey);
    }

    [Fact]
    public void ExtractTickets_JfKeyAndFhirKey_Deduplicated()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "JF-100 is the same as FHIR-100");

        Assert.Single(results);
        Assert.Equal("FHIR-100", results[0].JiraKey);
    }

    // ── Mixed Patterns ───────────────────────────────────────────────

    [Fact]
    public void ExtractTickets_MixedPatterns_AllExtracted()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "FHIR-100, JF-200, GF-300, J#400, GF#500 all referenced");

        Assert.Equal(5, results.Count);
        Assert.Contains(results, r => r.JiraKey == "FHIR-100");
        Assert.Contains(results, r => r.JiraKey == "FHIR-200");
        Assert.Contains(results, r => r.JiraKey == "FHIR-300");
        Assert.Contains(results, r => r.JiraKey == "FHIR-400");
        Assert.Contains(results, r => r.JiraKey == "FHIR-500");

        Assert.Contains(results, r => r.OriginalLiteral == "FHIR-100");
        Assert.Contains(results, r => r.OriginalLiteral == "JF-200");
        Assert.Contains(results, r => r.OriginalLiteral == "GF-300");
        Assert.Contains(results, r => r.OriginalLiteral == "J#400");
        Assert.Contains(results, r => r.OriginalLiteral == "GF#500");
    }

    [Fact]
    public void ExtractTickets_MultipleInOneSentence()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "This ballot comment references FHIR-100 and FHIR-200 as related items");

        Assert.Equal(2, results.Count);
    }

    // ── Validation ───────────────────────────────────────────────────

    [Fact]
    public void ExtractTickets_ValidJiraNumbers_FiltersJHash()
    {
        HashSet<int> valid = [42];

        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("J#42 and J#9999", valid);

        Assert.Single(results);
        Assert.Equal("FHIR-42", results[0].JiraKey);
    }

    [Fact]
    public void ExtractTickets_ValidJiraNumbers_FiltersGfHash()
    {
        HashSet<int> valid = [42];

        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("GF#42 and GF#9999", valid);

        Assert.Single(results);
        Assert.Equal("FHIR-42", results[0].JiraKey);
        Assert.Equal("GF#42", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_ValidJiraNumbers_DoesNotFilterExplicitKeys()
    {
        HashSet<int> valid = [42];

        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("FHIR-9999 stays", valid);

        Assert.Single(results);
        Assert.Equal("FHIR-9999", results[0].JiraKey);
    }

    // ── Edge Cases ───────────────────────────────────────────────────

    [Fact]
    public void ExtractTickets_EmptyText_ReturnsEmpty()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("");
        Assert.Empty(results);
    }

    [Fact]
    public void ExtractTickets_NullText_ReturnsEmpty()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(null!);
        Assert.Empty(results);
    }

    [Fact]
    public void ExtractTickets_NoMatches_ReturnsEmpty()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("No ticket references here");
        Assert.Empty(results);
    }

    [Fact]
    public void ExtractTickets_FhirKeyInUrl_NoDuplicate()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "https://jira.hl7.org/browse/FHIR-42 has the details");

        Assert.Single(results);
        Assert.Equal("FHIR-42", results[0].JiraKey);
    }

    // ── Context ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractTickets_Context_ContainsMatch()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "This is some surrounding context text about FHIR-42 and more text after it");

        Assert.Single(results);
        Assert.Contains("FHIR-42", results[0].Context);
    }

    [Fact]
    public void ExtractTickets_Context_LimitedLength()
    {
        string longText = new string('x', 500) + " FHIR-42 " + new string('y', 500);
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(longText);

        Assert.Single(results);
        // Context should be roughly 160 chars + possible ellipsis markers (~6 chars)
        Assert.True(results[0].Context.Length <= 170, $"Context too long: {results[0].Context.Length}");
    }
}
