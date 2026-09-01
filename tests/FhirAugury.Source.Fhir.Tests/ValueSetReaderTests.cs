using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class ValueSetReaderTests : IClassFixture<FhirSpecFixture>
{
    private const int R5 = 5;
    private const string VsUrl = "http://hl7.org/fhir/ValueSet/observation-status";
    private readonly FhirSpecFixture _fixture;

    public ValueSetReaderTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirSpecReader Reader()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        return new FhirSpecReader(db, new FhirReleaseResolver(db));
    }

    [Fact]
    public void ListValueSets_ReturnsSeeded()
    {
        ValueSetSummary vs = Assert.Single(Reader().ListValueSets(R5));
        Assert.Equal("ObservationStatus", vs.Name);
        Assert.Equal(3, vs.ConceptCount);
    }

    [Fact]
    public void GetValueSet_HasComposeReferencedSystemsAndBindingRollups()
    {
        ValueSetDetail? detail = Reader().GetValueSet(R5, VsUrl);

        Assert.NotNull(detail);
        ComposeRule include = Assert.Single(detail!.Compose);
        Assert.Equal("include", include.Mode);
        Assert.Equal("http://hl7.org/fhir/observation-status", include.System);
        Assert.Equal("http://hl7.org/fhir/observation-status", Assert.Single(detail.ReferencedSystems));
        Assert.Equal("Required", detail.StrongestBindingCore);
    }

    [Fact]
    public void GetValueSet_ResolvesById()
    {
        Assert.NotNull(Reader().GetValueSet(R5, "observation-status"));
    }

    [Fact]
    public void GetExpansion_ReturnsConcepts()
    {
        IReadOnlyList<ValueSetConceptInfo>? expansion = Reader().GetExpansion(R5, VsUrl);

        Assert.NotNull(expansion);
        Assert.Equal(["final", "amended", "registered"], expansion!.Select(c => c.Code));
        Assert.All(expansion, c => Assert.Equal("http://hl7.org/fhir/observation-status", c.System));
    }

    [Fact]
    public void GetBindings_ReturnsReverseElementBindings()
    {
        IReadOnlyList<ElementBindingRef>? bindings = Reader().GetBindings(R5, VsUrl);

        Assert.NotNull(bindings);
        ElementBindingRef binding = Assert.Single(bindings!);
        Assert.Equal("Observation", binding.Resource);
        Assert.Equal("Observation.status", binding.Path);
        Assert.Equal("Required", binding.Strength);
    }

    [Fact]
    public void GetValueSet_Unknown_ReturnsNull()
    {
        Assert.Null(Reader().GetValueSet(R5, "http://example/missing"));
    }
}
