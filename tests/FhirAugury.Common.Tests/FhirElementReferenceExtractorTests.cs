using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;

namespace FhirAugury.Common.Tests;

public class FhirElementReferenceExtractorTests
{
    [Fact]
    public void ExtractsSimpleElement()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "See Patient.name for details");
        Assert.Single(refs);
        Assert.Equal("Patient", refs[0].ResourceType);
        Assert.Equal("Patient.name", refs[0].ElementPath);
    }

    [Fact]
    public void ExtractsNestedElement()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Check Patient.name.family");
        Assert.Single(refs);
        Assert.Equal("Patient.name.family", refs[0].ElementPath);
    }

    [Fact]
    public void ExtractsChoiceType()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Observation.value[x] can vary");
        Assert.Single(refs);
        Assert.Equal("Observation.value[x]", refs[0].ElementPath);
    }

    [Fact]
    public void ExtractsMultipleElements()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Patient.name and Observation.value[x] are important");
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void RejectsNonFhirDottedPath()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "String.format is a Java method");
        Assert.Empty(refs);
    }

    [Fact]
    public void RejectsDotNetNamespace()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "System.IO.Path is a .NET class");
        Assert.Empty(refs);
    }

    [Fact]
    public void AcceptsTaskAsResource()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Task.status is a FHIR element");
        Assert.Single(refs);
        Assert.Equal("Task", refs[0].ResourceType);
    }

    [Fact]
    public void DeduplicatesSameElement()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Patient.name and again Patient.name");
        Assert.Single(refs);
    }

    [Fact]
    public void TargetTypeIsFhir()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Patient.name");
        Assert.Equal("fhir", refs[0].TargetType);
    }

    [Fact]
    public void TargetIdIsElementPath()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Patient.name");
        Assert.Equal("Patient.name", refs[0].TargetId);
    }

    [Fact]
    public void EmptyTextReturnsEmpty()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "");
        Assert.Empty(refs);
    }

    [Fact]
    public void SourceFieldsSet()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "message", "42", "Patient.name is required");
        Assert.Equal("message", refs[0].SourceType);
        Assert.Equal("42", refs[0].SourceId);
        Assert.Equal("mentions", refs[0].LinkType);
    }

    [Fact]
    public void ExtractsBundleEntryResource()
    {
        List<FhirElementXRefRecord> refs = FhirElementReferenceExtractor.GetReferences(
            "issue", "1", "Bundle.entry.resource contains the payload");
        Assert.Single(refs);
        Assert.Equal("Bundle.entry.resource", refs[0].ElementPath);
        Assert.Equal("Bundle", refs[0].ResourceType);
    }
}
