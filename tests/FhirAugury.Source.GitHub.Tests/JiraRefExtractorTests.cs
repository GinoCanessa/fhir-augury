using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class JiraRefExtractorTests
{
    [Fact]
    public void ExtractJiraReferences_FhirPattern_ExtractsCorrectly()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "Fix for FHIR-43499 and FHIR-12345",
            "issue", "HL7/fhir#1", hasIssues: true, [], null);

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-43499");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-12345");
    }

    [Fact]
    public void ExtractJiraReferences_JfPattern_ExtractsCorrectly()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "Related to JF-1234",
            "issue", "HL7/fhir#1", hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("JF-1234", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_GfPattern_ExtractsCorrectly()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "See GF-5678 for details",
            "issue", "HL7/fhir#1", hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("GF-5678", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_JHash_NormalizesToFhir()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "As per J#999",
            "issue", "HL7/fhir#1", hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("FHIR-999", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_BareHash_HasIssuesTrue_MatchesGitHub_Skipped()
    {
        HashSet<int> githubNumbers = new HashSet<int> { 42 };

        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "See #42 for context",
            "issue", "HL7/fhir#1", hasIssues: true, githubNumbers, null);

        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractJiraReferences_BareHash_HasIssuesTrue_NoGitHubMatch_ExtractsAsJira()
    {
        HashSet<int> githubNumbers = new HashSet<int> { 1, 2, 3 };

        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "See #9999 for context",
            "issue", "HL7/fhir#1", hasIssues: true, githubNumbers, null);

        Assert.Single(refs);
        Assert.Equal("FHIR-9999", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_BareHash_HasIssuesFalse_AllTreatedAsJira()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "References #100, #200, #300",
            "issue", "HL7/fhir#1", hasIssues: false, [], null);

        Assert.Equal(3, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-100");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-200");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-300");
    }

    [Fact]
    public void ExtractJiraReferences_ValidJiraNumbers_FiltersJHashAndBareHash()
    {
        // validJiraNumbers only affects J#N and bare #NNN patterns, not explicit FHIR-N
        HashSet<int> validNumbers = new HashSet<int> { 42 };

        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "J#42 and J#9999 and #100",
            "issue", "HL7/fhir#1", hasIssues: false, [], validNumbers);

        // J#42 → FHIR-42 (valid), J#9999 → FHIR-9999 (invalid), #100 → FHIR-100 (invalid)
        Assert.Single(refs);
        Assert.Equal("FHIR-42", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_Deduplication_NoDuplicates()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "FHIR-100 is related to FHIR-100 and also FHIR-100",
            "issue", "HL7/fhir#1", hasIssues: false, [], null);

        Assert.Single(refs);
    }

    [Fact]
    public void ExtractJiraReferences_EmptyText_ReturnsEmpty()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "", "issue", "HL7/fhir#1", hasIssues: false, [], null);
        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractJiraReferences_NullText_ReturnsEmpty()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            null!, "issue", "HL7/fhir#1", hasIssues: false, [], null);
        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractJiraReferences_Context_ExtractsSurroundingText()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "This is some surrounding context text about FHIR-42 and more text after it",
            "issue", "HL7/fhir#1", hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.NotEmpty(refs[0].Context!);
        Assert.Contains("FHIR-42", refs[0].Context);
    }

    [Fact]
    public void ExtractJiraReferences_MixedPatterns_AllExtracted()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "FHIR-100 and JF-200 and GF-300 mentioned together",
            "issue", "HL7/fhir#1", hasIssues: false, [], null);

        Assert.Equal(3, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-100");
        Assert.Contains(refs, r => r.JiraKey == "JF-200");
        Assert.Contains(refs, r => r.JiraKey == "GF-300");
    }

    [Fact]
    public void ExtractJiraReferences_JHashValidation_FiltersInvalid()
    {
        HashSet<int> validNumbers = new HashSet<int> { 42 };

        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "J#42 and J#9999",
            "issue", "HL7/fhir#1", hasIssues: false, [], validNumbers);

        Assert.Single(refs);
        Assert.Equal("FHIR-42", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_SourceFields_AreSet()
    {
        List<JiraXRefRecord> refs = JiraRefExtractor.ExtractJiraReferences(
            "FHIR-12345",
            "issue", "HL7/us-core#1", hasIssues: false, [], null);

        Assert.Single(refs);
        Assert.Equal("issue", refs[0].ContentType);
        Assert.Equal("HL7/us-core#1", refs[0].SourceId);
        Assert.Equal("mentions", refs[0].LinkType);
    }
}
