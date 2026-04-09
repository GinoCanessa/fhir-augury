using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests.Ingestion;

public class FhirResourceTypesTests
{
    [Theory]
    [InlineData("valueset-example.xml", "ValueSet")]
    [InlineData("structuredefinition-patient.xml", "StructureDefinition")]
    [InlineData("codesystem-v3-ActCode.xml", "CodeSystem")]
    [InlineData("conceptmap-example.xml", "ConceptMap")]
    [InlineData("searchparameter-patient-name.xml", "SearchParameter")]
    [InlineData("operationdefinition-example.xml", "OperationDefinition")]
    [InlineData("capabilitystatement-base.xml", "CapabilityStatement")]
    [InlineData("namingsystem-example.xml", "NamingSystem")]
    [InlineData("compartmentdefinition-patient.xml", "CompartmentDefinition")]
    [InlineData("bundle-transaction.xml", "Bundle")]
    [InlineData("questionnaire-example.xml", "Questionnaire")]
    public void TryGetFromFilename_ValidPrefix_ReturnsCanonicalName(string filename, string expected)
    {
        string? result = FhirResourceTypes.TryGetFromFilename(filename);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("patient-introduction.xml")]
    [InlineData("condition-example.xml")]
    [InlineData("observation-vitals.xml")]
    public void TryGetFromFilename_ClinicalResourcePrefix_ReturnsNull(string filename)
    {
        string? result = FhirResourceTypes.TryGetFromFilename(filename);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("spreadsheet.xml")]
    [InlineData("README.md")]
    [InlineData("data.json")]
    public void TryGetFromFilename_NoDash_ReturnsNull(string filename)
    {
        string? result = FhirResourceTypes.TryGetFromFilename(filename);
        Assert.Null(result);
    }

    [Fact]
    public void TryGetFromFilename_CaseInsensitive()
    {
        Assert.Equal("ValueSet", FhirResourceTypes.TryGetFromFilename("ValueSet-example.xml"));
        Assert.Equal("ValueSet", FhirResourceTypes.TryGetFromFilename("VALUESET-example.xml"));
        Assert.Equal("ValueSet", FhirResourceTypes.TryGetFromFilename("valueset-example.xml"));
    }

    [Theory]
    [InlineData("-example.xml")]
    [InlineData("-.xml")]
    public void TryGetFromFilename_EmptyPrefix_ReturnsNull(string filename)
    {
        string? result = FhirResourceTypes.TryGetFromFilename(filename);
        Assert.Null(result);
    }
}
