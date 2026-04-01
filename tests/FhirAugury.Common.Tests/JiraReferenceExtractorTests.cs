using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;

namespace FhirAugury.Common.Tests;

public class JiraReferenceExtractorTests
{
    [Fact]
    public void ExtractsFhirNKey()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "See FHIR-12345 for details");
        Assert.Single(refs);
        Assert.Equal("FHIR-12345", refs[0].JiraKey);
    }

    [Fact]
    public void ExtractsJfNKey()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "Tracked as JF-100");
        Assert.Single(refs);
        Assert.Equal("FHIR-100", refs[0].JiraKey);
        Assert.Equal("JF-100", refs[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractsGfNKey()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "Filed GF-200");
        Assert.Single(refs);
        Assert.Equal("FHIR-200", refs[0].JiraKey);
        Assert.Equal("GF-200", refs[0].OriginalLiteral);
    }

    [Fact]
    public void ExtractsJiraUrl()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "page", "p1", null, "See https://jira.hl7.org/browse/FHIR-999 here");
        Assert.Single(refs);
        Assert.Equal("FHIR-999", refs[0].JiraKey);
    }

    [Fact]
    public void DeduplicatesUrlAndKey()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "page", "p1", null, "FHIR-999 and https://jira.hl7.org/browse/FHIR-999");
        Assert.Single(refs);
    }

    [Fact]
    public void ExtractsMultipleKeys()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "page", "p1", null, "FHIR-1 and FHIR-2");
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void JHashWithValidNumber()
    {
        CrossRefExtractionContext ctx = new(ValidJiraNumbers: [500]);
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "issue", "1", ctx, "J#500");
        Assert.Single(refs);
        Assert.Equal("FHIR-500", refs[0].JiraKey);
    }

    [Fact]
    public void JHashWithInvalidNumber()
    {
        CrossRefExtractionContext ctx = new(ValidJiraNumbers: [600]);
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "issue", "1", ctx, "J#500");
        Assert.Empty(refs);
    }

    [Fact]
    public void MultipleTextsMerged()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "issue", "1", null, "FHIR-1", "FHIR-2");
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void SameKeyAcrossTextsDeduped()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "issue", "1", null, "FHIR-1", "FHIR-1");
        Assert.Single(refs);
    }

    [Fact]
    public void EmptyTextReturnsEmpty()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "");
        Assert.Empty(refs);
    }

    [Fact]
    public void ContextIsPopulated()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences(
            "issue", "1", null, "This is some text about FHIR-12345 that should give context");
        Assert.Single(refs);
        Assert.NotNull(refs[0].Context);
        Assert.NotEmpty(refs[0].Context!);
    }

    [Fact]
    public void TargetTypeIsJira()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "FHIR-42");
        Assert.Equal(SourceSystems.Jira, refs[0].TargetType);
    }

    [Fact]
    public void TargetIdEqualsJiraKey()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "FHIR-42");
        Assert.Equal("FHIR-42", refs[0].TargetId);
    }

    [Fact]
    public void OriginalLiteralPreservedForFhirKey()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "See FHIR-12345 for details");
        Assert.Single(refs);
        Assert.Equal("FHIR-12345", refs[0].OriginalLiteral);
    }

    [Fact]
    public void OriginalLiteralPreservedForJHash()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("issue", "1", null, "J#500");
        Assert.Single(refs);
        Assert.Equal("J#500", refs[0].OriginalLiteral);
    }

    [Fact]
    public void SourceFieldsSet()
    {
        List<JiraXRefRecord> refs = JiraReferenceExtractor.GetReferences("page", "p99", null, "FHIR-1");
        Assert.Equal("page", refs[0].ContentType);
        Assert.Equal("p99", refs[0].SourceId);
        Assert.Equal("mentions", refs[0].LinkType);
    }
}
