namespace FhirAugury.Parsing.Fhir.Tests;

public class ArtifactClassifierTests
{
    [Theory]
    [InlineData("resource", null, "Patient", "Resource")]
    [InlineData("resource", "specialization", "Patient", "Resource")]
    [InlineData("resource", "constraint", "Patient", "Profile")]
    [InlineData("complextype", null, "HumanName", "ComplexType")]
    [InlineData("complex-type", null, "HumanName", "ComplexType")]
    [InlineData("complextype", "specialization", "HumanName", "ComplexType")]
    [InlineData("complextype", "constraint", "Extension", "Extension")]
    [InlineData("complex-type", "constraint", "Extension", "Extension")]
    [InlineData("complextype", "constraint", "HumanName", "ComplexType")]
    [InlineData("primitivetype", null, "string", "PrimitiveType")]
    [InlineData("primitive-type", null, "string", "PrimitiveType")]
    [InlineData("logical", null, "FiveWs", "LogicalModel")]
    [InlineData("unknown-kind", null, null, "Unknown")]
    [InlineData("", null, null, "Unknown")]
    public void Classify_ReturnsExpectedClass(string kind, string? derivation, string? fhirType, string expected)
    {
        string result = ArtifactClassifier.Classify(kind, derivation, fhirType);
        Assert.Equal(expected, result);
    }
}
