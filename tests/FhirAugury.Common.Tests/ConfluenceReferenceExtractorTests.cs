using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;

namespace FhirAugury.Common.Tests;

public class ConfluenceReferenceExtractorTests
{
    [Fact]
    public void ExtractsConfluencePageUrl()
    {
        List<ConfluenceXRefRecord> refs = ConfluenceReferenceExtractor.GetReferences(
            "issue", "1", "See https://confluence.hl7.org/display/FHIR/123456 for details");
        Assert.Single(refs);
        Assert.Equal("123456", refs[0].PageId);
    }

    [Fact]
    public void DeduplicatesSamePage()
    {
        List<ConfluenceXRefRecord> refs = ConfluenceReferenceExtractor.GetReferences(
            "issue", "1",
            "See https://confluence.hl7.org/display/FHIR/123456 and https://confluence.hl7.org/pages/viewpage.action?pageId=123456");
        Assert.Single(refs);
    }

    [Fact]
    public void TargetIdEqualsPageId()
    {
        List<ConfluenceXRefRecord> refs = ConfluenceReferenceExtractor.GetReferences(
            "issue", "1", "https://confluence.hl7.org/display/FHIR/123456");
        Assert.Equal("123456", refs[0].TargetId);
    }

    [Fact]
    public void TargetTypeIsConfluence()
    {
        List<ConfluenceXRefRecord> refs = ConfluenceReferenceExtractor.GetReferences(
            "issue", "1", "https://confluence.hl7.org/display/FHIR/123456");
        Assert.Equal("confluence", refs[0].TargetType);
    }

    [Fact]
    public void EmptyTextReturnsEmpty()
    {
        List<ConfluenceXRefRecord> refs = ConfluenceReferenceExtractor.GetReferences("issue", "1", "");
        Assert.Empty(refs);
    }

    [Fact]
    public void SourceFieldsSet()
    {
        List<ConfluenceXRefRecord> refs = ConfluenceReferenceExtractor.GetReferences(
            "message", "42", "https://confluence.hl7.org/display/FHIR/123456");
        Assert.Equal("message", refs[0].SourceType);
        Assert.Equal("42", refs[0].SourceId);
        Assert.Equal("mentions", refs[0].LinkType);
    }

    [Fact]
    public void ContextIsPopulated()
    {
        List<ConfluenceXRefRecord> refs = ConfluenceReferenceExtractor.GetReferences(
            "issue", "1", "Check out https://confluence.hl7.org/display/FHIR/123456 for the spec");
        Assert.NotNull(refs[0].Context);
        Assert.NotEmpty(refs[0].Context!);
    }
}
