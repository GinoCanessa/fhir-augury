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

    // ── New Project Detection ────────────────────────────────────────

    [Theory]
    [InlineData("See BALLOT-100 here", "BALLOT-100", "BALLOT-100")]
    [InlineData("See BALLOT#100 here", "BALLOT-100", "BALLOT#100")]
    [InlineData("See PSS-50 here", "PSS-50", "PSS-50")]
    [InlineData("See PSS#50 here", "PSS-50", "PSS#50")]
    [InlineData("See UP-796 here", "UP-796", "UP-796")]
    [InlineData("See UP#796 here", "UP-796", "UP#796")]
    public void ExtractTickets_NewProjects_Detected(string text, string expectedKey, string expectedOriginal)
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(text);

        Assert.Single(results);
        Assert.Equal(expectedKey, results[0].JiraKey);
        Assert.Equal(expectedOriginal, results[0].OriginalLiteral);
    }

    // ── New URL Formats ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://jira.hl7.org/projects/FHIR/issues/FHIR-54988", "FHIR-54988")]
    [InlineData("https://jira.hl7.org/projects/UP/issues/UP-796", "UP-796")]
    [InlineData("https://jira.hl7.org/projects/BALLOT/issues/BALLOT-42", "BALLOT-42")]
    [InlineData("https://jira.hl7.org/projects/PSS/issues/PSS-100", "PSS-100")]
    [InlineData("https://jira.hl7.org/browse/BALLOT-42", "BALLOT-42")]
    [InlineData("https://jira.hl7.org/browse/PSS-50", "PSS-50")]
    [InlineData("https://jira.hl7.org/browse/UP-796", "UP-796")]
    public void ExtractTickets_UrlFormats_Detected(string url, string expectedKey)
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets($"See {url} for info");

        Assert.Single(results);
        Assert.Equal(expectedKey, results[0].JiraKey);
    }

    // ── Cross-Project & Deduplication ────────────────────────────────

    [Fact]
    public void ExtractTickets_CrossProject_NotDeduplicated()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("FHIR-100 and BALLOT-100");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.JiraKey == "FHIR-100");
        Assert.Contains(results, r => r.JiraKey == "BALLOT-100");
    }

    [Fact]
    public void ExtractTickets_CrossProject_MixedText_AllExtracted()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "FHIR-100, BALLOT-200, PSS-300, UP-400 all referenced");

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.JiraKey == "FHIR-100");
        Assert.Contains(results, r => r.JiraKey == "BALLOT-200");
        Assert.Contains(results, r => r.JiraKey == "PSS-300");
        Assert.Contains(results, r => r.JiraKey == "UP-400");
    }

    [Fact]
    public void ExtractTickets_UrlAndKey_NewProject_Deduplicated()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "https://jira.hl7.org/browse/BALLOT-42 and BALLOT-42");

        Assert.Single(results);
        Assert.Equal("BALLOT-42", results[0].JiraKey);
    }

    // ── Word Boundary Edge Cases ─────────────────────────────────────

    [Theory]
    [InlineData("SETUP-100")]
    [InlineData("UPON something")]
    [InlineData("IMPRESS-5")]
    [InlineData("JFUNCTION-3")]
    public void ExtractTickets_WordBoundary_NoFalsePositives(string text)
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(text);

        Assert.Empty(results);
    }

    // ── Case Insensitivity ───────────────────────────────────────────

    [Theory]
    [InlineData("ballot-100", "BALLOT-100")]
    [InlineData("up-796", "UP-796")]
    [InlineData("pss-50", "PSS-50")]
    [InlineData("fhir-42", "FHIR-42")]
    public void ExtractTickets_CaseInsensitive_CanonicalUppercase(string text, string expectedKey)
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(text);

        Assert.Single(results);
        Assert.Equal(expectedKey, results[0].JiraKey);
    }

    // ── Validation Filter Scoping ────────────────────────────────────

    [Fact]
    public void ExtractTickets_ValidJiraNumbers_DoesNotFilterNewProjects()
    {
        HashSet<int> valid = [42];

        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets(
            "BALLOT-9999 and UP-9999 and PSS-9999", valid);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ExtractTickets_ValidJiraNumbers_StillFiltersJHash()
    {
        HashSet<int> valid = [42];

        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("J#9999", valid);

        Assert.Empty(results);
    }

    // ── Newly Matched Patterns (consolidation additions) ─────────────

    [Fact]
    public void ExtractTickets_JfHash_NormalizesToFhir()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("See JF#1234");

        Assert.Single(results);
        Assert.Equal("FHIR-1234", results[0].JiraKey);
        Assert.Equal("JF#1234", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_JDash_NormalizesToFhir()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("See J-1234");

        Assert.Single(results);
        Assert.Equal("FHIR-1234", results[0].JiraKey);
        Assert.Equal("J-1234", results[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractTickets_FhirHash_NormalizesToFhir()
    {
        List<JiraTicketMatch> results = JiraTicketExtractor.ExtractTickets("See FHIR#5678");

        Assert.Single(results);
        Assert.Equal("FHIR-5678", results[0].JiraKey);
        Assert.Equal("FHIR#5678", results[0].OriginalLiteral);
    }
}
