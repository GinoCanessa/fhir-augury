using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class JiraRefExtractorTests
{
    private const string Repo = "HL7/fhir";

    [Fact]
    public void ExtractReferences_FhirPattern_ExtractsCorrectly()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "Fix for FHIR-43499 and FHIR-12345",
            Repo, hasIssues: true, [], null);

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-43499");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-12345");
    }

    [Fact]
    public void ExtractReferences_JfPattern_ExtractsCorrectly()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "Related to JF-1234",
            Repo, hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("JF-1234", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractReferences_GfPattern_ExtractsCorrectly()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "See GF-5678 for details",
            Repo, hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("GF-5678", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractReferences_JHash_NormalizesToFhir()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "As per J#999",
            Repo, hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("FHIR-999", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractReferences_BareHash_HasIssuesTrue_MatchesGitHub_Skipped()
    {
        var githubNumbers = new HashSet<int> { 42 };

        var refs = JiraRefExtractor.ExtractReferences(
            "See #42 for context",
            Repo, hasIssues: true, githubNumbers, null);

        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractReferences_BareHash_HasIssuesTrue_NoGitHubMatch_ExtractsAsJira()
    {
        var githubNumbers = new HashSet<int> { 1, 2, 3 };

        var refs = JiraRefExtractor.ExtractReferences(
            "See #9999 for context",
            Repo, hasIssues: true, githubNumbers, null);

        Assert.Single(refs);
        Assert.Equal("FHIR-9999", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractReferences_BareHash_HasIssuesFalse_AllTreatedAsJira()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "References #100, #200, #300",
            Repo, hasIssues: false, [], null);

        Assert.Equal(3, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-100");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-200");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-300");
    }

    [Fact]
    public void ExtractReferences_ValidJiraNumbers_FiltersJHashAndBareHash()
    {
        // validJiraNumbers only affects J#N and bare #NNN patterns, not explicit FHIR-N
        var validNumbers = new HashSet<int> { 42 };

        var refs = JiraRefExtractor.ExtractReferences(
            "J#42 and J#9999 and #100",
            Repo, hasIssues: false, [], validNumbers);

        // J#42 → FHIR-42 (valid), J#9999 → FHIR-9999 (invalid), #100 → FHIR-100 (invalid)
        Assert.Single(refs);
        Assert.Equal("FHIR-42", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractReferences_Deduplication_NoDuplicates()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "FHIR-100 is related to FHIR-100 and also FHIR-100",
            Repo, hasIssues: false, [], null);

        Assert.Single(refs);
    }

    [Fact]
    public void ExtractReferences_EmptyText_ReturnsEmpty()
    {
        var refs = JiraRefExtractor.ExtractReferences("", Repo, hasIssues: false, [], null);
        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractReferences_NullText_ReturnsEmpty()
    {
        var refs = JiraRefExtractor.ExtractReferences(null!, Repo, hasIssues: false, [], null);
        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractReferences_Context_ExtractsSurroundingText()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "This is some surrounding context text about FHIR-42 and more text after it",
            Repo, hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.NotEmpty(refs[0].Context!);
        Assert.Contains("FHIR-42", refs[0].Context);
    }

    [Fact]
    public void ExtractReferences_MixedPatterns_AllExtracted()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "FHIR-100 and JF-200 and GF-300 mentioned together",
            Repo, hasIssues: false, [], null);

        Assert.Equal(3, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-100");
        Assert.Contains(refs, r => r.JiraKey == "JF-200");
        Assert.Contains(refs, r => r.JiraKey == "GF-300");
    }

    [Fact]
    public void ExtractReferences_JHashValidation_FiltersInvalid()
    {
        var validNumbers = new HashSet<int> { 42 };

        var refs = JiraRefExtractor.ExtractReferences(
            "J#42 and J#9999",
            Repo, hasIssues: false, [], validNumbers);

        Assert.Single(refs);
        Assert.Equal("FHIR-42", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractReferences_RepoFullName_IsSet()
    {
        var refs = JiraRefExtractor.ExtractReferences(
            "FHIR-12345",
            "HL7/us-core", hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("HL7/us-core", refs[0].RepoFullName);
    }
}
