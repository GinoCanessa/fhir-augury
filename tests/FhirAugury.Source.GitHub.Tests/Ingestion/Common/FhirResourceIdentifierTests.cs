using FhirAugury.Source.GitHub.Ingestion.Common;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Common;

public class FhirResourceIdentifierTests
{
    [Theory]
    [InlineData("valueset-example.xml", "ValueSet")]
    [InlineData("structuredefinition-patient.xml", "StructureDefinition")]
    [InlineData("codesystem-v3-ActCode.xml", "CodeSystem")]
    public void TryIdentify_FilenamePrefix_ReturnsType(string filename, string expectedType)
    {
        FhirResourceIdentifier.IdentificationResult? result =
            FhirResourceIdentifier.TryIdentify(filename, null);

        Assert.NotNull(result);
        Assert.Equal(expectedType, result.ResourceType);
    }

    [Theory]
    [InlineData("patient-introduction.xml")]
    [InlineData("spreadsheet.xml")]
    [InlineData("README.md")]
    public void TryIdentify_NonResourceFilename_ReturnsNull(string filename)
    {
        FhirResourceIdentifier.IdentificationResult? result =
            FhirResourceIdentifier.TryIdentify(filename, null);

        Assert.Null(result);
    }

    [Fact]
    public void TryIdentify_XmlContent_ReturnsType()
    {
        string content = "<StructureDefinition xmlns=\"http://hl7.org/fhir\"><id value=\"test\"/></StructureDefinition>";

        FhirResourceIdentifier.IdentificationResult? result =
            FhirResourceIdentifier.TryIdentify("test.xml", content);

        Assert.NotNull(result);
        Assert.Equal("StructureDefinition", result.ResourceType);
    }

    [Fact]
    public void TryIdentify_XmlWithDeclaration_ReturnsType()
    {
        string content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ValueSet xmlns=\"http://hl7.org/fhir\"/>";

        FhirResourceIdentifier.IdentificationResult? result =
            FhirResourceIdentifier.TryIdentify("test.xml", content);

        Assert.NotNull(result);
        Assert.Equal("ValueSet", result.ResourceType);
    }

    [Fact]
    public void TryIdentify_JsonContent_ReturnsType()
    {
        string content = """{"resourceType": "StructureDefinition", "id": "test"}""";

        FhirResourceIdentifier.IdentificationResult? result =
            FhirResourceIdentifier.TryIdentify("test.json", content);

        Assert.NotNull(result);
        Assert.Equal("StructureDefinition", result.ResourceType);
    }

    [Fact]
    public void TryIdentify_UnknownXmlRoot_ReturnsNull()
    {
        string content = "<Patient xmlns=\"http://hl7.org/fhir\"/>";

        FhirResourceIdentifier.IdentificationResult? result =
            FhirResourceIdentifier.TryIdentify("test.xml", content);

        // Patient is excluded from FilenamePrefixTypes (clinical resource)
        Assert.Null(result);
    }

    [Fact]
    public void TryIdentify_EmptyContent_ReturnsNull()
    {
        FhirResourceIdentifier.IdentificationResult? result =
            FhirResourceIdentifier.TryIdentify("test.xml", "");

        Assert.Null(result);
    }
}
