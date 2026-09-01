using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class ResolveReaderTests : IClassFixture<FhirSpecFixture>
{
    private const int R5 = 5;
    private readonly FhirSpecFixture _fixture;

    public ResolveReaderTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirSpecReader Reader()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        return new FhirSpecReader(db, new FhirReleaseResolver(db));
    }

    [Theory]
    [InlineData("http://hl7.org/fhir/StructureDefinition/Observation", "Resource", "Observation")]
    [InlineData("http://hl7.org/fhir/StructureDefinition/Observation|5.0.0", "Resource", "Observation")]
    [InlineData("http://hl7.org/fhir/StructureDefinition/HumanName", "ComplexType", "HumanName")]
    [InlineData("http://hl7.org/fhir/ValueSet/observation-status", "ValueSet", "ObservationStatus")]
    [InlineData("http://hl7.org/fhir/observation-status", "CodeSystem", "ObservationStatus")]
    [InlineData("http://hl7.org/fhir/OperationDefinition/ValueSet-expand", "Operation", "Expand")]
    [InlineData("http://hl7.org/fhir/SearchParameter/Observation-code", "SearchParameter", "Observation-code")]
    public void Resolve_MatchesArtifactByCanonicalUrl(string url, string expectedKind, string expectedName)
    {
        ResolveResult? result = Reader().Resolve(R5, url);

        Assert.NotNull(result);
        Assert.Equal(expectedKind, result!.Kind);
        Assert.Equal(expectedName, result.Name);
    }

    [Fact]
    public void Resolve_Unknown_ReturnsNull()
    {
        Assert.Null(Reader().Resolve(R5, "http://example.org/nope"));
    }
}
