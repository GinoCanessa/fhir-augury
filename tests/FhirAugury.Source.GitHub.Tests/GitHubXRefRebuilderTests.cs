using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubXRefRebuilderTests
{
    [Fact]
    public void ExtractJiraReferences_FhirPattern_ExtractsCorrectly()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "Fix for FHIR-43499 and FHIR-12345",
            "issue", "HL7/fhir#1", null);

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-43499");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-12345");
    }

    [Fact]
    public void ExtractJiraReferences_JfPattern_ExtractsCorrectly()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "Related to JF-1234",
            "issue", "HL7/fhir#1", null);

        Assert.Single(refs);
        Assert.Equal("FHIR-1234", refs[0].JiraKey);
        Assert.Equal("JF-1234", refs[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractJiraReferences_GfPattern_ExtractsCorrectly()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "See GF-5678 for details",
            "issue", "HL7/fhir#1", null);

        Assert.Single(refs);
        Assert.Equal("FHIR-5678", refs[0].JiraKey);
        Assert.Equal("GF-5678", refs[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractJiraReferences_JHash_NormalizesToFhir()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "As per J#999",
            "issue", "HL7/fhir#1", null);

        Assert.Single(refs);
        Assert.Equal("FHIR-999", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_BareHash_IsIgnored()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "See #42 and #9999 for context",
            "issue", "HL7/fhir#1", null);

        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractJiraReferences_JHashValidation_FiltersInvalid()
    {
        HashSet<int> validNumbers = [42];

        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "J#42 and J#9999",
            "issue", "HL7/fhir#1", validNumbers);

        Assert.Single(refs);
        Assert.Equal("FHIR-42", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractJiraReferences_Deduplication_NoDuplicates()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "FHIR-100 is related to FHIR-100 and also FHIR-100",
            "issue", "HL7/fhir#1", null);

        Assert.Single(refs);
    }

    [Fact]
    public void ExtractJiraReferences_EmptyText_ReturnsEmpty()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "", "issue", "HL7/fhir#1", null);
        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractJiraReferences_NullText_ReturnsEmpty()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            null!, "issue", "HL7/fhir#1", null);
        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractJiraReferences_Context_ExtractsSurroundingText()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "This is some surrounding context text about FHIR-42 and more text after it",
            "issue", "HL7/fhir#1", null);

        Assert.Single(refs);
        Assert.NotEmpty(refs[0].Context!);
        Assert.Contains("FHIR-42", refs[0].Context);
    }

    [Fact]
    public void ExtractJiraReferences_MixedPatterns_AllExtracted()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "FHIR-100 and JF-200 and GF-300 mentioned together",
            "issue", "HL7/fhir#1", null);

        Assert.Equal(3, refs.Count);
        Assert.Contains(refs, r => r.JiraKey == "FHIR-100");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-200");
        Assert.Contains(refs, r => r.JiraKey == "FHIR-300");
        Assert.Contains(refs, r => r.OriginalLiteral == "FHIR-100");
        Assert.Contains(refs, r => r.OriginalLiteral == "JF-200");
        Assert.Contains(refs, r => r.OriginalLiteral == "GF-300");
    }

    [Fact]
    public void ExtractJiraReferences_SourceFields_AreSet()
    {
        List<JiraXRefRecord> refs = GitHubXRefRebuilder.ExtractJiraReferences(
            "FHIR-12345",
            "issue", "HL7/us-core#1", null);

        Assert.Single(refs);
        Assert.Equal("issue", refs[0].ContentType);
        Assert.Equal("HL7/us-core#1", refs[0].SourceId);
        Assert.Equal("mentions", refs[0].LinkType);
    }
}
